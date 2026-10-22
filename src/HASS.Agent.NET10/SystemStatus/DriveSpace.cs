namespace HASS.Agent.Companion.SystemStatus;

/// <summary>One drive with a letter, as the drive space sensor reports it.</summary>
internal sealed record DriveSpaceInfo(
    string Letter,
    string Label,
    string FileSystem,
    bool Removable,
    double TotalGb,
    double FreeGb);

/// <summary>
/// The space on the drives with a letter: the fixed ones, USB disks included, and removable ones
/// (a USB stick, a card) with something in them. Network drives are left out: the service, which
/// runs as SYSTEM, does not see a user's mapped drives, and one that does not answer would hold
/// up the read. The attributes are keyed by the letter (drive_space.D.free_gb), so a sensor made
/// on a drive stays on that drive however many sticks come and go.
/// </summary>
internal static class DriveSpace
{
    public static IReadOnlyList<DriveSpaceInfo> Read()
    {
        var drives = new List<DriveSpaceInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) ||
                    !drive.IsReady ||
                    drive.TotalSize <= 0 ||
                    Letter(drive.Name) is not { } letter)
                {
                    continue;
                }

                drives.Add(new DriveSpaceInfo(
                    letter,
                    drive.VolumeLabel,
                    drive.DriveFormat,
                    drive.DriveType == DriveType.Removable,
                    Gb(drive.TotalSize),
                    Gb(drive.AvailableFreeSpace)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Ejected or locked (BitLocker) between the listing and the read: not there.
            }
        }

        return Order(drives);
    }

    /// <summary>The letters with a drive now, in the order the attributes list them.</summary>
    public static IReadOnlyList<string> Letters()
    {
        try
        {
            return Read().Select(drive => drive.Letter).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>The fixed drives first, then the removable ones, each by letter.</summary>
    internal static IReadOnlyList<DriveSpaceInfo> Order(IEnumerable<DriveSpaceInfo> drives) =>
        drives.OrderBy(drive => drive.Removable).ThenBy(drive => drive.Letter, StringComparer.Ordinal).ToList();

    /// <summary>The letter of the drive Windows runs from ("C").</summary>
    public static string? SystemLetter() => Letter(Path.GetPathRoot(Environment.SystemDirectory) ?? string.Empty);

    /// <summary>
    /// The sensor's value: how full the system drive is, in percent. Always that one, so the value
    /// does not jump to a USB stick that is fuller; the other drives are in the attributes.
    /// </summary>
    internal static double? SystemPercent(IReadOnlyList<DriveSpaceInfo> drives, string? systemLetter) =>
        drives.FirstOrDefault(drive => drive.Letter == systemLetter) is { } system ? UsedPercent(system) : null;

    /// <summary>The attributes: which drive the value is about, and one entry per letter.</summary>
    internal static IReadOnlyDictionary<string, object?> Attributes(IReadOnlyList<DriveSpaceInfo> drives, string? systemLetter)
    {
        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (drives.Any(drive => drive.Letter == systemLetter))
        {
            attributes["system"] = systemLetter;
        }

        foreach (var drive in drives)
        {
            attributes[drive.Letter] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["label"] = drive.Label,
                ["file_system"] = drive.FileSystem,
                ["removable"] = drive.Removable,
                ["total_gb"] = drive.TotalGb,
                ["used_gb"] = Math.Round(drive.TotalGb - drive.FreeGb, 1),
                ["free_gb"] = drive.FreeGb,
                ["used_percent"] = UsedPercent(drive),
                ["free_percent"] = Math.Round(100 - UsedPercent(drive), 1)
            };
        }

        return attributes;
    }

    internal static double UsedPercent(DriveSpaceInfo drive) =>
        drive.TotalGb <= 0 ? 0 : Math.Round((drive.TotalGb - drive.FreeGb) * 100 / drive.TotalGb, 1);

    // "D:\" → "D"; a volume mounted in a folder has no letter of its own.
    private static string? Letter(string root) =>
        root.Length >= 2 && char.IsAsciiLetter(root[0]) && root[1] == ':' ? char.ToUpperInvariant(root[0]).ToString() : null;

    private static double Gb(long bytes) => Math.Round(bytes / 1024d / 1024d / 1024d, 1);
}
