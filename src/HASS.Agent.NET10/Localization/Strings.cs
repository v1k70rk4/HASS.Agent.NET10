using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HASS.Agent.Companion.Localization;

/// <summary>
/// The texts of the app, one JSON file per language in this folder, embedded in the program:
/// a "language" header (its code, its name as it calls itself, the Windows cultures it is for)
/// and the "strings". English is complete and stands in for any text a language lacks. A new
/// file is all a new language needs: it shows up in the language lists and is chosen on a
/// Windows of one of its cultures.
///
/// A translator can try a language without building the app: the tray app also reads the files
/// in %LOCALAPPDATA%\HASS.Agent.NET10\languages (LoadUserLanguages), for that Windows user only.
/// The Windows service never does, so nothing a user puts there reaches another user or SYSTEM.
/// </summary>
internal static partial class Strings
{
    internal sealed record LanguagePack(string Code, string Name, IReadOnlyList<string> Cultures, IReadOnlyDictionary<string, string> Texts);

    internal const string English = "en";
    private const string ResourcePrefix = "lang.";
    private const int MaxUserFiles = 20;
    private const long MaxFileBytes = 1024 * 1024;

    // Replaced whole, never changed in place: a reader on another thread sees the old set or
    // the new one, never half of one.
    private static Dictionary<string, LanguagePack> _packs = LoadEmbedded();
    private static string _language = English;
    private static string _haLanguage = English;

    public static string Language
    {
        get => _language;
        set => _language = Known(value);
    }

    public static string HaLanguage
    {
        get => _haLanguage;
        set => _haLanguage = Known(value);
    }

    /// <summary>English first, then the others by their own name.</summary>
    public static IReadOnlyList<string> AvailableLanguages
    {
        get
        {
            var packs = _packs;
            return packs.Values
                .OrderBy(pack => pack.Code == English ? 0 : 1)
                .ThenBy(pack => pack.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(pack => pack.Code)
                .ToList();
        }
    }

    public static string GetDisplayName(string langCode) =>
        _packs.TryGetValue(langCode, out var pack) ? pack.Name : langCode;

    public static string Get(string key) => Get(key, _language);

    public static string GetHa(string key) => Get(key, _haLanguage);

    internal static string Get(string key, string language)
    {
        var packs = _packs;
        if (packs.TryGetValue(language, out var pack) && pack.Texts.TryGetValue(key, out var value))
        {
            return value;
        }

        return packs.TryGetValue(English, out var english) && english.Texts.TryGetValue(key, out var fallback)
            ? fallback
            : key;
    }

    /// <summary>
    /// The language for a Windows culture: the first language that names the culture or one it
    /// derives from ("zh-CN", then "zh-Hans", then "zh"); English when none does.
    /// </summary>
    public static string LanguageForCulture(CultureInfo culture)
    {
        var packs = _packs;
        for (var current = culture; !Equals(current, CultureInfo.InvariantCulture); current = current.Parent)
        {
            foreach (var pack in packs.Values)
            {
                if (pack.Cultures.Any(name => string.Equals(name, current.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    return pack.Code;
                }
            }
        }

        return English;
    }

    /// <summary>
    /// Reads the language files of the user running the tray app. A file of a language the app
    /// has replaces those of its texts it has; a new language is added. English stays as it is
    /// built in, being what every other language falls back to. Returns what could not be taken,
    /// for the log.
    /// </summary>
    public static IReadOnlyList<string> LoadUserLanguages(string folder)
    {
        var problems = new List<string>();
        if (!Directory.Exists(folder))
        {
            return problems;
        }

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.OrdinalIgnoreCase).Take(MaxUserFiles).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // The built-in languages are all there is then: a language folder never stops the app.
            problems.Add($"The language folder {folder} could not be read: {ex.Message}");
            return problems;
        }

        var packs = new Dictionary<string, LanguagePack>(_packs, StringComparer.OrdinalIgnoreCase);
        var english = packs[English].Texts;
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            try
            {
                if (new FileInfo(file).Length > MaxFileBytes)
                {
                    problems.Add($"Language file {name} is over 1 MB; not read.");
                    continue;
                }

                var pack = Parse(File.ReadAllText(file), english, problems, name);
                if (pack is null)
                {
                    continue;
                }

                if (string.Equals(pack.Code, English, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"Language file {name} is English, which stays as built in; not read.");
                    continue;
                }

                if (packs.TryGetValue(pack.Code, out var builtIn))
                {
                    var texts = new Dictionary<string, string>(builtIn.Texts);
                    foreach (var (key, value) in pack.Texts)
                    {
                        texts[key] = value;
                    }

                    pack = pack with { Code = builtIn.Code, Texts = texts };
                }

                packs[pack.Code] = pack;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"Language file {name} could not be read: {ex.Message}");
            }
        }

