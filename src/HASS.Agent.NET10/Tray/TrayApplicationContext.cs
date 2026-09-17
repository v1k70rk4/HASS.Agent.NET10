using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Http;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.Networking;
using HASS.Agent.Companion.Localization;
using HASS.Agent.Companion.Runtime;
using HASS.Agent.Companion.SystemService;

namespace HASS.Agent.Companion.Tray;

internal sealed class TrayApplicationContext : ApplicationContext, INotificationSink
{
    private readonly CompanionSettings _settings;
    private readonly AppPaths _paths;
    private readonly FileLog _log;
    private readonly Control _uiInvoker = new();
    private readonly NotifyIcon _notifyIcon;
    private readonly List<ActionNotificationForm> _actionNotifications = [];
    private string? _pendingNotificationAction;
    private Action? _pendingBalloonAction;
    private MainForm? _mainForm;

    public event EventHandler<NotificationActionRequestedEventArgs>? NotificationActionRequested;
    public event EventHandler? SettingsSaved;

    /// <summary>Set by Program to the MQTT service's discovery republish. Used by the Danger Zone.</summary>
    public Func<Task<bool>>? DiscoveryRepublishHandler { get; set; }

    /// <summary>Set by Program to the MQTT service: reports a manual update check to Home Assistant.</summary>
    public Func<AppUpdateState, Task>? UpdateStateHandler { get; set; }

    public TrayApplicationContext(CompanionSettings settings, AppPaths paths, FileLog log)
    {
        _settings = settings;
        _paths = paths;
        _log = log;
        _uiInvoker.CreateControl();
        _ = _uiInvoker.Handle;

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = AppIdentity.DisplayName,
            Visible = true,
            ContextMenuStrip = BuildContextMenu()
        };

