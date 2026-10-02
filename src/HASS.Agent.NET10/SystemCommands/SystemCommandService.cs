using System.Runtime.InteropServices;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.Media;

namespace HASS.Agent.Companion.SystemCommands;

internal sealed class SystemCommandService : IDisposable
{
    private const int VolumeStep = 5;
    private const uint WmSysCommand = 0x0112;
    private const uint ScMonitorPower = 0xF170;
    private const int MonitorOff = 2;
    private const uint SmtoAbortIfHung = 0x0002;
    private static readonly IntPtr HwndBroadcast = new(0xFFFF);

    private readonly FileLog _log;
    private readonly AudioEndpointService _audioEndpointService;
    private readonly bool _ownsAudioEndpoint;

    // In the tray app the audio endpoint is shared (passed in) so all WASAPI COM
    // access goes through one instance; the service role passes null and owns its own.
    public SystemCommandService(FileLog log, AudioEndpointService? audioEndpointService = null)
    {
        _log = log;
        _audioEndpointService = audioEndpointService ?? new AudioEndpointService(log);
        _ownsAudioEndpoint = audioEndpointService is null;
    }

    public Task HandleCommandAsync(SystemCommandMessage message)
    {
        if (message.RestartCancel)
        {
            _log.Info("Executing system command: restart_cancel");
            CancelShutdownOrRestart();
            return Task.CompletedTask;
        }

        var command = message.Command?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(command))
        {
            return Task.CompletedTask;
        }

        _log.Info($"Executing system command: {command}");

