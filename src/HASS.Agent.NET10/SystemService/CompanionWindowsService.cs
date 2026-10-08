using System.ServiceProcess;
using System.Text.Json;
using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Http;
using HASS.Agent.Companion.Localization;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.Mqtt;
using HASS.Agent.Companion.Runtime;
using HASS.Agent.Companion.SystemCommands;
using HASS.Agent.Companion.SystemStatus;

namespace HASS.Agent.Companion.SystemService;

internal sealed class CompanionWindowsService : ServiceBase
{
    private readonly object _runtimeLock = new();
    private AppPaths? _paths;
    private FileLog? _log;
    private SystemCommandService? _systemCommandService;
    private SystemMetricsService? _systemMetricsService;
    private MqttCompanionService? _mqttService;
    private FileSystemWatcher? _settingsWatcher;
    private FileSystemWatcher? _policyWatcher;
    private System.Threading.Timer? _settingsReloadTimer;
    private string? _updatedFrom;
    private string _runningVersion = string.Empty;
    private string? _runtimeSettings;
    private bool _disposed;

    public CompanionWindowsService()
    {
        ServiceName = CompanionServiceManager.ServiceName;
        CanStop = true;
        CanPauseAndContinue = false;
        AutoLog = true;
    }

    protected override void OnStart(string[] args)
    {
        _paths = AppPaths.Create();
        _log = new FileLog(_paths.LogFile);
        _log.Info($"Starting {AppIdentity.DisplayName} system service.");

        try
        {
            var settings = LoadSettings(_paths, _log);
            ApplyLanguage(settings);
            _updatedFrom = DetectCompletedUpdate(settings);
            _systemCommandService = new SystemCommandService(_log);
            _systemMetricsService = new SystemMetricsService(_log, monitorPowerStateService: null, includeInteractiveMetrics: false);
            _runtimeSettings = WhatTheRuntimeUses(settings);
            StartRuntime(settings);
            StartSettingsWatcher();
        }
        catch (Exception ex)
        {
            _log?.Error(ex, "Unable to start system service.");
            DisposeRuntime();
            throw;
        }
    }

    protected override void OnStop()
    {
        _log?.Info($"Stopping {AppIdentity.DisplayName} system service.");

        try
        {
            StopSettingsWatcher();
            StopRuntime();
        }
        finally
        {
            DisposeRuntime();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposed = true;
            StopSettingsWatcher();
            DisposeRuntime();
        }

        base.Dispose(disposing);
    }

