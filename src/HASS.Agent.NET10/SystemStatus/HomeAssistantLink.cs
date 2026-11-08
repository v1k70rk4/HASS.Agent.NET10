using System.Security.AccessControl;
using System.Security.Principal;
using HASS.Agent.Companion.Logging;

namespace HASS.Agent.Companion.SystemStatus;

/// <summary>
/// The handover of the Home Assistant connection between the tray apps of one PC, across their
/// sessions. A named mutex, the turn, is held by the app that talks: the next one takes it
/// before it connects, and gets it once the first has stopped and let go, so the two never
/// overlap (the broker would see one take the other's session over). An app that dies with the
/// turn leaves the mutex abandoned, and Windows hands it to the next taker. A named event tells
/// every app of the PC to ask again which session stands for it, when one comes up that should
/// talk. Both are open to the users who may use the settings of this PC (the settings folder's
/// rule, <see cref="Participants"/>) and to nobody else; an app that cannot make or open them
/// goes without, as before this class.
/// </summary>
internal sealed class HomeAssistantLink : IDisposable
{
    private const string TurnName = @"Global\HASS.Agent.NET10.HomeAssistantLink";
    private const string SignalName = @"Global\HASS.Agent.NET10.SessionRoleChanged";
    private static readonly TimeSpan TakeTimeout = TimeSpan.FromSeconds(15);

    private readonly FileLog _log;
    private readonly Mutex? _turn;
    private readonly EventWaitHandle? _signal;
    private readonly ManualResetEvent _stop = new(false);
    private ManualResetEventSlim? _letGo;
    private Thread? _holder;

    public HomeAssistantLink(string configDirectory, FileLog log)
    {
        _log = log;
        try
        {
            var participants = Participants(configDirectory);
            var turnSecurity = new MutexSecurity();
            foreach (var participant in participants)
            {
                turnSecurity.AddAccessRule(new MutexAccessRule(participant, MutexRights.FullControl, AccessControlType.Allow));
            }

            _turn = MutexAcl.Create(initiallyOwned: false, TurnName, out _, turnSecurity);
            _signal = EventWaitHandleAcl.Create(false, EventResetMode.ManualReset, SignalName, out _, EventSecurity(participants));
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
    /// The identities that may use the settings of this PC, read from the settings folder's
    /// rule (<see cref="Configuration.SettingsAccess"/>): they may take part in the handover,
    /// nobody else. Every signed-in user when the folder's rule cannot be read.
    /// </summary>
    internal static IReadOnlyList<SecurityIdentifier> Participants(string configDirectory)
    {
        try
        {
            var allowed = new DirectoryInfo(configDirectory)
                .GetAccessControl(AccessControlSections.Access)
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .OfType<FileSystemAccessRule>()
                .Where(rule => rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & FileSystemRights.ReadData) != 0)
                .Select(rule => (SecurityIdentifier)rule.IdentityReference)
                .Distinct()
                .ToList();
            if (allowed.Count > 0)
            {
                return allowed;
            }
        }
        catch (Exception)
        {
            // No folder yet, or not ours to read: every signed-in user, as the folder is by default.
        }

        return [new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null)];
    }

    /// <summary>The rule for a named event of the handover: the participants, in full.</summary>
    internal static EventWaitHandleSecurity EventSecurity(IReadOnlyList<SecurityIdentifier> participants)
    {
        var security = new EventWaitHandleSecurity();
        foreach (var participant in participants)
        {
            security.AddAccessRule(new EventWaitHandleAccessRule(participant, EventWaitHandleRights.FullControl, AccessControlType.Allow));
        }

        return security;
    }

    /// <summary>
    /// Takes the turn, waiting for the app that has it to stop and let go. False when it is not
    /// this app's: the other did not let go in time (it hangs), or there is no telling (the
    /// mutex could not be made or waited for). An app that died with the turn has let go as far
    /// as Windows is concerned. What the caller does with a false rests on how sure it is of
    /// its session (<see cref="SessionRole.MayConnect"/>).
    /// </summary>
    public bool Take()
    {
        if (_letGo is not null)
        {
            return true;
        }

        if (_turn is null)
        {
            return false;
        }

        // A mutex is let go by the thread that took it: a thread of its own holds it until told.
        var taken = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var letGo = new ManualResetEventSlim(false);
        var holder = new Thread(() =>
        {
            bool owned;
            try
            {
                owned = _turn.WaitOne(TakeTimeout);
            }
            catch (AbandonedMutexException)
            {
                // The app that held the turn died without letting go: the turn is this one's.
                owned = true;
            }
            catch (Exception ex)
            {
                _log.Warning($"Unable to wait for the other app of this PC to let the connection go: {ex.Message}");
                owned = false;
            }

            taken.SetResult(owned);
            if (!owned)
            {
                return;
            }

            letGo.Wait();
            try
            {
                _turn.ReleaseMutex();
            }
            catch (Exception)
            {
                // Disposed with the app, or not owned any more: let go either way.
            }

            letGo.Dispose();
        })
        {
            IsBackground = true,
            Name = "HASS.Agent connection turn"
        };

        holder.Start();
        if (!taken.Task.GetAwaiter().GetResult())
        {
            _log.Warning("The other app of this PC did not let the connection go in time.");
            return false;
        }

        _letGo = letGo;
        _holder = holder;
        return true;
    }

    /// <summary>Lets the turn go, once the connection is stopped.</summary>
    public void LetGo()
    {
        var letGo = Interlocked.Exchange(ref _letGo, null);
        if (letGo is null)
        {
            return;
        }

        letGo.Set();
        _holder?.Join(TimeSpan.FromSeconds(2));
        _holder = null;
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
