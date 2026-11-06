using HASS.Agent.Companion.Tray;

namespace HASS.Agent.Companion.Tests;

/// <summary>
/// The zoom levels kept for the embedded browser's pages: what is kept, what is not, and
/// a file that is not what it should be.
/// </summary>
public class WebViewZoomTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("hass-agent-zoom-").FullName;

    private string File => Path.Combine(_folder, "webview-zoom.json");

    [Fact]
    public void A_zoom_is_there_again_for_the_same_address_only()
    {
        new WebViewZoom(File).Set("http://ha.local:8123/lovelace/popup", 1.25);

        var again = new WebViewZoom(File);

        Assert.Equal(1.25, again.Get("http://ha.local:8123/lovelace/popup"));
        Assert.Null(again.Get("http://ha.local:8123/lovelace/other"));
        Assert.Null(again.Get("http://HA.local:8123/lovelace/popup"));
    }

    [Fact]
    public void Every_web_view_of_the_app_shares_one_store()
    {
        // Two windows with their own copies would write over each other's change; the
        // app's store is one, however many windows ask for it. Nothing is written here.
        Assert.Same(WebViewZoom.ForUser(null), WebViewZoom.ForUser(null));
    }

    [Fact]
    public void The_default_zoom_is_forgotten_and_an_empty_file_goes()
    {
        var zoom = new WebViewZoom(File);
        zoom.Set("http://ha.local/a", 1.5);
        zoom.Set("http://ha.local/b", 0.8);

        zoom.Set("http://ha.local/a", 1.0);
        Assert.Null(new WebViewZoom(File).Get("http://ha.local/a"));
        Assert.Equal(0.8, new WebViewZoom(File).Get("http://ha.local/b"));

        zoom.Set("http://ha.local/b", 1.0);
        Assert.False(System.IO.File.Exists(File));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(0.1)]
    [InlineData(50.0)]
    [InlineData(double.NaN)]
    public void A_zoom_outside_what_the_browser_allows_is_not_kept(double level)
    {
        var zoom = new WebViewZoom(File);

        zoom.Set("http://ha.local/a", level);

        Assert.Null(zoom.Get("http://ha.local/a"));
        Assert.False(System.IO.File.Exists(File));
    }

    [Fact]
    public void A_file_that_is_not_what_it_should_be_starts_over()
    {
        System.IO.File.WriteAllText(File, "{ not json");
        Assert.Null(new WebViewZoom(File).Get("http://ha.local/a"));

        System.IO.File.WriteAllText(File, """{ "http://ha.local/a": 2.0, "http://ha.local/b": 99, "": 1.5, "http://ha.local/c": "x" }""");
        var zoom = new WebViewZoom(File);
        Assert.Null(zoom.Get("http://ha.local/a"));
        Assert.Null(zoom.Get("http://ha.local/b"));
    }

    [Fact]
    public void A_level_that_cannot_be_read_as_such_drops_only_what_is_wrong()
    {
        System.IO.File.WriteAllText(File, """{ "http://ha.local/a": 2.0, "http://ha.local/b": 99, "": 1.5 }""");

        var zoom = new WebViewZoom(File);

        Assert.Equal(2.0, zoom.Get("http://ha.local/a"));
        Assert.Null(zoom.Get("http://ha.local/b"));
        Assert.Null(zoom.Get(""));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
