using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Runtime;
using HASS.Agent.Companion.SystemCommands;
using HASS.Agent.Companion.SystemStatus;

namespace HASS.Agent.Companion.Tests;

public class ClassicImportTests
{
    // As the community client writes them (Newtonsoft, enums by name, the key code as a number).
    private const string AppSettings = """
        {
          "DeviceName": "OFFICE-PC",
          "MqttEnabled": true,
          "MqttAddress": "192.168.1.10",
          "MqttPort": 8883,
          "MqttUseTls": true,
          "MqttUsername": "agent",
          "MqttPassword": "secret",
          "MqttUseRetainFlag": false,
          "HassUri": "http://homeassistant.local:8123",
          "HassToken": "token-123"
        }
        """;

    private const string Sensors = """
        [
          { "Type": "CpuLoadSensor", "Id": "0b9c6a8e-1111-4c2a-9e7c-000000000001", "Name": "CPU", "EntityName": "cpu", "UpdateInterval": 15 },
          { "Type": "ProcessActiveSensor", "Id": "0b9c6a8e-1111-4c2a-9e7c-000000000002", "Name": "Steam running", "EntityName": "steam", "Query": "steam", "UpdateInterval": 30 },
          { "Type": "PowershellSensor", "Id": "0b9c6a8e-1111-4c2a-9e7c-000000000003", "Name": "Uptime", "Query": "(Get-Date).Hour", "UpdateInterval": 3600 },
          { "Type": "WmiQuerySensor", "Id": "0b9c6a8e-1111-4c2a-9e7c-000000000004", "Name": "Fan", "Query": "SELECT * FROM Win32_Fan" }
        ]
        """;

    private const string Commands = """
        [
          { "Type": "LockCommand", "Id": "0b9c6a8e-2222-4c2a-9e7c-000000000001", "Name": "Lock" },
          { "Type": "HibernateCommand", "Id": "0b9c6a8e-2222-4c2a-9e7c-000000000002", "Name": "Hibernate" },
          { "Type": "CustomCommand", "Id": "0b9c6a8e-2222-4c2a-9e7c-000000000003", "Name": "Cancel shutdown", "Command": "shutdown /a" },
          { "Type": "CustomCommand", "Id": "0b9c6a8e-2222-4c2a-9e7c-000000000004", "Name": "Steam", "Command": "start steam://open/bigpicture" },
          { "Type": "PowershellCommand", "Id": "0b9c6a8e-2222-4c2a-9e7c-000000000005", "Name": "Script", "Command": "C:\\scripts\\x.ps1" },
          { "Type": "KeyCommand", "Id": "0b9c6a8e-2222-4c2a-9e7c-000000000006", "Name": "Play", "KeyCode": 179 },
          { "Type": "MultipleKeysCommand", "Id": "0b9c6a8e-2222-4c2a-9e7c-000000000007", "Name": "Copy and close", "Keys": [ "^c", "{ENTER}", "%{F4}" ] },
          { "Type": "LaunchUrlCommand", "Id": "0b9c6a8e-2222-4c2a-9e7c-000000000008", "Name": "HA", "Command": "{\"Url\":\"https://example.com\",\"Incognito\":false}" },
          { "Type": "WebViewCommand", "Id": "0b9c6a8e-2222-4c2a-9e7c-000000000009", "Name": "Dashboard", "Command": "{\"Url\":\"http://ha.local:8123/lovelace\",\"Width\":800,\"Height\":600}" },
          { "Type": "MediaNextCommand", "Id": "0b9c6a8e-2222-4c2a-9e7c-000000000010", "Name": "Next" },
          { "Type": "SetVolumeCommand", "Id": "0b9c6a8e-2222-4c2a-9e7c-000000000011", "Name": "Volume 30", "Command": "30" }
        ]
        """;

