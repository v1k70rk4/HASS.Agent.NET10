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
        var settings = new CompanionSettings { DeviceName = "pc", MqttEnabled = false, HaApiEnabled = false };
        settings.SerialNumber = Serial;
        var log = new FileLog(Path.Combine(_folder, $"{Guid.NewGuid():N}.log"));
        // Never started: the outbox stands for the broker the app was connected to.
        var service = new MqttCompanionService(
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
            }
        };

        await service.RestartAsync();
        lock (sent)
        {
            return [.. sent];
        }
    }
}
