using System.Security.AccessControl;
using System.Security.Principal;
using HASS.Agent.Companion.Logging;

namespace HASS.Agent.Companion.SystemStatus;

/// <summary>
/// The handover of the Home Assistant connection between the tray apps of one PC, across their
/// sessions. A named semaphore of one is held by the app that talks: the next one takes it
/// before it connects, and gets it once the first has stopped and let go, so the two never
/// overlap (the broker would see one take the other's session over). A named event tells every
/// app of the PC to ask again which session stands for it, when one comes up that should talk.
/// Both are made so that every user of the PC may use them; an app that cannot make or open them
/// goes without, as before this class.
/// </summary>
internal sealed class HomeAssistantLink : IDisposable
{
    private const string TurnName = @"Global\HASS.Agent.NET10.HomeAssistantLink";
    private const string SignalName = @"Global\HASS.Agent.NET10.SessionRoleChanged";
    private static readonly TimeSpan TakeTimeout = TimeSpan.FromSeconds(15);

    private readonly FileLog _log;
    private readonly Semaphore? _turn;
    private readonly EventWaitHandle? _signal;
    private readonly ManualResetEvent _stop = new(false);
    private bool _held;

    public HomeAssistantLink(FileLog log)
    {
        _log = log;
        try
        {
            var users = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
            var turnSecurity = new SemaphoreSecurity();
            turnSecurity.AddAccessRule(new SemaphoreAccessRule(users, SemaphoreRights.FullControl, AccessControlType.Allow));
            _turn = SemaphoreAcl.Create(1, 1, TurnName, out _, turnSecurity);

            var signalSecurity = new EventWaitHandleSecurity();
            signalSecurity.AddAccessRule(new EventWaitHandleAccessRule(users, EventWaitHandleRights.FullControl, AccessControlType.Allow));
            _signal = EventWaitHandleAcl.Create(false, EventResetMode.ManualReset, SignalName, out _, signalSecurity);

            new Thread(Watch) { IsBackground = true, Name = "HASS.Agent session role" }.Start();
        }
        catch (Exception ex)
        {
            _log.Warning($"The handover of the connection between the users of this PC is not coordinated: {ex.Message}");
        }
    }

    /// <summary>Another app of the PC says the session that stands for the PC may have changed.</summary>
    public event EventHandler? RoleChangeSignaled;

    /// <summary>
    /// Takes the turn, waiting for the app that has it to stop and let go. False when it did not
    /// in time (it hangs, or died without letting go): the caller connects all the same, as it
    /// would without this class.
    /// </summary>
    public bool Take()
    {
        if (_turn is null || _held)
        {
            return true;
        }

        try
        {
            _held = _turn.WaitOne(TakeTimeout);
        }
        catch (Exception ex)
        {
            _log.Warning($"Unable to wait for the other app of this PC to let the connection go: {ex.Message}");
            return true;
        }

        if (!_held)
        {
            _log.Warning("The other app of this PC did not let the connection go in time; connecting all the same.");
        }

        return _held;
    }

    /// <summary>Lets the turn go, once the connection is stopped.</summary>
    public void LetGo()
    {
        if (_turn is null || !_held)
        {
            return;
        }

        _held = false;
        try
        {
            _turn.Release();
        }
        catch (SemaphoreFullException)
        {
            // Let go already, by a release that did not count.
        }
    }

    /// <summary>Tells every app of the PC to ask again which session stands for it: a pulse of the event.</summary>
    public void Signal()
    {
        var signal = _signal;
        if (signal is null)
        {
            return;
        }

        try
        {
            signal.Set();
            _ = Task.Delay(500).ContinueWith(_ =>
            {
                try { signal.Reset(); } catch { }
            });
        }
        catch (Exception ex)
        {
            _log.Debug($"Unable to signal the other apps of this PC: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _stop.Set();
        LetGo();
        _turn?.Dispose();
        _signal?.Dispose();
    }

    private void Watch()
    {
        var signal = _signal!;
        while (true)
        {
            try
            {
                if (WaitHandle.WaitAny([_stop, signal]) == 0)
                {
                    return;
                }

                RoleChangeSignaled?.Invoke(this, EventArgs.Empty);
                // The pulse stays up for half a second; not to be taken twice.
                if (_stop.WaitOne(700))
                {
                    return;
                }
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }
}
