using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.Media.Sendspin;
using Microsoft.Extensions.Logging;

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
    // The player's own connection to the server, closed by the sleep: the SDK dials again by itself.
    [InlineData("SendspinConnection", "Connection closed unexpectedly", true)]
    [InlineData("SendspinConnection", "Error completing close handshake", false)]
    public void Only_connections_the_next_one_makes_up_for_are_held_back(string category, string message, bool held)
    {
        Assert.Equal(held, ConnectionFailures.IsFailure(category, message));
    }

    [Fact]
    public void Failures_a_connection_follows_are_no_warning()
    {
        var warnings = new List<string>();
        var debug = new List<string>();
        using var failures = new ConnectionFailures(warnings.Add, debug.Add, TimeSpan.FromHours(1));

        failures.Failed(Closed);
        failures.Failed(Failed);
        failures.Connected();
        failures.GiveUp();

        Assert.Empty(warnings);
        Assert.Contains(Closed, debug);
        Assert.Contains(debug, line => line.Contains("2 connection(s) failed or closed before one got through", StringComparison.Ordinal));
    }

    [Fact]
    public void Failures_no_connection_follows_are_one_warning()
    {
        var warnings = new List<string>();
        using var failures = new ConnectionFailures(warnings.Add, _ => { }, TimeSpan.FromHours(1));

        failures.Failed(Closed);
        failures.Failed(Failed);
        failures.Failed(Closed);
        failures.GiveUp();
        failures.GiveUp();

        var warning = Assert.Single(warnings);
        Assert.Contains("3 connection(s) failed or closed, and none got through", warning, StringComparison.Ordinal);
        Assert.EndsWith(Closed, warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_warning_comes_by_itself_once_the_time_is_up()
    {
        var warned = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var failures = new ConnectionFailures(line => warned.TrySetResult(line), _ => { }, TimeSpan.FromMilliseconds(50));

        failures.Failed(Failed);

        var done = await Task.WhenAny(warned.Task, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Same(warned.Task, done);
    }

    [Fact]
    public void Failures_beside_a_connected_server_are_no_warning()
    {
        var warnings = new List<string>();
        var debug = new List<string>();
        using var failures = new ConnectionFailures(warnings.Add, debug.Add, TimeSpan.FromHours(1), serverConnected: () => true);

        failures.Failed(Closed);
        failures.GiveUp();

        Assert.Empty(warnings);
        Assert.Contains(debug, line => line.Contains("beside the one connected", StringComparison.Ordinal));
    }

    [Fact]
    public void A_late_timer_does_not_judge_a_batch_before_its_time()
    {
        var warnings = new List<string>();
        using var failures = new ConnectionFailures(warnings.Add, _ => { }, TimeSpan.FromHours(1));

        // The batch before ended with a connection; a new one has just begun when the old
        // batch's timer comes.
        failures.Failed(Closed);
        failures.Connected();
        failures.Failed(Failed);
        failures.TimeIsUp();

        Assert.Empty(warnings);
    }

    [Fact]
    public void An_early_timer_sets_itself_again_for_what_is_left()
    {
        var warnings = new List<string>();
        using var failures = new ConnectionFailures(warnings.Add, _ => { }, TimeSpan.FromHours(1));

        failures.Failed(Closed);
        var again = failures.TimeIsUp();

        Assert.Empty(warnings);
        Assert.NotNull(again);
        Assert.InRange(again.Value, TimeSpan.FromMinutes(59), TimeSpan.FromHours(1) + TimeSpan.FromMilliseconds(2));
    }

    [Fact]
    public void A_timer_with_nothing_held_back_is_not_set_again()
    {
        using var failures = new ConnectionFailures(_ => { }, _ => { }, TimeSpan.FromHours(1));

        Assert.Null(failures.TimeIsUp());
    }

    [Fact]
    public void The_provider_holds_back_the_failures_and_keeps_the_other_warnings()
    {
        var folder = Path.Combine(Path.GetTempPath(), "hass-agent-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "sendspin.log");
        try
        {
            using var log = new FileLog(path);
            using var failures = new ConnectionFailures(log.Warning, log.Debug, TimeSpan.FromHours(1));
            using var provider = new FileLogLoggerProvider(log, failures);

            provider.CreateLogger("Sendspin.SDK.Connection.IncomingConnection")
                .LogWarning("Connection closed during the handshake before the server replied (no close status)");
            provider.CreateLogger("Sendspin.SDK.Client.SendspinHostService")
                .LogWarning("Handshake failed for connection d2e8818b");
            provider.CreateLogger("Sendspin.SDK.Client.SendspinClientService")
                .LogWarning("[ClockSync] Convergence lost after 10 measurements");
            provider.CreateLogger("Sendspin.SDK.Connection.SendspinConnection")
                .LogWarning("Connection closed unexpectedly");
            provider.CreateLogger("Sendspin.SDK.Client.SendspinHostService")
                .LogWarning("Port 8928 is in use");

            var text = File.ReadAllText(path);
            Assert.DoesNotContain("handshake", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("closed unexpectedly", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ClockSync", text, StringComparison.Ordinal);
            Assert.Contains("[WARN] Sendspin SendspinHostService: Port 8928 is in use", text, StringComparison.Ordinal);

            // Then held back, not dropped: no connection followed.
            failures.GiveUp();
            Assert.Contains("[WARN] Sendspin: 3 connection(s) failed or closed, and none got through", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    // As the SDK throws it when the PC goes to sleep while it writes to Music Assistant.
    private static Exception DroppedByTheNetwork() => new System.Net.WebSockets.WebSocketException(
        "The remote party closed the WebSocket connection without completing the close handshake.",
        new IOException("Unable to write data to the transport connection.", new System.Net.Sockets.SocketException(10053)));

    [Fact]
    public void A_connection_the_network_dropped_is_held_back_in_one_line()
    {
        var folder = Path.Combine(Path.GetTempPath(), "hass-agent-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "sendspin.log");
        try
        {
            using var log = new FileLog(path);
            using var failures = new ConnectionFailures(log.Warning, log.Debug, TimeSpan.FromHours(1));
            using var provider = new FileLogLoggerProvider(log, failures);

            provider.CreateLogger("Sendspin.SDK.Client.SendspinHostService")
                .LogError(DroppedByTheNetwork(), "Error handling server connection 60582c75");

            Assert.False(File.Exists(path) && File.ReadAllText(path).Contains("60582c75", StringComparison.Ordinal));

            // No connection followed: one warning, with no stack trace.
            failures.GiveUp();
            var records = File.ReadAllLines(path).Where(line => !line.Contains("Log rotated", StringComparison.Ordinal))
                .Select(line => line[(line.IndexOf(' ') + 1)..]).ToArray();
            Assert.Equal(
                ["[WARN] Sendspin: 1 connection(s) failed or closed, and none got through in 3600 s. The last: Sendspin SendspinHostService: Error handling server connection 60582c75 (The remote party closed the WebSocket connection without completing the close handshake.)"],
                records);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public void Any_other_error_of_a_connection_stays_an_error_with_its_stack_trace()
    {
        var folder = Path.Combine(Path.GetTempPath(), "hass-agent-test-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "sendspin.log");
        try
        {
            using var log = new FileLog(path);
            using var failures = new ConnectionFailures(log.Warning, log.Debug, TimeSpan.FromHours(1));
            using var provider = new FileLogLoggerProvider(log, failures);
            Exception bug;
            try
            {
                throw new InvalidOperationException("Unexpected message type");
            }
            catch (InvalidOperationException ex)
            {
                bug = ex;
            }

            provider.CreateLogger("Sendspin.SDK.Client.SendspinHostService")
                .LogError(bug, "Error handling server connection 51c3f07b");

            var text = File.ReadAllText(path);
            Assert.Contains("[ERROR] Sendspin SendspinHostService: Error handling server connection 51c3f07b", text, StringComparison.Ordinal);
            Assert.Contains("System.InvalidOperationException: Unexpected message type", text, StringComparison.Ordinal);
            Assert.Contains(" at ", text, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("SendspinHostService", "Error handling server connection 60582c75", true, true)]
    [InlineData("SendspinHostService", "Error handling server connection 60582c75", false, false)]
    [InlineData("SendspinHostService", "Port 8928 is in use", true, false)]
    [InlineData("SendspinClientService", "Error handling server connection 60582c75", true, false)]
    public void Only_a_server_connection_the_network_dropped_counts(string category, string message, bool network, bool dropped)
    {
        var exception = network ? DroppedByTheNetwork() : new InvalidOperationException("Unexpected message type");

        Assert.Equal(dropped, FileLogLoggerProvider.IsDroppedConnection(category, message, exception));
    }

    [Fact]
    public void A_connection_before_any_failure_says_nothing()
    {
        var lines = new List<string>();
        using var failures = new ConnectionFailures(lines.Add, lines.Add, TimeSpan.FromHours(1));

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
