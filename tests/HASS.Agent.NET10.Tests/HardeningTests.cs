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
