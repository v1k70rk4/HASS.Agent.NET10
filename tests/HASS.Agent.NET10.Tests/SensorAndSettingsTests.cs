using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Media;
using System.Text.Json;
using HASS.Agent.Companion.SystemStatus;

namespace HASS.Agent.Companion.Tests;

public class LibreHardwareMonitorTests
{
    [Theory]
    [InlineData("45,0 °C", 45.0, "°C")]       // a comma as the decimal separator
    [InlineData("3.3 V", 3.3, "V")]
    [InlineData("1200 RPM", 1200.0, "RPM")]
    [InlineData("-5.5 °C", -5.5, "°C")]
    public void Value_and_unit_are_split(string text, double number, string unit)
    {
        var (value, parsedUnit) = LibreHardwareMonitorClient.SplitValue(text);

        Assert.Equal(number, value!.Value, precision: 3);
        Assert.Equal(unit, parsedUnit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("n/a")]
    public void Not_a_number(string text)
    {
        Assert.Null(LibreHardwareMonitorClient.SplitValue(text).Number);
    }

    [Theory]
    [InlineData("CPU / Core #1 = 45,0 °C | /intelcpu/0/temperature/0", "/intelcpu/0/temperature/0")]
    [InlineData("/gpu-nvidia/0/load/0", "/gpu-nvidia/0/load/0")]
    [InlineData("  /lpc/nct6798d/fan/1  ", "/lpc/nct6798d/fan/1")]
    public void Sensor_id_is_taken_from_the_editor_line(string parameter, string id)
    {
        Assert.Equal(id, LibreHardwareMonitorClient.ParseSensorId(parameter));
    }

    [Theory]
    [InlineData("", "http://localhost:8085/data.json")]
    [InlineData("http://pc:8085", "http://pc:8085/data.json")]
    [InlineData("http://pc:8085/", "http://pc:8085/data.json")]
    [InlineData("http://pc:8085/data.json", "http://pc:8085/data.json")]
    public void Data_address(string baseUrl, string expected)
    {
        Assert.Equal(expected, LibreHardwareMonitorClient.BuildDataUrl(baseUrl));
    }

    [Fact]
    public void Tree_of_the_remote_web_server_becomes_readings()
    {
        const string json = """
            {"Text":"Sensor","Children":[
              {"Text":"MY-PC","Children":[
                {"Text":"Intel Core i7","Children":[
                  {"Text":"Temperatures","Children":[
                    {"Text":"Core #1","Value":"45,0 °C","SensorId":"/intelcpu/0/temperature/0","Children":[]},
                    {"Text":"Core #2","Value":"47,5 °C","SensorId":"/intelcpu/0/temperature/1","Children":[]}]},
                  {"Text":"Load","Children":[
                    {"Text":"CPU Total","Value":"12,3 %","SensorId":"/intelcpu/0/load/0","Children":[]}]}]}]}]}
            """;

        var readings = LibreHardwareMonitorClient.Parse(json);

        Assert.Equal(3, readings.Count);
        var core = readings[0];
        Assert.Equal("/intelcpu/0/temperature/0", core.Id);
        Assert.Equal("Intel Core i7", core.Hardware);
        Assert.Equal("Core #1", core.Name);
        Assert.Equal(45.0, core.Value);
        Assert.Equal("°C", core.Unit);
        Assert.Equal("%", readings[2].Unit);
    }
}

public class GpuAdapterTests
{
    [Theory]
    [InlineData(0, 0x13893u, "0x00000000_0x00013893")]
    [InlineData(-1, 1u, "0xFFFFFFFF_0x00000001")]
    public void Adapter_luid_is_written_like_the_gpu_counters(int high, uint low, string expected)
    {
        // The counters name an adapter luid_0x{high}_0x{low}_phys_N.
        Assert.Equal(expected, GpuAdapterList.LuidKey(high, low));
    }

    private const string IntelLuid = "0x00000000_0x0000A001";
    private const string NvidiaLuid = "0x00000000_0x0000B002";

