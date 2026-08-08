using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Http;
using HASS.Agent.Companion.Localization;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.SystemService;
using HASS.Agent.Companion.Media;
using HASS.Agent.Companion.SystemCommands;
using HASS.Agent.Companion.SystemStatus;
using HASS.Agent.Companion.Runtime;
using MQTTnet;
using MQTTnet.Protocol;

namespace HASS.Agent.Companion.Mqtt;

internal sealed class MqttCompanionService : IDisposable
{
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly CompanionSettings _settings;
    private readonly INotificationSink _notificationSink;
    private readonly MediaSessionService? _mediaSessionService;
    private readonly SystemMetricsService? _systemMetricsService;
    private readonly SystemCommandService _systemCommandService;
    private readonly CompanionRuntimeRole _role;
    private readonly FileLog _log;
    private readonly MqttClientFactory _factory = new();
    private IMqttClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private readonly SemaphoreSlim _restartLock = new(1, 1);
    private HaWebSocketService? _haWs;
    private bool _isOnWebSocket;
    private string? _pendingUpdateCompletedFrom;
    private int _updateInstallInProgress;
    private volatile bool _forceReconnect;

    // Event-driven ("push") sensor updates: interactive state sources (monitor power,
    // session lock, AC/battery) signal here so the sensor loop publishes immediately
    // instead of waiting for the next poll. Debounced to coalesce rapid changes.
    // A publish must never block forever: when the monitor powers off, Wi-Fi power
    // save can leave the TCP connection half-open and the socket write hangs. Cap it
    // and drop the connection so the run loop reconnects.
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PushDebounce = TimeSpan.FromMilliseconds(600);
    private static readonly IReadOnlySet<SensorPollingProfile> PushProfiles =
        new HashSet<SensorPollingProfile> { SensorPollingProfile.Fast, SensorPollingProfile.Normal };
    private readonly SemaphoreSlim _pushSignal = new(0, 1);
    // Serializes publishes so the push extra-publish never races the media/sensor
    // publish on the shared MQTT client (a likely cause of the dimmed-state freeze).
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private readonly object _pushDebounceLock = new();
    private System.Threading.Timer? _pushDebounceTimer;

    // Connection state tracking — when only capabilities/sensors change,
    // we refresh discovery without restarting the whole MQTT connection.
    private string? _connectedHost;
    private int _connectedPort;
    private string? _connectedUser;
    private string? _connectedPassword;
    private bool _connectedTls;
    private string? _connectedDeviceName;
    private bool _connectedHaApiEnabled;
    private string? _connectedHaApiUrl;
    private string? _connectedHaApiToken;
    private bool _subscribedNotifications;
    private bool _subscribedMediaPlayer;
    private bool _subscribedButtons;

    public MqttCompanionService(
        CompanionSettings settings,
        INotificationSink notificationSink,
        MediaSessionService? mediaSessionService,
        SystemMetricsService? systemMetricsService,
        SystemCommandService systemCommandService,
        CompanionRuntimeRole role,
        FileLog log)
    {
        _settings = settings;
        _notificationSink = notificationSink;
        _mediaSessionService = mediaSessionService;
        _systemMetricsService = systemMetricsService;
        _systemCommandService = systemCommandService;
        _role = role;
        _log = log;
    }

    public void Start()
    {
        if (_cts is not null)
        {
            return;
        }

        if (!_settings.MqttEnabled && !_settings.HaApiEnabled)
        {
            _log.Info("MQTT and HA API both disabled.");
            return;
        }

        _cts = new CancellationTokenSource();
        _worker = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task RestartAsync()
    {
        await _restartLock.WaitAsync();
        try
        {
            if (!_settings.MqttEnabled && !_settings.HaApiEnabled)
            {
                if (_client is { IsConnected: true })
                {
                    _log.Info("MQTT disabled, publishing offline discovery.");
                    await PublishDiscoveryAsync(offline: true);
                }

                await StopAsync(publishOffline: false);
                return;
            }

            // When only capabilities/sensors changed (not connection settings),
            // just refresh discovery on the existing connection — no need to
            // restart MQTT, which would briefly take the media player offline.
            if (_client is { IsConnected: true } && !NeedsReconnect())
            {
                _log.Info("Refreshing MQTT discovery (connection unchanged).");
                await PublishDiscoveryAsync();
                await PublishLegacyTopicCleanupAsync();
                return;
            }

            _log.Info("Restarting MQTT runtime.");
            await StopAsync(publishOffline: false);
            Start();
        }
        finally
        {
            _restartLock.Release();
        }
    }

    private bool NeedsReconnect()
    {
        return _connectedHost != _settings.MqttHost
            || _connectedPort != _settings.MqttPort
            || _connectedUser != _settings.MqttUsername
            || _connectedPassword != _settings.GetMqttPassword()
            || _connectedTls != _settings.MqttUseTls
            || _connectedDeviceName != _settings.DeviceName
            || _connectedHaApiEnabled != _settings.HaApiEnabled
            || _connectedHaApiUrl != _settings.HaApiUrl
            || _connectedHaApiToken != _settings.GetHaApiToken()
            || _subscribedNotifications != _settings.MqttNotificationsEnabled
            || _subscribedMediaPlayer != _settings.MqttMediaPlayerEnabled
            || _subscribedButtons != _settings.MqttButtonsEnabled;
    }

    public async Task PublishNotificationActionAsync(string action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return;
        }

        var trimmedAction = action.Trim();
        _log.Info($"Publishing notification action: {trimmedAction}");

        if (_isOnWebSocket && _haWs is not null)
        {
            await _haWs.PublishNotificationActionAsync(trimmedAction, _cts?.Token ?? CancellationToken.None);
            return;
        }

        await PublishJsonAsync(
            $"hass.agent/notifications/{TopicId}/actions",
            new NotificationActionMessage(_settings.DeviceName, trimmedAction, DateTimeOffset.UtcNow, null),
            retain: false);
    }

    /// <summary>Re-sends the device discovery on the active transport. Returns false when not connected.</summary>
    public async Task<bool> RepublishDiscoveryAsync()
    {
        if (_isOnWebSocket && _haWs is not null)
        {
            await PublishDiscoveryViaWebSocketAsync(_cts?.Token ?? CancellationToken.None);
            _log.Info("Discovery republished over HA WebSocket API.");
            return true;
        }

        if (_client is { IsConnected: true })
        {
            await PublishDiscoveryAsync();
            _log.Info("Discovery republished over MQTT.");
            return true;
        }

        _log.Warning("Discovery republish requested but no transport is connected.");
        return false;
    }

    /// <summary>Queues an "updated to X" persistent notification for the next connect.</summary>
    public void NotifyUpdateCompleted(string previousVersion)
    {
        _pendingUpdateCompletedFrom = previousVersion;
    }

    /// <summary>Creates a Home Assistant persistent notification on the active transport.</summary>
    public async Task PublishPersistentNotificationAsync(string title, string message)
    {
        if (_isOnWebSocket && _haWs is not null)
        {
            await _haWs.PublishPersistentNotificationAsync(title, message, _cts?.Token ?? CancellationToken.None);
            return;
        }

        await PublishJsonAsync(PersistentNotificationTopic, new PersistentNotificationMessage(title, message), retain: false);
    }

