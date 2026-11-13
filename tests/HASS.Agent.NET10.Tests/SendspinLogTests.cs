using HASS.Agent.Companion.Media.Sendspin;

namespace HASS.Agent.Companion.Tests;

public class SendspinLogTests
{
    // As the SDK writes them when the PC wakes and Music Assistant tries several connections.
    private const string Closed = "Sendspin IncomingConnection: Connection closed during the handshake before the server replied (no close status); the handshake did not complete";
    private const string Failed = "Sendspin SendspinHostService: Handshake failed for connection d2e8818b";

    [Theory]
    [InlineData("IncomingConnection", "Connection closed during the handshake before the server replied (no close status)", true)]
    [InlineData("SendspinHostService", "Handshake failed for connection d2e8818b", true)]
    [InlineData("SendspinClientService", "Handshake failed", false)]
    [InlineData("SendspinHostService", "Port 8928 is in use", false)]
    public void Only_failed_incoming_handshakes_are_held_back(string category, string message, bool held)
    {
        Assert.Equal(held, HandshakeFailures.IsFailure(category, message));
    }

    [Fact]
    public void Failures_a_connection_follows_are_no_warning()
    {
        var warnings = new List<string>();
        var debug = new List<string>();
        using var failures = new HandshakeFailures(warnings.Add, debug.Add, TimeSpan.FromHours(1));

        failures.Failed(Closed);
        failures.Failed(Failed);
        failures.Connected();
        failures.GiveUp();

        Assert.Empty(warnings);
        Assert.Contains(Closed, debug);
        Assert.Contains(debug, line => line.Contains("2 incoming connection(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void Failures_no_connection_follows_are_one_warning()
    {
        var warnings = new List<string>();
        using var failures = new HandshakeFailures(warnings.Add, _ => { }, TimeSpan.FromHours(1));

        failures.Failed(Closed);
        failures.Failed(Failed);
        failures.Failed(Closed);
        failures.GiveUp();
        failures.GiveUp();

        var warning = Assert.Single(warnings);
        Assert.Contains("3 incoming connection(s)", warning, StringComparison.Ordinal);
        Assert.EndsWith(Closed, warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_warning_comes_by_itself_once_the_time_is_up()
    {
        var warned = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var failures = new HandshakeFailures(line => warned.TrySetResult(line), _ => { }, TimeSpan.FromMilliseconds(50));

        failures.Failed(Failed);

        var done = await Task.WhenAny(warned.Task, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Same(warned.Task, done);
    }

    [Fact]
    public void A_connection_before_any_failure_says_nothing()
    {
        var lines = new List<string>();
        using var failures = new HandshakeFailures(lines.Add, lines.Add, TimeSpan.FromHours(1));

        failures.Connected();

        Assert.Empty(lines);
    }

    [Theory]
    [InlineData("[ClockSync] Convergence lost after 10 measurements", true)]
    [InlineData("Handshake failed for connection d2e8818b", false)]
    public void The_clock_measuring_again_is_a_debug_note(string message, bool note)
    {
        Assert.Equal(note, FileLogLoggerProvider.IsClockNote(message));
    }
}
