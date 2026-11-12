using System.Text.Json;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.SystemStatus;

namespace HASS.Agent.Companion.Tests;

/// <summary>
/// The print jobs sensor: one value for every printer of the PC (how many jobs wait), and each
/// printer's queue and state in the attributes, for custom sensors to pick from.
/// </summary>
public sealed class PrintQueueTests
{
    private static readonly PrinterRow[] Printers =
    [
        new("Office, 2nd floor", Default: false, WorkOffline: false, PrinterStatus: 3, PrinterState: 0),
        new("Brother HL-L2350DW", Default: true, WorkOffline: false, PrinterStatus: 3, PrinterState: 0),
        new("Microsoft Print to PDF", Default: false, WorkOffline: false, PrinterStatus: 3, PrinterState: 0)
    ];

    private static readonly PrintJobRow[] Jobs =
    [
        new("Brother HL-L2350DW, 12", "Invoice.pdf", "viktor", StatusMask: 0x10, TotalPages: 2),
        new("Brother HL-L2350DW, 13", "Label", "viktor", StatusMask: 0, TotalPages: 1),
        new("Office, 2nd floor, 4", "Report.docx", "anna", StatusMask: 0x20, TotalPages: 12)
    ];

    [Fact]
    public void Every_printer_has_the_number_of_jobs_in_its_own_queue_and_the_value_is_all_of_them()
    {
        var queues = PrintQueues.Compose(Printers, Jobs);

        // By name, so an index stays on its printer.
        Assert.Equal(["Brother HL-L2350DW", "Microsoft Print to PDF", "Office, 2nd floor"], queues.Printers.Select(printer => printer.Name));
        Assert.Equal([2, 0, 1], queues.Printers.Select(printer => printer.Jobs));
        Assert.Equal(3, queues.JobCount);
        Assert.Equal("Brother HL-L2350DW", queues.DefaultPrinter?.Name);

        // In the order they print in, each with its printer, also one whose name has a comma.
        Assert.Equal(["Invoice.pdf", "Label", "Report.docx"], queues.Jobs.Select(job => job.Document));
        Assert.Equal(["printing", "waiting", "offline"], queues.Jobs.Select(job => job.Status));
        Assert.Equal("Office, 2nd floor", queues.Jobs[2].Printer);
        Assert.Equal(("anna", 12), (queues.Jobs[2].Owner, queues.Jobs[2].Pages));
    }

    [Fact]
    public void A_pc_without_printers_or_jobs_has_nothing_waiting()
    {
        var queues = PrintQueues.Compose([], []);

        Assert.Equal(0, queues.JobCount);
        Assert.Empty(queues.Printers);
        Assert.Null(queues.DefaultPrinter);
        Assert.Null(queues.Attributes()["default_printer"]);
        Assert.Equal(0, queues.Attributes()["printer_count"]);
    }

    [Fact]
    public void The_list_of_jobs_is_cut_and_the_count_is_not()
    {
        var many = Enumerable.Range(1, 50).Select(number => new PrintJobRow($"Brother HL-L2350DW, {number}", $"Page {number}", "viktor", 0, 1)).ToList();

        var queues = PrintQueues.Compose(Printers, many);

        Assert.Equal(50, queues.JobCount);
        Assert.Equal(50, queues.Printers[0].Jobs);
        Assert.Equal(PrintQueues.MaximumJobsListed, queues.Jobs.Count);
        Assert.Equal("Page 1", queues.Jobs[0].Document);
    }

    [Theory]
    [InlineData("Brother HL-L2350DW, 12", "Brother HL-L2350DW")]
    [InlineData("Office, 2nd floor, 4", "Office, 2nd floor")]
    [InlineData("Office, 2nd floor", "Office, 2nd floor")]   // no number: the whole of it is the name
    [InlineData("Printer,7", "Printer")]
    [InlineData("Printer, ", "Printer,")]
    public void A_job_is_named_after_its_printer_and_its_number(string jobName, string printer)
    {
        Assert.Equal(printer, PrintQueues.PrinterOf(jobName));
    }

