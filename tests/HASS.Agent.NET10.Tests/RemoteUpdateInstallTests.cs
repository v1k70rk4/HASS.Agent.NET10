using System.Text.Json;
using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Http;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.Mqtt;
using HASS.Agent.Companion.Runtime;

namespace HASS.Agent.Companion.Tests;

/// <summary>The Danger Zone's "updates only at this PC": an install asked for from Home Assistant is refused.</summary>
public sealed class RemoteUpdateInstallTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("hass-agent-remote-update-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A log still held open for a moment: the temp folder is cleaned by Windows.
        }
    }

    [Fact]
    public void Settings_from_before_the_switch_allow_the_install()
    {
        var settings = JsonSerializer.Deserialize<CompanionSettings>("""{ "DeviceName": "pc" }""");

        Assert.NotNull(settings);
        Assert.False(settings.RemoteUpdateInstallBlocked);
        Assert.False(new CompanionSettings().RemoteUpdateInstallBlocked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_update_entitys_button_is_refused_before_the_release_is_looked_up(bool asService)
    {
        var (service, log, path) = NewService(asService ? CompanionRuntimeRole.Service : CompanionRuntimeRole.App, blocked: true);
        using (log)
        using (service)
        {
            // What the update entity's Install button ends in: the tray app's handler, and the
            // service's when no tray app runs (it answers in Home Assistant then).
            await service.HandleUpdateInstallRequestAsync();
            await service.RunSilentUpdateInstallAsync(announce: true);
        }

        var text = File.ReadAllText(path);
        Assert.Equal(2, Count(text, "refused: updates are installed only at this PC"));
        Assert.DoesNotContain("Update install requested from Home Assistant.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Silent update install requested", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_handover_to_the_service_is_refused_too()
    {
        // The tray app hands the install to the service over the broker or Home Assistant's
        // event bus, where anyone could say the same words: the service refuses on its own.
        var (service, log, path) = NewService(CompanionRuntimeRole.Service, blocked: true);
        using (log)
        using (service)
        {
            await service.RunSilentUpdateInstallAsync();
        }

        var text = File.ReadAllText(path);
        Assert.Contains("[WARN]", text, StringComparison.Ordinal);
        Assert.Contains("refused: updates are installed only at this PC", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Silent update install requested", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_the_HA_API_the_update_entitys_button_is_refused_too()
    {
        // The same request as it comes over Home Assistant's WebSocket, through the wiring
        // the running app uses.
        var (service, log, path) = NewService(CompanionRuntimeRole.App, blocked: true);
        using (log)
        using (service)
        {
            using var ws = new HaWebSocketService(Settings(service), log);
            service.WireUpdateRequests(ws);

            ws.HandleEvent(Event("update_install"));

            await WaitForAsync(path, "refused: updates are installed only at this PC");
        }

        var text = File.ReadAllText(path);
        Assert.Equal(1, Count(text, "refused: updates are installed only at this PC"));
        Assert.DoesNotContain("Update install requested from Home Assistant.", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task On_the_HA_API_a_handover_to_the_service_is_refused_too()
    {
        var (service, log, path) = NewService(CompanionRuntimeRole.Service, blocked: true);
        using (log)
        using (service)
        {
            using var ws = new HaWebSocketService(Settings(service), log);
            service.WireUpdateRequests(ws);

            // Meant for the tray app: not the service's to take, refused or not.
            ws.HandleEvent(Event("install_update", target: CompanionRuntimeRole.App.Token()));
            ws.HandleEvent(Event("install_update", target: CompanionRuntimeRole.Service.Token()));

            await WaitForAsync(path, "refused: updates are installed only at this PC");
        }

        var text = File.ReadAllText(path);
        Assert.Equal(1, Count(text, "refused: updates are installed only at this PC"));
        Assert.DoesNotContain("Silent update install requested", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_switch_that_could_not_be_saved_is_taken_back()
    {
        var settings = new CompanionSettings();
        var saved = new List<bool>();

        // Saved: set, in the file as it is in the app.
        Assert.Null(settings.TrySetRemoteUpdateInstallBlocked(true, s => saved.Add(s.RemoteUpdateInstallBlocked)));
        Assert.True(settings.RemoteUpdateInstallBlocked);
        Assert.Equal([true], saved);

        // Not saved: the service reads the file, so the app does not say otherwise either way.
        var failure = settings.TrySetRemoteUpdateInstallBlocked(false, _ => throw new IOException("the disk is full"));
        Assert.Equal("the disk is full", failure?.Message);
        Assert.True(settings.RemoteUpdateInstallBlocked);

        settings.RemoteUpdateInstallBlocked = false;
        Assert.NotNull(settings.TrySetRemoteUpdateInstallBlocked(true, _ => throw new UnauthorizedAccessException()));
        Assert.False(settings.RemoteUpdateInstallBlocked);
    }

    [Fact]
    public void Saving_into_a_file_that_cannot_be_written_takes_the_switch_back()
    {
        // The real save, against a settings file Windows will not let it replace.
        var folder = Path.Combine(_folder, "settings");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "settings.json");
        var paths = new AppPaths(folder, file, Path.Combine(folder, "app.log"), Path.Combine(folder, "device-id"), []);
        var settings = new CompanionSettings { DeviceName = "pc" };
        SettingsStore.Save(paths, settings);
        var before = File.ReadAllText(file);

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = settings.TrySetRemoteUpdateInstallBlocked(true, s => SettingsStore.Save(paths, s));

            Assert.NotNull(failure);
            Assert.False(settings.RemoteUpdateInstallBlocked);
        }

        Assert.Equal(before, File.ReadAllText(file));

        // And once it can be written again, the tick holds.
        Assert.Null(settings.TrySetRemoteUpdateInstallBlocked(true, s => SettingsStore.Save(paths, s)));
        Assert.True(settings.RemoteUpdateInstallBlocked);
        Assert.Contains("emoteUpdateInstallBlocked\": true", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void Home_Assistant_is_told_whether_it_may_ask_for_the_install()
    {
        var state = AppUpdateState.Failed("10.13.0", "offline") with { Error = null };

        var allowed = MqttCompanionService.ForHomeAssistant(state, new CompanionSettings());
        var blocked = MqttCompanionService.ForHomeAssistant(state, new CompanionSettings { RemoteUpdateInstallBlocked = true });

        Assert.True(allowed.RemoteInstall);
        Assert.False(blocked.RemoteInstall);
        Assert.True(Json(allowed).GetProperty("remote_install").GetBoolean());
        Assert.False(Json(blocked).GetProperty("remote_install").GetBoolean());
        // The rest is what the check found.
        Assert.Equal("10.13.0", Json(blocked).GetProperty("installed_version").GetString());
    }

    [Fact]
    public void The_discovered_update_entity_has_no_install_button_then()
    {
        var settings = new CompanionSettings { DeviceName = "pc" };
        settings.SerialNumber = "0123456789abcdef0123456789abcdef";

        var allowed = Json(MqttCompanionService.UpdateDiscoveryFor(settings, "state", "install"));
        settings.RemoteUpdateInstallBlocked = true;
        var blocked = Json(MqttCompanionService.UpdateDiscoveryFor(settings, "state", "install"));

        Assert.Equal("install", allowed.GetProperty("command_topic").GetString());
        Assert.Equal("install", allowed.GetProperty("payload_install").GetString());
        Assert.False(blocked.TryGetProperty("command_topic", out _));
        Assert.False(blocked.TryGetProperty("payload_install", out _));
        // Still the same entity, showing the versions.
        Assert.Equal(allowed.GetProperty("unique_id").GetString(), blocked.GetProperty("unique_id").GetString());
        Assert.Equal("state", blocked.GetProperty("latest_version_topic").GetString());
    }

    [Fact]
    public void The_service_takes_the_switch_up_when_it_is_changed()
    {
        var before = new CompanionSettings { MqttHost = "broker.local" };
        var after = new CompanionSettings { MqttHost = "broker.local", RemoteUpdateInstallBlocked = true };
        before.SerialNumber = after.SerialNumber = "0123456789abcdef0123456789abcdef";

        Assert.NotEqual(
            SystemService.CompanionWindowsService.WhatTheRuntimeUses(before),
            SystemService.CompanionWindowsService.WhatTheRuntimeUses(after));
    }

    private const string Serial = "0123456789abcdef0123456789abcdef";

    private readonly Dictionary<MqttCompanionService, CompanionSettings> _settings = [];

    private CompanionSettings Settings(MqttCompanionService service) => _settings[service];

    /// <summary>A hass_agent_command event as Home Assistant hands it over on the WebSocket.</summary>
    private static JsonElement Event(string commandType, string? target = null) =>
        JsonSerializer.SerializeToElement(new
        {
            event_type = "hass_agent_command",
            data = new { serial_number = Serial, command_type = commandType, target, payload = new { } }
        });

    /// <summary>The handlers hand the work to another thread: wait until the log has the line.</summary>
    private static async Task WaitForAsync(string path, string line)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path) && ReadShared(path).Contains(line, StringComparison.Ordinal))
            {
                // A moment more, so a second line that should not come would be seen.
                await Task.Delay(300, TestContext.Current.CancellationToken);
                return;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Fail($"The log never said: {line}");
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private (MqttCompanionService Service, FileLog Log, string Path) NewService(CompanionRuntimeRole role, bool blocked)
    {
        var path = Path.Combine(_folder, $"{Guid.NewGuid():N}.log");
        var log = new FileLog(path);
        var settings = new CompanionSettings { DeviceName = "pc", RemoteUpdateInstallBlocked = blocked };
        settings.SerialNumber = Serial;
        // Never started: no broker, no Home Assistant. The command service is not reached by
        // an install request, and a real one would open the PC's audio devices.
        var service = new MqttCompanionService(
            settings, new NullNotificationSink(), mediaSessionService: null, systemMetricsService: null,
            systemCommandService: null!, role, log);
        _settings[service] = settings;
        return (service, log, path);
    }

    private static JsonElement Json(object value) =>
        JsonSerializer.SerializeToElement(value, MqttCompanionService.JsonOptions);

    private static int Count(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
