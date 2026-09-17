using System.Diagnostics;
using System.ServiceProcess;
using System.Text;
using System.Windows.Forms;
using HASS.Agent.Companion.Localization;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.Runtime;

namespace HASS.Agent.Companion.SystemService;

internal static class CompanionServiceManager
{
    public const string ServiceName = AppIdentity.ServiceName;
    private const string DisplayName = AppIdentity.ServiceDisplayName;
    private const string Description = AppIdentity.ServiceDescription;

    private static string S(string key) => Strings.Get(key);

    public static bool TryHandleCommandLine(string[] args)
    {
        if (args.Length == 0)
        {
            return false;
        }

        var command = args[0].Trim().ToLowerInvariant();
        if (command is not ("--install-service" or "--uninstall-service" or "--start-service" or "--stop-service"))
        {
            return false;
        }

        var result = ExecuteControlCommand(command);
        if (!args.Any(arg => string.Equals(arg, "--quiet", StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(
                result.Message,
                AppIdentity.ServiceDisplayName,
                MessageBoxButtons.OK,
                result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        }

        return true;
    }

    public static void RunElevated(string command, FileLog log)
    {
        try
        {
            var executable = Environment.ProcessPath ?? Application.ExecutablePath;
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = command,
                UseShellExecute = true,
                Verb = "runas"
            });
        }
        catch (Exception ex)
        {
            log.Warning($"Unable to start elevated service command '{command}': {ex.Message}");
            MessageBox.Show(ex.Message, AppIdentity.ServiceDisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public static string GetStatusText()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            return $"{S("SvcMgr.Installed")}: {S("SvcMgr.Yes")}{Environment.NewLine}{S("SvcMgr.Status")}: {controller.Status}{Environment.NewLine}{S("SvcMgr.StartType")}: {GetStartModeText()}";
        }
        catch (InvalidOperationException)
        {
            return $"{S("SvcMgr.Installed")}: {S("SvcMgr.No")}";
        }
        catch (Exception ex)
        {
            return string.Format(S("SvcMgr.StatusError"), ex.Message);
        }
    }

    private static ControlCommandResult ExecuteControlCommand(string command)
    {
        if (command == "--install-service" && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return new ControlCommandResult(false, string.Format(S("SvcMgr.OsRequired"), AppIdentity.DisplayName));
        }

        return command switch
        {
            "--install-service" => Install(),
            "--uninstall-service" => Uninstall(),
            "--start-service" => RunSc("start", Quote(ServiceName)),
            "--stop-service" => RunSc("stop", Quote(ServiceName)),
            _ => new ControlCommandResult(false, string.Format(S("SvcMgr.UnknownCommand"), command))
        };
    }

    private static ControlCommandResult Install()
    {
        var executable = Environment.ProcessPath ?? Application.ExecutablePath;
        var binaryPath = $"{Quote(executable)} --service";

        if (IsInstalled())
        {
            TryStopExistingService();

            var delete = RunSc("delete", Quote(ServiceName));
            if (!delete.Success)
            {
                return delete;
            }

            if (!WaitUntilNotInstalled(TimeSpan.FromSeconds(10)))
            {
                return new ControlCommandResult(false, S("SvcMgr.DeletePending"));
            }
        }

        var create = RunSc(
            "create",
            $"{Quote(ServiceName)} binPath= {Quote(binaryPath)} start= auto DisplayName= {Quote(DisplayName)}");

        if (!create.Success)
        {
            return create;
        }

        _ = RunSc("description", $"{Quote(ServiceName)} {Quote(Description)}");
        _ = RunSc("failure", $"{Quote(ServiceName)} reset= 86400 actions= restart/60000/restart/60000/none/0");

        var start = RunSc("start", Quote(ServiceName));
        if (!start.Success)
        {
            return new ControlCommandResult(false, $"{S("SvcMgr.InstalledNotStarted")}{Environment.NewLine}{start.Message}");
        }

        return new ControlCommandResult(true, string.Format(S("SvcMgr.InstalledAndStarted"), AppIdentity.ServiceDisplayName));
    }

    private static ControlCommandResult Uninstall()
    {
        if (!IsInstalled())
        {
            return new ControlCommandResult(true, S("SvcMgr.NotInstalled"));
        }

        _ = RunSc("stop", Quote(ServiceName));
        Thread.Sleep(750);

        return RunSc("delete", Quote(ServiceName));
    }

    public static bool IsInstalled()
    {
        return ServiceController.GetServices().Any(service => service.ServiceName == ServiceName);
    }

    public static bool IsRunning()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            return controller.Status == ServiceControllerStatus.Running;
        }
        catch
        {
            return false;
        }
    }

    private static void TryStopExistingService()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            if (controller.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
            {
                controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                return;
            }

            controller.Stop();
            controller.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
        }
        catch
        {
        }
    }

    private static bool WaitUntilNotInstalled(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!IsInstalled())
            {
                return true;
            }

            Thread.Sleep(500);
        }

        return !IsInstalled();
    }

    private static string GetStartModeText()
    {
        var query = RunSc("qc", Quote(ServiceName));
        if (!query.Success)
        {
            return S("SvcMgr.Unknown");
        }

        var line = query.Message
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(item => item.Contains("START_TYPE", StringComparison.OrdinalIgnoreCase));

        return line?.Trim() ?? S("SvcMgr.Unknown");
    }

    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceNotActive = 1062;

    private static ControlCommandResult RunSc(string command, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"{command} {arguments}",
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                StandardOutputEncoding = ConsoleOutputEncoding.Oem,
                StandardErrorEncoding = ConsoleOutputEncoding.Oem
            });

            if (process is null)
            {
                return new ControlCommandResult(false, S("SvcMgr.ScFailed"));
            }

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            var message = string.Join(
                Environment.NewLine,
                new[] { output.Trim(), error.Trim() }.Where(item => !string.IsNullOrWhiteSpace(item)));

            // Asking for the state the service is already in is not a failure.
            if (command == "start" && process.ExitCode == ErrorServiceAlreadyRunning)
            {
                return new ControlCommandResult(true, S("SvcMgr.AlreadyRunning"));
            }

            if (command == "stop" && process.ExitCode == ErrorServiceNotActive)
            {
                return new ControlCommandResult(true, S("SvcMgr.NotRunning"));
            }

            return new ControlCommandResult(process.ExitCode == 0, string.IsNullOrWhiteSpace(message) ? "OK" : message);
        }
        catch (Exception ex)
        {
            return new ControlCommandResult(false, ex.Message);
        }
    }

    private static string Quote(string value)
    {
        return $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    private sealed record ControlCommandResult(bool Success, string Message);
}
