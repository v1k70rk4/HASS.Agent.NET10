namespace HASS.Agent.Companion.Configuration;

/// <summary>How a notification is shown: the setting, and the per-notification override.</summary>
internal static class NotificationStyles
{
    /// <summary>A Windows notification: follows Do not disturb, stays in the notification centre.</summary>
    public const string Toast = "toast";

    /// <summary>The app's own always-on-top window.</summary>
    public const string Window = "window";

    public static string Normalize(string? value, string fallback = Toast)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            Toast => Toast,
            Window => Window,
            _ => fallback
        };
    }
}
