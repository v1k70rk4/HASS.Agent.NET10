using System.Net.Sockets;
using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.Runtime;
using Microsoft.Extensions.Logging;
using Sendspin.SDK.Audio;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection;
using Sendspin.SDK.Connection.Noise;
using Sendspin.SDK.Models;
using Sendspin.SDK.Synchronization;

namespace HASS.Agent.Companion.Media.Sendspin;

internal enum SendspinState
{
    Off,
    Starting,

    /// <summary>Listening and advertised on the network; no Music Assistant has connected yet.</summary>
    WaitingForServer,

    /// <summary>Connecting to the address in the settings.</summary>
    Connecting,

    /// <summary>A Music Assistant is connected but the player is not paired with it yet.</summary>
    NeedsPairing,
    Connected,
    Playing,

    /// <summary>Music Assistant plays to the player, but there is no output device to hear it on.</summary>
    NoOutputDevice,

    /// <summary>The port is taken: most likely the player of another Windows user's app, or another Sendspin player.</summary>
    PortInUse,

    /// <summary>The Music Assistant is older than the encrypted Sendspin protocol (before 2.10.7).</summary>
    ServerTooOld,

    /// <summary>The address to connect to is not one.</summary>
    InvalidAddress,
    Failed
}

internal sealed record SendspinStatus(
    SendspinState State,
    string? ServerName = null,
    string? Title = null,
    string? Artist = null,
    string? Device = null,
    string? Detail = null);

/// <summary>
/// The PC as a Sendspin player of Music Assistant, in the tray app. Music Assistant finds it on
/// the network (mDNS, port 8928) and connects to it, or it connects to the address in the
/// settings; either way it pairs with a token the user takes to Music Assistant, and plays what
/// the server sends in sync with the rest of its group.
///
/// The SDK does the protocol, the clock sync and the decoding; this class starts and stops it by
/// the settings, keeps a status for the settings window and turns the player down while an
/// announcement of the app plays.
/// </summary>
internal sealed class SendspinPlayerService : IAsyncDisposable
{
    public const int ListenPort = 8928;
    public const int DefaultServerPort = 8927;
    public const string SendspinPath = "/sendspin";

    // What the player says it can hold; the buffer is given the same, the SDK derives
    // the byte figure it announces from it.
    private const int BufferCapacityMs = 30_000;

    private static readonly TimeSpan PortRetryInterval = TimeSpan.FromSeconds(30);

    private readonly CompanionSettings _settings;
    private readonly FileLog _log;
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly object _statusGate = new();

    private Session? _session;
    private Config? _running;
    private SendspinStatus _status = new(SendspinState.Off);
    private float _duck = 1f;
    private bool _disposed;

    public SendspinPlayerService(CompanionSettings settings, FileLog log)
    {
        _settings = settings;
        _log = log;
    }

    public SendspinStatus Status
    {
        get
        {
            lock (_statusGate)
            {
                return _status;
            }
        }
    }

