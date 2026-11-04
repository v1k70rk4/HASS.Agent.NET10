using System.Diagnostics;
using System.ServiceProcess;
using System.Text.Json;
using HASS.Agent.Companion.Runtime;
using HASS.Agent.Companion.SystemCommands;
using HASS.Agent.Companion.SystemStatus;
using Microsoft.Win32;

namespace HASS.Agent.Companion.Configuration;

/// <summary>Where the classic HASS.Agent (LAB02 or the community one) keeps its settings on this PC.</summary>
internal sealed record ClassicInstall(string? ClientConfigDirectory, string? ServiceConfigDirectory)
{
    public bool Exists => ClientConfigDirectory is not null || ServiceConfigDirectory is not null;
}

/// <summary>The classic settings files, as text; null for a file that is not there or cannot be read.</summary>
internal sealed record ClassicFiles(
    string? AppSettings,
    string? Sensors,
    string? Commands,
    string? ServiceMqttSettings,
    string? ServiceSensors,
    string? ServiceCommands);

/// <summary>What an import took over, and what it had no counterpart for.</summary>
internal sealed class ClassicImportResult
{
    public bool Connection { get; set; }

    public List<string> Sensors { get; } = [];

    public List<string> Commands { get; } = [];

    /// <summary>"Name (ClassicType)" of each sensor or command that did not come over.</summary>
    public List<string> Skipped { get; } = [];

    public bool WebSocketDropped { get; set; }

    public bool CertificatesDropped { get; set; }
}

/// <summary>
/// Takes over the settings of the classic HASS.Agent: the connection, the device name, and the
/// sensors and commands this app has a counterpart for. The classic install is only read.
/// </summary>
internal static class ClassicImport
{
    // The community client's installer (Inno Setup, per user); its uninstall entry knows a
    // folder the user picked instead of the default one.
    private const string ClientUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{7BBED458-609B-4D13-AD9E-4FF219DF8644}_is1";
    private const string ServiceRegistryKey = @"SOFTWARE\HASSAgent\SatelliteService";
    private const string ClassicServiceName = "hass.agent.svc";
    private const string ClassicProcessName = "HASS.Agent";

    public static ClassicInstall Find()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        var client = FirstConfigDirectory(
            "appsettings.json",
            ReadRegistry(Registry.CurrentUser, ClientUninstallKey, "InstallLocation"),
            Under(localAppData, "HASS.Agent", "Client"),
            Under(appData, "LAB02 Research", "HASS.Agent"),
            Under(localAppData, "LAB02 Research", "HASS.Agent"));

        // The service's folder is written by administrators only (Program Files, or a folder an
        // administrator's install put in HKLM): what it holds may run in this app's service,
        // once an administrator approves it there, as with anything else for the service.
        var service = FirstConfigDirectory(
            "servicemqttsettings.json",
            ReadRegistry(Registry.LocalMachine, ServiceRegistryKey, "InstallPath"),
            Under(programFiles, "HASS.Agent", "Service"),
            Under(programFilesX86, "HASS.Agent", "Service"),
            Under(programFiles, "LAB02 Research", "HASS.Agent Satellite Service"),
            Under(programFilesX86, "LAB02 Research", "HASS.Agent Satellite Service"));

