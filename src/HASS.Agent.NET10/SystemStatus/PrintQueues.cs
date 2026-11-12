using System.Security.Principal;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace HASS.Agent.Companion.SystemStatus;

/// <summary>
/// A printer of the PC as the print jobs sensor reports it: its state in one word, the number
/// of jobs in its queue, and every state flag the spooler keeps for it.
/// </summary>
internal sealed record PrinterInfo(string Name, bool Default, string Status, int Jobs, long States)
{
    /// <summary>Whether the spooler has this flag up for the printer ("paper_out", "toner_low").</summary>
    public bool Is(string state) =>
        PrintQueues.StateFlags.Any(flag => flag.Name == state && (States & flag.Bit) != 0);
}

/// <summary>A job that waits in a print queue, or is being printed.</summary>
internal sealed record PrintJobInfo(
    [property: JsonPropertyName("printer")] string Printer,
    [property: JsonPropertyName("document")] string Document,
    [property: JsonPropertyName("owner")] string Owner,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("pages")] int Pages);

/// <summary>What the spooler says about a printer, before it is put into words: its status word and the jobs in its queue.</summary>
internal sealed record PrinterRow(string Name, bool WorkOffline, long State, int Jobs);

/// <summary>What the spooler says about a print job.</summary>
internal sealed record PrintJobRow(string Printer, string Document, string Owner, long StatusMask, int TotalPages);

/// <summary>The printers by name, and the jobs in their queues; <see cref="JobCount"/> is every job, the list the first few.</summary>
internal sealed record PrintQueueSnapshot(IReadOnlyList<PrinterInfo> Printers, IReadOnlyList<PrintJobInfo> Jobs, int JobCount)
{
    public PrinterInfo? DefaultPrinter => Printers.FirstOrDefault(printer => printer.Default);

    /// <summary>
    /// The attributes of the sensor: the default printer and its queue up front, then the
    /// printers, each with its jobs, its state in one word and every state flag by name, and
    /// the jobs.
    /// </summary>
    public IReadOnlyDictionary<string, object?> Attributes() => new Dictionary<string, object?>
    {
        ["default_printer"] = DefaultPrinter?.Name,
        ["default_printer_jobs"] = DefaultPrinter?.Jobs,
        ["printer_count"] = Printers.Count,
        ["printers"] = Printers.Select(AttributesOf).ToList(),
        ["jobs"] = Jobs
    };

    private static Dictionary<string, object?> AttributesOf(PrinterInfo printer)
    {
        var values = new Dictionary<string, object?>
        {
            ["name"] = printer.Name,
            ["jobs"] = printer.Jobs,
            ["status"] = printer.Status,
            ["default"] = printer.Default
        };
        foreach (var (name, bit) in PrintQueues.StateFlags)
        {
            values[name] = (printer.States & bit) != 0;
        }

        return values;
    }
}

/// <summary>
/// The print queues of the PC: which printers there are, what state they are in and what waits
/// in them. A job stays in its queue while the printer is switched off, which is what lets
/// Home Assistant switch a printer on when something is sent to it.
///
/// The printers are a user's own in two ways: which one is the default, and the ones connected
/// from a print server, which are there for that user alone. The tray app and the service
/// publish to one topic, so both have to see the same printers: the tray app asks the spooler
/// as its user, and the service asks it as the user who is logged in, with that user's rights
/// for the length of the question and not its own. With nobody logged in, the service reports
/// the printers installed on the PC, and no default printer.
/// </summary>
internal static class PrintQueues
{
    /// <summary>How many jobs the list carries; the count is of all of them.</summary>
    internal const int MaximumJobsListed = 20;

    private const int MaximumTextLength = 120;