    // A laptop with an integrated GPU and a graphics card, DXGI listing them the other way round.
    private static IReadOnlyList<GpuAdapterIdentity> TwoGpus() => GpuAdapterList.Match(
        [("Intel(R) UHD Graphics", 128), ("NVIDIA GeForce RTX 4060 Laptop GPU", 8188)],
        [("NVIDIA GeForce RTX 4060 Laptop GPU", NvidiaLuid, 8000L), ("Intel(R) UHD Graphics", IntelLuid, null), ("Microsoft Basic Render Driver", "0x00000000_0x0000C003", null)],
        name => name);

    [Fact]
    public void Adapters_keep_the_registry_order_and_get_their_own_luid()
    {
        var adapters = TwoGpus();
        Assert.Equal(["Intel(R) UHD Graphics", "NVIDIA GeForce RTX 4060 Laptop GPU"], adapters.Select(adapter => adapter.Name));
        Assert.Equal([IntelLuid, NvidiaLuid], adapters.Select(adapter => adapter.Luid));
        Assert.Equal(8188, adapters[1].MemoryMb);  // the registry's size wins over DXGI's
    }

    [Fact]
    public void Two_cards_of_one_model_get_no_luid()
    {
        // Only the name links the two lists, so which card is which can't be told.
        var adapters = GpuAdapterList.Match(
            [("RTX 4090", 24564), ("RTX 4090", 24564)],
            [("RTX 4090", "0x00000000_0x00000002", 24000L), ("RTX 4090", "0x00000000_0x00000001", 24000L)],
            name => name);
        Assert.All(adapters, adapter => Assert.Null(adapter.Luid));
        Assert.Equal(24564, adapters[0].MemoryMb);  // the registry's size stays
    }

    [Fact]
    public void Dxgi_size_is_used_when_the_registry_has_none()
    {
        var adapters = GpuAdapterList.Match(
            [("Radeon RX 7800 XT", null)],
            [("Radeon RX 7800 XT", "0x00000000_0x00000003", 16368L)],
            name => name);
        Assert.Equal(16368, adapters[0].MemoryMb);
        Assert.Equal("0x00000000_0x00000003", adapters[0].Luid);
    }

    [Fact]
    public void Each_adapter_gets_its_own_load_and_memory()
    {
        var engines = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase)
        {
            [NvidiaLuid] = new() { ["3d"] = 87.44, ["videodecode"] = 4 },
            // the Intel GPU has no running process: no counter
        };
        var memory = new Dictionary<string, (long?, long?)>(StringComparer.OrdinalIgnoreCase)
        {
            [IntelLuid] = (0, 512),
            [NvidiaLuid] = (6100, 200),
        };

        var adapters = SystemMetricsService.BuildGpuAdapters(TwoGpus(), engines, memory);

        Assert.Equal(0, adapters[0].Usage);
        Assert.Empty(adapters[0].Engines!);
        Assert.Equal(512, adapters[0].MemorySharedMb);
        Assert.Equal(87.4, adapters[1].Usage);
        Assert.Equal(87.4, adapters[1].Engines!["3d"]);
        Assert.Equal(6100, adapters[1].MemoryDedicatedMb);
    }

    [Fact]
    public void An_adapter_without_a_luid_has_no_values()
    {
        var adapters = SystemMetricsService.BuildGpuAdapters(
            [new GpuAdapterIdentity("Virtual Display", null, null)],
            new Dictionary<string, Dictionary<string, double>>(),
            new Dictionary<string, (long?, long?)>());

        Assert.Null(adapters[0].Usage);
        Assert.Null(adapters[0].Engines);
    }
}

public class LanIpTests
{
    // Windows' own order on a laptop with VirtualBox: the host-only adapter comes first.
    private static readonly (NetworkAddressInfo, int, bool)[] Adapters =
    [
        (new NetworkAddressInfo("Ethernet", "VirtualBox Host-Only Ethernet Adapter", "192.168.56.1"), 7, false),
        (new NetworkAddressInfo("vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", "172.25.16.1"), 41, false),
        (new NetworkAddressInfo("Wi-Fi", "Intel(R) Wi-Fi 7 BE201", "192.168.1.129"), 16, true),
    ];

