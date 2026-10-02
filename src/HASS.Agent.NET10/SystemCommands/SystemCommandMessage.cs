using System.Text.Json.Serialization;

namespace HASS.Agent.Companion.SystemCommands;

internal sealed record SystemCommandMessage(
    [property: JsonPropertyName("command")] string? Command,
    [property: JsonPropertyName("force")] bool Force,
    [property: JsonPropertyName("time")] int Time,
    [property: JsonPropertyName("comment")] string? Comment,
    [property: JsonPropertyName("restart_cancel")] bool RestartCancel,
    // For commands that carry a number, like set_brightness (percent).
    [property: JsonPropertyName("value")] int? Value = null);