    /// <summary>
    /// The state flags the spooler keeps for a printer (the PRINTER_STATUS_* bits of its status
    /// word), by the name they are reported under. How many of them a printer ever raises is
    /// up to its driver: many only know "offline". An idle printer has none up.
    /// </summary>
    internal static readonly IReadOnlyList<(string Name, long Bit)> StateFlags =
    [
        ("offline", 0x80),
        ("paused", 0x1),
        ("error", 0x2),
        ("paper_out", 0x10),
        ("paper_jam", 0x8),
        ("paper_problem", 0x40),
        ("manual_feed", 0x20),
        ("toner_low", 0x20000),
        ("no_toner", 0x40000),
        ("door_open", 0x400000),
        ("output_bin_full", 0x800),
        ("needs_attention", 0x100000),
        ("out_of_memory", 0x200000),
        ("not_available", 0x1000),
        ("printing", 0x400),
        ("processing", 0x4000),
        ("busy", 0x200),
        ("io_active", 0x100),
        ("waiting", 0x2000),
        ("initializing", 0x8000),
        ("warming_up", 0x10000),
        ("power_save", 0x1000000),
        ("page_punt", 0x80000),
        ("pending_deletion", 0x4),
        ("server_unknown", PrintSpooler.StatusUnknown),
        ("server_offline", 0x2000000)
    ];

    // The printer itself, "use printer offline" in Windows, or the print server it hangs on.
    private const long OfflineBits = 0x80 | 0x2000000;

    // Whatever keeps a printer from printing until someone does something about it.
    private const long ProblemBits = 0x2 | 0x8 | 0x10 | 0x40 | 0x20 | 0x40000 | 0x400000 | 0x800 | 0x100000 | 0x200000 | 0x1000;

    // A print server that does not answer must not hold up every other sensor: the read runs
    // against a deadline, and the sensor cycle goes on without it after that.
    private static readonly BoundedRead<PrintQueueSnapshot> Bounded = new(TimeSpan.FromSeconds(15));

    /// <summary>The printers and their queues, or a <see cref="TimeoutException"/> when the spooler does not answer in time.</summary>
    public static PrintQueueSnapshot Read() => Bounded.Run(ReadFromTheSpooler);

    private static PrintQueueSnapshot ReadFromTheSpooler()
    {
        using var asking = WindowsIdentity.GetCurrent();
        if (!asking.IsSystem)
        {
            // The tray app: the spooler answers for its user.
            var (ownPrinters, ownJobs) = PrintSpooler.Read(MaximumJobsListed);
            // Windows' own answer first: with "let Windows manage my default printer" on, the
            // registry may not hold it.
            return Compose(ownPrinters, ownJobs, PrintSpooler.DefaultPrinter() ?? DefaultPrinterOf(asking.User?.Value));
        }

        // The service. SYSTEM has printers of its own view and a default printer of its own;
        // what is wanted are those of the user who is logged in.
        using var token = SystemMetricsService.LoggedInUserSessionId() is { } session ? PrintSpooler.UserTokenOf(session) : null;
        if (token is null)
        {
            var (installed, waiting) = PrintSpooler.Read(MaximumJobsListed);
            return Compose(installed, waiting, defaultPrinter: null);
        }

        using var user = new WindowsIdentity(token.DangerousGetHandle());
        var (printers, jobs, asked) = WindowsIdentity.RunImpersonated(token, () =>
        {
            var (found, waiting) = PrintSpooler.Read(MaximumJobsListed);
            return (found, waiting, PrintSpooler.DefaultPrinter());
        });

        // The user's registry first: what Windows answers a thread that only borrows a user's
        // rights depends on whose part of the registry it takes for the current user.
        return Compose(printers, jobs, DefaultPrinterOf(user.User?.Value) ?? asked);
    }

