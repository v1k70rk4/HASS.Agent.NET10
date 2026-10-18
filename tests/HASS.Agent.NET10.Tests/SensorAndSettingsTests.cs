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
    public void Normalized_settings_always_have_a_serial_number()
    {
        var settings = new CompanionSettings { SerialNumber = " " };

        settings.Normalize();

        Assert.Matches("^[0-9a-f]{32}$", settings.SerialNumber);
    }

    [Fact]
    public void Local_api_is_off_by_default_and_has_no_key_while_off()
    {
        // Also what an older settings file gets: it has a key, and no switch.
        var settings = new CompanionSettings { ApiKey = "0123456789abcdef0123456789abcdef" };

        settings.Normalize();

        Assert.False(settings.LocalApiEnabled);
        Assert.Equal(string.Empty, settings.ApiKey);
    }

    [Fact]
    public void Turning_the_local_api_on_makes_a_key_and_keeps_it()
    {
        var settings = new CompanionSettings { LocalApiEnabled = true };

        settings.Normalize();
        var key = settings.ApiKey;
        settings.Normalize();

        Assert.Matches("^[0-9a-f]{32}$", key);
        Assert.Equal(key, settings.ApiKey);
    }

    [Fact]
    public void Export_leaves_every_secret_out_and_import_makes_a_new_key()
    {
        var settings = new CompanionSettings
        {
            DeviceName = "PC-1", LocalApiEnabled = true, MqttUsername = "ha",
            LibreHardwareMonitorUser = "lhm"
        };
        settings.Normalize();
        settings.SetMqttPassword("secret-mqtt");
        settings.SetHaApiToken("secret-token");
        settings.SetLibreHardwareMonitorPassword("secret-lhm");
        var keyBefore = settings.ApiKey;

        var json = SettingsStore.ExportJson(settings);

        Assert.DoesNotContain("secret-", json);
        Assert.DoesNotContain(keyBefore, json);
        using (var document = JsonDocument.Parse(json))
        {
            foreach (var secret in SettingsStore.SecretsLeftOutOfExport)
            {
                Assert.False(document.RootElement.TryGetProperty(secret, out _), secret);
            }

            Assert.Equal("PC-1", document.RootElement.GetProperty("deviceName").GetString());
            Assert.Equal("ha", document.RootElement.GetProperty("mqttUsername").GetString());
            Assert.Equal("lhm", document.RootElement.GetProperty("libreHardwareMonitorUser").GetString());
        }

        var imported = JsonSerializer.Deserialize<CompanionSettings>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        imported.Normalize();
        Assert.True(imported.LocalApiEnabled);
        Assert.Matches("^[0-9a-f]{32}$", imported.ApiKey);
        Assert.NotEqual(keyBefore, imported.ApiKey);
        Assert.Equal(string.Empty, imported.GetLibreHardwareMonitorPassword());
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

        // The integration before 10.9.2 sends no flag at all.
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

public class SystemHealthTests
{
    [Fact]
    public void Attribute_paths_repeat_for_every_item_the_pc_has()
    {
        var paths = new[] { "gpu_usage.name", "gpu_usage.adapters[0].usage", "gpu_usage.adapters[0].engines.3d", "active_display.displays[0].name" };
        int? Count(string list) => list switch { "gpu_usage.adapters" => 2, _ => null };

        var expanded = BuiltInSensorCatalog.ExpandAttributePaths(paths, Count);

        Assert.Equal(
        [
            "gpu_usage.name",
            "gpu_usage.adapters[0].usage", "gpu_usage.adapters[1].usage",
            "gpu_usage.adapters[0].engines.3d", "gpu_usage.adapters[1].engines.3d",
            // A list never seen keeps its [0].
            "active_display.displays[0].name"
        ], expanded);
    }

    [Fact]
    public void Drives_are_joined_to_their_counters_and_zero_means_not_reported()
    {
        var disks = new[]
        {
            new PhysicalDiskRow("2", "Kingston DataTraveler 3.0", 7, 0),
            new PhysicalDiskRow("0", "WDC WD30EFRX-68EUZN0", 11, 3, HealthStatus: 1),
            new PhysicalDiskRow("1", "KINGSTON SFYRD2000G", 17, 4, HealthStatus: 0)
        };
        var counters = new[]
        {
            new ReliabilityRow("1", 43, 84, 0, 0),
            new ReliabilityRow("0", 32, 0, 35908, 0),
            new ReliabilityRow("2", 0, 0, 0, 0)
        };

        var drives = DriveHealth.Compose(disks, counters, systemDiskId: "1");

        // The system drive first, then by name.
        Assert.Equal(["KINGSTON SFYRD2000G", "Kingston DataTraveler 3.0", "WDC WD30EFRX-68EUZN0"], drives.Select(drive => drive.Name));
        var nvme = drives[0];
        Assert.True(nvme.System);
        Assert.Equal(("NVMe", "SSD", 43, 84), (nvme.Bus, nvme.Media, nvme.Temperature, nvme.TemperatureMax));
        Assert.Null(nvme.PowerOnHours);
        Assert.Equal(0, nvme.Wear);
        var sata = drives[2];
        Assert.Equal(("SATA", "HDD", 32, 35908L), (sata.Bus, sata.Media, sata.Temperature, sata.PowerOnHours));
        Assert.Null(sata.TemperatureMax);
        var usb = drives[1];
        Assert.Equal("USB", usb.Bus);
        Assert.Null(usb.Temperature);
        Assert.Null(usb.Wear);

        // Windows' verdict per drive, and the worst of them as the sensor's value.
        Assert.Equal(["healthy", "unknown", "warning"], drives.Select(drive => drive.Health));
        Assert.Equal("warning", DriveHealth.WorstHealth(drives));
        Assert.Equal("unknown", DriveHealth.WorstHealth([]));
        Assert.Equal("unhealthy", DriveHealth.WorstHealth([nvme with { Health = "unhealthy" }, sata]));
    }

    [Theory]
    [InlineData("drive_health.temperature", "°C")]
    [InlineData("drive_health.drives[1].temperature_max", "°C")]
    [InlineData("drive_health.drives[0].power_on_hours", "h")]
    [InlineData("drive_health.drives[0].name", "")]
    [InlineData("gpu_usage.adapters[0].memory_gb", "GB")]
    [InlineData("gpu_usage.memory_shared_mb", "MB")]
    [InlineData("gpu_usage.adapters[1].engines.3d", "%")]
    [InlineData("gpu_usage.npu_usage", "%")]
    [InlineData("memory_usage.used_gb", "GB")]
    [InlineData("active_display.displays[0].width", "px")]
    [InlineData("sleep_blocked.primary_blocker", "")]
    public void Attribute_sensors_made_from_the_list_get_their_unit(string path, string unit)
    {
        Assert.Equal(unit, BuiltInSensorCatalog.AttributeUnit(path));
    }

    [Fact]
    public void An_attribute_sensor_keeps_only_the_roles_that_read_its_sensor()
    {
        var settings = new CompanionSettings
        {
            CustomSensors =
            [
                // Drive health is the service's; the active window is the app's; GPU is both.
                new CustomSensorDefinition { Type = CustomSensorTypes.BuiltInAttribute, Parameter = "drive_health.drives[0].temperature", TrayApp = true, Service = true },
                new CustomSensorDefinition { Type = CustomSensorTypes.BuiltInAttribute, Parameter = "active_window", TrayApp = true, Service = true },
                new CustomSensorDefinition { Type = CustomSensorTypes.BuiltInAttribute, Parameter = "gpu_usage.adapters[1].usage", TrayApp = true, Service = true },
                new CustomSensorDefinition { Type = CustomSensorTypes.ProcessRunning, Parameter = "notepad", TrayApp = true, Service = true }
            ]
        };

        settings.Normalize();

        Assert.Equal([(false, true), (true, false), (true, true), (true, true)], settings.CustomSensors.Select(sensor => (sensor.TrayApp, sensor.Service)));
    }

    [Fact]
    public void Gigabytes_sit_next_to_megabytes()
    {
        Assert.Equal(7.5, SystemMetricsService.MbToGb(7680));
        Assert.Null(SystemMetricsService.MbToGb(null));

        var adapter = new GpuAdapterInfo("RTX 4060", 8188, 12.5, new Dictionary<string, double>(), 2048, 512);
        var json = JsonSerializer.Serialize(adapter);
        Assert.Contains("\"memory_gb\":8", json);
        Assert.Contains("\"memory_dedicated_gb\":2", json);
        Assert.Contains("\"memory_shared_gb\":0.5", json);
        Assert.Contains("\"memory_dedicated_mb\":2048", json);
    }

    [Fact]
    public void Fast_interval_floor_is_five_seconds_unless_the_danger_zone_unlocks_it()
    {
        var locked = new CompanionSettings { FastSensorIntervalSeconds = 2 };
        locked.Normalize();
        Assert.Equal(5, locked.FastSensorIntervalSeconds);

        var unlocked = new CompanionSettings { FastSensorIntervalSeconds = 2, FastSensorsUnlocked = true };
        unlocked.Normalize();
        Assert.Equal(2, unlocked.FastSensorIntervalSeconds);

        var seven = new CompanionSettings { FastSensorIntervalSeconds = 7 };
        seven.Normalize();
        Assert.Equal(7, seven.FastSensorIntervalSeconds);

        // Nonsense still falls back to the default.
        var broken = new CompanionSettings { FastSensorIntervalSeconds = 0, FastSensorsUnlocked = true };
        broken.Normalize();
        Assert.Equal(10, broken.FastSensorIntervalSeconds);
    }

    [Fact]
    public void Editor_list_keeps_the_megabyte_twins_after_the_gigabyte_ones()
    {
        var gpu = BuiltInSensorCatalog.Find("gpu_usage")!;
        var paths = BuiltInSensorCatalog.EditorAttributePaths(gpu);

        Assert.Contains("gpu_usage.memory_dedicated_gb", paths);
        Assert.Contains("gpu_usage.memory_dedicated_mb", paths);
        Assert.Contains("gpu_usage.adapters[0].memory_mb", paths);
        Assert.True(paths.ToList().IndexOf("gpu_usage.memory_dedicated_gb") < paths.ToList().IndexOf("gpu_usage.memory_dedicated_mb"));

        // Totals have no megabyte twin.
        var memory = BuiltInSensorCatalog.Find("memory_usage")!;
        Assert.DoesNotContain("memory_usage.total_mb", BuiltInSensorCatalog.EditorAttributePaths(memory));
    }
}

public class SensorProfileTests
{
    [Fact]
    public void A_built_in_sensor_runs_on_the_profile_the_user_picked_when_it_may()
    {
        var gpu = BuiltInSensorCatalog.Find("gpu_usage")!;
        var volume = BuiltInSensorCatalog.Find("volume")!;
        var bootTime = BuiltInSensorCatalog.Find("boot_time")!;

        Assert.True(BuiltInSensorCatalog.CanChangeProfile(gpu));
        Assert.False(BuiltInSensorCatalog.CanChangeProfile(volume));
        Assert.False(BuiltInSensorCatalog.CanChangeProfile(bootTime));

        Assert.Equal(SensorPollingProfile.Fast, BuiltInSensorCatalog.EffectiveProfile(gpu, new BuiltInSensorSetting { Key = "gpu_usage", PollingProfile = "fast" }));
        Assert.Equal(SensorPollingProfile.Hourly, BuiltInSensorCatalog.EffectiveProfile(gpu, new BuiltInSensorSetting { Key = "gpu_usage", PollingProfile = "HOURLY" }));
        // The catalog's own when there is no choice, nonsense, startup, or a sensor that cannot move.
        Assert.Equal(SensorPollingProfile.Normal, BuiltInSensorCatalog.EffectiveProfile(gpu, null));
        Assert.Equal(SensorPollingProfile.Normal, BuiltInSensorCatalog.EffectiveProfile(gpu, new BuiltInSensorSetting { Key = "gpu_usage", PollingProfile = "weekly" }));
        Assert.Equal(SensorPollingProfile.Normal, BuiltInSensorCatalog.EffectiveProfile(gpu, new BuiltInSensorSetting { Key = "gpu_usage", PollingProfile = "startup" }));
        Assert.Equal(SensorPollingProfile.Fast, BuiltInSensorCatalog.EffectiveProfile(volume, new BuiltInSensorSetting { Key = "volume", PollingProfile = "hourly" }));
        Assert.Equal(SensorPollingProfile.Startup, BuiltInSensorCatalog.EffectiveProfile(bootTime, new BuiltInSensorSetting { Key = "boot_time", PollingProfile = "fast" }));
    }

    [Fact]
    public void Settings_keep_a_profile_only_when_it_is_a_real_change()
    {
        var settings = new CompanionSettings
        {
            BuiltInSensors =
            [
                new BuiltInSensorSetting { Key = "gpu_usage", Service = true, PollingProfile = "fast" },
                new BuiltInSensorSetting { Key = "cpu_usage", Service = true, PollingProfile = "fast" },
                new BuiltInSensorSetting { Key = "volume", TrayApp = true, PollingProfile = "hourly" },
                new BuiltInSensorSetting { Key = "boot_time", Service = true, PollingProfile = "normal" },
                new BuiltInSensorSetting { Key = "memory_usage", Service = true, PollingProfile = "sometimes" }
            ]
        };

        settings.Normalize();

        string? Profile(string key) => settings.BuiltInSensors.Single(sensor => sensor.Key == key).PollingProfile;
        Assert.Equal("fast", Profile("gpu_usage"));
        Assert.Null(Profile("cpu_usage"));
        Assert.Null(Profile("volume"));
        Assert.Null(Profile("boot_time"));
        Assert.Null(Profile("memory_usage"));
        Assert.Equal(SensorPollingProfile.Fast, BuiltInSensorCatalog.EffectiveProfile("gpu_usage", settings.BuiltInSensors));
        Assert.Equal(SensorPollingProfile.Fast, BuiltInSensorCatalog.EffectiveProfile("cpu_usage", settings.BuiltInSensors));
    }
}

public class CustomSensorUnitTests
{
    [Theory]
    [InlineData("°C", "temperature")]
    [InlineData(" °F ", "temperature")]
    [InlineData("GB", "data_size")]
    [InlineData("GiB", "data_size")]
    [InlineData("MB/s", "data_rate")]
    [InlineData("h", "duration")]
    [InlineData("W", "power")]
    [InlineData("MHz", "frequency")]
    [InlineData("kWh", null)]
    [InlineData("%", null)]
    [InlineData("RPM", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_custom_sensor_gets_a_device_class_from_its_unit(string? unit, string? deviceClass)
    {
        Assert.Equal(deviceClass, CustomSensorUnits.DeviceClassFor(unit));
    }
}

public class PublishPaceTests
{
    [Fact]
    public async Task A_fast_custom_sensor_refreshes_its_source_while_the_hourly_built_in_keeps_its_pace()
    {
        var log = new HASS.Agent.Companion.Logging.FileLog(Path.Combine(Path.GetTempPath(), "hass-agent-tests.log"));
        using var service = new SystemMetricsService(log, null);
        // Only the memory usage, on hourly: nothing else may touch the memory read.
        var builtIn = new List<BuiltInSensorSetting>
        {
            new() { Key = "memory_usage", TrayApp = true, Service = true, PollingProfile = "hourly" }
        };
        var custom = new List<CustomSensorDefinition>
        {
            new() { Id = "mem", Type = CustomSensorTypes.BuiltInAttribute, Parameter = "memory_usage.available_gb", PollingProfile = "fast", TrayApp = true, Service = true }
        };
        var fast = new HashSet<SensorPollingProfile> { SensorPollingProfile.Fast };

        // The first read takes everything.
        var first = service.Read(custom, serviceRole: false, dueProfiles: SensorPollingProfiles.All, builtInSensors: builtIn);
        Assert.Equal(1, service.MemoryReads);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        // A fast cycle: the custom sensor is fast, so the memory is read again for it...
        var second = service.Read(custom, serviceRole: false, dueProfiles: fast, builtInSensors: builtIn);
        Assert.Equal(2, service.MemoryReads);
        Assert.NotNull(second.CustomSensors.Single(sensor => sensor.Id == "mem").Value);
        // ...while Home Assistant is shown the hourly sensor's earlier value and attributes.
        Assert.Equal(first.MemoryUsage, second.MemoryUsage);
        Assert.Same(first.Attributes["memory_usage"], second.Attributes["memory_usage"]);

        // Without the custom sensor a fast cycle leaves the hourly memory read alone.
        service.Read([], serviceRole: false, dueProfiles: fast, builtInSensors: builtIn);
        Assert.Equal(2, service.MemoryReads);
    }
}

public class PushReadTests
{
    [Fact]
    public void A_push_read_refreshes_the_push_driven_sensors_only()
    {
        var log = new HASS.Agent.Companion.Logging.FileLog(Path.Combine(Path.GetTempPath(), "hass-agent-tests.log"));
        using var service = new SystemMetricsService(log, null);
        var builtIn = new List<BuiltInSensorSetting>
        {
            new() { Key = "memory_usage", TrayApp = true, Service = true },
            new() { Key = "volume", TrayApp = true, Service = false }
        };
        var pushProfiles = new HashSet<SensorPollingProfile> { SensorPollingProfile.Fast, SensorPollingProfile.Normal };
        // A fast custom sensor on a clock-driven source: a push event must not run it either.
        var custom = new List<CustomSensorDefinition>
        {
            new() { Id = "mem", Type = CustomSensorTypes.BuiltInAttribute, Parameter = "memory_usage.used_gb", PollingProfile = "fast", TrayApp = true, Service = true }
        };

        var first = service.Read(custom, serviceRole: false, dueProfiles: SensorPollingProfiles.All, builtInSensors: builtIn);
        Assert.Equal(1, service.MemoryReads);

        // A push event: the fast memory usage is not push-driven, so it is left alone, and
        // the custom sensor keeps its earlier state...
        var pushed = service.Read(custom, serviceRole: false, dueProfiles: pushProfiles, builtInSensors: builtIn, pushOnly: true);
        Assert.Equal(1, service.MemoryReads);
        Assert.Same(first.CustomSensors.Single(), pushed.CustomSensors.Single());

        // ...and a timed fast cycle reads both as before.
        var timed = service.Read(custom, serviceRole: false, dueProfiles: pushProfiles, builtInSensors: builtIn);
        Assert.Equal(2, service.MemoryReads);
        Assert.NotSame(first.CustomSensors.Single(), timed.CustomSensors.Single());
    }
}
