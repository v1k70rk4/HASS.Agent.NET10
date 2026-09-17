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
        new("memory_usage", SupportsService: true, SupportsTrayApp: true, SensorPollingProfile.Fast),
        new("memory_available_mb", SupportsService: true, SupportsTrayApp: true, SensorPollingProfile.Fast),
        new("system_drive_free_percent", SupportsService: true, SupportsTrayApp: true, SensorPollingProfile.Normal),
        new("system_drive_free_gb", SupportsService: true, SupportsTrayApp: true, SensorPollingProfile.Normal),
        new("uptime_seconds", SupportsService: true, SupportsTrayApp: true, SensorPollingProfile.Fast),
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
            "gpu_usage.engines.3d",
            "gpu_usage.engines.videodecode",
            "gpu_usage.engines.videoencode",
            "gpu_usage.npu_usage",
            "gpu_usage.memory_dedicated_mb",
            "gpu_usage.memory_shared_mb",
            "gpu_usage.adapters[0].name",
            "gpu_usage.adapters[0].memory_mb"
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
        new("microphone_muted", SupportsService: false, SupportsTrayApp: true, SensorPollingProfile.Normal, PushDriven: true)
    ];

    public static IReadOnlySet<string> AllKeys { get; } = Sensors
        .Select(sensor => sensor.Key)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static BuiltInSensorDefinition? Find(string key)
    {
        return Sensors.FirstOrDefault(sensor => string.Equals(sensor.Key, key, StringComparison.OrdinalIgnoreCase));
    }
}
