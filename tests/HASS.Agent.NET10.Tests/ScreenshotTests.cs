using System.ComponentModel;
using System.Drawing.Imaging;
using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Localization;
using HASS.Agent.Companion.Mqtt;
using HASS.Agent.Companion.SystemCommands;

namespace HASS.Agent.Companion.Tests;

/// <summary>
/// The screenshot the tray app takes when Home Assistant asks for one: off until it is ticked,
/// the tray app's alone, and a JPEG that fits where it is sent.
/// </summary>
public sealed class ScreenshotTests
{
    [Fact]
    public void The_command_is_off_until_it_is_ticked_and_is_not_the_service_s()
    {
        var command = Assert.Single(SystemCommandCatalog.Commands, c => c.Name == SystemCommandCatalog.Screenshot);
        Assert.True(command.SupportsTrayApp);
        Assert.False(command.SupportsService);

        Assert.DoesNotContain(SystemCommandCatalog.Screenshot, SystemCommandCatalog.DefaultTrayAppCommands);
        Assert.DoesNotContain(SystemCommandCatalog.Screenshot, SystemCommandCatalog.ServiceCapableCommands);

        // A new install, and one whose settings were written before the command existed.
        var settings = new CompanionSettings();
        settings.Normalize();
        Assert.False(settings.IsTrayAppCommandEnabled(SystemCommandCatalog.Screenshot));
    }

    [Fact]
    public void Written_into_the_service_s_list_by_hand_it_is_taken_out()
    {
        var settings = new CompanionSettings();
        settings.TrayAppCommands.Add(SystemCommandCatalog.Screenshot);
        settings.ServiceCommands.Add(SystemCommandCatalog.Screenshot);

        settings.Normalize();

        Assert.True(settings.IsTrayAppCommandEnabled(SystemCommandCatalog.Screenshot));
        Assert.False(settings.IsServiceCommandEnabled(SystemCommandCatalog.Screenshot));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("hu")]
    [InlineData("de")]
    [InlineData("ru")]
    [InlineData("zh-Hans")]
    public void The_command_has_a_name_in_every_language(string language)
    {
        var name = Strings.Get("Cmd.screenshot", language);

        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.NotEqual("Cmd.screenshot", name);
    }

    [Theory]
    [InlineData(-3, 1920, 0, 0, 1920, 75)]
    [InlineData(99, 100, 50000, 16, 320, 95)]
    [InlineData(2, 0, 10, 2, 0, 30)]
    [InlineData(1, 1280, 0, 1, 1280, 75)]
    public void What_is_set_for_it_is_kept_within_what_makes_a_picture(
        int monitor, int maxWidth, int quality, int expectedMonitor, int expectedWidth, int expectedQuality)
    {
        var settings = new CompanionSettings
        {
            ScreenshotMonitor = monitor,
            ScreenshotMaxWidth = maxWidth,
            ScreenshotQuality = quality
        };

        settings.Normalize();

        Assert.Equal(expectedMonitor, settings.ScreenshotMonitor);
        Assert.Equal(expectedWidth, settings.ScreenshotMaxWidth);
        Assert.Equal(expectedQuality, settings.ScreenshotQuality);
    }

    [Fact]
    public void One_monitor_is_taken_by_its_number_and_every_monitor_otherwise()
    {
        var left = new Rectangle(-1920, 0, 1920, 1080);
        var main = new Rectangle(0, 0, 2560, 1440);
        var all = Rectangle.Union(left, main);
        Rectangle[] monitors = [main, left];

        Assert.Equal(all, ScreenCapture.AreaOf(ScreenCapture.AllMonitors, monitors, all));
        Assert.Equal(main, ScreenCapture.AreaOf(1, monitors, all));
        Assert.Equal(left, ScreenCapture.AreaOf(2, monitors, all));
        // The third one was unplugged: everything, rather than nothing.
        Assert.Equal(all, ScreenCapture.AreaOf(3, monitors, all));
    }