    [Fact]
    public void The_routed_adapter_comes_first()
    {
        var ordered = SystemMetricsService.OrderNetworkAddresses(Adapters, routedInterfaceIndex: 16);
        Assert.Equal(["192.168.1.129", "192.168.56.1", "172.25.16.1"], ordered.Select(address => address.Address));
    }

    [Fact]
    public void Without_a_route_out_an_adapter_with_a_gateway_comes_first()
    {
        var ordered = SystemMetricsService.OrderNetworkAddresses(Adapters, routedInterfaceIndex: null);
        Assert.Equal("192.168.1.129", ordered[0].Address);
    }
}

public class UserSessionTests
{
    [Fact]
    public void The_console_user_comes_first()
    {
        // Signed in at the screen (session 1) and over Remote Desktop (session 3).
        Assert.Equal(1, SystemMetricsService.ChooseUserSession([(3, "active"), (1, "active")], consoleId: 1));
    }

    [Fact]
    public void Over_remote_desktop_only_the_active_session_is_taken()
    {
        // The console (1) shows the sign-in screen with nobody in it, so it is not in the list.
        Assert.Equal(1, SystemMetricsService.ChooseUserSession([(2, "disconnected"), (4, "active")], consoleId: 1));
    }

    [Fact]
    public void A_disconnected_session_is_the_last_resort()
    {
        Assert.Equal(0, SystemMetricsService.ChooseUserSession([(2, "disconnected")], consoleId: 1));
        Assert.Equal(-1, SystemMetricsService.ChooseUserSession([], consoleId: 1));
    }
}

public class SessionLockedTests
{
    [Theory]
    [InlineData(0, true)]    // WTS_SESSIONSTATE_LOCK
    [InlineData(1, false)]   // WTS_SESSIONSTATE_UNLOCK
    [InlineData(-1, null)]   // WTS_SESSIONSTATE_UNKNOWN
    public void Session_flags_say_whether_the_session_is_locked(int flags, bool? locked)
    {
        Assert.Equal(locked, SystemMetricsService.SessionLockedFromFlags(flags));
    }
}

public class NotificationRegistrationTests
{
    private const string Exe = @"C:\Program Files\HASS.Agent .NET10\HASS.Agent.NET10.exe";

    [Theory]
    [InlineData("\"C:\\Program Files\\HASS.Agent .NET10\\HASS.Agent.NET10.exe\" -ToastActivated", true)]
    [InlineData("\"c:\\program files\\hass.agent .net10\\hass.agent.net10.exe\"", true)]
    [InlineData("C:\\Program Files\\HASS.Agent .NET10\\HASS.Agent.NET10.exe", true)]
    [InlineData("\"D:\\Copy\\C:\\Program Files\\HASS.Agent .NET10\\HASS.Agent.NET10.exe\" -ToastActivated", false)]
    [InlineData("\"C:\\Program Files\\HASS.Agent .NET10\\HASS.Agent.NET10.exe.old\" -ToastActivated", false)]
    [InlineData("\"C:\\Program Files\\HASS.Agent .NET10\\HASS.Agent.NET10.exe", false)]
    public void Only_a_command_that_starts_this_exe_matches(string command, bool matches)
    {
        Assert.Equal(matches, HASS.Agent.Companion.Tray.ToastPresenter.CommandStarts(command, Exe));
    }
}

public class SettingsTests
{
    [Fact]
    public void Custom_notification_name_counts_only_when_ticked_and_not_empty()
    {
        Assert.Null(new CompanionSettings { CustomNotificationName = "Garage PC" }.EffectiveNotificationName);
        Assert.Null(new CompanionSettings { CustomNotificationNameEnabled = true, CustomNotificationName = "  " }.EffectiveNotificationName);
        Assert.Equal("Garage PC", new CompanionSettings { CustomNotificationNameEnabled = true, CustomNotificationName = " Garage PC " }.EffectiveNotificationName);

        var tooLong = new CompanionSettings { CustomNotificationNameEnabled = true, CustomNotificationName = new string('x', 100) };
        tooLong.Normalize();
        Assert.Equal(CompanionSettings.MaxNotificationNameLength, tooLong.CustomNotificationName.Length);
    }

