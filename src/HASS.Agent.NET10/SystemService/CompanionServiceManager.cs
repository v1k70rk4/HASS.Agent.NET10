using System.Diagnostics;
using System.ServiceProcess;
using System.Text;
using System.Windows.Forms;
using HASS.Agent.Companion.Configuration;
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
        if (command is not ("--install-service" or "--uninstall-service" or "--start-service" or "--stop-service" or "--approve-service-commands" or "--settings-access" or "--kiosk"))
        {
            return false;
        }

        var arguments = args.Skip(1).Where(arg => !string.Equals(arg, "--quiet", StringComparison.OrdinalIgnoreCase)).ToList();
        var result = command switch
        {
            "--settings-access" => ChangeSettingsAccess(arguments),
            "--kiosk" => ChangeKioskMode(arguments),
            _ => ExecuteControlCommand(command, args),
        };
        // For whoever started this (the installer, a script): 1 when it did not do what was asked.
        Environment.ExitCode = result.Success ? 0 : 1;
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
            return $"{S("SvcMgr.Installed")}: {S("SvcMgr.Yes")}{Environment.NewLine}{S("SvcMgr.Status")}: {StateText(controller.Status)}{Environment.NewLine}{S("SvcMgr.StartType")}: {StartTypeText(controller.StartType)}";
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

    private static ControlCommandResult ExecuteControlCommand(string command, string[] args)
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
            "--approve-service-commands" => ApproveServiceCommands(ValueAfter(args, "--expect"), migrate: false),
            _ => new ControlCommandResult(false, string.Format(S("SvcMgr.UnknownCommand"), command))
        };
    }

    private static ControlCommandResult Install()
    {
        var executable = Environment.ProcessPath ?? Application.ExecutablePath;
        var binaryPath = $"{Quote(executable)} --service";

        var update = IsInstalled();
        if (update)
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

        // Keeps what was approved; something new waits for the prompt in the app.
        _ = ApproveServiceCommands(expectedPending: null, migrate: update);

        var start = RunSc("start", Quote(ServiceName));
        if (!start.Success)
        {
            return new ControlCommandResult(false, $"{S("SvcMgr.InstalledNotStarted")}{Environment.NewLine}{start.Message}");
        }

        return new ControlCommandResult(true, string.Format(S("SvcMgr.InstalledAndStarted"), AppIdentity.ServiceDisplayName));
    }

    /// <summary>
    /// Writes the service policy. What it approved and the settings still ask for, unchanged,
    /// stays. Something new is added only by the prompt the app shows after the settings are
    /// saved, which passes <paramref name="expectedPending"/>: it is added only when the
    /// settings read here still ask for exactly what the user was shown. The installer and
    /// the service installation pass none, so an update approves nothing new, except once:
    /// <paramref name="migrate"/> on an update from a version without a policy (10.9.0 and
    /// older), where the service ran everything and should keep doing so.
    /// </summary>
    private static ControlCommandResult ApproveServiceCommands(string? expectedPending, bool migrate)
    {
        if (!IsAdministrator())
        {
            return new ControlCommandResult(false, S("SvcMgr.ApprovalNeedsAdmin"));
        }

        try
        {
            var paths = AppPaths.Create();
            using var log = new FileLog(paths.LogFile);
            var settings = SettingsStore.LoadOrCreate(paths, log);
            var current = ServicePolicy.Load(ServicePolicy.DefaultPath);
            ServicePolicy policy;
            if (expectedPending is not null)
            {
                if (!string.Equals(current.PendingKey(settings), expectedPending, StringComparison.Ordinal))
                {
                    log.Warning("Service approval refused: the settings changed after the approval was asked for.");
                    return new ControlCommandResult(false, S("SvcMgr.ApprovalChanged"));
                }

                policy = ServicePolicy.FromSettings(settings);
            }
            else if (migrate && !File.Exists(ServicePolicy.DefaultPath))
            {
                log.Info("Service policy created from the settings of a version without one.");
                policy = ServicePolicy.FromSettings(settings);
            }
            else
            {
                policy = current.KeepOnly(settings);
            }

            if (!ProgramFolderIsProtected(Path.GetDirectoryName(ServicePolicy.DefaultPath)!))
            {
                log.Warning($"The service policy is written next to {Environment.ProcessPath}, a folder users can write to; it protects nothing there.");
            }

            policy.Save(ServicePolicy.DefaultPath);
            log.Info($"Service policy written: {policy.Commands.Count} command(s), {policy.Sensors.Count} sensor(s) approved.");
            return new ControlCommandResult(true, S("SvcMgr.Approved"));
        }
        catch (Exception ex)
        {
            return new ControlCommandResult(false, string.Format(S("SvcMgr.ApprovalFailed"), ex.Message));
        }
    }

    /// <summary>
    /// "--settings-access only USER [USER ...]": the settings for these users only (the
    /// "HASS.Agent Users" group); "everyone": every user of the PC, as before; "open": the same
    /// without recording a choice, for a silent first install where nobody chose, so the app
    /// still asks; "keep": what the folder had, after an update. Needs an administrator.
    /// </summary>
    private static ControlCommandResult ChangeSettingsAccess(IReadOnlyList<string> arguments)
    {
        if (!IsAdministrator())
        {
            return new ControlCommandResult(false, S("Access.NeedsAdmin"));
        }

        var mode = arguments.FirstOrDefault()?.ToLowerInvariant();
        var users = arguments.Skip(1).Where(user => !string.IsNullOrWhiteSpace(user)).ToList();
        var paths = AppPaths.Create();
        using var log = new FileLog(paths.LogFile);
        PrepareLastRunRecord(log);
        try
        {
            switch (mode)
            {
                case "only" when users.Count > 0:
                    SettingsAccess.RestrictTo(paths.ConfigDirectory, users);
                    RememberAccessDecision(paths, log);
                    log.Info($"Settings limited to {SettingsAccess.GroupName}: {string.Join(", ", users)}.");
                    return new ControlCommandResult(true, string.Format(S("Access.Restricted"), string.Join(", ", users)));
                case "everyone":
                    SettingsAccess.OpenToEveryone(paths.ConfigDirectory);
                    RememberAccessDecision(paths, log);
                    log.Info("Settings open to every user of this PC.");
                    return new ControlCommandResult(true, S("Access.Everyone"));
                case "open":
                    SettingsAccess.OpenToEveryone(paths.ConfigDirectory);
                    log.Info("Settings open to every user of this PC until someone chooses in the app.");
                    return new ControlCommandResult(true, S("Access.Everyone"));
                case "keep":
                    SettingsAccess.KeepAsIs(paths.ConfigDirectory);
                    return new ControlCommandResult(true, string.Empty);
                default:
                    return new ControlCommandResult(false, S("Access.Usage"));
            }
        }
        catch (Exception ex)
        {
            log.Warning($"Changing the settings access failed: {ex.Message}");
            return new ControlCommandResult(false, string.Format(S("Access.Failed"), ex.Message));
        }
    }

    /// <summary>
    /// "--kiosk on USER [USER ...]": kiosk mode for these users, the members of the "HASS.Agent
    /// Kiosk" group from then on (KioskMode); "--kiosk off": the full app for every user again,
    /// and the group goes. Needs an administrator.
    /// </summary>
    private static ControlCommandResult ChangeKioskMode(IReadOnlyList<string> arguments)
    {
        if (!IsAdministrator())
        {
            return new ControlCommandResult(false, S("Kiosk.NeedsAdmin"));
        }

        var mode = arguments.FirstOrDefault()?.ToLowerInvariant();
        var users = arguments.Skip(1).Where(user => !string.IsNullOrWhiteSpace(user)).ToList();
        var paths = AppPaths.Create();
        using var log = new FileLog(paths.LogFile);
        var by = $"{Environment.UserDomainName}\\{Environment.UserName}";
        try
        {
            switch (mode)
            {
                case "on" when users.Count > 0:
                {
                    // Every name given must be someone, and no administrator; nothing changes otherwise.
                    // The members come last: until the folder rule and the task are in place,
                    // nobody new is a kiosk user.
                    var (group, kioskUsers) = SettingsAccess.PrepareKioskGroup(users);
                    SettingsAccess.ApplyKiosk(paths.ConfigDirectory, group);
                    PrepareLastRunRecord(log);
                    try
                    {
                        KioskEvents.Register();
                    }
                    catch (Exception ex)
                    {
                        // Kiosk mode works without it; only its entries in the event log, and the
                        // start at once after a close, wait for it.
                        log.Warning($"Unable to register the event log source: {ex.Message}");
                    }

                    if (!KioskWatchdog.Register(Environment.ProcessPath ?? Application.ExecutablePath, group, log))
                    {
                        return new ControlCommandResult(false, S("Kiosk.TaskFailed"));
                    }

                    SettingsAccess.SetKioskMembers(kioskUsers);
                    var names = string.Join(", ", users);
                    log.Info($"Kiosk mode on for {names}.");
                    KioskEvents.Write(KioskEvents.TurnedOn, $"Kiosk mode turned on by {by} for: {names}. Each of them gets it at their next logon.", log);
                    return new ControlCommandResult(true, string.Format(S("Kiosk.On"), names));
                }
                case "off":
                    KioskWatchdog.Remove(log);
                    if (KioskMode.GroupSid() is null)
                    {
                        // Not on (the uninstaller asks either way): the folder keeps its rule.
                        return new ControlCommandResult(true, S("Kiosk.Off"));
                    }

                    // The group goes last: an off that failed half way is tried again in full.
                    SettingsAccess.ApplyKiosk(paths.ConfigDirectory, kioskGroup: null);
                    SettingsAccess.RemoveKioskGroup();
                    log.Info("Kiosk mode off.");
                    KioskEvents.Write(KioskEvents.TurnedOff, $"Kiosk mode turned off by {by}.", log);
                    return new ControlCommandResult(true, S("Kiosk.Off"));
                default:
                    return new ControlCommandResult(false, S("Kiosk.Usage"));
            }
        }
        catch (Exception ex)
        {
            log.Warning($"Changing kiosk mode failed: {ex.Message}");
            return new ControlCommandResult(false, string.Format(S("Kiosk.Failed"), ex.Message));
        }
    }

    // The PC's record of the version that last ran (LastRunRecord). Every install runs
    // --settings-access as administrator, so the record is there from the first start on.
    private static void PrepareLastRunRecord(FileLog log)
    {
        try
        {
            LastRunRecord.Prepare();
        }
        catch (Exception ex)
        {
            log.Warning($"Unable to prepare the record of the version that last ran: {ex.Message}");
        }
    }

    // A choice made at install time or in the app: the app does not ask about it again.
    private static void RememberAccessDecision(AppPaths paths, FileLog log)
    {
        var settings = SettingsStore.LoadOrCreate(paths, log);
        if (!settings.SettingsAccessDecided)
        {
            settings.SettingsAccessDecided = true;
            SettingsStore.Save(paths, settings);
        }
    }

    private static string? ValueAfter(string[] args, string name)
    {
        var index = Array.FindIndex(args, arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    internal static bool IsAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>Whether ordinary users are kept from writing to the folder (Program Files is).</summary>
    private static bool ProgramFolderIsProtected(string folder)
    {
        try
        {
            var rules = new DirectoryInfo(folder).GetAccessControl()
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(System.Security.Principal.SecurityIdentifier));
            var users = new[]
            {
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, null),
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null),
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.WorldSid, null),
            };
            const System.Security.AccessControl.FileSystemRights write =
                System.Security.AccessControl.FileSystemRights.WriteData | System.Security.AccessControl.FileSystemRights.AppendData;
            return !rules.Cast<System.Security.AccessControl.FileSystemAccessRule>().Any(rule =>
                rule.AccessControlType == System.Security.AccessControl.AccessControlType.Allow
                && users.Contains(rule.IdentityReference)
                && (rule.FileSystemRights & write) != 0);
        }
        catch (Exception)
        {
            return false;
        }
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

    // In the app's language; sc.exe's "START_TYPE : 2 AUTO_START" read the same in every one.
    internal static string StateText(ServiceControllerStatus status) => status switch
    {
        ServiceControllerStatus.Running => S("SvcMgr.Running"),
        ServiceControllerStatus.Stopped => S("SvcMgr.Stopped"),
        ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending => S("SvcMgr.StartPending"),
        ServiceControllerStatus.StopPending or ServiceControllerStatus.PausePending => S("SvcMgr.StopPending"),
        ServiceControllerStatus.Paused => S("SvcMgr.Paused"),
        _ => S("SvcMgr.Unknown")
    };

    internal static string StartTypeText(ServiceStartMode mode) => mode switch
    {
        ServiceStartMode.Automatic => S("SvcMgr.Automatic"),
        ServiceStartMode.Manual => S("SvcMgr.Manual"),
        ServiceStartMode.Disabled => S("SvcMgr.Disabled"),
        _ => S("SvcMgr.Unknown")
    };

    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceNotActive = 1062;

    private static ControlCommandResult RunSc(string command, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = SystemTools.InSystem32("sc.exe"),
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
