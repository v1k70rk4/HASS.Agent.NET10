using System.ServiceProcess;
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
    private System.Threading.Timer? _settingsReloadTimer;
    private string? _updatedFrom;
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
            var settings = SettingsStore.LoadOrCreate(_paths, _log);
            ApplyLanguage(settings);
            _updatedFrom = DetectCompletedUpdate(settings);
            _systemCommandService = new SystemCommandService(_log);
            _systemMetricsService = new SystemMetricsService(_log, monitorPowerStateService: null, includeInteractiveMetrics: false);
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
            _log.Info("Settings changed; reloading system service runtime.");
            var settings = SettingsStore.LoadOrCreate(_paths, _log);
            ApplyLanguage(settings);

            lock (_runtimeLock)
            {
                StopRuntime();
                StartRuntime(settings);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Unable to reload system service settings.");
        }
    }

    private void StopSettingsWatcher()
    {
        _settingsReloadTimer?.Dispose();
        _settingsReloadTimer = null;

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

        if (!settings.MqttEnabled)
        {
            _log.Warning("MQTT disabled; system service will stay idle.");
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
    /// </summary>
    private string? DetectCompletedUpdate(CompanionSettings settings)
    {
        if (_paths is null)
        {
            return null;
        }

        var versionFile = Path.Combine(_paths.ConfigDirectory, "service-version");
        try
        {
            // Before this file existed, the tray app's record is the best there is.
            var previous = File.Exists(versionFile)
                ? File.ReadAllText(versionFile).Trim()
                : settings.LastRunVersion;
            if (previous == settings.SoftwareVersion)
            {
                return null;
            }

            File.WriteAllText(versionFile, settings.SoftwareVersion);
            if (string.IsNullOrEmpty(previous))
            {
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

    private void StopRuntime()
    {
        // Sent, or left to the tray app: either way it must not go out again after a
        // settings reload builds a new runtime.
        if (_mqttService is { UpdateCompletionPending: false })
        {
            _updatedFrom = null;
        }

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
