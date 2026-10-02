using System.Text.Json.Serialization;

namespace HASS.Agent.Companion.SystemCommands;

internal static class SystemCommandCatalog
{
    public static IReadOnlyList<SystemCommandDefinition> Commands { get; } =
    [
        new("lock", SupportsTrayApp: true, SupportsService: false, DefaultTrayApp: true, DefaultService: false),
        new("sleep", SupportsTrayApp: true, SupportsService: false, DefaultTrayApp: true, DefaultService: false),
        // Off by default: a button nobody asked for should not be able to end a session.
        new("hibernate", SupportsTrayApp: true, SupportsService: false, DefaultTrayApp: false, DefaultService: false),
        new("logoff", SupportsTrayApp: true, SupportsService: false, DefaultTrayApp: false, DefaultService: false),
        new("monitor_off", SupportsTrayApp: true, SupportsService: false, DefaultTrayApp: true, DefaultService: false),
        new("volume_up", SupportsTrayApp: true, SupportsService: false, DefaultTrayApp: true, DefaultService: false),
        new("volume_down", SupportsTrayApp: true, SupportsService: false, DefaultTrayApp: true, DefaultService: false),
        new("toggle_mute", SupportsTrayApp: true, SupportsService: false, DefaultTrayApp: true, DefaultService: false),
        new("shutdown", SupportsTrayApp: true, SupportsService: true, DefaultTrayApp: true, DefaultService: true),
        new("restart", SupportsTrayApp: true, SupportsService: true, DefaultTrayApp: true, DefaultService: true),
        new("restart_cancel", SupportsTrayApp: true, SupportsService: true, DefaultTrayApp: true, DefaultService: true)
    ];

    public static IReadOnlyList<string> AllCommandNames { get; } = Commands.Select(command => command.Name).ToArray();

    public static IReadOnlyList<string> DefaultTrayAppCommands { get; } = Commands
        .Where(command => command.DefaultTrayApp)
        .Select(command => command.Name)
        .ToArray();

    public static IReadOnlyList<string> DefaultServiceCommands { get; } = Commands
        .Where(command => command.DefaultService)
        .Select(command => command.Name)
        .ToArray();

    public static IReadOnlySet<string> ServiceCapableCommands { get; } = Commands
        .Where(command => command.SupportsService)
        .Select(command => command.Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool TryNormalizeName(string? command, out string normalized)
    {
        normalized = command?.Trim().ToLowerInvariant() ?? string.Empty;
        return AllCommandNames.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }
}

internal sealed record SystemCommandDefinition(
    string Name,
    bool SupportsTrayApp,
    bool SupportsService,
    bool DefaultTrayApp,
    bool DefaultService);

internal sealed record SystemCommandDescriptor(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("display_name")] string DisplayName,
    [property: JsonPropertyName("comment")] string? Comment = null);
