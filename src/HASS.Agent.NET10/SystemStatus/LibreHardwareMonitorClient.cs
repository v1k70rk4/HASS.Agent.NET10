using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HASS.Agent.Companion.SystemStatus;

/// <summary>
/// Reads hardware sensors (temperatures, fan speeds, voltages, loads) from a running
/// LibreHardwareMonitor through its "Remote Web Server" (data.json). The agent itself
/// stays free of vendor-specific hardware code: whoever wants these values runs
/// LibreHardwareMonitor, and the agent only asks it.
/// </summary>
internal static class LibreHardwareMonitorClient
{
    public const string DefaultUrl = "http://localhost:8085";

    // One request serves every sensor of a polling cycle, and a monitor that is not
    // running costs one timeout per cycle rather than one per sensor.
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(2);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private static readonly object Gate = new();
    private static IReadOnlyList<Reading> _cache = [];
    private static string? _cacheError;
    private static DateTime _cachedAt = DateTime.MinValue;
    private static string _cachedUrl = string.Empty;

    /// <summary>Where LibreHardwareMonitor's web server listens; from settings.</summary>
    public static string BaseUrl { get; private set; } = DefaultUrl;

    // LibreHardwareMonitor can ask for a user name and password (HTTP Basic).
    private static string _user = string.Empty;
    private static string _password = string.Empty;

    public static void Configure(string baseUrl, string user, string password)
    {
        lock (Gate)
        {
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DefaultUrl : baseUrl.Trim();
            _user = user ?? string.Empty;
            _password = password ?? string.Empty;
            _cachedAt = DateTime.MinValue;
        }
    }

    /// <param name="Id">LibreHardwareMonitor's own sensor id ("/intelcpu/0/temperature/0"), or the path of names when an old version does not send one.</param>
    /// <param name="Value">The number, when the value is one.</param>
    /// <param name="Text">The value as LibreHardwareMonitor shows it ("45,0 °C").</param>
    internal sealed record Reading(string Id, string Hardware, string Name, double? Value, string Unit, string Text);

    /// <summary>Every sensor LibreHardwareMonitor reports right now. Throws when it cannot be reached.</summary>
    public static IReadOnlyList<Reading> ReadAll()
    {
        lock (Gate)
        {
            var url = BuildDataUrl(BaseUrl);
            if (url == _cachedUrl && DateTime.UtcNow - _cachedAt < CacheLifetime)
            {
                return _cacheError is null ? _cache : throw new InvalidOperationException(_cacheError);
            }

            _cachedUrl = url;
            _cachedAt = DateTime.UtcNow;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (_user.Length > 0)
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue(
                        "Basic",
                        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_user}:{_password}")));
                }

                using var response = Http.Send(request);
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    throw new InvalidOperationException(_user.Length > 0
                        ? "the user name or password was refused"
                        : "it asks for a user name and password");
                }

                response.EnsureSuccessStatusCode();
                using var reader = new StreamReader(response.Content.ReadAsStream(), Encoding.UTF8);
                _cache = Parse(reader.ReadToEnd());
                _cacheError = null;
                return _cache;
            }
            catch (Exception ex)
            {
                _cache = [];
                _cacheError = $"LibreHardwareMonitor ({url}): {ex.Message}";
                throw new InvalidOperationException(_cacheError);
            }
        }
    }

    /// <summary>The reading with this id, or null when LibreHardwareMonitor is not there or has no such sensor.</summary>
    public static Reading? Find(string id)
    {
        try
        {
            return ReadAll().FirstOrDefault(reading => string.Equals(reading.Id, id, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The sensor id out of a parameter. The settings editor's list shows
    /// "hardware / name = value | id" (the readable part first, ids can be very long);
    /// what is stored is the id alone.
    /// </summary>
    public static string ParseSensorId(string parameter)
    {
        var text = parameter ?? string.Empty;
        var separator = text.LastIndexOf(" | ", StringComparison.Ordinal);
        return (separator >= 0 ? text[(separator + 3)..] : text).Trim();
    }

    internal static string BuildDataUrl(string baseUrl)
    {
        var trimmed = string.IsNullOrWhiteSpace(baseUrl) ? DefaultUrl : baseUrl.Trim();
        return trimmed.EndsWith("data.json", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed.TrimEnd('/') + "/data.json";
    }

    internal static IReadOnlyList<Reading> Parse(string json)
    {
        var readings = new List<Reading>();
        using var document = JsonDocument.Parse(json);
        Walk(document.RootElement, [], readings);
        return readings;
    }

    // The tree is: root, computer, hardware (possibly nested: a board and its chip),
    // a group per kind ("Temperatures"), then the sensors, which are the leaves.
    private static void Walk(JsonElement node, List<string> path, List<Reading> readings)
    {
        var text = GetString(node, "Text");
        var hasChildren = node.TryGetProperty("Children", out var children)
            && children.ValueKind == JsonValueKind.Array
            && children.GetArrayLength() > 0;

        if (!hasChildren)
        {
            var value = GetString(node, "Value");
            var sensorId = GetString(node, "SensorId");
            if (value.Length == 0 && sensorId.Length == 0)
            {
                return;
            }

            // path: [root, computer, hardware..., group]
            var hardware = path.Count > 3 ? string.Join(" / ", path.Skip(2).Take(path.Count - 3)) : string.Empty;
            var (number, unit) = SplitValue(value);
            readings.Add(new Reading(
                sensorId.Length > 0 ? sensorId : string.Join("/", path.Skip(2).Append(text)),
                hardware,
                text,
                number,
                unit,
                value));
            return;
        }

        path.Add(text);
        foreach (var child in children.EnumerateArray())
        {
            Walk(child, path, readings);
        }

        path.RemoveAt(path.Count - 1);
    }

    // "45,0 °C" / "1200 RPM" / "3.3 V": the values come formatted for display, with the
    // decimal separator of whoever runs LibreHardwareMonitor.
    internal static (double? Number, string Unit) SplitValue(string value)
    {
        var trimmed = value.Trim();
        var end = 0;
        while (end < trimmed.Length && (char.IsDigit(trimmed[end]) || trimmed[end] is '.' or ',' or '-' or '+'))
        {
            end++;
        }

        if (end == 0)
        {
            return (null, string.Empty);
        }

        var unit = trimmed[end..].Trim();
        return double.TryParse(trimmed[..end].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? (number, unit)
            : (null, unit);
    }

    private static string GetString(JsonElement node, string name)
    {
        return node.ValueKind == JsonValueKind.Object
            && node.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString() ?? string.Empty
                : string.Empty;
    }
}
