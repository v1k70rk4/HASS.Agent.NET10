using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace HASS.Agent.Companion.SystemStatus;

/// <summary>
/// The print spooler asked directly (winspool): the printers of whoever asks, each with its
/// status word and the jobs in its queue. The spooler answers for the account of the thread
/// that asks, which is what lets the service ask as the user who is logged in.
/// </summary>
internal static class PrintSpooler
{
    private const uint EnumLocal = 0x2;
    private const uint EnumConnections = 0x4;
    private const uint AttributeWorkOffline = 0x400;
    private const int InsufficientBuffer = 122;

    /// <summary>The spooler could not say anything about this printer (its server does not answer, or it is just being removed).</summary>
    internal const long StatusUnknown = 0x800000;

    // Each printer is asked on its own, all of them at once: a printer on a print server that
    // does not answer is reported as one whose state is not known after this long, and the
    // others as they are. Well within the deadline of the whole read.
    private static readonly PrinterAsks Asks = new(TimeSpan.FromSeconds(8));

    /// <summary>
    /// How many printers are asked and reported. Far more than a PC has; but which printers
    /// there are is the user's to say, and the service asks them with a thread for each and
    /// reports every one with all its state flags.
    /// </summary>
    internal const int MostPrinters = 64;

    /// <summary>
    /// The printers installed on the PC and the ones the account connected from a print
    /// server, with the first <paramref name="maximumJobs"/> jobs found in their queues, and
    /// the account's default printer as Windows answers it. The account is the one asking,
    /// or <paramref name="asUser"/>, whose rights are borrowed for every question: the service
    /// asks for the user who is logged in. A printer that cannot be asked, or does not answer
    /// in time, is still listed, as one whose state is not known: one print server that is
    /// away must not hide the other printers. Of more than <see cref="MostPrinters"/> printers
    /// the first that many are asked and listed (<see cref="TheOnesAsked"/>); how many there
    /// are in all is given beside them.
    /// </summary>
    /// <param name="defaultFirst">
    /// The default printer where the caller knows it better than the spooler's answer to
    /// this thread (the service, from the user's registry): the one that is asked in any case.
    /// When no printer has that name (the registry still names one that is gone), the
    /// spooler's answer is the one asked in any case.
    /// </param>
    public static (List<PrinterRow> Printers, List<PrintJobRow> Jobs, string? DefaultPrinter, int PrinterCount) Read(
        int maximumJobs, WindowsIdentity? asUser, string? defaultFirst = null)
    {
        // The names and the default are the spooler's own lists, which no printer is asked for.
        var (names, defaultPrinter) = asUser is null
            ? (PrinterNames(), DefaultPrinter())
            : WindowsIdentity.RunImpersonated(asUser.AccessToken, () => (PrinterNames(), DefaultPrinter()));

        var (printers, jobs) = Asks.Collect(
            TheOnesAsked(names, MostPrinters, defaultFirst, defaultPrinter),
            name => Question(name, maximumJobs, asUser),
            maximumJobs);
        return (printers, jobs, defaultPrinter, names.Count);
    }

    /// <summary>
    /// Which printers are asked when there are more than are asked at all: the default
    /// printer, then the others in the order the spooler lists them. The default is the
    /// first of <paramref name="defaults"/> that a printer has as its name: a name no
    /// printer has (none given, or one that is gone) is passed over for the next.
    /// </summary>
    internal static List<string> TheOnesAsked(IReadOnlyList<string> names, int most, params string?[] defaults)
    {
        var defaultPrinter = defaults.FirstOrDefault(candidate =>
            !string.IsNullOrEmpty(candidate) && names.Contains(candidate, StringComparer.OrdinalIgnoreCase));
        return names
            .OrderBy(name => !string.Equals(name, defaultPrinter, StringComparison.OrdinalIgnoreCase))
            .Take(most)
            .ToList();
    }

