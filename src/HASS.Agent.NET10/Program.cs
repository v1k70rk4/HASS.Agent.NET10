using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Http;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.Media;
using HASS.Agent.Companion.Media.Sendspin;
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
        if (!isFirstInstance && args.Any(arg => string.Equals(arg, "--await-exit", StringComparison.OrdinalIgnoreCase)))
        {
            // Started by the app's own restart: the one before may still be saying goodbye to
            // Music Assistant. Its place is taken when it has left, not given up.
            isFirstInstance = WaitForTheOneBefore(mutex);
        }

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

        // --exit comes from Setup and from the deploy of a build, which start the app again:
        // the Sendspin player then says it is coming back, and keeps its place in its group.
        using var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        var exitRegistration = ThreadPool.RegisterWaitForSingleObject(
            exitEvent,
            (_, _) =>
            {
                AppRestart.Pending = true;
                Application.Exit();
            },
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
        // The Sendspin player of Music Assistant: off unless turned on, tray app only, and only in
        // the session at the PC's screen; it reads its settings again when that session changes.
        var sendspinPlayer = new SendspinPlayerService(settings, log, paths.SettingsFile);
        mediaSessionService.AnnouncementActiveChanged += (_, active) => sendspinPlayer.SetAnnouncementActive(active);
        trayContext.SendspinPlayer = sendspinPlayer;
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
        using var link = new HomeAssistantLink(log);

        // One tray app of the PC talks to Home Assistant: the one in the session that stands for
        // the PC (SessionRole). Another user's app waits, and takes over on a switch of users.
        // Everything that starts or stops the connection runs under linkLock, and reads the
        // role inside it: a save just before a switch must not reconnect an app that has just
        // given the connection up.
        var linked = false;
        var linkLock = new SemaphoreSlim(1, 1);
        var recheckArmed = 0;
        async Task ApplySessionRoleAsync(bool startup)
        {
            await linkLock.WaitAsync();
            try
            {
                var verdict = SessionRole.Evaluate();
                SessionRole.WaitingForAnotherSession = !verdict.Talks;
                trayContext.RefreshSessionRole();
                if (verdict.Provisional)
                {
                    // Decided by which apps run (the console's user has none, or the sessions
                    // could not be listed): asked again, the app at the console may be starting.
                    ArmRoleRecheck();
                }

                if (!verdict.Talks)
                {
                    if (startup || linked)
                    {
                        log.Info("Another user's session stands for this PC; its app talks to Home Assistant, this one waits.");
                    }

                    if (linked)
                    {
                        linked = false;
                        await mqttService.StopAsync();
                        await localApi.StopAsync();
                        // Stopped: the app that takes over may connect now.
                        link.LetGo();
                    }

                    return;
                }

                if (linked)
                {
                    return;
                }

                linked = true;
                if (!startup)
                {
                    log.Info("This session stands for the PC now: connecting to Home Assistant.");
                    // The app that talked until now may have saved other connection settings.
                    SettingsStore.ReloadInto(paths, settings, log);
                }

                // The app that talked until now is told to ask again (it may not know yet that
                // this session stands for the PC), and this one waits for it to stop and let
                // go, so the broker never sees the two at once.
                link.Signal();
                if (!SessionRole.MayConnect(verdict, turnTaken: await Task.Run(link.Take)))
                {
                    // A claim made on which apps run (the sessions unknown), and the turn not
                    // this app's (another holds it, or there is no telling): this one waits and
                    // asks again later.
                    log.Info("Another app of this PC may hold the connection; this one waits.");
                    linked = false;
                    SessionRole.WaitingForAnotherSession = true;
                    trayContext.RefreshSessionRole();
                    return;
                }

                mqttService.Start();
                await StartLocalApiAsync(startup);
            }
            finally
            {
                linkLock.Release();
            }
        }

        // Caller holds linkLock. At a handover the port may still be the other session's for a
        // moment: it is tried again, as long as this session is the one that talks.
        async Task StartLocalApiAsync(bool startup)
        {
            if (!settings.LocalApiEnabled)
            {
                if (startup)
                {
                    log.Info("Local API is off.");
                }

                return;
            }

            try
            {
                await localApi.StartAsync();
                log.Info($"Local API listening on {settings.ListenUrl}.");
            }
            catch (Exception ex)
            {
                // At startup as well: the app of a user who just left may hold the port for a
                // moment. The retries report the failure when the last of them fails too.
                log.Warning($"Unable to start the Local API yet, trying again: {ex.Message}");
                _ = Task.Run(RetryLocalApiAsync);
            }
        }

        async Task RetryLocalApiAsync()
        {
            for (var attempt = 1; attempt <= 12; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                await linkLock.WaitAsync();
                try
                {
                    if (!linked || localApi.IsRunning || !settings.LocalApiEnabled)
                    {
                        return;
                    }

                    try
                    {
                        await localApi.StartAsync();
                        log.Info($"Local API listening on {settings.ListenUrl}.");
                        return;
                    }
                    catch (Exception ex) when (attempt < 12)
                    {
                        log.Debug($"Local API still not available: {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        log.Error(ex, "Unable to start Local API.");
                        trayContext.ShowStartupError(ex);
                        return;
                    }
                }
                finally
                {
                    linkLock.Release();
                }
            }
        }

        void ArmRoleRecheck()
        {
            if (Interlocked.Exchange(ref recheckArmed, 1) == 1)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
                Interlocked.Exchange(ref recheckArmed, 0);
                try
                {
                    await ApplySessionRoleAsync(startup: false);
                }
                catch (Exception ex)
                {
                    log.Error(ex, "Unable to ask again which session stands for the PC.");
                }
            });
        }

        // Event-driven sensor pushes: interactive state changes publish immediately
        // instead of waiting for the next poll (monitor power, session lock, AC/battery).
        monitorPowerStateService.StateChanged += (_, _) => mqttService.TriggerPushUpdate();
        link.RoleChangeSignaled += (_, _) => _ = Task.Run(async () =>
        {
            try
            {
                await ApplySessionRoleAsync(startup: false);
            }
            catch (Exception ex)
            {
                log.Error(ex, "Unable to ask again which session stands for the PC.");
            }
        });
        sessionPowerWatcher.StateChanged += (_, _) =>
        {
            mqttService.TriggerPushUpdate();
            // A user switched, signed in or out: the session that stands for the PC may have changed.
            _ = Task.Run(async () =>
            {
                try
                {
                    await ApplySessionRoleAsync(startup: false);
                }
                catch (Exception ex)
                {
                    log.Error(ex, "Unable to follow the change of sessions.");
                }
            });
        };
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
            _ = Task.Run(() => ApplySendspinAsync(sendspinPlayer, log));
            _ = Task.Run(async () =>
            {
                await linkLock.WaitAsync();
                try
                {
                    if (!linked)
                    {
                        // The saved file reaches the app that talks when it starts next.
                        log.Info("This session's app is waiting; the saved settings reach the connected one when it starts next.");
                        return;
                    }

                    await mqttService.RestartAsync();
                    try
                    {
                        await localApi.ApplySettingsAsync();
                    }
                    catch (Exception ex)
                    {
                        log.Error(ex, "Unable to start Local API.");
                        trayContext.ShowStartupError(ex);
                    }
                }
                finally
                {
                    linkLock.Release();
                }
            });
        };
        trayContext.DiscoveryRepublishHandler = mqttService.RepublishDiscoveryAsync;
        trayContext.HaTokenProbe = () => mqttService.HaTokenStatus;
        trayContext.HaUserProvisioner = mqttService.ProvisionHaUserAsync;
        trayContext.UpdateStateHandler = mqttService.ReportUpdateStateAsync;

        // Detect a completed update: the previous run was a different version. The record is
        // the PC's (LastRunRecord), which every user can write, a kiosk user too; the one in the
        // settings is where versions before it kept it. A running service reports the update
        // and writes the record itself: it has every right, this app may have few.
        // A waiting app (another user's session stands for the PC) leaves this to the app that
        // talks, or to the service: a notice queued here would never be sent.
        var lastRun = LastRunRecord.Read() ?? settings.LastRunVersion;
        if (lastRun != settings.SoftwareVersion && SessionRole.ThisSessionTalks())
        {
            // With nothing recorded yet (a first install) there is no update to report, and
            // the service records nothing then: the version is recorded here.
            if (!string.IsNullOrEmpty(lastRun) && CompanionServiceManager.IsRunning())
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

        // The installer's note for the service about the version it replaced, as the versions
        // before 10.12 left it: a file, which nobody reads any more (the note is a registry
        // value now, and the installer leaves it only for a PC with the service).
        try
        {
            File.Delete(Path.Combine(paths.ConfigDirectory, "updated-from"));
        }
        catch (Exception ex)
        {
            log.Warning($"Unable to remove the installer's old update note: {ex.Message}");
        }

        // On a pool thread: the main thread waits for it here, before the message loop runs, and
        // a continuation posted to that loop (the wait for the other app's turn) would never
        // run. The status it refreshes goes through BeginInvoke, and no form is open yet.
        Task.Run(() => ApplySessionRoleAsync(startup: true)).GetAwaiter().GetResult();
        _ = Task.Run(() => ApplySendspinAsync(sendspinPlayer, log));

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

        if (args.Any(arg => string.Equals(arg, "--open-window", StringComparison.OrdinalIgnoreCase)))
        {
            trayContext.OpenMainFormAtStart();
        }

        Application.Run(trayContext);
        exitRegistration.Unregister(null);

        // First: Music Assistant hears the player leave instead of finding it gone. Every step of
        // the leave is bounded (AppRestart): the instance of a restart waits for this one to go,
        // and a goodbye or a stop that hangs must not keep it waiting past that.
        try
        {
            var leaving = sendspinPlayer.DisposeAsync(comingBack: AppRestart.Pending).AsTask();
            if (!leaving.Wait(AppRestart.PlayerLeaveTimeout))
            {
                log.Warning($"Sendspin: the player did not stop within {AppRestart.PlayerLeaveTimeout.TotalSeconds:0} s; leaving without its goodbye.");
            }
        }
        catch (Exception ex)
        {
            log.Warning($"Sendspin: the player did not stop cleanly: {(ex as AggregateException)?.InnerException?.Message ?? ex.Message}");
        }

        try
        {
            var stopping = Task.WhenAll(mqttService.StopAsync(), localApi.StopAsync());
            if (!stopping.Wait(AppRestart.ConnectionLeaveTimeout))
            {
                log.Warning($"The connection did not stop within {AppRestart.ConnectionLeaveTimeout.TotalSeconds:0} s; leaving.");
            }
        }
        catch (Exception ex)
        {
            log.Warning($"The connection did not stop cleanly: {(ex as AggregateException)?.InnerException?.Message ?? ex.Message}");
        }

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

    // The restart's new instance waits for the old one to leave; an old one that died without
    // releasing (the mutex abandoned) has left all the same.
    private static bool WaitForTheOneBefore(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(AppRestart.WaitForTheOneBefore);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    private static async Task ApplySendspinAsync(SendspinPlayerService player, FileLog log)
    {
        try
        {
            await player.ApplySettingsAsync();
        }
        catch (Exception ex)
        {
            log.Error(ex, "Sendspin: unable to apply the settings.");
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
            // A kiosk user the settings are limited away from would only get the app to stop
            // again, once a minute.
            if (!KioskMode.AppliesToCurrentUser() || !SettingsAccess.CurrentUserCanUse(AppPaths.ConfigDirectoryPath))
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
