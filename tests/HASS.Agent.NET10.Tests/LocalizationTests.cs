using System.Globalization;
using System.Reflection;
using System.Text.Json;
using HASS.Agent.Companion.Localization;

namespace HASS.Agent.Companion.Tests;

/// <summary>
/// The language files: every built-in language has exactly the English texts, with the same
/// placeholders, and a translator's file is checked the same way before the app uses it.
/// </summary>
[Collection(nameof(LocalizationCollection))]
public sealed class LocalizationTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("hass-agent-languages-").FullName;

    public void Dispose()
    {
        Strings.ResetToBuiltIn();
        Directory.Delete(_folder, recursive: true);
    }

    private static readonly Assembly App = typeof(Strings).Assembly;

    public static TheoryData<string> BuiltInFiles()
    {
        var data = new TheoryData<string>();
        foreach (var name in App.GetManifestResourceNames().Where(name => name.StartsWith("lang.", StringComparison.Ordinal)))
        {
            data.Add(name);
        }

        return data;
    }

    private static Dictionary<string, string> TextsOf(string resource)
    {
        using var stream = App.GetManifestResourceStream(resource)!;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("strings").EnumerateObject().ToDictionary(item => item.Name, item => item.Value.GetString()!);
    }

    [Fact]
    public void English_and_hungarian_are_built_in()
    {
        Assert.Contains("en", Strings.AvailableLanguages);
        Assert.Contains("hu", Strings.AvailableLanguages);
        Assert.Equal("en", Strings.AvailableLanguages[0]);
        Assert.Equal("Magyar", Strings.GetDisplayName("hu"));
    }

    [Theory]
    [MemberData(nameof(BuiltInFiles))]
    public void A_built_in_language_has_every_english_text_and_no_other(string resource)
    {
        var english = TextsOf("lang.en.json");
        var texts = TextsOf(resource);

        Assert.Empty(english.Keys.Except(texts.Keys));
        Assert.Empty(texts.Keys.Except(english.Keys));
    }

    [Theory]
    [MemberData(nameof(BuiltInFiles))]
    public void A_built_in_text_has_the_placeholders_of_the_english_one(string resource)
    {
        var english = TextsOf("lang.en.json");
        var wrong = TextsOf(resource).Where(item => !Strings.SamePlaceholders(english[item.Key], item.Value)).Select(item => item.Key);

        Assert.Empty(wrong);
    }

    [Theory]
    [MemberData(nameof(BuiltInFiles))]
    public void A_built_in_language_names_itself_and_its_cultures(string resource)
    {
        using var stream = App.GetManifestResourceStream(resource)!;
        using var document = JsonDocument.Parse(stream);
        var header = document.RootElement.GetProperty("language");

        Assert.Equal(resource["lang.".Length..^".json".Length], header.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(header.GetProperty("name").GetString()));
        Assert.NotEmpty(header.GetProperty("cultures").EnumerateArray());
    }

    [Theory]
    [MemberData(nameof(BuiltInFiles))]
    public void A_todo_list_names_texts_that_exist(string resource)
    {
        // The keys waiting for the translator (new ones in English, or put together from their
        // words for them to check); a misspelt one would never be looked at.
        using var stream = App.GetManifestResourceStream(resource)!;
        using var document = JsonDocument.Parse(stream);
        if (!document.RootElement.GetProperty("language").TryGetProperty("todo", out var todo))
        {
            return;
        }

        var english = TextsOf("lang.en.json");
        Assert.Equal(JsonValueKind.Array, todo.ValueKind);
        Assert.All(todo.EnumerateArray(), item => Assert.Contains(item.GetString() ?? string.Empty, english.Keys));
    }

    [Fact]
    public void The_language_of_windows_is_chosen_from_the_files()
    {
        Assert.Equal("hu", Strings.LanguageForCulture(new CultureInfo("hu-HU")));
        Assert.Equal("en", Strings.LanguageForCulture(new CultureInfo("en-GB")));
        Assert.Equal("en", Strings.LanguageForCulture(new CultureInfo("ja-JP")));   // none: English
    }

    [Fact]
    public void A_translators_file_adds_a_language_found_by_its_culture()
    {
        Write("zh-Hans.json", "zh-Hans", "简体中文", ["zh-Hans"], new() { ["Nav.General"] = "常规" });

        Assert.Empty(Strings.LoadUserLanguages(_folder));

        Assert.Contains("zh-Hans", Strings.AvailableLanguages);
        Assert.Equal("简体中文", Strings.GetDisplayName("zh-Hans"));
        Assert.Equal("zh-Hans", Strings.LanguageForCulture(new CultureInfo("zh-CN")));   // zh-CN → zh-Hans
        Assert.Equal("常规", Strings.Get("Nav.General", "zh-Hans"));
        Assert.Equal(Strings.Get("Nav.Mqtt", "en"), Strings.Get("Nav.Mqtt", "zh-Hans"));   // not translated: English
    }

    [Fact]
    public void A_text_with_other_placeholders_or_an_unknown_key_is_left_out()
    {
        var englishFormat = Strings.Get("Access.Failed", "en");
        Assert.Contains("{0}", englishFormat);
        Write("xx.json", "xx", "Test", ["xx"], new()
        {
            ["Access.Failed"] = "no placeholder at all",
            ["App.AlreadyRunning"] = "{0} {",                   // would not format
            ["Nav.General"] = "fine",
            ["No.Such.Key"] = "?",
        });

        var problems = Strings.LoadUserLanguages(_folder);

        var problem = Assert.Single(problems);
        Assert.Contains("Access.Failed", problem);
        Assert.Contains("App.AlreadyRunning", problem);
        Assert.Contains("No.Such.Key", problem);
        Assert.Equal(englishFormat, Strings.Get("Access.Failed", "xx"));
        Assert.Equal("fine", Strings.Get("Nav.General", "xx"));
    }

    [Theory]
    [InlineData("General", "{", false)]                        // a stray brace would not format
    [InlineData("General", "Altalanos {0}", false)]            // a placeholder English does not have
    [InlineData("{0} is running", "{99999999999999999999} fut", false)]   // no overflow, just refused
    [InlineData("{0} is running", "{0} fut", true)]
    [InlineData("{0} of {1}", "{1}-ből {0}", true)]           // order is the language's
    [InlineData("Use {{braces}}", "Kapcsos {{zárójel}}", true)]
    public void A_text_is_checked_against_the_english_one(string english, string translated, bool taken)
    {
        Assert.Equal(taken, Strings.SamePlaceholders(english, translated));
    }

    [Fact]
    public void A_rejected_text_of_a_built_in_language_keeps_the_built_in_translation()
    {
        var builtIn = Strings.Get("Access.Failed", "hu");
        Write("hu.json", "hu", "Magyar", ["hu"], new() { ["Access.Failed"] = "nincs helyőrző" });

        Assert.Single(Strings.LoadUserLanguages(_folder));

        Assert.Equal(builtIn, Strings.Get("Access.Failed", "hu"));
    }

    [Fact]
    public void A_translators_file_for_a_built_in_language_replaces_only_its_own_texts()
    {
        var before = Strings.Get("Nav.Mqtt", "hu");
        Write("hu.json", "hu", "Magyar (javítás)", ["hu"], new() { ["Nav.General"] = "Általános (javított)" });

        Assert.Empty(Strings.LoadUserLanguages(_folder));

        Assert.Equal("Általános (javított)", Strings.Get("Nav.General", "hu"));
        Assert.Equal(before, Strings.Get("Nav.Mqtt", "hu"));
    }

    [Fact]
    public void English_and_broken_files_are_not_taken()
    {
        Write("en.json", "en", "English", ["en"], new() { ["Nav.General"] = "Changed" });
        File.WriteAllText(Path.Combine(_folder, "broken.json"), "{ not json");
        File.WriteAllText(Path.Combine(_folder, "nameless.json"), """{ "language": { "code": "yy" }, "strings": {} }""");

        var problems = Strings.LoadUserLanguages(_folder);

        Assert.Equal(3, problems.Count);
        Assert.NotEqual("Changed", Strings.Get("Nav.General", "en"));
        Assert.DoesNotContain("yy", Strings.AvailableLanguages);
    }

    private void Write(string file, string code, string name, string[] cultures, Dictionary<string, string> texts)
    {
        var document = new { language = new { code, name, cultures }, strings = texts };
        File.WriteAllText(Path.Combine(_folder, file), JsonSerializer.Serialize(document));
    }
}

/// <summary>The tests that load language files change the app-wide set: one at a time.</summary>
[CollectionDefinition(nameof(LocalizationCollection), DisableParallelization = true)]
public sealed class LocalizationCollection;
