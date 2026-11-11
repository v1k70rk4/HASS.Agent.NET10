using System.Reflection;
using HASS.Agent.Companion.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Models;

namespace HASS.Agent.Companion.Media.Sendspin;

/// <summary>
/// The Sendspin player's sound: WASAPI shared mode through NAudio, on the Windows default output
/// or on the device chosen by name, pulling floats from the SDK's sample source.
///
/// An output device goes away (a TV switched off takes its HDMI output with it, a headset is
/// unplugged, the default changes): the output is built again on the device there is then, and
/// while there is none it is tried again every few seconds. The SDK is not told about it as an
/// error, because then it reports the player as unavailable and Music Assistant takes it out of
/// its group for good. What was buffered meanwhile is late by then, and the SDK's buffer catches
/// up on its own (it re-anchors past half a second of error).
///
/// The volume is a gain on these samples, not the Windows volume and not the app's audio session,
/// which the announcements play through as well.
/// </summary>
internal sealed class SendspinWasapiOutput : IAudioPlayer
{
    // NAudio's default and the Windows client's: steady across hardware in shared mode.
    private const int RequestedLatencyMs = 100;

    // The engine's overhead after the queue when the driver reports no stream latency.
    private const int FallbackFixedLatencyMs = 10;

    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RebuildDelay = TimeSpan.FromMilliseconds(500);

    // NAudio keeps the AudioClient of a WasapiPlayer private. What the player itself tells of it is
    // too coarse for the sync: the stream latency in whole milliseconds, and the requested latency
    // in its place when the driver reports none.
    private static readonly FieldInfo? AudioClientField =
        typeof(WasapiPlayer).GetField("audioClient", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly FileLog _log;
    private readonly Func<string?> _deviceName;
    private readonly object _gate = new();
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly MMDeviceNotificationClient? _notifications;
    private readonly GainSampleProvider _gain;

    private AudioFormat? _format;
    private WasapiPlayer? _out;
    private LatencyProbe? _probe;
    private string? _deviceId;
    private bool _wantPlaying;
    private bool _disposed;
    private int _rebuildScheduled;
    private System.Threading.Timer? _retry;

    /// <param name="deviceName">The output to use by its name, or null for the Windows default; read at every (re)build.</param>
    public SendspinWasapiOutput(FileLog log, Func<string?> deviceName)
    {
        _log = log;
        _deviceName = deviceName;
        _gain = new GainSampleProvider();
        try
        {
            // As they come, on Windows' own thread and not through a window's message loop: the
            // handlers only note that a rebuild is due.
            _notifications = _enumerator.CreateNotificationClient(useSynchronizationContext: false);
            _notifications.DefaultDeviceChanged += (_, e) =>
            {
                if (e.Flow == DataFlow.Render && e.Role == Role.Multimedia)
                {
                    OnDeviceNotification(e.DeviceId, defaultChanged: true);
                }
            };
            _notifications.DeviceStateChanged += (_, e) => OnDeviceNotification(e.DeviceId, defaultChanged: false);
            _notifications.DeviceAdded += (_, e) => OnDeviceNotification(e.DeviceId, defaultChanged: false);
            _notifications.DeviceRemoved += (_, e) => OnDeviceNotification(e.DeviceId, defaultChanged: false);
        }
        catch (Exception ex)
        {
            _log.Warning($"Sendspin: unable to watch the audio devices, a lost device is only noticed by its error: {ex.Message}");
        }
    }

    /// <summary>The device the sound goes to now, or null while there is none.</summary>
    public string? CurrentDeviceName { get; private set; }

    /// <summary>Raised when the output gets a device or loses it.</summary>
    public event EventHandler? DeviceChanged;

    public AudioPlayerState State { get; private set; } = AudioPlayerState.Uninitialized;

    public float Volume
    {
        get => _gain.Volume;
        set => _gain.Volume = Math.Clamp(value, 0f, 1f);
    }

    public bool IsMuted
    {
        get => _gain.Muted;
        set => _gain.Muted = value;
    }

    /// <summary>1 normally; lower while an announcement of the app plays, 0 to silence the player for it.</summary>
    public float Duck
    {
        get => _gain.Duck;
        set => _gain.Duck = Math.Clamp(value, 0f, 1f);
    }

    /// <summary>
    /// The latency after the device's queue only. What is queued is reported per sample by
    /// <see cref="GetCurrentOutputLatencyMicroseconds"/>; counting the device buffer here as well
    /// put a player that joined a running stream a whole buffer (about 100 ms) off the others.
    /// </summary>
    public int OutputLatencyMs { get; private set; } = FallbackFixedLatencyMs;

    public int CalibratedStartupLatencyMs => 0;

    public AudioFormat OutputFormat =>
        _format ?? new AudioFormat { Codec = "pcm", SampleRate = 48000, Channels = 2, BitDepth = 32 };

    public event EventHandler<AudioPlayerState>? StateChanged;

    // Never raised: a lost device is rebuilt here, an error would make the SDK give the player up.
    public event EventHandler<AudioPlayerError>? ErrorOccurred
    {
        add { }
        remove { }
    }

    public Task InitializeAsync(AudioFormat format, CancellationToken cancellationToken)
    {
        _format = format;
        SetState(AudioPlayerState.Stopped);
        return Task.CompletedTask;
    }

    public void SetSampleSource(IAudioSampleSource source)
    {
        lock (_gate)
        {
            _gain.Source = source;
            Rebuild("new stream");
        }
    }

    public void Play()
    {
        lock (_gate)
        {
            _wantPlaying = true;
            if (_out is null)
            {
                Rebuild("play");
            }
            else
            {
                _out.Play();
            }
        }

        SetState(AudioPlayerState.Playing);
    }

    public void Pause()
    {
        lock (_gate)
        {
            _wantPlaying = false;
            _out?.Pause();
        }

        SetState(AudioPlayerState.Paused);
    }

    public void Stop()
    {
        lock (_gate)
        {
            _wantPlaying = false;
            _out?.Stop();
        }

        SetState(AudioPlayerState.Stopped);
    }

    /// <summary>The device is chosen in the settings and read at each rebuild; this rebuilds now.</summary>
    public Task SwitchDeviceAsync(string? deviceId, CancellationToken cancellationToken)
    {
        ScheduleRebuild("device switch");
        return Task.CompletedTask;
    }

    // Wall-clock timing: the device clock lags by the prefill and puts the player off the others.
    public long? GetAudioClockMicroseconds() => null;

    /// <summary>Frames queued in the device now, plus its latency after the queue: how late a sample handed over now is heard.</summary>
    public long? GetCurrentOutputLatencyMicroseconds()
    {
        var probe = Volatile.Read(ref _probe);
        if (probe is null)
        {
            return null;
        }

        try
        {
            return (probe.Client.CurrentPadding * 1_000_000L / probe.SampleRate) + probe.FixedLatencyMicros;
        }
        catch
        {
            // The device is going away; the SDK falls back to OutputLatencyMs.
            return null;
        }
    }

    public void NotifyReconnect()
    {
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            _retry?.Dispose();
            _retry = null;
            DisposeOutput();
        }

        try { _notifications?.Dispose(); } catch { }
        try { _enumerator.Dispose(); } catch { }
        return ValueTask.CompletedTask;
    }