    [Fact]
    public void A_picture_is_made_no_wider_than_asked_and_keeps_its_shape()
    {
        using var desktop = Noise(2560, 1440);

        var shot = ScreenCapture.Encode(desktop, maxWidth: 1280, quality: 75, maxBytes: long.MaxValue);

        Assert.NotNull(shot);
        Assert.Equal((1280, 720, 75), (shot.Width, shot.Height, shot.Quality));
        AssertJpegOf(shot, 1280, 720);
    }

    [Fact]
    public void A_picture_smaller_than_the_limit_is_left_as_large_as_it_is()
    {
        using var desktop = Noise(800, 600);

        var shot = ScreenCapture.Encode(desktop, maxWidth: 1920, quality: 75, maxBytes: long.MaxValue);

        Assert.NotNull(shot);
        AssertJpegOf(shot, 800, 600);
    }

    [Fact]
    public void A_picture_too_large_to_send_loses_quality_first_and_then_size()
    {
        // Noise is the worst a JPEG can be given: it does not get small by quality alone.
        using var desktop = Noise(1920, 1080);
        var asIs = ScreenCapture.Encode(desktop, maxWidth: 1920, quality: 75, maxBytes: long.MaxValue);
        Assert.NotNull(asIs);

        var limit = asIs.Jpeg.Length / 6;
        var shot = ScreenCapture.Encode(desktop, maxWidth: 1920, quality: 75, maxBytes: limit);

        Assert.NotNull(shot);
        Assert.True(shot.Jpeg.Length <= limit);
        Assert.Equal(ScreenCapture.LowestQuality, shot.Quality);
        Assert.True(shot.Width < 1920);
        Assert.Equal(16.0 / 9, (double)shot.Width / shot.Height, 2);
        AssertJpegOf(shot, shot.Width, shot.Height);
    }

    [Fact]
    public void A_picture_that_fits_nowhere_is_not_sent_at_all()
    {
        using var desktop = Noise(1920, 1080);

        Assert.Null(ScreenCapture.Encode(desktop, maxWidth: 1920, quality: 75, maxBytes: 200));
    }

    [Fact]
    public void The_broker_s_own_limit_counts_when_it_is_the_lower_one()
    {
        var own = MqttCompanionService.PayloadLimit(null);
        Assert.Equal(512 * 1024, own);

        Assert.Equal(own, MqttCompanionService.PayloadLimit(256 * 1024 * 1024));
        Assert.True(MqttCompanionService.PayloadLimit(128 * 1024) < 128 * 1024);
        Assert.Equal(0, MqttCompanionService.PayloadLimit(16));
    }

    [Fact]
    public void The_screen_of_this_pc_makes_a_picture_that_can_be_sent()
    {
        // The real thing, where there is a desktop to take: a build server may have none.
        Assert.SkipUnless(SystemInformation.UserInteractive && Screen.AllScreens.Length > 0, "No desktop to take a picture of.");

        Screenshot? shot;
        try
        {
            shot = ScreenCapture.Take(ScreenCapture.AllMonitors, ScreenCapture.DefaultMaxWidth, ScreenCapture.DefaultQuality, MqttCompanionService.PayloadLimit(null));
        }
        catch (Win32Exception ex)
        {
            // A locked screen, or a session with no desktop of its own.
            Assert.Skip($"The screen cannot be read here: {ex.Message}");
            return;
        }

        Assert.NotNull(shot);
        Assert.InRange(shot.Width, 1, ScreenCapture.DefaultMaxWidth);
        Assert.True(shot.Jpeg.Length <= MqttCompanionService.PayloadLimit(null));
        AssertJpegOf(shot, shot.Width, shot.Height);
    }

    private static void AssertJpegOf(Screenshot shot, int width, int height)
    {
        Assert.Equal([0xFF, 0xD8, 0xFF], shot.Jpeg.Take(3));
        using var stream = new MemoryStream(shot.Jpeg);
        using var image = Image.FromStream(stream);
        Assert.Equal(ImageFormat.Jpeg.Guid, image.RawFormat.Guid);
        Assert.Equal((width, height), (image.Width, image.Height));
    }

    /// <summary>A bitmap of random pixels, the same ones every time.</summary>
    private static Bitmap Noise(int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var bytes = new byte[data.Stride * height];
            new Random(12345).NextBytes(bytes);
            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }
}
