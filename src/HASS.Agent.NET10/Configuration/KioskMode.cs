using System.Runtime.InteropServices;
using System.Security.Principal;

namespace HASS.Agent.Companion.Configuration;

/// <summary>
/// Kiosk mode, for a PC where the sensors need the tray app but the person in front of the
/// screen must not change anything. The kiosk users are the members of a local group, "HASS.Agent
/// Kiosk", which only an administrator can change: with this app (Danger Zone, Kiosk mode,
/// --kiosk) or with Windows' own tools, also from a domain's group policy. For them the tray
/// app shows nothing but "Kiosk mode" in the tray: no window, no menu, no Exit; the settings
/// folder denies the group every change (SettingsAccess.SetFolderRule); and a scheduled task
/// that runs for the group starts the app (KioskWatchdog). Kiosk mode is on while the group is
/// there. An administrator is never a kiosk user. Like any group, a new member is one from
/// their next logon.
/// </summary>
internal static class KioskMode
{
    public const string GroupName = "HASS.Agent Kiosk";
    public const string GroupComment = "Get the HASS.Agent .NET10 tray app locked: no window, no menu, no Exit.";

    /// <summary>The kiosk group; null when kiosk mode is off.</summary>
    public static SecurityIdentifier? GroupSid() => SettingsAccess.TryGroupSid(GroupName);

    /// <summary>Whether the user running this gets the kiosk tray: in the group by their logon, and no administrator.</summary>
    public static bool AppliesToCurrentUser()
    {
        try
        {
            if (GroupSid() is not { } group)
            {
                return false;
            }

            using var identity = WindowsIdentity.GetCurrent();
            return IsKioskUser(identity.Groups?.Contains(group) == true, IsAdministrator(identity));
        }
        catch (Exception)
        {
            // The group cannot be looked up: the app the user had before.
            return false;
        }
    }

    internal static bool IsKioskUser(bool inGroup, bool administrator) => inGroup && !administrator;

    /// <summary>
    /// An administrator of this PC, also when the app runs without elevation: with UAC an
    /// administrator gets a limited token, in which the Administrators group only denies, so
    /// asking for the role would say no. The elevation type of the token says it either way;
    /// without UAC there is one token, and the role is asked.
    /// </summary>
    internal static bool IsAdministrator(WindowsIdentity identity)
    {
        if (GetTokenInformation(identity.Token, TokenElevationType, out var type, sizeof(int), out _)
            && type is TokenElevationTypeFull or TokenElevationTypeLimited)
        {
            return true;
        }

        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private const int TokenElevationType = 18;
    private const int TokenElevationTypeFull = 2;
    private const int TokenElevationTypeLimited = 3;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass, out int information, int length, out int returned);
}
