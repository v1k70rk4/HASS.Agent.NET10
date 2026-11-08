namespace HASS.Agent.Companion.Runtime;

/// <summary>
/// Whether the app exits to be started again: by Setup or the deploy of a build (the --exit
/// request), or by itself (a change of language, the classic client's settings taken over, a
/// factory reset). The Sendspin player says so to Music Assistant when it leaves, which then
/// keeps the player's place in its group for it.
/// </summary>
internal static class AppRestart
{
    private static volatile bool _pending;

    public static bool Pending
    {
        get => _pending;
        set => _pending = value;
    }
}
