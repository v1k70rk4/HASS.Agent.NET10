using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Http;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.Media;
using HASS.Agent.Companion.SystemCommands;

namespace HASS.Agent.Companion.Mqtt;

/// <summary>
/// Manages a WebSocket connection to the Home Assistant WebSocket API.
/// Used as a failover transport when MQTT is unavailable.
/// Protocol: <see href="https://developers.home-assistant.io/docs/api/websocket"/>
/// </summary>
internal sealed class HaWebSocketService : IDisposable
{
    public const string IntegrationDomain = "hass_agent";
    public const string MinimumIntegrationVersion = "10.6.7";

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly CompanionSettings _settings;
    private readonly FileLog _log;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _ws;
    private int _messageId;
    private int? _commandSubscriptionId;

    // The integration's own commands (10.9.1+): hass_agent/fire and hass_agent/subscribe,
    // which a token of a user who is not an administrator may use. Without them, Home
    // Assistant's fire_event and subscribe_events, which take an administrator's token.
    private bool _ownCommands;
    private bool _approvalNoticeLogged;

    // Requests made while the receive loop runs: their result comes in through HandleMessage.
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();

    /// <summary>
    /// Whether the token's user is a Home Assistant administrator; null when not known (an
    /// integration older than 10.9.1, or not connected).
    /// </summary>
    public bool? TokenUserIsAdmin { get; private set; }

    /// <summary>The integration offers its own commands (10.9.1+), provisioning among them.</summary>
    public bool HasOwnCommands => _ownCommands;

    /// <summary>Fired when a notification is received from HA via the event bus.</summary>
    public event Action<NotificationPayload>? NotificationReceived;

    /// <summary>Fired when a media command is received from HA via the event bus.</summary>
    public event Action<MediaCommand>? MediaCommandReceived;

    /// <summary>Fired when a button/system command is received from HA via the event bus.</summary>
    /// <summary>
    /// Raised for an incoming button command. The second argument is the role the
    /// integration addressed it to ("app"/"service"), or null when it came from an
    /// older integration that does not target commands.
    /// </summary>
    public event Action<SystemCommandMessage, string?>? ButtonCommandReceived;

    /// <summary>Raised when Home Assistant's update entity asks us to install.</summary>
    public event Action? UpdateInstallRequested;

    /// <summary>
    /// The integration asks for the device data again (10.9.0+): it has just been set up,
    /// and what was sent on connect may have arrived before it listened.
    /// </summary>
    public event Action? AnnounceRequested;
    /// <summary>The tray app asks the service to run the silent install (internal, addressed to the service role).</summary>
    public event Action<string?>? SilentInstallRequested;

    public bool IsConnected => _ws is { State: WebSocketState.Open };

    public HaWebSocketService(CompanionSettings settings, FileLog log)
    {
        _settings = settings;
        _log = log;
    }

    /// <summary>
    /// Connects to the HA WebSocket API, authenticates, and subscribes to command events.
    /// Returns when the connection is ready to use.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        _ws?.Dispose();
        _ws = new ClientWebSocket();
        _messageId = 0;
        _commandSubscriptionId = null;
        _ownCommands = false;
        _approvalNoticeLogged = false;
        TokenUserIsAdmin = null;

        var wsUrl = BuildWebSocketUrl();
        _log.Info($"HA WebSocket connecting to {wsUrl}");

        await _ws.ConnectAsync(new Uri(wsUrl), cancellationToken);
        _log.Info("HA WebSocket TCP connected, awaiting auth_required.");

        // Step 1: Read the auth_required message.
        using var authRequired = await ReceiveMessageAsync(cancellationToken);
        var authType = authRequired.RootElement.GetProperty("type").GetString();
        if (authType != "auth_required")
        {
            throw new InvalidOperationException($"Expected auth_required, got: {authType}");
        }

        // Step 2: Send auth message.
        var token = _settings.GetHaApiToken();
        await SendAsync(new { type = "auth", access_token = token }, cancellationToken);