    private void StartSettingsWatcher()
    {
        if (_paths is null || _log is null)
        {
            return;
        }

        _settingsReloadTimer = new System.Threading.Timer(
            _ => ReloadSettings(),
            state: null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);

        _settingsWatcher = new FileSystemWatcher(_paths.ConfigDirectory)
        {
            Filter = Path.GetFileName(_paths.SettingsFile),
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };

        _settingsWatcher.Changed += ScheduleSettingsReload;
        _settingsWatcher.Created += ScheduleSettingsReload;
        _settingsWatcher.Renamed += ScheduleSettingsReload;

        // An approval arrives after the settings were saved (once the user said yes to the
        // prompt), so a new policy reloads the settings too.
        var policyFolder = Path.GetDirectoryName(ServicePolicy.DefaultPath);
        if (policyFolder is not null && Directory.Exists(policyFolder))
        {
            _policyWatcher = new FileSystemWatcher(policyFolder)
            {
                Filter = ServicePolicy.FileName,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            _policyWatcher.Changed += ScheduleSettingsReload;
            _policyWatcher.Created += ScheduleSettingsReload;
            _policyWatcher.Renamed += ScheduleSettingsReload;
            _policyWatcher.Deleted += ScheduleSettingsReload;
        }
    }

    private void ScheduleSettingsReload(object sender, FileSystemEventArgs args)
    {
        if (_disposed)
        {
            return;
        }

        _settingsReloadTimer?.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
    }

    private void ReloadSettings()
    {
        if (_disposed || _paths is null || _log is null)
        {
            return;
        }

        try
        {
            // Timer callbacks can overlap: the comparison and the restart are one step, so a
            // second callback sees what the first one started.
            lock (_runtimeLock)
            {
                var settings = LoadSettings(_paths, _log);
                var uses = WhatTheRuntimeUses(settings);
                if (uses == _runtimeSettings)
                {
                    // The tray app's own bookkeeping (the version it last ran, after an
                    // update): a restart would only drop and rebuild the connection.
                    _log.Debug("Settings file written; nothing the service uses changed.");
                    return;
                }

                _log.Info("Settings changed; reloading system service runtime.");
                ApplyLanguage(settings);
                StopRuntime();
                _runtimeSettings = uses;
                StartRuntime(settings);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Unable to reload system service settings.");
        }
    }

    /// <summary>
    /// The shared settings, with what the service policy does not approve taken away from the
    /// service: anyone who can log on to this PC can write the settings, and this runs as SYSTEM.
    /// </summary>
    private static CompanionSettings LoadSettings(AppPaths paths, FileLog log)
    {
        var settings = SettingsStore.LoadOrCreate(paths, log);
        var restricted = ServicePolicy.Load(ServicePolicy.DefaultPath).Restrict(settings);
        if (restricted.Count > 0)
        {
            log.Warning(
                $"Not run by the service, as no administrator approved them: {string.Join(", ", restricted)}. " +
                "Save the settings in the app and approve the prompt to let the service run them.");
        }

        return settings;
    }

    /// <summary>
    /// What the service's runtime is built from: the settings as loaded (the policy already
    /// applied), without what only the tray app keeps there for itself.
    /// </summary>
    internal static string WhatTheRuntimeUses(CompanionSettings settings)
    {
        var (lastRun, decided, declined) = (settings.LastRunVersion, settings.SettingsAccessDecided, settings.HaApiOwnUserDeclined);
        settings.LastRunVersion = string.Empty;
        settings.SettingsAccessDecided = false;
        settings.HaApiOwnUserDeclined = false;
        try
        {
            return JsonSerializer.Serialize(settings);
        }
        finally
        {
            settings.LastRunVersion = lastRun;
            settings.SettingsAccessDecided = decided;
            settings.HaApiOwnUserDeclined = declined;
        }
    }

    private void StopSettingsWatcher()
    {
        _settingsReloadTimer?.Dispose();
        _settingsReloadTimer = null;

        if (_policyWatcher is not null)
        {
            _policyWatcher.EnableRaisingEvents = false;
            _policyWatcher.Dispose();
            _policyWatcher = null;
        }

        if (_settingsWatcher is not null)
        {
            _settingsWatcher.EnableRaisingEvents = false;
            _settingsWatcher.Dispose();
            _settingsWatcher = null;
        }
    }

    private void StartRuntime(CompanionSettings settings)
    {
        if (_log is null || _systemCommandService is null)
        {
            return;
        }

        _mqttService = new MqttCompanionService(
            settings,
            new NullNotificationSink(),
            mediaSessionService: null,
            _systemMetricsService,
            _systemCommandService,
            CompanionRuntimeRole.Service,
            _log);

        if (_updatedFrom is not null)
        {
            _mqttService.NotifyUpdateCompleted(_updatedFrom);
            _mqttService.UpdateCompletionHandled = RecordServiceVersion;
        }

        _mqttService.Start();
    }

    /// <summary>
    /// The version the service ran as before this start, when it differs from the current
    /// one: an update was installed in between. The tray app reports a finished update when
    /// it starts; on a PC nobody is logged in to there is no tray app, and the service is
    /// the one that can tell Home Assistant the install went through.
    /// The service's last version lives in a file of its own, so the service never has to
    /// write settings.json (the tray app owns that, and a write reloads the runtime).
    /// The new version is only recorded once the notification is dealt with, so a restart
    /// while Home Assistant is unreachable does not lose it.
    /// The installer's own note (the "updated-from" file) comes first: it names the version
    /// it replaced whatever that version was, also one that predates this bookkeeping, or
    /// one installed again after a newer one had already been recorded here.
    /// </summary>
    private string? DetectCompletedUpdate(CompanionSettings settings)
    {
        if (_paths is null)
        {
            return null;
        }

        _runningVersion = settings.SoftwareVersion;
        var versionFile = ServiceVersionFile(_paths);
        try
        {
            var installerNote = UpdatedFromFile(_paths);
            if (File.Exists(installerNote))
            {
                var replaced = File.ReadAllText(installerNote).Trim();
                if (replaced.Length > 0 && replaced != settings.SoftwareVersion)
                {
                    _log?.Info($"Service updated from {replaced} to {settings.SoftwareVersion} (installer).");
                    return replaced;
                }

                File.Delete(installerNote);
            }

            // Before this file existed, the tray app's record is the best there is.
            var previous = File.Exists(versionFile)
                ? File.ReadAllText(versionFile).Trim()
                : settings.LastRunVersion;
            if (previous == settings.SoftwareVersion)
            {
                return null;
            }

            if (string.IsNullOrEmpty(previous))
            {
                // Nothing to compare with: a first install, nothing to report.
                File.WriteAllText(versionFile, settings.SoftwareVersion);
                return null;
            }

            _log?.Info($"Service updated from {previous} to {settings.SoftwareVersion}.");
            return previous;
        }
        catch (Exception ex)
        {
            _log?.Warning($"Unable to track the service version: {ex.Message}");
            return null;
        }
    }

    private static string ServiceVersionFile(AppPaths paths) => Path.Combine(paths.ConfigDirectory, "service-version");

    private static string UpdatedFromFile(AppPaths paths) => Path.Combine(paths.ConfigDirectory, "updated-from");

    /// <summary>The finished update was reported (or left to the tray app): this version is now the known one.</summary>
    private void RecordServiceVersion()
    {
        _updatedFrom = null;
        if (_paths is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(ServiceVersionFile(_paths), _runningVersion);
            File.Delete(UpdatedFromFile(_paths));
        }
        catch (Exception ex)
        {
            _log?.Warning($"Unable to record the service version: {ex.Message}");
        }
    }

    private void StopRuntime()
    {
        _mqttService?.StopAsync().GetAwaiter().GetResult();
        _mqttService?.Dispose();
        _mqttService = null;
    }

    private static void ApplyLanguage(CompanionSettings settings)
    {
        Strings.Language = settings.Language;
        Strings.HaLanguage = settings.HaLanguage;
    }

    private void DisposeRuntime()
    {
        StopRuntime();

        _systemMetricsService?.Dispose();
        _systemMetricsService = null;

        _systemCommandService?.Dispose();
        _systemCommandService = null;

        _log?.Dispose();
        _log = null;
    }
}
