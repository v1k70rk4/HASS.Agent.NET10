using System.Text.Json;
using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.Runtime;
using HASS.Agent.Companion.SystemStatus;
using HASS.Agent.Companion.Tray;

namespace HASS.Agent.Companion.Tests;

public class SystemToolsTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("hass-agent-tools-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Windows_tools_are_started_from_system32()
    {
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "sc.exe"), SystemTools.InSystem32("sc.exe"));
        Assert.True(Path.IsPathFullyQualified(SystemTools.WindowsPowerShell));
        Assert.StartsWith(Environment.SystemDirectory, SystemTools.WindowsPowerShell, StringComparison.OrdinalIgnoreCase);
        Assert.True(Path.IsPathFullyQualified(SystemTools.Pwsh));
    }

    [Fact]
    public void Relative_path_entries_are_skipped()
    {
        // A pwsh.exe in a relative PATH entry resolves against the current directory.
        Directory.CreateDirectory(Path.Combine(_folder, "planted"));
        File.WriteAllText(Path.Combine(_folder, "planted", "pwsh.exe"), "");
        var previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = _folder;
        try
        {
            Assert.Null(SystemTools.FindOnPath("pwsh.exe", "planted;.;"));
        }
        finally
        {
            Environment.CurrentDirectory = previous;
        }
    }

    [Fact]
    public void First_absolute_entry_holding_the_file_wins()
    {
        var first = Directory.CreateDirectory(Path.Combine(_folder, "one")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(_folder, "two")).FullName;
        File.WriteAllText(Path.Combine(second, "pwsh.exe"), "");

        Assert.Equal(Path.Combine(second, "pwsh.exe"), SystemTools.FindOnPath("pwsh.exe", $"{first};\"{second}\""));
    }
}

public class SensorPayloadFilterTests
{
    private static readonly JsonSerializerOptions Options = new();

    // One real reading of this PC, shared by the tests: which values it holds does not
    // matter, only which keys survive the filter.
    private static readonly Lazy<SystemMetricsMessage> Reading = new(() =>
        new SystemMetricsService(new FileLog(Path.Combine(Path.GetTempPath(), "hass-agent-tests.log")), null, includeInteractiveMetrics: true).Read());

    private static SystemMetricsMessage Message() => Reading.Value;

    private static BuiltInSensorSetting On(string key, bool app = true, bool service = false) =>
        new() { Key = key, TrayApp = app, Service = service };

    [Fact]
    public void Switched_off_sensors_are_not_sent()
    {
        var payload = SensorPayloadFilter.WithoutDisabled(Message(), Options, [On("cpu_usage")], serviceRole: false);

        Assert.True(payload.ContainsKey("cpu_usage"));
        foreach (var key in new[] { "active_window", "active_process", "foreground_app_title", "logged_in_user", "clipboard_text_available", "network_address" })
        {
            Assert.False(payload.ContainsKey(key), key);
        }

        Assert.True(payload.ContainsKey("custom_sensors"));
        Assert.True(payload.ContainsKey("updated_at"));
    }

    [Fact]
    public void Attributes_of_switched_off_sensors_are_not_sent()
    {
        var payload = SensorPayloadFilter.WithoutDisabled(Message(), Options, [On("cpu_usage")], serviceRole: false);

        var attributes = payload["attributes"]?.AsObject();
        Assert.True(attributes is null || !attributes.ContainsKey("network_address"));
    }

    [Fact]
    public void The_role_decides()
    {
        var settings = new[] { On("active_window", app: false, service: true) };

        Assert.False(SensorPayloadFilter.WithoutDisabled(Message(), Options, settings, serviceRole: false).ContainsKey("active_window"));
        Assert.True(SensorPayloadFilter.WithoutDisabled(Message(), Options, settings, serviceRole: true).ContainsKey("active_window"));
    }

    [Fact]
    public void Display_light_keeps_what_it_needs()
    {
        var payload = SensorPayloadFilter.WithoutDisabled(Message(), Options, [On("display_brightness")], serviceRole: false);

        Assert.True(payload.ContainsKey("monitor_power_state"));
    }