    [Theory]
    [InlineData(3, 0L, "idle")]
    [InlineData(4, 0L, "printing")]
    [InlineData(3, 0x400L, "printing")]
    [InlineData(5, 0L, "warming_up")]
    [InlineData(6, 0L, "stopped")]
    [InlineData(3, 0x1L, "paused")]
    [InlineData(4, 0x80L, "offline")]             // offline wins over what it was doing
    [InlineData(4, 0x10L, "error")]               // out of paper while printing
    [InlineData(3, 0x20000L, "idle")]             // low toner still prints
    [InlineData(3, 0x40000L, "error")]            // no toner does not
    [InlineData(3, 0x400000L, "error")]           // the door is open
    [InlineData(1, 0L, "unknown")]
    [InlineData(2, 0L, "unknown")]
    public void A_printer_s_state_in_one_word_names_what_stops_it_first(int printerStatus, long states, string word)
    {
        Assert.Equal(word, PrintQueues.PrinterStatus(printerStatus, states));
    }

    [Fact]
    public void A_printer_switched_to_work_offline_or_reported_offline_is_offline_in_every_way()
    {
        var queues = PrintQueues.Compose(
        [
            new("A", false, WorkOffline: true, PrinterStatus: 3, PrinterState: 0),
            new("B", false, WorkOffline: false, PrinterStatus: 7, PrinterState: 0),
            new("C", false, WorkOffline: false, PrinterStatus: 3, PrinterState: 0x80),
            new("D", false, WorkOffline: false, PrinterStatus: 3, PrinterState: 0)
        ], []);

        Assert.Equal(["offline", "offline", "offline", "idle"], queues.Printers.Select(printer => printer.Status));
        Assert.Equal([true, true, true, false], queues.Printers.Select(printer => printer.Is("offline")));
    }

    [Theory]
    [InlineData(0L, "waiting")]
    [InlineData(0x8L, "spooling")]
    [InlineData(0x10L, "printing")]
    [InlineData(0x18L, "printing")]
    [InlineData(0x1L, "paused")]
    [InlineData(0x12L, "error")]
    [InlineData(0x50L, "paper_out")]
    [InlineData(0x20L, "offline")]
    [InlineData(0x200L, "blocked")]
    [InlineData(0x400L, "blocked")]
    [InlineData(0x4L, "deleting")]
    [InlineData(0x80L, "printed")]
    public void A_job_s_state_in_one_word_names_what_stops_it_first(long statusMask, string word)
    {
        Assert.Equal(word, PrintQueues.JobStatus(statusMask));
    }

    [Fact]
    public void Every_state_of_every_printer_is_in_the_attributes_by_name()
    {
        var queues = PrintQueues.Compose(
        [
            new("Brother HL-L2350DW", Default: true, WorkOffline: false, PrinterStatus: 4, PrinterState: 0x400 | 0x20000),
            new("Kitchen", Default: false, WorkOffline: false, PrinterStatus: 3, PrinterState: 0x10 | 0x400000)
        ], Jobs);
        var attributes = new Dictionary<string, IReadOnlyDictionary<string, object?>> { ["print_jobs"] = queues.Attributes() };

        object? Value(string path) => SystemMetricsService.ReadBuiltInAttributeValue(path, attributes);

        Assert.Equal("Brother HL-L2350DW", Text(Value("print_jobs.default_printer")));
        Assert.Equal("2", Text(Value("print_jobs.default_printer_jobs")));
        Assert.Equal("2", Text(Value("print_jobs.printer_count")));
        Assert.Equal("printing", Text(Value("print_jobs.printers[0].status")));
        Assert.Equal("True", Text(Value("print_jobs.printers[0].toner_low")), ignoreCase: true);
        Assert.Equal("False", Text(Value("print_jobs.printers[0].paper_out")), ignoreCase: true);
        Assert.Equal("Kitchen", Text(Value("print_jobs.printers[1].name")));
        Assert.Equal("error", Text(Value("print_jobs.printers[1].status")));
        Assert.Equal("True", Text(Value("print_jobs.printers[1].paper_out")), ignoreCase: true);
        Assert.Equal("True", Text(Value("print_jobs.printers[1].door_open")), ignoreCase: true);
        Assert.Equal("Invoice.pdf", Text(Value("print_jobs.jobs[0].document")));

        // Each flag once, and each under its own bit.
        Assert.Equal(PrintQueues.StateFlags.Count, PrintQueues.StateFlags.Select(flag => flag.Name).Distinct().Count());
        Assert.Equal(PrintQueues.StateFlags.Count, PrintQueues.StateFlags.Select(flag => flag.Bit).Distinct().Count());
        Assert.All(PrintQueues.StateFlags, flag => Assert.True(long.IsPow2(flag.Bit), flag.Name));
    }

