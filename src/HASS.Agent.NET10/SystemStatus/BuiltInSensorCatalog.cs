using System.Text.Json.Serialization;

namespace HASS.Agent.Companion.SystemStatus;

internal sealed record BuiltInSensorDefinition(
    string Key,
    bool SupportsService,
    bool SupportsTrayApp,
    SensorPollingProfile PollingProfile,
    IReadOnlyList<string>? AttributePaths = null,
    string? DeviceClass = null,
    IReadOnlyList<string>? Options = null,
    bool PushDriven = false,
    // Off for new installs and for sensors added in a later version; a setting the user
    // already has is never touched. For sensors that are useful, but not to everyone.
    bool EnabledByDefault = true)
{
    public bool HasMultipleValues => AttributePaths is { Count: > 0 };

    public string? DefaultAttributePath => AttributePaths?.FirstOrDefault();
}

internal sealed class BuiltInSensorSetting
{
    public string Key { get; set; } = string.Empty;

    public bool Service { get; set; }

    public bool TrayApp { get; set; }

    /// <summary>
    /// The user's own polling profile for this sensor (fast, normal, hourly), null for the
    /// catalog's. Never set for a push-driven or a startup sensor.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PollingProfile { get; set; }
}

internal sealed record BuiltInSensorDescriptor(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("polling_profile")] string PollingProfile,
    [property: JsonPropertyName("multiple_values")] bool MultipleValues,
    [property: JsonPropertyName("default_attribute_path")] string? DefaultAttributePath,
    [property: JsonPropertyName("attribute_paths")] IReadOnlyList<string>? AttributePaths,
    [property: JsonPropertyName("device_class")] string? DeviceClass,
    [property: JsonPropertyName("options")] IReadOnlyList<string>? Options);

