using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
    // 50 megapixels: a phone's photo, but not a small file that claims a picture the size of
    // a house, which would take gigabytes to decode.
    private const long MaxPixels = 50_000_000;
    private const int MaxRedirects = 3;

    // Anyone who can send this PC a notification decides the picture's address: two fetches at
    // a time, so a burst of notifications cannot make the app hold dozens of large downloads.
    private static readonly SemaphoreSlim Fetches = new(2);

    // A notification stays in the Windows notification centre for three days and shows
    // its picture from the file for as long.
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(4);
    // An address that is not reachable from here (Home Assistant's internal one, from
    // outside) has to fail fast: the next one is tried, and the notification waits.
    // Redirects are followed by hand, so that each address is checked like the first one.
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(3),
        AllowAutoRedirect = false,
        ConnectCallback = ConnectAsync,
    })
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

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
               !string.IsNullOrWhiteSpace(notification.Data?.ImagePath) ||
               !string.IsNullOrWhiteSpace(notification.Data?.ImageAlt);
    }

    /// <summary>The picture as a local file, or null when there is none or it cannot be had.</summary>
    public async Task<string?> FetchAsync(NotificationPayload notification, CancellationToken cancellationToken)
    {
        try
        {
            await Fetches.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            return await FetchOneAsync(notification, cancellationToken);
        }
        finally
        {
            Fetches.Release();
        }
    }

    private async Task<string?> FetchOneAsync(NotificationPayload notification, CancellationToken cancellationToken)
    {
        _ownHaHost = Uri.TryCreate(_settings.HaApiUrl, UriKind.Absolute, out var own) ? own.Host : null;
        foreach (var address in Candidates(notification))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var bytes = await DownloadAsync(address, cancellationToken);
                if (bytes is null)
                {
                    continue;
                }

                var (converted, extension) = await ConvertAsync(bytes, cancellationToken);
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
                var why = ex is OperationCanceledException
                    ? "no answer in time"
                    : string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
                _log.Warning($"Notification picture not available from {address.GetLeftPart(UriPartial.Path)}: {why}");
            }
        }

        return null;
    }

    /// <summary>
    /// Where to look for the picture, best first: the Home Assistant path on this PC's own
    /// Home Assistant address, then the address the notification gives, then its second one.
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

        if (Uri.TryCreate(data.ImageAlt?.Trim(), UriKind.Absolute, out var other) && IsWeb(other) && other != given)
        {
            yield return other;
        }
    }

    private static bool IsWeb(Uri address) => address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps;

    private static bool IsRedirect(HttpStatusCode status) => (int)status is >= 300 and < 400 and not 304;

    // Home Assistant's own address from the settings: allowed even on this PC itself.
    private static volatile string? _ownHaHost;

    // The picture may be on the LAN (Home Assistant, a camera), so the LAN stays allowed. Not
    // allowed: this PC itself, by any of its addresses, where local services trust a request
    // from the same machine (unless Home Assistant is set up there), and link-local IPv4
    // (169.254.x.x). Checked on
    // the address actually connected to, so neither a name that resolves there nor a redirect
    // gets round it.
    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        var isOwnHa = string.Equals(context.DnsEndPoint.Host, _ownHaHost, StringComparison.OrdinalIgnoreCase);
        var allowed = addresses.Where(address => IsAllowedTarget(address, allowThisPc: isOwnHa)).ToArray();
        if (allowed.Length == 0)
        {
            throw new HttpRequestException($"{context.DnsEndPoint.Host} is this PC or a link-local address");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // This PC's own LAN addresses count as this PC: a service listening on every interface
    // may trust a request that comes from one of the machine's own addresses.
    private static bool IsThisPcsAddress(IPAddress address)
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
                .Any(unicast => unicast.Address.Equals(address)
                    || (unicast.Address.IsIPv4MappedToIPv6 && unicast.Address.MapToIPv4().Equals(address)));
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    internal static bool IsAllowedTarget(IPAddress address, bool allowThisPc = false)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (!allowThisPc && (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || IsThisPcsAddress(address)))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return !(address.AddressFamily == AddressFamily.InterNetwork && bytes[0] == 169 && bytes[1] == 254);
    }

    private static async Task<byte[]?> DownloadAsync(Uri address, CancellationToken cancellationToken)
    {
        var response = await Http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        for (var hop = 0; IsRedirect(response.StatusCode) && hop < MaxRedirects; hop++)
        {
            var next = response.Headers.Location is { } location ? new Uri(address, location) : null;
            response.Dispose();
            if (next is null || !IsWeb(next) || (address.Scheme == Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException("redirected to an address that is not allowed");
            }

            address = next;
            response = await Http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }

        using var _ = response;
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxDownloadBytes)
        {
            throw new InvalidOperationException($"larger than {MaxDownloadBytes / (1024 * 1024)} MB");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
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
    private static async Task<(byte[] Bytes, string Extension)> ConvertAsync(byte[] source, CancellationToken cancellationToken)
    {
        using var input = new InMemoryRandomAccessStream();
        await input.WriteAsync(source.AsBuffer());
        input.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(input).AsTask(cancellationToken);
        if ((long)decoder.PixelWidth * decoder.PixelHeight > MaxPixels)
        {
            throw new InvalidOperationException($"{decoder.PixelWidth} x {decoder.PixelHeight} pixels is more than a notification needs");
        }

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
            ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);

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

    // Once an hour is plenty for files kept for days, and a run of notifications with
    // pictures does not list the whole folder again for each one.
    private static readonly TimeSpan CleanUpEvery = TimeSpan.FromHours(1);
    private static long _lastCleanUpTicks;

    private void RemoveOldFiles()
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastCleanUpTicks);
        if (now - last < CleanUpEvery.Ticks || Interlocked.CompareExchange(ref _lastCleanUpTicks, now, last) != last)
        {
            return;
        }

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
