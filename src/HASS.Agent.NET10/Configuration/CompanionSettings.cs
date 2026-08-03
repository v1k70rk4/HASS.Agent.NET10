using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;
using HASS.Agent.Companion.Runtime;
using HASS.Agent.Companion.Security;
using HASS.Agent.Companion.SystemCommands;
using HASS.Agent.Companion.SystemStatus;

namespace HASS.Agent.Companion.Configuration;

internal sealed class CompanionSettings
{
    public string DeviceName { get; set; } = Environment.MachineName;

    public string SerialNumber { get; set; } = string.Empty;

    public string BindHost { get; set; } = "0.0.0.0";

    public int Port { get; set; } = 5115;

    public string ApiKey { get; set; } = string.Empty;

    public bool ShowStartupNotification { get; set; }

    public bool DangerZoneEnabled { get; set; }

    public bool BetaUpdatesEnabled { get; set; }

    /// <summary>Version of the previous run — used to detect a completed update on startup.</summary>
    public string LastRunVersion { get; set; } = string.Empty;

    public bool AutoStartOnLogin { get; set; }

    public string Language { get; set; } = DetectSystemLanguage();

    public string HaLanguage { get; set; } = "en";

    private static string DetectSystemLanguage()
    {
        var culture = CultureInfo.CurrentUICulture;
        // Walk up the culture chain (e.g. "hu-HU" → "hu" → invariant).
        while (culture != CultureInfo.InvariantCulture)
        {
            if (culture.TwoLetterISOLanguageName == "hu") return "hu";
            culture = culture.Parent;
        }
        return "en";
    }

    public string Manufacturer { get; set; } = "v1k70rk4";

    public string Model { get; set; } = AppIdentity.DisplayName;

    public string SoftwareVersion { get; set; } = GetSoftwareVersion();

    /// <summary>
    /// Prefers the informational version so pre-release suffixes (10.3.0-beta.1)
    /// survive — the assembly version would truncate them to 10.3.0.
    /// </summary>
    private static string GetSoftwareVersion()
    {
        var assembly = typeof(CompanionSettings).Assembly;
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var buildIndex = info.IndexOf('+');
            return buildIndex >= 0 ? info[..buildIndex] : info;
        }

