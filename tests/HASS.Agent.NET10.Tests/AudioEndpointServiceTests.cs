using System.Diagnostics;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.Media;
using NAudio.CoreAudioApi;

namespace HASS.Agent.Companion.Tests;

/// <summary>
/// The audio devices of the PC the tests run on, read through NAudio as the app reads them. A PC
/// without an output device (a build server) has nothing to read, and the tests are skipped there.
/// Reading changes nothing. The one test that changes something, the mute for a moment, runs only
/// when it is asked for: set HASS_AGENT_AUDIO_TESTS=1.
/// </summary>
public sealed class AudioEndpointServiceTests
{
    [Fact]
    public void The_volume_the_devices_and_the_sessions_of_this_pc_can_be_read()
    {
        Assert.SkipUnless(HasDefaultOutput(), "This PC has no audio output device.");

        using var log = NewLog();
        using var audio = new AudioEndpointService(log);

        Assert.InRange(audio.GetVolume(), 0, 100);
        _ = audio.GetMuted();

        var name = audio.GetOutputDeviceName();
        Assert.False(string.IsNullOrWhiteSpace(name));
        Assert.Contains(name, audio.GetOutputDeviceNames());

        // Whatever plays or records right now: the lists come back, each entry with a name.
        Assert.All(audio.GetSessions(), session =>
        {
            Assert.False(string.IsNullOrWhiteSpace(session.App));
            Assert.InRange(session.Volume, 0, 100);
        });
        _ = audio.GetInputDeviceNames();
        _ = audio.GetCapturingProcessIds();
    }

    [Fact]
    public void A_change_of_the_mute_is_told_without_a_window_to_carry_it()
    {
        Assert.SkipUnless(HasDefaultOutput(), "This PC has no audio output device.");
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("HASS_AGENT_AUDIO_TESTS") == "1",
            "HASS_AGENT_AUDIO_TESTS is not set: this test turns the mute of the PC over for a moment.");

        using var log = NewLog();

        // As in the app, where the service is made on the thread of the window: the notification
        // must not wait for that thread, which here never runs a message loop at all.
        var before = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new NeverRunContext());
        try
        {
            using var audio = new AudioEndpointService(log);
            using var told = new ManualResetEventSlim();
            audio.StateChanged += (_, _) => told.Set();

            // Windows tells of a change only, not of the same value set again.
            var muted = audio.GetMuted();
            try
            {
                audio.SetMuted(!muted);
                Assert.True(told.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "No notification came for the mute.");
            }
            finally
            {
                audio.SetMuted(muted);
            }
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(before);
        }
    }

    private static FileLog NewLog() => new(Path.Combine(Path.GetTempPath(), "hass-agent-tests.log"));

    private static bool HasDefaultOutput()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>A context nobody pumps: what is posted to it is never run.</summary>
    private sealed class NeverRunContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
        }
    }
}
