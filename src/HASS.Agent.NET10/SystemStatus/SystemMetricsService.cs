using System.Collections;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.Win32;
using System.Windows.Forms;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.Media;
using HASS.Agent.Companion.SystemCommands;

namespace HASS.Agent.Companion.SystemStatus;

internal sealed class SystemMetricsService : IDisposable
{
    private static readonly TimeSpan WindowsUpdatePendingCacheDuration = TimeSpan.FromMinutes(30);

    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;
    private const uint EsAwayModeRequired = 0x00000040;
    private const int PowerInformationSystemExecutionState = 16;
    private const int PowerRequestsTimeoutMs = 5000;
    private const int MaxPowerRequests = 20;
    private const string CapabilityConsentStorePath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private const string CapabilityNonPackagedKey = "NonPackaged";
    private const int MaxCapabilityApps = 10;

    // A desktop resuming from sleep or hibernate logs Power-Troubleshooter 1; a Modern Standby
    // machine never really sleeps, and logs Kernel-Power 507 each time it leaves standby.
    private const string WakeEventQuery =
        "*[System[(Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1) or " +
        "(Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=507)]]";
    private const int ModernStandbyExitEventId = 507;

    // The counters behind Task Manager's GPU page. They come from the Windows graphics
    // stack (WDDM), so they are the same on Intel, AMD and NVIDIA.
    private const string GpuEngineCategoryName = "GPU Engine";
    private const string GpuEngineUtilizationCounter = "Utilization Percentage";
    private const string GpuAdapterMemoryCategoryName = "GPU Adapter Memory";
    private const string GpuNeuralEngineType = "neural";
    private const string DisplayAdapterClassPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const int GpuFirstSampleDelayMs = 250;
    private static readonly Regex GpuEngineInstancePattern = new(
        @"luid_(0x[0-9a-f]+_0x[0-9a-f]+)_phys_\d+_eng_\d+_engtype_(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly JsonSerializerOptions AttributeJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _cpuLock = new();
    private readonly FileLog _log;
    private readonly AudioEndpointService? _audioEndpointService;
    private readonly MonitorPowerStateService? _monitorPowerStateService;
    private readonly bool _includeInteractiveMetrics;
    private ulong? _lastIdleTime;
    private ulong? _lastKernelTime;
    private ulong? _lastUserTime;
    private DateTimeOffset? _lastWindowsUpdatePendingReadAt;
    private bool _lastWindowsUpdatePending;
    private SystemMetricsMessage? _lastMessage;
    private IReadOnlyList<NetworkAddressInfo> _lastNetworkAddresses = [];
    private IReadOnlyList<DisplayInfo> _lastDisplays = [];
    private IReadOnlyList<EventLogErrorInfo> _lastRecentErrors = [];
    private ShutdownInfo _lastShutdown = new(string.Empty, string.Empty, null, 0, string.Empty);
    private volatile WakeInfo _lastWake = WakeInfo.None;
    private EventLogWatcher? _wakeWatcher;
    private PerformanceCounterCategory? _gpuEngineCategory;
    private InstanceDataCollection? _lastGpuEngineSamples;
    private IReadOnlyList<GpuAdapterInfo>? _gpuAdapters;
    private GpuInfo? _lastGpu;
    private uint? _lastExecutionState;
    private IReadOnlyList<PowerRequestInfo> _lastPowerRequests = [];
    private IReadOnlyList<string>? _lastCameraApps;
    private IReadOnlyList<string>? _lastMicrophoneApps;

    public SystemMetricsService(
        FileLog log,
        MonitorPowerStateService? monitorPowerStateService,
        AudioEndpointService? audioEndpointService = null,
        bool includeInteractiveMetrics = true)
    {
        // The audio service is owned by the caller (it pushes change events), so
        // it is injected rather than created/disposed here.
        _log = log;
        _audioEndpointService = audioEndpointService;
        _monitorPowerStateService = monitorPowerStateService;
        _includeInteractiveMetrics = includeInteractiveMetrics;
    }

    public SystemMetricsMessage Read(
        IReadOnlyList<CustomSensorDefinition>? customSensors = null,
        bool serviceRole = false,
        IReadOnlySet<SensorPollingProfile>? dueProfiles = null,
        IReadOnlyList<BuiltInSensorSetting>? builtInSensors = null)
    {
        // The reads that cost something (a process start, performance counters, an event log
        // subscription) are skipped for a sensor nobody uses: not enabled for this role, and
        // not the source of a custom attribute sensor. Without settings everything is read.
        bool Wanted(string key) =>
            builtInSensors is null ||
            builtInSensors.Any(sensor =>
                string.Equals(sensor.Key, key, StringComparison.OrdinalIgnoreCase) &&
                (serviceRole ? sensor.Service : sensor.TrayApp)) ||
            (customSensors?.Any(sensor =>
                sensor.Enabled &&
                sensor.IsBuiltInAttribute &&
                (serviceRole ? sensor.Service : sensor.TrayApp) &&
                sensor.Parameter.StartsWith($"{key}.", StringComparison.OrdinalIgnoreCase)) ?? false);

        var profiles = dueProfiles ?? SensorPollingProfiles.All;
        var previous = _lastMessage;
        if (previous is null)
        {
            profiles = SensorPollingProfiles.All;
        }

        var updateFast = profiles.Contains(SensorPollingProfile.Fast);
        var updateNormal = profiles.Contains(SensorPollingProfile.Normal);
        var updateHourly = profiles.Contains(SensorPollingProfile.Hourly);
        var updateStartup = profiles.Contains(SensorPollingProfile.Startup);

        var memory = updateFast ? Safe(ReadMemory, (UsagePercent: previous?.MemoryUsage ?? 0, AvailableMb: previous?.MemoryAvailableMb ?? 0)) : (UsagePercent: previous!.MemoryUsage, AvailableMb: previous.MemoryAvailableMb);
        var activeWindow = updateFast && _includeInteractiveMetrics ? Safe(ReadActiveWindow, (Title: previous?.ActiveWindow ?? string.Empty, ProcessName: previous?.ActiveProcess ?? string.Empty)) : (Title: previous?.ActiveWindow ?? string.Empty, ProcessName: previous?.ActiveProcess ?? string.Empty);
        var sessionLocked = updateFast && _includeInteractiveMetrics ? Safe(ReadSessionLocked, previous?.SessionLocked) : previous?.SessionLocked;

        var drive = updateNormal ? Safe(ReadSystemDrive, (FreePercent: previous?.SystemDriveFreePercent ?? 0, FreeGb: previous?.SystemDriveFreeGb ?? 0)) : (FreePercent: previous!.SystemDriveFreePercent, FreeGb: previous.SystemDriveFreeGb);
        var power = updateNormal ? Safe(ReadPowerStatus, (BatteryLevel: previous?.BatteryLevel, Status: previous?.PowerStatus ?? "unknown", TimeRemainingSeconds: previous?.BatteryTimeRemaining)) : (BatteryLevel: previous!.BatteryLevel, Status: previous.PowerStatus, TimeRemainingSeconds: previous.BatteryTimeRemaining);
        var session = updateNormal ? Safe(ReadSessionStatus, (State: previous?.SessionState ?? "none", User: previous?.LoggedInUser ?? string.Empty)) : (State: previous!.SessionState, User: previous.LoggedInUser);
        var wifi = updateNormal ? Safe(ReadWifiStatus, (Ssid: previous?.WifiSsid ?? string.Empty, Signal: previous?.WifiSignal)) : (Ssid: previous!.WifiSsid, Signal: previous.WifiSignal);
        var sessions = updateNormal ? Safe(ReadSessionCounts, (LoggedInUsers: previous?.LoggedInUsers ?? 0, RdpSessions: previous?.RdpSessions ?? 0)) : (LoggedInUsers: previous!.LoggedInUsers, RdpSessions: previous.RdpSessions);

        if (updateNormal)
        {
            _lastNetworkAddresses = Safe(ReadNetworkAddresses, _lastNetworkAddresses);
            _lastDisplays = _includeInteractiveMetrics ? Safe(ReadDisplaysSafe, _lastDisplays) : [];
        }

        // Service only. Who holds a power request is visible to administrators alone
        // (powercfg /requests refuses otherwise), so the list needs the SYSTEM service.
        // And both roles publish to one state topic, where Home Assistant replaces a
        // sensor's attributes wholesale: a tray app without the list would blank out
        // the service's on every cycle. Leaving the sensor out of its payload is what
        // makes Home Assistant skip it.
        if (!serviceRole || !Wanted("sleep_blocked"))
        {
            _lastExecutionState = null;
            _lastPowerRequests = [];
        }
        else if (updateFast || updateNormal)
        {
            var previousExecutionState = _lastExecutionState;
            _lastExecutionState = Safe(ReadSystemExecutionState, _lastExecutionState);

            // The flags are one cheap call, so they follow the Fast cycle. The holders need a
            // powercfg process: listed only while something is held, at once when the flags
            // change, and otherwise refreshed on the Normal cycle (a video handing over from
            // one browser to another keeps the same flags).
            if (_lastExecutionState is not > 0)
            {
                _lastPowerRequests = [];
            }
            else if (updateNormal || _lastExecutionState != previousExecutionState)
            {
                _lastPowerRequests = Safe(ReadPowerRequests, _lastPowerRequests);
            }
        }

        // Tray app only: Windows keeps the usage record in the signed-in user's hive, and the
        // service's HKCU is SYSTEM's. Null (never read) keeps both sensors out of the payload.
        if (updateFast && _includeInteractiveMetrics)
        {
            _lastCameraApps = Wanted("camera_in_use") ? Safe(() => ReadCapabilityUsers("webcam"), _lastCameraApps) : null;
            _lastMicrophoneApps = Wanted("microphone_in_use") ? Safe(() => ReadCapabilityUsers("microphone"), _lastMicrophoneApps) : null;
        }

        if (!Wanted("gpu_usage"))
        {
            _lastGpu = null;
            _lastGpuEngineSamples = null;
        }
        else if (updateNormal)
        {
            _lastGpu = Safe(ReadGpu, _lastGpu);
        }

        if (updateHourly)
        {
            _lastRecentErrors = Safe(() => ReadRecentEventLogErrors(TimeSpan.FromHours(1)), _lastRecentErrors);
        }

        if (updateStartup)
        {
            _lastShutdown = Safe(ReadLastShutdownInfo, _lastShutdown);

            // The agent keeps running across a sleep, so a Startup read alone would go stale:
            // after the first read, the event log itself reports every later wake.
            if (Wanted("last_wake_reason"))
            {
                _lastWake = Safe(ReadLastWakeInfo, _lastWake);
                StartWakeWatcher();
            }
        }

        // Null for a role that does not report the sensor: both roles publish to one state
        // topic, and an empty value from one would blank out what the other reports.
        var lastWake = Wanted("last_wake_reason") ? _lastWake : null;
        var attributes = BuildAttributes(_lastNetworkAddresses, _lastDisplays, _lastRecentErrors, _lastShutdown,
            lastWake, _lastExecutionState, _lastPowerRequests, _lastCameraApps, _lastMicrophoneApps, _lastGpu);
        var message = new SystemMetricsMessage(
            CpuUsage: updateFast ? Safe(ReadCpuUsage, previous?.CpuUsage ?? 0) : previous!.CpuUsage,
            MemoryUsage: memory.UsagePercent,
            MemoryAvailableMb: memory.AvailableMb,
            SystemDriveFreePercent: drive.FreePercent,
            SystemDriveFreeGb: drive.FreeGb,
            UptimeSeconds: updateFast ? Math.Max(0, Environment.TickCount64 / 1000) : previous!.UptimeSeconds,
            // Null, not empty: a role that cannot measure these (the service runs without
            // interactive metrics) must leave them out of the payload entirely. The
            // serializer drops nulls and Home Assistant ignores absent fields, so the
            // service no longer overwrites the tray app's values with blanks.
            ActiveWindow: updateFast && _includeInteractiveMetrics ? LimitState(activeWindow.Title) : previous?.ActiveWindow,
            ActiveProcess: updateFast && _includeInteractiveMetrics ? LimitState(activeWindow.ProcessName) : previous?.ActiveProcess,
            ForegroundAppTitle: updateFast && _includeInteractiveMetrics ? BuildForegroundAppTitle(activeWindow) : previous?.ForegroundAppTitle,
            Volume: updateFast ? Safe(() => _audioEndpointService?.GetVolume(), previous?.Volume) : previous!.Volume,
            Muted: updateFast ? Safe(() => _audioEndpointService?.GetMuted(), previous?.Muted) : previous!.Muted,
            AudioOutputDevice: updateNormal && _includeInteractiveMetrics ? Safe<string?>(() => LimitState(_audioEndpointService?.GetOutputDeviceName() ?? string.Empty), previous?.AudioOutputDevice) : previous?.AudioOutputDevice,
            MicrophoneMuted: updateNormal && _includeInteractiveMetrics ? Safe(() => _audioEndpointService?.GetMicrophoneMuted(), previous?.MicrophoneMuted) : previous?.MicrophoneMuted,
            BatteryLevel: power.BatteryLevel,
            PowerStatus: power.Status,
            BatteryTimeRemaining: power.TimeRemainingSeconds,
            MonitorPowerState: updateNormal ? Safe(() => _monitorPowerStateService?.State, previous?.MonitorPowerState) : previous!.MonitorPowerState,
            ActiveDisplay: updateNormal && _includeInteractiveMetrics ? Safe<string?>(() => FormatDisplayState(_lastDisplays), previous?.ActiveDisplay) : previous?.ActiveDisplay,
            NetworkAddress: updateNormal ? _lastNetworkAddresses.FirstOrDefault()?.Address ?? string.Empty : previous!.NetworkAddress,
            VpnConnected: updateNormal ? Safe(ReadVpnConnected, previous?.VpnConnected ?? false) : previous!.VpnConnected,
            WifiSsid: wifi.Ssid,
            WifiSignal: wifi.Signal,
            IdleTimeSeconds: updateFast && _includeInteractiveMetrics ? Safe(ReadIdleTimeSeconds, previous?.IdleTimeSeconds) : previous?.IdleTimeSeconds,
            SessionLocked: sessionLocked,
            UserPresent: updateFast && _includeInteractiveMetrics ? session.State == "active" && sessionLocked is false && !string.IsNullOrWhiteSpace(session.User) : previous?.UserPresent,
            ClipboardTextAvailable: updateFast && _includeInteractiveMetrics ? Safe(ReadClipboardTextAvailable, previous?.ClipboardTextAvailable) : previous?.ClipboardTextAvailable,
            CameraInUse: _lastCameraApps is { } cameraApps ? cameraApps.Count > 0 : null,
            MicrophoneInUse: _lastMicrophoneApps is { } microphoneApps ? microphoneApps.Count > 0 : null,
            GpuUsage: _lastGpu?.Usage,
            SessionState: session.State,
            LoggedInUser: session.User,
            LoggedInUsers: sessions.LoggedInUsers,
            RdpSessions: sessions.RdpSessions,
            PendingReboot: updateNormal ? Safe(ReadPendingReboot, previous?.PendingReboot ?? false) : previous!.PendingReboot,
            // A display request counts too: a browser playing video raises only that one on
            // many machines (whether audio adds a system request depends on the audio driver),
            // and the machine still does not go to sleep while it is held.
            SleepBlocked: _lastExecutionState is { } executionState
                ? (executionState & (EsSystemRequired | EsDisplayRequired | EsAwayModeRequired)) != 0
                : null,
            WindowsUpdatePending: updateHourly ? Safe(ReadWindowsUpdatePending, previous?.WindowsUpdatePending ?? false) : previous!.WindowsUpdatePending,
            BluetoothEnabled: updateHourly ? Safe(ReadBluetoothEnabled, previous?.BluetoothEnabled ?? false) : previous!.BluetoothEnabled,
            EventLogErrorsRecent: _lastRecentErrors.Count,
            LastShutdownReason: _lastShutdown.Summary,
            LastWakeReason: lastWake?.Summary,
            BootTime: updateStartup ? DateTimeOffset.Now.AddMilliseconds(-Environment.TickCount64) : previous!.BootTime,
            CustomSensors: Safe(() => ReadCustomSensors(customSensors ?? [], serviceRole, attributes, profiles, previous?.CustomSensors ?? []), previous?.CustomSensors ?? []),
            Attributes: attributes,
            UpdatedAt: DateTimeOffset.UtcNow);

        _lastMessage = message;
        return message;
    }

    public void Dispose()
    {
        _wakeWatcher?.Dispose();
        _wakeWatcher = null;
    }

    // Isolates a single metric read: on failure it logs which read threw (and the full
    // exception, so the real method shows even when the Release build inlines the caller)
    // and returns a fallback, so one bad read can't take the whole sensor cycle offline.
    private T Safe<T>(Func<T> read, T fallback, [CallerArgumentExpression(nameof(read))] string name = "")
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            _log.Warning($"Metric read failed [{name}]: {ex}");
            return fallback;
        }
    }

    private double ReadCpuUsage()
    {
        lock (_cpuLock)
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user))
            {
                return 0;
            }

            var idleTime = idle.ToUInt64();
            var kernelTime = kernel.ToUInt64();
            var userTime = user.ToUInt64();

            if (_lastIdleTime is null || _lastKernelTime is null || _lastUserTime is null)
            {
                _lastIdleTime = idleTime;
                _lastKernelTime = kernelTime;
                _lastUserTime = userTime;
                return 0;
            }

            var idleDelta = idleTime - _lastIdleTime.Value;
            var kernelDelta = kernelTime - _lastKernelTime.Value;
            var userDelta = userTime - _lastUserTime.Value;
            var totalDelta = kernelDelta + userDelta;

            _lastIdleTime = idleTime;
            _lastKernelTime = kernelTime;
            _lastUserTime = userTime;

            if (totalDelta == 0)
            {
                return 0;
            }

            var usage = (totalDelta - idleDelta) * 100d / totalDelta;
            return Math.Round(Math.Clamp(usage, 0, 100), 1);
        }
    }

    private static (double UsagePercent, long AvailableMb) ReadMemory()
    {
        var status = new MemoryStatusEx
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
        };

        if (!GlobalMemoryStatusEx(ref status))
        {
            return (0, 0);
        }

        return (
            Math.Round((double)status.MemoryLoad, 1),
            Convert.ToInt64(status.AvailablePhysical / 1024 / 1024));
    }

    private static (double FreePercent, double FreeGb) ReadSystemDrive()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory);
        if (string.IsNullOrWhiteSpace(root))
        {
            return (0, 0);
        }

        try
        {
            var drive = new DriveInfo(root);
            if (!drive.IsReady || drive.TotalSize <= 0)
            {
                return (0, 0);
            }

            return (
                Math.Round(drive.AvailableFreeSpace * 100d / drive.TotalSize, 1),
                Math.Round(drive.AvailableFreeSpace / 1024d / 1024d / 1024d, 1));
        }
        catch
        {
            return (0, 0);
        }
    }

    private static (string Title, string ProcessName) ReadActiveWindow()
    {
        var handle = GetForegroundWindow();
        if (handle == IntPtr.Zero)
        {
            return (string.Empty, string.Empty);
        }

        var titleBuilder = new StringBuilder(512);
        _ = GetWindowText(handle, titleBuilder, titleBuilder.Capacity);

        var processName = string.Empty;
        _ = GetWindowThreadProcessId(handle, out var processId);
        if (processId > 0)
        {
            try
            {
                using var process = Process.GetProcessById((int)processId);
                processName = process.ProcessName;
            }
            catch
            {
                processName = string.Empty;
            }
        }

        return (titleBuilder.ToString(), processName);
    }

    private static string BuildForegroundAppTitle((string Title, string ProcessName) activeWindow)
    {
        if (string.IsNullOrWhiteSpace(activeWindow.ProcessName))
        {
            return LimitState(activeWindow.Title);
        }

        if (string.IsNullOrWhiteSpace(activeWindow.Title))
        {
            return LimitState(activeWindow.ProcessName);
        }

        return LimitState($"{activeWindow.ProcessName} - {activeWindow.Title}");
    }

    private static (int? BatteryLevel, string Status, long? TimeRemainingSeconds) ReadPowerStatus()
    {
        var status = SystemInformation.PowerStatus;
        long? timeRemaining = status.BatteryLifeRemaining >= 0 ? status.BatteryLifeRemaining : null;
        if (status.BatteryChargeStatus.HasFlag(BatteryChargeStatus.NoSystemBattery))
        {
            return (null, "no_battery", null);
        }

        int? batteryLevel = status.BatteryLifePercent >= 0
            ? Convert.ToInt32(Math.Clamp(Math.Round(status.BatteryLifePercent * 100), 0, 100))
            : null;

        if (status.PowerLineStatus == PowerLineStatus.Online &&
            status.BatteryChargeStatus.HasFlag(BatteryChargeStatus.Charging))
        {
            return (batteryLevel, "charging", timeRemaining);
        }

        return status.PowerLineStatus switch
        {
            PowerLineStatus.Online => (batteryLevel, "plugged_in", timeRemaining),
            PowerLineStatus.Offline => (batteryLevel, "battery", timeRemaining),
            _ => (batteryLevel, "unknown", timeRemaining)
        };
    }

    private static IReadOnlyList<NetworkAddressInfo> ReadNetworkAddresses()
    {
        var result = new List<NetworkAddressInfo>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    // Any adapter property (status, GetIPProperties, …) can throw on an adapter
                    // that is mid-reinitialization after startup/resume; skip just that adapter
                    // instead of failing the whole read.
                    if (adapter.OperationalStatus != OperationalStatus.Up ||
                        adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }

                    foreach (var address in adapter.GetIPProperties().UnicastAddresses)
                    {
                        if (address.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !IPAddress.IsLoopback(address.Address) &&
                            !address.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                        {
                            result.Add(new NetworkAddressInfo(
                                adapter.Name ?? string.Empty,
                                adapter.Description ?? string.Empty,
                                address.Address.ToString()));
                        }
                    }
                }
                catch
                {
                    // Ignore this adapter and keep going.
                }
            }
        }
        catch
        {
            // Enumerating interfaces failed entirely — return whatever was collected.
        }

        return result;
    }

    /// <summary>
    /// Reads displays with a timeout. Screen.AllScreens can block while Windows
    /// reconfigures the display subsystem during a monitor power transition
    /// (dimmed/off) — exactly when a push update fires — so fall back to the last
    /// known displays instead of freezing the whole sensor loop.
    /// </summary>
    private IReadOnlyList<DisplayInfo> ReadDisplaysSafe()
    {
        try
        {
            var task = Task.Run(ReadDisplays);
            return task.Wait(TimeSpan.FromSeconds(2)) ? task.Result : _lastDisplays;
        }
        catch
        {
            return _lastDisplays;
        }
    }

    private static IReadOnlyList<DisplayInfo> ReadDisplays()
    {
        try
        {
            return Screen.AllScreens
                .Select(screen => new DisplayInfo(
                    screen.DeviceName,
                    screen.Primary,
                    screen.Bounds.Width,
                    screen.Bounds.Height,
                    screen.Bounds.X,
                    screen.Bounds.Y))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string FormatDisplayState(IReadOnlyList<DisplayInfo> displays)
    {
        return LimitState(string.Join(
            ", ",
            displays.Select(display =>
                $"{(display.Primary ? "primary:" : string.Empty)}{display.Name} {display.Width}x{display.Height}")));
    }

    private static bool ReadVpnConnected()
    {
        string[] vpnHints = ["vpn", "wireguard", "tailscale", "zerotier", "tap", "tun", "openvpn", "nord", "proton", "surfshark"];
        NetworkInterface[] adapters;
        try
        {
            adapters = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch
        {
            return false;
        }

        foreach (var adapter in adapters)
        {
            try
            {
                // Right after startup/resume an adapter can be "Up" but still have a null
                // Name/Description while the network stack re-initializes. Guard each adapter
                // on its own so one flaky adapter doesn't hide a VPN on a later one.
                if (adapter.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                var name = adapter.Name ?? string.Empty;
                var description = adapter.Description ?? string.Empty;
                if (vpnHints.Any(hint => name.Contains(hint, StringComparison.OrdinalIgnoreCase)) ||
                    vpnHints.Any(hint => description.Contains(hint, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            catch
            {
                // Skip this adapter and keep checking the rest.
            }
        }

        return false;
    }

    private static (string Ssid, int? Signal) ReadWifiStatus()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = "wlan show interfaces",
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            });

            if (process is null)
            {
                return (string.Empty, null);
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            if (process.ExitCode != 0)
            {
                return (string.Empty, null);
            }

            var ssid = Regex.Match(output, @"^\s*SSID\s*:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value.Trim();
            var signalText = Regex.Match(output, @"^\s*(Signal|Jel)\s*:\s*(\d+)%", RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[2].Value;
            int? signal = int.TryParse(signalText, out var parsedSignal) ? parsedSignal : null;
            return (LimitState(ssid), signal);
        }
        catch
        {
            return (string.Empty, null);
        }
    }

    private static long? ReadIdleTimeSeconds()
    {
        var input = new LastInputInfo
        {
            Size = (uint)Marshal.SizeOf<LastInputInfo>()
        };

        if (!GetLastInputInfo(ref input))
        {
            return null;
        }

        var idleMilliseconds = Environment.TickCount64 - input.Time;
        return Math.Max(0, idleMilliseconds / 1000);
    }

    private static (string State, string User) ReadSessionStatus()
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == uint.MaxValue)
        {
            return ("none", string.Empty);
        }

        var state = QuerySessionConnectState(sessionId);
        var userName = QuerySessionString(sessionId, WtsInfoClass.UserName);
        var domain = QuerySessionString(sessionId, WtsInfoClass.DomainName);

        var user = string.IsNullOrWhiteSpace(userName)
            ? string.Empty
            : string.IsNullOrWhiteSpace(domain) ? userName : $"{domain}\\{userName}";

        return (state, LimitState(user));
    }

    private static bool? ReadClipboardTextAvailable()
    {
        bool? result = null;
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = Clipboard.ContainsText();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromMilliseconds(750)))
        {
            return null;
        }

        return exception is null ? result : null;
    }

    private static (int LoggedInUsers, int RdpSessions) ReadSessionCounts()
    {
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var sessionsPointer, out var sessionCount))
        {
            return (0, 0);
        }

        try
        {
            var loggedInUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rdpSessions = 0;
            var itemSize = Marshal.SizeOf<WtsSessionInfo>();

            for (var index = 0; index < sessionCount; index++)
            {
                var itemPointer = IntPtr.Add(sessionsPointer, index * itemSize);
                var session = Marshal.PtrToStructure<WtsSessionInfo>(itemPointer);
                if (session.State != WtsConnectState.Active)
                {
                    continue;
                }

                var userName = QuerySessionString(session.SessionId, WtsInfoClass.UserName);
                if (!string.IsNullOrWhiteSpace(userName))
                {
                    var domain = QuerySessionString(session.SessionId, WtsInfoClass.DomainName);
                    loggedInUsers.Add(string.IsNullOrWhiteSpace(domain) ? userName : $"{domain}\\{userName}");
                }

                var clientProtocol = QuerySessionUInt16(session.SessionId, WtsInfoClass.ClientProtocolType);
                if (clientProtocol == 2)
                {
                    rdpSessions++;
                }
            }

            return (loggedInUsers.Count, rdpSessions);
        }
        finally
        {
            WTSFreeMemory(sessionsPointer);
        }
    }

    private static bool? ReadSessionLocked()
    {
        var desktop = OpenInputDesktop(0, false, DesktopSwitchDesktop);
        if (desktop == IntPtr.Zero)
        {
            return true;
        }

        try
        {
            return false;
        }
        finally
        {
            _ = CloseDesktop(desktop);
        }
    }

    private static string QuerySessionConnectState(uint sessionId)
    {
        if (!WTSQuerySessionInformation(
            IntPtr.Zero,
            sessionId,
            WtsInfoClass.ConnectState,
            out var buffer,
            out var bytesReturned))
        {
            return "unknown";
        }

        try
        {
            if (bytesReturned < sizeof(int))
            {
                return "unknown";
            }

            var state = (WtsConnectState)Marshal.ReadInt32(buffer);
            return state switch
            {
                WtsConnectState.Active => "active",
                WtsConnectState.Connected => "connected",
                WtsConnectState.ConnectQuery => "connect_query",
                WtsConnectState.Shadow => "shadow",
                WtsConnectState.Disconnected => "disconnected",
                WtsConnectState.Idle => "idle",
                WtsConnectState.Listen => "listen",
                WtsConnectState.Reset => "reset",
                WtsConnectState.Down => "down",
                WtsConnectState.Init => "init",
                _ => "unknown"
            };
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static string QuerySessionString(uint sessionId, WtsInfoClass infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out var buffer, out _))
        {
            return string.Empty;
        }

        try
        {
            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static ushort? QuerySessionUInt16(uint sessionId, WtsInfoClass infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out var buffer, out var bytesReturned))
        {
            return null;
        }

        try
        {
            return bytesReturned >= sizeof(ushort)
                ? unchecked((ushort)Marshal.ReadInt16(buffer))
                : null;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <summary>
    /// The system-wide execution state: the union of every power request, including the
    /// modern PowerSetRequest ones (browsers, media players) and driver requests (an open
    /// audio stream), not just SetThreadExecutionState.
    /// </summary>
    private static uint? ReadSystemExecutionState()
    {
        var status = CallNtPowerInformation(PowerInformationSystemExecutionState, IntPtr.Zero, 0, out var state, sizeof(uint));
        return status == 0 ? state : null;
    }

    private static IReadOnlyList<PowerRequestInfo> ReadPowerRequests()
    {
        // powercfg writes in the OEM code page: the request reasons are localized
        // ("Egy hangadatfolyam használatban van."), so reading it as UTF-8 mangles them.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding((int)GetOEMCP());

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powercfg.exe",
            Arguments = "/requests",
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding
        });

        if (process is null)
        {
            return [];
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(PowerRequestsTimeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignore — the process may already be exiting.
            }

            return [];
        }

        var output = stdoutTask.GetAwaiter().GetResult();
        _ = stderrTask.GetAwaiter().GetResult();
        return process.ExitCode == 0 ? ParsePowerRequests(output) : [];
    }

    /// <summary>
    /// Parses <c>powercfg /requests</c>. The category headers ("DISPLAY:") and the holder
    /// tags ("[PROCESS]") stay English on localized Windows; only the reason lines are
    /// translated, and they are passed through as they are.
    /// </summary>
    internal static IReadOnlyList<PowerRequestInfo> ParsePowerRequests(string output)
    {
        var requests = new List<PowerRequestInfo>();
        string? category = null;
        string? type = null;
        string? name = null;
        var reason = new StringBuilder();

        void Flush()
        {
            if (category is "display" or "system" or "awaymode" or "execution" && type is not null && name is not null)
            {
                // An execution request keeps its process running (it matters in Modern Standby),
                // but it does not stop the machine from going to sleep and sets no flag.
                requests.Add(new PowerRequestInfo(category, type, name, reason.ToString().Trim(), Blocking: category != "execution"));
            }

            type = null;
            name = null;
            reason.Clear();
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            var header = Regex.Match(line, @"^([A-Z]+):$");
            if (header.Success)
            {
                Flush();
                category = header.Groups[1].Value.ToLowerInvariant();
                continue;
            }

            var holder = Regex.Match(line, @"^\[([A-Z]+)\]\s*(.+)$");
            if (holder.Success)
            {
                Flush();
                type = holder.Groups[1].Value.ToLowerInvariant();
                name = PowerRequestHolderName(type, holder.Groups[2].Value.Trim());
                continue;
            }

            if (line.Length == 0)
            {
                Flush();
            }
            else if (name is not null)
            {
                // "None." under an empty category never follows a holder, so it is skipped.
                reason.Append(reason.Length == 0 ? string.Empty : " ").Append(line);
            }
        }

        Flush();
        return requests.Take(MaxPowerRequests).ToList();
    }

    private static string PowerRequestHolderName(string type, string holder)
    {
        // [PROCESS] \Device\HarddiskVolume3\...\msedge.exe
        // [SERVICE] \Device\HarddiskVolume3\...\svchost.exe (AudioSrv)
        // [DRIVER]  NVIDIA High Definition Audio (HDAUDIO\FUNC_01&...)
        var detailStart = holder.LastIndexOf(" (", StringComparison.Ordinal);
        var main = detailStart > 0 && holder.EndsWith(')') ? holder[..detailStart] : holder;
        var detail = detailStart > 0 && holder.EndsWith(')') ? holder[(detailStart + 2)..^1] : null;
        return type switch
        {
            "driver" => LimitState(main),
            "service" when detail is not null => LimitState(detail),
            _ => LimitState(Path.GetFileName(main))
        };
    }

    /// <summary>
    /// The apps using the camera or the microphone right now, from the record behind the
    /// Windows privacy indicator: every app that ever used the capability has a key with
    /// LastUsedTimeStart / LastUsedTimeStop, and the stop time is 0 while it is in use.
    /// </summary>
    private static IReadOnlyList<string> ReadCapabilityUsers(string capability)
    {
        using var store = Registry.CurrentUser.OpenSubKey($@"{CapabilityConsentStorePath}\{capability}");
        if (store is null)
        {
            return [];
        }

        var apps = new List<string>();
        foreach (var packageName in store.GetSubKeyNames())
        {
            if (string.Equals(packageName, CapabilityNonPackagedKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var package = store.OpenSubKey(packageName);
            if (IsCapabilityInUse(package))
            {
                // MSTeams_8wekyb3d8bbwe -> MSTeams
                var publisherStart = packageName.LastIndexOf('_');
                apps.Add(LimitState(publisherStart > 0 ? packageName[..publisherStart] : packageName));
            }
        }

        using var nonPackaged = store.OpenSubKey(CapabilityNonPackagedKey);
        foreach (var appKeyName in nonPackaged?.GetSubKeyNames() ?? [])
        {
            using var app = nonPackaged!.OpenSubKey(appKeyName);
            if (!IsCapabilityInUse(app))
            {
                continue;
            }

            // C:#Program Files (x86)#Microsoft#Edge#Application#msedge.exe
            var fileName = Path.GetFileName(appKeyName.Replace('#', '\\'));

            // An app that crashed mid-call never writes its stop time, and would hold the
            // sensor on until it next uses the device. Only desktop apps can be checked.
            if (IsProcessRunning(Path.GetFileNameWithoutExtension(fileName)))
            {
                apps.Add(LimitState(fileName));
            }
        }

        return apps
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxCapabilityApps)
            .ToList();
    }

    private static bool IsCapabilityInUse(RegistryKey? app)
    {
        return app?.GetValue("LastUsedTimeStart") is long and not 0
            && app.GetValue("LastUsedTimeStop") is long and 0;
    }

    private static bool IsProcessRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        foreach (var process in processes)
        {
            process.Dispose();
        }

        return processes.Length > 0;
    }

    private static bool ReadPendingReboot()
    {
        return RegistryKeyExists(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending") ||
            RegistryKeyExists(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired") ||
            RegistryValueExists(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations");
    }

    private bool ReadWindowsUpdatePending()
    {
        if (_lastWindowsUpdatePendingReadAt is not null &&
            DateTimeOffset.UtcNow - _lastWindowsUpdatePendingReadAt < WindowsUpdatePendingCacheDuration)
        {
            return _lastWindowsUpdatePending;
        }

        _lastWindowsUpdatePending = ReadWindowsUpdatePendingFromAgent();
        _lastWindowsUpdatePendingReadAt = DateTimeOffset.UtcNow;
        return _lastWindowsUpdatePending;
    }

    private static bool ReadWindowsUpdatePendingFromAgent()
    {
        object? session = null;
        object? searcher = null;
        object? result = null;
        object? updates = null;

        try
        {
            var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (sessionType is null)
            {
                return false;
            }

            session = Activator.CreateInstance(sessionType);
            if (session is null)
            {
                return false;
            }

            searcher = sessionType.InvokeMember(
                "CreateUpdateSearcher",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                session,
                null);
            if (searcher is null)
            {
                return false;
            }

            result = searcher.GetType().InvokeMember(
                "Search",
                System.Reflection.BindingFlags.InvokeMethod,
                null,
                searcher,
                ["IsInstalled=0 and IsHidden=0 and Type='Software'"]);

            updates = result?.GetType().InvokeMember(
                "Updates",
                System.Reflection.BindingFlags.GetProperty,
                null,
                result,
                null);

            var count = updates?.GetType().InvokeMember(
                "Count",
                System.Reflection.BindingFlags.GetProperty,
                null,
                updates,
                null);

            return count is int updateCount && updateCount > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            ReleaseComObject(updates);
            ReleaseComObject(result);
            ReleaseComObject(searcher);
            ReleaseComObject(session);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.ReleaseComObject(value);
        }
    }

    private static bool ReadBluetoothEnabled()
    {
        try
        {
            using var controller = new ServiceController("bthserv");
            if (controller.Status is not (ServiceControllerStatus.Running or ServiceControllerStatus.StartPending))
            {
                return false;
            }

            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var devices = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices");
            return devices?.GetSubKeyNames().Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<EventLogErrorInfo> ReadRecentEventLogErrors(TimeSpan window)
    {
        var since = DateTime.UtcNow - window;
        return ReadRecentLogErrors("System", since)
            .Concat(ReadRecentLogErrors("Application", since))
            .OrderByDescending(error => error.CreatedAt)
            .Take(20)
            .ToList();
    }

    private static IReadOnlyList<EventLogErrorInfo> ReadRecentLogErrors(string logName, DateTime sinceUtc)
    {
        try
        {
            var query = new EventLogQuery(
                logName,
                PathType.LogName,
                "*[System[(Level=1 or Level=2)]]")
            {
                ReverseDirection = true
            };

            using var reader = new EventLogReader(query);
            var errors = new List<EventLogErrorInfo>();
            for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
            {
                using (record)
                {
                    if (record.TimeCreated is null || record.TimeCreated.Value.ToUniversalTime() < sinceUtc)
                    {
                        break;
                    }

                    errors.Add(new EventLogErrorInfo(
                        logName,
                        record.ProviderName ?? string.Empty,
                        record.Id,
                        record.LevelDisplayName ?? string.Empty,
                        record.TimeCreated.Value));
                }
            }

            return errors;
        }
        catch
        {
            return [];
        }
    }

    private static ShutdownInfo ReadLastShutdownInfo()
    {
        try
        {
            var query = new EventLogQuery(
                "System",
                PathType.LogName,
                "*[System[(EventID=1074 or EventID=6008 or EventID=41)]]")
            {
                ReverseDirection = true
            };

            using var reader = new EventLogReader(query);
            using var record = reader.ReadEvent();
            if (record is null)
            {
                return new ShutdownInfo(string.Empty, string.Empty, null, 0, string.Empty);
            }

            var message = string.Empty;
            try
            {
                message = record.FormatDescription() ?? string.Empty;
            }
            catch
            {
                // Some localized event messages cannot be formatted if the provider resources are unavailable.
            }

            var reason = record.Id switch
            {
                1074 => "planned",
                6008 => "unexpected",
                41 => "kernel_power",
                _ => "unknown"
            };

            var created = record.TimeCreated?.ToString("yyyy-MM-dd HH:mm:ss") ?? "unknown time";
            var summary = LimitState(string.IsNullOrWhiteSpace(message)
                ? $"{reason} at {created}"
                : $"{reason} at {created}: {message}");
            return new ShutdownInfo(summary, reason, record.TimeCreated, record.Id, LimitState(message));
        }
        catch
        {
            return new ShutdownInfo(string.Empty, string.Empty, null, 0, string.Empty);
        }
    }

    /// <summary>
    /// GPU load the way Task Manager shows it: every process has a counter per engine, the
    /// engines of one type add up, and the busiest engine type is the GPU's load.
    /// </summary>
    private GpuInfo? ReadGpu()
    {
        if (_gpuEngineCategory is null)
        {
            if (!PerformanceCounterCategory.Exists(GpuEngineCategoryName))
            {
                return null;
            }

            _gpuEngineCategory = new PerformanceCounterCategory(GpuEngineCategoryName);
        }

        var current = _gpuEngineCategory.ReadCategory()[GpuEngineUtilizationCounter];
        var previous = _lastGpuEngineSamples;
        if (previous is null && current is not null)
        {
            // The load is the difference between two samples. Later cycles use the one from
            // the cycle before; only the very first read has to wait for a second sample.
            Thread.Sleep(GpuFirstSampleDelayMs);
            previous = current;
            current = _gpuEngineCategory.ReadCategory()[GpuEngineUtilizationCounter];
        }

        _lastGpuEngineSamples = current;
        if (current is null || previous is null)
        {
            return null;
        }

        // adapter LUID -> engine type -> load
        var adapters = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in current)
        {
            if (entry.Value is not InstanceData instance ||
                previous[instance.InstanceName] is not { } before ||
                GpuEngineInstancePattern.Match(instance.InstanceName) is not { Success: true } match)
            {
                continue;
            }

            var load = CounterSample.Calculate(before.Sample, instance.Sample);
            if (float.IsNaN(load) || float.IsInfinity(load))
            {
                continue;
            }

            var engineType = match.Groups[2].Value.Trim().ToLowerInvariant().Replace(' ', '_');
            if (!adapters.TryGetValue(match.Groups[1].Value, out var engines))
            {
                adapters[match.Groups[1].Value] = engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            }

            engines[engineType] = engines.GetValueOrDefault(engineType) + load;
        }

        // An NPU shows up as an adapter of its own with nothing but neural engines. It is
        // reported next to the GPU, never as the GPU's load.
        var npu = adapters.Values
            .Where(engines => engines.Keys.All(type => type.StartsWith(GpuNeuralEngineType, StringComparison.Ordinal)))
            .Select(engines => (double?)engines.Values.Max())
            .Max();

        // The busiest adapter; when all are idle, the one with the most engine types (the
        // Microsoft software renderer has a single one).
        var gpu = adapters
            .Where(adapter => !adapter.Value.Keys.All(type => type.StartsWith(GpuNeuralEngineType, StringComparison.Ordinal)))
            .OrderByDescending(adapter => adapter.Value.Values.Max())
            .ThenByDescending(adapter => adapter.Value.Count)
            .FirstOrDefault();
        if (gpu.Value is null)
        {
            return null;
        }

        var memory = ReadGpuMemory(gpu.Key);
        _gpuAdapters ??= ReadGpuAdapters();
        return new GpuInfo(
            Percent(gpu.Value.Values.Max()),
            gpu.Value
                .Where(engine => !engine.Key.StartsWith(GpuNeuralEngineType, StringComparison.Ordinal))
                .OrderBy(engine => engine.Key, StringComparer.Ordinal)
                .ToDictionary(engine => engine.Key, engine => Percent(engine.Value)),
            npu is { } npuLoad ? Percent(npuLoad) : null,
            memory.DedicatedMb,
            memory.SharedMb,
            _gpuAdapters);

        static double Percent(double value) => Math.Round(Math.Clamp(value, 0, 100), 1);
    }

    private static (long? DedicatedMb, long? SharedMb) ReadGpuMemory(string luid)
    {
        if (!PerformanceCounterCategory.Exists(GpuAdapterMemoryCategoryName))
        {
            return (null, null);
        }

        var data = new PerformanceCounterCategory(GpuAdapterMemoryCategoryName).ReadCategory();
        return (Read("Dedicated Usage"), Read("Shared Usage"));

        long? Read(string counter)
        {
            if (data[counter] is not { } instances)
            {
                return null;
            }

            foreach (DictionaryEntry entry in instances)
            {
                // luid_0x00000000_0x0000E9FD_phys_0
                if (entry.Value is InstanceData instance &&
                    instance.InstanceName.StartsWith($"luid_{luid}_", StringComparison.OrdinalIgnoreCase))
                {
                    return instance.RawValue / (1024 * 1024);
                }
            }

            return null;
        }
    }

    /// <summary>
    /// The installed display adapters with their real memory size: WMI's AdapterRAM is a
    /// 32-bit field and stops at 4 GB.
    /// </summary>
    private static IReadOnlyList<GpuAdapterInfo> ReadGpuAdapters()
    {
        var adapters = new List<GpuAdapterInfo>();
        using var displayClass = Registry.LocalMachine.OpenSubKey(DisplayAdapterClassPath);
        foreach (var keyName in displayClass?.GetSubKeyNames() ?? [])
        {
            // 0000, 0001, ... are the adapters; the rest ("Properties") is not readable anyway.
            if (keyName.Length != 4 || !keyName.All(char.IsAsciiDigit))
            {
                continue;
            }

            using var adapter = displayClass!.OpenSubKey(keyName);
            if (adapter?.GetValue("DriverDesc") is not string { Length: > 0 } name)
            {
                continue;
            }

            var memoryMb = adapter.GetValue("HardwareInformation.qwMemorySize") is long bytes and > 0
                ? bytes / (1024 * 1024)
                : (long?)null;
            adapters.Add(new GpuAdapterInfo(LimitState(name), memoryMb));
        }

        return adapters;
    }

    private static WakeInfo ReadLastWakeInfo()
    {
        var query = new EventLogQuery("System", PathType.LogName, WakeEventQuery)
        {
            ReverseDirection = true
        };

        using var reader = new EventLogReader(query);
        using var record = reader.ReadEvent();
        return record is null ? WakeInfo.None : ParseWakeEvent(record);
    }

    private void StartWakeWatcher()
    {
        if (_wakeWatcher is not null)
        {
            return;
        }

        try
        {
            var watcher = new EventLogWatcher(new EventLogQuery("System", PathType.LogName, WakeEventQuery));
            watcher.EventRecordWritten += (_, e) =>
            {
                using var record = e.EventRecord;
                if (record is not null)
                {
                    // Picked up by the next sensor cycle, which publishes the whole state.
                    _lastWake = Safe(() => ParseWakeEvent(record), _lastWake);
                }
            };
            watcher.Enabled = true;
            _wakeWatcher = watcher;
        }
        catch (Exception ex)
        {
            _log.Warning($"Wake event watcher could not start: {ex.Message}");
        }
    }

    private static WakeInfo ParseWakeEvent(EventRecord record)
    {
        var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in XDocument.Parse(record.ToXml()).Descendants().Where(element => element.Name.LocalName == "Data"))
        {
            if (element.Attribute("Name")?.Value is { Length: > 0 } name)
            {
                data.TryAdd(name, element.Value);
            }
        }

        var message = string.Empty;
        try
        {
            message = record.FormatDescription() ?? string.Empty;
        }
        catch
        {
            // Some localized event messages cannot be formatted if the provider resources are unavailable.
        }

        var modernStandby = record.Id == ModernStandbyExitEventId;
        var source = WakeSourceFromMessage(message);
        if (source.Length == 0)
        {
            source = modernStandby
                ? $"Reason {data.GetValueOrDefault("Reason", "unknown")}"
                : data.GetValueOrDefault("WakeSourceText") is { Length: > 0 } text ? text : "Unknown";
        }

        // A reason Windows has no name for is rendered as its bare number.
        if (source.All(char.IsAsciiDigit))
        {
            source = $"Unknown ({source})";
        }

        string kind;
        long? durationSeconds = null;
        bool? sleepEntered;
        var detail = string.Empty;
        if (modernStandby)
        {
            kind = "modern_standby";
            // False when only the screen was off and the machine never reached its sleep state.
            sleepEntered = bool.TryParse(data.GetValueOrDefault("SleepEntered"), out var entered) ? entered : null;
            if (long.TryParse(data.GetValueOrDefault("DurationInUs"), NumberStyles.None, CultureInfo.InvariantCulture, out var microseconds))
            {
                durationSeconds = microseconds / 1_000_000;
            }
        }
        else
        {
            // SYSTEM_POWER_STATE: 4 = S3, 5 = hibernate, 6 = shutdown. A shutdown that ended up
            // in hibernate is Fast Startup: the "wake" is the next power-on.
            kind = data.GetValueOrDefault("TargetState") == "6"
                ? "fast_startup"
                : data.GetValueOrDefault("EffectiveState") switch
                {
                    "4" => "sleep",
                    "5" => "hibernate",
                    _ => "unknown"
                };
            sleepEntered = true;
            if (DateTime.TryParse(data.GetValueOrDefault("SleepTime"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var sleepTime) &&
                DateTime.TryParse(data.GetValueOrDefault("WakeTime"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var wakeTime) &&
                wakeTime >= sleepTime)
            {
                durationSeconds = (long)(wakeTime - sleepTime).TotalSeconds;
            }

            // The device that woke the machine, or the task behind a wake timer.
            detail = data.GetValueOrDefault("WakeTimerOwner") is { Length: > 0 } owner
                ? owner
                : data.GetValueOrDefault("WakeSourceText", string.Empty);
        }

        var created = record.TimeCreated?.ToString("yyyy-MM-dd HH:mm:ss") ?? "unknown time";
        return new WakeInfo(
            LimitState($"{source} at {created}"),
            LimitState(source),
            kind,
            record.TimeCreated,
            durationSeconds,
            sleepEntered,
            LimitState(detail));
    }

    /// <summary>
    /// Both events end in "&lt;label&gt;: &lt;source&gt;". The label is localized, the source is not
    /// ("Felébresztés forrása: Unknown", "Ok: Input Mouse."), so it reads the same everywhere.
    /// </summary>
    internal static string WakeSourceFromMessage(string message)
    {
        var line = message
            .Split('\n')
            .Select(part => part.Replace("\u200E", string.Empty).Trim())
            .LastOrDefault(part => part.Length > 0) ?? string.Empty;
        var labelEnd = line.IndexOf(':');
        return labelEnd < 0 ? string.Empty : line[(labelEnd + 1)..].Trim().TrimEnd('.').Trim();
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> BuildAttributes(
        IReadOnlyList<NetworkAddressInfo> networkAddresses,
        IReadOnlyList<DisplayInfo> displays,
        IReadOnlyList<EventLogErrorInfo> recentErrors,
        ShutdownInfo lastShutdown,
        WakeInfo? lastWake,
        uint? executionState,
        IReadOnlyList<PowerRequestInfo> powerRequests,
        IReadOnlyList<string>? cameraApps,
        IReadOnlyList<string>? microphoneApps,
        GpuInfo? gpu)
    {
        var attributes = new Dictionary<string, IReadOnlyDictionary<string, object?>>
        {
            ["network_address"] = new Dictionary<string, object?>
            {
                ["addresses"] = networkAddresses
            },
            ["active_display"] = new Dictionary<string, object?>
            {
                ["displays"] = displays
            },
            ["event_log_errors_recent"] = new Dictionary<string, object?>
            {
                ["window_minutes"] = 60,
                ["events"] = recentErrors
            },
            ["last_shutdown_reason"] = new Dictionary<string, object?>
            {
                ["reason"] = lastShutdown.Reason,
                ["event_id"] = lastShutdown.EventId,
                ["created_at"] = lastShutdown.CreatedAt,
                ["message"] = lastShutdown.Message
            }
        };

        if (lastWake is not null)
        {
            attributes["last_wake_reason"] = new Dictionary<string, object?>
            {
                ["source"] = lastWake.Source,
                ["kind"] = lastWake.Kind,
                ["created_at"] = lastWake.CreatedAt,
                ["duration_seconds"] = lastWake.DurationSeconds,
                ["sleep_entered"] = lastWake.SleepEntered,
                ["detail"] = lastWake.Detail
            };
        }

        // No execution state means this role does not report the sensor (see Read).
        if (executionState is { } state)
        {
            attributes["sleep_blocked"] = new Dictionary<string, object?>
            {
                ["system_required"] = (state & EsSystemRequired) != 0,
                ["display_required"] = (state & EsDisplayRequired) != 0,
                ["away_mode_required"] = (state & EsAwayModeRequired) != 0,
                // The first holder that actually keeps the machine awake, for a notification
                // that does not want to walk the list.
                ["primary_blocker"] = powerRequests.FirstOrDefault(request => request.Blocking)?.Name ?? string.Empty,
                ["blockers"] = powerRequests
            };
        }

        if (cameraApps is not null)
        {
            attributes["camera_in_use"] = new Dictionary<string, object?>
            {
                ["apps"] = cameraApps
            };
        }

        if (gpu is not null)
        {
            attributes["gpu_usage"] = new Dictionary<string, object?>
            {
                ["engines"] = gpu.Engines,
                ["npu_usage"] = gpu.NpuUsage,
                ["memory_dedicated_mb"] = gpu.MemoryDedicatedMb,
                ["memory_shared_mb"] = gpu.MemorySharedMb,
                ["adapters"] = gpu.Adapters
            };
        }

        if (microphoneApps is not null)
        {
            attributes["microphone_in_use"] = new Dictionary<string, object?>
            {
                ["apps"] = microphoneApps
            };
        }

        return attributes;
    }

    /// <summary>
    /// Reads a single custom sensor value for the settings "Test value" button.
    /// Process / service / disk sensors need no system metrics, so they skip the
    /// full Read() (which would scan the event log etc. — seconds of work).
    /// Only built-in-attribute sensors fall back to a full snapshot.
    /// </summary>
    public static object? TestCustomSensorValue(CustomSensorDefinition sensor, FileLog log)
    {
        if (sensor.IsProcessRunning || sensor.IsServiceStatus || sensor.IsDiskFree || sensor.IsAnyCommand)
        {
            var empty = new Dictionary<string, IReadOnlyDictionary<string, object?>>();
            return ReadCustomSensor(sensor, empty).Value;
        }

        using var service = new SystemMetricsService(log, null);
        return service.Read([sensor], serviceRole: false).CustomSensors.FirstOrDefault()?.Value;
    }

    private static IReadOnlyList<CustomSensorState> ReadCustomSensors(
        IReadOnlyList<CustomSensorDefinition> sensors,
        bool serviceRole,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> attributes,
        IReadOnlySet<SensorPollingProfile> dueProfiles,
        IReadOnlyList<CustomSensorState> previousStates)
    {
        var previousById = previousStates.ToDictionary(state => state.Id, StringComparer.OrdinalIgnoreCase);

        var active = sensors
            .Where(sensor => sensor.Enabled && (serviceRole ? sensor.Service : sensor.TrayApp))
            .ToList();

        bool IsDue(CustomSensorDefinition sensor) =>
            dueProfiles.Contains(sensor.EffectivePollingProfile) || !previousById.ContainsKey(sensor.Id);

        // Command sensors can each block up to the command timeout, so run the due ones
        // concurrently — a slow or hung command then delays the snapshot by roughly one
        // timeout instead of the sum of all of them.
        var commandTasks = active
            .Where(sensor => sensor.IsAnyCommand && IsDue(sensor))
            .ToDictionary(
                sensor => sensor.Id,
                sensor => Task.Run(() => ReadCustomSensor(sensor, attributes)),
                StringComparer.OrdinalIgnoreCase);

        return active
            .Select(sensor =>
            {
                if (commandTasks.TryGetValue(sensor.Id, out var task))
                {
                    return task.GetAwaiter().GetResult();
                }

                return IsDue(sensor)
                    ? ReadCustomSensor(sensor, attributes)
                    : previousById[sensor.Id];
            })
            .ToList();
    }

    private static CustomSensorState ReadCustomSensor(
        CustomSensorDefinition sensor,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> attributes)
    {
        try
        {
            if (sensor.IsProcessRunning)
            {
                var processName = Path.GetFileNameWithoutExtension(sensor.Parameter.Trim());
                return new CustomSensorState(sensor.Id, Process.GetProcessesByName(processName).Length > 0);
            }

            if (sensor.IsServiceStatus)
            {
                using var controller = new ServiceController(sensor.Parameter.Trim());
                return new CustomSensorState(sensor.Id, controller.Status.ToString().ToLowerInvariant());
            }

            if (sensor.IsDiskFree)
            {
                var root = NormalizeDriveRoot(sensor.Parameter);
                var drive = new DriveInfo(root);
                if (!drive.IsReady)
                {
                    return new CustomSensorState(sensor.Id, null);
                }

                return new CustomSensorState(sensor.Id, Math.Round(drive.AvailableFreeSpace / 1024d / 1024d / 1024d, 1));
            }

            if (sensor.IsBuiltInAttribute)
            {
                return new CustomSensorState(sensor.Id, ReadBuiltInAttributeValue(sensor.Parameter, attributes));
            }

            if (sensor.IsAnyCommand)
            {
                return new CustomSensorState(sensor.Id, ReadCommandSensorValue(sensor));
            }
        }
        catch
        {
            return new CustomSensorState(sensor.Id, null);
        }

        return new CustomSensorState(sensor.Id, null);
    }

    // A command sensor runs a program (or PowerShell / pwsh command / .ps1) and uses its
    // captured stdout as the value. The command is defined by the user; Home Assistant
    // only reads the resulting value. Runs with a bounded wait so a hung command cannot
    // stall the polling thread.
    private const int CommandSensorTimeoutMs = 10_000;

    private static object? ReadCommandSensorValue(CustomSensorDefinition sensor)
    {
        var parameter = sensor.Parameter?.Trim() ?? string.Empty;
        if (parameter.Length == 0)
        {
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            UseShellExecute = false
        };

        if (sensor.IsCommandPowerShell || sensor.IsCommandPwsh)
        {
            startInfo.FileName = sensor.IsCommandPwsh ? "pwsh.exe" : "powershell.exe";
            var isScriptFile = parameter.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);
            startInfo.Arguments = isScriptFile
                ? $"-NoProfile -ExecutionPolicy Bypass -File \"{parameter}\""
                : $"-NoProfile -ExecutionPolicy Bypass -Command \"{parameter}\"";
        }
        else
        {
            var (fileName, arguments) = SystemCommandService.SplitProcessCommand(parameter);
            startInfo.FileName = fileName;
            startInfo.Arguments = arguments;
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        // Drain both pipes before waiting: if a command writes a lot to stdout or stderr,
        // an unread pipe fills up and blocks the child, which would then hit the timeout
        // and discard otherwise-valid output.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(CommandSensorTimeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignore — the process may already be exiting.
            }

            return null;
        }

        var output = stdoutTask.GetAwaiter().GetResult();
        _ = stderrTask.GetAwaiter().GetResult();
        return ExtractFirstLine(output);
    }

    private static object? ExtractFirstLine(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            // Publish a numeric value when the output is a plain number (invariant culture),
            // so a unit + measurement state class produces a proper numeric HA sensor.
            if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
            {
                return longValue;
            }

            // Guard against NaN / Infinity: double.TryParse accepts "NaN"/"Infinity",
            // which System.Text.Json cannot serialize by default — keep those as strings.
            if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue)
                && !double.IsNaN(doubleValue)
                && !double.IsInfinity(doubleValue))
            {
                return doubleValue;
            }

            return trimmed.Length <= 255 ? trimmed : trimmed[..255];
        }

        return null;
    }

    private static object? ReadBuiltInAttributeValue(
        string parameter,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> attributes)
    {
        var path = parameter.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var element = JsonSerializer.SerializeToElement(attributes, AttributeJsonOptions);
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryApplyAttributePathPart(ref element, part))
            {
                return null;
            }
        }

        return ToSensorValue(element);
    }

    private static bool TryApplyAttributePathPart(ref JsonElement element, string part)
    {
        var bracketIndex = part.IndexOf('[', StringComparison.Ordinal);
        var propertyName = bracketIndex >= 0 ? part[..bracketIndex] : part;

        if (!string.IsNullOrWhiteSpace(propertyName))
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty(propertyName, out var property))
            {
                return false;
            }

            element = property;
        }

        while (bracketIndex >= 0)
        {
            var bracketEnd = part.IndexOf(']', bracketIndex + 1);
            if (bracketEnd < 0 ||
                !int.TryParse(part[(bracketIndex + 1)..bracketEnd], out var index) ||
                index < 0 ||
                element.ValueKind != JsonValueKind.Array ||
                element.GetArrayLength() <= index)
            {
                return false;
            }

            element = element[index];
            bracketIndex = part.IndexOf('[', bracketEnd + 1);
        }

        return true;
    }

    private static object? ToSensorValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => LimitState(element.GetString() ?? string.Empty),
            JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
            JsonValueKind.Number when element.TryGetDouble(out var doubleValue) => doubleValue,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.Array or JsonValueKind.Object => LimitState(element.GetRawText()),
            _ => null
        };
    }

    private static string NormalizeDriveRoot(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 1 && char.IsLetter(trimmed[0]))
        {
            return $"{trimmed}:\\";
        }

        if (trimmed.Length == 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':')
        {
            return $"{trimmed}\\";
        }

        return trimmed;
    }

    private static bool RegistryKeyExists(RegistryHive hive, string path)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(path);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool RegistryValueExists(RegistryHive hive, string path, string valueName)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(path);
            return key?.GetValue(valueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static string LimitState(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= 255 ? trimmed : $"{trimmed[..252]}...";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(int informationLevel, IntPtr inputBuffer, uint inputBufferLength, out uint outputBuffer, uint outputBufferLength);

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int count);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetLastInputInfo(ref LastInputInfo lastInputInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr server,
        uint sessionId,
        WtsInfoClass infoClass,
        out IntPtr buffer,
        out uint bytesReturned);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(
        IntPtr server,
        uint reserved,
        uint version,
        out IntPtr sessions,
        out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;

        public ulong ToUInt64()
        {
            return ((ulong)HighDateTime << 32) | LowDateTime;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    private enum WtsInfoClass
    {
        UserName = 5,
        DomainName = 7,
        ConnectState = 8,
        ClientProtocolType = 16
    }

    private enum WtsConnectState
    {
        Active = 0,
        Connected = 1,
        ConnectQuery = 2,
        Shadow = 3,
        Disconnected = 4,
        Idle = 5,
        Listen = 6,
        Reset = 7,
        Down = 8,
        Init = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public uint SessionId;
        public IntPtr WinStationName;
        public WtsConnectState State;
    }

    private const uint DesktopSwitchDesktop = 0x0100;
}

internal sealed record SystemMetricsMessage(
    [property: JsonPropertyName("cpu_usage")] double CpuUsage,
    [property: JsonPropertyName("memory_usage")] double MemoryUsage,
    [property: JsonPropertyName("memory_available_mb")] long MemoryAvailableMb,
    [property: JsonPropertyName("system_drive_free_percent")] double SystemDriveFreePercent,
    [property: JsonPropertyName("system_drive_free_gb")] double SystemDriveFreeGb,
    [property: JsonPropertyName("uptime_seconds")] long UptimeSeconds,
    [property: JsonPropertyName("active_window")] string? ActiveWindow,
    [property: JsonPropertyName("active_process")] string? ActiveProcess,
    [property: JsonPropertyName("foreground_app_title")] string? ForegroundAppTitle,
    [property: JsonPropertyName("volume")] int? Volume,
    [property: JsonPropertyName("muted")] bool? Muted,
    [property: JsonPropertyName("audio_output_device")] string? AudioOutputDevice,
    [property: JsonPropertyName("microphone_muted")] bool? MicrophoneMuted,
    [property: JsonPropertyName("battery_level")] int? BatteryLevel,
    [property: JsonPropertyName("power_status")] string PowerStatus,
    [property: JsonPropertyName("battery_time_remaining")] long? BatteryTimeRemaining,
    [property: JsonPropertyName("monitor_power_state")] string? MonitorPowerState,
    [property: JsonPropertyName("active_display")] string? ActiveDisplay,
    [property: JsonPropertyName("network_address")] string NetworkAddress,
    [property: JsonPropertyName("vpn_connected")] bool VpnConnected,
    [property: JsonPropertyName("wifi_ssid")] string WifiSsid,
    [property: JsonPropertyName("wifi_signal")] int? WifiSignal,
    [property: JsonPropertyName("idle_time_seconds")] long? IdleTimeSeconds,
    [property: JsonPropertyName("session_locked")] bool? SessionLocked,
    [property: JsonPropertyName("user_present")] bool? UserPresent,
    [property: JsonPropertyName("clipboard_text_available")] bool? ClipboardTextAvailable,
    [property: JsonPropertyName("camera_in_use")] bool? CameraInUse,
    [property: JsonPropertyName("microphone_in_use")] bool? MicrophoneInUse,
    [property: JsonPropertyName("gpu_usage")] double? GpuUsage,
    [property: JsonPropertyName("session_state")] string SessionState,
    [property: JsonPropertyName("logged_in_user")] string LoggedInUser,
    [property: JsonPropertyName("logged_in_users")] int LoggedInUsers,
    [property: JsonPropertyName("rdp_sessions")] int RdpSessions,
    [property: JsonPropertyName("pending_reboot")] bool PendingReboot,
    [property: JsonPropertyName("sleep_blocked")] bool? SleepBlocked,
    [property: JsonPropertyName("windows_update_pending")] bool WindowsUpdatePending,
    [property: JsonPropertyName("bluetooth_enabled")] bool BluetoothEnabled,
    [property: JsonPropertyName("event_log_errors_recent")] int EventLogErrorsRecent,
    [property: JsonPropertyName("last_shutdown_reason")] string LastShutdownReason,
    [property: JsonPropertyName("last_wake_reason")] string? LastWakeReason,
    [property: JsonPropertyName("boot_time")] DateTimeOffset BootTime,
    [property: JsonPropertyName("custom_sensors")] IReadOnlyList<CustomSensorState> CustomSensors,
    [property: JsonPropertyName("attributes")] IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Attributes,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt);

internal sealed record NetworkAddressInfo(
    [property: JsonPropertyName("adapter")] string Adapter,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("address")] string Address);

internal sealed record DisplayInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("primary")] bool Primary,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y);

internal sealed record EventLogErrorInfo(
    [property: JsonPropertyName("log")] string Log,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("event_id")] int EventId,
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("created_at")] DateTime CreatedAt);

internal sealed record PowerRequestInfo(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("blocking")] bool Blocking);

internal sealed record GpuInfo(
    double Usage,
    IReadOnlyDictionary<string, double> Engines,
    double? NpuUsage,
    long? MemoryDedicatedMb,
    long? MemorySharedMb,
    IReadOnlyList<GpuAdapterInfo> Adapters);

internal sealed record GpuAdapterInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("memory_mb")] long? MemoryMb);

internal sealed record WakeInfo(
    string Summary,
    string Source,
    string Kind,
    DateTime? CreatedAt,
    long? DurationSeconds,
    bool? SleepEntered,
    string Detail)
{
    public static WakeInfo None { get; } = new(string.Empty, string.Empty, string.Empty, null, null, null, string.Empty);
}

internal sealed record ShutdownInfo(
    string Summary,
    string Reason,
    DateTime? CreatedAt,
    int EventId,
    string Message);
