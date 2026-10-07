using System.Drawing;
using HASS.Agent.Companion.SystemCommands;

namespace HASS.Agent.Companion.Tests;

public class KeyCombinationTests
{
    private const ushort Ctrl = 0x11, Shift = 0x10, Win = 0x5B;

    [Fact]
    public void One_combination()
    {
        Assert.True(KeySender.TryParse("win+r", out var combinations, out _));

        Assert.Equal([Win, (ushort)'R'], Assert.Single(combinations));
    }

    [Fact]
    public void Several_combinations_separated_by_spaces_or_commas()
    {
        Assert.True(KeySender.TryParse("ctrl+c, ctrl+v  ctrl+shift+esc", out var combinations, out _));

        Assert.Equal(3, combinations.Count);
        Assert.Equal([Ctrl, Shift, (ushort)0x1B], combinations[2]);
    }

    [Fact]
    public void Key_names_ignore_case()
    {
        Assert.True(KeySender.TryParse("CTRL+Alt+Delete", out var combinations, out _));

        Assert.Equal(3, Assert.Single(combinations).Count);
    }

    [Fact]
    public void Unknown_key_is_named()
    {
        Assert.False(KeySender.TryParse("ctrl+banana", out _, out var unknown));

        Assert.Equal("banana", unknown);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+ ,")]
    public void Nothing_to_press(string text)
    {
        Assert.False(KeySender.TryParse(text, out _, out _));
    }

    [Fact]
    public void Hotkey_needs_a_modifier_and_one_key()
    {
        Assert.True(KeySender.TryParseHotkey("ctrl+alt+h", out var modifiers, out var key));
        Assert.Equal(0x0003u, modifiers); // MOD_ALT | MOD_CONTROL
        Assert.Equal('H', (char)key);

        Assert.True(KeySender.TryParseHotkey("win+f12", out modifiers, out _));
        Assert.Equal(0x0008u, modifiers);
    }

    [Theory]
    [InlineData("h")]                  // no modifier
    [InlineData("ctrl")]               // no key
    [InlineData("ctrl+a+b")]           // two keys
    [InlineData("ctrl+a ctrl+b")]      // two combinations
    [InlineData("ctrl+nonsense")]
    public void Not_a_hotkey(string text)
    {
        Assert.False(KeySender.TryParseHotkey(text, out _, out _));
    }
}

public class PopupSizeTests
{
    [Theory]
    [InlineData("1024x720", 1024, 720)]
    [InlineData("800 X 600", 800, 600)]
    [InlineData("640*480", 640, 480)]
    [InlineData("1920×1080", 1920, 1080)]
    public void Size_is_read_in_several_spellings(string text, int width, int height)
    {
        Assert.True(WebViewOptions.TryParseSize(text, out var size));
        Assert.Equal(new Size(width, height), size);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1024")]
    [InlineData("100x100")]     // under the minimum
    [InlineData("5000x800")]    // over the maximum
    [InlineData("wide x tall")]
    public void Invalid_size_gives_the_default(string text)
    {
        Assert.False(WebViewOptions.TryParseSize(text, out var size));
        Assert.Equal(WebViewOptions.DefaultWindowSize, size);
    }

    [Theory]
    [InlineData("https://ha.local:8123/lovelace/0", true)]
    [InlineData("http://192.168.1.10:8123", true)]
    [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ha.local", false)]
    public void Only_web_addresses_open_in_the_embedded_browser(string address, bool allowed)
    {
        Assert.Equal(allowed, WebViewOptions.IsWebAddress(address));
    }
}
