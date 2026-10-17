using System.Management;
using System.Text.Json.Serialization;
using HASS.Agent.Companion.Logging;

namespace HASS.Agent.Companion.SystemStatus;

/// <summary>
/// A drive's health data as Windows keeps it (the Storage Management API, what
/// Get-StorageReliabilityCounter shows). A field the drive does not report is null: an NVMe
/// drive gives the temperature and its highest value but no power-on hours, a SATA drive
/// the temperature and the hours but no highest value, a USB enclosure nothing.
/// </summary>
internal sealed record DriveHealthInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("bus")] string Bus,
    [property: JsonPropertyName("media")] string Media,
    [property: JsonPropertyName("temperature")] int? Temperature,
    [property: JsonPropertyName("temperature_max")] int? TemperatureMax,
    [property: JsonPropertyName("power_on_hours")] long? PowerOnHours,
    [property: JsonPropertyName("wear")] int? Wear,
    // The drive Windows runs from.
    [property: JsonPropertyName("system")] bool System);

/// <summary>What Windows knows about a physical disk, before it is matched to its counters.</summary>
internal sealed record PhysicalDiskRow(string DeviceId, string Name, int BusType, int MediaType);

/// <summary>One drive's reliability counters; a zero from Windows means the drive does not report it.</summary>
internal sealed record ReliabilityRow(string DeviceId, long Temperature, long TemperatureMax, long PowerOnHours, long Wear);

internal static class DriveHealth
{
    private const string StorageNamespace = @"root\Microsoft\Windows\Storage";

    /// <summary>
    /// Every physical disk with what it reports, the system drive first. Reading the counters
    /// needs administrator rights (the service has them); without them, or on a PC whose
    /// storage provider is missing, the result is empty.
    /// </summary>
    public static IReadOnlyList<DriveHealthInfo> Read(FileLog log)
    {
        try
        {
            var disks = Query("SELECT DeviceId, FriendlyName, BusType, MediaType FROM MSFT_PhysicalDisk")
                .Select(disk => new PhysicalDiskRow(
                    Text(disk["DeviceId"]),
                    Text(disk["FriendlyName"]),
                    (int)Number(disk["BusType"]),
                    (int)Number(disk["MediaType"])))
                .ToList();

            var counters = Query("SELECT DeviceId, Temperature, TemperatureMax, PowerOnHours, Wear FROM MSFT_StorageReliabilityCounter")
                .Select(counter => new ReliabilityRow(
                    Text(counter["DeviceId"]),
                    Number(counter["Temperature"]),
                    Number(counter["TemperatureMax"]),
                    Number(counter["PowerOnHours"]),
                    Number(counter["Wear"])))
                .ToList();

            return Compose(disks, counters, ReadSystemDiskId());
        }
        catch (Exception ex)
        {
            log.Debug($"Drive health not available: {ex.Message}");
            return [];
        }
    }

    /// <summary>Pure: joins the disks to their counters by DeviceId; a zero counter is "not reported".</summary>
    internal static IReadOnlyList<DriveHealthInfo> Compose(
        IReadOnlyList<PhysicalDiskRow> disks,
        IReadOnlyList<ReliabilityRow> counters,
        string? systemDiskId)
    {
        var byDisk = counters.ToDictionary(counter => counter.DeviceId, StringComparer.OrdinalIgnoreCase);
        return disks
            .Select(disk =>
            {
                var counter = byDisk.GetValueOrDefault(disk.DeviceId);
                return new DriveHealthInfo(
                    disk.Name,
                    BusName(disk.BusType),
                    MediaName(disk.MediaType),
                    Reported(counter?.Temperature),
                    Reported(counter?.TemperatureMax),
                    counter is { PowerOnHours: > 0 } ? counter.PowerOnHours : null,
                    Reported(counter?.Wear, allowZero: counter?.Temperature > 0),
                    string.Equals(disk.DeviceId, systemDiskId, StringComparison.OrdinalIgnoreCase));
            })
            .OrderByDescending(drive => drive.System)
            .ThenBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // Wear is 0 on a healthy drive, so it counts as reported when the same counter row gave
    // a temperature; a row that gave nothing at all reports no wear either.
    private static int? Reported(long? value, bool allowZero = false) =>
        value is null ? null : value > 0 || allowZero ? (int)Math.Min(value.Value, int.MaxValue) : null;

    // The disk behind the drive letter Windows runs from (C: on nearly every PC).
    private static string? ReadSystemDiskId()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory);
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }

        var letter = char.ToUpperInvariant(root[0]);
        foreach (var partition in Query("SELECT DriveLetter, DiskNumber FROM MSFT_Partition"))
        {
            // DriveLetter is a char16: it comes out as a char, or as its code.
            var value = partition["DriveLetter"];
            var partitionLetter = value switch
            {
                char c => c,
                ushort code => (char)code,
                string text when text.Length > 0 => text[0],
                _ => '\0'
            };
            if (char.ToUpperInvariant(partitionLetter) == letter)
            {
                return Text(partition["DiskNumber"]);
            }
        }

        return null;
    }

    private static IEnumerable<ManagementBaseObject> Query(string query)
    {
        using var searcher = new ManagementObjectSearcher(new ManagementScope(StorageNamespace), new ObjectQuery(query));
        using var results = searcher.Get();
        foreach (var item in results)
        {
            yield return item;
        }
    }

    private static string Text(object? value) => value?.ToString()?.Trim() ?? string.Empty;

    private static long Number(object? value) => value is null ? 0 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);

    // MSFT_PhysicalDisk.BusType and MediaType, as Windows numbers them.
    internal static string BusName(int busType) => busType switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "IEEE 1394",
        5 => "SSA",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        14 => "Virtual",
        15 => "File-backed virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        18 => "SCM",
        _ => "Unknown"
    };

    internal static string MediaName(int mediaType) => mediaType switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => "Unspecified"
    };
}
