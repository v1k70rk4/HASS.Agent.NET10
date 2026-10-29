using System.Text.Json;
using System.Text.Json.Nodes;
using HASS.Agent.Companion.Logging;

using HASS.Agent.Companion.Security;

namespace HASS.Agent.Companion.Configuration;

internal static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static CompanionSettings LoadOrCreate(AppPaths paths, FileLog log)
    {
        CompanionSettings settings;
        string? originalJson = null;
        var restoredFromBackup = false;

        if (File.Exists(paths.SettingsFile))
        {
            originalJson = File.ReadAllText(paths.SettingsFile);
            try
            {
                settings = JsonSerializer.Deserialize<CompanionSettings>(originalJson, JsonOptions) ?? new CompanionSettings();
            }
            catch (JsonException) when (TryReadBackup(paths, out var backupJson, out var backupSettings))
            {
                // The settings file is unreadable (a truncated write, for example) but the
                // backup still parses — recover instead of starting from scratch.
                settings = backupSettings!;
                originalJson = backupJson;
                restoredFromBackup = true;
            }
        }
        else if (TryReadBackup(paths, out var backupJson, out var backupSettings))
        {
            settings = backupSettings!;
            originalJson = backupJson;
            restoredFromBackup = true;
        }
        else
        {
            settings = new CompanionSettings();
        }

        // Restore the device serial from its sidecar before Normalize() would mint a new
        // one: a lost settings file must not turn this PC into a new Home Assistant device.
        var adoptedSerial = false;
        if (string.IsNullOrWhiteSpace(settings.SerialNumber))
        {
            var storedSerial = ReadDeviceId(paths);
            if (storedSerial is not null)
            {
                settings.SerialNumber = storedSerial;
                adoptedSerial = true;
            }
        }

        var keptLocalApiOn = originalJson is not null && KeepLocalApiOnFromOlderFile(originalJson, settings);
        settings.Normalize();
        WriteDeviceId(paths, settings.SerialNumber);
        var migratedPassword = settings.MigratePlainTextPassword();
        var migratedPasswordScope = settings.MigrateProtectedPasswordToMachineScope();
        var migratedHaApiToken = settings.MigrateHaApiPlainTextToken();
        var normalizedJson = Serialize(settings);
        // restoredFromBackup/adoptedSerial force a write: the settings file itself is
        // missing or unreadable in those cases and has to be rebuilt, even when the
        // recovered content happens to match what we would have written.
        string? notWritten = null;
        if (originalJson is null || restoredFromBackup || adoptedSerial || keptLocalApiOn || migratedPassword || migratedPasswordScope || migratedHaApiToken || !JsonEquals(originalJson, normalizedJson))
        {
            try
            {
                Write(paths, normalizedJson, rotateBackup: !restoredFromBackup);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                // A user who may only read the settings (kiosk mode) still starts: the settings
                // are used as they were read, and whoever may change them writes them back.
                notWritten = ex.Message;
            }
        }

        log.Info($"Loaded settings from {paths.SettingsFile}.");
        if (notWritten is not null)
        {
            log.Info($"The settings were not written back (read only for this user): {notWritten}");
        }
        if (restoredFromBackup)
        {
            log.Warning($"Settings were unreadable or missing; restored from {Path.GetFileName(BackupFile(paths))}.");
        }
        if (adoptedSerial)
        {
            log.Warning("Settings were reset; kept the existing device serial so Home Assistant sees the same device.");
        }
        if (migratedPassword)
        {
            log.Info("Migrated MQTT password to Windows protected storage.");
        }
        if (migratedPasswordScope)
        {
            log.Info("Migrated MQTT password to machine protected storage for service access.");
        }
        if (migratedHaApiToken)
        {
            log.Info("Migrated HA API token to Windows protected storage.");
        }

        return settings;
    }

    /// <summary>
    /// Removes every file the next start could rebuild the settings from. Deleting only
    /// settings.json is not a reset: LoadOrCreate restores it from the backup, adopts the
    /// serial from the device-id sidecar, and AppPaths.Create migrates a legacy file back.
    /// </summary>
    public static void FactoryReset(AppPaths paths, FileLog log)
    {
        var files = new List<string> { paths.SettingsFile, BackupFile(paths), paths.DeviceIdFile };
        files.AddRange(paths.LegacySettingsFiles);
        foreach (var file in files)
        {
            if (!File.Exists(file))
            {
                continue;
            }

            File.Delete(file);
            log.Info($"Factory reset: deleted {file}.");
        }
    }

    public static void Save(AppPaths paths, CompanionSettings settings)
    {
        settings.Normalize();
        Write(paths, Serialize(settings));
    }

    /// <summary>The settings an export leaves out: every secret, whether it would travel or not.</summary>
    internal static readonly string[] SecretsLeftOutOfExport =
    [
        // DPAPI blobs, bound to this machine: useless anywhere else.
        "mqttPasswordProtected",
        "haApiTokenProtected",
        "libreHardwareMonitorPasswordProtected",
        // Plain text: whoever gets the file could send this PC notifications. Import makes a new one.
        "apiKey",
    ];

    /// <summary>Writes a portable copy of the settings, without the secrets.</summary>
    public static void Export(CompanionSettings settings, string targetFile)
    {
        File.WriteAllText(targetFile, ExportJson(settings));
    }

    internal static string ExportJson(CompanionSettings settings)
    {
        var node = JsonSerializer.SerializeToNode(settings, JsonOptions)!.AsObject();
        foreach (var secret in SecretsLeftOutOfExport)
        {
            node.Remove(secret);
        }

        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static CompanionSettings Import(string sourceFile)
    {
        var json = File.ReadAllText(sourceFile);
        var settings = JsonSerializer.Deserialize<CompanionSettings>(json, JsonOptions)
            ?? throw new InvalidDataException("Not a valid settings file.");
        KeepLocalApiOnFromOlderFile(json, settings);
        settings.Normalize();
        return settings;
    }

    /// <summary>
    /// A file from before 10.9.3 has no switch for the local HTTP API, and a key, because the
    /// API was always on: it stays on, with its key, so a PC set up in Home Assistant with the
    /// Local HTTP API goes on working. Only a new install starts with it off.
    /// </summary>
    internal static bool KeepLocalApiOnFromOlderFile(string json, CompanionSettings settings)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.TryGetProperty("localApiEnabled", out _)
                || !root.TryGetProperty("apiKey", out var key)
                || key.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(key.GetString()))
            {
                return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        settings.LocalApiEnabled = true;
        return true;
    }

    private static string BackupFile(AppPaths paths) => paths.SettingsFile + ".bak";

    private static bool TryReadBackup(AppPaths paths, out string? json, out CompanionSettings? settings)
    {
        json = null;
        settings = null;
        var backup = BackupFile(paths);
        if (!File.Exists(backup))
        {
            return false;
        }

        try
        {
            var backupJson = File.ReadAllText(backup);
            var parsed = JsonSerializer.Deserialize<CompanionSettings>(backupJson, JsonOptions);
            if (parsed is null)
            {
                return false;
            }

            json = backupJson;
            settings = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadDeviceId(AppPaths paths)
    {
        try
        {
            if (!File.Exists(paths.DeviceIdFile))
            {
                return null;
            }

            var value = File.ReadAllText(paths.DeviceIdFile).Trim();
            return value.Length > 0 ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteDeviceId(AppPaths paths, string serialNumber)
    {
        if (string.IsNullOrWhiteSpace(serialNumber) || ReadDeviceId(paths) == serialNumber)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(paths.ConfigDirectory);
            SafeFiles.WriteAllText(paths.DeviceIdFile, serialNumber);
        }
        catch
        {
            // Best effort: the serial in settings.json remains the source of truth.
        }
    }

    /// <summary>
    /// Writes the settings atomically. A plain write truncates the file first, so a process
    /// kill at the wrong moment (the installer force-closes the app during an update) could
    /// leave it empty. Writing to a temp file and replacing keeps the previous content as a
    /// backup and never leaves a half-written settings file behind.
    /// </summary>
    private static void Write(AppPaths paths, string json, bool rotateBackup = true)
    {
        Directory.CreateDirectory(paths.ConfigDirectory);
        var target = paths.SettingsFile;
        // Unique per write: the tray app and the service both save settings, so a shared
        // temp path would let one process commit the other's content.
        var temp = $"{target}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";

        try
        {
            // Through SafeFiles, as every write into the shared folder: a link put under the
            // temp file's name beforehand is not followed.
            SafeFiles.WriteAllText(temp, json);
            if (File.Exists(target))
            {
                // rotateBackup is off when recovering from the backup: the file we are
                // replacing is the unreadable one, and it must not overwrite the good copy.
                File.Replace(temp, target, rotateBackup ? BackupFile(paths) : null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, target);
            }

            return;
        }
        catch
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch
            {
                // Ignore: the direct write below is what matters.
            }
        }

        // Fall back to a direct write so a locked file or a failed replace can never stop
        // settings from being saved at all.
        SafeFiles.WriteAllText(target, json);
    }

    private static string Serialize(CompanionSettings settings)
    {
        return JsonSerializer.Serialize(settings, JsonOptions);
    }

    private static bool JsonEquals(string left, string right)
    {
        try
        {
            using var leftDocument = JsonDocument.Parse(left);
            using var rightDocument = JsonDocument.Parse(right);
            return JsonElementEquals(leftDocument.RootElement, rightDocument.RootElement);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.Ordinal);
        }
    }

    private static bool JsonElementEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            return false;
        }

        return left.ValueKind switch
        {
            JsonValueKind.Object => ObjectEquals(left, right),
            JsonValueKind.Array => ArrayEquals(left, right),
            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.Number => left.GetRawText() == right.GetRawText(),
            JsonValueKind.True or JsonValueKind.False => left.GetBoolean() == right.GetBoolean(),
            JsonValueKind.Null or JsonValueKind.Undefined => true,
            _ => left.GetRawText() == right.GetRawText()
        };
    }

    private static bool ObjectEquals(JsonElement left, JsonElement right)
    {
        var leftProperties = left.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).ToList();
        var rightProperties = right.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).ToList();
        if (leftProperties.Count != rightProperties.Count)
        {
            return false;
        }

        for (var index = 0; index < leftProperties.Count; index++)
        {
            if (leftProperties[index].Name != rightProperties[index].Name ||
                !JsonElementEquals(leftProperties[index].Value, rightProperties[index].Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ArrayEquals(JsonElement left, JsonElement right)
    {
        var leftItems = left.EnumerateArray().ToList();
        var rightItems = right.EnumerateArray().ToList();
        if (leftItems.Count != rightItems.Count)
        {
            return false;
        }

        for (var index = 0; index < leftItems.Count; index++)
        {
            if (!JsonElementEquals(leftItems[index], rightItems[index]))
            {
                return false;
            }
        }

        return true;
    }
}
