using System.Management;
using System.Text.Json.Serialization;

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

/// <summary>What Windows says about a printer (Win32_Printer), before it is put into words.</summary>
internal sealed record PrinterRow(string Name, bool Default, bool WorkOffline, int PrinterStatus, long PrinterState);

/// <summary>What Windows says about a print job (Win32_PrintJob); its name is "printer, job number".</summary>
internal sealed record PrintJobRow(string Name, string Document, string Owner, long StatusMask, int TotalPages);

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
/// Home Assistant switch a printer on when something is sent to it. Read from Windows' own
/// tables (Win32_Printer, Win32_PrintJob), which the spooler fills; anyone may read them. The
/// service, which runs as SYSTEM, sees the printers installed on the PC, but not a user's own
/// network printers or which one that user made the default: the tray app sees those.
/// </summary>
internal static class PrintQueues
{
    /// <summary>How many jobs the list carries; the count is of all of them.</summary>
    internal const int MaximumJobsListed = 20;

    private const int MaximumTextLength = 120;

    /// <summary>
    /// The state flags the spooler keeps for a printer, by the name they are reported under.
    /// How many of them a printer ever raises is up to its driver: many only know "offline".
    /// Win32_Printer.PrinterState is the spooler's own status word (PRINTER_INFO_6.dwStatus,
    /// the PRINTER_STATUS_* bits): the class lists the names in the order of the bits, and an
    /// idle printer reports 0, no bit set, not the first name of that list.
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
        ("server_unknown", 0x800000)
    ];

    private const long OfflineBit = 0x80;

    // Whatever keeps a printer from printing until someone does something about it.
    private const long ProblemBits = 0x2 | 0x8 | 0x10 | 0x40 | 0x20 | 0x40000 | 0x400000 | 0x800 | 0x100000 | 0x200000 | 0x1000;

    // A network printer that does not answer must not hold up every other sensor. The query
    // returns at once and each step through its results waits ten seconds at most; that alone
    // does not bound the whole of it, so the two queries together also run against a deadline.
    private static readonly System.Management.EnumerationOptions QueryOptions = new() { Timeout = TimeSpan.FromSeconds(10), ReturnImmediately = true };

    private static readonly BoundedRead<PrintQueueSnapshot> Bounded = new(TimeSpan.FromSeconds(15));

    /// <summary>The printers and their queues, or a <see cref="TimeoutException"/> when Windows does not answer in time.</summary>
    public static PrintQueueSnapshot Read() => Bounded.Run(ReadFromWindows);

    private static PrintQueueSnapshot ReadFromWindows()
    {
        var printers = Query("SELECT Name, Default, WorkOffline, PrinterStatus, PrinterState FROM Win32_Printer")
            .Select(item => new PrinterRow(
                Text(item["Name"]),
                item["Default"] is true,
                item["WorkOffline"] is true,
                (int)Number(item["PrinterStatus"]),
                Number(item["PrinterState"])))
            .ToList();
        var jobs = Query("SELECT Name, Document, Owner, StatusMask, TotalPages FROM Win32_PrintJob")
            .Select(item => new PrintJobRow(
                Text(item["Name"]),
                Text(item["Document"]),
                Text(item["Owner"]),
                Number(item["StatusMask"]),
                (int)Math.Min(Number(item["TotalPages"]), int.MaxValue)))
            .ToList();
        return Compose(printers, jobs);
    }

    /// <summary>
    /// How many printers this user has, without reading the queues: for the attribute list in
    /// the settings window, which is asked for the printers' attributes right after the sensor
    /// is ticked, before it was ever read. Null when Windows does not say. Counted at most
    /// once a minute.
    /// </summary>
    public static int? PrinterCount()
    {
        if (Environment.TickCount64 - _printerCountAtTicks < 60_000)
        {
            return _printerCount;
        }

        try
        {
            _printerCount = System.Drawing.Printing.PrinterSettings.InstalledPrinters.Count;
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
    /// long as none is added before it, each with the number of jobs in its queue; and the
    /// jobs in the order Windows gave them, which is the order they print in.
    /// </summary>
    internal static PrintQueueSnapshot Compose(IReadOnlyList<PrinterRow> printers, IReadOnlyList<PrintJobRow> jobs)
    {
        var listed = jobs
            .Select(job => new PrintJobInfo(PrinterOf(job.Name), Limit(job.Document), Limit(job.Owner), JobStatus(job.StatusMask), Math.Max(0, job.TotalPages)))
            .ToList();
        var perPrinter = listed
            .GroupBy(job => job.Printer, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        return new PrintQueueSnapshot(
            printers
                .Where(printer => printer.Name.Length > 0)
                .OrderBy(printer => printer.Name, StringComparer.OrdinalIgnoreCase)
                .Select(printer =>
                {
                    // "Use printer offline" is a switch of Windows' own, kept apart from the flags.
                    var states = printer.PrinterState | (printer.WorkOffline || printer.PrinterStatus == 7 ? OfflineBit : 0);
                    return new PrinterInfo(
                        Limit(printer.Name),
                        printer.Default,
                        PrinterStatus(printer.PrinterStatus, states),
                        perPrinter.GetValueOrDefault(printer.Name),
                        states);
                })
                .ToList(),
            listed.Take(MaximumJobsListed).ToList(),
            listed.Count);
    }

    /// <summary>The printer of a job: Windows names a job "printer, number", and a printer's own name may have commas.</summary>
    internal static string PrinterOf(string jobName)
    {
        var comma = jobName.LastIndexOf(',');
        return comma > 0 && jobName[(comma + 1)..].Trim() is { Length: > 0 } number && number.All(char.IsAsciiDigit)
            ? jobName[..comma].Trim()
            : jobName.Trim();
    }

    /// <summary>
    /// A printer's state in one word: what keeps it from printing comes first, then what it is
    /// doing. Many drivers only ever say "idle": what the device itself is doing is not
    /// something Windows always knows.
    /// </summary>
    internal static string PrinterStatus(int printerStatus, long states) => printerStatus switch
    {
        _ when (states & OfflineBit) != 0 => "offline",
        _ when (states & ProblemBits) != 0 => "error",
        _ when (states & 0x1) != 0 => "paused",
        _ when (states & (0x400 | 0x4000)) != 0 => "printing",
        4 => "printing",
        _ when (states & (0x8000 | 0x10000)) != 0 => "warming_up",
        5 => "warming_up",
        6 => "stopped",
        3 => "idle",
        _ => "unknown"
    };

    /// <summary>
    /// A job's state in one word, from the flags Windows keeps for it: what stops it comes
    /// first, then what it is doing. Without any flag it waits for its turn.
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

    private static IEnumerable<ManagementBaseObject> Query(string query)
    {
        using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\cimv2"), new ObjectQuery(query), QueryOptions);
        using var results = searcher.Get();
        foreach (var item in results)
        {
            using (item)
            {
                yield return item;
            }
        }
    }

    private static string Text(object? value) => value?.ToString()?.Trim() ?? string.Empty;

    private static long Number(object? value) => value is null ? 0 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
}
