using System.Text.Json.Serialization;

namespace HASS.Agent.Companion.Configuration;

/// <summary>A global key combination that is reported to Home Assistant as an event.</summary>
internal sealed class HotkeyDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>What Home Assistant sees: the event type, and the name in the UI.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>One combination in the key command syntax, e.g. "ctrl+alt+h".</summary>
    public string Keys { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}

// Advertised to Home Assistant so the integration knows the event types to expect.
internal sealed record HotkeyDescriptor(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("keys")] string Keys);