        _packs = packs;
        return problems;
    }

    /// <summary>
    /// A language file, checked against English: a text English does not have is left out, and
    /// so is one whose {0}-style placeholders differ from the English text's or that would not
    /// format; English stands in for those. Null when the file itself is not usable.
    /// </summary>
    internal static LanguagePack? Parse(string json, IReadOnlyDictionary<string, string>? english, List<string> problems, string source)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            problems.Add($"Language file {source} is not valid JSON: {ex.Message}");
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("language", out var header) || header.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("strings", out var strings) || strings.ValueKind != JsonValueKind.Object)
            {
                problems.Add($"Language file {source} needs a \"language\" and a \"strings\" object; not read.");
                return null;
            }

            var code = header.TryGetProperty("code", out var codeValue) && codeValue.ValueKind == JsonValueKind.String ? codeValue.GetString() : null;
            var name = header.TryGetProperty("name", out var nameValue) && nameValue.ValueKind == JsonValueKind.String ? nameValue.GetString()?.Trim() : null;
            if (code is null || !LanguageCode().IsMatch(code) || string.IsNullOrEmpty(name))
            {
                problems.Add($"Language file {source} needs a language code (like \"zh-Hans\") and a name; not read.");
                return null;
            }

            var cultures = new List<string>();
            if (header.TryGetProperty("cultures", out var cultureValues) && cultureValues.ValueKind == JsonValueKind.Array)
            {
                cultures.AddRange(cultureValues.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String && LanguageCode().IsMatch(item.GetString() ?? ""))
                    .Select(item => item.GetString()!));
            }

            var texts = new Dictionary<string, string>(StringComparer.Ordinal);
            var dropped = new List<string>();
            foreach (var property in strings.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    dropped.Add(property.Name);
                    continue;
                }

                var text = property.Value.GetString()!;
                if (english is not null)
                {
                    if (!english.TryGetValue(property.Name, out var englishText) || !SamePlaceholders(englishText, text))
                    {
                        dropped.Add(property.Name);
                        continue;
                    }
                }

                texts[property.Name] = text;
            }

            if (dropped.Count > 0)
            {
                problems.Add($"Language file {source}: left out (English is used instead): {string.Join(", ", dropped.Take(20))}{(dropped.Count > 20 ? $" and {dropped.Count - 20} more" : "")}.");
            }

            return new LanguagePack(code, name, cultures, texts);
        }
    }

    /// <summary>
    /// The same {0}-style placeholders as the English text, and a text that formats wherever the
    /// English one does: a stray brace makes string.Format throw, also in a text with no
    /// placeholder. An index of more than three digits is no placeholder of this app and fails.
    /// </summary>
    internal static bool SamePlaceholders(string english, string translated)
    {
        var expected = Placeholders(english);
        var found = Placeholders(translated);
        if (expected is null || found is null || !expected.SetEquals(found))
        {
            return false;
        }

        var arguments = new object[expected.Count == 0 ? 0 : expected.Max() + 1];
        return !Formats(english, arguments) || Formats(translated, arguments);
    }

    private static bool Formats(string text, object[] arguments)
    {
        try
        {
            _ = string.Format(CultureInfo.InvariantCulture, text, arguments);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // Null when an index is out of all proportion (more than three digits).
    private static HashSet<int>? Placeholders(string text)
    {
        var indices = new HashSet<int>();
        foreach (Match match in Placeholder().Matches(text.Replace("{{", "").Replace("}}", "")))
        {
            if (match.Groups[1].Value.Length > 3
                || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            {
                return null;
            }

            indices.Add(index);
        }

        return indices;
    }

    private static string Known(string? language) =>
        language is not null && _packs.TryGetValue(language, out var pack) ? pack.Code : English;

    private static Dictionary<string, LanguagePack> LoadEmbedded()
    {
        var assembly = typeof(Strings).Assembly;
        var problems = new List<string>();
        string Read(string resource)
        {
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Missing language resource {resource}.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        var englishPack = Parse(Read(ResourcePrefix + English + ".json"), null, problems, "en.json")
            ?? throw new InvalidOperationException("The built-in English texts cannot be read.");
        var packs = new Dictionary<string, LanguagePack>(StringComparer.OrdinalIgnoreCase) { [English] = englishPack };
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
                     .Where(name => name != ResourcePrefix + English + ".json")
                     .Order(StringComparer.Ordinal))
        {
            var pack = Parse(Read(resource), englishPack.Texts, problems, resource[ResourcePrefix.Length..]);
            if (pack is not null)
            {
                packs[pack.Code] = pack;
            }
        }

        return packs;
    }

    /// <summary>Back to the built-in languages, for the tests.</summary>
    internal static void ResetToBuiltIn() => _packs = LoadEmbedded();

    [GeneratedRegex(@"^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$")]
    private static partial Regex LanguageCode();

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();
}