        return new ClassicInstall(client, service);
    }

    public static ClassicFiles Read(ClassicInstall install)
    {
        return new ClassicFiles(
            ReadFile(install.ClientConfigDirectory, "appsettings.json"),
            ReadFile(install.ClientConfigDirectory, "sensors.json"),
            ReadFile(install.ClientConfigDirectory, "commands.json"),
            ReadFile(install.ServiceConfigDirectory, "servicemqttsettings.json"),
            ReadFile(install.ServiceConfigDirectory, "sensors.json"),
            ReadFile(install.ServiceConfigDirectory, "commands.json"));
    }

    /// <summary>Whether the classic client or its service still runs on this PC.</summary>
    public static bool IsRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName(ClassicProcessName);
            var running = processes.Length > 0;
            foreach (var process in processes)
            {
                process.Dispose();
            }

            if (running)
            {
                return true;
            }
        }
        catch (Exception)
        {
            // Not knowing is not running.
        }

        try
        {
            var services = ServiceController.GetServices();
            try
            {
                var classic = services.FirstOrDefault(service => service.ServiceName.Equals(ClassicServiceName, StringComparison.OrdinalIgnoreCase));
                return classic is not null && classic.Status != ServiceControllerStatus.Stopped;
            }
            finally
            {
                foreach (var service in services)
                {
                    service.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Puts the classic settings into <paramref name="target"/>: the connection and the device
    /// name replace this app's, the sensors and commands are added to its own. An item imported
    /// before (same id) is not added twice.
    /// </summary>
    public static ClassicImportResult Apply(ClassicFiles files, CompanionSettings target)
    {
        var result = new ClassicImportResult();
        var app = Parse(files.AppSettings);
        var serviceMqtt = Parse(files.ServiceMqttSettings);

        if (app is { } settings)
        {
            ApplyClientSettings(settings, target, result);
        }
        else if (serviceMqtt is { } mqtt)
        {
            ApplyMqtt(mqtt, target, result);
        }

        ApplySensors(files.Sensors, service: false, target, result);
        ApplySensors(files.ServiceSensors, service: true, target, result);
        ApplyCommands(files.Commands, service: false, target, result);
        ApplyCommands(files.ServiceCommands, service: true, target, result);

        target.Normalize();
        return result;
    }

    private static void ApplyClientSettings(JsonElement settings, CompanionSettings target, ClassicImportResult result)
    {
        var deviceName = GetString(settings, "DeviceName");
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            target.DeviceName = deviceName.Trim();
        }

        var mqttEnabled = GetBool(settings, "MqttEnabled") ?? true;
        if (mqttEnabled)
        {
            ApplyMqtt(settings, target, result);
        }

        // The classic client used Home Assistant's API for its notifications' actions and the
        // Quick Actions. Here it is a whole connection: kept ready, switched on only for a PC
        // that had no MQTT.
        var hassUri = GetString(settings, "HassUri");
        var hassToken = GetString(settings, "HassToken");
        if (!string.IsNullOrWhiteSpace(hassUri) && !string.IsNullOrWhiteSpace(hassToken))
        {
            target.HaApiUrl = hassUri.Trim();
            target.SetHaApiToken(hassToken.Trim());
            if (!mqttEnabled)
            {
                target.HaApiEnabled = true;
            }

            result.Connection = true;
        }

        if (!string.IsNullOrWhiteSpace(GetString(settings, "HassClientCertificate")))
        {
            result.CertificatesDropped = true;
        }
    }

    private static void ApplyMqtt(JsonElement settings, CompanionSettings target, ClassicImportResult result)
    {
        var address = GetString(settings, "MqttAddress");
        if (string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        target.MqttEnabled = true;
        target.MqttHost = address.Trim();
        if (GetInt(settings, "MqttPort") is > 0 and <= 65535 and var port)
        {
            target.MqttPort = port;
        }

        target.MqttUseTls = GetBool(settings, "MqttUseTls") ?? false;
        target.MqttUsername = GetString(settings, "MqttUsername")?.Trim() ?? string.Empty;
        target.SetMqttPassword(GetString(settings, "MqttPassword") ?? string.Empty);
        if (GetBool(settings, "MqttUseRetainFlag") is { } retain)
        {
            target.MqttRetainDiscovery = retain;
        }

        result.Connection = true;
        result.WebSocketDropped |= GetBool(settings, "MqttUseWebSocket") == true;
        result.CertificatesDropped |= !string.IsNullOrWhiteSpace(GetString(settings, "MqttRootCertificate"))
            || !string.IsNullOrWhiteSpace(GetString(settings, "MqttClientCertificate"));
    }

    // --- sensors -------------------------------------------------------------------------------

    /// <summary>Classic sensor types that are one or more of this app's built-in sensors.</summary>
    internal static readonly IReadOnlyDictionary<string, string[]> BuiltInSensorsFor = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["CpuLoadSensor"] = ["cpu_usage"],
        ["MemoryUsageSensor"] = ["memory_usage"],
        ["GpuLoadSensor"] = ["gpu_usage"],
        ["ActiveWindowSensor"] = ["active_window"],
        ["LastBootSensor"] = ["boot_time"],
        ["LoggedUserSensor"] = ["logged_in_user"],
        ["LoggedUsersSensor"] = ["logged_in_users"],
        ["SessionStateSensor"] = ["session_state"],
        ["WebcamActiveSensor"] = ["camera_in_use"],
        ["WebcamProcessSensor"] = ["camera_in_use"],
        ["MicrophoneActiveSensor"] = ["microphone_in_use"],
        ["MicrophoneProcessSensor"] = ["microphone_in_use"],
        ["MonitorPowerStateSensor"] = ["monitor_power_state"],
        ["CurrentVolumeSensor"] = ["volume"],
        ["AudioSensors"] = ["audio_output_device", "audio_sessions", "volume", "muted"],
        ["BatterySensors"] = ["battery_level", "power_status", "battery_time_remaining"],
        ["NetworkSensors"] = ["network_address"],
        ["StorageSensors"] = ["drive_space"],
        ["DisplaySensors"] = ["active_display"],
        ["WindowsUpdatesSensors"] = ["windows_update_pending"],
        ["LastActiveSensor"] = ["idle_time_seconds"],
    };

    private static void ApplySensors(string? json, bool service, CompanionSettings target, ClassicImportResult result)
    {
        foreach (var item in ParseArray(json))
        {
            var type = GetString(item, "Type") ?? string.Empty;
            var name = DisplayName(item, type);

            if (BuiltInSensorsFor.TryGetValue(type, out var keys))
            {
                foreach (var key in keys)
                {
                    EnableBuiltInSensor(target, key, service);
                }

                result.Sensors.Add(name);
                continue;
            }

            var customType = type switch
            {
                "ProcessActiveSensor" => CustomSensorTypes.ProcessRunning,
                "ServiceStateSensor" => CustomSensorTypes.ServiceStatus,
                "PowershellSensor" => CustomSensorTypes.CommandPowerShell,
                _ => null
            };
            var parameter = GetString(item, "Query")?.Trim();
            if (customType is null || string.IsNullOrWhiteSpace(parameter))
            {
                result.Skipped.Add($"{name} ({type})");
                continue;
            }

            var id = ClassicId(item);
            var existing = target.CustomSensors.FirstOrDefault(sensor => sensor.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                // The same item in the client and the service: one sensor for both.
                existing.Service |= service;
                existing.TrayApp |= !service;
                result.Sensors.Add(name);
                continue;
            }

            target.CustomSensors.Add(new CustomSensorDefinition
            {
                Id = id,
                Type = customType,
                Name = name,
                Parameter = parameter,
                PollingProfile = SensorPollingProfiles.ToKey(ProfileFor(GetInt(item, "UpdateInterval"))),
                Enabled = true,
                Service = service,
                TrayApp = !service,
            });
            result.Sensors.Add(name);
        }
    }

    private static void EnableBuiltInSensor(CompanionSettings target, string key, bool service)
    {
        var definition = BuiltInSensorCatalog.Find(key);
        if (definition is null)
        {
            return;
        }

        var setting = target.BuiltInSensors.FirstOrDefault(sensor => sensor.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (setting is null)
        {
            setting = new BuiltInSensorSetting { Key = definition.Key };
            target.BuiltInSensors.Add(setting);
        }

        // A sensor only the tray app can read comes from the tray app, wherever it was before.
        if (service && definition.SupportsService)
        {
            setting.Service = true;
        }
        else if (definition.SupportsTrayApp)
        {
            setting.TrayApp = true;
        }
        else
        {
            setting.Service = true;
        }
    }

    internal static SensorPollingProfile ProfileFor(int? updateIntervalSeconds) => updateIntervalSeconds switch
    {
        null => SensorPollingProfile.Normal,
        <= 20 => SensorPollingProfile.Fast,
        <= 900 => SensorPollingProfile.Normal,
        _ => SensorPollingProfile.Hourly
    };

    // --- commands ------------------------------------------------------------------------------

    /// <summary>Classic command types that are one of this app's system commands.</summary>
    internal static readonly IReadOnlyDictionary<string, string> SystemCommandFor = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["LockCommand"] = "lock",
        ["SleepCommand"] = "sleep",
        ["WinformsSleepCommand"] = "sleep",
        ["HibernateCommand"] = "hibernate",
        ["LogOffCommand"] = "logoff",
        ["ShutdownCommand"] = "shutdown",
        ["RestartCommand"] = "restart",
        ["MonitorSleepCommand"] = "monitor_off",
        ["MediaVolumeUpCommand"] = "volume_up",
        ["MediaVolumeDownCommand"] = "volume_down",
        ["MediaMuteCommand"] = "toggle_mute",
    };

    /// <summary>Classic media commands that are a key press here.</summary>
    private static readonly IReadOnlyDictionary<string, string> MediaKeyFor = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["MediaPlayPauseCommand"] = "play_pause",
        ["MediaNextCommand"] = "next_track",
        ["MediaPreviousCommand"] = "prev_track",
    };

    private static void ApplyCommands(string? json, bool service, CompanionSettings target, ClassicImportResult result)
    {
        foreach (var item in ParseArray(json))
        {
            var type = GetString(item, "Type") ?? string.Empty;
            var name = DisplayName(item, type);

            if (SystemCommandFor.TryGetValue(type, out var systemCommand))
            {
                EnableSystemCommand(target, systemCommand, service);
                result.Commands.Add(name);
                continue;
            }

            var converted = ConvertCommand(item, type);
            if (converted is null)
            {
                result.Skipped.Add($"{name} ({type})");
                continue;
            }

            var id = ClassicId(item);
            var existing = target.CustomCommands.FirstOrDefault(command => command.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.Service |= service && !existing.NeedsUserSession;
                existing.TrayApp |= !service || existing.NeedsUserSession;
                result.Commands.Add(name);
                continue;
            }

            converted.Id = id;
            converted.Name = name;
            converted.Enabled = true;
            // Key presses, addresses and windows need the user's desktop: the tray app runs them,
            // even if the classic service had them.
            converted.Service = service && !converted.NeedsUserSession;
            converted.TrayApp = !service || converted.NeedsUserSession;
            target.CustomCommands.Add(converted);
            result.Commands.Add(name);
        }
    }

    private static void EnableSystemCommand(CompanionSettings target, string command, bool service)
    {
        var definition = SystemCommandCatalog.Commands.FirstOrDefault(item => item.Name.Equals(command, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
        {
            return;
        }

        var list = service && definition.SupportsService ? target.ServiceCommands : target.TrayAppCommands;
        if (!list.Contains(definition.Name, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(definition.Name);
        }
    }

    internal static CustomCommandDefinition? ConvertCommand(JsonElement item, string type)
    {
        var command = GetString(item, "Command")?.Trim() ?? string.Empty;

        if (MediaKeyFor.TryGetValue(type, out var mediaKey))
        {
            return new CustomCommandDefinition { Type = CustomCommandTypes.Key, Command = mediaKey };
        }

        switch (type)
        {
            case "CustomCommand" when command.Length > 0:
                return ConvertCommandLine(command);

            case "PowershellCommand" when command.Length > 0:
                return new CustomCommandDefinition { Type = CustomCommandTypes.PowerShell, Command = command };

            case "KeyCommand":
                return GetInt(item, "KeyCode") is { } keyCode && KeyName(keyCode) is { } key
                    ? new CustomCommandDefinition { Type = CustomCommandTypes.Key, Command = key }
                    : null;

            case "MultipleKeysCommand":
                return ConvertSendKeys(GetStringArray(item, "Keys")) is { } keys
                    ? new CustomCommandDefinition { Type = CustomCommandTypes.Key, Command = keys }
                    : null;

            case "LaunchUrlCommand":
                var url = UrlOf(command);
                return url is not null && SystemCommandService.IsOpenableAddress(url)
                    ? new CustomCommandDefinition { Type = CustomCommandTypes.Url, Command = url }
                    : null;

            case "WebViewCommand":
                return ConvertWebView(command);

            default:
                return null;
        }
    }

    // The classic client ran a custom command headless through cmd /C. A plain program with
    // its arguments runs the same as a Program here; what needs cmd (its own commands, pipes,
    // redirection, chaining, variables) keeps going through it.
    private static readonly HashSet<string> CmdBuiltIns = new(StringComparer.OrdinalIgnoreCase)
    {
        "start", "echo", "del", "erase", "copy", "move", "ren", "rename", "md", "mkdir", "rd", "rmdir",
        "cd", "chdir", "dir", "type", "set", "call", "if", "for", "cls", "pushd", "popd", "exit", "title", "mklink"
    };

    internal static CustomCommandDefinition ConvertCommandLine(string commandLine)
    {
        var (fileName, _) = SystemCommandService.SplitProcessCommand(commandLine);
        var needsCmd = commandLine.IndexOfAny(['&', '|', '<', '>', '%', '^']) >= 0 || CmdBuiltIns.Contains(fileName);

        return needsCmd
            ? new CustomCommandDefinition { Type = CustomCommandTypes.Process, Command = SystemTools.InSystem32("cmd.exe"), Arguments = $"/C {commandLine}" }
            : new CustomCommandDefinition { Type = CustomCommandTypes.Process, Command = commandLine };
    }

    private static CustomCommandDefinition? ConvertWebView(string command)
    {
        var info = command.StartsWith('{') ? Parse(command) : null;
        var url = info is { } json ? GetString(json, "Url")?.Trim() : command;
        if (string.IsNullOrWhiteSpace(url) || !SystemCommandService.IsOpenableAddress(url))
        {
            return null;
        }

        var size = string.Empty;
        if (info is { } window && GetInt(window, "Width") is > 0 and var width && GetInt(window, "Height") is > 0 and var height)
        {
            size = $"{width}x{height}";
        }

        return new CustomCommandDefinition { Type = CustomCommandTypes.WebView, Command = url, Arguments = size };
    }

    // The community client keeps the address in a small JSON object ({"Url": ..., "Incognito": ...});
    // the LAB02 one had the address alone.
    private static string? UrlOf(string command)
    {
        if (!command.StartsWith('{'))
        {
            return command.Length > 0 ? command : null;
        }

        return Parse(command) is { } info ? GetString(info, "Url")?.Trim() : null;
    }

    // --- keys ----------------------------------------------------------------------------------

    /// <summary>This app's name for a Windows virtual-key code, or null when it has none.</summary>
    internal static string? KeyName(int virtualKey)
    {
        switch (virtualKey)
        {
            case >= 0x41 and <= 0x5A:
                return ((char)('a' + virtualKey - 0x41)).ToString();
            case >= 0x30 and <= 0x39:
                return ((char)virtualKey).ToString();
            case >= 0x70 and <= 0x87:
                return $"f{virtualKey - 0x70 + 1}";
            case >= 0x60 and <= 0x69:
                return $"num{virtualKey - 0x60}";
        }

        return virtualKey switch
        {
            0x11 or 0xA2 or 0xA3 => "ctrl",
            0x10 or 0xA0 or 0xA1 => "shift",
            0x12 or 0xA4 or 0xA5 => "alt",
            0x5B or 0x5C => "win",
            0x0D => "enter",
            0x1B => "esc",
            0x09 => "tab",
            0x20 => "space",
            0x08 => "backspace",
            0x2E => "delete",
            0x2D => "insert",
            0x24 => "home",
            0x23 => "end",
            0x21 => "pageup",
            0x22 => "pagedown",
            0x26 => "up",
            0x28 => "down",
            0x25 => "left",
            0x27 => "right",
            0x2C => "printscreen",
            0x13 => "pause",
            0x14 => "capslock",
            0x90 => "numlock",
            0x91 => "scrolllock",
            0x5D => "menu",
            0xBB => "plus",
            0xBD => "minus",
            0xBC => "comma",
            0xBE => "period",
            0xB3 => "play_pause",
            0xB0 => "next_track",
            0xB1 => "prev_track",
            0xB2 => "media_stop",
            0xAF => "volume_up",
            0xAE => "volume_down",
            0xAD => "volume_mute",
            0xA6 => "browser_back",
            0xA7 => "browser_forward",
            0xA8 => "browser_refresh",
            0xAC => "browser_home",
            _ => null
        };
    }

    private static readonly Dictionary<string, string> SendKeysNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ENTER"] = "enter", ["~"] = "enter", ["TAB"] = "tab", ["ESC"] = "esc", ["ESCAPE"] = "esc",
        ["BACKSPACE"] = "backspace", ["BS"] = "backspace", ["BKSP"] = "backspace",
        ["DEL"] = "delete", ["DELETE"] = "delete", ["INS"] = "insert", ["INSERT"] = "insert",
        ["HOME"] = "home", ["END"] = "end", ["PGUP"] = "pageup", ["PGDN"] = "pagedown",
        ["UP"] = "up", ["DOWN"] = "down", ["LEFT"] = "left", ["RIGHT"] = "right",
        ["CAPSLOCK"] = "capslock", ["NUMLOCK"] = "numlock", ["SCROLLLOCK"] = "scrolllock",
        ["PRTSC"] = "printscreen", ["BREAK"] = "pause", ["ADD"] = "plus", ["SUBTRACT"] = "minus",
        ["+"] = "plus", ["^"] = "^", ["%"] = "%", ["{"] = "{", ["}"] = "}", ["("] = "(", [")"] = ")", ["["] = "[", ["]"] = "]",
    };

    /// <summary>
    /// The classic multiple-keys command's list, in WinForms SendKeys notation ("^c", "%{F4}",
    /// "{ENTER}", "abc"), as this app's key text ("ctrl+c alt+f4 enter a b c"). Null when a part
    /// has no counterpart (grouping in brackets, repeat counts, an unknown key).
    /// </summary>
    internal static string? ConvertSendKeys(IReadOnlyList<string> keys)
    {
        var combinations = new List<string>();
        foreach (var entry in keys)
        {
            if (!TryConvertSendKeys(entry, combinations))
            {
                return null;
            }
        }

        return combinations.Count > 0 ? string.Join(' ', combinations) : null;
    }

    private static bool TryConvertSendKeys(string text, List<string> combinations)
    {
        var modifiers = new List<string>();
        var index = 0;
        while (index < text.Length)
        {
            var character = text[index];
            switch (character)
            {
                case '^':
                    modifiers.Add("ctrl");
                    index++;
                    continue;
                case '+':
                    modifiers.Add("shift");
                    index++;
                    continue;
                case '%':
                    modifiers.Add("alt");
                    index++;
                    continue;
                case '(' or ')':
                    // A modifier held over a group: no counterpart.
                    return false;
            }

            string key;
            if (character == '{')
            {
                // "{}}" and "{{}" name the braces themselves; a brace at the very end closes nothing.
                if (index + 2 > text.Length)
                {
                    return false;
                }

                var close = text.IndexOf('}', index + 2);
                if (close < 0)
                {
                    return false;
                }

                var inner = text[(index + 1)..close];
                if (inner.Contains(' '))
                {
                    // A repeat count ("{LEFT 4}").
                    return false;
                }

                if (SendKeysNames.TryGetValue(inner, out var named))
                {
                    key = named;
                }
                else if (inner.Length > 1 && inner[0] is 'F' or 'f' && int.TryParse(inner[1..], out var function) && function is >= 1 and <= 24)
                {
                    key = $"f{function}";
                }
                else if (inner.Length == 1)
                {
                    key = inner;
                }
                else
                {
                    return false;
                }

                index = close + 1;
            }
            else
            {
                key = character.ToString();
                index++;
            }

            // SendKeys typed a capital letter as such; here a letter is a key, so the shift
            // that makes it a capital is said.
            if (key.Length == 1 && char.IsAsciiLetterUpper(key[0]) && !modifiers.Contains("shift"))
            {
                modifiers.Add("shift");
            }

            key = key switch
            {
                " " => "space",
                "," => "comma",
                "+" => "plus",
                "~" => "enter",
                _ => key.ToLowerInvariant()
            };

            modifiers.Add(key);
            combinations.Add(string.Join('+', modifiers));
            modifiers.Clear();
        }

        // Modifiers with nothing after them.
        return modifiers.Count == 0;
    }

    // --- reading -------------------------------------------------------------------------------

    private static string DisplayName(JsonElement item, string type)
    {
        // LAB02: FriendlyName and Name (the entity's id); community: Name and EntityName.
        foreach (var property in new[] { "FriendlyName", "Name", "EntityName" })
        {
            var value = GetString(item, property);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return type;
    }

    private static string ClassicId(JsonElement item)
    {
        return GetString(item, "Id") is { } id && Guid.TryParse(id, out var guid) && guid != Guid.Empty
            ? guid.ToString("N")
            : Guid.NewGuid().ToString("N");
    }

    private static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<JsonElement> ParseArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).Select(item => item.Clone()).ToList()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static int? GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private static IReadOnlyList<string> GetStringArray(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToList()
            : [];

    private static string? FirstConfigDirectory(string marker, params string?[] installDirectories)
    {
        foreach (var directory in installDirectories)
        {
            // A folder that is not there gives an empty special folder (Program Files (x86) on
            // a 32-bit Windows), and the registry may hold anything: only a full path is looked
            // at, never one relative to wherever this app was started from.
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory) || directory.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                continue;
            }

            var config = Path.Combine(directory, "config");
            if (File.Exists(Path.Combine(config, marker)))
            {
                return config;
            }
        }

        return null;
    }

    /// <summary>A path under a special folder, or null when Windows has no such folder.</summary>
    private static string? Under(string specialFolder, params string[] parts) =>
        string.IsNullOrWhiteSpace(specialFolder) ? null : Path.Combine([specialFolder, .. parts]);

    private static string? ReadRegistry(RegistryKey root, string path, string name)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return key?.GetValue(name) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadFile(string? directory, string file)
    {
        if (directory is null)
        {
            return null;
        }

        try
        {
            var path = Path.Combine(directory, file);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