        return assembly.GetName().Version?.ToString(3) ?? "10.3.0";
    }

    public bool MqttEnabled { get; set; }

    public string MqttHost { get; set; } = "homeassistant.local";

    public int MqttPort { get; set; } = 1883;

    public string MqttUsername { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string MqttPassword { get; set; } = string.Empty;

    public string MqttPasswordProtected { get; set; } = string.Empty;

    public bool MqttUseTls { get; set; }

    public bool MqttRetainDiscovery { get; set; } = true;

    public bool HaApiEnabled { get; set; }

    public string HaApiUrl { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string HaApiToken { get; set; } = string.Empty;

    public string HaApiTokenProtected { get; set; } = string.Empty;

    public bool MqttNotificationsEnabled { get; set; } = true;

    public bool MqttMediaPlayerEnabled { get; set; } = true;

    public bool MqttButtonsEnabled { get; set; } = true;

    public bool MqttSystemSensorsEnabled { get; set; } = true;

    public bool MqttServiceSystemSensorsEnabled { get; set; } = true;

    public int FastSensorIntervalSeconds { get; set; } = 10;

    public int NormalSensorIntervalSeconds { get; set; } = 60;

    public int HourlySensorIntervalSeconds { get; set; } = 3600;

    public List<string> TrayAppCommands { get; set; } = SystemCommandCatalog.DefaultTrayAppCommands.ToList();

    public List<string> ServiceCommands { get; set; } = SystemCommandCatalog.DefaultServiceCommands.ToList();

    public List<CustomSensorDefinition> CustomSensors { get; set; } = [];

    public List<CustomCommandDefinition> CustomCommands { get; set; } = [];

    public List<BuiltInSensorSetting> BuiltInSensors { get; set; } = [];

    [JsonIgnore]
    public string ListenUrl => $"http://{BindHost}:{Port}";

    [JsonIgnore]
    public string MqttClientId => GetMqttClientId("app");

    public string GetMqttClientId(string role)
    {
        var roleSuffix = string.IsNullOrWhiteSpace(role) ? "app" : role.Trim().ToLowerInvariant();
        return $"{AppIdentity.MqttClientIdPrefix}-{SerialNumber}-{roleSuffix}";
    }

    public string GetMqttPassword()
    {
        return ProtectedSecretStore.Unprotect(MqttPasswordProtected, SerialNumber);
    }

    public void SetMqttPassword(string password)
    {
        MqttPasswordProtected = ProtectedSecretStore.Protect(password, SerialNumber);
        MqttPassword = string.Empty;
    }

    public bool MigratePlainTextPassword()
    {
        if (string.IsNullOrEmpty(MqttPassword))
        {
            return false;
        }

        SetMqttPassword(MqttPassword);
        return true;
    }

    public string GetHaApiToken()
    {
        return ProtectedSecretStore.Unprotect(HaApiTokenProtected, SerialNumber);
    }

    public void SetHaApiToken(string token)
    {
        HaApiTokenProtected = ProtectedSecretStore.Protect(token, SerialNumber);
        HaApiToken = string.Empty;
    }

    public bool MigrateHaApiPlainTextToken()
    {
        if (string.IsNullOrEmpty(HaApiToken))
        {
            return false;
        }

        SetHaApiToken(HaApiToken);
        return true;
    }

    public bool MigrateProtectedPasswordToMachineScope()
    {
        if (string.IsNullOrWhiteSpace(MqttPasswordProtected) ||
            ProtectedSecretStore.IsMachineProtected(MqttPasswordProtected))
        {
            return false;
        }

        var password = GetMqttPassword();
        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        SetMqttPassword(password);
        return true;
    }

    public void Normalize()
    {
        DeviceName = NormalizeText(DeviceName, Environment.MachineName);
        BindHost = NormalizeText(BindHost, "0.0.0.0");
        if (BindHost is "127.0.0.1" or "localhost")
        {
            BindHost = "0.0.0.0";
        }
        Manufacturer = NormalizeText(Manufacturer, "v1k70rk4");
        Model = NormalizeText(Model, AppIdentity.DisplayName);
        SoftwareVersion = GetSoftwareVersion();
        MqttHost = NormalizeText(MqttHost, "homeassistant.local");
        MqttUsername = MqttUsername.Trim();
        HaApiUrl = NormalizeUrl(HaApiUrl);
        if (Language is not "hu" and not "en") Language = "en";
        if (HaLanguage is not "hu" and not "en") HaLanguage = "en";

        if (string.IsNullOrWhiteSpace(SerialNumber))
        {
            SerialNumber = Guid.NewGuid().ToString("N");
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            ApiKey = Guid.NewGuid().ToString("N");
        }

        if (Port is < 1 or > 65535)
        {
            Port = 5115;
        }

        if (MqttPort is < 1 or > 65535)
        {
            MqttPort = MqttUseTls ? 8883 : 1883;
        }

        FastSensorIntervalSeconds = NormalizeInterval(FastSensorIntervalSeconds, 10, max: 3600);
        NormalSensorIntervalSeconds = NormalizeInterval(NormalSensorIntervalSeconds, 60, max: 86400);
        HourlySensorIntervalSeconds = NormalizeInterval(HourlySensorIntervalSeconds, 3600, max: 86400);

        TrayAppCommands = NormalizeCommandList(TrayAppCommands, SystemCommandCatalog.DefaultTrayAppCommands);
        ServiceCommands = NormalizeCommandList(ServiceCommands, SystemCommandCatalog.DefaultServiceCommands)
            .Where(command => SystemCommandCatalog.ServiceCapableCommands.Contains(command))
            .ToList();
        CustomSensors = NormalizeCustomSensors(CustomSensors);
        CustomCommands = NormalizeCustomCommands(CustomCommands);
        BuiltInSensors = NormalizeBuiltInSensors(BuiltInSensors);
        MqttButtonsEnabled = TrayAppCommands.Count > 0 || CustomCommands.Any(command => command.Enabled);
    }

    private static string NormalizeText(string value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string NormalizeUrl(string value)
    {
        var url = (value ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(url))
        {
            return string.Empty;
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "http://" + url;
        }

        return url;
    }

    private static int NormalizeInterval(int value, int fallback, int max)
    {
        return value is < 10 || value > max ? fallback : value;
    }

    private static List<string> NormalizeCommandList(List<string>? commands, IReadOnlyList<string> defaults)
    {
        var source = commands ?? defaults;
        return source
            .Select(command => command.Trim().ToLowerInvariant())
            .Where(command => SystemCommandCatalog.AllCommandNames.Contains(command, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<CustomSensorDefinition> NormalizeCustomSensors(List<CustomSensorDefinition>? sensors)
    {
        if (sensors is null)
        {
            return [];
        }

        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<CustomSensorDefinition>();

        foreach (var sensor in sensors)
        {
            sensor.Id = NormalizeCustomSensorId(sensor.Id, usedIds);
            sensor.Type = CustomSensorTypes.Normalize(sensor.Type);
            sensor.Name = NormalizeText(sensor.Name, sensor.Type);
            sensor.Parameter = sensor.Parameter.Trim();
            sensor.PollingProfile = SensorPollingProfiles.NormalizeKey(sensor.PollingProfile, SensorPollingProfile.Normal);

            if (string.IsNullOrWhiteSpace(sensor.Parameter))
            {
                continue;
            }

            normalized.Add(sensor);
        }

        return normalized;
    }

    private static List<CustomCommandDefinition> NormalizeCustomCommands(List<CustomCommandDefinition>? commands)
    {
        if (commands is null)
        {
            return [];
        }

        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<CustomCommandDefinition>();

        foreach (var command in commands)
        {
            command.Id = NormalizeCustomSensorId(command.Id, usedIds);
            command.Type = CustomCommandTypes.Normalize(command.Type);
            command.Name = NormalizeText(command.Name, command.Type);
            command.Command = (command.Command ?? string.Empty).Trim();
            command.Arguments = (command.Arguments ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(command.Command))
            {
                continue;
            }

            normalized.Add(command);
        }

        return normalized;
    }

    private static string NormalizeCustomSensorId(string value, HashSet<string> usedIds)
    {
        var id = new string((value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Where(character => char.IsLetterOrDigit(character) || character == '_')
            .ToArray());

        if (string.IsNullOrWhiteSpace(id) || usedIds.Contains(id))
        {
            id = Guid.NewGuid().ToString("N");
        }

        usedIds.Add(id);
        return id;
    }

    private static List<BuiltInSensorSetting> NormalizeBuiltInSensors(List<BuiltInSensorSetting>? sensors)
    {
        var byKey = sensors?
            .Where(sensor => BuiltInSensorCatalog.AllKeys.Contains(sensor.Key))
            .GroupBy(sensor => sensor.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase) ??
            new Dictionary<string, BuiltInSensorSetting>(StringComparer.OrdinalIgnoreCase);

        return BuiltInSensorCatalog.Sensors
            .Select(definition =>
            {
                if (!byKey.TryGetValue(definition.Key, out var setting))
                {
                    setting = new BuiltInSensorSetting
                    {
                        Key = definition.Key,
                        Service = definition.SupportsService,
                        TrayApp = definition.SupportsTrayApp
                    };
                }

                setting.Key = definition.Key;
                setting.Service = definition.SupportsService && setting.Service;
                setting.TrayApp = definition.SupportsTrayApp && setting.TrayApp;
                return setting;
            })
            .ToList();
    }

    public bool IsTrayAppCommandEnabled(string command)
    {
        return TrayAppCommands.Contains(command, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsServiceCommandEnabled(string command)
    {
        return ServiceCommands.Contains(command, StringComparer.OrdinalIgnoreCase);
    }
}
