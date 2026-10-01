using System.Drawing;
using System.Text.Json.Serialization;

namespace HASS.Agent.Companion.SystemCommands;

internal sealed class CustomCommandDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Type { get; set; } = CustomCommandTypes.Process;

    public string Name { get; set; } = "Custom command";

    // For "process": the executable path. For "powershell"/"pwsh": the script path
    // or an inline command (see CommandArguments for how it is passed). For "key": the
    // key combinations to press ("win+r", "ctrl+c ctrl+v"). For "url" and "webview": the
    // address to open ("webview" takes an optional window size, "1024x720", as Arguments).
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

    [JsonIgnore]
    public bool IsKey => string.Equals(Type, CustomCommandTypes.Key, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsUrl => string.Equals(Type, CustomCommandTypes.Url, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsWebView => string.Equals(Type, CustomCommandTypes.WebView, StringComparison.OrdinalIgnoreCase);

    // Key presses, opening an address and showing a window only mean something on the
    // desktop of the logged-in user, so the service (session 0) never runs these.
    [JsonIgnore]
    public bool NeedsUserSession => IsKey || IsUrl || IsWebView;
}

internal static class CustomCommandTypes
{
    public const string Process = "process";
    public const string PowerShell = "powershell";
    public const string Pwsh = "pwsh";
    public const string Key = "key";
    public const string Url = "url";
    public const string WebView = "webview";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Process,
        PowerShell,
        Pwsh,
        Key,
        Url,
        WebView
    };

    public static string Normalize(string value)
    {
        return All.Contains(value ?? string.Empty) ? value!.Trim().ToLowerInvariant() : Process;
    }
}

internal enum CustomCommandOutcome
{
    Done,
    NeedsUserSession,
    UnknownKey,
    NoKeys,
    InputRefused,
    InvalidAddress,
    InvalidSize,
    NotStarted,
    Failed
}

internal readonly record struct CustomCommandResult(CustomCommandOutcome Outcome, string Detail = "")
{
    public bool Ok => Outcome == CustomCommandOutcome.Done;
}

/// <summary>What a web view window may show, and how big it is. Sizes are logical (96 DPI) pixels.</summary>
internal static class WebViewOptions
{
    public const int MinimumEdge = 200;
    public const int MaximumEdge = 4000;
    public static readonly Size DefaultPopupSize = new(420, 640);
    public static readonly Size DefaultWindowSize = new(1024, 720);

    /// <summary>Only web pages; anything else has no business in an embedded browser.</summary>
    public static bool IsWebAddress(string address)
    {
        return Uri.TryCreate(address, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    /// <summary>"1024x720" (also with *, × or a space in between).</summary>
    public static bool TryParseSize(string text, out Size size)
    {
        size = DefaultWindowSize;
        var parts = (text ?? string.Empty).Split(['x', 'X', '*', '×', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var width)
            || !int.TryParse(parts[1], out var height)
            || width < MinimumEdge || height < MinimumEdge
            || width > MaximumEdge || height > MaximumEdge)
        {
            return false;
        }

        size = new Size(width, height);
        return true;
    }
}

// Advertised to Home Assistant so the integration can create a button per command.
internal sealed record CustomCommandDescriptor(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name);
