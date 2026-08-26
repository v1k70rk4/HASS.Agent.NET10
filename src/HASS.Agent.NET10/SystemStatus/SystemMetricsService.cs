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
using Microsoft.Win32;
using System.Windows.Forms;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.Media;
using HASS.Agent.Companion.SystemCommands;

namespace HASS.Agent.Companion.SystemStatus;

internal sealed class SystemMetricsService : IDisposable
{
    private static readonly TimeSpan WindowsUpdatePendingCacheDuration = TimeSpan.FromMinutes(30);

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
        IReadOnlySet<SensorPollingProfile>? dueProfiles = null)
    {
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

        if (updateHourly)
        {
            _lastRecentErrors = Safe(() => ReadRecentEventLogErrors(TimeSpan.FromHours(1)), _lastRecentErrors);
        }

        if (updateStartup)
        {
            _lastShutdown = Safe(ReadLastShutdownInfo, _lastShutdown);
        }

        var attributes = BuildAttributes(_lastNetworkAddresses, _lastDisplays, _lastRecentErrors, _lastShutdown);
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
            SessionState: session.State,
            LoggedInUser: session.User,
            LoggedInUsers: sessions.LoggedInUsers,
            RdpSessions: sessions.RdpSessions,
            PendingReboot: updateNormal ? Safe(ReadPendingReboot, previous?.PendingReboot ?? false) : previous!.PendingReboot,
            WindowsUpdatePending: updateHourly ? Safe(ReadWindowsUpdatePending, previous?.WindowsUpdatePending ?? false) : previous!.WindowsUpdatePending,
            BluetoothEnabled: updateHourly ? Safe(ReadBluetoothEnabled, previous?.BluetoothEnabled ?? false) : previous!.BluetoothEnabled,
            EventLogErrorsRecent: _lastRecentErrors.Count,
            LastShutdownReason: _lastShutdown.Summary,
            BootTime: updateStartup ? DateTimeOffset.Now.AddMilliseconds(-Environment.TickCount64) : previous!.BootTime,
            CustomSensors: Safe(() => ReadCustomSensors(customSensors ?? [], serviceRole, attributes, profiles, previous?.CustomSensors ?? []), previous?.CustomSensors ?? []),
            Attributes: attributes,
            UpdatedAt: DateTimeOffset.UtcNow);

        _lastMessage = message;
        return message;
    }

    public void Dispose()
    {
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

                var clientProtocol = QuerySessionInt(session.SessionId, WtsInfoClass.ClientProtocolType);
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

    private static int? QuerySessionInt(uint sessionId, WtsInfoClass infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out var buffer, out var bytesReturned))
        {
            return null;
        }

        try
        {
            return bytesReturned >= sizeof(int) ? Marshal.ReadInt32(buffer) : null;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
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

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> BuildAttributes(
        IReadOnlyList<NetworkAddressInfo> networkAddresses,
        IReadOnlyList<DisplayInfo> displays,
        IReadOnlyList<EventLogErrorInfo> recentErrors,
        ShutdownInfo lastShutdown)
    {
        return new Dictionary<string, IReadOnlyDictionary<string, object?>>
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
    [property: JsonPropertyName("session_state")] string SessionState,
    [property: JsonPropertyName("logged_in_user")] string LoggedInUser,
    [property: JsonPropertyName("logged_in_users")] int LoggedInUsers,
    [property: JsonPropertyName("rdp_sessions")] int RdpSessions,
    [property: JsonPropertyName("pending_reboot")] bool PendingReboot,
    [property: JsonPropertyName("windows_update_pending")] bool WindowsUpdatePending,
    [property: JsonPropertyName("bluetooth_enabled")] bool BluetoothEnabled,
    [property: JsonPropertyName("event_log_errors_recent")] int EventLogErrorsRecent,
    [property: JsonPropertyName("last_shutdown_reason")] string LastShutdownReason,
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

internal sealed record ShutdownInfo(
    string Summary,
    string Reason,
    DateTime? CreatedAt,
    int EventId,
    string Message);