    /// <summary>Whether the reflection the latency report relies on still finds NAudio's field; a test watches it across NAudio updates.</summary>
    internal static bool CanReadAudioClient => AudioClientField?.FieldType == typeof(AudioClient);

    // Never from a device notification: the callback holds a WASAPI lock, and COM work there
    // deadlocks (see AudioEndpointService). Neither from NAudio's PlaybackStopped: disposing a
    // WasapiPlayer on its own play thread waits for that thread.
    private void ScheduleRebuild(string reason)
    {
        if (Interlocked.Exchange(ref _rebuildScheduled, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            // A device change comes as several notifications; one rebuild after they settle.
            await Task.Delay(RebuildDelay).ConfigureAwait(false);
            Interlocked.Exchange(ref _rebuildScheduled, 0);
            lock (_gate)
            {
                Rebuild(reason);
            }
        });
    }

    // Caller holds _gate.
    private void Rebuild(string reason)
    {
        if (_disposed || _gain.Source is null)
        {
            return;
        }

        DisposeOutput();

        MMDevice? device;
        try
        {
            device = FindDevice();
        }
        catch (Exception ex)
        {
            device = null;
            _log.Debug($"Sendspin: no output device ({reason}): {ex.Message}");
        }

        if (device is null)
        {
            SetDevice(null, null);
            StartRetry();
            return;
        }

        try
        {
            // Plain shared mode on purpose, not the low-latency one: its short buffer leaves no
            // room for a late wake-up, and the sync is told how much is queued anyway.
            var output = new WasapiPlayerBuilder()
                .WithDevice(device)
                .WithSharedMode()
                .WithEventSync()
                .WithLatency(RequestedLatencyMs)
                .Build();
            // Ours before Init: an Init that throws leaves it to the catch below to dispose.
            _out = output;
            output.PlaybackStopped += OnPlaybackStopped;
            output.Init(_gain);
            MeasureLatency(output);
            if (_wantPlaying)
            {
                output.Play();
            }

            StopRetry();
            SetDevice(device.ID, device.FriendlyName);
            _log.Info($"Sendspin: playing to {device.FriendlyName} ({reason}).");
        }
        catch (Exception ex)
        {
            _log.Warning($"Sendspin: unable to open {device.FriendlyName} ({reason}), trying again: {ex.Message}");
            DisposeOutput();
            SetDevice(null, null);
            StartRetry();
        }
    }

    private MMDevice? FindDevice()
    {
        var name = _deviceName();
        if (string.IsNullOrWhiteSpace(name))
        {
            return _enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                ? _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : null;
        }

        // The chosen one only: playing on another device instead would be a surprise, and it is
        // back on its own once the device is (a TV switched on again).
        return _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .FirstOrDefault(d => string.Equals(d.FriendlyName, name, StringComparison.OrdinalIgnoreCase));
    }

