using System.Diagnostics;

namespace HASS.Agent.Companion.SystemStatus;

/// <summary>The outcome of <see cref="SessionRole.Evaluate"/>: whether this app talks, and whether that is to be asked again.</summary>
internal sealed record SessionVerdict(bool Talks, bool Provisional);

/// <summary>
/// Which session's tray app stands for the PC towards Home Assistant. Every signed-in user has
/// a tray app of their own, and the PC is one device: two apps connected at once publish the
/// same entities in turns and run a command twice, and on MQTT they take each other's session
/// over, both having the PC's client id. So one talks and the others wait: the one in the
/// session the logged-in-user sensor reports too (<see cref="SystemMetricsService.ChooseUserSession"/>),
/// the console's, else the active one (a PC used over Remote Desktop only), else the first
/// disconnected one; among the sessions that run a tray app, when that session has none (a
/// user who may not use the settings). A switch of users hands the connection over.
/// </summary>
internal static class SessionRole
{
    public static SessionVerdict Evaluate()
    {
        var current = (uint)Process.GetCurrentProcess().SessionId;
        var withApp = SessionsWithTrayApp();
        return Decide(SystemMetricsService.ElectUserSession(withApp), current, withApp);
    }

    /// <summary>Whether this app's session is the one that talks to Home Assistant now.</summary>
    public static bool ThisSessionTalks() => Evaluate().Talks;

    /// <summary>
    /// The verdict for the app in <paramref name="current"/>. With an election it talks when it
    /// is the elected session. With none (Windows could not list the sessions) the lowest
    /// session that runs a tray app talks, so that one talks rather than every one or none; with
    /// no such list either, this one. A verdict is provisional when it rests on which apps run
    /// rather than on the sessions alone: the app at the console may still be starting.
    /// </summary>
    internal static SessionVerdict Decide(SessionElection? election, uint current, IReadOnlyCollection<uint> withApp)
    {
        if (election is { } result)
        {
            return new SessionVerdict(result.Elected == current, result.Provisional);
        }

        return withApp.Count > 0
            ? new SessionVerdict(withApp.Min() == current, Provisional: true)
            : new SessionVerdict(true, Provisional: true);
    }

    /// <summary>
    /// Whether an app with <paramref name="verdict"/> connects, given whether it got the turn
    /// (the PC's one connection, <c>HomeAssistantLink.Take</c>). A final verdict connects either
    /// way: the sessions alone named this one, and a turn not got means the other app hangs or
    /// the turn cannot be told. A provisional one rests on which apps run, which two apps may
    /// both see wrongly; it connects only with the turn in hand, and is asked again otherwise.
    /// </summary>
    internal static bool MayConnect(SessionVerdict verdict, bool turnTaken) => turnTaken || !verdict.Provisional;

    /// <summary>For the General page: another session's app has the connection.</summary>
    public static bool WaitingForAnotherSession { get; set; }

    // The sessions with a tray app of this program in them, the service's session 0 left out.
    // This app's own session is always among them, so the list is never empty: two apps that
    // both see the others pick the same lowest session, and one that sees none but itself
    // talks, which is right when it is alone.
    private static HashSet<uint> SessionsWithTrayApp()
    {
        var current = Process.GetCurrentProcess();
        var sessions = new HashSet<uint> { (uint)current.SessionId };
        try
        {
            foreach (var process in Process.GetProcessesByName(current.ProcessName))
            {
                using (process)
                {
                    try
                    {
                        if (process.SessionId != 0)
                        {
                            sessions.Add((uint)process.SessionId);
                        }
                    }
                    catch (Exception)
                    {
                        // Gone, or not ours to look at.
                    }
                }
            }
        }
        catch (Exception)
        {
            // The processes could not be listed: this session alone is known.
        }

        return sessions;
    }
}
