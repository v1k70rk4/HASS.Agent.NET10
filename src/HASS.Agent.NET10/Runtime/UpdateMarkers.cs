using Microsoft.Win32;

namespace HASS.Agent.Companion.Runtime;

/// <summary>
/// The two notes of the service's update bookkeeping, as values of the app's key under HKLM,
/// which only administrators and SYSTEM may write: "UpdatedFrom", put there by the installer
/// (as administrator) for the service to report the update, and "ServiceVersion", the
/// version the service last reported. They used to be files in the settings folder, which
/// every user may write: a link planted under the note's name would have had the installer
/// write through it, and the service read whatever it found as a version. A value is only
/// taken when it looks like a version (LastRunRecord.IsVersion).
/// </summary>
internal static class UpdateMarkers
{
    private const string KeyPath = @"SOFTWARE\HASS.Agent.NET10";
    private const string UpdatedFromValue = "UpdatedFrom";
    private const string ServiceVersionValue = "ServiceVersion";

    /// <summary>The version the installer replaced, when it left a note; null otherwise.</summary>
    public static string? ReadUpdatedFrom() => Read(UpdatedFromValue);

    public static void ClearUpdatedFrom() => Delete(UpdatedFromValue);

    /// <summary>The version the service last reported; null when none, or not a version.</summary>
    public static string? ReadServiceVersion() => Read(ServiceVersionValue);

    public static void WriteServiceVersion(string version)
    {
        using var key = Registry.LocalMachine.CreateSubKey(KeyPath, writable: true);
        key.SetValue(ServiceVersionValue, version, RegistryValueKind.String);
    }

    private static string? Read(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
            return key?.GetValue(name) is string version && LastRunRecord.IsVersion(version) ? version : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Delete(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
            key?.DeleteValue(name, throwOnMissingValue: false);
        }
        catch (Exception)
        {
            // Left as it is: read again at the next start, and the version check keeps a
            // stale note from reporting more than one update.
        }
    }
}