    // The question to one printer, to run on a thread of its own. With a user, that thread
    // gets a copy of the user's token, taken now while the caller still holds it: the
    // question may outlive the read that asked it.
    private static Func<PrinterAnswer> Question(string name, int maximumJobs, WindowsIdentity? asUser)
    {
        if (asUser is null)
        {
            return () => Ask(name, maximumJobs);
        }

        var copy = new WindowsIdentity(asUser.AccessToken.DangerousGetHandle());
        return () =>
        {
            using (copy)
            {
                return WindowsIdentity.RunImpersonated(copy.AccessToken, () => Ask(name, maximumJobs));
            }
        };
    }

    private static PrinterAnswer Ask(string name, int maximumJobs)
    {
        if (!OpenPrinter(name, out var printer, IntPtr.Zero))
        {
            return new PrinterAnswer(PrinterAsks.Unknown(name), []);
        }

        try
        {
            if (InfoOf(printer) is not { } info)
            {
                return new PrinterAnswer(PrinterAsks.Unknown(name), []);
            }

            var row = new PrinterRow(name, (info.Attributes & AttributeWorkOffline) != 0, info.Status, (int)Math.Min(info.Jobs, int.MaxValue));
            return new PrinterAnswer(row, info.Jobs > 0 ? JobsOf(printer, name, maximumJobs) : []);
        }
        finally
        {
            ClosePrinter(printer);
        }
    }

