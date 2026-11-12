using System.Security.Principal;
using System.Text.Json;
using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Localization;
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
        new(@"\\server\Office", WorkOffline: false, State: 0, Jobs: 1),
        new("Brother HL-L2350DW", WorkOffline: false, State: 0, Jobs: 2),
        new("Microsoft Print to PDF", WorkOffline: false, State: 0, Jobs: 0)
    ];

    private static readonly PrintJobRow[] Jobs =
    [
        new("Brother HL-L2350DW", "Invoice.pdf", "viktor", StatusMask: 0x10, TotalPages: 2),
        new("Brother HL-L2350DW", "Label", "viktor", StatusMask: 0, TotalPages: 1),
        new(@"\\server\Office", "Report.docx", "anna", StatusMask: 0x20, TotalPages: 12)
    ];

    [Fact]
    public void Every_printer_has_the_number_of_jobs_in_its_own_queue_and_the_value_is_all_of_them()
    {
        var queues = PrintQueues.Compose(Printers, Jobs, "Brother HL-L2350DW");

        // By name, so an index stays on its printer; one from a print server comes last, as its
        // backslashes sort after the letters.
        Assert.Equal(["Brother HL-L2350DW", "Microsoft Print to PDF", @"\\server\Office"], queues.Printers.Select(printer => printer.Name));
        Assert.Equal([2, 0, 1], queues.Printers.Select(printer => printer.Jobs));
        Assert.Equal(3, queues.JobCount);
        Assert.Equal("Brother HL-L2350DW", queues.DefaultPrinter?.Name);
        Assert.Equal(2, queues.Attributes()["default_printer_jobs"]);

        // In the order they print in, each with its printer.
        Assert.Equal(["Invoice.pdf", "Label", "Report.docx"], queues.Jobs.Select(job => job.Document));
        Assert.Equal(["printing", "waiting", "offline"], queues.Jobs.Select(job => job.Status));
        Assert.Equal(@"\\server\Office", queues.Jobs[2].Printer);
        Assert.Equal(("anna", 12), (queues.Jobs[2].Owner, queues.Jobs[2].Pages));
    }

    [Fact]
    public void A_pc_without_printers_or_jobs_has_nothing_waiting()
    {
        var queues = PrintQueues.Compose([], [], defaultPrinter: null);

        Assert.Equal(0, queues.JobCount);
        Assert.Empty(queues.Printers);
        Assert.Null(queues.DefaultPrinter);
        Assert.Null(queues.Attributes()["default_printer"]);
        Assert.Equal(0, queues.Attributes()["printer_count"]);
    }

    [Fact]
    public void The_list_of_jobs_is_cut_and_the_count_is_what_the_spooler_counts()
    {
        var many = Enumerable.Range(1, 50).Select(number => new PrintJobRow("Brother HL-L2350DW", $"Page {number}", "viktor", 0, 1)).ToList();
        PrinterRow[] busy = [new("Brother HL-L2350DW", WorkOffline: false, State: 0, Jobs: 50)];

        var queues = PrintQueues.Compose(busy, many, defaultPrinter: null);

        Assert.Equal(50, queues.JobCount);
        Assert.Equal(50, queues.Printers[0].Jobs);
        Assert.Equal(PrintQueues.MaximumJobsListed, queues.Jobs.Count);
        Assert.Equal("Page 1", queues.Jobs[0].Document);

        // A queue whose jobs could not be listed still counts: the number is the spooler's.
        Assert.Equal(50, PrintQueues.Compose(busy, [], defaultPrinter: null).JobCount);
    }

    [Fact]
    public void Of_more_printers_than_are_asked_the_default_and_the_first_ones_are_asked_and_the_count_is_all_of_them()
    {
        var names = Enumerable.Range(1, 100).Select(number => $@"\\server\Queue {number}").ToList();

        // The default printer first, whatever its place and however it is written, then the
        // others in the order the spooler lists them.
        var asked = PrintSpooler.TheOnesAsked(names, @"\\SERVER\queue 90", PrintSpooler.MostPrinters);
        Assert.Equal(PrintSpooler.MostPrinters, asked.Count);
        Assert.Equal(@"\\server\Queue 90", asked[0]);
        Assert.Equal(names.Take(PrintSpooler.MostPrinters - 1), asked.Skip(1));

        // No default, or one that is not among them: the first ones as they are.
        Assert.Equal(names.Take(3), PrintSpooler.TheOnesAsked(names, defaultPrinter: null, most: 3));
        Assert.Equal(names.Take(3), PrintSpooler.TheOnesAsked(names, "Gone", most: 3));

        // Fewer than are asked: every one of them, in the spooler's order.
        Assert.Equal(names.Take(5), PrintSpooler.TheOnesAsked(names.Take(5).ToList(), defaultPrinter: null, PrintSpooler.MostPrinters));

        // The count is of every printer there is; the list has the ones that were asked.
        var queues = PrintQueues.Compose(Printers, Jobs, defaultPrinter: null, printerCount: 100);
        Assert.Equal(3, queues.Printers.Count);
        Assert.Equal(100, queues.Attributes()["printer_count"]);
        Assert.Equal(3, PrintQueues.Compose(Printers, Jobs, defaultPrinter: null).Attributes()["printer_count"]);
    }

    [Theory]
    [InlineData(0L, "idle")]
    [InlineData(0x400L, "printing")]
    [InlineData(0x4000L, "printing")]
    [InlineData(0x10000L, "warming_up")]
    [InlineData(0x1L, "paused")]
    [InlineData(0x480L, "offline")]               // offline wins over what it was doing
    [InlineData(0x2000000L, "offline")]           // its print server is away
    [InlineData(0x410L, "error")]                 // out of paper while printing
    [InlineData(0x20000L, "idle")]                // low toner still prints
    [InlineData(0x40000L, "error")]               // no toner does not
    [InlineData(0x400000L, "error")]              // the door is open
    [InlineData(0x800000L, "unknown")]            // the spooler could not say
    public void A_printer_s_state_in_one_word_names_what_stops_it_first(long states, string word)
    {
        Assert.Equal(word, PrintQueues.PrinterStatus(states));
    }

    [Fact]
    public void A_printer_switched_to_work_offline_or_reported_offline_is_offline_in_every_way()
    {
        var queues = PrintQueues.Compose(
        [
            new("A", WorkOffline: true, State: 0, Jobs: 0),
            new("B", WorkOffline: false, State: 0x80, Jobs: 0),
            new("C", WorkOffline: false, State: 0, Jobs: 0)
        ], [], defaultPrinter: null);

        Assert.Equal(["offline", "offline", "idle"], queues.Printers.Select(printer => printer.Status));
        Assert.Equal([true, true, false], queues.Printers.Select(printer => printer.Is("offline")));
    }

    [Fact]
    public void An_idle_printer_has_no_state_flag_up()
    {
        var queues = PrintQueues.Compose([new("Idle", WorkOffline: false, State: 0, Jobs: 0)], [], "Idle");

        Assert.Equal("idle", queues.Printers[0].Status);
        Assert.All(PrintQueues.StateFlags, flag => Assert.False(queues.Printers[0].Is(flag.Name), flag.Name));
    }

    [Theory]
    [InlineData(0L, "waiting")]
    [InlineData(0x8L, "spooling")]
    [InlineData(0x10L, "printing")]
    [InlineData(0x18L, "printing")]
    [InlineData(0x1L, "paused")]
    [InlineData(0x12L, "error")]
    [InlineData(0x2012L, "error")]                // what a job sent to a printer that is switched off has
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
            new("Brother HL-L2350DW", WorkOffline: false, State: 0x400 | 0x20000, Jobs: 2),
            new("Kitchen", WorkOffline: false, State: 0x10 | 0x400000, Jobs: 0)
        ], Jobs, "Brother HL-L2350DW");
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
    public void The_default_printer_is_the_user_s_and_not_what_the_asking_account_has()
    {
        // The service is told the printers as the logged-in user sees them, and the default
        // printer by its name from that user's registry: SYSTEM's own never comes into it.
        var forTheUser = PrintQueues.Compose(Printers, Jobs, "brother hl-l2350dw");
        Assert.Equal("Brother HL-L2350DW", forTheUser.DefaultPrinter?.Name);
        Assert.Single(forTheUser.Printers, printer => printer.Default);

        // A user's default may be one from a print server.
        Assert.Equal(@"\\server\Office", PrintQueues.Compose(Printers, Jobs, @"\\SERVER\office").DefaultPrinter?.Name);

        // Nobody logged in, or a default that is not among the printers: none.
        Assert.Null(PrintQueues.Compose(Printers, [], defaultPrinter: null).DefaultPrinter);
        Assert.Null(PrintQueues.Compose(Printers, [], "").DefaultPrinter);
        Assert.Null(PrintQueues.Compose(Printers, [], "Gone").DefaultPrinter);
    }

    [Theory]
    [InlineData("KX DRIVER for Universal Printing,winspool,LPT1:", "KX DRIVER for Universal Printing")]
    [InlineData("Microsoft Print to PDF,winspool,Ne00:", "Microsoft Print to PDF")]
    [InlineData(@"\\server\Office,winspool,Ne03:", @"\\server\Office")]
    [InlineData(" Brother ,winspool,USB001", "Brother")]
    [InlineData("Brother", "Brother")]
    [InlineData(",winspool,Ne00:", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_default_printer_is_the_name_in_front_of_what_Windows_keeps(string? device, string? printer)
    {
        Assert.Equal(printer, PrintQueues.PrinterOfDevice(device));
    }

    [Theory]
    // the sensor is ticked for: tray, service -> its attribute sensors: tray, service
    [InlineData(true, false, true, false)]
    [InlineData(false, true, false, true)]
    [InlineData(true, true, true, true)]
    [InlineData(false, false, true, true)]     // not ticked at all: wherever it can be read
    public void The_sensors_the_attributes_button_makes_are_read_where_the_sensor_is(
        bool trayTicked, bool serviceTicked, bool tray, bool service)
    {
        var printJobs = BuiltInSensorCatalog.Find("print_jobs")!;

        Assert.Equal((tray, service), BuiltInSensorCatalog.AttributeSensorRoles(printJobs, trayTicked, serviceTicked));
    }

    [Fact]
    public void A_role_that_cannot_read_a_sensor_does_not_get_its_attribute_sensors()
    {
        // Drive health is the service's, the active displays are the tray app's, whatever is ticked.
        var driveHealth = BuiltInSensorCatalog.Find("drive_health")!;
        var displays = BuiltInSensorCatalog.Find("active_display")!;

        Assert.Equal((false, true), BuiltInSensorCatalog.AttributeSensorRoles(driveHealth, trayTicked: true, serviceTicked: true));
        Assert.Equal((false, true), BuiltInSensorCatalog.AttributeSensorRoles(driveHealth, trayTicked: true, serviceTicked: false));
        Assert.Equal((true, false), BuiltInSensorCatalog.AttributeSensorRoles(displays, trayTicked: false, serviceTicked: true));
    }

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
        using (var stream = typeof(Strings).Assembly.GetManifestResourceStream("lang.en.json")!)
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
        // What the Attributes button falls back on right after the sensor is ticked, or while
        // only the service reads it: the printers counted on their own, the way the drives are.
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
                return PrintQueues.Compose([new("Brother HL-L2350DW", WorkOffline: false, State: 0, Jobs: asked)], [], defaultPrinter: null);
            }
        };
        BuiltInSensorSetting[] settings = [new() { Key = "print_jobs", TrayApp = true }];

        Assert.Equal(1, metrics.Read(builtInSensors: settings).PrintJobs);
        Assert.Equal(1, metrics.Read(dueProfiles: new HashSet<SensorPollingProfile> { SensorPollingProfile.Fast }, builtInSensors: settings).PrintJobs);
        Assert.Equal(2, metrics.Read(dueProfiles: new HashSet<SensorPollingProfile> { SensorPollingProfile.Normal }, builtInSensors: settings).PrintJobs);
        Assert.Equal(2, asked);
    }

    [Fact]
    public void The_print_queues_of_this_pc_can_be_read()
    {
        PrintQueueSnapshot queues;
        try
        {
            queues = PrintQueues.Read();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or TimeoutException)
        {
            Assert.Skip($"The print spooler of this PC does not answer: {ex.Message}");
            return;
        }

        // The printers Windows itself lists for this user, in the order of their names.
        var installed = System.Drawing.Printing.PrinterSettings.InstalledPrinters.Cast<string>()
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        Assert.Equal(installed, queues.Printers.Select(printer => printer.Name));
        Assert.Equal(installed.Count, PrintQueues.PrinterCount());
        Assert.Equal(queues.Printers.Sum(printer => printer.Jobs), queues.JobCount);
        Assert.InRange(queues.Jobs.Count, 0, Math.Min(queues.JobCount, PrintQueues.MaximumJobsListed));
        Assert.All(queues.Jobs, job => Assert.Contains(job.Printer, installed));

        // The default printer is the one Windows names for this user.
        var byWindows = new System.Drawing.Printing.PrinterSettings();
        if (installed.Count > 0 && byWindows.IsDefaultPrinter)
        {
            Assert.Equal(byWindows.PrinterName, PrintSpooler.DefaultPrinter());
            Assert.Equal(byWindows.PrinterName, queues.DefaultPrinter?.Name);
        }

        Assert.Null(PrintQueues.DefaultPrinterOf("S-1-5-21-1-2-3-4"));
        Assert.Null(PrintQueues.DefaultPrinterOf(null));

        // Through the sensor cycle, as the tray app reads it: a value, and the attributes.
        using var log = new FileLog(Path.Combine(Path.GetTempPath(), "hass-agent-tests.log"));
        using var metrics = new SystemMetricsService(log, null);
        var message = metrics.Read(builtInSensors: [new BuiltInSensorSetting { Key = "print_jobs", TrayApp = true }]);

        Assert.NotNull(message.PrintJobs);
        Assert.True(message.Attributes.ContainsKey("print_jobs"));
        Assert.Equal(installed.Count, SystemMetricsService.ListCount("print_jobs.printers"));

        // Switched off, it is neither read nor reported.
        using var without = new SystemMetricsService(log, null);
        var off = without.Read(builtInSensors: [new BuiltInSensorSetting { Key = "cpu_usage", TrayApp = true }]);
        Assert.Null(off.PrintJobs);
        Assert.False(off.Attributes.ContainsKey("print_jobs"));
    }

    [Fact]
    public void The_token_of_a_session_is_for_the_service_only()
    {
        // Windows gives a user's token to an account that is part of the operating system; the
        // tests, like the tray app, are not, and must not get one.
        using var identity = WindowsIdentity.GetCurrent();
        Assert.SkipWhen(identity.IsSystem, "Run as SYSTEM, which is given the token.");

        using var token = PrintSpooler.UserTokenOf((uint)System.Diagnostics.Process.GetCurrentProcess().SessionId);
        Assert.Null(token);
    }

    [Fact]
    public void The_classic_client_s_printer_sensors_come_over_to_this_one()
    {
        Assert.Equal(["print_jobs"], ClassicImport.BuiltInSensorsFor["PrintersSensors"]);
    }
}
