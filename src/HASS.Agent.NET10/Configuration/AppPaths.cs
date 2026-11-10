using HASS.Agent.Companion.Runtime;

namespace HASS.Agent.Companion.Configuration;

internal sealed record AppPaths(
    string ConfigDirectory,
    string SettingsFile,
    string LogFile,
    string DeviceIdFile,
    IReadOnlyList<string> LegacySettingsFiles)
{
    /// <summary>The shared settings folder, ProgramData\HASS.Agent.NET10.</summary>
    public static string ConfigDirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        AppIdentity.ConfigurationDirectoryName);

    public static AppPaths Create()
    {
        var configDirectory = ConfigDirectoryPath;
        var legacyProgramDataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            AppIdentity.LegacyConfigurationDirectoryName);
        var legacyConfigDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppIdentity.LegacyConfigurationDirectoryName);

        Directory.CreateDirectory(configDirectory);

        var settingsFile = Path.Combine(configDirectory, "settings.json");
        var legacyProgramDataSettingsFile = Path.Combine(legacyProgramDataDirectory, "settings.json");
        var legacySettingsFile = Path.Combine(legacyConfigDirectory, "settings.json");
        try
        {
            if (!File.Exists(settingsFile) && LegacySource(legacyProgramDataDirectory, legacyProgramDataSettingsFile, legacySettingsFile) is { } legacy)
            {
                File.Copy(legacy, settingsFile);
            }
        }
        catch (UnauthorizedAccessException) when (KioskMode.AppliesToCurrentUser())
        {
            // A kiosk user may only read the settings folder: their app starts without the
            // settings and cannot write any in their place, so the migration waits for whoever
            // may write there. For anyone else a failed copy still stops the start, as before,
            // and is tried again next time instead of being replaced by defaults.
        }

        return new AppPaths(
            configDirectory,
            settingsFile,
            Path.Combine(configDirectory, "hass-agent-net10.log"),
            // Mirrors the device serial so losing settings.json does not create a new
            // device in Home Assistant. It lives in the config directory on purpose: a
            // deliberate clean install wipes that directory and should give a fresh identity.
            Path.Combine(configDirectory, "device-id"),
            // The migration sources above: a factory reset has to remove them as well, or the
            // next start would simply migrate the old settings back in.
            [legacyProgramDataSettingsFile, legacySettingsFile]);
    }

    /// <summary>
    /// The settings of the first versions to start from, when there are any: the PC's, then
    /// the user's own. The PC's are in ProgramData, where every user can make a folder, and
    /// whoever runs this may be the service or an administrator: a folder or a file there that
    /// another user made is not the PC's settings, and is left where it is.
    /// </summary>
    internal static string? LegacySource(string programDataDirectory, string programDataFile, string userFile) =>
        File.Exists(programDataFile) && SettingsAccess.IsOwnOrAdministrators(programDataDirectory, programDataFile) ? programDataFile
        : File.Exists(userFile) ? userFile
        : null;
}
