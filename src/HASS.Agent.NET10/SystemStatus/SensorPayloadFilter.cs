using System.Text.Json;
using System.Text.Json.Nodes;
using HASS.Agent.Companion.Configuration;

namespace HASS.Agent.Companion.SystemStatus;

/// <summary>
/// Leaves the built-in sensors that are switched off for this role out of the payload that
/// goes to Home Assistant. Some values are read anyway (the active window takes no effort, a
/// custom sensor may use a built-in one's attribute), and a sensor switched off used to be
/// left out only of the discovery: its value, the window title or the logged-in user, still
/// went to the broker with every update.
/// </summary>
internal static class SensorPayloadFilter
{
    // Fields that are not sensors of their own but go with one: the options of a select,
    // and what the display light needs besides the brightness.
    private static readonly Dictionary<string, string[]> Companions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["audio_output_device"] = ["audio_output_devices"],
        ["audio_input_device"] = ["audio_input_devices"],
        ["display_brightness"] = ["display_brightness_supported", "monitor_power_state"],
    };

    public static JsonObject WithoutDisabled(
        SystemMetricsMessage message,
        JsonSerializerOptions options,
        IReadOnlyList<BuiltInSensorSetting>? settings,
        bool serviceRole)
    {
        var payload = JsonSerializer.SerializeToNode(message, options)!.AsObject();
        if (settings is null)
        {
            return payload;
        }

        var enabled = settings
            .Where(sensor => serviceRole ? sensor.Service : sensor.TrayApp)
            .Select(sensor => sensor.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = new HashSet<string>(enabled, StringComparer.OrdinalIgnoreCase);
        foreach (var key in enabled)
        {
            if (Companions.TryGetValue(key, out var companions))
            {
                kept.UnionWith(companions);
            }
        }

        var removable = BuiltInSensorCatalog.AllKeys
            .Concat(Companions.Values.SelectMany(companions => companions))
            .Where(key => !kept.Contains(key))
            .ToList();
        var attributes = payload["attributes"] as JsonObject;
        foreach (var key in removable)
        {
            payload.Remove(key);
            attributes?.Remove(key);
        }

        return payload;
    }
}