    private const string ServiceSensors = """
        [
          { "Type": "ServiceStateSensor", "Id": "0b9c6a8e-3333-4c2a-9e7c-000000000001", "Name": "Spooler", "Query": "Spooler" },
          { "Type": "ProcessActiveSensor", "Id": "0b9c6a8e-1111-4c2a-9e7c-000000000002", "Name": "Steam running", "Query": "steam" }
        ]
        """;

    private const string ServiceCommands = """
        [
          { "Type": "ShutdownCommand", "Id": "0b9c6a8e-4444-4c2a-9e7c-000000000001", "Name": "Shutdown" },
          { "Type": "LaunchUrlCommand", "Id": "0b9c6a8e-4444-4c2a-9e7c-000000000002", "Name": "Site", "Command": "{\"Url\":\"https://example.org\"}" }
        ]
        """;

    private static CompanionSettings NewSettings()
    {
        var settings = new CompanionSettings();
        settings.Normalize();
        return settings;
    }

    private static (CompanionSettings Settings, ClassicImportResult Result) ImportAll()
    {
        var settings = NewSettings();
        var result = ClassicImport.Apply(new ClassicFiles(AppSettings, Sensors, Commands, null, ServiceSensors, ServiceCommands), settings);
        return (settings, result);
    }

    [Fact]
    public void The_connection_and_the_name_come_over_with_the_secrets_protected()
    {
        var (settings, result) = ImportAll();

        Assert.True(result.Connection);
        Assert.Equal("OFFICE-PC", settings.DeviceName);
        Assert.True(settings.MqttEnabled);
        Assert.Equal("192.168.1.10", settings.MqttHost);
        Assert.Equal(8883, settings.MqttPort);
        Assert.True(settings.MqttUseTls);
        Assert.Equal("agent", settings.MqttUsername);
        Assert.Equal("secret", settings.GetMqttPassword());
        Assert.Equal(string.Empty, settings.MqttPassword);
        Assert.False(settings.MqttRetainDiscovery);

        // The HA API beside it, as the classic client had it; the app offers a user of the PC's
        // own for the token once connected.
        Assert.True(settings.HaApiEnabled);
        Assert.Equal("http://homeassistant.local:8123", settings.HaApiUrl);
        Assert.Equal("token-123", settings.GetHaApiToken());
        Assert.Equal(string.Empty, settings.HaApiToken);
        Assert.True(result.Mqtt);
        Assert.True(result.HaApi);
    }

    [Fact]
    public void A_pc_without_mqtt_gets_the_ha_api()
    {
        var settings = NewSettings();
        var result = ClassicImport.Apply(new ClassicFiles(AppSettings.Replace("\"MqttEnabled\": true", "\"MqttEnabled\": false"), null, null, null, null, null), settings);

        Assert.False(settings.MqttEnabled);
        Assert.True(settings.HaApiEnabled);
        Assert.Equal("http://homeassistant.local:8123", settings.HaApiUrl);
        Assert.Equal("token-123", settings.GetHaApiToken());
        Assert.Equal(string.Empty, settings.HaApiToken);
        Assert.True(result.Connection);
        Assert.True(result.HaApi);
        Assert.False(result.Mqtt);
    }

    [Fact]
    public void Sensors_become_built_in_or_custom_ones()
    {
        var (settings, result) = ImportAll();

        var cpu = settings.BuiltInSensors.Single(sensor => sensor.Key == "cpu_usage");
        Assert.True(cpu.TrayApp);

        var steam = settings.CustomSensors.Single(sensor => sensor.Name == "Steam running");
        Assert.Equal(CustomSensorTypes.ProcessRunning, steam.Type);
        Assert.Equal("steam", steam.Parameter);
        Assert.Equal("normal", steam.PollingProfile);
        // In the client and in the service before: one sensor for both.
        Assert.True(steam.TrayApp);
        Assert.True(steam.Service);

        var powershell = settings.CustomSensors.Single(sensor => sensor.Name == "Uptime");
        Assert.Equal(CustomSensorTypes.CommandPowerShell, powershell.Type);
        Assert.Equal("hourly", powershell.PollingProfile);

        var spooler = settings.CustomSensors.Single(sensor => sensor.Name == "Spooler");
        Assert.Equal(CustomSensorTypes.ServiceStatus, spooler.Type);
        Assert.True(spooler.Service);
        Assert.False(spooler.TrayApp);

        Assert.Contains("Fan (WmiQuerySensor)", result.Skipped);
    }

