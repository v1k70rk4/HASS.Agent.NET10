using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text.Json;
using HASS.Agent.Companion.Logging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Control;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace HASS.Agent.Companion.Media;

internal sealed class MediaSessionService : IDisposable
{
    private readonly FileLog _log;
    private readonly AudioEndpointService _audioEndpoint;
    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private MediaPlayer? _localPlayer;
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private Func<MediaStateMessage, Task>? _publishState;
    private Func<byte[]?, Task>? _publishThumbnail;
    private string? _lastThumbnailHash;

    // Start and stop arrive from several places (the MQTT and the HA API path start the
    // monitor, shutdown stops it from the service's StopAsync and from the connection loop
    // unwinding at the same moment), so each transition runs to its end before the next.
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    // The audio endpoint is shared with SystemMetricsService: two instances would
    // each create their own WASAPI COM objects and deadlock against each other
    // during an audio device change (e.g. monitor power-off dropping HDMI audio).
    public MediaSessionService(FileLog log, AudioEndpointService audioEndpoint)
    {
        _log = log;
        _audioEndpoint = audioEndpoint;
    }

    public async Task StartAsync(Func<MediaStateMessage, Task> publishState, Func<byte[]?, Task>? publishThumbnail, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (_worker is not null)
            {
                return;
            }

            _publishState = publishState;
            _publishThumbnail = publishThumbnail;
            _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _localPlayer = new MediaPlayer
            {
                AutoPlay = false,
                IsLoopingEnabled = false
            };

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // The token, not the field: the worker must not depend on what _cts holds by the
            // time it gets to run.
            var token = _cts.Token;
            _worker = Task.Run(() => MonitorAsync(token), CancellationToken.None);
            _log.Info("Media session monitor started.");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        // Two stops used to get past the null check together, and the second crashed the app
        // on exit when it reached Dispose on a field the first one had already cleared.
        // A free gate is taken synchronously, so nothing changes for a lone caller; a caller
        // that had to wait resumes on the thread pool, because Dispose() blocks on this
        // method and a continuation queued to that blocked thread would never run.
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            var cts = _cts;
            if (cts is null)
            {
                return;
            }

            _cts = null;
            await cts.CancelAsync();

            if (_worker is not null)
            {
                try
                {
                    await _worker;
                }
                catch (OperationCanceledException)
                {
                    // Expected during shutdown.
                }
            }

            _localPlayer?.Dispose();
            _localPlayer = null;
            _sessionManager = null;
            cts.Dispose();
            _worker = null;
            _publishThumbnail = null;
            _lastThumbnailHash = null;
            _log.Info("Media session monitor stopped.");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task HandleCommandAsync(MediaCommand command)
    {
        var commandName = command.Command?.Trim().ToLowerInvariant();
        var session = GetCurrentSession();

        switch (commandName)
        {
            case "volumeup":
                MediaKeySender.VolumeUp();
                break;
            case "volumedown":
                MediaKeySender.VolumeDown();
                break;
            case "setvolume":
                if (TryGetInt(command.Data, out var volume))
                {
                    _audioEndpoint.SetVolume(volume);
                }
                break;
            case "mute":
                if (TryGetBool(command.Data, out var muted))
                {
                    _audioEndpoint.SetMuted(muted);
                }
                else
                {
                    MediaKeySender.MuteToggle();
                }
                break;
            case "play":
                if (session is not null)
                {
                    await session.TryPlayAsync();
                }
                else
                {
                    MediaKeySender.PlayPause();
                }
                break;
            case "pause":
                if (session is not null)
                {
                    await session.TryPauseAsync();
                }
                else
                {
                    MediaKeySender.PlayPause();
                }
                break;
            case "stop":
                if (session is not null)
                {
                    await session.TryStopAsync();
                }
                else
                {
                    MediaKeySender.Stop();
                }
                break;
            case "next":
                if (session is not null)
                {
                    await session.TrySkipNextAsync();
                }
                else
                {
                    MediaKeySender.Next();
                }
                break;
            case "previous":
                if (session is not null)
                {
                    await session.TrySkipPreviousAsync();
                }
                else
                {
                    MediaKeySender.Previous();
                }
                break;
            case "seek":
                if (session is not null && TryGetDouble(command.Data, out var seconds))
                {
                    await session.TryChangePlaybackPositionAsync((long)TimeSpan.FromSeconds(seconds).TotalMilliseconds * 10_000);
                }
                break;
            case "playmedia":
                if (command.Data is not null)
                {
                    PlayMedia(command.Data.ToString());
                }
                break;
            default:
                _log.Warning($"Unsupported media command received: {command.Command ?? "[null]"}");
                break;
        }
    }

    public Task<MediaStateMessage> GetCurrentStateAsync() => BuildStateMessageAsync();

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var message = await BuildStateMessageAsync();
                if (_publishState is not null)
                {
                    await _publishState(message);
                }

                if (_publishThumbnail is not null)
                {
                    var session = GetCurrentSession();
                    var thumbnail = session is not null ? await ReadThumbnailAsync(session) : null;
                    var hash = thumbnail is not null
                        ? Convert.ToHexString(SHA256.HashData(thumbnail))
                        : null;

                    // The hash is taken on the artwork as Windows holds it, so a cover that
                    // has not changed is not decoded and re-encoded every cycle.
                    if (hash != _lastThumbnailHash)
                    {
                        _lastThumbnailHash = hash;
                        await _publishThumbnail(thumbnail is null ? null : await ShrinkThumbnailAsync(thumbnail, _log));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warning($"Unable to publish media state: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    // The artwork goes out as it is when it is already small. Anything bigger is scaled to
    // this edge and re-encoded as JPEG: a browser hands over the full-size cover, which
    // can run to megabytes, and Mosquitto 2.1 drops any packet over 2 MB by default.
    private const int MaxThumbnailEdge = 512;
    private const int MaxThumbnailBytesAsIs = 128 * 1024;
    private const float ThumbnailJpegQuality = 0.85f;

    private async Task<byte[]?> ReadThumbnailAsync(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var mediaProperties = await session.TryGetMediaPropertiesAsync();
            if (mediaProperties?.Thumbnail is not { } thumbnailRef)
                return null;

            using var stream = await thumbnailRef.OpenReadAsync();
            if (stream.Size == 0)
                return null;

            return await ReadAllBytesAsync(stream);
        }
        catch (Exception ex)
        {
            _log.Warning($"Unable to read media thumbnail: {ex.Message}");
            return null;
        }
    }

    internal static async Task<byte[]?> ShrinkThumbnailAsync(byte[] artwork, FileLog log)
    {
        try
        {
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await stream.WriteAsync(artwork.AsBuffer());
            stream.Seek(0);

            BitmapDecoder? decoder = null;
            try
            {
                decoder = await BitmapDecoder.CreateAsync(stream);
            }
            catch (Exception ex)
            {
                // A format Windows cannot decode (no codec installed). Small enough, it is
                // still worth sending as it is; otherwise there is nothing to be done with it.
                if (stream.Size > MaxThumbnailBytesAsIs)
                {
                    var why = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
                    log.Warning($"Media thumbnail skipped: {stream.Size / 1024} KB and not decodable ({why}).");
                    return null;
                }
            }

            if (decoder is null ||
                (stream.Size <= MaxThumbnailBytesAsIs &&
                 decoder.PixelWidth <= MaxThumbnailEdge &&
                 decoder.PixelHeight <= MaxThumbnailEdge))
            {
                stream.Seek(0);
                return await ReadAllBytesAsync(stream);
            }

            var scale = Math.Min(1.0, (double)MaxThumbnailEdge / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale)),
                ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale)),
                InterpolationMode = BitmapInterpolationMode.Fant
            };

            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);