internal static class BuiltInSensorCatalog
{
    public static IReadOnlyList<BuiltInSensorDefinition> Sensors { get; } =
    [
        new("cpu_usage", SupportsService: true, SupportsTrayApp: true, SensorPollingProfile.Fast),
        new("memory_usage", SupportsService: true, SupportsTrayApp: true, SensorPollingProfile.Fast,
        [
            "memory_usage.total_gb",
            "memory_usage.used_gb",
            "memory_usage.available_gb"
        ]),
        new("memory_available_mb", SupportsService: true, SupportsTrayApp: true, SensorPollingProfile.Fast),
        new("system_drive_free_percent", SupportsService: true, SupportsTrayApp: true, SensorPollingProfile.Normal),
        // The drive's totals hang on both system drive sensors; the list is on one of them
        // so that the Attributes button does not make the same sensors twice.
        new("system_drive_free_gb", SupportsService: true, SupportsTrayApp: true, SensorPollingProfile.Normal,
        [
            "system_drive_free_gb.drive",
            "system_drive_free_gb.total_gb",
            "system_drive_free_gb.used_gb"
        ]),
        // Windows' own drive health data (what Get-StorageReliabilityCounter shows) needs
        // administrator rights, which the service has. NVMe drives report it; a SATA drive
        // when its controller passes it on; USB enclosures not at all.
        new("drive_health", SupportsService: true, SupportsTrayApp: false, SensorPollingProfile.Normal,
        [
            "drive_health.temperature",
            "drive_health.drives[0].name",
            "drive_health.drives[0].health",
            "drive_health.drives[0].temperature",
            "drive_health.drives[0].temperature_max",
            "drive_health.drives[0].power_on_hours"
        ], DeviceClass: "enum", Options: ["healthy", "warning", "unhealthy", "unknown"]),
        new("uptime_seconds", SupportsService: true, SupportsTrayApp: true, SensorPollingProfile.Normal),
        new("battery_level", SupportsService: true, SupportsTrayApp: true, SensorPollingProfile.Normal),
        new("power_status", SupportsService: true, SupportsTrayApp: true, SensorPollingProfile.Normal,
            DeviceClass: "enum", Options: ["no_battery", "charging", "plugged_in", "battery", "unknown"], PushDriven: true),
        new("network_address", SupportsService: true, SupportsTrayApp: true, SensorPollingProfile.Normal,
        [
            "network_address.addresses[0].adapter",
            "network_address.addresses[0].description",
            "network_address.addresses[0].address"
        ]),
        new("session_state", SupportsService: true, SupportsTrayApp: true, SensorPollingProfile.Normal,
            DeviceClass: "enum", Options: ["active", "connected", "connect_query", "shadow", "disconnected", "idle", "listen", "reset", "down", "init", "none", "unknown"], PushDriven: true),
        new("logged_in_user", SupportsService: true, SupportsTrayApp: true, SensorPollingProfile.Normal),
        new("pending_reboot", SupportsService: true, SupportsTrayApp: true, SensorPollingProfile.Normal),
        new("gpu_usage", SupportsService: true, SupportsTrayApp: true, SensorPollingProfile.Normal,
        [
            // The _mb attributes are still published for the sensors people already built;
            // the list offers the GB ones.
            "gpu_usage.name",
            "gpu_usage.engines.3d",
            "gpu_usage.engines.videodecode",
            "gpu_usage.engines.videoencode",
            "gpu_usage.npu_usage",
            "gpu_usage.memory_dedicated_gb",
            "gpu_usage.memory_shared_gb",
            "gpu_usage.adapters[0].name",
            "gpu_usage.adapters[0].memory_gb",
            "gpu_usage.adapters[0].usage",
            "gpu_usage.adapters[0].engines.3d",
            "gpu_usage.adapters[0].memory_dedicated_gb"
        ], EnabledByDefault: false),
        new("sleep_blocked", SupportsService: true, SupportsTrayApp: false, SensorPollingProfile.Fast,
        [
            "sleep_blocked.system_required",
            "sleep_blocked.display_required",
            "sleep_blocked.away_mode_required",
            "sleep_blocked.primary_blocker",
            "sleep_blocked.blockers[0].category",
            "sleep_blocked.blockers[0].type",
            "sleep_blocked.blockers[0].name",
            "sleep_blocked.blockers[0].reason",
            "sleep_blocked.blockers[0].blocking"
        ], EnabledByDefault: false),
        new("boot_time", SupportsService: true, SupportsTrayApp: true, SensorPollingProfile.Startup),
        new("battery_time_remaining", SupportsService: true, SupportsTrayApp: true, SensorPollingProfile.Normal),
        new("vpn_connected", SupportsService: true, SupportsTrayApp: true, SensorPollingProfile.Normal, EnabledByDefault: false),
        new("wifi_ssid", SupportsService: true, SupportsTrayApp: true, SensorPollingProfile.Normal),
        new("wifi_signal", SupportsService: true, SupportsTrayApp: true, SensorPollingProfile.Normal),
        new("logged_in_users", SupportsService: true, SupportsTrayApp: true, SensorPollingProfile.Normal),
        new("rdp_sessions", SupportsService: true, SupportsTrayApp: true, SensorPollingProfile.Normal, EnabledByDefault: false),
        new("bluetooth_enabled", SupportsService: true, SupportsTrayApp: true, SensorPollingProfile.Normal),
        new("windows_update_pending", SupportsService: true, SupportsTrayApp: true, SensorPollingProfile.Hourly),
        new("event_log_errors_recent", SupportsService: true, SupportsTrayApp: true, SensorPollingProfile.Hourly,
        [
            "event_log_errors_recent.window_minutes",
            "event_log_errors_recent.events[0].log",
            "event_log_errors_recent.events[0].provider",
            "event_log_errors_recent.events[0].event_id",
            "event_log_errors_recent.events[0].level",
            "event_log_errors_recent.events[0].created_at"
        ], EnabledByDefault: false),
        new("last_shutdown_reason", SupportsService: true, SupportsTrayApp: true, SensorPollingProfile.Startup,
        [
            "last_shutdown_reason.reason",
            "last_shutdown_reason.event_id",
            "last_shutdown_reason.created_at",
            "last_shutdown_reason.message"
        ], EnabledByDefault: false),
        new("last_wake_reason", SupportsService: true, SupportsTrayApp: true, SensorPollingProfile.Startup,
        [
            "last_wake_reason.source",
            "last_wake_reason.kind",
            "last_wake_reason.created_at",
            "last_wake_reason.duration_seconds",
            "last_wake_reason.sleep_entered",
            "last_wake_reason.detail"
        ], PushDriven: true, EnabledByDefault: false),
        new("active_window", SupportsService: false, SupportsTrayApp: true, SensorPollingProfile.Fast),
        new("active_process", SupportsService: false, SupportsTrayApp: true, SensorPollingProfile.Fast),
        new("foreground_app_title", SupportsService: false, SupportsTrayApp: true, SensorPollingProfile.Fast),
        new("volume", SupportsService: false, SupportsTrayApp: true, SensorPollingProfile.Fast, PushDriven: true),
        new("muted", SupportsService: false, SupportsTrayApp: true, SensorPollingProfile.Fast, PushDriven: true),
        new("monitor_power_state", SupportsService: false, SupportsTrayApp: true, SensorPollingProfile.Normal,
            DeviceClass: "enum", Options: ["off", "on", "dimmed", "unknown"], PushDriven: true),
        new("active_display", SupportsService: false, SupportsTrayApp: true, SensorPollingProfile.Normal,
        [
            "active_display.displays[0].name",
            "active_display.displays[0].primary",
            "active_display.displays[0].width",
            "active_display.displays[0].height",
            "active_display.displays[0].x",
            "active_display.displays[0].y"
        ]),
        new("idle_time_seconds", SupportsService: false, SupportsTrayApp: true, SensorPollingProfile.Fast),
        new("session_locked", SupportsService: false, SupportsTrayApp: true, SensorPollingProfile.Fast, PushDriven: true),
        new("user_present", SupportsService: false, SupportsTrayApp: true, SensorPollingProfile.Fast, PushDriven: true),
        new("clipboard_text_available", SupportsService: false, SupportsTrayApp: true, SensorPollingProfile.Fast, EnabledByDefault: false),
        new("camera_in_use", SupportsService: false, SupportsTrayApp: true, SensorPollingProfile.Fast,
        [
            "camera_in_use.apps[0]"
        ], EnabledByDefault: false),
        new("microphone_in_use", SupportsService: false, SupportsTrayApp: true, SensorPollingProfile.Fast,
        [
            "microphone_in_use.apps[0]"
        ], EnabledByDefault: false),
        new("audio_output_device", SupportsService: false, SupportsTrayApp: true, SensorPollingProfile.Normal, PushDriven: true),
        // The apps in the volume mixer, with their volume: what the hass_agent.set_app_volume
        // service (integration 10.9.0+) works on. The state is how many of them play right now.
        new("audio_sessions", SupportsService: false, SupportsTrayApp: true, SensorPollingProfile.Normal,
        [
            "audio_sessions.apps[0].app",
            "audio_sessions.apps[0].volume",
            "audio_sessions.apps[0].muted",
            "audio_sessions.apps[0].active"
        ], EnabledByDefault: false),
        // With these two on, the integration (10.9.0+) also offers a select that changes the default device.
        new("audio_input_device", SupportsService: false, SupportsTrayApp: true, SensorPollingProfile.Normal, PushDriven: true, EnabledByDefault: false),
        // Also what the integration (10.9.0+) builds its "Display" light from: with this on,
        // Home Assistant can set the brightness too.
        new("display_brightness", SupportsService: false, SupportsTrayApp: true, SensorPollingProfile.Normal, PushDriven: true, EnabledByDefault: false),
        new("microphone_muted", SupportsService: false, SupportsTrayApp: true, SensorPollingProfile.Normal, PushDriven: true)
    ];