    [Fact]
    public void Commands_become_system_or_custom_ones()
    {
        var (settings, result) = ImportAll();

        Assert.Contains("lock", settings.TrayAppCommands);
        Assert.Contains("hibernate", settings.TrayAppCommands);
        Assert.Contains("shutdown", settings.ServiceCommands);

        CustomCommandDefinition Command(string name) => settings.CustomCommands.Single(command => command.Name == name);

        Assert.Equal(CustomCommandTypes.Process, Command("Cancel shutdown").Type);
        Assert.Equal("shutdown /a", Command("Cancel shutdown").Command);

        // "start" is cmd's own: it keeps going through cmd, as the classic client ran it.
        Assert.Equal(SystemTools.InSystem32("cmd.exe"), Command("Steam").Command);
        Assert.Equal("/C start steam://open/bigpicture", Command("Steam").Arguments);

        Assert.Equal(CustomCommandTypes.PowerShell, Command("Script").Type);
        Assert.Equal("play_pause", Command("Play").Command);
        Assert.Equal("ctrl+c enter alt+f4", Command("Copy and close").Command);
        Assert.Equal("next_track", Command("Next").Command);

        Assert.Equal(CustomCommandTypes.Url, Command("HA").Type);
        Assert.Equal("https://example.com", Command("HA").Command);

        Assert.Equal(CustomCommandTypes.WebView, Command("Dashboard").Type);
        Assert.Equal("http://ha.local:8123/lovelace", Command("Dashboard").Command);
        Assert.Equal("800x600", Command("Dashboard").Arguments);

        // The classic service had it, but an address opens on the user's desktop.
        Assert.True(Command("Site").TrayApp);
        Assert.False(Command("Site").Service);

        Assert.Contains("Volume 30 (SetVolumeCommand)", result.Skipped);
    }

    [Fact]
    public void Importing_twice_adds_nothing_new()
    {
        var settings = NewSettings();
        var files = new ClassicFiles(AppSettings, Sensors, Commands, null, ServiceSensors, ServiceCommands);
        ClassicImport.Apply(files, settings);
        var sensors = settings.CustomSensors.Count;
        var commands = settings.CustomCommands.Count;

        ClassicImport.Apply(files, settings);

        Assert.Equal(sensors, settings.CustomSensors.Count);
        Assert.Equal(commands, settings.CustomCommands.Count);
    }

    [Fact]
    public void An_item_without_an_id_gets_the_same_one_every_time()
    {
        var settings = NewSettings();
        var commands = """
            [
              { "Type": "CustomCommand", "Name": "No id", "Command": "notepad.exe" },
              { "Type": "CustomCommand", "Id": "00000000-0000-0000-0000-000000000000", "Name": "Empty id", "Command": "calc.exe" }
            ]
            """;
        // The same friendly name and query, another entity name: two sensors.
        var sensors = """
            [
              { "Type": "ProcessActiveSensor", "Name": "No id", "EntityName": "notepad_a", "Query": "notepad" },
              { "Type": "ProcessActiveSensor", "Name": "No id", "EntityName": "notepad_b", "Query": "notepad" }
            ]
            """;
        var files = new ClassicFiles(null, sensors, commands, null, null, null);

        ClassicImport.Apply(files, settings);
        ClassicImport.Apply(files, settings);

        Assert.Equal(2, settings.CustomCommands.Count);
        Assert.Equal(2, settings.CustomSensors.Count);
        // Different items, different ids.
        Assert.NotEqual(settings.CustomCommands[0].Id, settings.CustomCommands[1].Id);
    }

