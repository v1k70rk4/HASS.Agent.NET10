using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.SystemCommands;
using HASS.Agent.Companion.SystemService;
using HASS.Agent.Companion.SystemStatus;
using HASS.Agent.Companion.Tray;

namespace HASS.Agent.Companion.Tests;

public class ServiceReloadTests
{
    [Fact]
    public void The_tray_apps_bookkeeping_does_not_restart_the_service()
    {
        var before = new CompanionSettings { MqttHost = "broker.local", LastRunVersion = "10.9.1-beta.1" };
        var after = new CompanionSettings { MqttHost = "broker.local", LastRunVersion = "10.9.1-beta.2", SettingsAccessDecided = true };
        before.SerialNumber = after.SerialNumber = "0123456789abcdef0123456789abcdef";

        Assert.Equal(
            SystemService.CompanionWindowsService.WhatTheRuntimeUses(before),
            SystemService.CompanionWindowsService.WhatTheRuntimeUses(after));
        Assert.Equal("10.9.1-beta.2", after.LastRunVersion);   // given back as it was
        Assert.True(after.SettingsAccessDecided);
    }

    [Fact]
    public void A_setting_the_service_uses_restarts_it()
    {
        var before = new CompanionSettings { MqttHost = "broker.local" };
        var after = new CompanionSettings { MqttHost = "other.local" };
        before.SerialNumber = after.SerialNumber = "0123456789abcdef0123456789abcdef";

        Assert.NotEqual(
            SystemService.CompanionWindowsService.WhatTheRuntimeUses(before),
            SystemService.CompanionWindowsService.WhatTheRuntimeUses(after));
    }
}