    /// <summary>
    /// The name in the text Windows keeps a user's default printer as: "name,winspool,port".
    /// A printer's name cannot have a comma in it.
    /// </summary>
    internal static string? PrinterOfDevice(string? device)
    {
        var name = device?.Split(',')[0].Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    /// <summary>
    /// The default printer of the user with this SID, from that user's part of the registry,
    /// which is there while the user is logged in. Null when it is not, or says nothing.
    /// </summary>
    internal static string? DefaultPrinterOf(string? sid)
    {
        if (string.IsNullOrEmpty(sid))
        {
            return null;
        }

        try
        {
            using var key = Registry.Users.OpenSubKey($@"{sid}\Software\Microsoft\Windows NT\CurrentVersion\Windows");
            return PrinterOfDevice(key?.GetValue("Device") as string);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// How many printers this user has, without asking any of them: for the attribute list in
    /// the settings window, which is asked for the printers' attributes right after the sensor
    /// is ticked, before it was ever read, or while only the service reads it. Null when the
    /// spooler does not say. Counted at most once a minute.
    /// </summary>
    public static int? PrinterCount()
    {
        if (Environment.TickCount64 - _printerCountAtTicks < 60_000)
        {
            return _printerCount;
        }

        try
        {
            _printerCount = PrintSpooler.PrinterNames().Count;
        }
        catch
        {
            _printerCount = null;
        }

        _printerCountAtTicks = Environment.TickCount64;
        return _printerCount;
    }

    private static int? _printerCount;
    private static long _printerCountAtTicks = long.MinValue / 2;

    /// <summary>
    /// The printers in the order of their names, so that an index stays on a printer for as
    /// long as none is added before it, with the user's default marked by its name; and the
    /// jobs in the order the spooler gave them, which is the order they print in. The number
    /// of jobs is what the spooler counts in each queue, the list only the first few of them.
    /// </summary>
    internal static PrintQueueSnapshot Compose(IReadOnlyList<PrinterRow> printers, IReadOnlyList<PrintJobRow> jobs, string? defaultPrinter)
    {
        var listed = printers
            .Where(printer => printer.Name.Length > 0)
            .OrderBy(printer => printer.Name, StringComparer.OrdinalIgnoreCase)
            .Select(printer =>
            {
                // "Use printer offline" is a switch of Windows' own, kept apart from the flags.
                var states = printer.State | (printer.WorkOffline ? 0x80L : 0);
                return new PrinterInfo(
                    Limit(printer.Name),
                    !string.IsNullOrEmpty(defaultPrinter) && string.Equals(printer.Name, defaultPrinter, StringComparison.OrdinalIgnoreCase),
                    PrinterStatus(states),
                    Math.Max(0, printer.Jobs),
                    states);
            })
            .ToList();

        return new PrintQueueSnapshot(
            listed,
            jobs
                .Take(MaximumJobsListed)
                .Select(job => new PrintJobInfo(Limit(job.Printer), Limit(job.Document), Limit(job.Owner), JobStatus(job.StatusMask), Math.Max(0, job.TotalPages)))
                .ToList(),
            listed.Sum(printer => printer.Jobs));
    }

    /// <summary>
    /// A printer's state in one word: what keeps it from printing comes first, then what it is
    /// doing. Many drivers never raise a flag, and their printer is "idle" whatever it does:
    /// what the device itself is doing is not something Windows always knows.
    /// </summary>
    internal static string PrinterStatus(long states) => states switch
    {
        _ when (states & OfflineBits) != 0 => "offline",
        _ when (states & ProblemBits) != 0 => "error",
        _ when (states & PrintSpooler.StatusUnknown) != 0 => "unknown",
        _ when (states & 0x1) != 0 => "paused",
        _ when (states & (0x400 | 0x4000)) != 0 => "printing",
        _ when (states & (0x8000 | 0x10000)) != 0 => "warming_up",
        _ => "idle"
    };

    /// <summary>
    /// A job's state in one word, from the flags the spooler keeps for it: what stops it comes
    /// first, then what it is doing. Without any of these it waits for its turn.
    /// </summary>
    internal static string JobStatus(long statusMask) => statusMask switch
    {
        _ when (statusMask & 0x002) != 0 => "error",
        _ when (statusMask & 0x040) != 0 => "paper_out",
        _ when (statusMask & 0x020) != 0 => "offline",
        _ when (statusMask & 0x600) != 0 => "blocked",      // blocked in the queue, or waits for the user
        _ when (statusMask & 0x001) != 0 => "paused",
        _ when (statusMask & 0x104) != 0 => "deleting",
        _ when (statusMask & 0x010) != 0 => "printing",
        _ when (statusMask & 0x080) != 0 => "printed",
        _ when (statusMask & 0x008) != 0 => "spooling",
        _ => "waiting"
    };

    private static string Limit(string text) =>
        text.Length <= MaximumTextLength ? text : text[..MaximumTextLength];
}