    [Fact]
    public void Custom_sensor_ids_are_clean_and_unique()
    {
        var used = new HashSet<string>();

        Assert.Equal("cpu_temp", CompanionSettings.NormalizeCustomSensorId(" CPU_Temp! ", used));
        var repeated = CompanionSettings.NormalizeCustomSensorId("cpu_temp", used);
        Assert.NotEqual("cpu_temp", repeated);
        Assert.Matches("^[0-9a-f]{32}$", repeated);
        Assert.Matches("^[0-9a-f]{32}$", CompanionSettings.NormalizeCustomSensorId("!!!", used));
    }

    [Fact]
    public void Normalized_settings_always_have_an_api_key_and_a_serial_number()
    {
        var settings = new CompanionSettings { ApiKey = "", SerialNumber = " " };

        settings.Normalize();

        Assert.Matches("^[0-9a-f]{32}$", settings.ApiKey);
        Assert.Matches("^[0-9a-f]{32}$", settings.SerialNumber);
    }

    [Theory]
    [InlineData(0, false, 1883)]
    [InlineData(70000, true, 8883)]
    [InlineData(1884, false, 1884)]
    public void Mqtt_port_falls_back_to_the_default_of_the_transport(int port, bool tls, int expected)
    {
        var settings = new CompanionSettings { MqttPort = port, MqttUseTls = tls };

        settings.Normalize();

        Assert.Equal(expected, settings.MqttPort);
    }

    [Fact]
    public void Local_api_never_binds_to_loopback_only()
    {
        var settings = new CompanionSettings { BindHost = "127.0.0.1" };

        settings.Normalize();

        Assert.Equal("0.0.0.0", settings.BindHost);
    }
}

public class MediaPlayerTests
{
    [Fact]
    public void Playmedia_announce_flag_is_read_and_defaults_to_off()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var announced = JsonSerializer.Deserialize<MediaCommand>("""{"command":"playmedia","data":"http://ha.local:8123/tts.mp3","announce":true}""", options);
        Assert.NotNull(announced);
        Assert.True(announced.Announce);
        Assert.Equal("http://ha.local:8123/tts.mp3", announced.Data?.ToString());

        // The integration before 10.10.0 sends no flag at all.
        var plain = JsonSerializer.Deserialize<MediaCommand>("""{"command":"playmedia","data":"http://ha.local:8123/song.mp3"}""", options);
        Assert.NotNull(plain);
        Assert.False(plain.Announce);
    }

    [Theory]
    [InlineData("http://ha.local:8123/api/tts_proxy/x.mp3", true)]
    [InlineData("https://example.org/a.mp3", true)]
    [InlineData("file:///C:/Windows/Media/chimes.wav", false)]
    [InlineData("/local/a.mp3", false)]
    [InlineData("  ", false)]
    [InlineData(null, false)]
    public void Only_http_addresses_are_played(string? address, bool played)
    {
        Assert.Equal(played, MediaSessionService.TryParseMediaUri(address, out var uri));
        Assert.Equal(played, uri is not null);
    }

    [Fact]
    public void Announcement_settings_have_sensible_defaults_and_a_volume_within_range()
    {
        var settings = new CompanionSettings();
        Assert.True(settings.MediaPlayerCoverArtEnabled);
        Assert.True(settings.AnnouncementVolumeEnabled);
        Assert.True(settings.AnnouncementPausesMedia);
        Assert.Equal(CompanionSettings.DefaultAnnouncementVolume, settings.AnnouncementVolume);

        foreach (var outOfRange in new[] { 0, -5, 101, 1000 })
        {
            var broken = new CompanionSettings { AnnouncementVolume = outOfRange };
            broken.Normalize();
            Assert.Equal(CompanionSettings.DefaultAnnouncementVolume, broken.AnnouncementVolume);
        }

        var kept = new CompanionSettings { AnnouncementVolume = 35 };
        kept.Normalize();
        Assert.Equal(35, kept.AnnouncementVolume);
    }
}
