using System.Text.Json.Serialization;

namespace HASS.Agent.Companion.SystemCommands;

internal sealed record SystemCommandMessage(
    [property: JsonPropertyName("command")] string? Command,
    [property: JsonPropertyName("force")] bool Force,
    [property: JsonPropertyName("time")] int Time,
    [property: JsonPropertyName("comment")] string? Comment,
    [property: JsonPropertyName("restart_cancel")] bool RestartCancel,
    // For commands that carry a number, like set_brightness (percent).
    [property: JsonPropertyName("value")] int? Value = null,
    // For commands that carry a name, like set_audio_output (the device).
    [property: JsonPropertyName("text")] string? Text = null,
    // For set_app_volume: mute or unmute the app (next to, or instead of, a volume).
    [property: JsonPropertyName("muted")] bool? Muted = null);
