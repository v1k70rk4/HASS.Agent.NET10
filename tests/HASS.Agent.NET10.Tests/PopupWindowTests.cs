using System.Drawing;
using System.Text.Json;
using HASS.Agent.Companion.Localization;
using HASS.Agent.Companion.SystemCommands;
using HASS.Agent.Companion.Tray;

namespace HASS.Agent.Companion.Tests;

/// <summary>
/// The popup windows: what a Popup window command says about its window, when a window is
/// brought up again and when it is made anew, and which windows a close is for.
/// </summary>
public sealed class PopupWindowTests : IDisposable
{
    public void Dispose()
    {
        SystemCommandService.WebViewHandler = null;
        SystemCommandService.WebViewCloseHandler = null;
    }

    [Fact]
    public void Nothing_said_is_the_window_as_it_always_was()
    {
        Assert.True(WebViewOptions.TryParseWindow("", out var window));
        Assert.Equal(new PopupWindowOptions(WebViewOptions.DefaultWindowSize, KeepLoaded: false, CloseAfter: null), window);

        Assert.True(WebViewOptions.TryParseWindow(null, out window));
        Assert.Equal(WebViewOptions.DefaultWindowSize, window.Size);
    }

    [Theory]
    [InlineData("800x600", 800, 600, false, 0)]
    [InlineData("800 X 600", 800, 600, false, 0)]      // the spellings a size alone always had
    [InlineData("640*480", 640, 480, false, 0)]
    [InlineData("keep", 1024, 720, true, 0)]
    [InlineData("30s", 1024, 720, false, 30)]
    [InlineData("800x600 keep 30s", 800, 600, true, 30)]
    [InlineData("5m KEEP 800x600", 800, 600, true, 300)]
    [InlineData("keep, 1h", 1024, 720, true, 3600)]
    [InlineData("800 x 600 keep", 800, 600, true, 0)]
    [InlineData("24h", 1024, 720, false, 86400)]
    [InlineData("1S", 1024, 720, false, 1)]
    public void The_parts_come_in_any_order_and_each_may_be_left_out(string text, int width, int height, bool keep, int seconds)
    {
        Assert.True(WebViewOptions.TryParseWindow(text, out var window));

        Assert.Equal(new Size(width, height), window.Size);
        Assert.Equal(keep, window.KeepLoaded);
        Assert.Equal(seconds == 0 ? null : TimeSpan.FromSeconds(seconds), window.CloseAfter);
    }

    [Theory]
    [InlineData("keep keep")]
    [InlineData("30s 5m")]
    [InlineData("0s")]
    [InlineData("25h")]
    [InlineData("99999999s")]
    [InlineData("30")]             // a number alone is neither a size nor a time
    [InlineData("30x")]
    [InlineData("kept")]
    [InlineData("100x100 keep")]   // a size under the minimum
    [InlineData("800x600 600x400")]
    [InlineData("s")]
    public void What_cannot_be_read_is_refused_whole(string text)
    {
        Assert.False(WebViewOptions.TryParseWindow(text, out var window));
        Assert.Equal(new PopupWindowOptions(WebViewOptions.DefaultWindowSize, KeepLoaded: false, CloseAfter: null), window);
    }

    [Theory]
    // there is one, it keeps its page, to be kept now, same address, same size
    [InlineData(false, false, false, false, false, nameof(PopupReuse.Create))]
    [InlineData(false, true, true, true, true, nameof(PopupReuse.Create))]
    [InlineData(true, true, true, true, true, nameof(PopupReuse.ShowAgain))]
    [InlineData(true, false, false, true, true, nameof(PopupReuse.Replace))]   // a camera: fresh every time
    [InlineData(true, true, false, true, true, nameof(PopupReuse.Replace))]    // keeping was switched off
    [InlineData(true, false, true, true, true, nameof(PopupReuse.Replace))]    // keeping was switched on: the old one cannot be hidden
    [InlineData(true, true, true, false, true, nameof(PopupReuse.Replace))]    // another address
    [InlineData(true, true, true, true, false, nameof(PopupReuse.Replace))]    // another size
    public void A_window_comes_back_as_it_was_only_while_it_is_asked_for_the_same_way(
        bool thereIsOne, bool itKeepsItsPage, bool keepNow, bool sameAddress, bool sameSize, string expected)
    {
        Assert.Equal(expected, PopupWindows.Decide(thereIsOne, itKeepsItsPage, keepNow, sameAddress, sameSize).ToString());
    }

    private static readonly (string Id, string Name)[] Open =
    [
        ("custom_1", "Doorbell camera"),
        ("custom_2", "Player"),
        ("custom_3", "player ")
    ];

    [Theory]
    [InlineData("*")]
    [InlineData(" * ")]
    [InlineData("")]
    [InlineData(null)]
    public void A_close_that_names_no_command_is_for_every_window(string? target)
    {
        Assert.True(PopupWindows.IsForEveryWindow(target));
        Assert.Equal(["custom_1", "custom_2", "custom_3"], PopupWindows.Targets(Open, target));
    }

