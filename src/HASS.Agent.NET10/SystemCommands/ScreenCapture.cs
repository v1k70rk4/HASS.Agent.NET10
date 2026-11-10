using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace HASS.Agent.Companion.SystemCommands;

/// <summary>
/// The screenshot the tray app takes when Home Assistant asks for one (the Screenshot
/// command, off until it is ticked): what is on the screen, as a JPEG small enough to send.
/// Nothing takes one on its own; there is no interval here.
/// </summary>
internal static class ScreenCapture
{
    public const int AllMonitors = 0;

    public const int DefaultMaxWidth = 1920;
    public const int SmallestWidth = 320;
    public const int LargestWidth = 7680;

    public const int DefaultQuality = 75;
    public const int LowestQuality = 30;
    public const int HighestQuality = 95;

    private const int QualityStep = 15;

    /// <summary>
    /// The part of the desktop to take: every monitor in one picture, or one of them, counted
    /// from 1 in the order Windows lists them. A number with no monitor behind it (one was
    /// unplugged) is every monitor, rather than nothing.
    /// </summary>
    public static Rectangle AreaOf(int monitor, IReadOnlyList<Rectangle> monitors, Rectangle all)
    {
        return monitor >= 1 && monitor <= monitors.Count ? monitors[monitor - 1] : all;
    }

    /// <summary>
    /// Takes the screenshot. Throws when the screen cannot be read: on the lock screen or the
    /// secure desktop of a UAC prompt Windows refuses it, and that is not worked around.
    /// </summary>
    public static Screenshot? Take(int monitor, int maxWidth, int quality, long maxBytes)
    {
        var area = AreaOf(
            monitor,
            Screen.AllScreens.Select(screen => screen.Bounds).ToList(),
            SystemInformation.VirtualScreen);
        if (area.Width <= 0 || area.Height <= 0)
        {
            throw new InvalidOperationException("There is no screen to take a picture of.");
        }

        using var shot = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(shot))
        {
            graphics.CopyFromScreen(area.Location, Point.Empty, area.Size, CopyPixelOperation.SourceCopy);
        }

        return Encode(shot, maxWidth, quality, maxBytes);
    }

    /// <summary>
    /// The picture as a JPEG of at most <paramref name="maxBytes"/>: no wider than asked, and
    /// when that is still too large, first at a lower quality and then smaller, a quarter
    /// narrower at a time. Null when even the smallest does not fit.
    /// </summary>
    public static Screenshot? Encode(Bitmap source, int maxWidth, int quality, long maxBytes)
    {
        var width = maxWidth > 0 ? Math.Min(source.Width, maxWidth) : source.Width;
        quality = Math.Clamp(quality, LowestQuality, HighestQuality);

        while (true)
        {
            var height = Math.Max(1, (int)Math.Round(source.Height * (double)width / source.Width));
            var jpeg = ToJpeg(source, width, height, quality);
            if (jpeg.Length <= maxBytes)
            {
                return new Screenshot(jpeg, width, height, quality);
            }

            if (quality > LowestQuality)
            {
                quality = Math.Max(LowestQuality, quality - QualityStep);
            }
            else if (width > SmallestWidth)
            {
                width = Math.Max(SmallestWidth, width * 3 / 4);
            }
            else
            {
                return null;
            }
        }
    }

    private static byte[] ToJpeg(Bitmap source, int width, int height, int quality)
    {
        var encoder = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
        using var stream = new MemoryStream();

        if (width == source.Width && height == source.Height)
        {
            source.Save(stream, encoder, parameters);
            return stream.ToArray();
        }

        using var scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var wrap = new ImageAttributes();
            // Without it the bicubic filter mixes the edge rows with nothing and leaves a dark line.
            wrap.SetWrapMode(WrapMode.TileFlipXY);
            graphics.DrawImage(
                source,
                new Rectangle(0, 0, width, height),
                0, 0, source.Width, source.Height,
                GraphicsUnit.Pixel,
                wrap);
        }

        scaled.Save(stream, encoder, parameters);
        return stream.ToArray();
    }
}

/// <summary>A screenshot ready to send: the JPEG, and what it came out as.</summary>
internal sealed record Screenshot(byte[] Jpeg, int Width, int Height, int Quality);