    [Fact]
    public void Without_settings_nothing_is_removed()
    {
        var all = JsonSerializer.SerializeToNode(Message(), Options)!.AsObject();

        var payload = SensorPayloadFilter.WithoutDisabled(Message(), Options, null, serviceRole: false);

        Assert.Equal(all.Select(property => property.Key).Order(), payload.Select(property => property.Key).Order());
    }
}

public class LogRecordTests
{
    private static readonly DateTimeOffset Time = new(2026, 10, 7, 18, 0, 0, TimeSpan.FromHours(2));

    [Fact]
    public void One_line_message()
    {
        Assert.Equal($"2026-10-07T18:00:00.0000000+02:00 [INFO] Hello{Environment.NewLine}", FileLog.FormatRecord(Time, "INFO", "Hello"));
    }

    [Theory]
    [InlineData("title\r\n2026-10-07T18:00:01.0000000+02:00 [ERROR] fake record")]
    [InlineData("title\n2026-10-07T18:00:01.0000000+02:00 [ERROR] fake record")]
    [InlineData("title\r2026-10-07T18:00:01.0000000+02:00 [ERROR] fake record")]
    [InlineData("title\u20282026-10-07T18:00:01.0000000+02:00 [ERROR] fake record")]
    public void Text_from_outside_cannot_start_a_record_of_its_own(string message)
    {
        var lines = FileLog.FormatRecord(Time, "INFO", message).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.StartsWith("2026-10-07T18:00:00", lines[0]);
        Assert.StartsWith("    ", lines[1]);
    }
}