    /// <summary>The names alone, which the spooler has at hand without asking any printer.</summary>
    internal static List<string> PrinterNames()
    {
        var names = new List<string>();
        WithBuffer(
            (IntPtr buffer, uint size, out uint needed, out uint count) =>
                EnumPrinters(EnumLocal | EnumConnections, null, 4, buffer, size, out needed, out count),
            (buffer, count) =>
            {
                var step = Marshal.SizeOf<PrinterInfo4>();
                for (var index = 0; index < count; index++)
                {
                    var name = Marshal.PtrToStringUni(Marshal.PtrToStructure<PrinterInfo4>(buffer + index * step).PrinterName);
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        names.Add(name.Trim());
                    }
                }
            },
            "list the printers");
        return names;
    }

    private static (uint Attributes, uint Status, uint Jobs)? InfoOf(IntPtr printer)
    {
        (uint Attributes, uint Status, uint Jobs)? result = null;
        try
        {
            WithBuffer(
                (IntPtr buffer, uint size, out uint needed, out uint count) =>
                {
                    count = 1;
                    return GetPrinter(printer, 2, buffer, size, out needed);
                },
                (buffer, _) =>
                {
                    var info = Marshal.PtrToStructure<PrinterInfo2>(buffer);
                    result = (info.Attributes, info.Status, info.Jobs);
                },
                "ask a printer");
        }
        catch (Win32Exception)
        {
            // This printer does not answer; the others still may.
        }

        return result;
    }

    private static List<PrintJobRow> JobsOf(IntPtr printer, string printerName, int maximum)
    {
        var jobs = new List<PrintJobRow>();
        try
        {
            WithBuffer(
                (IntPtr buffer, uint size, out uint needed, out uint count) =>
                    EnumJobs(printer, 0, (uint)maximum, 1, buffer, size, out needed, out count),
                (buffer, count) =>
                {
                    var step = Marshal.SizeOf<JobInfo1>();
                    for (var index = 0; index < count; index++)
                    {
                        var job = Marshal.PtrToStructure<JobInfo1>(buffer + index * step);
                        jobs.Add(new PrintJobRow(
                            printerName,
                            Marshal.PtrToStringUni(job.Document)?.Trim() ?? string.Empty,
                            Marshal.PtrToStringUni(job.UserName)?.Trim() ?? string.Empty,
                            job.Status,
                            (int)Math.Min(job.TotalPages, int.MaxValue)));
                    }
                },
                "list the jobs of a printer");
        }
        catch (Win32Exception)
        {
            // The queue could not be listed; the number of jobs in it is still known.
        }

        return jobs;
    }

    private delegate bool Fill(IntPtr buffer, uint size, out uint needed, out uint count);

    // The spooler's calls say how much room they need when given none; between the two calls
    // the answer may grow (a job arrives), so the second is tried again with what it then asks.
    private static void WithBuffer(Fill fill, Action<IntPtr, uint> read, string what)
    {
        uint size = 0;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var buffer = size == 0 ? IntPtr.Zero : Marshal.AllocHGlobal((int)size);
            try
            {
                if (fill(buffer, size, out var needed, out var count))
                {
                    if (buffer != IntPtr.Zero)
                    {
                        read(buffer, count);
                    }

                    return;
                }

                var error = Marshal.GetLastWin32Error();
                if (error != InsufficientBuffer || needed == 0)
                {
                    throw new Win32Exception(error, $"The print spooler could not {what}: {new Win32Exception(error).Message}");
                }

                size = needed;
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }

        throw new Win32Exception(InsufficientBuffer, $"The print spooler could not {what}: its answer kept growing.");
    }

    /// <summary>The default printer of whoever asks, as Windows answers it; null when there is none.</summary>
    internal static string? DefaultPrinter()
    {
        uint size = 0;
        GetDefaultPrinter(null, ref size);
        if (size == 0)
        {
            return null;
        }

        var name = new System.Text.StringBuilder((int)size);
        return GetDefaultPrinter(name, ref size) && name.ToString().Trim() is { Length: > 0 } found ? found : null;
    }

    /// <summary>
    /// The token of the user logged in to a session, for the service to ask the spooler as
    /// that user. Only an account that is part of the operating system (SYSTEM) is given one.
    /// Null when the session has no user, or the token is not given.
    /// </summary>
    internal static SafeAccessTokenHandle? UserTokenOf(uint sessionId)
    {
        if (WTSQueryUserToken(sessionId, out var token) && !token.IsInvalid)
        {
            return token;
        }

        token?.Dispose();
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterInfo4
    {
        public IntPtr PrinterName;
        public IntPtr ServerName;
        public uint Attributes;
    }

    // PRINTER_INFO_2: thirteen pointers, then eight numbers.
    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterInfo2
    {
        public IntPtr ServerName;
        public IntPtr PrinterName;
        public IntPtr ShareName;
        public IntPtr PortName;
        public IntPtr DriverName;
        public IntPtr Comment;
        public IntPtr Location;
        public IntPtr DevMode;
        public IntPtr SepFile;
        public IntPtr PrintProcessor;
        public IntPtr Datatype;
        public IntPtr Parameters;
        public IntPtr SecurityDescriptor;
        public uint Attributes;
        public uint Priority;
        public uint DefaultPriority;
        public uint StartTime;
        public uint UntilTime;
        public uint Status;
        public uint Jobs;
        public uint AveragePagesPerMinute;
    }

    // JOB_INFO_1, with its SYSTEMTIME as eight 16-bit numbers.
    [StructLayout(LayoutKind.Sequential)]
    private struct JobInfo1
    {
        public uint JobId;
        public IntPtr PrinterName;
        public IntPtr MachineName;
        public IntPtr UserName;
        public IntPtr Document;
        public IntPtr Datatype;
        public IntPtr StatusText;
        public uint Status;
        public uint Priority;
        public uint Position;
        public uint TotalPages;
        public uint PagesPrinted;
        public ushort Year;
        public ushort Month;
        public ushort DayOfWeek;
        public ushort Day;
        public ushort Hour;
        public ushort Minute;
        public ushort Second;
        public ushort Milliseconds;
    }

    [DllImport("winspool.drv", EntryPoint = "EnumPrintersW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumPrinters(uint flags, string? name, uint level, IntPtr buffer, uint size, out uint needed, out uint returned);

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenPrinter(string printerName, out IntPtr printer, IntPtr defaults);

    [DllImport("winspool.drv", EntryPoint = "GetPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPrinter(IntPtr printer, uint level, IntPtr buffer, uint size, out uint needed);

    [DllImport("winspool.drv", EntryPoint = "EnumJobsW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumJobs(IntPtr printer, uint firstJob, uint jobCount, uint level, IntPtr buffer, uint size, out uint needed, out uint returned);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClosePrinter(IntPtr printer);

    [DllImport("winspool.drv", EntryPoint = "GetDefaultPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDefaultPrinter(System.Text.StringBuilder? buffer, ref uint size);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle token);
}
