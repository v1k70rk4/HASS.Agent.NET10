using System.Text.Json.Serialization;

namespace HASS.Agent.Companion.SystemCommands;

internal sealed class CustomCommandDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Type { get; set; } = CustomCommandTypes.Process;

    public string Name { get; set; } = "Custom command";

    // For "process": the executable path. For "powershell"/"pwsh": the script path
    // or an inline command (see CommandArguments for how it is passed).
    public string Command { get; set; } = string.Empty;

    // For "process": command-line arguments. For "powershell"/"pwsh": ignored when
    // Command is an inline command; used as script arguments when Command is a .ps1.
    public string Arguments { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public bool Service { get; set; }

    public bool TrayApp { get; set; } = true;

    [JsonIgnore]
    public bool IsProcess => string.Equals(Type, CustomCommandTypes.Process, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsPowerShell => string.Equals(Type, CustomCommandTypes.PowerShell, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsPwsh => string.Equals(Type, CustomCommandTypes.Pwsh, StringComparison.OrdinalIgnoreCase);
}

internal static class CustomCommandTypes
{
    public const string Process = "process";
    public const string PowerShell = "powershell";
    public const string Pwsh = "pwsh";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Process,
        PowerShell,
        Pwsh
    };

    public static string Normalize(string value)
    {
        return All.Contains(value ?? string.Empty) ? value!.Trim().ToLowerInvariant() : Process;
    }
}

// Advertised to Home Assistant so the integration can create a button per command.
internal sealed record CustomCommandDescriptor(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name);
