using System.Diagnostics;

namespace HASS.Agent.Companion.SystemStatus;

/// <summary>
/// Which session's tray app stands for the PC towards Home Assistant. Every signed-in user has
/// a tray app of their own, and the PC is one device: two apps connected at once publish the
/// same entities in turns and run a command twice, and on MQTT they take each other's session
/// over, both having the PC's client id. So one talks and the others wait: the one in the
/// session the logged-in-user sensor reports too (<see cref="SystemMetricsService.ChooseUserSession"/>),
/// the console's, else the active one (a PC used over Remote Desktop only), else the first
/// disconnected one. A switch of users hands the connection over.
/// </summary>
internal static class SessionRole
{
    /// <summary>Whether this app's session is the one that talks to Home Assistant now.</summary>
    public static bool ThisSessionTalks() => Decide(SystemMetricsService.ElectedUserSessionId(), (uint)Process.GetCurrentProcess().SessionId);

    /// <summary>
    /// Whether the app in <paramref name="current"/> talks: when it is the elected session, and
    /// when there is none (Windows could not list the sessions), rather than nobody talking.
    /// </summary>
    internal static bool Decide(uint? elected, uint current) => elected is null || elected == current;

    /// <summary>For the General page: another session's app has the connection.</summary>
    public static bool WaitingForAnotherSession { get; set; }
}