    private static string Text(object? value) => value switch
    {
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
        JsonElement element => element.GetRawText(),
        null => string.Empty,
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
    };

    [Fact]
    public void The_attributes_button_offers_the_main_ones_and_the_editor_every_state()
    {
        var sensor = BuiltInSensorCatalog.Find("print_jobs")!;

        Assert.False(sensor.EnabledByDefault);
        Assert.True(sensor.SupportsService);
        Assert.True(sensor.SupportsTrayApp);

        var button = BuiltInSensorCatalog.SuggestedAttributePaths(sensor);
        Assert.Contains("print_jobs.default_printer_jobs", button);
        Assert.Contains("print_jobs.printers[0].jobs", button);
        Assert.DoesNotContain(button, path => path.EndsWith(".paper_out", StringComparison.Ordinal));
        Assert.DoesNotContain(button, path => path.StartsWith("print_jobs.jobs", StringComparison.Ordinal));

        // What the button makes a sensor of has a name in the language of Home Assistant.
        using (var stream = typeof(HASS.Agent.Companion.Localization.Strings).Assembly.GetManifestResourceStream("lang.en.json")!)
        using (var document = JsonDocument.Parse(stream))
        {
            var english = document.RootElement.GetProperty("strings");
            Assert.All(sensor.AttributePaths!, path =>
            {
                var name = System.Text.RegularExpressions.Regex.Replace(path[(path.LastIndexOf('.') + 1)..], @"\[\d+\]", string.Empty);
                Assert.True(english.TryGetProperty($"Attribute.{name}", out _), name);
            });
        }

        var editor = BuiltInSensorCatalog.EditorAttributePaths(sensor);
        Assert.All(button, path => Assert.Contains(path, editor));
        Assert.All(PrintQueues.StateFlags, flag => Assert.Contains($"print_jobs.printers[0].{flag.Name}", editor));
        Assert.Contains("print_jobs.jobs[0].document", editor);
        Assert.Equal(editor.Count, editor.Distinct().Count());
    }

