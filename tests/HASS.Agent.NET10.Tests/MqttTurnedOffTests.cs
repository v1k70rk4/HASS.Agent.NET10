using System.Text.Json;
using System.Text.RegularExpressions;
using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Http;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.Mqtt;
using HASS.Agent.Companion.Runtime;

namespace HASS.Agent.Companion.Tests;

/// <summary>
/// MQTT turned off: what the app kept on the broker is taken off it; and the watchdog that
/// brings the tray app back after an install waits for the setup however long the download takes.
/// </summary>
public sealed class MqttTurnedOffTests : IDisposable
{
    private const string Serial = "0123456789abcdef0123456789abcdef";
    private readonly string _folder = Directory.CreateTempSubdirectory("hass-agent-mqtt-off-").FullName;

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
    public async Task The_tray_app_takes_its_retained_messages_off_the_broker_after_the_offline_discovery()
    {
        var sent = await TurnOff(CompanionRuntimeRole.App);

        var device = $"hass.agent/devices/{Serial}";
        var offline = sent.FindIndex(m => m.Topic == device && m.Payload is not null);
        Assert.True(offline >= 0, "the offline discovery goes out first");
        using (var payload = JsonDocument.Parse(sent[offline].Payload!))
        {
            Assert.All(
                payload.RootElement.GetProperty("apis").EnumerateObject().Where(p => p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False),
                p => Assert.False(p.Value.GetBoolean()));
        }

        var cleared = sent.Skip(offline + 1).Where(m => m.Payload is null && m.Retain).Select(m => m.Topic).ToList();
        Assert.Contains(device, cleared);
        Assert.Contains($"{device}/availability", cleared);
        Assert.Contains($"hass.agent/update/{Serial}/state", cleared);
        Assert.Contains(cleared, t => t.StartsWith("homeassistant/update/", StringComparison.Ordinal) && t.EndsWith("/config", StringComparison.Ordinal));
        // Nothing of the service's: it clears its own.
        Assert.DoesNotContain(sent, m => m.Topic.StartsWith("hass.agent/system/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_service_takes_its_state_off_the_broker_after_the_offline_one()
    {
        var sent = await TurnOff(CompanionRuntimeRole.Service);

        var state = $"hass.agent/system/{Serial}/state";
        var offline = sent.FindIndex(m => m.Topic == state && m.Payload is not null);
        Assert.True(offline >= 0, "the offline state goes out first");
        using (var payload = JsonDocument.Parse(sent[offline].Payload!))
        {
            Assert.False(payload.RootElement.GetProperty("online").GetBoolean());
        }

        Assert.Contains(sent.Skip(offline + 1), m => m.Topic == state && m.Payload is null && m.Retain);
        // Nothing of the tray app's.
        Assert.DoesNotContain(sent, m => m.Topic.StartsWith("hass.agent/devices/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_runtime_that_is_not_on_the_broker_has_nothing_to_leave()
    {
        var settings = new CompanionSettings { DeviceName = "pc", MqttEnabled = true, HaApiEnabled = true };
        settings.SerialNumber = Serial;
        // Never started and no outbox: no broker, as on the HA API. The service then stops as
        // ever, announcing itself offline there.
        var service = new MqttCompanionService(
            settings, new NullNotificationSink(), mediaSessionService: null, systemMetricsService: null,
            systemCommandService: null!, CompanionRuntimeRole.Service, new FileLog(Path.Combine(_folder, "off.log")));

        Assert.False(await service.LeaveBrokerAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task What_a_publisher_sends_as_it_stops_does_not_outlive_the_leave(bool asService)
    {
        var role = asService ? CompanionRuntimeRole.Service : CompanionRuntimeRole.App;
        var topic = asService ? $"hass.agent/system/{Serial}/state" : $"hass.agent/devices/{Serial}/availability";
        var sent = new List<(string Topic, string? Payload, bool Retain)>();
        var service = NewService(role, sent);

        // A publisher of the worker that is in the middle of a publish when the stop comes:
        // its message reaches the broker a moment after the cancellation.
        using var cts = new CancellationTokenSource();
        var worker = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException)
            {
            }

            await Task.Delay(200, CancellationToken.None);
            lock (sent)
            {
                sent.Add((topic, "online", true));
            }
        }, TestContext.Current.CancellationToken);
        service.RunningAs(worker, cts);

        await service.StopAsync(publishOffline: true, leaveBroker: true);

        List<(string Topic, string? Payload, bool Retain)> all;
        lock (sent)
        {
            all = [.. sent];
        }

        // The empty message is the last word on the topic: nothing is retained there.
        var last = all.Last(m => m.Topic == topic);
        Assert.Null(last.Payload);
        Assert.True(last.Retain);
    }

    [Fact]
    public async Task A_service_that_left_the_broker_does_not_announce_itself_offline_there_again()
    {
        var state = $"hass.agent/system/{Serial}/state";
        var sent = new List<(string Topic, string? Payload, bool Retain)>();
        var service = NewService(CompanionRuntimeRole.Service, sent);
        using var cts = new CancellationTokenSource();
        service.RunningAs(Task.CompletedTask, cts);

        await service.StopAsync(publishOffline: true, leaveBroker: true);

        // The offline state for whoever listens, then the empty one, and nothing after it.
        var onTopic = sent.Where(m => m.Topic == state).ToList();
        Assert.Equal(2, onTopic.Count);
        Assert.NotNull(onTopic[0].Payload);
        Assert.Null(onTopic[1].Payload);
    }

    [Fact]
    public async Task A_leave_that_did_not_get_through_leaves_the_usual_offline_announcement()
    {
        var state = $"hass.agent/system/{Serial}/state";
        var sent = new List<(string Topic, string? Payload, bool Retain)>();
        // The connection drops as the empty messages go out: none of them is taken.
        var service = NewService(CompanionRuntimeRole.Service, sent, drops: (_, payload) => payload is null);
        using var cts = new CancellationTokenSource();
        service.RunningAs(Task.CompletedTask, cts);

        Assert.False(await service.LeaveBrokerAsync());

        sent.Clear();
        await service.StopAsync(publishOffline: true, leaveBroker: true);

        // Not counted as left: the stop says offline as it always did, and that is what stays.
        var last = sent.Last(m => m.Topic == state);
        Assert.NotNull(last.Payload);
        using var payload = JsonDocument.Parse(last.Payload!);
        Assert.False(payload.RootElement.GetProperty("online").GetBoolean());
    }

    [Fact]
    public void The_relaunch_watchdog_waits_for_the_setup_longer_than_the_service_lets_an_install_run()
    {
        var script = MqttCompanionService.RelaunchWatchdogScript(@"C:\Program Files\HASS.Agent .NET10\HASS.Agent.NET10.exe");

        // One poll is a second's timeout and a tasklist: the bound is in polls, at least a second each.
        var bound = Regex.Match(script, @"if %tries% lss (\d+) goto waitappear");
        Assert.True(bound.Success);
        Assert.True(int.Parse(bound.Groups[1].Value) >= TimeSpan.FromMinutes(10).TotalSeconds);
        Assert.Contains("timeout /t 1 /nobreak", script);

        // The app is started only after the setup has come and gone (or the wait ran out).
        Assert.True(script.IndexOf(":waitgone", StringComparison.Ordinal) < script.IndexOf(":launch", StringComparison.Ordinal));
        Assert.EndsWith(
            "start \"\" \"C:\\Program Files\\HASS.Agent .NET10\\HASS.Agent.NET10.exe\" --quiet" + Environment.NewLine + "endlocal" + Environment.NewLine,
            script);
    }

    private async Task<List<(string Topic, string? Payload, bool Retain)>> TurnOff(CompanionRuntimeRole role)
    {
        var sent = new List<(string Topic, string? Payload, bool Retain)>();
        var service = NewService(role, sent);

        await service.RestartAsync();
        lock (sent)
        {
            return [.. sent];
        }
    }

    /// <summary>A runtime with MQTT turned off in its settings; the outbox stands for the broker it was connected to.</summary>
    private MqttCompanionService NewService(
        CompanionRuntimeRole role, List<(string Topic, string? Payload, bool Retain)> sent, Func<string, string?, bool>? drops = null)
    {
        var settings = new CompanionSettings { DeviceName = "pc", MqttEnabled = false, HaApiEnabled = false };
        settings.SerialNumber = Serial;
        var log = new FileLog(Path.Combine(_folder, $"{Guid.NewGuid():N}.log"));
        return new MqttCompanionService(
            settings, new NullNotificationSink(), mediaSessionService: null, systemMetricsService: null,
            systemCommandService: null!, role, log)
        {
            Outbox = (topic, payload, retain) =>
            {
                lock (sent)
                {
                    sent.Add((topic, payload, retain));
                }

                return Task.CompletedTask;
            },
            OutboxDrops = drops
        };
    }
}
