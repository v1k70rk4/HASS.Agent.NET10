using HASS.Agent.Companion.Logging;
using Timer = System.Threading.Timer;

namespace HASS.Agent.Companion.Media.Sendspin;

/// <summary>
/// Incoming connections that failed in the handshake, held back for a while. When the PC
/// wakes, Music Assistant tries several connections to the player at once; one gets through
/// and the rest are closed in the middle of their handshake, which the SDK warns about one
/// by one. Failures that a connection follows go to the debug log only. Failures that no
/// connection follows within <see cref="Grace"/> are a warning, in one line.
/// </summary>
internal sealed class HandshakeFailures : IDisposable
{
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromMinutes(2);

    private readonly Action<string> _warning;
    private readonly Action<string> _debug;
    private readonly object _gate = new();
    private readonly Timer _timer;
    private int _count;
    private string? _last;
    private bool _disposed;

    public HandshakeFailures(FileLog log)
        : this(log.Warning, log.Debug, DefaultGrace)
    {
    }

    public HandshakeFailures(Action<string> warning, Action<string> debug, TimeSpan grace)
    {
        _warning = warning;
        _debug = debug;
        Grace = grace;
        _timer = new Timer(_ => GiveUp());
    }

    public TimeSpan Grace { get; }

    /// <summary>Whether a warning of the SDK's is about an incoming connection that failed in its handshake.</summary>
    public static bool IsFailure(string category, string message) =>
        category is "IncomingConnection" or "SendspinHostService"
        && message.Contains("handshake", StringComparison.OrdinalIgnoreCase);

    /// <summary>A failed handshake: in the debug log now, a warning later if no connection follows.</summary>
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

            _debug($"Sendspin: {_count} incoming connection(s) failed in the handshake before one got through.");
            Reset();
        }
    }

    /// <summary>No server got through in time: the failures are a warning.</summary>
    internal void GiveUp()
    {
        lock (_gate)
        {
            if (_disposed || _count == 0)
            {
                return;
            }

            _warning($"Sendspin: {_count} incoming connection(s) failed in the handshake, and none got through in {Grace.TotalSeconds:0} s. The last: {_last}");
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
