using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Http;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.Media.Sendspin;
using HASS.Agent.Companion.Mqtt;
using HASS.Agent.Companion.Networking;
using HASS.Agent.Companion.Localization;
using HASS.Agent.Companion.Runtime;
using HASS.Agent.Companion.SystemCommands;
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
    private readonly NotificationImageCache _notificationImages;
    private readonly ToastPresenter _toasts;
    private string? _toastRefusal;
    private string? _notificationName;
    private Action? _pendingBalloonAction;
    private MainForm? _mainForm;
    private ToolStripMenuItem? _copyUrlItem;
    private readonly PopupWindows _popups;
    private readonly HotkeyService _hotkeys;

    public event EventHandler<NotificationActionRequestedEventArgs>? NotificationActionRequested;
    public event EventHandler? SettingsSaved;

    /// <summary>A hotkey from the settings was pressed.</summary>
    public event EventHandler<HotkeyDefinition>? HotkeyPressed;

    /// <summary>The session that stands for the PC changed: the General page shows whether this app waits.</summary>
    public void RefreshSessionRole()
    {
        if (_mainForm is { IsDisposed: false } form)
        {
            form.RefreshStatus();
        }
    }

    /// <summary>
    /// Another user's app saved other settings, and this one took them over at a handover: a
    /// settings window open here shows the old ones, with edits not saved yet, maybe. The user
    /// chooses: the window opened again on its page with the new ones (the edits gone), or kept
    /// as it is, in which case a Save writes what it shows over the other user's. Nothing is
    /// discarded before they chose; the question is modal, so no Save runs meanwhile.
    /// </summary>
    public void SettingsReloaded()
    {
        if (_uiInvoker.IsDisposed)
        {
            return;
        }

        _uiInvoker.BeginInvoke(() =>
        {
            // The hotkeys and the menu follow the settings taken over, window or not.
            ApplyTraySettings();
            if (_mainForm is not { IsDisposed: false } form)
            {
                return;
            }

            var load = MessageBox.Show(form, Strings.Get("General.SettingsReloaded"), AppIdentity.DisplayName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
            if (!load || form.IsDisposed)
            {
                return;
            }

            var page = form.CurrentPage;
            form.Close();
            OpenMainForm(page);
        });
    }

    /// <summary>Set by Program: the Sendspin player, for its status and pairing token on the Media player page.</summary>
    public SendspinPlayerService? SendspinPlayer { get; set; }

    /// <summary>Set by Program to the MQTT service's discovery republish. Used by the Danger Zone.</summary>
    public Func<Task<bool>>? DiscoveryRepublishHandler { get; set; }

    /// <summary>Set by Program to the MQTT service: reports a manual update check to Home Assistant.</summary>
    public Func<AppUpdateState, Task>? UpdateStateHandler { get; set; }

    /// <summary>Set by Program: whose the HA API token is, and whether a user of the PC's own can be made.</summary>
    public Func<(HaTokenUser? User, bool CanProvision)>? HaTokenProbe { get; set; }

    /// <summary>Set by Program: asks Home Assistant for a user of this PC's own and its token.</summary>
    public Func<Task<(string Token, string User)>>? HaUserProvisioner { get; set; }

    // Windows reads the app's name again only once the app has no notifications left in the
    // notification centre, so a changed name clears them and shows from the next one. Only
    // when the name changes: a start, or a save that keeps it, leaves the centre alone.
    private void ApplyNotificationName()
    {
        var name = _settings.EffectiveNotificationName;
        // Nothing is cleared when the name could not be written, and the name counts as
        // applied only once both worked, so the next save, or the next start, tries again.
        if (_toasts.ApplyName(name) &&
            !string.Equals(name, _notificationName, StringComparison.Ordinal) &&
            _toasts.ClearNotificationCentre())
        {
            _notificationName = name;
            StoreAppliedNotificationName(name);
        }
    }

    // The name the notification centre was last cleared for, kept for the user (the
    // registration is the user's too) so that a clear that failed is retried after a restart.
    private const string UserKeyPath = @"Software\HASS.Agent.NET10";
    private const string AppliedNotificationNameValue = "AppliedNotificationName";

    private static string? ReadAppliedNotificationName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(UserKeyPath);
            return key?.GetValue(AppliedNotificationNameValue) is string { Length: > 0 } name ? name : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void StoreAppliedNotificationName(string? name)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(UserKeyPath);
            key.SetValue(AppliedNotificationNameValue, name ?? string.Empty);
        }
        catch (Exception ex)
        {
            _log.Warning($"Unable to remember the notification name: {ex.Message}");
        }
    }

    // Kiosk mode (KioskMode): the icon says so, and opens nothing and closes nothing.
    private readonly bool _kiosk;

    public TrayApplicationContext(CompanionSettings settings, AppPaths paths, FileLog log, bool kiosk = false)
    {
        _settings = settings;
        _paths = paths;
        _log = log;
        _kiosk = kiosk;
        _popups = new PopupWindows(log);
        _uiInvoker.CreateControl();
        _ = _uiInvoker.Handle;

        _notificationImages = new NotificationImageCache(settings, log);
        _toasts = new ToastPresenter(log);
        _toasts.ActionSelected += RaiseNotificationAction;
        _toasts.Listen();
        // The toolkit has just written the app's own name: put the chosen one back, and clear
        // the centre if a change of name has not reached it yet.
        _notificationName = ReadAppliedNotificationName();
        ApplyNotificationName();

        _hotkeys = new HotkeyService(log);
        _hotkeys.Pressed += (_, hotkey) => HotkeyPressed?.Invoke(this, hotkey);
        _hotkeys.Apply(settings.Hotkeys);

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = kiosk ? $"{AppIdentity.DisplayName} - {Strings.Get("Kiosk.Tray")}" : AppIdentity.DisplayName,
            Visible = true,
            ContextMenuStrip = kiosk ? null : BuildContextMenu()
        };

        if (!kiosk)
        {
            _notifyIcon.DoubleClick += (_, _) => OpenMainForm();
            _notifyIcon.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left && _settings.WebViewOnTrayClick)
                {
                    ToggleDashboard();
                }
            };
        }

        // The Popup window custom command, and the one that closes its windows: asked for on a
        // connection thread, done on the UI thread.
        SystemCommandService.WebViewHandler = request =>
        {
            if (_uiInvoker.IsDisposed)
            {
                return false;
            }

            _uiInvoker.BeginInvoke(() => _popups.ShowForCommand(request));
            return true;
        };
        SystemCommandService.WebViewCloseHandler = target =>
        {
            if (_uiInvoker.IsDisposed)
            {
                return false;
            }

            _uiInvoker.BeginInvoke(() => _popups.Close(target));
            return true;
        };
        _notifyIcon.BalloonTipClicked += (_, _) =>
        {
            if (_pendingBalloonAction is not null)
            {
                var action = _pendingBalloonAction;
                _pendingBalloonAction = null;
                action();
            }
        };
    }

    /// <summary>
    /// The window right at the start (--open-window): after a restart that changed the
    /// connection, with the offer of a Home Assistant user of the PC's own once it is connected.
    /// </summary>
    public void OpenMainFormAtStart()
    {
        OpenMainForm();
        if (_mainForm is not null)
        {
            _mainForm.OfferOwnUserWhenConnected = true;
        }
    }

    public void ShowSetupRequired()
    {
        if (_kiosk)
        {
            // Nothing a kiosk user could do about it.
            return;
        }

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
            Strings.Get("Tray.LocalApiNotStarted") + Environment.NewLine + Environment.NewLine + exception.Message,
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

        if (!NotificationImageCache.HasImage(notification))
        {
            DispatchNotification(notification, imageFile: null);
            return;
        }

        // The picture is fetched first, off the UI thread. A notification is worth more on
        // time than complete: after ten seconds it is shown without the picture.
        _ = Task.Run(async () =>
        {
            string? imageFile = null;
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                imageFile = await _notificationImages.FetchAsync(notification, deadline.Token);
            }
            catch (Exception ex)
            {
                _log.Warning($"Unable to fetch the notification picture: {ex.Message}");
            }

            DispatchNotification(notification, imageFile);
        });
    }

    private void DispatchNotification(NotificationPayload notification, string? imageFile)
    {
        try
        {
            if (_uiInvoker.IsDisposed)
            {
                return;
            }

            _uiInvoker.BeginInvoke(() => ShowNotificationOnUiThread(notification, imageFile));
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Unable to dispatch notification to UI thread.");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemCommandService.WebViewHandler = null;
            SystemCommandService.WebViewCloseHandler = null;
            _popups.Dispose();
            _hotkeys.Dispose();

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
        menu.Items.Add(Strings.Get("Tray.Dashboard"), null, (_, _) => ToggleDashboard());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(BuildServiceMenu());
        // The local HTTP API's address: only while there is one.
        _copyUrlItem = new ToolStripMenuItem(Strings.Get("Tray.CopyUrl"), null, (_, _) => CopyApiUrl())
        {
            Visible = _settings.LocalApiEnabled
        };
        menu.Items.Add(_copyUrlItem);
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
        if (_kiosk)
        {
            return;
        }

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
        // "Open now" shows what is in the fields, saved or not, and leaves a kept page alone.
        _mainForm.DashboardPreviewHandler = _popups.ShowPreview;
        _mainForm.HaTokenProbe = () => HaTokenProbe?.Invoke() ?? (null, false);
        _mainForm.HaUserProvisioner = HaUserProvisioner;
        _mainForm.SendspinPlayer = SendspinPlayer;
        _mainForm.SettingsSaved += (_, _) =>
        {
            ApplyTraySettings();
            SettingsSaved?.Invoke(this, EventArgs.Empty);
        };
        _mainForm.FormClosed += (_, _) => _mainForm = null;
        _mainForm.Show();
    }

    /// <summary>What of the settings the tray itself uses: the hotkeys, the notifications' name, the menu. UI thread.</summary>
    private void ApplyTraySettings()
    {
        _hotkeys.Apply(_settings.Hotkeys);
        ApplyNotificationName();
        _popups.SettingsChanged(
            _settings.WebViewUrl, new Size(_settings.WebViewWidth, _settings.WebViewHeight), _settings.WebViewKeepLoaded,
            _settings.CustomCommands);
        if (_copyUrlItem is not null)
        {
            _copyUrlItem.Visible = _settings.LocalApiEnabled;
        }
    }

    /// <summary>The dashboard popup above the tray: brings it up, or sends away the one that is up.</summary>
    private void ToggleDashboard()
    {
        if (!WebViewOptions.IsWebAddress(_settings.WebViewUrl))
        {
            // Not set up yet: the page where it is done.
            OpenMainForm(3);
            return;
        }

        _popups.ToggleDashboard(
            _settings.WebViewUrl, new Size(_settings.WebViewWidth, _settings.WebViewHeight), _settings.WebViewKeepLoaded);
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

    private void ShowNotificationOnUiThread(NotificationPayload notification, string? imageFile)
    {
        // The notification can ask for a style of its own: a doorbell that has to be seen
        // also during Do not disturb goes to the window whatever the setting says.
        var style = NotificationStyles.Normalize(notification.Data?.Style, _settings.NotificationStyle);
        _log.Info(
            $"Showing notification ({style}, {notification.Actions.Count} action(s), " +
            $"{notification.Inputs.Count} input(s){(imageFile is null ? string.Empty : ", picture")}).");

        if (style == NotificationStyles.Toast)
        {
            var shown = _toasts.TryShow(
                notification,
                imageFile,
                failed: () => DispatchWindowNotification(notification, imageFile),
                out var reason);
            if (shown)
            {
                _toastRefusal = null;
                return;
            }

            // Somebody who turned the app's notifications off in Windows gets the window
            // every time; the log says why once, not with every notification.
            if (!string.Equals(_toastRefusal, reason, StringComparison.Ordinal))
            {
                _toastRefusal = reason;
                _log.Warning($"Windows notification not shown: {reason}. Using the app's own window.");
            }
        }

        ShowWindowNotification(notification, imageFile);
    }

    /// <summary>From a Windows callback thread: Windows gave up on a notification it had accepted.</summary>
    private void DispatchWindowNotification(NotificationPayload notification, string? imageFile)
    {
        try
        {
            if (!_uiInvoker.IsDisposed)
            {
                _uiInvoker.BeginInvoke(() => ShowWindowNotification(notification, imageFile));
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Unable to dispatch notification to UI thread.");
        }
    }

    private void RaiseNotificationAction(string action, IReadOnlyDictionary<string, string> input)
    {
        NotificationActionRequested?.Invoke(this, new NotificationActionRequestedEventArgs(action, input));
    }

    private void ShowWindowNotification(NotificationPayload notification, string? imageFile)
    {
        var form = new ActionNotificationForm(notification, imageFile, RaiseNotificationAction, _settings.EffectiveNotificationName);

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

    private static Icon LoadTrayIcon()
    {
        var stream = typeof(TrayApplicationContext).Assembly.GetManifestResourceStream("hassagent.ico");
        return stream is not null ? new Icon(stream) : SystemIcons.Application;
    }
}

internal sealed class NotificationActionRequestedEventArgs(
    string action,
    IReadOnlyDictionary<string, string>? input = null) : EventArgs
{
    public string Action { get; } = action;

    /// <summary>What was typed into the notification's text fields, by field id.</summary>
    public IReadOnlyDictionary<string, string>? Input { get; } = input;
}