            using var output = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(
                BitmapEncoder.JpegEncoderId,
                output,
                new BitmapPropertySet
                {
                    ["ImageQuality"] = new BitmapTypedValue(ThumbnailJpegQuality, PropertyType.Single)
                });
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();

            output.Seek(0);
            var bytes = await ReadAllBytesAsync(output);
            log.Debug($"Media thumbnail {decoder.PixelWidth}x{decoder.PixelHeight}, {stream.Size / 1024} KB -> {transform.ScaledWidth}x{transform.ScaledHeight} JPEG, {bytes.Length / 1024} KB.");
            return bytes;
        }
        catch (Exception ex)
        {
            // Sent as it is when small enough; a big one that cannot be converted is dropped.
            log.Warning($"Unable to shrink media thumbnail: {ex.Message}");
            return artwork.Length <= MaxThumbnailBytesAsIs ? artwork : null;
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(Windows.Storage.Streams.IRandomAccessStream stream)
    {
        var size = (uint)stream.Size;
        using var reader = new Windows.Storage.Streams.DataReader(stream);
        await reader.LoadAsync(size);
        var bytes = new byte[size];
        reader.ReadBytes(bytes);
        reader.DetachStream();
        return bytes;
    }

    private async Task<MediaStateMessage> BuildStateMessageAsync()
    {
        var session = GetCurrentSession();
        if (session is null)
        {
            return new MediaStateMessage
            {
                State = "off",
                Volume = _audioEndpoint.GetVolume(),
                Muted = _audioEndpoint.GetMuted()
            };
        }

        var mediaProperties = await session.TryGetMediaPropertiesAsync();
        var playbackInfo = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();

        return new MediaStateMessage
        {
            State = ConvertState(playbackInfo?.PlaybackStatus),
            Title = mediaProperties?.Title,
            Artist = mediaProperties?.Artist,
            AlbumArtist = mediaProperties?.AlbumArtist,
            AlbumTitle = mediaProperties?.AlbumTitle,
            Duration = Math.Max(0, (timeline.EndTime - timeline.StartTime).TotalSeconds),
            CurrentPosition = Math.Max(0, timeline.Position.TotalSeconds),
            Volume = _audioEndpoint.GetVolume(),
            Muted = _audioEndpoint.GetMuted()
        };
    }

    private GlobalSystemMediaTransportControlsSession? GetCurrentSession()
    {
        var sessions = _sessionManager?.GetSessions();
        if (sessions is null || sessions.Count == 0)
        {
            return null;
        }

        return sessions.FirstOrDefault(session =>
            session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) ?? sessions[0];
    }

    private void PlayMedia(string? mediaUri)
    {
        if (string.IsNullOrWhiteSpace(mediaUri))
        {
            return;
        }

        if (!Uri.TryCreate(mediaUri, UriKind.Absolute, out var uri)
            || uri.Scheme is not "http" and not "https")
        {
            _log.Warning($"Rejected invalid playmedia URI: {mediaUri}");
            return;
        }

        _localPlayer ??= new MediaPlayer();
        _localPlayer.Source = MediaSource.CreateFromUri(uri);
        _localPlayer.Play();
    }

    private static string ConvertState(GlobalSystemMediaTransportControlsSessionPlaybackStatus? status)
    {
        return status switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "playing",
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "paused",
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => "off",
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => "off",
            _ => "idle"
        };
    }

    private static bool TryGetInt(object? value, out int result)
    {
        if (value is JsonElement json && json.ValueKind == JsonValueKind.Number)
        {
            return json.TryGetInt32(out result);
        }

        return int.TryParse(value?.ToString(), out result);
    }

    private static bool TryGetDouble(object? value, out double result)
    {
        if (value is JsonElement json && json.ValueKind == JsonValueKind.Number)
        {
            return json.TryGetDouble(out result);
        }

        return double.TryParse(value?.ToString(), out result);
    }

    private static bool TryGetBool(object? value, out bool result)
    {
        if (value is JsonElement json)
        {
            if (json.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                result = json.GetBoolean();
                return true;
            }
        }

        return bool.TryParse(value?.ToString(), out result);
    }
}
