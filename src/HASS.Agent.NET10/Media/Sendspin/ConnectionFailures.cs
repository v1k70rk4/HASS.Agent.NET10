using System.Diagnostics;
using HASS.Agent.Companion.Logging;
using Timer = System.Threading.Timer;

namespace HASS.Agent.Companion.Media.Sendspin;

/// <summary>
/// Connections to Music Assistant that failed or closed, held back for a while. When the PC
/// wakes, Music Assistant tries several connections to the player at once; one gets through
/// and the rest are closed in the middle of their handshake, which the SDK warns about one by
/// one. A player that dials the server sees its connection closed by the sleep instead, and
/// the SDK warns about that before it connects again by itself. Failures that a connection
/// follows, or that come while a server is connected, go to the debug log only. Failures
/// that no connection follows within <see cref="Grace"/> are a warning, in one line.
/// </summary>
internal sealed class ConnectionFailures : IDisposable
{
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromMinutes(2);

    private readonly Action<string> _warning;
    private readonly Action<string> _debug;
    private readonly Func<bool> _serverConnected;
    private readonly object _gate = new();
    private readonly Timer _timer;
    private int _count;
    private string? _last;
    private long _batchStarted;
    private bool _disposed;

    /// <param name="serverConnected">Whether a server is connected to the player right now.</param>
    public ConnectionFailures(FileLog log, Func<bool> serverConnected)
        : this(log.Warning, log.Debug, DefaultGrace, serverConnected)
    {
    }

    public ConnectionFailures(Action<string> warning, Action<string> debug, TimeSpan grace, Func<bool>? serverConnected = null)
    {
        _warning = warning;
        _debug = debug;
        _serverConnected = serverConnected ?? (() => false);
        Grace = grace;
        _timer = new Timer(_ => TimeIsUp());
    }

    public TimeSpan Grace { get; }

    /// <summary>
    /// Whether a warning of the SDK's is about a connection that failed or closed and that the
    /// next connection makes up for: an incoming one that failed in its handshake, or the
    /// player's own connection to the server that closed, which the SDK opens again by itself.
    /// </summary>
    public static bool IsFailure(string category, string message) => category switch
    {
        "IncomingConnection" or "SendspinHostService" => message.Contains("handshake", StringComparison.OrdinalIgnoreCase),
        "SendspinConnection" => message.StartsWith("Connection closed unexpectedly", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    /// <summary>A failed connection: in the debug log now, a warning later if no connection follows.</summary>
    public void Failed(string message)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _debug(message);
            _count++;
            _last = message;
            if (_count == 1)
            {
                _batchStarted = Stopwatch.GetTimestamp();
                _timer.Change(Grace, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>A server got through: the failures before it were the usual ones.</summary>
    public void Connected()
    {
        lock (_gate)
        {
            if (_disposed || _count == 0)
            {
                return;
            }

            _debug($"Sendspin: {_count} connection(s) failed or closed before one got through.");
            Reset();
        }
    }

    // A callback of the timer can start late, after the batch it was set for ended and a new
    // one began: that one has its own time, and is not judged before it is up. It can also
    // come a little early, as the timer counts in whole milliseconds: then the timer is set
    // again for what is left, rounded up, so the batch is not left without one.
    /// <returns>The time the timer was set again for, or null when there was nothing to wait for.</returns>
    internal TimeSpan? TimeIsUp()
    {
        lock (_gate)
        {
            if (_disposed || _count == 0)
            {
                return null;
            }

            var left = Grace - Stopwatch.GetElapsedTime(_batchStarted);
            if (left <= TimeSpan.Zero)
            {
                GiveUp();
                return null;
            }

            var wait = TimeSpan.FromMilliseconds(Math.Ceiling(left.TotalMilliseconds) + 1);
            _timer.Change(wait, Timeout.InfiniteTimeSpan);
            return wait;
        }
    }

    /// <summary>
    /// The time is up for the failures held back: a warning, unless a server is connected,
    /// which a failure on another connection beside it does not trouble.
    /// </summary>
    internal void GiveUp()
    {
        lock (_gate)
        {
            if (_disposed || _count == 0)
            {
                return;
            }

            if (_serverConnected())
            {
                _debug($"Sendspin: {_count} connection(s) failed or closed beside the one connected.");
            }
            else
            {
                _warning($"Sendspin: {_count} connection(s) failed or closed, and none got through in {Grace.TotalSeconds:0} s. The last: {_last}");
            }

            Reset();
        }
    }

    private void Reset()
    {
        _count = 0;
        _last = null;
        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer.Dispose();
        }
    }
}