    private void MeasureLatency(WasapiPlayer output)
    {
        if (AudioClientField?.GetValue(output) is not AudioClient client)
        {
            Volatile.Write(ref _probe, null);
            OutputLatencyMs = FallbackFixedLatencyMs;
            _log.Warning("Sendspin: the audio client of NAudio is out of reach, the output latency is a guess; sync can be a few milliseconds off.");
            return;
        }

        var rate = output.OutputWaveFormat.SampleRate;
        var streamLatency100Ns = client.StreamLatency;
        var fixedMicros = streamLatency100Ns > 0 ? streamLatency100Ns / 10 : FallbackFixedLatencyMs * 1000L;
        Volatile.Write(ref _probe, new LatencyProbe(client, rate, fixedMicros));
        OutputLatencyMs = (int)Math.Round(fixedMicros / 1000.0, MidpointRounding.AwayFromZero);
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null)
        {
            return;
        }

        _log.Info($"Sendspin: the output device stopped ({e.Exception.Message}); building it again.");
        ScheduleRebuild("device lost");
    }

    // Caller holds _gate.
    private void DisposeOutput()
    {
        Volatile.Write(ref _probe, null);
        var output = _out;
        _out = null;
        if (output is null)
        {
            return;
        }

        output.PlaybackStopped -= OnPlaybackStopped;
        try { output.Stop(); } catch { }
        try { output.Dispose(); } catch { }
    }

    // Caller holds _gate.
    private void StartRetry()
    {
        _retry ??= new System.Threading.Timer(_ => ScheduleRebuild("retry"), null, RetryInterval, RetryInterval);
    }

    // Caller holds _gate.
    private void StopRetry()
    {
        _retry?.Dispose();
        _retry = null;
    }

    private void SetDevice(string? id, string? name)
    {
        var changed = _deviceId != id;
        _deviceId = id;
        CurrentDeviceName = name;
        if (changed)
        {
            DeviceChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetState(AudioPlayerState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, state);
    }

    // A device notification only matters when it can change where the sound goes: the default
    // changed while following it, the device in use went, or one came while there is none.
    private void OnDeviceNotification(string? deviceId, bool defaultChanged)
    {
        if (_disposed || _gain.Source is null)
        {
            return;
        }

        var followsDefault = string.IsNullOrWhiteSpace(_deviceName());
        if ((defaultChanged && followsDefault) || _deviceId is null || deviceId == _deviceId)
        {
            ScheduleRebuild(defaultChanged ? "default device changed" : "device changed");
        }
    }

    private sealed record LatencyProbe(AudioClient Client, int SampleRate, long FixedLatencyMicros);

    /// <summary>
    /// The SDK's samples as NAudio's, with the gain applied: the volume, mute and the announcement
    /// duck, eased over about 10 ms so a change does not click. The volume is an amplitude
    /// already: the SDK puts the spec's loudness curve on it (volume^1.5) before it hands it to
    /// the player, so it is applied as it comes; put on it once more, 50 % played at 0.21
    /// instead of 0.35.
    /// </summary>
    internal sealed class GainSampleProvider : ISampleProvider
    {
        private IAudioSampleSource? _source;
        private float _current = 1f;

        // The SDK fills an array, NAudio asks for a span of the device's own buffer: the block
        // goes through this, which grows to the largest block asked for and stays.
        private float[] _block = [];

        public float Volume { get; set; } = 1f;

        public bool Muted { get; set; }

        public float Duck { get; set; } = 1f;

        public IAudioSampleSource? Source
        {
            get => _source;
            set
            {
                _source = value;
                if (value is not null)
                {
                    WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(value.Format.SampleRate, value.Format.Channels);
                }
            }
        }

        public WaveFormat WaveFormat { get; private set; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        internal float Target => Muted ? 0f : Math.Clamp(Volume, 0f, 1f) * Duck;

        public int Read(Span<float> buffer)
        {
            var source = _source;
            if (source is null)
            {
                buffer.Clear();
                return buffer.Length;
            }

            if (_block.Length < buffer.Length)
            {
                _block = new float[buffer.Length];
            }

            // The source fills the whole block itself, padding a shortfall with silence.
            source.Read(_block, 0, buffer.Length);
            _block.AsSpan(0, buffer.Length).CopyTo(buffer);
            Apply(buffer, WaveFormat.Channels, WaveFormat.SampleRate);
            return buffer.Length;
        }

        internal void Apply(Span<float> buffer, int channels, int sampleRate)
        {
            var target = Target;
            var current = _current;
            if (current == target && target >= 0.99999f)
            {
                return;
            }

            var step = 1f / Math.Max(1, sampleRate / 100);
            for (var i = 0; i + channels <= buffer.Length; i += channels)
            {
                if (current < target)
                {
                    current = Math.Min(target, current + step);
                }
                else if (current > target)
                {
                    current = Math.Max(target, current - step);
                }

                for (var c = 0; c < channels; c++)
                {
                    buffer[i + c] *= current;
                }
            }

            _current = current;
        }
    }
}