        switch (command)
        {
            case "lock":
                Lock();
                break;
            case "sleep":
                Sleep();
                break;
            case "hibernate":
                Hibernate();
                break;
            case "logoff":
                LogOff();
                break;
            case "monitor_off":
                TurnMonitorOff();
                break;
            case "volume_up":
                ChangeVolume(VolumeStep);
                break;
            case "volume_down":
                ChangeVolume(-VolumeStep);
                break;
            case "toggle_mute":
                ToggleMute();
                break;
            case "shutdown":
                Shutdown(message.Force, message.Time, message.Comment);
                break;
            case "restart":
                Restart(message.Force, message.Time, message.Comment);
                break;
            default:
                _log.Warning($"Unsupported system command received: {command}");
                break;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Runs a user-defined custom command: a program with arguments, a Windows
    /// PowerShell / pwsh inline command or .ps1 script, key presses, or an address to
    /// open. The command is chosen from the user's own list in settings — Home Assistant
    /// only triggers it by id.
    /// </summary>
    public async Task RunCustomCommandAsync(CustomCommandDefinition command)
    {
        _log.Info($"Executing custom command '{command.Name}' ({command.Type}).");
        var result = await ExecuteCustomCommandAsync(command);
        if (!result.Ok)
        {
            _log.Warning($"Custom command '{command.Name}': {Describe(command, result)}");
        }
    }

    /// <summary>
    /// Shows an address in a window of the tray app (address, logical size); set by the tray
    /// app, which owns the UI thread. Null in the service, which has no desktop to show it on.
    /// </summary>
    public static Func<string, System.Drawing.Size, bool>? WebViewHandler { get; set; }

    /// <summary>
    /// Runs the command and says how it went. Also behind the Test button of the editor
    /// in settings, which shows the outcome in the user's language.
    /// </summary>
    public static async Task<CustomCommandResult> ExecuteCustomCommandAsync(CustomCommandDefinition command)
    {
        try
        {
            if (command.NeedsUserSession && System.Diagnostics.Process.GetCurrentProcess().SessionId == 0)
            {
                return new CustomCommandResult(CustomCommandOutcome.NeedsUserSession);
            }

            if (command.IsKey)
            {
                if (!KeySender.TryParse(command.Command, out var combinations, out var unknownKey))
                {
                    return unknownKey.Length > 0
                        ? new CustomCommandResult(CustomCommandOutcome.UnknownKey, unknownKey)
                        : new CustomCommandResult(CustomCommandOutcome.NoKeys);
                }

                return await KeySender.SendAsync(combinations)
                    ? new CustomCommandResult(CustomCommandOutcome.Done)
                    : new CustomCommandResult(CustomCommandOutcome.InputRefused);
            }

            if (command.IsWebView)
            {
                if (!WebViewOptions.IsWebAddress(command.Command))
                {
                    return new CustomCommandResult(CustomCommandOutcome.InvalidAddress, command.Command);
                }

                var size = WebViewOptions.DefaultWindowSize;
                if (command.Arguments.Length > 0 && !WebViewOptions.TryParseSize(command.Arguments, out size))
                {
                    return new CustomCommandResult(CustomCommandOutcome.InvalidSize, command.Arguments);
                }

                return WebViewHandler?.Invoke(command.Command, size) == true
                    ? new CustomCommandResult(CustomCommandOutcome.Done)
                    : new CustomCommandResult(CustomCommandOutcome.NotStarted);
            }

            if (command.IsUrl && !IsOpenableAddress(command.Command))
            {
                return new CustomCommandResult(CustomCommandOutcome.InvalidAddress, command.Command);
            }

            // An address is opened by the shell, so it lands in the default browser, or in
            // the app registered for the scheme (ms-settings:, steam://, ...).
            var startInfo = command.IsUrl
                ? new System.Diagnostics.ProcessStartInfo { FileName = command.Command, UseShellExecute = true }
                : BuildCustomCommandStartInfo(command);
            using var process = System.Diagnostics.Process.Start(startInfo);

            // The shell may hand an address or a document to a running app without
            // starting a process, so only a program that did not start is a failure.
            return process is null && !command.IsUrl
                ? new CustomCommandResult(CustomCommandOutcome.NotStarted)
                : new CustomCommandResult(CustomCommandOutcome.Done);
        }
        catch (Exception ex)
        {
            return new CustomCommandResult(CustomCommandOutcome.Failed, ex.Message);
        }
    }

    /// <summary>An absolute address with a scheme; file paths belong to the Program type.</summary>
    public static bool IsOpenableAddress(string address)
    {
        return Uri.TryCreate(address, UriKind.Absolute, out var uri) && !uri.IsFile;
    }

    private static string Describe(CustomCommandDefinition command, CustomCommandResult result)
    {
        return result.Outcome switch
        {
            CustomCommandOutcome.NeedsUserSession => $"type '{command.Type}' needs the desktop of a logged-in user; only the tray app can run it.",
            CustomCommandOutcome.UnknownKey => $"unknown key '{result.Detail}'.",
            CustomCommandOutcome.NoKeys => "no keys to press.",
            CustomCommandOutcome.InputRefused => "Windows did not accept the key presses (the active window runs elevated, or the desktop is locked).",
            CustomCommandOutcome.InvalidAddress => $"'{result.Detail}' is not an address that can be opened (expected something like https://example.com).",
            CustomCommandOutcome.InvalidSize => $"'{result.Detail}' is not a window size (expected something like 1024x720).",
            CustomCommandOutcome.NotStarted => "could not be started.",
            _ => $"failed: {result.Detail}"
        };
    }

    private static System.Diagnostics.ProcessStartInfo BuildCustomCommandStartInfo(CustomCommandDefinition command)
    {
        if (command.IsPowerShell || command.IsPwsh)
        {
            var shell = command.IsPwsh ? "pwsh.exe" : "powershell.exe";
            var isScriptFile = command.Command.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);
            var arguments = isScriptFile
                ? $"-NoProfile -ExecutionPolicy Bypass -File \"{command.Command}\" {command.Arguments}".Trim()
                : $"-NoProfile -ExecutionPolicy Bypass -Command \"{command.Command}\"";

            return new System.Diagnostics.ProcessStartInfo
            {
                FileName = shell,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false
            };
        }

        // process: launch a program. The user may type just the executable and put its
        // arguments in the Arguments field, or paste a whole command line
        // ("taskkill /F /IM x.exe /T") into the command field. Split the executable from
        // any inline arguments so Windows does not look for a file literally named after
        // the whole line. ShellExecute resolves PATH and lets GUI apps show in the user
        // session when run from the tray app.
        var (fileName, inlineArguments) = SplitProcessCommand(command.Command);
        var processArguments = inlineArguments;
        if (!string.IsNullOrWhiteSpace(command.Arguments))
        {
            processArguments = string.IsNullOrWhiteSpace(inlineArguments)
                ? command.Arguments
                : $"{inlineArguments} {command.Arguments}";
        }

        return new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            Arguments = processArguments,
            UseShellExecute = true
        };
    }

    // Splits "C:\app.exe --flag" or "taskkill /F /IM x /T" into the executable and its
    // inline arguments. A quoted path, or an unquoted path that exists as-is, is kept
    // whole so paths containing spaces still work; otherwise the first whitespace-
    // delimited token is treated as the executable.
    internal static (string FileName, string Arguments) SplitProcessCommand(string command)
    {
        var trimmed = (command ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return (trimmed, string.Empty);
        }

        if (trimmed[0] == '"')
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            return closingQuote > 0
                ? (trimmed[1..closingQuote], trimmed[(closingQuote + 1)..].Trim())
                : (trimmed, string.Empty);
        }

        // A path that exists as-is (e.g. C:\Program Files\app\app.exe with no inline
        // arguments) must stay intact even though it contains spaces.
        if (System.IO.File.Exists(trimmed))
        {
            return (trimmed, string.Empty);
        }

        var firstSpace = trimmed.IndexOf(' ');
        return firstSpace < 0
            ? (trimmed, string.Empty)
            : (trimmed[..firstSpace], trimmed[(firstSpace + 1)..].Trim());
    }

    public void Dispose()
    {
        if (_ownsAudioEndpoint)
        {
            _audioEndpointService.Dispose();
        }
    }

    private void Lock()
    {
        if (!LockWorkStation())
        {
            _log.Warning("Unable to lock Windows workstation.");
        }
    }

    private void Sleep()
    {
        if (!Application.SetSuspendState(PowerState.Suspend, force: false, disableWakeEvent: false))
        {
            _log.Warning("Unable to suspend Windows.");
        }
    }

    /// <summary>
    /// Wakes the displays the way a user would: the smallest mouse movement, there and
    /// back. (The monitor-power message that switches them off is not honoured for "on"
    /// by current Windows versions.)
    /// </summary>
    public static void WakeMonitor()
    {
        const uint mouseEventMove = 0x0001;
        mouse_event(mouseEventMove, 0, 1, 0, UIntPtr.Zero);
        mouse_event(mouseEventMove, 0, -1, 0, UIntPtr.Zero);
    }

    private void Hibernate()
    {
        if (!Application.SetSuspendState(PowerState.Hibernate, force: false, disableWakeEvent: false))
        {
            _log.Warning("Unable to hibernate Windows (hibernation may be turned off on this PC).");
        }
    }

    // Ends the session of the user the tray app runs as; apps get the usual chance to
    // ask about unsaved work.
    private void LogOff()
    {
        if (!ExitWindowsEx(0, 0))
        {
            _log.Warning("Unable to log off the Windows session.");
        }
    }

    /// <summary>Makes the named playback device the default. False when there is no such device.</summary>
    public bool SetAudioOutput(string name) => _audioEndpointService.SetOutputDevice(name);

    /// <summary>Makes the named recording device the default. False when there is no such device.</summary>
    public bool SetAudioInput(string name) => _audioEndpointService.SetInputDevice(name);

    /// <summary>Sets an app's volume and/or mute in the volume mixer. False when the app plays nothing.</summary>
    public bool SetAppVolume(string app, int? volume, bool? muted) => _audioEndpointService.SetSessionVolume(app, volume, muted);

    private void TurnMonitorOff()
    {
        _ = SendMessageTimeout(
            HwndBroadcast,
            WmSysCommand,
            (IntPtr)ScMonitorPower,
            (IntPtr)MonitorOff,
            SmtoAbortIfHung,
            1000,
            out _);
    }

    private void ChangeVolume(int delta)
    {
        _audioEndpointService.SetVolume(_audioEndpointService.GetVolume() + delta);
    }

    private void ToggleMute()
    {
        _audioEndpointService.SetMuted(!_audioEndpointService.GetMuted());
    }

    private void Shutdown(bool force, int seconds, string? comment)
    {
        RunShutdownExe(BuildShutdownArguments("/s", force, seconds, comment));
    }

    private void Restart(bool force, int seconds, string? comment)
    {
        RunShutdownExe(BuildShutdownArguments("/r", force, seconds, comment));
    }

    private void CancelShutdownOrRestart()
    {
        RunShutdownExe("/a");
    }

    private static string BuildShutdownArguments(string mode, bool force, int seconds, string? comment)
    {
        var clampedSeconds = Math.Clamp(seconds, 0, 315_360_000);
        var arguments = force
            ? $"{mode} /f /t {clampedSeconds}"
            : $"{mode} /t {clampedSeconds}";

        var sanitizedComment = SanitizeShutdownComment(comment);
        if (sanitizedComment.Length > 0)
        {
            arguments += $" /c \"{sanitizedComment}\"";
        }

        return arguments;
    }

    private static string SanitizeShutdownComment(string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment))
        {
            return string.Empty;
        }

        var sanitized = comment
            .ReplaceLineEndings(" ")
            .Replace("\"", "'", StringComparison.Ordinal)
            .Trim();

        return sanitized.Length <= 512 ? sanitized : sanitized[..512];
    }

    private void RunShutdownExe(string arguments)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false
            });

            if (process is null)
            {
                _log.Warning("Unable to start shutdown.exe.");
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Unable to execute shutdown.exe: {ex.Message}");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ExitWindowsEx(uint flags, uint reason);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extraInfo);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr result);
}
