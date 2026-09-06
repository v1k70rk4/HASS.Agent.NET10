using System.Buffers;
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
    public const string MinimumIntegrationVersion = "10.6.6";

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

        var id = NextId();
        await SendAsync(new
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
    public async Task PublishNotificationActionAsync(string action, CancellationToken cancellationToken)
    {
        await FireEventAsync("hass_agent_notification_action", new
        {
            serial_number = _settings.SerialNumber,
            action,
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

    private static string BuildWebSocketUrl(string url)
    {
        url = url.Trim().TrimEnd('/');
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
        var id = NextId();
        _commandSubscriptionId = id;
        await SendAsync(new
        {
            id,
            type = "subscribe_events",
            event_type = "hass_agent_command"
        }, cancellationToken);

        await WaitForResultAsync(id, "subscribe_events", cancellationToken);
        _log.Info("HA WebSocket subscribed to hass_agent_command events.");
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

    private async Task WaitForResultAsync(int id, string operation, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var message = await ReceiveMessageAsync(cancellationToken);
            if (!message.RootElement.TryGetProperty("type", out var typeElement) ||
                typeElement.GetString() != "result")
            {
                HandleMessage(message);
                continue;
            }

            if (!message.RootElement.TryGetProperty("id", out var idElement) ||
                idElement.GetInt32() != id)
            {
                HandleMessage(message);
                continue;
            }

            var success = message.RootElement.TryGetProperty("success", out var successElement) &&
                successElement.GetBoolean();
            if (success)
            {
                return;
            }

            var error = message.RootElement.TryGetProperty("error", out var errorElement)
                ? errorElement.ToString()
                : "unknown";
            throw new InvalidOperationException($"HA WebSocket {operation} failed: {error}");
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

        if (type == "result")
        {
            // fire_event / subscribe_events result — check for errors.
            if (message.RootElement.TryGetProperty("success", out var success) && !success.GetBoolean())
            {
                var error = message.RootElement.TryGetProperty("error", out var err) ? err.ToString() : "unknown";
                _log.Warning($"HA WebSocket command error: {error}");
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
                var id = NextId();
                await SendAsync(new { id, type = "ping" }, cancellationToken);
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
