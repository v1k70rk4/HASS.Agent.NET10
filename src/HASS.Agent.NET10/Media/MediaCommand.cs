using System.Text.Json.Serialization;

namespace HASS.Agent.Companion.Media;

internal sealed class MediaCommand
{
    [JsonPropertyName("command")]
    public string? Command { get; init; }

    [JsonPropertyName("data")]
    public object? Data { get; init; }

    /// <summary>
    /// A playmedia sent with Home Assistant's announce flag (integration 10.10.0+): it is meant to
    /// be heard, so the PC is unmuted and turned up for it when the user allows that.
    /// </summary>
    [JsonPropertyName("announce")]
    public bool Announce { get; init; }
}