    [Fact]
    public void A_printer_sensor_that_was_never_read_still_gets_a_path_for_every_printer()
    {
        // What the Attributes button falls back on right after the sensor is ticked: the
        // printers counted on their own, the way the drives are.
        Assert.Equal(PrintQueues.PrinterCount(), BuiltInSensorCatalog.CountedOnItsOwn("print_jobs.printers"));
        Assert.Equal(PrintQueues.PrinterCount(), BuiltInSensorCatalog.CountedOnItsOwn("PRINT_JOBS.PRINTERS"));
        Assert.Null(BuiltInSensorCatalog.CountedOnItsOwn("print_jobs.jobs"));
        Assert.Null(BuiltInSensorCatalog.CountedOnItsOwn("gpu_usage.adapters"));

        // With three printers counted, the button offers each of them.
        var sensor = BuiltInSensorCatalog.Find("print_jobs")!;
        var paths = BuiltInSensorCatalog.ExpandAttributePaths(sensor.AttributePaths!, path => path == "print_jobs.printers" ? 3 : null);
        Assert.Equal(
            ["print_jobs.printers[0].jobs", "print_jobs.printers[1].jobs", "print_jobs.printers[2].jobs"],
            paths.Where(path => path.EndsWith(".jobs", StringComparison.Ordinal) && path.Contains("printers", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_read_that_fails_is_tried_again_at_the_sensor_s_own_pace_only()
    {
        var asked = 0;
        PrintQueueSnapshot Fails()
        {
            asked++;
            throw new TimeoutException("No answer.");
        }

        using var log = new FileLog(Path.Combine(Path.GetTempPath(), "hass-agent-tests.log"));
        using var metrics = new SystemMetricsService(log, null) { PrintQueueReader = Fails };
        BuiltInSensorSetting[] settings = [new() { Key = "print_jobs", TrayApp = true }];
        IReadOnlySet<SensorPollingProfile> fast = new HashSet<SensorPollingProfile> { SensorPollingProfile.Fast };
        IReadOnlySet<SensorPollingProfile> normal = new HashSet<SensorPollingProfile> { SensorPollingProfile.Fast, SensorPollingProfile.Normal };

        // The first read of the app takes everything.
        var first = metrics.Read(builtInSensors: settings);
        Assert.Equal(1, asked);
        Assert.Null(first.PrintJobs);

        // Fast cycles and pushes are not the sensor's turn: nothing is asked again.
        metrics.Read(dueProfiles: fast, builtInSensors: settings);
        metrics.Read(dueProfiles: fast, builtInSensors: settings);
        metrics.Read(dueProfiles: fast, builtInSensors: settings, pushOnly: true);
        Assert.Equal(1, asked);

        // Its own cycle is.
        metrics.Read(dueProfiles: normal, builtInSensors: settings);
        Assert.Equal(2, asked);

        // Switched off and on again, it is read at once, as a sensor that has just been ticked.
        metrics.Read(dueProfiles: fast, builtInSensors: [new() { Key = "cpu_usage", TrayApp = true }]);
        metrics.Read(dueProfiles: fast, builtInSensors: settings);
        Assert.Equal(3, asked);
    }

    [Fact]
    public void A_read_that_works_is_reported_and_kept_between_its_turns()
    {
        var asked = 0;
        using var log = new FileLog(Path.Combine(Path.GetTempPath(), "hass-agent-tests.log"));
        using var metrics = new SystemMetricsService(log, null)
        {
            PrintQueueReader = () =>
            {
                asked++;
                return PrintQueues.Compose(Printers, Jobs.Take(asked).ToList());
            }
        };
        BuiltInSensorSetting[] settings = [new() { Key = "print_jobs", TrayApp = true }];

        Assert.Equal(1, metrics.Read(builtInSensors: settings).PrintJobs);
        Assert.Equal(1, metrics.Read(dueProfiles: new HashSet<SensorPollingProfile> { SensorPollingProfile.Fast }, builtInSensors: settings).PrintJobs);
        Assert.Equal(2, metrics.Read(dueProfiles: new HashSet<SensorPollingProfile> { SensorPollingProfile.Normal }, builtInSensors: settings).PrintJobs);
        Assert.Equal(2, asked);
    }

    [Fact]
    public void An_idle_printer_has_no_state_flag_up()
    {
        // 0 is "no flag", not the first name in Windows' list of them (paused): what the three
        // idle printers of a PC report.
        var queues = PrintQueues.Compose([new("Idle", Default: true, WorkOffline: false, PrinterStatus: 3, PrinterState: 0)], []);

        Assert.Equal("idle", queues.Printers[0].Status);
        Assert.All(PrintQueues.StateFlags, flag => Assert.False(queues.Printers[0].Is(flag.Name), flag.Name));
    }

    [Fact]
    public void The_print_queues_of_this_pc_can_be_read()
    {
        PrintQueueSnapshot queues;
        try
        {
            queues = PrintQueues.Read();
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or System.Runtime.InteropServices.COMException)
        {
            Assert.Skip($"The print spooler of this PC does not answer: {ex.Message}");
            return;
        }

        Assert.InRange(queues.Printers.Sum(printer => printer.Jobs), 0, queues.JobCount);
        Assert.All(queues.Printers, printer => Assert.False(string.IsNullOrWhiteSpace(printer.Name)));
        Assert.InRange(queues.Printers.Count(printer => printer.Default), 0, 1);

        // Through the sensor cycle, as the tray app reads it: a value, and the attributes.
        using var log = new FileLog(Path.Combine(Path.GetTempPath(), "hass-agent-tests.log"));
        using var metrics = new SystemMetricsService(log, null);
        var message = metrics.Read(builtInSensors: [new BuiltInSensorSetting { Key = "print_jobs", TrayApp = true }]);

        // The settings window is asked for the printers' attributes before the sensor was ever
        // read; the count it falls back on is the one the sensor then finds.
        Assert.Equal(queues.Printers.Count, PrintQueues.PrinterCount());

        Assert.NotNull(message.PrintJobs);
        Assert.True(message.Attributes.ContainsKey("print_jobs"));
        Assert.Equal(queues.Printers.Count, SystemMetricsService.ListCount("print_jobs.printers"));

        // Switched off, it is neither read nor reported.
        using var without = new SystemMetricsService(log, null);
        var off = without.Read(builtInSensors: [new BuiltInSensorSetting { Key = "cpu_usage", TrayApp = true }]);
        Assert.Null(off.PrintJobs);
        Assert.False(off.Attributes.ContainsKey("print_jobs"));
    }
}
