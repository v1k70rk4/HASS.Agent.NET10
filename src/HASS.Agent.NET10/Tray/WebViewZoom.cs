using System.Text.Json;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.Runtime;

namespace HASS.Agent.Companion.Tray;

/// <summary>
/// The zoom each page was last shown at in an embedded browser. WebView2 keeps a zoom the
/// user set (Ctrl+scroll) for the page it is on and no longer, and the dashboard popup makes
/// a new browser each time it opens, so the level is kept here, by address, per Windows
/// user next to the browser profile, and given back to the browser as its default zoom when
/// the page opens again.
/// </summary>
internal sealed class WebViewZoom
{
    /// <summary>What WebView2 itself allows, near enough; anything else in the file is ignored.</summary>
    public const double Minimum = 0.25;
    public const double Maximum = 5.0;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly FileLog? _log;
    private readonly Dictionary<string, double> _levels = new(StringComparer.Ordinal);

    public WebViewZoom(string path, FileLog? log = null)
    {
        _path = path;
        _log = log;
        Load();
    }

    /// <summary>The file of the Windows user running the app, beside the browser profile.</summary>
    public static WebViewZoom ForUser(FileLog? log) => new(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppIdentity.ConfigurationDirectoryName,
            "webview-zoom.json"),
        log);

    /// <summary>The zoom the page was last shown at, or null when it was not zoomed.</summary>
    public double? Get(string url) => _levels.TryGetValue(url, out var level) ? level : null;

    /// <summary>Keeps the page's zoom; the default (1.0) is forgotten rather than kept.</summary>
    public void Set(string url, double zoom)
    {
        if (string.IsNullOrEmpty(url) || !IsLevel(zoom))
        {
            return;
        }

        zoom = Math.Round(zoom, 4);
        bool changed;
        if (zoom == 1.0)
        {
            changed = _levels.Remove(url);
        }
        else
        {
            changed = !_levels.TryGetValue(url, out var current) || current != zoom;
            _levels[url] = zoom;
        }

        if (changed)
        {
            Save();
        }
    }

    private static bool IsLevel(double zoom) => !double.IsNaN(zoom) && zoom >= Minimum && zoom <= Maximum;

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var levels = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(_path));
            foreach (var (url, zoom) in levels ?? new Dictionary<string, double>())
            {
                if (!string.IsNullOrEmpty(url) && IsLevel(zoom))
                {
                    _levels[url] = zoom;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A file that cannot be read, or is not what it should be, starts over.
            _log?.Warning($"The zoom levels of the embedded browser could not be read from {_path}: {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (_levels.Count == 0)
            {
                File.Delete(_path);
                return;
            }

            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_levels, JsonOptions));
            File.Move(temporary, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Warning($"The zoom levels of the embedded browser could not be written to {_path}: {ex.Message}");
        }
    }
}