    [Fact]
    public void A_close_finds_its_windows_by_the_name_of_the_command_or_its_id()
    {
        Assert.Equal(["custom_1"], PopupWindows.Targets(Open, "doorbell CAMERA"));
        Assert.Equal(["custom_1"], PopupWindows.Targets(Open, " Doorbell camera "));
        Assert.Equal(["custom_2", "custom_3"], PopupWindows.Targets(Open, "Player"));
        Assert.Equal(["custom_3"], PopupWindows.Targets(Open, "CUSTOM_3"));
    }

    [Fact]
    public void A_close_for_a_command_that_has_no_window_closes_nothing()
    {
        Assert.False(PopupWindows.IsForEveryWindow("Garage"));
        Assert.Empty(PopupWindows.Targets(Open, "Garage"));
        Assert.Empty(PopupWindows.Targets([], "*"));
    }

    [Fact]
    public async Task A_popup_window_command_hands_over_what_its_window_is_like()
    {
        PopupWindowRequest? asked = null;
        SystemCommandService.WebViewHandler = request =>
        {
            asked = request;
            return true;
        };

        var result = await SystemCommandService.ExecuteCustomCommandAsync(new CustomCommandDefinition
        {
            Id = "custom_7",
            Name = "Player",
            Type = CustomCommandTypes.WebView,
            Command = "http://homeassistant.local:8123/lovelace/player",
            Arguments = "900x500 keep 10m"
        });

        Assert.Equal(CustomCommandOutcome.Done, result.Outcome);
        Assert.Equal(
            new PopupWindowRequest(
                "custom_7", "Player", "http://homeassistant.local:8123/lovelace/player",
                new PopupWindowOptions(new Size(900, 500), KeepLoaded: true, CloseAfter: TimeSpan.FromMinutes(10))),
            asked);
    }

    [Fact]
    public async Task A_popup_window_command_with_an_unreadable_window_opens_nothing()
    {
        var asked = false;
        SystemCommandService.WebViewHandler = _ => asked = true;

        var result = await SystemCommandService.ExecuteCustomCommandAsync(new CustomCommandDefinition
        {
            Type = CustomCommandTypes.WebView,
            Command = "http://homeassistant.local:8123",
            Arguments = "800x600 forever"
        });

        Assert.Equal(CustomCommandOutcome.InvalidSize, result.Outcome);
        Assert.Equal("800x600 forever", result.Detail);
        Assert.False(asked);
    }

    [Fact]
    public async Task A_close_command_hands_over_whose_window_it_is_for()
    {
        string? target = null;
        SystemCommandService.WebViewCloseHandler = name =>
        {
            target = name;
            return true;
        };
        var command = new CustomCommandDefinition { Type = CustomCommandTypes.WebViewClose, Command = "Doorbell camera" };

        var result = await SystemCommandService.ExecuteCustomCommandAsync(command);

        Assert.Equal(CustomCommandOutcome.Done, result.Outcome);
        Assert.Equal("Doorbell camera", target);
    }

    [Fact]
    public async Task Without_the_tray_app_there_is_no_window_to_open_or_close()
    {
        var open = new CustomCommandDefinition { Type = CustomCommandTypes.WebView, Command = "http://homeassistant.local:8123" };
        var close = new CustomCommandDefinition { Type = CustomCommandTypes.WebViewClose, Command = "*" };

        Assert.Equal(CustomCommandOutcome.NotStarted, (await SystemCommandService.ExecuteCustomCommandAsync(open)).Outcome);
        Assert.Equal(CustomCommandOutcome.NotStarted, (await SystemCommandService.ExecuteCustomCommandAsync(close)).Outcome);
    }

    [Fact]
    public void Closing_a_window_is_for_the_tray_app_only_like_opening_one()
    {
        var close = new CustomCommandDefinition { Type = "WebView_Close", Command = "*", Service = true };

        Assert.True(close.IsWebViewClose);
        Assert.True(close.NeedsUserSession);
        Assert.Equal(CustomCommandTypes.WebViewClose, CustomCommandTypes.Normalize("WEBVIEW_CLOSE"));
        Assert.True(CustomCommandTypes.NeedsUserSession(CustomCommandTypes.WebViewClose));
        Assert.All(
            CustomCommandTypes.InOrder,
            type => Assert.Equal(
                new CustomCommandDefinition { Type = type }.NeedsUserSession, CustomCommandTypes.NeedsUserSession(type)));
    }

    [Fact]
    public void Every_type_is_offered_once_and_has_its_texts()
    {
        Assert.Equal(CustomCommandTypes.All.Count, CustomCommandTypes.InOrder.Count);
        Assert.Equal(CustomCommandTypes.All.Order(), CustomCommandTypes.InOrder.Order());

        using var stream = typeof(Strings).Assembly.GetManifestResourceStream("lang.en.json")!;
        using var document = JsonDocument.Parse(stream);
        var english = document.RootElement.GetProperty("strings");
        Assert.All(CustomCommandTypes.InOrder, type =>
        {
            Assert.True(english.TryGetProperty($"CmdType.{type}", out _), $"CmdType.{type}");
            Assert.True(english.TryGetProperty($"CmdField.{type}", out _), $"CmdField.{type}");
            Assert.True(english.TryGetProperty($"CmdHint.{type}", out _), $"CmdHint.{type}");
        });
    }
}
