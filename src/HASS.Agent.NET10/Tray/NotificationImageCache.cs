using System.Runtime.InteropServices.WindowsRuntime;
using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Http;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.Runtime;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace HASS.Agent.Companion.Tray;

/// <summary>
/// Fetches the picture of a notification and keeps it as a local file: a Windows
/// notification of a desktop app can only show a file, not a web address, and the app's
/// own window shows the same file.
/// </summary>
internal sealed class NotificationImageCache
{
    private const long MaxDownloadBytes = 15 * 1024 * 1024;
    private const int MaxEdge = 1024;
    private const float JpegQuality = 0.9f;

    // A notification stays in the Windows notification centre for three days and shows
    // its picture from the file for as long.
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(4);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private readonly CompanionSettings _settings;
    private readonly FileLog _log;
    private readonly string _directory;

    public NotificationImageCache(CompanionSettings settings, FileLog log)
    {
        _settings = settings;
        _log = log;
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppIdentity.ConfigurationDirectoryName,
            "Notifications");
    }

    public static bool HasImage(NotificationPayload notification)
    {
        return !string.IsNullOrWhiteSpace(notification.Data?.Image) ||
               !string.IsNullOrWhiteSpace(notification.Data?.ImagePath);
    }

    /// <summary>The picture as a local file, or null when there is none or it cannot be had.</summary>
    public async Task<string?> FetchAsync(NotificationPayload notification)
    {
        foreach (var address in Candidates(notification))
        {
            try
            {
                var bytes = await DownloadAsync(address);
                if (bytes is null)
                {
                    continue;
                }

                var (converted, extension) = await ConvertAsync(bytes);
                Directory.CreateDirectory(_directory);
                RemoveOldFiles();

                var file = Path.Combine(_directory, $"{Guid.NewGuid():N}.{extension}");
                await File.WriteAllBytesAsync(file, converted);
                _log.Debug($"Notification picture: {bytes.Length / 1024} KB from {address.Host} -> {converted.Length / 1024} KB.");
                return file;
            }
            catch (Exception ex)
            {
                // The address may carry a signature; the log gets the host and path only.
                var why = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
                _log.Warning($"Notification picture not available from {address.GetLeftPart(UriPartial.Path)}: {why}");
            }
        }

        return null;
    }

    /// <summary>
    /// Where to look for the picture, best first: the Home Assistant path on this PC's own
    /// Home Assistant address, then the address the notification gives.
    /// </summary>
    private IEnumerable<Uri> Candidates(NotificationPayload notification)
    {
        var data = notification.Data;
        if (data is null)
        {
            yield break;
        }

        var ownBase = _settings.HaApiEnabled ? _settings.HaApiUrl : string.Empty;
        var image = data.Image?.Trim() ?? string.Empty;
        var path = data.ImagePath?.Trim() ?? string.Empty;

        // An integration older than 10.9.0 passes a Home Assistant path on as it is.
        if (path.Length == 0 && image.StartsWith('/'))
        {
            path = image;
        }

        if (path.StartsWith('/') && !string.IsNullOrEmpty(ownBase) &&
            Uri.TryCreate(ownBase + path, UriKind.Absolute, out var own) && IsWeb(own))
        {
            yield return own;
        }

        if (Uri.TryCreate(image, UriKind.Absolute, out var given) && IsWeb(given))
        {
            yield return given;
        }
    }

    private static bool IsWeb(Uri address) => address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps;

    private static async Task<byte[]?> DownloadAsync(Uri address)
    {
        using var response = await Http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxDownloadBytes)
        {
            throw new InvalidOperationException($"larger than {MaxDownloadBytes / (1024 * 1024)} MB");
        }

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk)) > 0)
        {
            if (buffer.Length + read > MaxDownloadBytes)
            {
                throw new InvalidOperationException($"larger than {MaxDownloadBytes / (1024 * 1024)} MB");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.Length == 0 ? null : buffer.ToArray();
    }

    /// <summary>
    /// Decodes whatever arrived with the codecs Windows has, scales it down to what a
    /// notification can use, and writes it as JPEG, or as PNG when it has transparency.
    /// </summary>
    private static async Task<(byte[] Bytes, string Extension)> ConvertAsync(byte[] source)
    {
        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(source.AsBuffer());
        input.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(input);
        var hasAlpha = decoder.BitmapAlphaMode != BitmapAlphaMode.Ignore;
        var scale = Math.Min(1.0, (double)MaxEdge / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
            ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
            InterpolationMode = BitmapInterpolationMode.Fant
        };

        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            hasAlpha ? BitmapAlphaMode.Premultiplied : BitmapAlphaMode.Ignore,
            transform,
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);

        using var output = new InMemoryRandomAccessStream();
        var encoder = hasAlpha
            ? await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output)
            : await BitmapEncoder.CreateAsync(
                BitmapEncoder.JpegEncoderId,
                output,
                new BitmapPropertySet
                {
                    ["ImageQuality"] = new BitmapTypedValue(JpegQuality, Windows.Foundation.PropertyType.Single)
                });
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();

        output.Seek(0);
        var bytes = new byte[output.Size];
        using var reader = new DataReader(output);
        await reader.LoadAsync((uint)output.Size);
        reader.ReadBytes(bytes);
        return (bytes, hasAlpha ? "png" : "jpg");
    }

    private void RemoveOldFiles()
    {
        try
        {
            var limit = DateTime.UtcNow - KeepFor;
            foreach (var file in Directory.EnumerateFiles(_directory))
            {
                if (File.GetLastWriteTimeUtc(file) < limit)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Debug($"Unable to clean up old notification pictures: {ex.Message}");
        }
    }
}