    public static IReadOnlySet<string> AllKeys { get; } = Sensors
        .Select(sensor => sensor.Key)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static BuiltInSensorDefinition? Find(string key)
    {
        return Sensors.FirstOrDefault(sensor => string.Equals(sensor.Key, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The sensor's attribute paths as this PC has them: a path with [0] is repeated for
    /// every item the list had the last time the sensor was read (a second GPU, a second
    /// display), so that nobody has to know to change the index by hand.
    /// </summary>
    public static IReadOnlyList<string> SuggestedAttributePaths(BuiltInSensorDefinition definition) =>
        ExpandAttributePaths(definition.AttributePaths ?? [], ListCount);

    // The counts come from the last read in this process. The settings window lives in the
    // tray app, which never reads the drive health (that takes administrator rights), so the
    // number of drives is counted on its own: that much Windows tells anyone.
    private static int? ListCount(string listPath) =>
        SystemMetricsService.ListCount(listPath)
        ?? (string.Equals(listPath, "drive_health.drives", StringComparison.OrdinalIgnoreCase) ? DriveHealth.DiskCount() : null);

    /// <summary>
    /// The list the editor offers to type from: the suggested paths, then the _mb twin of every
    /// _gb one, since those are still published. The Attributes button does not use these, or it
    /// would make two sensors of every memory value.
    /// </summary>
    public static IReadOnlyList<string> EditorAttributePaths(BuiltInSensorDefinition definition)
    {
        var suggested = SuggestedAttributePaths(definition);
        return suggested
            .Concat(suggested
                .Where(path => path.EndsWith("_gb", StringComparison.Ordinal) && !path.Contains("total_gb") && !path.Contains("used_gb") && !path.Contains("free_gb") && !path.Contains("available_gb"))
                .Select(path => path[..^3] + "_mb"))
            .ToList();
    }

    /// <summary>
    /// Whether the user may move the sensor to another timed profile. A push-driven sensor
    /// reports on change and its profile is only a safety net; a startup sensor is read once.
    /// </summary>
    public static bool CanChangeProfile(BuiltInSensorDefinition definition) =>
        !definition.PushDriven && definition.PollingProfile != SensorPollingProfile.Startup;

    /// <summary>The profile the sensor runs on: the user's choice when there is a valid one, else the catalog's.</summary>
    public static SensorPollingProfile EffectiveProfile(BuiltInSensorDefinition definition, BuiltInSensorSetting? setting)
    {
        if (setting?.PollingProfile is { } chosen &&
            CanChangeProfile(definition) &&
            Enum.TryParse<SensorPollingProfile>(chosen, ignoreCase: true, out var profile) &&
            profile != SensorPollingProfile.Startup)
        {
            return profile;
        }

        return definition.PollingProfile;
    }

    /// <summary>By key, from the settings; the catalog's profile for a key the settings do not have.</summary>
    public static SensorPollingProfile EffectiveProfile(string key, IReadOnlyList<BuiltInSensorSetting>? settings)
    {
        var definition = Find(key);
        if (definition is null)
        {
            return SensorPollingProfile.Normal;
        }

        var setting = settings?.FirstOrDefault(sensor => string.Equals(sensor.Key, key, StringComparison.OrdinalIgnoreCase));
        return EffectiveProfile(definition, setting);
    }

    /// <summary>The built-in sensor an attribute path (gpu_usage.adapters[0].name) belongs to, if any.</summary>
    public static BuiltInSensorDefinition? ForAttributePath(string? attributePath)
    {
        if (string.IsNullOrWhiteSpace(attributePath))
        {
            return null;
        }

        var dot = attributePath.IndexOf('.');
        return Find(dot > 0 ? attributePath[..dot] : attributePath);
    }

    /// <summary>
    /// The unit an attribute sensor gets when it is made from the list, by the attribute's
    /// name: temperatures in °C, hours, percentages, memory in GB or MB. Empty when the
    /// attribute is not a measurement (a name, a flag).
    /// </summary>
    public static string AttributeUnit(string attributePath)
    {
        var lastDot = attributePath.LastIndexOf('.');
        var name = lastDot >= 0 ? attributePath[(lastDot + 1)..] : attributePath;
        var parent = lastDot >= 0 ? attributePath[..lastDot] : string.Empty;
        if (name.EndsWith("_gb", StringComparison.Ordinal)) return "GB";
        if (name.EndsWith("_mb", StringComparison.Ordinal)) return "MB";
        if (name.StartsWith("temperature", StringComparison.Ordinal)) return "°C";
        if (name.EndsWith("_hours", StringComparison.Ordinal)) return "h";
        if (name.EndsWith("_seconds", StringComparison.Ordinal)) return "s";
        if (name.EndsWith("_minutes", StringComparison.Ordinal)) return "min";
        if (name is "usage" or "npu_usage" or "wear" || parent.EndsWith(".engines", StringComparison.Ordinal)) return "%";
        if (name is "width" or "height" or "x" or "y") return "px";
        return string.Empty;
    }

    private static readonly System.Text.RegularExpressions.Regex FirstItem = new(@"\[0\]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// <paramref name="countOf"/> gives the item count of a list by its path (gpu_usage.adapters);
    /// null when the list has not been seen, which keeps the [0] path as it is.
    /// </summary>
    internal static IReadOnlyList<string> ExpandAttributePaths(IEnumerable<string> paths, Func<string, int?> countOf)
    {
        var result = new List<string>();
        foreach (var path in paths)
        {
            var match = FirstItem.Match(path);
            if (!match.Success)
            {
                result.Add(path);
                continue;
            }

            var count = countOf(path[..match.Index]) ?? 1;
            for (var index = 0; index < Math.Max(1, count); index++)
            {
                result.Add($"{path[..match.Index]}[{index}]{path[(match.Index + match.Length)..]}");
            }
        }

        return result;
    }
}
