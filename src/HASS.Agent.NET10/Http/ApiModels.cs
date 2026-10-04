using System.Text.Json;
using System.Text.Json.Serialization;
using HASS.Agent.Companion.SystemCommands;

namespace HASS.Agent.Companion.Http;

internal sealed class NotificationPayload
{
    public string? Message { get; init; }

    public string? Title { get; init; }

    public NotificationDataPayload? Data { get; init; }

    /// <summary>The action sent when a notification has text inputs but no button of its own.</summary>
    public const string ReplyAction = "reply";

    /// <summary>Windows shows at most five buttons on a notification; the app's own window follows suit.</summary>
    public const int MaxActions = 5;

    public const int MaxInputs = 5;

    public IReadOnlyList<NotificationActionPayload> Actions =>
        Data?.Actions?
            .Where(action => !string.IsNullOrWhiteSpace(action.Action))
            .Take(MaxActions)
            .ToList() ?? [];

    public bool HasActions => Actions.Count > 0;

    /// <summary>
    /// The text fields to show, each with an id that is unique within the notification
    /// (a missing or repeated one is replaced by "input1", "input2", ...).
    /// </summary>
    public IReadOnlyList<NotificationInputPayload> Inputs
    {
        get
        {
            var inputs = new List<NotificationInputPayload>();
            foreach (var input in Data?.Inputs ?? [])
            {
                if (input is null || inputs.Count == MaxInputs)
                {
                    continue;
                }

                var id = input.Id?.Trim();
                if (string.IsNullOrEmpty(id) || inputs.Any(known => string.Equals(known.Id, id, StringComparison.Ordinal)))
                {
                    id = $"input{inputs.Count + 1}";
                }

                inputs.Add(new NotificationInputPayload { Id = id, Title = input.Title?.Trim() });
            }

            return inputs;
        }
    }

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

    /// <summary>Web address of a picture to show with the notification.</summary>
    public string? Image { get; init; }

    /// <summary>
    /// The same picture as a path on Home Assistant, signed by the integration (10.9.0+).
    /// Used with this PC's own Home Assistant address when it has one, since that is the
    /// address known to be reachable from here.
    /// </summary>
    [JsonPropertyName("image_path")]
    public string? ImagePath { get; init; }

    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; init; }

    /// <summary>"toast" or "window": overrides the notification style setting for this one.</summary>
    public string? Style { get; init; }

    public List<NotificationActionPayload>? Actions { get; init; } = [];

    public List<NotificationInputPayload?>? Inputs { get; init; } = [];
}

internal sealed class NotificationInputPayload
{
    public string? Id { get; init; }

    /// <summary>Shown in the empty field as a hint.</summary>
    public string? Title { get; init; }
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
    [property: JsonPropertyName("custom_commands")] IReadOnlyList<HASS.Agent.Companion.SystemCommands.CustomCommandDescriptor>? CustomCommands = null,
    // The hotkeys the tray app reports as events (integration 10.9.0+ builds an event entity from them).
    [property: JsonPropertyName("hotkeys")] IReadOnlyList<HASS.Agent.Companion.Configuration.HotkeyDescriptor>? Hotkeys = null);