public class ServicePolicyTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("hass-agent-policy-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static CustomCommandDefinition Command(string type, string command, bool service = true, string id = "cmd") =>
        new() { Id = id, Name = $"{type} {command}", Type = type, Command = command, Service = service };

    private static CustomSensorDefinition Sensor(string type, string parameter, bool service = true, string id = "sensor") =>
        new() { Id = id, Name = $"{type} {parameter}", Type = type, Parameter = parameter, Service = service };

    private static CompanionSettings Settings(IEnumerable<CustomCommandDefinition>? commands = null, IEnumerable<CustomSensorDefinition>? sensors = null) =>
        new() { CustomCommands = commands?.ToList() ?? [], CustomSensors = sensors?.ToList() ?? [] };

    [Theory]
    [InlineData(CustomCommandTypes.Process, true)]
    [InlineData(CustomCommandTypes.PowerShell, true)]
    [InlineData(CustomCommandTypes.Pwsh, true)]
    [InlineData(CustomCommandTypes.Key, false)]     // the service never runs these
    [InlineData(CustomCommandTypes.Url, false)]
    [InlineData(CustomCommandTypes.WebView, false)]
    [InlineData("some_future_type", true)]          // unknown: approval needed
    public void Commands_that_run_something_need_approval(string type, bool needed)
    {
        Assert.Equal(needed, ServicePolicy.NeedsApproval(Command(type, "x")));
    }

    [Fact]
    public void Commands_of_the_tray_app_only_need_no_approval()
    {
        Assert.False(ServicePolicy.NeedsApproval(Command(CustomCommandTypes.Process, "notepad.exe", service: false)));
        Assert.False(ServicePolicy.NeedsApproval(new CustomCommandDefinition { Type = CustomCommandTypes.Process, Command = "x", Service = true, Enabled = false }));
    }

    [Theory]
    [InlineData(CustomSensorTypes.Command, true)]
    [InlineData(CustomSensorTypes.CommandPowerShell, true)]
    [InlineData(CustomSensorTypes.CommandPwsh, true)]
    [InlineData("some_future_type", true)]
    [InlineData(CustomSensorTypes.ProcessRunning, false)]
    [InlineData(CustomSensorTypes.ServiceStatus, false)]
    [InlineData(CustomSensorTypes.DiskFree, false)]
    [InlineData(CustomSensorTypes.BuiltInAttribute, false)]
    [InlineData(CustomSensorTypes.LibreHardwareMonitor, false)]
    public void Sensors_that_run_something_need_approval(string type, bool needed)
    {
        Assert.Equal(needed, ServicePolicy.NeedsApproval(Sensor(type, "x")));
    }

    [Fact]
    public void Approved_commands_stay_with_the_service_others_go_to_the_tray_app()
    {
        var approved = Command(CustomCommandTypes.PowerShell, "Restart-Service Spooler", id: "a");
        var settings = Settings([approved], [Sensor(CustomSensorTypes.CommandPowerShell, "(Get-Date).Hour", id: "s")]);
        var policy = ServicePolicy.FromSettings(settings);

        // Someone who can write the settings adds a command and changes the sensor.
        var planted = Command(CustomCommandTypes.Process, @"C:\Users\Public\evil.exe", id: "b");
        settings.CustomCommands.Add(planted);
        settings.CustomSensors[0].Parameter = "Start-Process evil.exe";

        var restricted = policy.Restrict(settings);

        Assert.True(approved.Service);
        Assert.False(planted.Service);
        Assert.False(settings.CustomSensors[0].Service);
        Assert.Equal(2, restricted.Count);
    }

    [Fact]
    public void Changing_what_a_command_runs_needs_a_new_approval()
    {
        var command = Command(CustomCommandTypes.Process, "backup.exe");
        var policy = ServicePolicy.FromSettings(Settings([command]));
        Assert.True(policy.Allows(command));

        command.Arguments = "--delete-everything";
        Assert.False(policy.Allows(command));

        command.Arguments = string.Empty;
        command.Id = "other";
        Assert.False(policy.Allows(command));
    }

    [Fact]
    public void Renaming_a_command_needs_no_new_approval()
    {
        var command = Command(CustomCommandTypes.Process, "backup.exe");
        var policy = ServicePolicy.FromSettings(Settings([command]));

        command.Name = "Nightly backup";

        Assert.True(policy.Allows(command));
    }

    [Fact]
    public void Without_a_policy_file_nothing_is_approved()
    {
        var settings = Settings([Command(CustomCommandTypes.Process, "x")]);

        var restricted = ServicePolicy.Load(Path.Combine(_folder, "missing.json")).Restrict(settings);

        Assert.Single(restricted);
        Assert.False(settings.CustomCommands[0].Service);
    }

    [Fact]
    public void Unreadable_policy_approves_nothing()
    {
        var path = Path.Combine(_folder, ServicePolicy.FileName);
        File.WriteAllText(path, "{ not json");

        var policy = ServicePolicy.Load(path);

        Assert.Empty(policy.Commands);
        Assert.Empty(policy.Sensors);
    }

    [Fact]
    public void Policy_survives_a_save_and_a_load()
    {
        var settings = Settings(
            [Command(CustomCommandTypes.Pwsh, "Get-Process", id: "c1"), Command(CustomCommandTypes.Key, "win+r", id: "c2")],
            [Sensor(CustomSensorTypes.Command, "hostname", id: "s1"), Sensor(CustomSensorTypes.DiskFree, "C:", id: "s2")]);
        var path = Path.Combine(_folder, ServicePolicy.FileName);

        ServicePolicy.FromSettings(settings).Save(path);
        var loaded = ServicePolicy.Load(path);

        Assert.Equal(["c1"], loaded.Commands.Select(entry => entry.Id));
        Assert.Equal(["s1"], loaded.Sensors.Select(entry => entry.Id));
        Assert.Empty(loaded.Restrict(settings));
    }

    [Fact]
    public void A_policy_covers_a_smaller_one_but_not_a_larger_one()
    {
        var one = Command(CustomCommandTypes.Process, "a.exe", id: "1");
        var two = Command(CustomCommandTypes.Process, "b.exe", id: "2");
        var small = ServicePolicy.FromSettings(Settings([one]));
        var large = ServicePolicy.FromSettings(Settings([one, two]));

        Assert.True(large.Covers(small));
        Assert.False(small.Covers(large));
    }

    [Fact]
    public void An_update_keeps_what_was_approved_and_adds_nothing()
    {
        var approved = Command(CustomCommandTypes.Process, "backup.exe", id: "a");
        var changed = Command(CustomCommandTypes.Process, "report.exe", id: "b");
        var removed = Command(CustomCommandTypes.Process, "old.exe", id: "c");
        var before = ServicePolicy.FromSettings(Settings([approved, changed, removed]));

        // Meanwhile someone who can write the settings adds one and changes another.
        changed.Arguments = "--upload-everything";
        var planted = Command(CustomCommandTypes.PowerShell, "net localgroup administrators me /add", id: "d");
        var settings = Settings([approved, changed, planted]);

        var kept = before.KeepOnly(settings);

        Assert.True(kept.Allows(approved));
        Assert.False(kept.Allows(changed));
        Assert.False(kept.Allows(planted));
        Assert.Equal(["a"], kept.Commands.Select(entry => entry.Id));
    }

    [Fact]
    public void The_approval_is_for_exactly_what_was_shown()
    {
        var policy = ServicePolicy.FromSettings(Settings([Command(CustomCommandTypes.Process, "a.exe", id: "1")]));
        var shown = Settings([Command(CustomCommandTypes.Process, "a.exe", id: "1"), Command(CustomCommandTypes.Process, "b.exe", id: "2")]);
        var key = policy.PendingKey(shown);

        // The same settings, read again by the elevated process: the same key.
        var same = Settings([Command(CustomCommandTypes.Process, "a.exe", id: "1"), Command(CustomCommandTypes.Process, "b.exe", id: "2")]);
        Assert.Equal(key, policy.PendingKey(same));

        // Changed while Windows asked for the administrator: another key, so nothing is approved.
        var swapped = Settings([Command(CustomCommandTypes.Process, "a.exe", id: "1"), Command(CustomCommandTypes.Process, "evil.exe", id: "2")]);
        Assert.NotEqual(key, policy.PendingKey(swapped));
        var added = Settings([.. same.CustomCommands, Command(CustomCommandTypes.Process, "c.exe", id: "3")]);
        Assert.NotEqual(key, policy.PendingKey(added));
    }

    [Fact]
    public void The_prompt_shows_what_each_one_runs()
    {
        var settings = Settings(
            [new CustomCommandDefinition { Id = "1", Name = "Backup", Type = CustomCommandTypes.Process, Command = @"C:\Tools\backup.exe", Arguments = "--full", Service = true }],
            [Sensor(CustomSensorTypes.CommandPowerShell, "(Get-Date).Hour", id: "2")]);

        var shown = ServiceApproval.PendingDescriptions(settings, new ServicePolicy());

        Assert.Equal(2, shown.Count);
        Assert.StartsWith("Backup [process]:", shown[0]);
        Assert.Contains(@"C:\Tools\backup.exe --full", shown[0]);
        Assert.Contains("(Get-Date).Hour", shown[1]);
    }

    [Fact]
    public void The_prompt_shows_a_script_whole_and_line_by_line()
    {
        // A comment sign on one line hides nothing that is on the next one.
        var script = "Write-Output OK #" + new string(' ', 200) + "\nStart-Process evil.exe";
        var settings = Settings(sensors: [Sensor(CustomSensorTypes.CommandPowerShell, script)]);

        var shown = ServiceApproval.PendingDescriptions(settings, new ServicePolicy()).Single();
        var lines = shown.Split(Environment.NewLine);

        Assert.Contains(lines, line => line.Trim() == "Start-Process evil.exe");
        Assert.Contains(lines, line => line.TrimStart().StartsWith("Write-Output OK #"));
    }

    [Fact]
    public void A_command_sensor_goes_to_home_assistant_as_a_hash()
    {
        var command = Sensor(CustomSensorTypes.CommandPowerShell, "Invoke-RestMethod -Headers @{Authorization='Bearer secret'}");
        var disk = Sensor(CustomSensorTypes.DiskFree, "C:");

        var published = Mqtt.MqttCompanionService.PublishedParameter(command);

        Assert.StartsWith("sha256:", published);
        Assert.DoesNotContain("secret", published);
        Assert.Equal("C:", Mqtt.MqttCompanionService.PublishedParameter(disk));

        command.Parameter += " ";
        Assert.NotEqual(published, Mqtt.MqttCompanionService.PublishedParameter(command));
    }

    [Fact]
    public void Pending_names_are_what_the_app_asks_to_approve()
    {
        var approved = Command(CustomCommandTypes.Process, "a.exe", id: "1");
        var policy = ServicePolicy.FromSettings(Settings([approved]));
        var settings = Settings(
            [approved, Command(CustomCommandTypes.Process, "b.exe", id: "2"), Command(CustomCommandTypes.Url, "https://x", id: "3")],
            [Sensor(CustomSensorTypes.CommandPwsh, "hostname", id: "4")]);

        Assert.Equal(["process b.exe", "command_pwsh hostname"], ServiceApproval.PendingNames(settings, policy));
    }

    [Fact]
    public void A_name_with_line_breaks_cannot_pass_for_entries_of_its_own()
    {
        // Written into the settings by someone else: one command, made to read as two harmless ones.
        var command = new CustomCommandDefinition
        {
            Id = "1", Type = CustomCommandTypes.PowerShell, Service = true,
            Name = "Disk cleanup:\r\n    cleanmgr.exe /sagerun:1\r\n\r\nNightly backup\u2028more",
            Command = "robocopy C:\\Data D:\\Backup /MIR; Start-Process evil.exe",
        };
        var settings = Settings([command]);

        var lines = ServiceApproval.PendingDescriptions(settings, new ServicePolicy()).Single().Split(Environment.NewLine);

        Assert.Equal(2, lines.Length);
        Assert.Equal("Disk cleanup:      cleanmgr.exe /sagerun:1    Nightly backup more [powershell]:", lines[0]);
        Assert.Equal("    robocopy C:\\Data D:\\Backup /MIR; Start-Process evil.exe", lines[1]);
        Assert.DoesNotContain(ServiceApproval.PendingNames(settings, new ServicePolicy()), name => name.Contains('\n') || name.Contains('\r'));
    }

    [Fact]
    public void A_name_cannot_turn_the_reading_direction_around()
    {
        // U+202E has what follows it written from right to left: "exe.tnemucod" would be
        // read as "document.exe". Not shown itself, like the other characters of its kind.
        Assert.Equal("Backup  exe.tnemucod", ServiceApproval.OneLine("Backup \u202Eexe.tnemucod\u2069"));
        Assert.Equal("Nightly  backup", ServiceApproval.OneLine("\u200BNightly\u200F backup\uFEFF"));
    }

    [Fact]
    public void What_a_command_runs_is_shown_with_nothing_hidden_in_it()
    {
        // In the text that is run nothing is left out: what cannot be seen is written out.
        var command = new CustomCommandDefinition
        {
            Id = "1", Name = "Report", Type = CustomCommandTypes.PowerShell, Service = true,
            Command = "Remove-Item C:\\Data \u202Etxt.troper\u202C -Recurse\tnow",
        };

        var lines = ServiceApproval.PendingDescriptions(Settings([command]), new ServicePolicy()).Single().Split(Environment.NewLine);

        Assert.Equal("    Remove-Item C:\\Data <U+202E>txt.troper<U+202C> -Recurse\tnow", lines[1]);
        Assert.Equal("plain text", ServiceApproval.Visible("plain text"));
    }

    [Fact]
    public void What_only_looks_like_a_space_or_like_nothing_is_written_out()
    {
        // Not a format character, any of these, and each reads as nothing or as a plain
        // space: a no-break space in "Program Files" is a folder a user can make on drive C.
        Assert.Equal(@"C:\Program<U+00A0>Files\App\app.exe", ServiceApproval.Visible(@"C:\Program" + "\u00A0" + @"Files\App\app.exe"));
        Assert.Equal("a<U+3000>b", ServiceApproval.Visible("a\u3000b"));                        // the wide space
        Assert.Equal(@"C:\Tools\back<U+E0100>up.exe", ServiceApproval.Visible(@"C:\Tools\back" + "\U000E0100" + "up.exe"));   // a variation selector
        Assert.Equal("back<U+FE0F>up", ServiceApproval.Visible("back\uFE0Fup"));
        Assert.Equal("back<U+3164>up", ServiceApproval.Visible("back\u3164up"));                // a Hangul filler: a letter that is blank
        Assert.Equal("back<U+2800>up", ServiceApproval.Visible("back\u2800up"));                // the blank Braille pattern: a symbol
        Assert.Equal("back<U+034F>up", ServiceApproval.Visible("back\u034Fup"));
        Assert.Equal("back<U+E000>up", ServiceApproval.Visible("back\uE000up"));                // private use: whatever a font makes of it
        Assert.Equal("back<U+E0080>up", ServiceApproval.Visible("back\U000E0080up"));           // not assigned
        Assert.Equal("Program Files", ServiceApproval.OneLine("Program\u00A0Files"));
        Assert.Equal("back up", ServiceApproval.OneLine("back\u3164up"));

        // What is seen stays as it is: letters of any script, an accent written as a
        // character of its own (a file name that came from a Mac), digits, signs.
        foreach (var seen in new[]
        {
            "\u00E1rv\u00EDzt\u0171r\u0151 t\u00FCk\u00F6rf\u00FAr\u00F3g\u00E9p.ps1",
            "a\u0301rvi\u0301z.ps1",
            "\u5907\u4EFD.ps1",
            "\u0440\u0435\u0437\u0435\u0440\u0432.ps1",
            "echo \U0001F600 \u20AC5 \u00BD \u2192 (x) [y] {z} #1 -a_b",
            @"C:\Program Files\App\app.exe /x ""two words""",
        })
        {
            Assert.Equal(seen, ServiceApproval.Visible(seen));
            Assert.Equal(seen, ServiceApproval.OneLine(seen));
        }
    }

    [Fact]
    public void The_code_of_a_character_cannot_be_read_as_a_part_of_the_path()
    {
        // Written with a backslash and a letter, "backup" + U+202E + "1sp.ps1" read as the
        // file u202E1sp.ps1 in the folder backup, which can be there too. No name of a file
        // or a folder has an angle bracket in it.
        Assert.Equal(@"C:\Scripts\backup<U+202E>1sp.ps1", ServiceApproval.Visible(@"C:\Scripts\backup" + "\u202E" + "1sp.ps1"));
        Assert.Contains('<', Path.GetInvalidFileNameChars());
        Assert.Contains('>', Path.GetInvalidFileNameChars());
    }

    [Fact]
    public void Letters_written_from_right_to_left_are_said_below_the_entry()
    {
        // No character is hidden here. The window lays Hebrew and Arabic letters out from the
        // right and takes the digits and the separators next to them along: the script 12.ps1
        // in a folder with a Hebrew name is shown as a script with a Hebrew name in a folder 12.
        const string hebrew = @"C:\Data\" + "\u05D0\u05D1" + @"\12.ps1";
        Assert.True(ServiceApproval.HasRightToLeftLetters(hebrew));
        Assert.True(ServiceApproval.HasRightToLeftLetters("echo \u0628\u0627\u0628"));            // Arabic
        Assert.True(ServiceApproval.HasRightToLeftLetters("x \U0001E900 y"));                     // Adlam, past U+FFFF
        Assert.False(ServiceApproval.HasRightToLeftLetters(@"C:\Data\backup.ps1 -Force"));
        Assert.False(ServiceApproval.HasRightToLeftLetters("\u00E1rv\u00EDz \u5907\u4EFD \u0440\u0435\u0437\u0435\u0440\u0432"));
        Assert.False(ServiceApproval.HasRightToLeftLetters(null));

        var settings = Settings(
        [
            new CustomCommandDefinition { Id = "1", Name = "Report", Type = CustomCommandTypes.PowerShell, Service = true, Command = hebrew },
            new CustomCommandDefinition { Id = "2", Name = "Plain", Type = CustomCommandTypes.PowerShell, Service = true, Command = "Restart-Service Spooler" },
        ]);

        var shown = ServiceApproval.PendingDescriptions(settings, new ServicePolicy());

        // The letters stay as they are (as codes the name could not be read at all), and a
        // line below says what they do.
        var lines = shown[0].Split(Environment.NewLine);
        Assert.Equal("    " + hebrew, lines[1]);
        Assert.Equal("    " + HASS.Agent.Companion.Localization.Strings.Get("SvcMgr.ApprovalRightToLeft"), lines[^1]);
        Assert.Equal(lines.Length - 1, shown[1].Split(Environment.NewLine).Length);
    }

    [Fact]
    public void A_character_that_is_not_shown_is_found_when_it_is_written_as_two()
    {
        // A character past U+FFFF is two chars in a string, and neither says what it is.
        // U+E0041 is a "tag" letter, one of those: it is not shown, and a path with it in it
        // is another path than the one that is read.
        const string tag = "\U000E0041";

        Assert.Equal("Nightly backup", ServiceApproval.OneLine($"Nightly{tag}backup"));
        Assert.Equal(@"C:\Tools\back<U+E0041>up.exe /x", ServiceApproval.Visible($@"C:\Tools\back{tag}up.exe /x"));

        // One that is shown stays as it is, whole.
        Assert.Equal("Report \U0001F600", ServiceApproval.OneLine("Report \U0001F600"));
        Assert.Equal("echo \U0001F600", ServiceApproval.Visible("echo \U0001F600"));

        // Half of a pair on its own is no character: not left for the window to make something of.
        Assert.Equal("a b", ServiceApproval.OneLine("a\uD83Db"));
        Assert.Equal("a<U+D83D>b", ServiceApproval.Visible("a\uD83Db"));
    }

    [Fact]
    public void The_service_keeps_no_command_of_the_tray_app()
    {
        var approved = Command(CustomCommandTypes.PowerShell, "Restart-Service Spooler", id: "a");
        var settings = Settings([approved]);
        var policy = ServicePolicy.FromSettings(settings);

        // Someone who can write the settings adds one for the tray app, and one for the service.
        settings.CustomCommands.Add(Command(CustomCommandTypes.PowerShell, "whoami", service: false, id: "tray"));
        settings.CustomCommands.Add(Command(CustomCommandTypes.Process, @"C:\Users\Public\evil.exe", id: "planted"));

        policy.Restrict(settings);
        ServicePolicy.DropTrayAppCommands(settings);

        Assert.Equal(["a"], settings.CustomCommands.Select(command => command.Id));
    }
}

// The service page reads these in the app's language; sc.exe's own words read the same in all.
[Collection(nameof(LocalizationCollection))]
public class ServiceStateTextTests
{
    [Fact]
    public void Every_state_of_the_service_has_a_text()
    {
        foreach (var status in Enum.GetValues<System.ServiceProcess.ServiceControllerStatus>())
        {
            Assert.NotEqual(Localization.Strings.Get("SvcMgr.Unknown"), CompanionServiceManager.StateText(status));
        }
    }

    [Theory]
    [InlineData(System.ServiceProcess.ServiceStartMode.Automatic, "SvcMgr.Automatic")]
    [InlineData(System.ServiceProcess.ServiceStartMode.Manual, "SvcMgr.Manual")]
    [InlineData(System.ServiceProcess.ServiceStartMode.Disabled, "SvcMgr.Disabled")]
    public void The_start_type_in_the_apps_words(System.ServiceProcess.ServiceStartMode mode, string key)
    {
        Assert.Equal(Localization.Strings.Get(key), CompanionServiceManager.StartTypeText(mode));
    }
}
