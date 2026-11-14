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
    public async Task A_tick_made_by_another_users_app_holds_in_the_app_that_has_the_connection()
    {
        // Two users logged in: the one whose app talks to Home Assistant read the settings
        // before the other one ticked the box.
        var (service, log, path, file) = NewServiceWithFile(CompanionRuntimeRole.App, inMemory: false, inFile: true);
        using (log)
        using (service)
        {
            await service.HandleUpdateInstallRequestAsync();

            Assert.True(Settings(service).RemoteUpdateInstallBlocked);
        }

        var text = File.ReadAllText(path);
        Assert.Contains("was ticked by another app of this PC", text, StringComparison.Ordinal);
        Assert.Equal(1, Count(text, "refused: updates are installed only at this PC"));
        Assert.DoesNotContain("Update install requested from Home Assistant.", text, StringComparison.Ordinal);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task The_service_reads_the_tick_from_the_file_before_it_takes_a_handover()
    {
        // The service reloads a moment after the file changes; a handover that comes within
        // that moment is decided on the file all the same.
        var (service, log, path, _) = NewServiceWithFile(CompanionRuntimeRole.Service, inMemory: false, inFile: true);
        using (log)
        using (service)
        {
            await service.RunSilentUpdateInstallAsync();
        }

        var text = File.ReadAllText(path);
        Assert.Equal(1, Count(text, "refused: updates are installed only at this PC"));
        Assert.DoesNotContain("Silent update install requested", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_switch_follows_the_file_both_ways_and_keeps_what_it_has_when_the_file_cannot_be_read()
    {
        var (service, log, path, file) = NewServiceWithFile(CompanionRuntimeRole.App, inMemory: true, inFile: false);
        using (log)
        using (service)
        {
            // Unticked by the other app.
            Assert.True(service.RefreshRemoteInstallSwitch());
            Assert.False(Settings(service).RemoteUpdateInstallBlocked);

            // Nothing new: nothing changes, nothing is said.
            Assert.False(service.RefreshRemoteInstallSwitch());

            // A file that is not settings, and one that is gone: the switch stays.
            Settings(service).RemoteUpdateInstallBlocked = true;
            File.WriteAllText(file, "not json");
            Assert.False(service.RefreshRemoteInstallSwitch());
            Assert.True(Settings(service).RemoteUpdateInstallBlocked);

            File.Delete(file);
            Assert.False(service.RefreshRemoteInstallSwitch());
            Assert.True(Settings(service).RemoteUpdateInstallBlocked);
        }

        Assert.Equal(1, Count(File.ReadAllText(path), "was unticked by another app of this PC"));
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
            var failure = settings.TrySetRemoteUpdateInstallBlocked(true, s => SettingsStore.Save(paths, s, withItsRemoteInstallSwitch: true));

            Assert.NotNull(failure);
            Assert.False(settings.RemoteUpdateInstallBlocked);
        }

        Assert.Equal(before, File.ReadAllText(file));

        // And once it can be written again, the tick holds.
        Assert.Null(settings.TrySetRemoteUpdateInstallBlocked(true, s => SettingsStore.Save(paths, s, withItsRemoteInstallSwitch: true)));
        Assert.True(settings.RemoteUpdateInstallBlocked);
        Assert.True(SettingsStore.SavedRemoteUpdateInstallBlocked(file));
    }

    [Fact]
    public void A_save_from_an_app_that_read_the_settings_before_the_tick_does_not_untick_it()
    {
        var (paths, file) = NewSettingsFile();
        // Two users' apps read the same settings; then one of them ticks the box.
        var theirs = new CompanionSettings { DeviceName = "pc" };
        var mine = new CompanionSettings { DeviceName = "pc" };
        SettingsStore.Save(paths, theirs);
        Assert.Null(theirs.TrySetRemoteUpdateInstallBlocked(true, s => SettingsStore.Save(paths, s, withItsRemoteInstallSwitch: true)));

        // The other one saves something else, whole, as every save is.
        mine.BetaUpdatesEnabled = true;
        SettingsStore.Save(paths, mine);

        Assert.True(SettingsStore.SavedRemoteUpdateInstallBlocked(file));
        Assert.True(SettingsStore.Import(file).BetaUpdatesEnabled);
        // And knows of the tick from then on.
        Assert.True(mine.RemoteUpdateInstallBlocked);
    }

    [Fact]
    public void Only_the_tick_and_an_import_change_the_switch_in_the_file()
    {
        var (paths, file) = NewSettingsFile();
        var settings = new CompanionSettings { DeviceName = "pc", RemoteUpdateInstallBlocked = true };

        // No file yet: what the settings say is written.
        SettingsStore.Save(paths, settings);
        Assert.True(SettingsStore.SavedRemoteUpdateInstallBlocked(file));

        // Unticked in another app's copy and saved as anything else: the file keeps it.
        var stale = new CompanionSettings { DeviceName = "pc" };
        SettingsStore.Save(paths, stale);
        Assert.True(SettingsStore.SavedRemoteUpdateInstallBlocked(file));

        // The untick itself, and an import: written as they are.
        var unticked = new CompanionSettings { DeviceName = "pc" };
        SettingsStore.Save(paths, unticked, withItsRemoteInstallSwitch: true);
        Assert.False(SettingsStore.SavedRemoteUpdateInstallBlocked(file));
    }

    [Theory]
    [InlineData("""{ "deviceName": "pc" }""", null)]
    [InlineData("""{ "remoteUpdateInstallBlocked": true }""", true)]
    [InlineData("""{ "RemoteUpdateInstallBlocked": false }""", false)]
    [InlineData("""{ "remoteUpdateInstallBlocked": "yes" }""", null)]
    [InlineData("not json", null)]
    [InlineData("[]", null)]
    public void The_switch_is_read_from_the_file_only_when_it_is_there(string content, bool? expected)
    {
        var (_, file) = NewSettingsFile();
        File.WriteAllText(file, content);

        Assert.Equal(expected, SettingsStore.SavedRemoteUpdateInstallBlocked(file));
    }

    [Fact]
    public void A_file_from_before_the_switch_takes_what_the_settings_say()
    {
        var (paths, file) = NewSettingsFile();
        File.WriteAllText(file, """{ "deviceName": "pc" }""");

        SettingsStore.Save(paths, new CompanionSettings { DeviceName = "pc", RemoteUpdateInstallBlocked = true });

        Assert.True(SettingsStore.SavedRemoteUpdateInstallBlocked(file));
        Assert.Null(SettingsStore.SavedRemoteUpdateInstallBlocked(Path.Combine(_folder, "nowhere", "settings.json")));
    }

    private (AppPaths Paths, string File) NewSettingsFile()
    {
        var folder = Path.Combine(_folder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "settings.json");
        return (new AppPaths(folder, file, Path.Combine(folder, "app.log"), Path.Combine(folder, "device-id"), []), file);
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

    [Theory]
    // The tray app on MQTT, its discovery out: the one case.
    [InlineData(false, false, true, true, true)]
    // The service never: it does not know whether the integration builds the entity.
    [InlineData(true, false, true, true, false)]
    [InlineData(true, true, true, true, false)]
    // The HA API has no broker; nothing before this connection's discovery; nothing unconnected.
    [InlineData(false, true, true, true, false)]
    [InlineData(false, false, false, true, false)]
    [InlineData(false, false, true, false, false)]
    public void Only_the_tray_app_on_mqtt_sends_the_update_discovery_again(bool asService, bool onWebSocket, bool discoveryPublished, bool mqttConnected, bool expected)
    {
        var role = asService ? CompanionRuntimeRole.Service : CompanionRuntimeRole.App;

        Assert.Equal(expected, MqttCompanionService.RepublishesUpdateDiscovery(role, onWebSocket, discoveryPublished, mqttConnected));
    }

    [Fact]
    public async Task The_service_that_learns_the_switch_from_the_file_publishes_no_discovery()
    {
        // Ticked and unticked in the file while the service holds the other value: it decides
        // on the file and says so in the log, and nothing of the update entity's discovery
        // is its to send, in either direction.
        foreach (var ticked in new[] { true, false })
        {
            var (service, log, path, _) = NewServiceWithFile(CompanionRuntimeRole.Service, inMemory: !ticked, inFile: ticked);
            using (log)
            using (service)
            {
                Assert.True(service.RefreshRemoteInstallSwitch());
                await service.ReportRemoteInstallSwitchAsync();

                Assert.Equal(ticked, Settings(service).RemoteUpdateInstallBlocked);
                Assert.False(MqttCompanionService.RepublishesUpdateDiscovery(CompanionRuntimeRole.Service, onWebSocket: false, discoveryPublished: true, mqttConnected: true));
            }

            Assert.Contains(ticked ? "was ticked by another app" : "was unticked by another app", File.ReadAllText(path), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task On_the_broker_the_tray_apps_discovery_follows_the_switch_both_ways()
    {
        // No integration that builds the entity: Home Assistant's own MQTT discovery does,
        // and its install command is there only while the switch allows it.
        var sent = new List<(string Topic, string? Payload)>();
        var (service, log, _, file) = NewServiceWithFile(
            CompanionRuntimeRole.App, inMemory: false, inFile: false,
            outbox: (topic, payload, _) => { sent.Add((topic, payload)); return Task.CompletedTask; });
        using (log)
        using (service)
        {
            // Nothing changed: the state alone, and Home Assistant may ask for the install.
            await service.ReportUpdateStateAsync(AnUpdate);
            var only = Assert.Single(sent);
            Assert.Equal(StateTopic, only.Topic);
            Assert.True(Parse(only.Payload).GetProperty("remote_install").GetBoolean());

            // Ticked in another user's window.
            sent.Clear();
            WriteSwitch(file, true);
            await service.ReportUpdateStateAsync(AnUpdate);
            Assert.Equal([DiscoveryTopic, StateTopic], sent.Select(message => message.Topic));
            Assert.False(Parse(sent[0].Payload).TryGetProperty("command_topic", out _));
            Assert.False(Parse(sent[0].Payload).TryGetProperty("payload_install", out _));
            Assert.False(Parse(sent[1].Payload).GetProperty("remote_install").GetBoolean());

            // And unticked again: the install command is back in the discovery.
            sent.Clear();
            WriteSwitch(file, false);
            await service.ReportUpdateStateAsync(AnUpdate);
            Assert.Equal([DiscoveryTopic, StateTopic], sent.Select(message => message.Topic));
            Assert.Equal(InstallTopic, Parse(sent[0].Payload).GetProperty("command_topic").GetString());
            Assert.Equal("install", Parse(sent[0].Payload).GetProperty("payload_install").GetString());
            Assert.True(Parse(sent[1].Payload).GetProperty("remote_install").GetBoolean());
        }
    }

    [Fact]
    public async Task On_the_broker_the_service_sends_the_state_and_never_the_discovery()
    {
        // The service does not know whether the integration builds the update entity: a
        // discovery from it would put a second entity next to that one.
        var sent = new List<(string Topic, string? Payload)>();
        var (service, log, _, file) = NewServiceWithFile(
            CompanionRuntimeRole.Service, inMemory: false, inFile: false,
            outbox: (topic, payload, _) => { sent.Add((topic, payload)); return Task.CompletedTask; });
        using (log)
        using (service)
        {
            foreach (var ticked in new[] { true, false })
            {
                sent.Clear();
                WriteSwitch(file, ticked);
                await service.ReportUpdateStateAsync(AnUpdate);

                var only = Assert.Single(sent);
                Assert.Equal(StateTopic, only.Topic);
                Assert.Equal(!ticked, Parse(only.Payload).GetProperty("remote_install").GetBoolean());
            }

            // The switch reported on its own (the path a refused install takes) is no different.
            sent.Clear();
            WriteSwitch(file, true);
            Assert.True(service.RefreshRemoteInstallSwitch());
            await service.ReportRemoteInstallSwitchAsync();
            Assert.DoesNotContain(sent, message => message.Topic == DiscoveryTopic);
        }
    }

    private const string StateTopic = "hass.agent/update/" + Serial + "/state";
    private const string InstallTopic = "hass.agent/update/" + Serial + "/install";
    private const string DiscoveryTopic = "homeassistant/update/" + Serial + "/hass_agent_net10/config";

    /// <summary>What a finished check found; no check is made again within the hour.</summary>
    private static AppUpdateState AnUpdate =>
        AppUpdateState.Failed("10.13.1", "none") with { Error = null, UpdateAvailable = true, LatestVersion = "10.14.0" };

    private static JsonElement Parse(string? payload) => JsonDocument.Parse(payload ?? "null").RootElement.Clone();

    /// <summary>The switch as another user's app of the PC would leave it in the settings file.</summary>
    private static void WriteSwitch(string file, bool ticked)
    {
        var folder = Path.GetDirectoryName(file)!;
        var paths = new AppPaths(folder, file, Path.Combine(folder, "app.log"), Path.Combine(folder, "device-id"), []);
        var theirs = new CompanionSettings { DeviceName = "pc", RemoteUpdateInstallBlocked = ticked };
        theirs.SerialNumber = Serial;
        SettingsStore.Save(paths, theirs, withItsRemoteInstallSwitch: true);
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

    /// <summary>A service that reads the switch from a settings file of its own, as the running app does.</summary>
    private (MqttCompanionService Service, FileLog Log, string Path, string File) NewServiceWithFile(
        CompanionRuntimeRole role, bool inMemory, bool inFile, Func<string, string?, bool, Task>? outbox = null)
    {
        var folder = Path.Combine(_folder, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "settings.json");
        var paths = new AppPaths(folder, file, Path.Combine(folder, "app.log"), Path.Combine(folder, "device-id"), []);
        var saved = new CompanionSettings { DeviceName = "pc", RemoteUpdateInstallBlocked = inFile };
        saved.SerialNumber = Serial;
        SettingsStore.Save(paths, saved);

        var path = Path.Combine(folder, "test.log");
        var log = new FileLog(path);
        var settings = new CompanionSettings { DeviceName = "pc", RemoteUpdateInstallBlocked = inMemory };
        settings.SerialNumber = Serial;
        var service = new MqttCompanionService(
            settings, new NullNotificationSink(), mediaSessionService: null, systemMetricsService: null,
            systemCommandService: null!, role, log)
        {
            SettingsFile = file,
            Outbox = outbox
        };
        _settings[service] = settings;
        return (service, log, path, file);
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