        _notifyIcon.DoubleClick += (_, _) => OpenMainForm();
        _notifyIcon.BalloonTipClicked += (_, _) =>
        {
            if (_pendingBalloonAction is not null)
            {
                var action = _pendingBalloonAction;
                _pendingBalloonAction = null;
                action();
            }
            else
            {
                PublishPendingNotificationAction();
            }
        };
    }

    public void ShowSetupRequired()
    {
        _pendingBalloonAction = () => OpenMainForm(1);
        _notifyIcon.ShowBalloonTip(
            10_000,
            AppIdentity.DisplayName,
            Strings.Get("Msg.NotConfigured"),
            ToolTipIcon.Warning);
    }

    public void ShowStartupError(Exception exception)
    {
        MessageBox.Show(
            $"The Local API could not start.{Environment.NewLine}{Environment.NewLine}{exception.Message}",
            AppIdentity.DisplayName,
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    public void ShowNotification(NotificationPayload notification)
    {
        if (_uiInvoker.IsDisposed)
        {
            return;
        }

        if (_uiInvoker.InvokeRequired)
        {
            try
            {
                _uiInvoker.BeginInvoke(() => ShowNotificationOnUiThread(notification));
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Unable to dispatch notification to UI thread.");
            }
            return;
        }

        ShowNotificationOnUiThread(notification);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var actionNotification in _actionNotifications.ToList())
            {
                actionNotification.Close();
            }

            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _uiInvoker.Dispose();
        }

        base.Dispose(disposing);
    }

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add(Strings.Get("Tray.Open"), null, (_, _) => OpenMainForm());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(BuildServiceMenu());
        menu.Items.Add(Strings.Get("Tray.CopyUrl"), null, (_, _) => CopyApiUrl());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Strings.Get("Tray.Exit"), null, (_, _) => ExitThread());

        return menu;
    }

    private ToolStripMenuItem BuildServiceMenu()
    {
        var serviceMenu = new ToolStripMenuItem(Strings.Get("Tray.Service"));
        serviceMenu.DropDownItems.Add(Strings.Get("Tray.ServiceStatus"), null, (_, _) => ShowServiceStatus());
        serviceMenu.DropDownItems.Add(new ToolStripSeparator());
        serviceMenu.DropDownItems.Add(Strings.Get("Tray.ServiceInstall"), null, (_, _) => RunServiceCommand("--install-service"));
        serviceMenu.DropDownItems.Add(Strings.Get("Tray.ServiceStart"), null, (_, _) => RunServiceCommand("--start-service"));
        serviceMenu.DropDownItems.Add(Strings.Get("Tray.ServiceStop"), null, (_, _) => RunServiceCommand("--stop-service"));
        serviceMenu.DropDownItems.Add(Strings.Get("Tray.ServiceUninstall"), null, (_, _) => RunServiceCommand("--uninstall-service"));

        return serviceMenu;
    }

    private void OpenMainForm(int page = 0)
    {
        if (_mainForm is not null && !_mainForm.IsDisposed)
        {
            _mainForm.NavigateToPage(page);
            _mainForm.BringToFront();
            _mainForm.Activate();
            return;
        }

        _mainForm = new MainForm(_settings, _paths, _log, page);
        _mainForm.DiscoveryRepublishHandler = () => DiscoveryRepublishHandler?.Invoke() ?? Task.FromResult(false);
        _mainForm.UpdateStateHandler = state => UpdateStateHandler?.Invoke(state) ?? Task.CompletedTask;
        _mainForm.SettingsSaved += (_, _) => SettingsSaved?.Invoke(this, EventArgs.Empty);
        _mainForm.FormClosed += (_, _) => _mainForm = null;
        _mainForm.Show();
    }

    private void CopyApiUrl()
    {
        Clipboard.SetText(NetworkInfo.GetPreferredLanUrl(_settings.Port));
        _notifyIcon.ShowBalloonTip(3000, AppIdentity.DisplayName, "Home Assistant URL copied.", ToolTipIcon.Info);
    }

    private static void ShowServiceStatus()
    {
        MessageBox.Show(
            CompanionServiceManager.GetStatusText(),
            AppIdentity.ServiceDisplayName,
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void RunServiceCommand(string command)
    {
        CompanionServiceManager.RunElevated(command, _log);
        _notifyIcon.ShowBalloonTip(3000, AppIdentity.DisplayName, Strings.Get("Tray.ServiceCommand"), ToolTipIcon.Info);
    }

    private void OpenSettingsFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _paths.ConfigDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Unable to open settings folder.");
            MessageBox.Show(ex.Message, AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowNotificationOnUiThread(NotificationPayload notification)
    {
        if (notification.HasActions)
        {
            _log.Info($"Showing actionable notification with {notification.Actions.Count} action(s).");
            ShowActionNotification(notification);
            return;
        }

        var title = string.IsNullOrWhiteSpace(notification.Title)
            ? "Home Assistant"
            : notification.Title.Trim();

        _pendingNotificationAction = notification.PrimaryAction;

        _notifyIcon.ShowBalloonTip(
            notification.TimeoutMilliseconds,
            title,
            notification.Message!.Trim(),
            ToolTipIcon.Info);
    }

    private void ShowActionNotification(NotificationPayload notification)
    {
        var form = new ActionNotificationForm(notification, action =>
        {
            NotificationActionRequested?.Invoke(this, new NotificationActionRequestedEventArgs(action));
        });

        _actionNotifications.Add(form);
        form.FormClosed += (_, _) =>
        {
            _actionNotifications.Remove(form);
            RepositionActionNotifications();
        };

        RepositionActionNotifications();
        form.Show();
    }

    private void RepositionActionNotifications()
    {
        var bottomOffset = 16;
        foreach (var form in _actionNotifications.AsEnumerable().Reverse())
        {
            form.PositionFromBottom(bottomOffset);
            bottomOffset += form.Height + 12;
        }
    }

    private void PublishPendingNotificationAction()
    {
        var action = _pendingNotificationAction;
        _pendingNotificationAction = null;

        if (string.IsNullOrWhiteSpace(action))
        {
            return;
        }

        NotificationActionRequested?.Invoke(this, new NotificationActionRequestedEventArgs(action.Trim()));
    }

    private static Icon LoadTrayIcon()
    {
        var stream = typeof(TrayApplicationContext).Assembly.GetManifestResourceStream("hassagent.ico");
        return stream is not null ? new Icon(stream) : SystemIcons.Application;
    }
}

internal sealed class NotificationActionRequestedEventArgs(string action) : EventArgs
{
    public string Action { get; } = action;
}
