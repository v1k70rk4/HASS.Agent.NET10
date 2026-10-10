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
        Assert.Contains(@"Backup: C:\Tools\backup.exe --full", shown[0]);
        Assert.Contains("(Get-Date).Hour", shown[1]);
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
}
