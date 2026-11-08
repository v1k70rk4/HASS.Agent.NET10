namespace HASS.Agent.Companion.Runtime;

/// <summary>
/// Whether the app exits to be started again: by Setup or the deploy of a build (the --exit
/// request), or by itself (a change of language, the classic client's settings taken over, a
/// factory reset). The Sendspin player says so to Music Assistant when it leaves, which then
/// keeps the player's place in its group for it.
/// </summary>
internal static class AppRestart
{
    /// <summary>How long the restart's new instance waits for the one before to leave.</summary>
    public static readonly TimeSpan WaitForTheOneBefore = TimeSpan.FromSeconds(30);

    /// <summary>How long the leaving instance gives the Sendspin player for its goodbye.</summary>
    public static readonly TimeSpan PlayerLeaveTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long the leaving instance gives the connection (MQTT, Local API) to stop.</summary>
    public static readonly TimeSpan ConnectionLeaveTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long the leaving instance waits for a start of the connection still under way.</summary>
    public static readonly TimeSpan RoleLeaveTimeout = TimeSpan.FromSeconds(5);

    private static volatile bool _pending;

    public static bool Pending
    {
        get => _pending;
        set => _pending = value;
    }
}
