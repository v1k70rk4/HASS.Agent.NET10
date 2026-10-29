using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Http;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.Media;
using HASS.Agent.Companion.Mqtt;
using HASS.Agent.Companion.Networking;
using HASS.Agent.Companion.Runtime;
using HASS.Agent.Companion.SystemCommands;
using HASS.Agent.Companion.SystemService;
using HASS.Agent.Companion.SystemStatus;
using HASS.Agent.Companion.Localization;
using HASS.Agent.Companion.Tray;
using System.ServiceProcess;
using System.Text.Json;
using System.Windows.Forms;

namespace HASS.Agent.Companion;

internal static class Program
{
    private const string MutexName = "Local\\HASS.Agent.NET10";
    private const string ExitEventName = "Local\\HASS.Agent.NET10.Exit";

    [STAThread]
    private static void Main()
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (args.Any(arg => string.Equals(arg, "--exit", StringComparison.OrdinalIgnoreCase)))
        {
            SignalExistingInstanceToExit();
            return;
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            if (args.Any(arg => string.Equals(arg, "--service", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            InitializeWindowsForms();
            MessageBox.Show(
                $"{AppIdentity.DisplayName} requires Windows 10 version 2004 (build 19041) or newer. Windows 11 is recommended.",
                AppIdentity.DisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        if (args.Any(arg => string.Equals(arg, "--service", StringComparison.OrdinalIgnoreCase)))
        {
            ServiceBase.Run(new CompanionWindowsService());
            return;
        }

        // The kiosk task: at logon, when the app closed or crashed, and once a minute.
        if (args.Any(arg => string.Equals(arg, "--kiosk-start", StringComparison.OrdinalIgnoreCase)))
        {
            StartForKiosk();
            return;
        }

        // The uninstaller: takes back what Windows notifications registered for this user.
        if (args.Any(arg => string.Equals(arg, "--unregister-notifications", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                Tray.ToastPresenter.Unregister();
            }
            catch
            {
                // Nothing was registered, or it is not ours to remove; either way, done.
            }

            return;
        }

        InitializeWindowsForms();

        // Load language early so elevated service commands show localized messages
        LoadLanguageEarly();

        if (CompanionServiceManager.TryHandleCommandLine(args))
        {
            return;
        }

        // A kiosk user gets the tray app with nothing to open or close (KioskMode).
        var kiosk = KioskMode.AppliesToCurrentUser();

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // --quiet: relaunch watchdogs may race an already-restarted instance.
            // -ToastActivated: Windows starting the app for a pressed notification button;
            // the running instance has it already.
            // A kiosk user has the app started at logon twice (the kiosk task, and their own
            // autostart if they had one) and has nothing to do with the note.
            if (!kiosk && !args.Any(arg =>
                    string.Equals(arg, "--quiet", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "-ToastActivated", StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(
                    string.Format(Localization.Strings.Get("App.AlreadyRunning"), AppIdentity.DisplayName),
                    AppIdentity.DisplayName,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }

            return;
        }

        using var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        var exitRegistration = ThreadPool.RegisterWaitForSingleObject(
            exitEvent,
            (_, _) => Application.Exit(),
            state: null,
            Timeout.InfiniteTimeSpan,
            executeOnlyOnce: false);

        // Before AppPaths.Create, which already reads and copies files there.
        if (!SettingsAccess.CurrentUserCanUse(AppPaths.ConfigDirectoryPath))
        {
            // The settings of this PC are limited to other users: say so once, then stay out.
            TellOnceAboutLimitedAccess();
            return;
        }

        var paths = AppPaths.Create();
        if (kiosk)
        {
            // A kiosk user can only read the settings folder, the shared log there included:
            // their app logs for them alone.
            paths = paths with
            {
                LogFile = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    AppIdentity.ConfigurationDirectoryName,
                    Path.GetFileName(paths.LogFile))
            };
        }

        using var log = new FileLog(paths.LogFile);
        log.Info($"Starting {AppIdentity.DisplayName}{(kiosk ? " in kiosk mode" : string.Empty)}.");
        var sessionEnding = false;
        if (kiosk)
        {
            var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
            if (KioskRanInThisSession())
            {
                KioskEvents.Write(KioskEvents.CameBack, $"The kiosk tray app of {user} was started again after it closed.", log, System.Diagnostics.EventLogEntryType.Warning);
            }
            else
            {
                KioskEvents.Write(KioskEvents.Started, $"The kiosk tray app started for {user}.", log);
            }

            // Closed by a logoff or a shutdown, the app is not to be started again.
            Microsoft.Win32.SystemEvents.SessionEnding += (_, _) => sessionEnding = true;
        }

        // A translator's own language files, before the settings name a language: for this
        // Windows user only, never for the service.
        var userLanguages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppIdentity.ConfigurationDirectoryName, "languages");
        foreach (var problem in Localization.Strings.LoadUserLanguages(userLanguages))
        {
            log.Warning(problem);
        }

        CompanionSettings settings;
        try
        {
            settings = SettingsStore.LoadOrCreate(paths, log);
        }
        catch (Exception ex)
        {
            log.Error(ex, "Unable to load settings.");
            MessageBox.Show(
                string.Format(Localization.Strings.Get("App.SettingsNotLoaded"), ex.Message),
                AppIdentity.DisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        Localization.Strings.Language = settings.Language;
        Localization.Strings.HaLanguage = settings.HaLanguage;

        // If an update ran, its one-shot scheduled tasks have already done their job by the
        // time we are up again. Clear any left behind (older versions never removed them).
        _ = Task.Run(() => DetachedUpdateLauncher.CleanUpLeftoverTasks(log));
        // A kiosk user's app is started by the kiosk task, and their settings cannot be saved.
        try
        {
            if (!kiosk)
            {
                if (!settings.AutoStartOnLogin && StartupManager.IsEnabled())
                {
                    settings.AutoStartOnLogin = true;
                    SettingsStore.Save(paths, settings);
                    log.Info("Adopted auto-start setting from registry (installer).");
                }

                StartupManager.SetEnabled(settings.AutoStartOnLogin);
            }
        }
        catch (Exception ex)
        {
            log.Warning($"Unable to update startup registration: {ex.Message}");
        }

        using var trayContext = new TrayApplicationContext(settings, paths, log, kiosk);
        using var monitorPowerStateService = new MonitorPowerStateService();
        using var sessionPowerWatcher = new SessionPowerWatcher();
        using var audioEndpointService = new AudioEndpointService(log);
        using var mediaSessionService = new MediaSessionService(log, audioEndpointService, settings);
        using var systemMetricsService = new SystemMetricsService(log, monitorPowerStateService, audioEndpointService);
        using var systemCommandService = new SystemCommandService(log, audioEndpointService);
        using var mqttService = new MqttCompanionService(
            settings,
            trayContext,
            mediaSessionService,
            systemMetricsService,
            systemCommandService,
            CompanionRuntimeRole.App,
            log);
        using var localApi = new LocalApiServer(settings, trayContext, log);

        // Event-driven sensor pushes: interactive state changes publish immediately
        // instead of waiting for the next poll (monitor power, session lock, AC/battery).
        monitorPowerStateService.StateChanged += (_, _) => mqttService.TriggerPushUpdate();
        sessionPowerWatcher.StateChanged += (_, _) => mqttService.TriggerPushUpdate();
        audioEndpointService.StateChanged += (_, _) => mqttService.TriggerPushUpdate();

        trayContext.NotificationActionRequested += (_, args) =>
        {
            log.Info($"Notification action selected: {args.Action}");
            _ = Task.Run(() => mqttService.PublishNotificationActionAsync(args.Action, args.Input));
        };
        trayContext.HotkeyPressed += (_, hotkey) => _ = Task.Run(() => mqttService.PublishHotkeyAsync(hotkey));
        trayContext.SettingsSaved += (_, _) =>
        {
            log.Info("Settings saved from UI.");
            _ = Task.Run(() => mqttService.RestartAsync());
            _ = Task.Run(async () =>
            {
                try
                {
                    await localApi.ApplySettingsAsync();
                }
                catch (Exception ex)
                {
                    log.Error(ex, "Unable to start Local API.");
                    trayContext.ShowStartupError(ex);
                }
            });
        };
        trayContext.DiscoveryRepublishHandler = mqttService.RepublishDiscoveryAsync;
        trayContext.HaTokenProbe = () => mqttService.HaTokenStatus;
        trayContext.HaUserProvisioner = mqttService.ProvisionHaUserAsync;
        trayContext.UpdateStateHandler = mqttService.ReportUpdateStateAsync;

        if (settings.LocalApiEnabled)
        {
            try
            {
                localApi.StartAsync().GetAwaiter().GetResult();
                log.Info($"Local API listening on {settings.ListenUrl}.");
            }
            catch (Exception ex)
            {
                log.Error(ex, "Unable to start Local API.");
                trayContext.ShowStartupError(ex);
            }
        }
        else
        {
            log.Info("Local API is off.");
        }

        // Detect a completed update: the previous run was a different version. The record is
        // the PC's (LastRunRecord), which every user can write, a kiosk user too; the one in the
        // settings is where versions before it kept it. A running service reports the update
        // and writes the record itself: it has every right, this app may have few.
        var lastRun = LastRunRecord.Read() ?? settings.LastRunVersion;
        if (lastRun != settings.SoftwareVersion)
        {
            if (CompanionServiceManager.IsRunning())
            {
                log.Info($"Updated from {lastRun} to {settings.SoftwareVersion}; the service tells Home Assistant.");
            }
            else
            {
                if (!string.IsNullOrEmpty(lastRun))
                {
                    log.Info($"Updated from {lastRun} to {settings.SoftwareVersion}.");
                    mqttService.NotifyUpdateCompleted(lastRun);
                }

                RecordLastRun(settings, paths, log);
            }
        }

        // The installer's note for the service about the version it replaced. With no
        // service installed nobody else reads it, and it must not sit there until a
        // service is installed some day and reports an update from long ago.
        try
        {
            var installerNote = Path.Combine(paths.ConfigDirectory, "updated-from");
            if (File.Exists(installerNote) && !CompanionServiceManager.IsInstalled())
            {
                File.Delete(installerNote);
            }
        }
        catch (Exception ex)
        {
            log.Warning($"Unable to remove the installer's update note: {ex.Message}");
        }

        mqttService.Start();

        if (!settings.MqttEnabled && !settings.HaApiEnabled)
        {
            trayContext.ShowSetupRequired();
        }

        // Nothing to see for a kiosk user, also after the app came back.
        if (settings.ShowStartupNotification && !kiosk)
        {
            trayContext.ShowNotification(new NotificationPayload
            {
                Title = AppIdentity.DisplayName,
                Message = settings.LocalApiEnabled
                    ? $"Home Assistant URL: {NetworkInfo.GetPreferredLanUrl(settings.Port)}"
                    : Strings.Get("Tray.Running")
            });
        }

        Application.Run(trayContext);
        exitRegistration.Unregister(null);

        mqttService.StopAsync().GetAwaiter().GetResult();
        localApi.StopAsync().GetAwaiter().GetResult();
        log.Info($"Stopped {AppIdentity.DisplayName}.");

        if (kiosk && !sessionEnding)
        {
            // The kiosk task starts the app again on this event, and its start must find the
            // place free: the instance lock goes first, the connection and the port went above.
            mutex.ReleaseMutex();
            mutex.Dispose();
            KioskEvents.Write(KioskEvents.Closed, $"The kiosk tray app of {Environment.UserDomainName}\\{Environment.UserName} was closed.", log, System.Diagnostics.EventLogEntryType.Warning);
        }
    }

    /// <summary>
    /// From the kiosk task: starts the tray app when the user running this is a kiosk user and
    /// it is not running in this session. Started as a process of its own, not the task's, so
    /// the task's time limit and End do not reach it. Anyone else is left alone.
    /// </summary>
    private static void StartForKiosk()
    {
        try
        {
            if (!KioskMode.AppliesToCurrentUser())
            {
                return;
            }

            if (Mutex.TryOpenExisting(MutexName, out var running))
            {
                running.Dispose();
                return;
            }

            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? Application.ExecutablePath,
                Arguments = "--quiet",
                UseShellExecute = false,
            });
        }
        catch
        {
            // The next trigger of the task tries again.
        }
    }

    private const string UserKeyPath = @"Software\HASS.Agent.NET10";

    /// <summary>
    /// Whether the kiosk tray app already ran in this logon session, and marks it for the next
    /// start: a volatile key, which Windows drops when the user logs off.
    /// </summary>
    private static bool KioskRanInThisSession()
    {
        try
        {
            // Not volatile itself: the other values of the app live there, and a volatile key
            // takes no lasting one under it.
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(UserKeyPath);
            using (var existing = key.OpenSubKey("KioskSession"))
            {
                if (existing is not null)
                {
                    return true;
                }
            }

            using var created = key.CreateSubKey("KioskSession", writable: false, Microsoft.Win32.RegistryOptions.Volatile);
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The version that runs now, in the PC's record. Without that record (a build put in
    /// place by hand, with no install and no service to make it) in the settings, as before.
    /// </summary>
    private static void RecordLastRun(CompanionSettings settings, AppPaths paths, FileLog log)
    {
        if (LastRunRecord.TryWrite(settings.SoftwareVersion))
        {
            return;
        }

        settings.LastRunVersion = settings.SoftwareVersion;
        try
        {
            SettingsStore.Save(paths, settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            log.Warning($"The version that ran could not be recorded; the next start reports the update again: {ex.Message}");
        }
    }

    private static void TellOnceAboutLimitedAccess()
    {
        const string keyPath = @"Software\HASS.Agent.NET10";
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(keyPath);
            if (key.GetValue("LimitedAccessNoticeShown") is not null)
            {
                return;
            }

            key.SetValue("LimitedAccessNoticeShown", 1);
        }
        catch (Exception)
        {
            // Without the registry the notice comes every time, which is still better than none.
        }

        MessageBox.Show(
            string.Format(Localization.Strings.Get("Access.NotForYou"), Environment.UserName, SettingsAccess.GroupName),
            AppIdentity.DisplayName,
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private static void SignalExistingInstanceToExit()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ExitEventName, out var exitEvent))
            {
                using (exitEvent)
                {
                    exitEvent.Set();
                }
            }
        }
        catch
        {
            // Best-effort helper for installers and scripts.
        }
    }

    private static void InitializeWindowsForms()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();
    }

    private static void LoadLanguageEarly()
    {
        // Until the settings say otherwise, the language of Windows: a user the settings are
        // limited away from cannot read them, and the note telling them so came in English.
        Localization.Strings.Language = CompanionSettings.DetectSystemLanguage();
        try
        {
            var settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                AppIdentity.ConfigurationDirectoryName,
                "settings.json");

            if (!File.Exists(settingsPath))
            {
                return;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));

            if (doc.RootElement.TryGetProperty("language", out var lang))
            {
                Localization.Strings.Language = lang.GetString() ?? "en";
            }

            if (doc.RootElement.TryGetProperty("haLanguage", out var haLang))
            {
                Localization.Strings.HaLanguage = haLang.GetString() ?? "en";
            }
        }
        catch
        {
            // Settings file may not exist yet or be unreadable — keep defaults.
        }
    }
}