    [Fact]
    public void The_lab02_format_is_read_too()
    {
        var settings = NewSettings();
        var commands = """
            [
              { "Type": "LaunchUrlCommand", "Id": "0b9c6a8e-5555-4c2a-9e7c-000000000001", "FriendlyName": "Open HA", "Name": "open_ha", "Command": "https://example.com" },
              { "Type": "KeyCommand", "Id": "0b9c6a8e-5555-4c2a-9e7c-000000000002", "FriendlyName": "Mute", "Name": "mute", "KeyCode": 173 }
            ]
            """;

        ClassicImport.Apply(new ClassicFiles(null, null, commands, null, null, null), settings);

        var url = settings.CustomCommands.Single(command => command.Name == "Open HA");
        Assert.Equal("https://example.com", url.Command);
        Assert.Equal("volume_mute", settings.CustomCommands.Single(command => command.Name == "Mute").Command);
    }

    [Fact]
    public void The_service_mqtt_settings_are_used_without_a_client()
    {
        var settings = NewSettings();
        var mqtt = """{ "MqttAddress": "broker.lan", "MqttPort": 1883, "MqttUseWebSocket": true, "MqttUsername": "svc", "MqttPassword": "pw" }""";

        var result = ClassicImport.Apply(new ClassicFiles(null, null, null, mqtt, null, null), settings);

        Assert.True(settings.MqttEnabled);
        Assert.Equal("broker.lan", settings.MqttHost);
        Assert.Equal("pw", settings.GetMqttPassword());
        Assert.True(result.WebSocketDropped);
    }

    [Fact]
    public void Broken_files_import_nothing()
    {
        var settings = NewSettings();

        var result = ClassicImport.Apply(new ClassicFiles("{ not json", "[ broken", "{}", null, null, null), settings);

        Assert.False(result.Connection);
        Assert.Empty(result.Sensors);
        Assert.Empty(result.Commands);
        Assert.False(settings.MqttEnabled);
    }

    [Theory]
    [InlineData(new[] { "^c" }, "ctrl+c")]
    [InlineData(new[] { "^+{ESC}" }, "ctrl+shift+esc")]
    [InlineData(new[] { "abc" }, "a b c")]
    [InlineData(new[] { "{F5}", "~" }, "f5 enter")]
    [InlineData(new[] { "a,b" }, "a comma b")]
    [InlineData(new[] { "{+}" }, "plus")]
    [InlineData(new[] { "Ab" }, "shift+a b")]
    [InlineData(new[] { "+A" }, "shift+a")]
    public void Send_keys_notation_is_converted(string[] keys, string expected)
    {
        Assert.Equal(expected, ClassicImport.ConvertSendKeys(keys));
    }

    [Theory]
    [InlineData("{LEFT 4}")]
    [InlineData("^(ac)")]
    [InlineData("{NOSUCHKEY}")]
    [InlineData("^")]
    [InlineData("a{")]
    [InlineData("{")]
    public void Send_keys_without_a_counterpart_are_left_out(string keys)
    {
        Assert.Null(ClassicImport.ConvertSendKeys([keys]));
    }

    [Fact]
    public void Every_key_name_given_to_a_key_code_is_one_this_app_knows()
    {
        for (var virtualKey = 0; virtualKey < 256; virtualKey++)
        {
            if (ClassicImport.KeyName(virtualKey) is { } name)
            {
                Assert.True(KeySender.TryParse(name, out _, out _), $"0x{virtualKey:X2} -> {name}");
            }
        }
    }

    [Theory]
    [InlineData(null, "normal")]
    [InlineData(10, "fast")]
    [InlineData(60, "normal")]
    [InlineData(3600, "hourly")]
    public void Update_intervals_become_polling_profiles(int? seconds, string expected)
    {
        Assert.Equal(expected, SensorPollingProfiles.ToKey(ClassicImport.ProfileFor(seconds)));
    }
}
