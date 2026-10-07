using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.SystemCommands;
using HASS.Agent.Companion.SystemStatus;

namespace HASS.Agent.Companion.SystemService;

/// <summary>
/// What the Windows service may run. The service runs as SYSTEM, and the settings it reads
/// live in ProgramData, where every user of the PC can write: whoever could put a command
/// there would have it run as SYSTEM. So the service runs a custom command or a command
/// sensor only when this policy approves it, by its id and a fingerprint of what it runs.
/// The policy sits next to the program, in a folder only administrators can write, and is
/// written by the app run as administrator: by the installer, by the service installation,
/// and after the user approves (UAC) a change in the settings. A command that is not
/// approved, or changed since, stays with the tray app.
/// </summary>
internal sealed class ServicePolicy
{
    public const string FileName = "service-policy.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Custom sensor types that run nothing: everything else needs approval, so a new type
    // that runs something cannot slip past this list by being new.
    private static readonly HashSet<string> HarmlessSensorTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        CustomSensorTypes.ProcessRunning,
        CustomSensorTypes.ServiceStatus,
        CustomSensorTypes.DiskFree,
        CustomSensorTypes.BuiltInAttribute,
        CustomSensorTypes.LibreHardwareMonitor,
    };

    [JsonPropertyName("commands")]
    public List<Entry> Commands { get; init; } = [];

    [JsonPropertyName("sensors")]
    public List<Entry> Sensors { get; init; } = [];

    internal sealed record Entry(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("fingerprint")] string Fingerprint);

    /// <summary>The policy file next to the running program.</summary>
    public static string DefaultPath =>
        Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, FileName);

    public static bool NeedsApproval(CustomCommandDefinition command) =>
        command.Enabled && command.Service && !command.NeedsUserSession;

    public static bool NeedsApproval(CustomSensorDefinition sensor) =>
        sensor.Enabled && sensor.Service && !HarmlessSensorTypes.Contains(sensor.Type);

    public static string Fingerprint(CustomCommandDefinition command) =>
        Hash(command.Type.Trim().ToLowerInvariant(), command.Command, command.Arguments);

    public static string Fingerprint(CustomSensorDefinition sensor) =>
        Hash(sensor.Type.Trim().ToLowerInvariant(), sensor.Parameter);

    /// <summary>The policy that approves what the settings ask the service to run.</summary>
    public static ServicePolicy FromSettings(CompanionSettings settings) => new()
    {
        Commands = settings.CustomCommands.Where(NeedsApproval)
            .Select(command => new Entry(command.Id, Fingerprint(command))).ToList(),
        Sensors = settings.CustomSensors.Where(NeedsApproval)
            .Select(sensor => new Entry(sensor.Id, Fingerprint(sensor))).ToList(),
    };

    /// <summary>The policy in the file; none (nothing approved) when it is missing or unreadable.</summary>
    public static ServicePolicy Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ServicePolicy>(File.ReadAllText(path), JsonOptions) ?? new ServicePolicy()
                : new ServicePolicy();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ServicePolicy();
        }
    }

    public void Save(string path)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    public bool Allows(CustomCommandDefinition command) =>
        Commands.Contains(new Entry(command.Id, Fingerprint(command)));

    public bool Allows(CustomSensorDefinition sensor) =>
        Sensors.Contains(new Entry(sensor.Id, Fingerprint(sensor)));

    /// <summary>Whether everything the other policy approves is approved here too.</summary>
    public bool Covers(ServicePolicy other) =>
        other.Commands.All(Commands.Contains) && other.Sensors.All(Sensors.Contains);

    /// <summary>
    /// In the service: takes what the policy does not approve away from the service (it stays
    /// with the tray app). Returns the names of what was taken away.
    /// </summary>
    public IReadOnlyList<string> Restrict(CompanionSettings settings)
    {
        var restricted = new List<string>();
        foreach (var command in settings.CustomCommands.Where(command => NeedsApproval(command) && !Allows(command)))
        {
            command.Service = false;
            restricted.Add(command.Name);
        }

        foreach (var sensor in settings.CustomSensors.Where(sensor => NeedsApproval(sensor) && !Allows(sensor)))
        {
            sensor.Service = false;
            restricted.Add(sensor.Name);
        }

        return restricted;
    }

    private static string Hash(params string?[] parts)
    {
        var text = string.Join('\0', parts.Select(part => part ?? string.Empty));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