    private async Task PublishPendingUpdateNotificationAsync()
    {
        var previousVersion = _pendingUpdateCompletedFrom;
        if (previousVersion is null)
        {
            return;
        }

        _pendingUpdateCompletedFrom = null;
        await PublishPersistentNotificationAsync(
            Strings.GetHa("HaPn.UpdateTitle"),
            string.Format(Strings.GetHa("HaPn.UpdateCompleted"), _settings.DeviceName, previousVersion, _settings.SoftwareVersion));
    }

    /// <summary>Handles the Install button of the HA update entity (tray app role).</summary>
    private async Task HandleUpdateInstallRequestAsync()
    {
        if (Interlocked.Exchange(ref _updateInstallInProgress, 1) == 1)
        {
            _log.Info("Update install already in progress, ignoring request.");
            return;
        }

        try
        {
            _log.Info("Update install requested from Home Assistant.");
            var update = await AppUpdateService.CheckAsync(_settings.SoftwareVersion, _settings.BetaUpdatesEnabled);
            if (!update.UpdateAvailable)
            {
                await PublishPersistentNotificationAsync(
                    Strings.GetHa("HaPn.UpdateTitle"),
                    string.Format(Strings.GetHa("HaPn.NoUpdate"), _settings.DeviceName));
                return;
            }

            if (!IsInstallerAsset(update.AssetName))
            {
                await PublishPersistentNotificationAsync(
                    Strings.GetHa("HaPn.UpdateTitle"),
                    string.Format(Strings.GetHa("HaPn.NoInstaller"), _settings.DeviceName, update.LatestVersion));
                return;
            }

            if (CompanionServiceManager.IsRunning())
            {
                // The SYSTEM service installs without a UAC prompt; a detached
                // watchdog relaunches this tray app once the installer finishes.
                SpawnRelaunchWatchdog();
                await PublishJsonAsync(
                    ServiceCommandTopic,
                    new SystemCommandMessage("install_update", Force: false, Time: 0, Comment: null, RestartCancel: false),
                    retain: false);
                await PublishPersistentNotificationAsync(
                    Strings.GetHa("HaPn.UpdateTitle"),
                    string.Format(Strings.GetHa("HaPn.UpdateStartedSilent"), _settings.DeviceName, update.LatestVersion));
                return;
            }

            // No running service: install from the user session — someone has to confirm UAC.
            // Distinguish "installed but stopped" (just start it) from "not installed at all".
            var messageKey = CompanionServiceManager.IsInstalled()
                ? "HaPn.UpdateStartedServiceStopped"
                : "HaPn.UpdateStartedUac";
            await PublishPersistentNotificationAsync(
                Strings.GetHa("HaPn.UpdateTitle"),
                string.Format(Strings.GetHa(messageKey), _settings.DeviceName, update.LatestVersion));

            var installerPath = await AppUpdateService.DownloadAsync(update, GetUpdateDownloadDirectory());
            _log.Info($"Starting installer with elevation: {installerPath}");
            Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
                UseShellExecute = true,
                Verb = "runas"
            });
        }
        catch (Exception ex)
        {
            _log.Warning($"Update install failed: {ex.Message}");
            try
            {
                await PublishPersistentNotificationAsync(
                    Strings.GetHa("HaPn.UpdateTitle"),
                    string.Format(Strings.GetHa("HaPn.UpdateFailed"), _settings.DeviceName, ex.Message));
            }
            catch
            {
                // Transport may be down; the log entry above is the fallback.
            }
        }
        finally
        {
            Interlocked.Exchange(ref _updateInstallInProgress, 0);
        }
    }

    /// <summary>
    /// Service-role silent install. Downloads the installer from GitHub itself —
    /// never runs a caller-supplied path as SYSTEM.
    /// </summary>
    private async Task RunSilentUpdateInstallAsync()
    {
        try
        {
            _log.Info("Silent update install requested (service role).");
            var update = await AppUpdateService.CheckAsync(_settings.SoftwareVersion, _settings.BetaUpdatesEnabled);
            if (!update.UpdateAvailable)
            {
                _log.Info("No update available, skipping silent install.");
                return;
            }

            if (!IsInstallerAsset(update.AssetName))
            {
                _log.Warning($"Release {update.LatestVersion} has no installer asset, skipping silent install.");
                return;
            }

            var installerPath = await AppUpdateService.DownloadAsync(update, GetUpdateDownloadDirectory());
            _log.Info($"Downloaded installer: {installerPath}");

            // Run via Task Scheduler, NOT as a child process: the installer stops
            // this service to free the locked exe, which would kill a child process
            // (and the install) mid-way. A scheduled task survives the service stop.
            var batch = WriteBatch("run-update.cmd",
                $"@echo off{Environment.NewLine}" +
                $"\"{installerPath}\" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SILENTUPDATE{Environment.NewLine}");
            RunDetachedTask("HASSAgentNet10Update", batch, asSystem: true);
            _log.Info("Silent installer scheduled via Task Scheduler.");
        }
        catch (Exception ex)
        {
            _log.Warning($"Silent update install failed: {ex.Message}");
        }
    }

    private string WriteBatch(string fileName, string content)
    {
        var directory = GetUpdateDownloadDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// Runs a batch file via a one-shot scheduled task, fully detached from this
    /// process tree so it survives both service stop and taskkill /T on the tray app.
    /// </summary>
    private void RunDetachedTask(string taskName, string batchPath, bool asSystem)
    {
        RunSchtasks($"/delete /tn \"{taskName}\" /f", ignoreErrors: true);
        var runAs = asSystem ? " /ru SYSTEM /rl HIGHEST" : string.Empty;
        RunSchtasks($"/create /tn \"{taskName}\" /tr \"{batchPath}\" /sc once /st 00:00{runAs} /f");
        RunSchtasks($"/run /tn \"{taskName}\"");
    }

    private void RunSchtasks(string arguments, bool ignoreErrors = false)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null)
            {
                return;
            }

            process.WaitForExit(10_000);
            if (process.ExitCode != 0 && !ignoreErrors)
            {
                _log.Warning($"schtasks {arguments} exited with code {process.ExitCode}: {process.StandardError.ReadToEnd().Trim()}");
            }
        }
        catch (Exception ex) when (ignoreErrors)
        {
            _log.Debug($"schtasks {arguments} failed (ignored): {ex.Message}");
        }
    }

    private static bool IsInstallerAsset(string? assetName)
    {
        return assetName is not null &&
               assetName.Contains("Setup", StringComparison.OrdinalIgnoreCase) &&
               assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetUpdateDownloadDirectory()
    {
        return Path.Combine(Path.GetTempPath(), "HASS.Agent.NET10", "updates");
    }

    /// <summary>
    /// The installer stops this tray app (taskkill /T would kill a child watchdog),
    /// so the watchdog runs via a one-shot user scheduled task: it waits for the
    /// setup process to come and go, then relaunches the tray app in the user session.
    /// </summary>
    private void SpawnRelaunchWatchdog()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            return;
        }

        var batch = WriteBatch("relaunch.cmd",
            "@echo off" + Environment.NewLine +
            "setlocal" + Environment.NewLine +
            "set /a tries=0" + Environment.NewLine +
            ":waitappear" + Environment.NewLine +
            "timeout /t 1 /nobreak >nul" + Environment.NewLine +
            "tasklist 2>nul | find /i \"HASS.Agent.NET10-Setup\" >nul" + Environment.NewLine +
            "if not errorlevel 1 goto waitgone" + Environment.NewLine +
            "set /a tries+=1" + Environment.NewLine +
            "if %tries% lss 30 goto waitappear" + Environment.NewLine +
            "goto launch" + Environment.NewLine +
            ":waitgone" + Environment.NewLine +
            "timeout /t 2 /nobreak >nul" + Environment.NewLine +
            "tasklist 2>nul | find /i \"HASS.Agent.NET10-Setup\" >nul" + Environment.NewLine +
            "if not errorlevel 1 goto waitgone" + Environment.NewLine +
            ":launch" + Environment.NewLine +
            "timeout /t 3 /nobreak >nul" + Environment.NewLine +
            $"start \"\" \"{executable}\" --quiet" + Environment.NewLine +
            "endlocal" + Environment.NewLine);

        // User task (not SYSTEM): relaunches the tray in the visible user session.
        RunDetachedTask("HASSAgentNet10Relaunch", batch, asSystem: false);
        _log.Info("Relaunch watchdog scheduled via Task Scheduler.");
    }

    public async Task StopAsync(bool publishOffline = true)
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync();

        if (publishOffline)
        {
            await PublishAvailabilityAsync(online: false);
            await PublishDiscoveryAsync(offline: true);
        }
        if (_mediaSessionService is not null)
        {
            await _mediaSessionService.StopAsync();
        }

        if (_client is { IsConnected: true })
        {
            await _client.DisconnectAsync();
        }

        if (_haWs is not null)
        {
            await _haWs.DisconnectAsync();
        }

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

        _cts.Dispose();
        _cts = null;
        _worker = null;
        _isOnWebSocket = false;
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _haWs?.Dispose();
        lock (_pushDebounceLock)
        {
            _pushDebounceTimer?.Dispose();
            _pushDebounceTimer = null;
        }
        _pushSignal.Dispose();
        _publishLock.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        _haWs?.Dispose();
        _haWs = null;

        // Initialize HA WebSocket service if configured.
        if (_settings.HaApiEnabled && !string.IsNullOrWhiteSpace(_settings.HaApiUrl))
        {
            _haWs = new HaWebSocketService(_settings, _log);
            _haWs.NotificationReceived += notification => _notificationSink.ShowNotification(notification);
            _haWs.MediaCommandReceived += command =>
            {
                if (_mediaSessionService is not null)
                {
                    _ = _mediaSessionService.HandleCommandAsync(command);
                }
            };
            _haWs.ButtonCommandReceived += command =>
            {
                var commandName = GetSystemCommandName(command);
                var enabled = _role == CompanionRuntimeRole.Service
                    ? commandName is not null && _settings.IsServiceCommandEnabled(commandName)
                    : commandName is not null && _settings.IsTrayAppCommandEnabled(commandName);
                if (enabled)
                {
                    _ = _systemCommandService.HandleCommandAsync(command);
                    return;
                }

                _log.Warning($"Unsupported {_role.Token()} WebSocket command received: {commandName ?? "(empty)"}");
            };
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            CancellationTokenSource? connectionCts = null;
            Task? systemSensorsTask = null;
            Task? updateTask = null;

            try
            {
                if (!_settings.MqttEnabled)
                {
                    // MQTT disabled — go straight to WebSocket if available.
                    if (_haWs is not null)
                    {
                        _log.Info("MQTT disabled, using HA WebSocket API as primary transport.");
                        await RunWebSocketFailoverAsync(tryMqttReconnect: false, cancellationToken);
                    }

                    // If WS also exits, wait and retry.
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                    continue;
                }

                _client?.Dispose();
                _client = _factory.CreateMqttClient();
                _client.ApplicationMessageReceivedAsync += HandleMessageAsync;

                var options = BuildOptions();
                _log.Info($"Connecting MQTT to {_settings.MqttHost}:{_settings.MqttPort}.");
                _isOnWebSocket = false;
                await _client.ConnectAsync(options, cancellationToken);

                _log.Info("MQTT connected.");
                _forceReconnect = false;
                await SubscribeAsync(cancellationToken);
                await PublishDiscoveryAsync();
                await PublishLegacyTopicCleanupAsync();
                if (_role == CompanionRuntimeRole.App)
                {
                    await PublishAvailabilityAsync(online: true);
                    await PublishUpdateStateAsync();
                    await PublishPendingUpdateNotificationAsync();
                }

                // Store connection state so RestartAsync can decide whether a
                // full reconnect is needed or just a discovery refresh.
                _connectedHost = _settings.MqttHost;
                _connectedPort = _settings.MqttPort;
                _connectedUser = _settings.MqttUsername;
                _connectedPassword = _settings.GetMqttPassword();
                _connectedTls = _settings.MqttUseTls;
                _connectedDeviceName = _settings.DeviceName;
                _connectedHaApiEnabled = _settings.HaApiEnabled;
                _connectedHaApiUrl = _settings.HaApiUrl;
                _connectedHaApiToken = _settings.GetHaApiToken();
                _subscribedNotifications = _settings.MqttNotificationsEnabled;
                _subscribedMediaPlayer = _settings.MqttMediaPlayerEnabled;
                _subscribedButtons = _settings.MqttButtonsEnabled;

                connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                if (_role == CompanionRuntimeRole.App && _settings.MqttMediaPlayerEnabled && _mediaSessionService is not null)
                {
                    await _mediaSessionService.StartAsync(
                        PublishMediaStateAsync,
                        thumbnail => PublishRawAsync(
                            $"hass.agent/media_player/{TopicId}/thumbnail",
                            thumbnail,
                            retain: false),
                        connectionCts.Token);
                }

                if (ShouldPublishSystemSensors() && _systemMetricsService is not null)
                {
                    systemSensorsTask = Task.Run(
                        () => PublishSystemSensorsLoopAsync(connectionCts.Token),
                        connectionCts.Token);
                }

                if (_role == CompanionRuntimeRole.App)
                {
                    updateTask = Task.Run(
                        () => PublishUpdateLoopAsync(connectionCts.Token),
                        connectionCts.Token);
                }

                while (_client.IsConnected && !_forceReconnect && !cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }

                if (_forceReconnect)
                {
                    _log.Warning("Forcing MQTT reconnect after a stuck publish.");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warning($"MQTT connection loop failed: {ex.Message}");

                // MQTT failed — try WebSocket failover if configured.
                if (_haWs is not null && !cancellationToken.IsCancellationRequested)
                {
                    _log.Info("Switching to HA WebSocket API failover.");
                    try
                    {
                        await RunWebSocketFailoverAsync(tryMqttReconnect: true, cancellationToken);
                        // If we get here, MQTT reconnected — loop will retry MQTT.
                        continue;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception wsEx)
                    {
                        _log.Warning($"HA WebSocket failover also failed: {wsEx.Message}");
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
            finally
            {
                if (connectionCts is not null)
                {
                    await connectionCts.CancelAsync();
                }

                if (systemSensorsTask is not null)
                {
                    try
                    {
                        await systemSensorsTask;
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when the MQTT connection is stopped.
                    }
                    catch (Exception ex)
                    {
                        _log.Warning($"System sensor publisher stopped unexpectedly: {ex.Message}");
                    }
                }

                if (updateTask is not null)
                {
                    try
                    {
                        await updateTask;
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when the MQTT connection is stopped.
                    }
                    catch (Exception ex)
                    {
                        _log.Warning($"Update state publisher stopped unexpectedly: {ex.Message}");
                    }
                }

                connectionCts?.Dispose();
                if (_mediaSessionService is not null)
                {
                    await _mediaSessionService.StopAsync();
                }
            }
        }
    }

    /// <summary>
    /// Runs on the WebSocket transport. Publishes discovery & sensors via HA events.
    /// If <paramref name="tryMqttReconnect"/> is true, periodically checks if MQTT is back
    /// and returns to let the main loop reconnect.
    /// </summary>
    private async Task RunWebSocketFailoverAsync(bool tryMqttReconnect, CancellationToken cancellationToken)
    {
        _isOnWebSocket = true;
        await _haWs!.ConnectAsync(cancellationToken);
        _log.Info("HA WebSocket connected as failover transport.");

        using var wsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Publish discovery via WebSocket.
        await PublishDiscoveryViaWebSocketAsync(wsCts.Token);
        if (_role == CompanionRuntimeRole.App)
        {
            await PublishAvailabilityAsync(online: true);
            await PublishPendingUpdateNotificationAsync();
        }

        // Start the receive loop (handles incoming commands).
        var receiveTask = Task.Run(() => _haWs.ReceiveLoopAsync(wsCts.Token), wsCts.Token);

        // Start media player on WS transport.
        if (_role == CompanionRuntimeRole.App && _settings.MqttMediaPlayerEnabled && _mediaSessionService is not null)
        {
            await _mediaSessionService.StartAsync(
                state => _haWs.PublishMediaStateAsync(state, wsCts.Token),
                thumbnail => _haWs.PublishMediaThumbnailAsync(thumbnail, wsCts.Token),
                wsCts.Token);
        }

        // Start sensor publishing on WS transport.
        Task? sensorTask = null;
        if (ShouldPublishSystemSensors() && _systemMetricsService is not null)
        {
            sensorTask = Task.Run(() => PublishSystemSensorsViaWebSocketLoopAsync(wsCts.Token), wsCts.Token);
        }

        // Heartbeat: the WS transport has no broker Last Will, so the integration
        // marks the device offline if these stop arriving (crash / network loss).
        Task? heartbeatTask = null;
        if (_role == CompanionRuntimeRole.App)
        {
            heartbeatTask = Task.Run(() => PublishWebSocketHeartbeatLoopAsync(wsCts.Token), wsCts.Token);
        }

        try
        {
            if (tryMqttReconnect)
            {
                // Periodically try MQTT reconnection in background.
                while (!cancellationToken.IsCancellationRequested && _haWs.IsConnected)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);

                    if (await TryMqttPingAsync(cancellationToken))
                    {
                        _log.Info("MQTT broker is back online — switching back from WebSocket.");
                        break;
                    }
                }
            }
            else
            {
                // WS-only mode: just wait until connection drops or cancel.
                while (!cancellationToken.IsCancellationRequested && _haWs.IsConnected)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
            }
        }
        finally
        {
            _isOnWebSocket = false;
            await wsCts.CancelAsync();

            if (sensorTask is not null)
            {
                try { await sensorTask; } catch (OperationCanceledException) { }
            }

            if (heartbeatTask is not null)
            {
                try { await heartbeatTask; } catch (OperationCanceledException) { }
            }

            try { await receiveTask; } catch (OperationCanceledException) { }

            if (_mediaSessionService is not null)
            {
                await _mediaSessionService.StopAsync();
            }

            await _haWs.DisconnectAsync();
        }
    }

    private async Task PublishDiscoveryViaWebSocketAsync(CancellationToken cancellationToken)
    {
        var systemSensorsEnabled = _settings.MqttSystemSensorsEnabled;
        var buttonsEnabled = _settings.MqttButtonsEnabled;

        var apis = new ApiCapabilitiesResponse(
            _settings.MqttNotificationsEnabled,
            _settings.MqttMediaPlayerEnabled,
            buttonsEnabled,
            systemSensorsEnabled,
            true,
            buttonsEnabled ? BuildCommandDescriptors(_settings.TrayAppCommands) : [],
            systemSensorsEnabled ? BuildCustomSensorDescriptors(serviceRole: false) : [],
            systemSensorsEnabled ? BuildStandardSensorDescriptors(serviceRole: false) : [],
            buttonsEnabled ? BuildCustomCommandDescriptors(serviceRole: false) : []);

        await _haWs!.PublishDeviceDiscoveryAsync(new
        {
            serial_number = _settings.SerialNumber,
            device = new
            {
                name = _settings.DeviceName,
                manufacturer = _settings.Manufacturer,
                model = _settings.Model,
                sw_version = _settings.SoftwareVersion
            },
            apis
        }, cancellationToken);
    }

    private async Task PublishWebSocketHeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _haWs is not null)
        {
            try
            {
                await _haWs.PublishAvailabilityAsync(online: true, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Debug($"WS heartbeat failed: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        }
    }

    private async Task PublishSystemSensorsViaWebSocketLoopAsync(CancellationToken cancellationToken)
    {
        var intervals = new Dictionary<SensorPollingProfile, TimeSpan>
        {
            [SensorPollingProfile.Fast] = TimeSpan.FromSeconds(_settings.FastSensorIntervalSeconds),
            [SensorPollingProfile.Normal] = TimeSpan.FromSeconds(_settings.NormalSensorIntervalSeconds),
            [SensorPollingProfile.Hourly] = TimeSpan.FromSeconds(_settings.HourlySensorIntervalSeconds),
            [SensorPollingProfile.Startup] = Timeout.InfiniteTimeSpan
        };

        var now = DateTimeOffset.UtcNow;
        var nextDue = new Dictionary<SensorPollingProfile, DateTimeOffset>
        {
            [SensorPollingProfile.Fast] = now,
            [SensorPollingProfile.Normal] = now,
            [SensorPollingProfile.Hourly] = now,
            [SensorPollingProfile.Startup] = now
        };

        while (!cancellationToken.IsCancellationRequested)
        {
            now = DateTimeOffset.UtcNow;
            var dueProfiles = nextDue
                .Where(item => item.Value <= now)
                .Select(item => item.Key)
                .ToHashSet();

            if (dueProfiles.Count == 0)
            {
                var next = nextDue.Values.Min();
                var delay = next > now ? next - now : TimeSpan.FromSeconds(1);
                var pushed = await WaitForPushOrDelayAsync(delay, cancellationToken);
                if (pushed)
                {
                    var pushData = _systemMetricsService?.Read(
                        _settings.CustomSensors,
                        _role == CompanionRuntimeRole.Service,
                        PushProfiles);
                    if (pushData is not null)
                    {
                        await _haWs!.PublishSensorStateAsync(new
                        {
                            serial_number = _settings.SerialNumber,
                            sensors = pushData
                        }, cancellationToken);
                    }
                }

                continue;
            }

            var sensorData = _systemMetricsService?.Read(
                _settings.CustomSensors,
                _role == CompanionRuntimeRole.Service,
                dueProfiles);

            if (sensorData is not null)
            {
                await _haWs!.PublishSensorStateAsync(new
                {
                    serial_number = _settings.SerialNumber,
                    sensors = sensorData
                }, cancellationToken);
            }

            foreach (var profile in dueProfiles)
            {
                if (intervals[profile] == Timeout.InfiniteTimeSpan)
                {
                    nextDue[profile] = DateTimeOffset.MaxValue;
                    continue;
                }

                nextDue[profile] = now + intervals[profile];
            }
        }
    }

    /// <summary>
    /// Tries a quick MQTT connect/disconnect to see if the broker is reachable.
    /// Returns true if the broker responds, false otherwise.
    /// </summary>
    private async Task<bool> TryMqttPingAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var pingClient = _factory.CreateMqttClient();
            var options = BuildOptions();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            await pingClient.ConnectAsync(options, timeoutCts.Token);
            await pingClient.DisconnectAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private MqttClientOptions BuildOptions()
    {
        var builder = _factory.CreateClientOptionsBuilder()
            .WithClientId(_settings.GetMqttClientId(_role.Token()))
            .WithTcpServer(_settings.MqttHost, _settings.MqttPort)
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .WithTimeout(TimeSpan.FromSeconds(10));

        if (!string.IsNullOrWhiteSpace(_settings.MqttUsername))
        {
            builder.WithCredentials(_settings.MqttUsername, _settings.GetMqttPassword());
        }

        if (_settings.MqttUseTls)
        {
            builder.WithTlsOptions(tls => tls.UseTls());
        }

        var options = builder.Build();

        if (_role == CompanionRuntimeRole.Service)
        {
            options.WillTopic = ServiceStateTopic;
            options.WillPayload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(BuildServiceStatus(online: false), JsonOptions));
            options.WillQualityOfServiceLevel = MqttQualityOfServiceLevel.AtMostOnce;
            options.WillRetain = true;
        }
        else
        {
            // Tray app: Last Will marks the device offline if the connection drops
            // (crash, shutdown, network loss) so HA shows the entities as unavailable.
            options.WillTopic = AvailabilityTopic;
            options.WillPayload = Encoding.UTF8.GetBytes("offline");
            options.WillQualityOfServiceLevel = MqttQualityOfServiceLevel.AtLeastOnce;
            options.WillRetain = true;
        }

        return options;
    }

    private async Task SubscribeAsync(CancellationToken cancellationToken)
    {
        if (_client is not { IsConnected: true })
        {
            return;
        }

        var builder = _factory.CreateSubscribeOptionsBuilder();
        var hasSubscriptions = false;

        if (_role == CompanionRuntimeRole.Service)
        {
            // Always subscribed: silent update installs arrive here even when
            // the user disabled every service command.
            builder.WithTopicFilter(ServiceCommandTopic, MqttQualityOfServiceLevel.AtMostOnce);
            await _client.SubscribeAsync(builder.Build(), cancellationToken);
            return;
        }

        // The HA update entity's Install button publishes here.
        builder.WithTopicFilter(UpdateInstallTopic, MqttQualityOfServiceLevel.AtMostOnce);
        hasSubscriptions = true;

        if (_settings.MqttNotificationsEnabled)
        {
            builder.WithTopicFilter($"hass.agent/notifications/{TopicId}", MqttQualityOfServiceLevel.AtMostOnce);
            hasSubscriptions = true;
        }

        if (_settings.MqttMediaPlayerEnabled)
        {
            builder.WithTopicFilter($"hass.agent/media_player/{TopicId}/cmd", MqttQualityOfServiceLevel.AtMostOnce);
            hasSubscriptions = true;
        }

        if (_settings.MqttButtonsEnabled)
        {
            builder.WithTopicFilter($"hass.agent/buttons/{TopicId}/cmd", MqttQualityOfServiceLevel.AtMostOnce);
            hasSubscriptions = true;
        }

        if (!hasSubscriptions)
        {
            _log.Info("No MQTT subscriptions enabled for tray app role.");
            return;
        }

        await _client.SubscribeAsync(builder.Build(), cancellationToken);
    }

    private async Task HandleMessageAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        var topic = args.ApplicationMessage.Topic;
        var payload = ReadPayload(args.ApplicationMessage);
        _log.Debug($"MQTT ← {topic} ({payload.Length} B)");

        try
        {
            if (topic == $"hass.agent/notifications/{TopicId}")
            {
                var notification = JsonSerializer.Deserialize<NotificationPayload>(payload, JsonOptions);
                if (notification is not null && !string.IsNullOrWhiteSpace(notification.Message))
                {
                    _notificationSink.ShowNotification(notification);
                }

                return;
            }

            if (topic == $"hass.agent/media_player/{TopicId}/cmd")
            {
                var command = JsonSerializer.Deserialize<MediaCommand>(payload, JsonOptions);
                if (command is not null && _mediaSessionService is not null)
                {
                    await _mediaSessionService.HandleCommandAsync(command);
                }
            }

            if (topic == $"hass.agent/buttons/{TopicId}/cmd")
            {
                var command = JsonSerializer.Deserialize<SystemCommandMessage>(payload, JsonOptions);
                if (command is not null)
                {
                    var commandName = GetSystemCommandName(command);
                    if (commandName is null)
                    {
                        _log.Warning("Received empty tray app command.");
                        return;
                    }

                    var customCommand = FindCustomCommand(commandName, serviceRole: false);
                    if (customCommand is not null)
                    {
                        await _systemCommandService.RunCustomCommandAsync(customCommand);
                        return;
                    }

                    if (!_settings.IsTrayAppCommandEnabled(commandName))
                    {
                        _log.Warning($"Unsupported tray app command received: {commandName}");
                        return;
                    }

                    await _systemCommandService.HandleCommandAsync(command);
                }
            }

            if (topic == UpdateInstallTopic && _role == CompanionRuntimeRole.App)
            {
                if (string.Equals(payload.Trim().Trim('"'), "install", StringComparison.OrdinalIgnoreCase))
                {
                    _ = Task.Run(HandleUpdateInstallRequestAsync);
                }

                return;
            }

            if (topic == ServiceCommandTopic)
            {
                var command = JsonSerializer.Deserialize<SystemCommandMessage>(payload, JsonOptions);
                if (command is not null)
                {
                    var commandName = GetSystemCommandName(command);

                    // Internal command, not user-configurable: the tray app asks the
                    // SYSTEM service to install an update without a UAC prompt.
                    if (commandName == "install_update")
                    {
                        if (_role == CompanionRuntimeRole.Service)
                        {
                            _ = Task.Run(RunSilentUpdateInstallAsync);
                        }

                        return;
                    }

                    if (commandName is null)
                    {
                        _log.Warning("Received empty system service command.");
                        return;
                    }

                    var customCommand = FindCustomCommand(commandName, serviceRole: true);
                    if (customCommand is not null)
                    {
                        await _systemCommandService.RunCustomCommandAsync(customCommand);
                        return;
                    }

                    if (!_settings.IsServiceCommandEnabled(commandName))
                    {
                        _log.Warning($"Unsupported system service command received: {commandName}");
                        return;
                    }

                    await _systemCommandService.HandleCommandAsync(command);
                }
            }
        }
        catch (JsonException ex)
        {
            _log.Warning($"Rejected malformed MQTT payload on {topic}: {ex.Message}");
        }
        catch (Exception ex)
        {
            _log.Warning($"Unable to process MQTT message on {topic}: {ex.Message}");
        }
    }

    private async Task PublishDiscoveryAsync(bool offline = false)
    {
        if (_role == CompanionRuntimeRole.Service)
        {
            await PublishJsonAsync(ServiceStateTopic, BuildServiceStatus(!offline), retain: true);
            return;
        }

        var systemSensorsEnabled = !offline && _settings.MqttSystemSensorsEnabled;
        var buttonsEnabled = !offline && _settings.MqttButtonsEnabled;

        var apis = offline
            ? new ApiCapabilitiesResponse(false, false, false, false, false, [], [], [], [])
            : new ApiCapabilitiesResponse(
                _settings.MqttNotificationsEnabled,
                _settings.MqttMediaPlayerEnabled,
                buttonsEnabled,
                systemSensorsEnabled,
                true,
                buttonsEnabled ? BuildCommandDescriptors(_settings.TrayAppCommands) : [],
                systemSensorsEnabled ? BuildCustomSensorDescriptors(serviceRole: false) : [],
                systemSensorsEnabled ? BuildStandardSensorDescriptors(serviceRole: false) : [],
                buttonsEnabled ? BuildCustomCommandDescriptors(serviceRole: false) : []);

        await PublishJsonAsync(
            $"hass.agent/devices/{TopicId}",
            new MqttDiscoveryMessage(
                _settings.SerialNumber,
                new DeviceInfoResponse(
                    _settings.DeviceName,
                    _settings.Manufacturer,
                    _settings.Model,
                    _settings.SoftwareVersion),
                apis),
            retain: _settings.MqttRetainDiscovery);

        if (!offline)
        {
            await PublishHomeAssistantUpdateDiscoveryAsync();
        }
    }

    private async Task PublishLegacyTopicCleanupAsync()
    {
        var legacyTopicId = _settings.DeviceName;
        if (string.IsNullOrWhiteSpace(legacyTopicId) ||
            string.Equals(legacyTopicId, TopicId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await PublishRawAsync($"hass.agent/devices/{legacyTopicId}", null, retain: true);
        await PublishRawAsync($"hass.agent/system/{legacyTopicId}/state", null, retain: true);
        await PublishRawAsync($"hass.agent/update/{legacyTopicId}/state", null, retain: true);
    }

    private async Task PublishMediaStateAsync(MediaStateMessage state)
    {
        await PublishJsonAsync($"hass.agent/media_player/{TopicId}/state", state, retain: false);
    }

    /// <summary>Publishes the device availability (online/offline) for HA's unavailable handling.</summary>
    private async Task PublishAvailabilityAsync(bool online)
    {
        if (_role != CompanionRuntimeRole.App)
        {
            return;
        }

        if (_isOnWebSocket && _haWs is not null)
        {
            // Use None: a graceful offline must still send after the worker token is cancelled.
            await _haWs.PublishAvailabilityAsync(online, CancellationToken.None);
            return;
        }

        await PublishRawAsync(AvailabilityTopic, Encoding.UTF8.GetBytes(online ? "online" : "offline"), retain: true);
    }

    private async Task PublishUpdateLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(UpdateCheckInterval, cancellationToken);
            await PublishUpdateStateAsync();
        }
    }

    private async Task PublishUpdateStateAsync()
    {
        var update = await AppUpdateService.CheckAsync(_settings.SoftwareVersion, _settings.BetaUpdatesEnabled);
        if (!string.IsNullOrWhiteSpace(update.Error))
        {
            _log.Warning($"Unable to publish update state: {update.Error}");
        }

        await PublishJsonAsync(UpdateStateTopic, update, retain: true);
    }

    private async Task PublishHomeAssistantUpdateDiscoveryAsync()
    {
        await PublishJsonAsync(
            $"homeassistant/update/{SanitizeDiscoveryId(_settings.SerialNumber)}/hass_agent_net10/config",
            new MqttUpdateDiscoveryConfig(
                Name: $"{AppIdentity.DisplayName} Update",
                UniqueId: $"{SanitizeDiscoveryId(_settings.SerialNumber)}_hass_agent_net10_update",
                Device: new MqttDiscoveryDevice(
                    Identifiers: [_settings.SerialNumber],
                    Name: _settings.DeviceName,
                    Manufacturer: _settings.Manufacturer,
                    Model: _settings.Model,
                    SoftwareVersion: _settings.SoftwareVersion),
                StateTopic: UpdateStateTopic,
                ValueTemplate: "{{ value_json.installed_version }}",
                LatestVersionTopic: UpdateStateTopic,
                LatestVersionTemplate: "{{ value_json.latest_version }}",
                TitleTopic: UpdateStateTopic,
                TitleTemplate: "{{ value_json.title }}",
                ReleaseUrlTopic: UpdateStateTopic,
                ReleaseUrlTemplate: "{{ value_json.release_url }}",
                JsonAttributesTopic: UpdateStateTopic,
                CommandTopic: UpdateInstallTopic,
                PayloadInstall: "install"),
            retain: _settings.MqttRetainDiscovery);
    }

    /// <summary>
    /// Requests an immediate (debounced) sensor publish. Called by interactive state
    /// sources (monitor power, session lock, AC/battery) so changes reach HA at once.
    /// </summary>
    public void TriggerPushUpdate()
    {
        if (_role != CompanionRuntimeRole.App)
        {
            return;
        }

        lock (_pushDebounceLock)
        {
            _pushDebounceTimer ??= new System.Threading.Timer(_ =>
            {
                try
                {
                    _pushSignal.Release();
                }
                catch (SemaphoreFullException)
                {
                    // A push is already pending; the loop will pick it up.
                }
            });
            _pushDebounceTimer.Change(PushDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Waits for either the poll delay or a push signal. Returns true if a push woke us.</summary>
    private async Task<bool> WaitForPushOrDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var signalTask = _pushSignal.WaitAsync(cts.Token);
        var delayTask = Task.Delay(delay, cts.Token);
        var completed = await Task.WhenAny(signalTask, delayTask);

        cts.Cancel(); // cancel the loser so it doesn't linger / consume a permit later
        try
        {
            await Task.WhenAll(signalTask, delayTask);
        }
        catch
        {
            // Expected cancellation of the losing task.
        }

        return completed == signalTask && signalTask.IsCompletedSuccessfully;
    }

    // Reads and publishes the sensor state. A read failure (e.g. a transient COM/WMI
    // hiccup) must not kill the loop, so it is caught and logged with a full stack.
    private async Task PublishSensorsAsync(IReadOnlySet<SensorPollingProfile> profiles)
    {
        if (_systemMetricsService is null)
        {
            return;
        }

        SystemMetricsMessage message;
        try
        {
            message = _systemMetricsService.Read(_settings.CustomSensors, _role == CompanionRuntimeRole.Service, profiles);
        }
        catch (Exception ex)
        {
            _log.Warning($"Sensor read failed, skipping this cycle: {ex}");
            return;
        }

        await PublishJsonAsync($"hass.agent/sensors/{TopicId}/state", message, retain: false);
    }

    private async Task PublishSystemSensorsLoopAsync(CancellationToken cancellationToken)
    {
        var intervals = new Dictionary<SensorPollingProfile, TimeSpan>
        {
            [SensorPollingProfile.Fast] = TimeSpan.FromSeconds(_settings.FastSensorIntervalSeconds),
            [SensorPollingProfile.Normal] = TimeSpan.FromSeconds(_settings.NormalSensorIntervalSeconds),
            [SensorPollingProfile.Hourly] = TimeSpan.FromSeconds(_settings.HourlySensorIntervalSeconds),
            [SensorPollingProfile.Startup] = Timeout.InfiniteTimeSpan
        };

        var now = DateTimeOffset.UtcNow;
        var nextDue = new Dictionary<SensorPollingProfile, DateTimeOffset>
        {
            [SensorPollingProfile.Fast] = now,
            [SensorPollingProfile.Normal] = now,
            [SensorPollingProfile.Hourly] = now,
            [SensorPollingProfile.Startup] = now
        };

        while (!cancellationToken.IsCancellationRequested)
        {
            now = DateTimeOffset.UtcNow;
            var dueProfiles = nextDue
                .Where(item => item.Value <= now)
                .Select(item => item.Key)
                .ToHashSet();

            if (dueProfiles.Count == 0)
            {
                var next = nextDue.Values.Min();
                var delay = next > now ? next - now : TimeSpan.FromSeconds(1);
                var pushed = await WaitForPushOrDelayAsync(delay, cancellationToken);
                if (pushed)
                {
                    // Interactive state changed — publish the push profiles immediately.
                    await PublishSensorsAsync(PushProfiles);
                }

                continue;
            }

            await PublishSensorsAsync(dueProfiles);

            foreach (var profile in dueProfiles)
            {
                if (intervals[profile] == Timeout.InfiniteTimeSpan)
                {
                    nextDue[profile] = DateTimeOffset.MaxValue;
                    continue;
                }

                nextDue[profile] = now + intervals[profile];
            }
        }
    }

    private MqttServiceStatusMessage BuildServiceStatus(bool online)
    {
        var systemSensorsEnabled = online && _settings.MqttServiceSystemSensorsEnabled;

        return new MqttServiceStatusMessage(
            online,
            _role.Token(),
            online ? BuildCommandDescriptors(_settings.ServiceCommands) : [],
            systemSensorsEnabled,
            systemSensorsEnabled ? BuildCustomSensorDescriptors(serviceRole: true) : [],
            systemSensorsEnabled ? BuildStandardSensorDescriptors(serviceRole: true) : [],
            DateTimeOffset.UtcNow,
            online ? BuildCustomCommandDescriptors(serviceRole: true) : []);
    }

    private static IReadOnlyList<SystemCommandDescriptor> BuildCommandDescriptors(IEnumerable<string> commands)
    {
        return commands
            .Select(command => SystemCommandCatalog.TryNormalizeName(command, out var normalized) ? normalized : string.Empty)
            .Where(command => !string.IsNullOrEmpty(command))
            .Select(command => new SystemCommandDescriptor(
                command,
                Localization.Strings.GetHa($"Cmd.{command}"),
                command is "shutdown" or "restart" ? Localization.Strings.GetHa($"Cmd.{command}_comment") : null))
            .ToList();
    }

    private IReadOnlyList<BuiltInSensorDescriptor> BuildStandardSensorDescriptors(bool serviceRole)
    {
        return _settings.BuiltInSensors
            .Where(sensor => serviceRole ? sensor.Service : sensor.TrayApp)
            .Select(sensor => BuiltInSensorCatalog.Find(sensor.Key))
            .Where(sensor => sensor is not null)
            .Select(sensor => new BuiltInSensorDescriptor(
                sensor!.Key,
                Localization.Strings.GetHa($"Sensor.{sensor.Key}"),
                SensorPollingProfiles.ToKey(sensor.PollingProfile),
                sensor.HasMultipleValues,
                sensor.DefaultAttributePath,
                sensor.AttributePaths,
                sensor.DeviceClass,
                sensor.Options))
            .ToList();
    }

    private IReadOnlyList<CustomSensorDescriptor> BuildCustomSensorDescriptors(bool serviceRole)
    {
        return _settings.CustomSensors
            .Where(sensor => sensor.Enabled && (serviceRole ? sensor.Service : sensor.TrayApp))
            .Select(sensor =>
            {
                // A user-set unit (e.g. °C on a command sensor) wins; disk_free keeps its
                // built-in GiB. Any sensor that carries a unit is a numeric measurement.
                var unit = !string.IsNullOrWhiteSpace(sensor.Unit)
                    ? sensor.Unit.Trim()
                    : (sensor.IsDiskFree ? "GiB" : null);

                return new CustomSensorDescriptor(
                sensor.Id,
                sensor.Type,
                sensor.Name,
                sensor.Parameter,
                SensorPollingProfiles.NormalizeKey(sensor.PollingProfile, SensorPollingProfile.Normal),
                unit,
                null,
                unit is not null ? "measurement" : null,
                sensor.Type switch
                {
                    CustomSensorTypes.ProcessRunning => "mdi:application-cog",
                    CustomSensorTypes.ServiceStatus => "mdi:cog-sync",
                    CustomSensorTypes.DiskFree => "mdi:harddisk",
                    CustomSensorTypes.BuiltInAttribute => "mdi:table-column-plus-after",
                    CustomSensorTypes.Command => "mdi:console",
                    CustomSensorTypes.CommandPowerShell => "mdi:powershell",
                    CustomSensorTypes.CommandPwsh => "mdi:powershell",
                    _ => "mdi:gauge"
                });
            })
            .ToList();
    }

    private bool ShouldPublishSystemSensors()
    {
        return _role == CompanionRuntimeRole.Service
            ? _settings.MqttServiceSystemSensorsEnabled
            : _settings.MqttSystemSensorsEnabled;
    }

    private static string? GetSystemCommandName(SystemCommandMessage message)
    {
        return message.RestartCancel
            ? "restart_cancel"
            : message.Command?.Trim().ToLowerInvariant();
    }

    private IReadOnlyList<CustomCommandDescriptor> BuildCustomCommandDescriptors(bool serviceRole)
    {
        return _settings.CustomCommands
            .Where(command => command.Enabled && (serviceRole ? command.Service : command.TrayApp))
            .Select(command => new CustomCommandDescriptor(command.Id, command.Name))
            .ToList();
    }

    private CustomCommandDefinition? FindCustomCommand(string id, bool serviceRole)
    {
        return _settings.CustomCommands.FirstOrDefault(command =>
            command.Enabled &&
            (serviceRole ? command.Service : command.TrayApp) &&
            string.Equals(command.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private async Task PublishJsonAsync(string topic, object payload, bool retain)
    {
        var client = _client;
        if (client is not { IsConnected: true })
        {
            return;
        }

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var message = _factory.CreateApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(json)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
            .WithRetainFlag(retain)
            .Build();

        if (await TryPublishAsync(client, message, topic))
        {
            _log.Debug($"MQTT → {topic} ({json.Length} B)");
        }
    }

    private async Task PublishRawAsync(string topic, byte[]? payload, bool retain)
    {
        var client = _client;
        if (client is not { IsConnected: true })
        {
            return;
        }

        var message = _factory.CreateApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload ?? [])
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
            .WithRetainFlag(retain)
            .Build();

        await TryPublishAsync(client, message, topic);
    }

    /// <summary>
    /// Publishes with a caller-side timeout. MQTTnet's PublishAsync can get stuck in a
    /// way its own cancellation token does not unblock, which would freeze every
    /// publisher (sensors, media) on the shared client. Task.WhenAny guarantees we
    /// return after the timeout regardless; a stuck publish is orphaned and the run
    /// loop is told to reconnect via _forceReconnect.
    /// </summary>
    private async Task<bool> TryPublishAsync(IMqttClient client, MqttApplicationMessage message, string topic)
    {
        if (!await _publishLock.WaitAsync(PublishTimeout))
        {
            _log.Warning($"MQTT publish lock stuck >{PublishTimeout.TotalSeconds:0}s before {topic}; forcing reconnect.");
            _forceReconnect = true;
            return false;
        }

        try
        {
            Task publishTask;
            try
            {
                publishTask = client.PublishAsync(message, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.Warning($"MQTT publish to {topic} threw ({ex.GetType().Name}); forcing reconnect.");
                _forceReconnect = true;
                return false;
            }

            var winner = await Task.WhenAny(publishTask, Task.Delay(PublishTimeout));
            if (winner != publishTask)
            {
                // Stuck publish — leave it orphaned (observe its exception later) and reconnect.
                _ = publishTask.ContinueWith(static t => { _ = t.Exception; }, TaskScheduler.Default);
                _log.Warning($"MQTT publish to {topic} stuck >{PublishTimeout.TotalSeconds:0}s; forcing reconnect.");
                _forceReconnect = true;
                return false;
            }

            try
            {
                await publishTask;
                return true;
            }
            catch (Exception ex)
            {
                _log.Warning($"MQTT publish to {topic} failed ({ex.GetType().Name}); forcing reconnect.");
                _forceReconnect = true;
                return false;
            }
        }
        finally
        {
            _publishLock.Release();
        }
    }

    private static string ReadPayload(MqttApplicationMessage message)
    {
        return Encoding.UTF8.GetString(message.Payload.ToArray());
    }

    private static string SanitizeDiscoveryId(string value)
    {
        var id = new string(value
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray());

        return string.IsNullOrWhiteSpace(id) ? "hass_agent_net10" : id;
    }

    private string TopicId => _settings.SerialNumber;

    private string ServiceStateTopic => $"hass.agent/system/{TopicId}/state";

    private string ServiceCommandTopic => $"hass.agent/system/{TopicId}/cmd";

    private string UpdateStateTopic => $"hass.agent/update/{TopicId}/state";

    private string UpdateInstallTopic => $"hass.agent/update/{TopicId}/install";

    private string PersistentNotificationTopic => $"hass.agent/persistent_notification/{TopicId}";

    private string AvailabilityTopic => $"hass.agent/devices/{TopicId}/availability";
}

internal sealed record MqttDiscoveryMessage(
    [property: JsonPropertyName("serial_number")] string SerialNumber,
    [property: JsonPropertyName("device")] DeviceInfoResponse Device,
    [property: JsonPropertyName("apis")] ApiCapabilitiesResponse Apis);

internal sealed record NotificationActionMessage(
    [property: JsonPropertyName("device_name")] string DeviceName,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("input")] string? Input);

internal sealed record MqttServiceStatusMessage(
    [property: JsonPropertyName("online")] bool Online,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("commands")] IReadOnlyList<SystemCommandDescriptor> Commands,
    [property: JsonPropertyName("system_sensors")] bool SystemSensors,
    [property: JsonPropertyName("custom_sensors")] IReadOnlyList<CustomSensorDescriptor> CustomSensors,
    [property: JsonPropertyName("standard_sensors")] IReadOnlyList<BuiltInSensorDescriptor> StandardSensors,
    [property: JsonPropertyName("published_at")] DateTimeOffset PublishedAt,
    [property: JsonPropertyName("custom_commands")] IReadOnlyList<CustomCommandDescriptor>? CustomCommands = null);

internal sealed record MqttUpdateDiscoveryConfig(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("unique_id")] string UniqueId,
    [property: JsonPropertyName("device")] MqttDiscoveryDevice Device,
    [property: JsonPropertyName("state_topic")] string StateTopic,
    [property: JsonPropertyName("value_template")] string ValueTemplate,
    [property: JsonPropertyName("latest_version_topic")] string LatestVersionTopic,
    [property: JsonPropertyName("latest_version_template")] string LatestVersionTemplate,
    [property: JsonPropertyName("title_topic")] string TitleTopic,
    [property: JsonPropertyName("title_template")] string TitleTemplate,
    [property: JsonPropertyName("release_url_topic")] string ReleaseUrlTopic,
    [property: JsonPropertyName("release_url_template")] string ReleaseUrlTemplate,
    [property: JsonPropertyName("json_attributes_topic")] string JsonAttributesTopic,
    [property: JsonPropertyName("command_topic")] string CommandTopic,
    [property: JsonPropertyName("payload_install")] string PayloadInstall);

internal sealed record PersistentNotificationMessage(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("message")] string Message);

internal sealed record MqttDiscoveryDevice(
    [property: JsonPropertyName("identifiers")] IReadOnlyList<string> Identifiers,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("manufacturer")] string Manufacturer,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("sw_version")] string SoftwareVersion);
