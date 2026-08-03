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
    /// Runs a user-defined custom command: a program with arguments, or a Windows
    /// PowerShell / pwsh inline command or .ps1 script. The command is chosen from the
    /// user's own list in settings — Home Assistant only triggers it by id.
    /// </summary>
    public Task RunCustomCommandAsync(CustomCommandDefinition command)
    {
        _log.Info($"Executing custom command '{command.Name}' ({command.Type}).");
        try
        {
            var startInfo = BuildCustomCommandStartInfo(command);
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
            {
                _log.Warning($"Custom command '{command.Name}' could not be started.");
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Custom command '{command.Name}' failed: {ex.Message}");
        }

        return Task.CompletedTask;
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

        // process: launch the program (ShellExecute resolves PATH and lets GUI apps
        // show in the user session when run from the tray app).
        return new System.Diagnostics.ProcessStartInfo
        {
            FileName = command.Command,
            Arguments = command.Arguments,
            UseShellExecute = true
        };
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
    private static extern IntPtr SendMessageTimeout(
        IntPtr windowHandle,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeout,
        out IntPtr result);
}
