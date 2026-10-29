using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace HASS.Agent.Companion.Runtime;

/// <summary>
/// The version of the app that last ran on this PC, for telling Home Assistant once that an
/// update went through. One record for the PC, in a key of its own under HKLM where every
/// signed-in user may set the value: the tray app of a user who can only read the settings
/// (kiosk mode) writes it too. The key is made by the service, which runs as SYSTEM, and by
/// every install (as administrator). Anyone may write it, so what is read is used only when
/// it looks like a version: at worst a user hides an update notice or brings one back.
/// </summary>
internal static partial class LastRunRecord
{
    private const string KeyPath = @"SOFTWARE\HASS.Agent.NET10\LastRun";
    private const string ValueName = "Version";

    private static readonly SecurityIdentifier AuthenticatedUsers = new(WellKnownSidType.AuthenticatedUserSid, null);

    /// <summary>The version that last ran; null when there is no record, or it is not a version.</summary>
    public static string? Read()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) is string version && IsVersion(version) ? version : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Records the version; false when there is no key yet or it cannot be written.</summary>
    public static bool TryWrite(string version)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
            if (key is null)
            {
                return false;
            }

            key.SetValue(ValueName, version, RegistryValueKind.String);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Makes the key, and lets every signed-in user set its value (only that: they cannot
    /// delete the key or change who may write it). Needs SYSTEM or an administrator.
    /// </summary>
    public static void Prepare()
    {
        using var key = Registry.LocalMachine.CreateSubKey(KeyPath, writable: true);
        var security = key.GetAccessControl();
        security.SetAccessRule(new RegistryAccessRule(
            AuthenticatedUsers,
            RegistryRights.QueryValues | RegistryRights.SetValue,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        key.SetAccessControl(security);
    }

    internal static bool IsVersion(string? text) => text is { Length: <= 64 } && VersionPattern().IsMatch(text);

    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?(\+[0-9A-Za-z.]+)?$")]
    private static partial Regex VersionPattern();
}
