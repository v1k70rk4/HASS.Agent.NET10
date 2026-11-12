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
    // address to open ("webview" takes what its window is like as Arguments: a size, "keep",
    // a time after which it closes; see WebViewOptions.TryParseWindow). For "webview_close":
    // the name of the "webview" command whose window is closed, or "*" for every one.
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

    [JsonIgnore]
    public bool IsWebViewClose => string.Equals(Type, CustomCommandTypes.WebViewClose, StringComparison.OrdinalIgnoreCase);

    // Key presses, opening an address, showing a window and closing one only mean something
    // on the desktop of the logged-in user, so the service (session 0) never runs these.
    [JsonIgnore]
    public bool NeedsUserSession => IsKey || IsUrl || IsWebView || IsWebViewClose;
}

internal static class CustomCommandTypes
{
    public const string Process = "process";
    public const string PowerShell = "powershell";
    public const string Pwsh = "pwsh";
    public const string Key = "key";
    public const string Url = "url";
    public const string WebView = "webview";
    public const string WebViewClose = "webview_close";

    /// <summary>The types in the order the editor and the list offer them.</summary>
    public static IReadOnlyList<string> InOrder { get; } = [Process, PowerShell, Pwsh, Key, Url, WebView, WebViewClose];

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(InOrder, StringComparer.OrdinalIgnoreCase);

    /// <summary>The types that act on the desktop of the logged-in user: the service never runs them.</summary>
    public static bool NeedsUserSession(string type) => type is Key or Url or WebView or WebViewClose;

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

    /// <summary>In a Popup window command: the page stays loaded when its window goes away.</summary>
    public const string KeepWord = "keep";

    /// <summary>In a Popup window command: the window is one like the dashboard popup, above the tray.</summary>
    public const string PopupWord = "popup";

    /// <summary>In a Close popup windows command: every window, not one command's.</summary>
    public const string EveryWindow = "*";

    public static readonly TimeSpan LongestCloseAfter = TimeSpan.FromHours(24);

    /// <summary>
    /// What a Popup window command says about its window, in any order and each of them
    /// optional: a size ("1024x720"), "popup" (no frame, above the tray and over the other
    /// windows, gone at a click elsewhere, like the dashboard popup), "keep" (the page stays
    /// loaded when the window goes away, and comes back as it was), and a time after which
    /// the window goes away by itself ("30s", "5m", "1h"). Nothing at all is the window as it
    /// always was.
    /// </summary>
    public static bool TryParseWindow(string? text, out PopupWindowOptions options)
    {
        options = new PopupWindowOptions(DefaultWindowSize, KeepLoaded: false, CloseAfter: null);
        var keep = false;
        var aboveTray = false;
        TimeSpan? closeAfter = null;
        var rest = new List<string>();
        foreach (var word in (text ?? string.Empty).Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(word, KeepWord, StringComparison.OrdinalIgnoreCase))
            {
                if (keep)
                {
                    return false;
                }

                keep = true;
            }
            else if (string.Equals(word, PopupWord, StringComparison.OrdinalIgnoreCase))
            {
                if (aboveTray)
                {
                    return false;
                }

                aboveTray = true;
            }
            else if (word.Length > 1 && char.ToLowerInvariant(word[^1]) is 's' or 'm' or 'h' && word[..^1].All(char.IsAsciiDigit))
            {
                // Longer than any time that is taken: no number to read at all.
                if (closeAfter is not null || word.Length > 7)
                {
                    return false;
                }

                var number = int.Parse(word[..^1], System.Globalization.CultureInfo.InvariantCulture);
                var time = char.ToLowerInvariant(word[^1]) switch
                {
                    's' => TimeSpan.FromSeconds(number),
                    'm' => TimeSpan.FromMinutes(number),
                    _ => TimeSpan.FromHours(number)
                };
                if (time < TimeSpan.FromSeconds(1) || time > LongestCloseAfter)
                {
                    return false;
                }

                closeAfter = time;
            }
            else
            {
                rest.Add(word);
            }
        }

        var size = DefaultWindowSize;
        if (rest.Count > 0 && !TryParseSize(string.Join(' ', rest), out size))
        {
            return false;
        }

        options = new PopupWindowOptions(size, keep, closeAfter, aboveTray);
        return true;
    }

    /// <summary>
    /// What <see cref="TryParseWindow"/> reads, written out: how the fields of the editor are
    /// kept in the command, and what the list shows for them.
    /// </summary>
    public static string Format(PopupWindowOptions options)
    {
        var parts = new List<string> { $"{options.Size.Width}x{options.Size.Height}" };
        if (options.AboveTray)
        {
            parts.Add(PopupWord);
        }

        if (options.KeepLoaded)
        {
            parts.Add(KeepWord);
        }

        if (options.CloseAfter is { } after)
        {
            parts.Add(((long)after.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture) + "s");
        }

        return string.Join(' ', parts);
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

/// <summary>
/// The window of a Popup window command: how big, whether its page stays loaded, when it closes
/// by itself, and whether it is a window of the usual kind or one like the dashboard popup.
/// </summary>
internal readonly record struct PopupWindowOptions(Size Size, bool KeepLoaded, TimeSpan? CloseAfter, bool AboveTray = false);

/// <summary>A Popup window command asking the tray app for its window.</summary>
internal sealed record PopupWindowRequest(string CommandId, string CommandName, string Url, PopupWindowOptions Options);

// Advertised to Home Assistant so the integration can create a button per command.
internal sealed record CustomCommandDescriptor(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name);
