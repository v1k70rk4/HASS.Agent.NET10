using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HASS.Agent.Companion.Http;

internal sealed class LocalApiServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly CompanionSettings _settings;
    private readonly INotificationSink _notificationSink;
    private readonly FileLog _log;
    // One start or stop at a time: two saves in a row must not race for the same port.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebApplication? _app;
    private string? _listenUrl;

    public LocalApiServer(CompanionSettings settings, INotificationSink notificationSink, FileLog log)
    {
        _settings = settings;
        _notificationSink = notificationSink;
        _log = log;
    }

    public bool IsRunning => _app is not null;

    /// <summary>
    /// Brings the server in line with the settings: started when the API was turned on, stopped
    /// when it was turned off, restarted when its address or port changed.
    /// </summary>
    public async Task ApplySettingsAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!_settings.LocalApiEnabled)
            {
                if (_app is not null)
                {
                    await StopCoreAsync();
                    _log.Info("Local API stopped: turned off in the settings.");
                }

                return;
            }

            if (_app is not null && _listenUrl == _settings.ListenUrl)
            {
                return;
            }

            await StopCoreAsync();
            await StartCoreAsync();
            _log.Info($"Local API listening on {_settings.ListenUrl}.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StartCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartCoreAsync()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = AppIdentity.ExecutableName
        });

        builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNameCaseInsensitive = true;
        });
        builder.WebHost.UseUrls(_settings.ListenUrl);

        var app = builder.Build();

        app.MapGet("/", () => Results.Redirect("/info"));

        app.MapGet("/info", () => Results.Json(new InfoResponse(
            _settings.SerialNumber,
            new DeviceInfoResponse(
                _settings.DeviceName,
                _settings.Manufacturer,
                _settings.Model,
                _settings.SoftwareVersion),
            new ApiCapabilitiesResponse(
                Notifications: _settings.MqttNotificationsEnabled,
                MediaPlayer: false,
                Buttons: false,
                SystemSensors: false,
                Update: true,
                Commands: []))));

        app.MapPost("/notify", async (HttpContext context) =>
        {
            if (!IsAuthorized(context))
            {
                return Results.Json(new { error = "unauthorized" }, statusCode: 401);
            }

            if (!_settings.MqttNotificationsEnabled)
            {
                // The same switch the MQTT and HA API notifications obey (Capabilities page).
                return Results.Json(new { error = "notifications_disabled" }, statusCode: 403);
            }

            NotificationPayload? payload;
            try
            {
                payload = await JsonSerializer.DeserializeAsync<NotificationPayload>(
                    context.Request.Body,
                    JsonOptions,
                    context.RequestAborted);
            }
            catch (JsonException ex)
            {
                _log.Warning($"Rejected malformed notification payload: {ex.Message}");
                return Results.BadRequest(new { error = "invalid_json" });
            }

            if (payload is null || string.IsNullOrWhiteSpace(payload.Message))
            {
                return Results.BadRequest(new { error = "message_required" });
            }

            _notificationSink.ShowNotification(payload);
            return Results.Ok(new { status = "notification_processed" });
        });

        // Remembered only once it listens: a port that was taken is tried again at the next
        // save, instead of being taken for running.
        try
        {
            await app.StartAsync();
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        _app = app;
        _listenUrl = _settings.ListenUrl;
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        if (_app is null)
        {
            return;
        }

        await _app.StopAsync(TimeSpan.FromSeconds(5));
        await _app.DisposeAsync();
        _app = null;
        _listenUrl = null;
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }

    private bool IsAuthorized(HttpContext context)
    {
        return IsAuthorized(context.Request.Headers.Authorization.ToString(), _settings.ApiKey);
    }

    internal static bool IsAuthorized(string? authorization, string? apiKey)
    {
        // There is a key only while the API is on; without one nothing is let in.
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return false;
        }

        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Compared in constant time, on hashes so the lengths match too: how long the
        // comparison takes says nothing about how much of a guessed key was right.
        var given = SHA256.HashData(Encoding.UTF8.GetBytes(authorization[7..].Trim()));
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        return CryptographicOperations.FixedTimeEquals(given, expected);
    }
}