    /// <summary>Raised on any thread when <see cref="Status"/> changes.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Starts, stops or restarts the player for the settings as they are now.</summary>
    public async Task ApplySettingsAsync()
    {
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            var wanted = Config.From(_settings);

            // A player that could not start (the port taken, a failure) starts again on a save
            // of the same settings: whatever was in the way may be gone by now.
            var stuck = Status.State is SendspinState.PortInUse or SendspinState.Failed;
            if (wanted == _running && !stuck)
            {
                return;
            }

            if (!stuck && _running is not null && _session is not null && wanted.Enabled && wanted with { Device = _running.Device } == _running)
            {
                // Only the output device changed: the output is built again, the connection stays.
                _running = wanted;
                await _session.SwitchDeviceAsync().ConfigureAwait(false);
                return;
            }

            await StopSessionAsync().ConfigureAwait(false);
            _running = wanted;
            if (!wanted.Enabled)
            {
                SetStatus(new SendspinStatus(SendspinState.Off));
                return;
            }

            await StartSessionAsync(wanted).ConfigureAwait(false);
        }
        finally
        {
            _transition.Release();
        }
    }

    /// <summary>
    /// The pairing token to enter in Music Assistant: the player's public key and its Pairing
    /// PSK in one string. Made on first use and the same afterwards, also while the player is off.
    /// </summary>
    public string GetPairingToken() => WithPairing(p => p.EnsurePairingPsk());

    /// <summary>A new pairing token; the old one no longer pairs. Pairings already made stay.</summary>
    public string RotatePairingToken() => WithPairing(p => p.RotatePairingPsk());

    // The token comes from the running player, which holds the store; while it is off, from a
    // client that is never connected, over the same identity and store.
    private string WithPairing(Func<IPairingTokenSource, string> operation)
    {
        var session = _session;
        if (session?.TokenSource is { } running)
        {
            return operation(running);
        }

        var client = SendspinClientService.CreateForDial(
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            new SendspinClientOptions { Identity = Identity(), PairingRecordStore = PairingStore() });
        try
        {
            return operation(new ClientTokenSource(client));
        }
        finally
        {
            // Called from the settings window: disposed on the thread pool, so that nothing in the
            // SDK's disposal can wait for the UI thread that is waiting here.
            Task.Run(() => client.DisposeAsync().AsTask()).GetAwaiter().GetResult();
        }
    }

    private interface IPairingTokenSource
    {
        string EnsurePairingPsk();

        string RotatePairingPsk();
    }

    private sealed class ClientTokenSource(SendspinClientService client) : IPairingTokenSource
    {
        public string EnsurePairingPsk() => client.EnsurePairingPsk();

        public string RotatePairingPsk() => client.RotatePairingPsk();
    }

    private sealed class HostTokenSource(SendspinHostService host) : IPairingTokenSource
    {
        public string EnsurePairingPsk() => host.EnsurePairingPsk();

        public string RotatePairingPsk() => host.RotatePairingPsk();
    }

    /// <summary>
    /// An announcement of the app (a play_media with announce) plays or has ended. The player is
    /// silenced for it when the settings pause what is playing, turned down otherwise: the group
    /// keeps playing, there is nothing of the PC's to pause.
    /// </summary>
    public void SetAnnouncementActive(bool active)
    {
        _duck = !active ? 1f : _settings.AnnouncementPausesMedia ? 0f : 0.25f;
        _session?.SetDuck(_duck);
    }

    public async ValueTask DisposeAsync()
    {
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            await StopSessionAsync().ConfigureAwait(false);
        }
        finally
        {
            _transition.Release();
        }
    }

    /// <summary>The address to connect to, from what the user typed: a host, host:port, or a full ws:// address.</summary>
    internal static Uri? ParseServerAddress(string? text)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (!value.Contains("://", StringComparison.Ordinal))
        {
            value = "ws://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss") || string.IsNullOrEmpty(uri.Host))
        {
            return null;
        }

        var builder = new UriBuilder(uri);
        if (uri.IsDefaultPort && !value.Contains($":{uri.Port}", StringComparison.Ordinal))
        {
            builder.Port = DefaultServerPort;
        }

        if (builder.Path is "" or "/")
        {
            builder.Path = SendspinPath;
        }

        return builder.Uri;
    }

    private static ProtectedPairingRecordStore PairingStore() => new(SendspinStorage.PairingFile);

    private static SendspinIdentity Identity() => SendspinIdentity.FromStore(new ProtectedSendspinIdentityStore(SendspinStorage.IdentityFile));

    // Caller holds _transition.
    private async Task StartSessionAsync(Config config)
    {
        SetStatus(new SendspinStatus(SendspinState.Starting));
        Session? started = null;
        try
        {
            started = new Session(this, config);
            _session = started;
            await started.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Sendspin: unable to start the player.");
            SetStatus(new SendspinStatus(SendspinState.Failed, Detail: ex.Message));
        }

        if (started is not null && Status.State == SendspinState.PortInUse)
        {
            RetryWhilePortInUse(started);
        }
    }

    // The port is another Windows user's player as a rule; when that user logs off, this one
    // takes over without anybody having to press Save.
    private void RetryWhilePortInUse(Session session) => _ = Task.Run(async () =>
    {
        await Task.Delay(PortRetryInterval).ConfigureAwait(false);
        await _transition.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || _session != session || _running is not { } config || Status.State != SendspinState.PortInUse)
            {
                return;
            }

            await StopSessionAsync().ConfigureAwait(false);
            _running = config;
            await StartSessionAsync(config).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warning($"Sendspin: trying the port again failed: {ex.Message}");
        }
        finally
        {
            _transition.Release();
        }
    });

    // Caller holds _transition.
    private async Task StopSessionAsync()
    {
        var session = _session;
        _session = null;
        _running = null;
        if (session is null)
        {
            return;
        }

        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warning($"Sendspin: the player did not stop cleanly: {ex.Message}");
        }
    }

    private void SetStatus(SendspinStatus status)
    {
        lock (_statusGate)
        {
            if (_status == status)
            {
                return;
            }

            _status = status;
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>What the running player was started with; a change restarts it (the device alone does not).</summary>
    private sealed record Config(bool Enabled, string Name, bool ConnectToServer, string ServerAddress, string Device)
    {
        public static Config From(CompanionSettings settings) => new(
            settings.SendspinEnabled,
            settings.EffectiveSendspinPlayerName,
            settings.SendspinConnectToServer,
            settings.SendspinConnectToServer ? settings.SendspinServerAddress : string.Empty,
            settings.SendspinOutputDevice);
    }

    /// <summary>One run of the player: the SDK's objects for the settings it was started with.</summary>
    private sealed class Session : IAsyncDisposable
    {
        private readonly SendspinPlayerService _owner;
        private readonly Config _config;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ProtectedPairingRecordStore _pairing = PairingStore();
        private readonly CancellationTokenSource _cts = new();

        private SendspinHostService? _host;
        private SendspinClientService? _client;
        private SendspinWasapiOutput? _output;
        private Task? _connectLoop;

        private SendspinState _link = SendspinState.Starting;
        private string? _serverName;
        private GroupState? _group;
        private string? _detail;

        public Session(SendspinPlayerService owner, Config config)
        {
            _owner = owner;
            _config = config;
            _loggerFactory = LoggerFactory.Create(builder => builder
                .SetMinimumLevel(LogLevel.Trace)
                .AddProvider(new FileLogLoggerProvider(owner._log)));
        }

        private FileLog Log => _owner._log;

        public async Task StartAsync()
        {
            var clock = new KalmanClockSynchronizer();
            var pipeline = new AudioPipeline(
                _loggerFactory.CreateLogger<AudioPipeline>(),
                new AudioDecoderFactory(_loggerFactory),
                clock,
                bufferFactory: (format, sync) => new TimedAudioBuffer(format, sync, BufferCapacityMs, null, _loggerFactory.CreateLogger<TimedAudioBuffer>()),
                playerFactory: CreateOutput,
                sourceFactory: (buffer, now) => new SyncCorrectedSampleSource(buffer, now, null, _loggerFactory.CreateLogger<SyncCorrectedSampleSource>()));

            var options = new SendspinClientOptions
            {
                Identity = Identity(),
                PairingRecordStore = _pairing,
                ClockSynchronizer = clock,
                AudioPipeline = pipeline,
                OutputDelayStore = new SendspinOutputDelayStore(SendspinStorage.OutputDelayFile),
                Capabilities = new ClientCapabilities
                {
                    ClientName = _config.Name,
                    ProductName = AppIdentity.DisplayName,
                    SoftwareVersion = _owner._settings.SoftwareVersion,
                    // No artwork or colours: nothing on the PC shows them, and the artwork
                    // would be sent for every track for nothing.
                    Roles = [ClientRoles.Player, ClientRoles.Controller, ClientRoles.Metadata],
                    AudioBufferCapacityMs = BufferCapacityMs,
                },
            };

            if (_config.ConnectToServer)
            {
                var uri = ParseServerAddress(_config.ServerAddress);
                if (uri is null)
                {
                    await pipeline.DisposeAsync().ConfigureAwait(false);
                    _owner.SetStatus(new SendspinStatus(SendspinState.InvalidAddress, Detail: _config.ServerAddress));
                    Log.Warning($"Sendspin: \"{_config.ServerAddress}\" is not an address to connect to.");
                    return;
                }

                _client = SendspinClientService.CreateForDial(_loggerFactory, options, new ConnectionOptions { AutoReconnect = true });
                _client.ConnectionStateChanged += OnConnectionStateChanged;
                _client.GroupStateChanged += OnGroupStateChanged;
                _client.PairingCompleted += OnPairingCompleted;
                Update(SendspinState.Connecting);
                _connectLoop = Task.Run(() => ConnectLoopAsync(uri, _cts.Token));
                Log.Info($"Sendspin: player \"{_config.Name}\" connects to {uri}.");
                return;
            }

            _host = new SendspinHostService(
                _loggerFactory,
                options,
                new ListenerOptions { Port = ListenPort, Path = SendspinPath },
                lastPlayedServerStore: new SendspinLastPlayedServerStore(SendspinStorage.LastPlayedFile));
            _host.ServerConnected += OnServerConnected;
            _host.ServerDisconnected += OnServerDisconnected;
            _host.GroupStateChanged += OnGroupStateChanged;
            _host.PairingCompleted += OnPairingCompleted;
            try
            {
                await _host.StartAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsPortInUse(ex))
            {
                Log.Warning($"Sendspin: port {ListenPort} is in use, the player is not started: {ex.Message}");
                Update(SendspinState.PortInUse);
                return;
            }

            Update(SendspinState.WaitingForServer);
            Log.Info($"Sendspin: player \"{_config.Name}\" waits for Music Assistant on port {ListenPort}.");
        }

        public IPairingTokenSource? TokenSource =>
            _host is not null ? new HostTokenSource(_host) : _client is not null ? new ClientTokenSource(_client) : null;

        public Task SwitchDeviceAsync() => _output?.SwitchDeviceAsync(null, CancellationToken.None) ?? Task.CompletedTask;

        public void SetDuck(float duck)
        {
            if (_output is not null)
            {
                _output.Duck = duck;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            if (_connectLoop is not null)
            {
                try { await _connectLoop.ConfigureAwait(false); } catch { }
            }

            if (_host is not null)
            {
                _host.ServerConnected -= OnServerConnected;
                _host.ServerDisconnected -= OnServerDisconnected;
                _host.GroupStateChanged -= OnGroupStateChanged;
                _host.PairingCompleted -= OnPairingCompleted;
                await _host.DisposeAsync().ConfigureAwait(false);
            }

            if (_client is not null)
            {
                _client.ConnectionStateChanged -= OnConnectionStateChanged;
                _client.GroupStateChanged -= OnGroupStateChanged;
                _client.PairingCompleted -= OnPairingCompleted;
                try { await _client.DisconnectAsync("shutdown").ConfigureAwait(false); } catch { }
                await _client.DisposeAsync().ConfigureAwait(false);
            }

            if (_output is not null)
            {
                _output.DeviceChanged -= OnDeviceChanged;
            }

            _loggerFactory.Dispose();
            _cts.Dispose();
        }

        private SendspinWasapiOutput CreateOutput()
        {
            if (_output is not null)
            {
                _output.DeviceChanged -= OnDeviceChanged;
            }

            var output = new SendspinWasapiOutput(Log, () => string.IsNullOrWhiteSpace(_owner._settings.SendspinOutputDevice) ? null : _owner._settings.SendspinOutputDevice)
            {
                Duck = _owner._duck
            };
            output.DeviceChanged += OnDeviceChanged;
            _output = output;
            return output;
        }

        private async Task ConnectLoopAsync(Uri uri, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TimeSpan wait;
                try
                {
                    // With auto-reconnect on, an unreachable server does not throw: the SDK
                    // keeps trying itself and this returns.
                    await _client!.ConnectAsync(uri, token).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (SendspinHandshakeException ex) when (ex.Kind == HandshakeFailureKind.LegacyServer)
                {
                    Update(SendspinState.ServerTooOld);
                    Log.Warning($"Sendspin: {uri} is older than the encrypted Sendspin protocol; Music Assistant 2.10.7 or newer is needed.");
                    wait = TimeSpan.FromMinutes(5);
                }
                catch (SendspinHandshakeException ex) when (ex.Kind is HandshakeFailureKind.PairingRequired or HandshakeFailureKind.PairingStateDiverged)
                {
                    Update(SendspinState.NeedsPairing);
                    wait = TimeSpan.FromSeconds(15);
                }
                catch (SendspinHandshakeException ex)
                {
                    _detail = ex.Message;
                    Update(SendspinState.Failed);
                    Log.Warning($"Sendspin: {uri} refused the player ({ex.Kind}): {ex.Message}");
                    wait = TimeSpan.FromSeconds(30);
                }
                catch (TimeoutException)
                {
                    wait = TimeSpan.FromSeconds(10);
                }
                catch (Exception ex)
                {
                    _detail = ex.Message;
                    Update(SendspinState.Failed);
                    Log.Warning($"Sendspin: unable to connect to {uri}: {ex.Message}");
                    wait = TimeSpan.FromSeconds(30);
                }

                try
                {
                    await Task.Delay(wait, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
        {
            switch (e.NewState)
            {
                case ConnectionState.Connected:
                    _serverName = _client?.ServerName;
                    Update(_client?.TrustLevel == SendspinTrustLevel.Paired ? SendspinState.Connected : SendspinState.NeedsPairing);
                    break;
                case ConnectionState.Connecting or ConnectionState.Handshaking or ConnectionState.Reconnecting:
                    _group = null;
                    Update(SendspinState.Connecting);
                    break;
                case ConnectionState.Disconnected:
                    _group = null;
                    if (_link is not (SendspinState.ServerTooOld or SendspinState.NeedsPairing or SendspinState.Failed))
                    {
                        Update(SendspinState.Connecting);
                    }

                    break;
            }
        }

        private void OnServerConnected(object? sender, ConnectedServerInfo info)
        {
            _serverName = info.ServerName;
            Update(_pairing.IsPairedWith(info.ServerId) ? SendspinState.Connected : SendspinState.NeedsPairing);
            Log.Info($"Sendspin: {info.ServerName} connected.");
        }

        private void OnServerDisconnected(object? sender, string serverId)
        {
            if (_host?.ConnectedServers.Count is null or 0)
            {
                _group = null;
                _serverName = null;
                Update(SendspinState.WaitingForServer);
            }
        }

        private void OnPairingCompleted(object? sender, string serverId)
        {
            Log.Info("Sendspin: paired with Music Assistant.");
            Update(SendspinState.Connected);
        }

        private void OnGroupStateChanged(object? sender, GroupState group)
        {
            _group = group;
            Publish();
        }

        private void OnDeviceChanged(object? sender, EventArgs e) => Publish();

        private void Update(SendspinState link)
        {
            _link = link;
            if (link is not SendspinState.Failed)
            {
                _detail = null;
            }

            Publish();
        }

        // The status the window shows: the link to the server, and once it is up, what plays.
        private void Publish()
        {
            if (_cts.IsCancellationRequested)
            {
                return;
            }

            // Read once: the SDK's threads and the device watcher change these meanwhile.
            var state = _link;
            var group = _group;
            var output = _output;
            var device = output?.CurrentDeviceName;
            string? title = null;
            string? artist = null;
            if (state == SendspinState.Connected && group?.PlaybackState == PlaybackState.Playing)
            {
                state = output is not null && device is null ? SendspinState.NoOutputDevice : SendspinState.Playing;
                title = group.Metadata?.Title;
                artist = group.Metadata?.Artist;
            }

            _owner.SetStatus(new SendspinStatus(state, _serverName, title, artist, device, _detail));
        }

        private static bool IsPortInUse(Exception ex) => ex switch
        {
            SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.AccessDenied } => true,
            { InnerException: { } inner } => IsPortInUse(inner),
            _ => false
        };
    }
}