public class UpdateDialogLinkTests
{
    [Theory]
    [InlineData("https://github.com/v1k70rk4/HASS.Agent.NET10/releases/tag/v10.9.0", true)]
    [InlineData("https://GitHub.com/v1k70rk4/HASS.Agent.NET10/releases/latest", true)]
    [InlineData("http://github.com/v1k70rk4/HASS.Agent.NET10", false)]
    [InlineData("https://github.com.evil.example/x", false)]
    [InlineData("https://evil.example/github.com", false)]
    [InlineData("https://user@github.com/x", false)]
    [InlineData("https://github.com:8443/x", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("C:\\Windows\\System32\\cmd.exe", false)]
    [InlineData("ms-settings:", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_pages_on_github_open(string? url, bool opens)
    {
        Assert.Equal(opens, UpdatePromptDialog.IsGitHubPage(url));
    }
}

public class AddressWithoutSchemeTests
{
    // An address typed without http:// or https://: plain only inside the home network,
    // so the Home Assistant token never goes over the internet unencrypted by a guess.
    [Theory]
    [InlineData("homeassistant.local:8123", "http://homeassistant.local:8123")]
    [InlineData("192.168.1.10:8123", "http://192.168.1.10:8123")]
    [InlineData("10.0.0.5", "http://10.0.0.5")]
    [InlineData("homeassistant:8123", "http://homeassistant:8123")]
    [InlineData("[fd00::5]:8123", "http://[fd00::5]:8123")]
    [InlineData("100.101.1.2:8123", "https://100.101.1.2:8123")]   // CGNAT: an ISP's as well as Tailscale's
    [InlineData("user:pass@ha.example.com", "https://user:pass@ha.example.com")]
    [InlineData("user@192.168.1.10:8123", "http://user@192.168.1.10:8123")]
    [InlineData("abcdef.ui.nabu.casa", "https://abcdef.ui.nabu.casa")]
    [InlineData("ha.example.com:8443/", "https://ha.example.com:8443")]
    [InlineData("8.8.8.8", "https://8.8.8.8")]
    [InlineData("http://ha.example.com", "http://ha.example.com")]
    [InlineData("https://192.168.1.10:8123", "https://192.168.1.10:8123")]
    public void The_scheme_is_chosen_by_where_the_address_points(string typed, string saved)
    {
        var settings = new CompanionSettings { HaApiUrl = typed };
        settings.Normalize();

        Assert.Equal(saved, settings.HaApiUrl);
    }

    [Theory]
    [InlineData("homeassistant.local:8123", "ws://homeassistant.local:8123/api/websocket")]
    [InlineData("abcdef.ui.nabu.casa", "wss://abcdef.ui.nabu.casa/api/websocket")]
    [InlineData("https://abcdef.ui.nabu.casa", "wss://abcdef.ui.nabu.casa/api/websocket")]
    [InlineData("http://192.168.1.10:8123/", "ws://192.168.1.10:8123/api/websocket")]
    public void The_websocket_address_follows_the_same_rule(string url, string expected)
    {
        Assert.Equal(expected, HASS.Agent.Companion.Mqtt.HaWebSocketService.BuildWebSocketUrl(url));
    }
}

public class LibreHardwareMonitorLimitTests
{
    [Fact]
    public void Long_names_are_cut()
    {
        var name = new string('x', 10_000);
        var json = $$"""{"Text":"root","Children":[{"Text":"pc","Children":[{"Text":"{{name}}","Children":[{"Text":"Temperatures","Children":[{"Text":"{{name}}","Value":"45 °C","SensorId":"/{{name}}"}]}]}]}]}""";

        var reading = Assert.Single(LibreHardwareMonitorClient.Parse(json));

        Assert.Equal(LibreHardwareMonitorClient.MaxNameLength, reading.Name.Length);
        Assert.Equal(LibreHardwareMonitorClient.MaxIdLength, reading.Id.Length);
        Assert.Equal(LibreHardwareMonitorClient.MaxNameLength, reading.Hardware.Length);
    }

    [Fact]
    public void An_id_a_real_monitor_sends_is_kept_whole()
    {
        var id = "/lpc/nct6798d/0/" + new string('t', 300);
        var json = $$"""{"Text":"root","Children":[{"Text":"pc","Children":[{"Text":"board","Children":[{"Text":"Temperatures","Children":[{"Text":"CPU","Value":"45 °C","SensorId":"{{id}}"}]}]}]}]}""";

        Assert.Equal(id, Assert.Single(LibreHardwareMonitorClient.Parse(json)).Id);
    }

    [Fact]
    public void A_hardware_path_of_many_names_is_cut_too()
    {
        var levels = string.Concat(Enumerable.Range(0, 5).Select(i => $$"""{"Text":"{{new string('h', 150)}}","Children":["""));
        var json = $$"""{"Text":"root","Children":[{"Text":"pc","Children":[{{levels}}{"Text":"Temperatures","Children":[{"Text":"CPU","Value":"45 °C","SensorId":"/x"}]}{{string.Concat(Enumerable.Repeat("]}", 5))}}]}]}""";

        Assert.Equal(LibreHardwareMonitorClient.MaxNameLength, Assert.Single(LibreHardwareMonitorClient.Parse(json)).Hardware.Length);
    }

    [Fact]
    public void The_number_of_readings_is_capped()
    {
        var sensors = string.Join(',', Enumerable.Range(0, LibreHardwareMonitorClient.MaxReadings + 100)
            .Select(i => $$"""{"Text":"s{{i}}","Value":"1 V","SensorId":"/s/{{i}}"}"""));
        var json = $$"""{"Text":"root","Children":[{"Text":"pc","Children":[{"Text":"board","Children":[{"Text":"Voltages","Children":[{{sensors}}]}]}]}]}""";

        Assert.Equal(LibreHardwareMonitorClient.MaxReadings, LibreHardwareMonitorClient.Parse(json).Count);
    }

    [Fact]
    public void Deep_nesting_is_refused()
    {
        var json = string.Concat(Enumerable.Repeat("""{"Text":"n","Children":[""", 40)) + """{"Text":"s","Value":"1 V"}""" + string.Concat(Enumerable.Repeat("]}", 40));

        Assert.ThrowsAny<System.Text.Json.JsonException>(() => LibreHardwareMonitorClient.Parse(json));
    }
}