        // Step 3: Read auth result.
        using var authResult = await ReceiveMessageAsync(cancellationToken);
        var resultType = authResult.RootElement.GetProperty("type").GetString();
        if (resultType == "auth_invalid")
        {
            var message = authResult.RootElement.TryGetProperty("message", out var msg) ? msg.GetString() : "unknown";
            throw new InvalidOperationException($"HA WebSocket authentication failed: {message}");
        }

        if (resultType != "auth_ok")
        {
            throw new InvalidOperationException($"Expected auth_ok, got: {resultType}");
        }

        _log.Info("HA WebSocket authenticated.");

        // Step 4: Check the installed HASS.Agent integration version.
        await LogIntegrationVersionCompatibilityAsync(cancellationToken);

        // Step 5: Subscribe to hass_agent_command events.
        await SubscribeToCommandEventsAsync(cancellationToken);

        // Step 6: whose token this is. An administrator's token can be swapped for a user
        // of the PC's own (the app offers it), which only the integration's commands allow.
        if (_ownCommands)
        {
            var id = await SendWithIdAsync(i => new { id = i, type = "auth/current_user" }, cancellationToken);
            var (ok, _, _, result) = await ReadResultAsync(id, cancellationToken);
            TokenUserIsAdmin = ok && result is { } user && user.TryGetProperty("is_admin", out var admin)
                ? admin.GetBoolean()
                : null;
        }
    }

    /// <summary>
    /// Asks the integration for a Home Assistant user of this PC's own, not an administrator,
    /// and a token for it (an administrator's token only). Returns the token and the user's name.
    /// </summary>
    public async Task<(string Token, string User)> ProvisionUserAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected || !_ownCommands)
        {
            throw new InvalidOperationException("not connected to a HASS.Agent integration 10.9.1 or newer");
        }

        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        int requestId = 0;
        await SendWithIdAsync(id =>
        {
            requestId = id;
            _pending[id] = completion;
            return new { id, type = "hass_agent/provision", serial_number = _settings.SerialNumber };
        }, cancellationToken);

        try
        {
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            var token = result.GetProperty("access_token").GetString();
            var user = result.TryGetProperty("user", out var name) ? name.GetString() : null;
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("Home Assistant returned no token");
            }

            return (token, user ?? string.Empty);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    /// Main receive loop. Processes incoming messages (event subscriptions, pong responses).
    /// Blocks until the connection closes or cancellation is requested.
    /// </summary>
    public async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = HeartbeatLoopAsync(heartbeatCts.Token);

        try
        {
            while (!cancellationToken.IsCancellationRequested && IsConnected)
            {
                JsonDocument message;
                try
                {
                    message = await ReceiveMessageAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (WebSocketException)
                {
                    break;
                }

                using (message)
                {
                    try
                    {
                        HandleMessage(message);
                    }
                    catch (Exception ex)
                    {
                        _log.Warning($"HA WebSocket message handling error: {ex.Message}");
                    }
                }
            }
        }
        finally
        {
            await heartbeatCts.CancelAsync();
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException)
            {
                // Expected.
            }
        }
    }

    /// <summary>Fires a custom event on the HA event bus via the WebSocket API.</summary>
    public async Task FireEventAsync(string eventType, object eventData, CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            return;
        }

        if (_ownCommands)
        {
            await SendWithIdAsync(id => new
            {
                id,
                type = "hass_agent/fire",
                event_type = eventType,
                event_data = eventData
            }, cancellationToken);
            return;
        }

        await SendWithIdAsync(id => new
        {
            id,
            type = "fire_event",
            event_type = eventType,
            event_data = eventData
        }, cancellationToken);
    }

    /// <summary>Publishes a device discovery payload as a hass_agent_device_update event.</summary>
    public async Task PublishDeviceDiscoveryAsync(object discoveryPayload, CancellationToken cancellationToken)
    {
        await FireEventAsync("hass_agent_device_update", discoveryPayload, cancellationToken);
    }

    /// <summary>
    /// Publishes the system service's own status as a hass_agent_service_update event.
    /// Mirrors the dedicated MQTT service topic: the app and the service each advertise
    /// what they can handle, and the integration merges the two.
    /// </summary>
    public async Task PublishServiceStatusAsync(object statusPayload, CancellationToken cancellationToken)
    {
        await FireEventAsync("hass_agent_service_update", statusPayload, cancellationToken);
    }

    /// <summary>
    /// Publishes the app update state as a hass_agent_update_state event. Over MQTT the
    /// update entity comes from Home Assistant's own discovery, which needs a broker;
    /// on this transport the integration builds it from these events instead.
    /// </summary>
    public async Task PublishUpdateStateAsync(object statePayload, CancellationToken cancellationToken)
    {
        await FireEventAsync("hass_agent_update_state", statePayload, cancellationToken);
    }

    /// <summary>Publishes sensor state data as a hass_agent_sensor_update event.</summary>
    public async Task PublishSensorStateAsync(object sensorPayload, CancellationToken cancellationToken)
    {
        await FireEventAsync("hass_agent_sensor_update", sensorPayload, cancellationToken);
    }

    /// <summary>Publishes media player state as a hass_agent_media_update event.</summary>
    public async Task PublishMediaStateAsync(MediaStateMessage state, CancellationToken cancellationToken)
    {
        await FireEventAsync("hass_agent_media_update", new
        {
            serial_number = _settings.SerialNumber,
            state
        }, cancellationToken);
    }

    /// <summary>Publishes a media thumbnail as a base64 hass_agent_media_thumbnail event.</summary>
    public async Task PublishMediaThumbnailAsync(byte[]? thumbnail, CancellationToken cancellationToken)
    {
        var base64 = thumbnail is { Length: > 0 } ? Convert.ToBase64String(thumbnail) : null;
        await FireEventAsync("hass_agent_media_thumbnail", new
        {
            serial_number = _settings.SerialNumber,
            thumbnail = base64
        }, cancellationToken);
    }

    /// <summary>Publishes a notification action response.</summary>
    public async Task PublishNotificationActionAsync(
        string action,
        IReadOnlyDictionary<string, string>? input,
        CancellationToken cancellationToken)
    {
        await FireEventAsync("hass_agent_notification_action", new
        {
            serial_number = _settings.SerialNumber,
            action,
            input,
            created_at = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    /// <summary>A registered hotkey was pressed.</summary>
    public async Task PublishHotkeyAsync(HotkeyDefinition hotkey, CancellationToken cancellationToken)
    {
        await FireEventAsync("hass_agent_hotkey", new
        {
            serial_number = _settings.SerialNumber,
            id = hotkey.Id,
            hotkey = hotkey.Name,
            keys = hotkey.Keys,
            created_at = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    /// <summary>Asks the integration to create a Home Assistant persistent notification.</summary>
    public async Task PublishPersistentNotificationAsync(string title, string message, CancellationToken cancellationToken)
    {
        await FireEventAsync("hass_agent_persistent_notification", new
        {
            serial_number = _settings.SerialNumber,
            title,
            message
        }, cancellationToken);
    }

    /// <summary>Reports device availability (online/offline) over the WebSocket transport.</summary>
    public async Task PublishAvailabilityAsync(bool online, CancellationToken cancellationToken)
    {
        await FireEventAsync("hass_agent_availability", new
        {
            serial_number = _settings.SerialNumber,
            online
        }, cancellationToken);
    }

    /// <summary>
    /// Tests the connection by connecting, reading the HA and integration versions, and disconnecting.
    /// Returns version details on success, or throws on connection/auth failure.
    /// </summary>
    public async Task<HaConnectionTestResult> TestConnectionAsync(string url, string token, CancellationToken cancellationToken)
    {
        using var ws = new ClientWebSocket();
        var wsUrl = BuildWebSocketUrl(url);
        var messageId = 0;

        await ws.ConnectAsync(new Uri(wsUrl), cancellationToken);

        using var authRequired = await ReceiveMessageAsync(ws, cancellationToken);
        var haVersion = authRequired.RootElement.TryGetProperty("ha_version", out var ver) ? ver.GetString() : null;

        await SendAsync(ws, new { type = "auth", access_token = token }, cancellationToken);

        using var authResult = await ReceiveMessageAsync(ws, cancellationToken);
        var resultType = authResult.RootElement.GetProperty("type").GetString();
        if (resultType == "auth_invalid")
        {
            var msg = authResult.RootElement.TryGetProperty("message", out var m) ? m.GetString() : "unknown";
            throw new InvalidOperationException(msg ?? "Authentication failed");
        }

        if (resultType != "auth_ok")
        {
            throw new InvalidOperationException($"Unexpected response: {resultType}");
        }

        var integration = await TryGetIntegrationVersionAsync(ws, NextTestId(ref messageId), cancellationToken);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "test", cancellationToken);
        return new HaConnectionTestResult(
            haVersion ?? "unknown",
            integration.Version,
            integration.Error);
    }

    public static bool IsIntegrationVersionSupported(string? version)
    {
        return TryParseVersion(version, out var installed) &&
            TryParseVersion(MinimumIntegrationVersion, out var minimum) &&
            installed >= minimum;
    }

    public async Task DisconnectAsync()
    {
        if (_ws is { State: WebSocketState.Open })
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", cts.Token);
            }
            catch
            {
                // Best effort close.
            }
        }

        _ws?.Dispose();
        _ws = null;
    }

    public void Dispose()
    {
        _ws?.Dispose();
        _sendLock.Dispose();
    }

    // ───── Private helpers ─────

    private string BuildWebSocketUrl()
    {
        return BuildWebSocketUrl(_settings.HaApiUrl);
    }

    internal static string BuildWebSocketUrl(string url)
    {
        url = url.Trim().TrimEnd('/');
        if (url.Length > 0 && !url.Contains("://", StringComparison.Ordinal))
        {
            // No scheme: plain only inside the home network (see CompanionSettings.IsLocalAddress).
            url = (CompanionSettings.IsLocalAddress(url) ? "ws://" : "wss://") + url;
        }

        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "wss://" + url["https://".Length..];
        }
        else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            url = "ws://" + url["http://".Length..];
        }
        else if (!url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
                 !url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            url = "ws://" + url;
        }

        if (!url.EndsWith("/api/websocket", StringComparison.OrdinalIgnoreCase))
        {
            url += "/api/websocket";
        }

        return url;
    }

    private async Task SubscribeToCommandEventsAsync(CancellationToken cancellationToken)
    {
        // The integration's own subscription first: it only carries this PC's commands, and
        // works with a token of a user who is not an administrator.
        var ownId = await SendWithIdAsync(id => new
        {
            id,
            type = "hass_agent/subscribe",
            serial_number = _settings.SerialNumber
        }, cancellationToken);
        var (ok, code, message, _) = await ReadResultAsync(ownId, cancellationToken);
        if (ok)
        {
            _commandSubscriptionId = ownId;
            _ownCommands = true;
            _log.Info("HA WebSocket subscribed to this PC's commands (the token's user need not be an administrator).");
            return;
        }

        if (code != "unknown_command")
        {
            throw new InvalidOperationException($"HA WebSocket hass_agent/subscribe failed: {code} {message}");
        }

        // An integration older than 10.9.1, or one that is not set up yet.
        var subscriptionId = await SendWithIdAsync(id => new
        {
            id,
            type = "subscribe_events",
            event_type = "hass_agent_command"
        }, cancellationToken);
        _commandSubscriptionId = subscriptionId;

        (ok, code, message, _) = await ReadResultAsync(subscriptionId, cancellationToken);
        if (!ok)
        {
            throw new InvalidOperationException(code == "unauthorized"
                ? "HA WebSocket: Home Assistant refused the subscription. The token's user is not an administrator, " +
                  "which needs the HASS.Agent integration 10.9.1 or newer, set up with at least one device; " +
                  "or add this PC with the integration's \"HA API\" option, which waits for it."
                : $"HA WebSocket subscribe_events failed: {code} {message}");
        }

        _log.Info("HA WebSocket subscribed to hass_agent_command events.");
    }

    /// <summary>The result of one command: success with its result, or the error code and message Home Assistant gave.</summary>
    private async Task<(bool Success, string? Code, string? Message, JsonElement? Result)> ReadResultAsync(int id, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var message = await ReceiveMessageAsync(cancellationToken);
            if (!message.RootElement.TryGetProperty("type", out var typeElement) ||
                typeElement.GetString() != "result" ||
                !message.RootElement.TryGetProperty("id", out var idElement) ||
                idElement.GetInt32() != id)
            {
                HandleMessage(message);
                continue;
            }

            if (message.RootElement.TryGetProperty("success", out var successElement) && successElement.GetBoolean())
            {
                return (true, null, null,
                    message.RootElement.TryGetProperty("result", out var resultElement) ? resultElement.Clone() : null);
            }

            string? code = null, text = null;
            if (message.RootElement.TryGetProperty("error", out var error))
            {
                code = error.TryGetProperty("code", out var codeElement) ? codeElement.GetString() : null;
                text = error.TryGetProperty("message", out var messageElement) ? messageElement.GetString() : null;
            }

            return (false, code, text, null);
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private async Task LogIntegrationVersionCompatibilityAsync(CancellationToken cancellationToken)
    {
        try
        {
            var ws = _ws ?? throw new InvalidOperationException("WebSocket is not connected.");
            var integration = await TryGetIntegrationVersionAsync(ws, NextId(), cancellationToken);
            if (!string.IsNullOrWhiteSpace(integration.Error))
            {
                _log.Warning($"Unable to check HASS.Agent integration version: {integration.Error}");
                return;
            }

            if (string.IsNullOrWhiteSpace(integration.Version))
            {
                _log.Warning($"HASS.Agent integration '{IntegrationDomain}' was not found in Home Assistant. Required version: {MinimumIntegrationVersion}+.");
                return;
            }

            if (!IsIntegrationVersionSupported(integration.Version))
            {
                _log.Warning($"HASS.Agent integration version {integration.Version} is older than required {MinimumIntegrationVersion}. Please update the Home Assistant integration.");
                return;
            }

            _log.Info($"HASS.Agent integration version OK: {integration.Version}.");
        }
        catch (Exception ex)
        {
            _log.Warning($"Unable to check HASS.Agent integration version: {ex.Message}");
        }
    }

    private static async Task<HaIntegrationVersionInfo> TryGetIntegrationVersionAsync(ClientWebSocket ws, int id, CancellationToken cancellationToken)
    {
        try
        {
            await SendAsync(ws, new
            {
                id,
                type = "manifest/get",
                integration = IntegrationDomain
            }, cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                using var message = await ReceiveMessageAsync(ws, cancellationToken);
                if (!message.RootElement.TryGetProperty("type", out var typeElement) ||
                    typeElement.GetString() != "result")
                {
                    continue;
                }

                if (!message.RootElement.TryGetProperty("id", out var idElement) ||
                    idElement.GetInt32() != id)
                {
                    continue;
                }

                var success = message.RootElement.TryGetProperty("success", out var successElement) &&
                    successElement.GetBoolean();
                if (!success)
                {
                    if (message.RootElement.TryGetProperty("error", out var errorElement) &&
                        errorElement.TryGetProperty("code", out var codeElement) &&
                        codeElement.GetString() == "not_found")
                    {
                        return new HaIntegrationVersionInfo(null, null);
                    }

                    return new HaIntegrationVersionInfo(
                        null,
                        message.RootElement.TryGetProperty("error", out var error)
                            ? error.ToString()
                            : "unknown error");
                }

                if (!message.RootElement.TryGetProperty("result", out var resultElement))
                {
                    return new HaIntegrationVersionInfo(null, "manifest response has no result");
                }

                var version = resultElement.TryGetProperty("version", out var versionElement)
                    ? versionElement.GetString()
                    : null;
                return new HaIntegrationVersionInfo(version, null);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new HaIntegrationVersionInfo(null, ex.Message);
        }

        return new HaIntegrationVersionInfo(null, "cancelled");
    }

    private void HandleMessage(JsonDocument message)
    {
        if (!message.RootElement.TryGetProperty("type", out var typeElement))
        {
            return;
        }

        var type = typeElement.GetString();

        if (type == "event" && message.RootElement.TryGetProperty("event", out var eventElement))
        {
            HandleEvent(eventElement);
            return;
        }

        if (type == "pong")
        {
            // Heartbeat response — connection is alive.
            return;
        }

        if (type == "result"
            && message.RootElement.TryGetProperty("id", out var resultId)
            && _pending.TryRemove(resultId.GetInt32(), out var waiting))
        {
            // The answer to a request made while the loop runs (ProvisionUserAsync).
            if (message.RootElement.TryGetProperty("success", out var answered) && answered.GetBoolean())
            {
                waiting.TrySetResult(message.RootElement.TryGetProperty("result", out var answer) ? answer.Clone() : default);
            }
            else
            {
                var reason = message.RootElement.TryGetProperty("error", out var failure) && failure.TryGetProperty("message", out var text)
                    ? text.GetString()
                    : "refused";
                waiting.TrySetException(new InvalidOperationException(reason));
            }

            return;
        }

        if (type == "result")
        {
            // fire_event / subscribe_events result — check for errors.
            if (message.RootElement.TryGetProperty("success", out var success) && !success.GetBoolean())
            {
                var unauthorized = message.RootElement.TryGetProperty("error", out var err)
                    && err.TryGetProperty("code", out var code) && code.GetString() == "unauthorized";
                if (_ownCommands && unauthorized)
                {
                    // Every message is refused until an administrator approves this PC for the
                    // token's user; one line per connection says so.
                    if (!_approvalNoticeLogged)
                    {
                        _approvalNoticeLogged = true;
                        _log.Warning("Home Assistant has not approved this PC for the token's user yet. An administrator " +
                            "confirms it in Home Assistant under Settings > Devices & services (Discovered).");
                    }

                    return;
                }

                _log.Warning($"HA WebSocket command error: {(message.RootElement.TryGetProperty("error", out var error) ? error.ToString() : "unknown")}");
            }
        }
    }

    private void HandleEvent(JsonElement eventElement)
    {
        if (!eventElement.TryGetProperty("data", out var data))
        {
            return;
        }

        // Filter: only process commands addressed to this installation.
        if (!data.TryGetProperty("serial_number", out var serialNumber) ||
            !string.Equals(serialNumber.GetString(), _settings.SerialNumber, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!data.TryGetProperty("command_type", out var commandTypeElement))
        {
            return;
        }

        var commandType = commandTypeElement.GetString();

        try
        {
            switch (commandType)
            {
                case "notification":
                    if (data.TryGetProperty("payload", out var notifPayload))
                    {
                        var notification = JsonSerializer.Deserialize<NotificationPayload>(notifPayload.GetRawText(), JsonOptions);
                        if (notification is not null && !string.IsNullOrWhiteSpace(notification.Message))
                        {
                            NotificationReceived?.Invoke(notification);
                        }
                    }
                    break;

                case "media_command":
                    if (data.TryGetProperty("payload", out var mediaPayload))
                    {
                        var command = JsonSerializer.Deserialize<MediaCommand>(mediaPayload.GetRawText(), JsonOptions);
                        if (command is not null)
                        {
                            MediaCommandReceived?.Invoke(command);
                        }
                    }
                    break;

                case "button_command":
                    if (data.TryGetProperty("payload", out var btnPayload))
                    {
                        var command = JsonSerializer.Deserialize<SystemCommandMessage>(btnPayload.GetRawText(), JsonOptions);
                        if (command is not null)
                        {
                            var target = data.TryGetProperty("target", out var targetElement)
                                ? targetElement.GetString()
                                : null;
                            ButtonCommandReceived?.Invoke(command, target);
                        }
                    }
                    break;

                case "update_install":
                    UpdateInstallRequested?.Invoke();
                    break;

                case "announce":
                    AnnounceRequested?.Invoke();
                    break;

                case "install_update":
                    // Fired by the tray app itself when the broker is not there to carry the
                    // service command; every instance sees it, so it names the role it means.
                    SilentInstallRequested?.Invoke(
                        data.TryGetProperty("target", out var installTarget) ? installTarget.GetString() : null);
                    break;

                default:
                    _log.Warning($"HA WebSocket unknown command type: {commandType}");
                    break;
            }
        }
        catch (JsonException ex)
        {
            _log.Warning($"HA WebSocket malformed command payload ({commandType}): {ex.Message}");
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && IsConnected)
        {
            await Task.Delay(HeartbeatInterval, cancellationToken);

            if (!IsConnected)
            {
                break;
            }

            try
            {
                await SendWithIdAsync(id => new { id, type = "ping" }, cancellationToken);
            }
            catch (Exception ex)
            {
                _log.Warning($"HA WebSocket heartbeat failed: {ex.Message}");
                break;
            }
        }
    }

    private int NextId() => Interlocked.Increment(ref _messageId);

    private static int NextTestId(ref int messageId) => Interlocked.Increment(ref messageId);

    private static bool TryParseVersion(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().TrimStart('v', 'V');
        var suffixIndex = normalized.IndexOfAny(new[] { '-', '+' });
        if (suffixIndex >= 0)
        {
            normalized = normalized[..suffixIndex];
        }

        if (!Version.TryParse(normalized, out var parsed))
        {
            return false;
        }

        version = parsed;
        return true;
    }

    /// <summary>Sends a message that carries no id (the authentication).</summary>
    private async Task SendAsync(object payload, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (_ws is null)
            {
                throw new InvalidOperationException("WebSocket is not connected.");
            }

            await SendAsync(_ws, payload, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Sends a message that carries an id, and returns the id. Home Assistant insists on
    /// ids that grow from one message to the next, so the id is taken under the same lock
    /// that orders the sends: taken before it, two senders could pass each other, and the
    /// one with the lower id was refused ("id_reuse") and its message lost.
    /// </summary>
    private async Task<int> SendWithIdAsync(Func<int, object> build, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (_ws is null)
            {
                throw new InvalidOperationException("WebSocket is not connected.");
            }

            var id = NextId();
            await SendAsync(_ws, build(id), cancellationToken);
            return id;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static async Task SendAsync(ClientWebSocket ws, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    private Task<JsonDocument> ReceiveMessageAsync(CancellationToken cancellationToken)
    {
        if (_ws is null)
        {
            throw new InvalidOperationException("WebSocket is not connected.");
        }

        return ReceiveMessageAsync(_ws, cancellationToken);
    }

    private static async Task<JsonDocument> ReceiveMessageAsync(ClientWebSocket ws, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new WebSocketException("Server closed the connection.");
                }

                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            ms.Position = 0;
            return await JsonDocument.ParseAsync(ms, cancellationToken: cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

internal sealed record HaConnectionTestResult(
    string HomeAssistantVersion,
    string? IntegrationVersion,
    string? IntegrationVersionError);

internal sealed record HaIntegrationVersionInfo(string? Version, string? Error);
