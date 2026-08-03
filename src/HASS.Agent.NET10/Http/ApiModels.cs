using System.Text.Json;
using System.Text.Json.Serialization;
using HASS.Agent.Companion.SystemCommands;

namespace HASS.Agent.Companion.Http;

internal sealed class NotificationPayload
{
    public string? Message { get; init; }

    public string? Title { get; init; }

    public NotificationDataPayload? Data { get; init; }

    public string? PrimaryAction => Data?.Actions.FirstOrDefault(action => !string.IsNullOrWhiteSpace(action.Action))?.Action;

    public IReadOnlyList<NotificationActionPayload> Actions =>
        Data?.Actions
            .Where(action => !string.IsNullOrWhiteSpace(action.Action))
            .ToList() ?? [];

    public bool HasActions => Actions.Count > 0;

    public int TimeoutMilliseconds
    {
        get
        {
            if (Data?.Duration is not > 0)
            {
                return 10_000;
            }

            return Math.Clamp(Data.Duration * 1000, 1_000, 60_000);
        }
    }
}

internal sealed class NotificationDataPayload
{
    public int Duration { get; init; }

    public string? Image { get; init; }

    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; init; }

    public List<NotificationActionPayload> Actions { get; init; } = [];
}

internal sealed class NotificationActionPayload
{
    public string? Action { get; init; }

    public string? Title { get; init; }
}

internal sealed record InfoResponse(
    [property: JsonPropertyName("serial_number")] string SerialNumber,
    [property: JsonPropertyName("device")] DeviceInfoResponse Device,
    [property: JsonPropertyName("apis")] ApiCapabilitiesResponse Apis);

internal sealed record DeviceInfoResponse(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("manufacturer")] string Manufacturer,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("sw_version")] string SoftwareVersion);

internal sealed record ApiCapabilitiesResponse(
    [property: JsonPropertyName("notifications")] bool Notifications,
    [property: JsonPropertyName("media_player")] bool MediaPlayer,
    [property: JsonPropertyName("buttons")] bool Buttons,
    [property: JsonPropertyName("system_sensors")] bool SystemSensors,
    [property: JsonPropertyName("update")] bool Update,
    [property: JsonPropertyName("commands")] IReadOnlyList<SystemCommandDescriptor> Commands,
    [property: JsonPropertyName("custom_sensors")] IReadOnlyList<HASS.Agent.Companion.SystemStatus.CustomSensorDescriptor>? CustomSensors = null,
    [property: JsonPropertyName("standard_sensors")] IReadOnlyList<HASS.Agent.Companion.SystemStatus.BuiltInSensorDescriptor>? StandardSensors = null,
    [property: JsonPropertyName("custom_commands")] IReadOnlyList<HASS.Agent.Companion.SystemCommands.CustomCommandDescriptor>? CustomCommands = null);
