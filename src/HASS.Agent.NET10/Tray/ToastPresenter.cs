using HASS.Agent.Companion.Http;
using HASS.Agent.Companion.Localization;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.Runtime;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Win32;
using Windows.UI.Notifications;

namespace HASS.Agent.Companion.Tray;

/// <summary>
/// Shows a notification as a Windows notification (toast): it follows Do not disturb,
/// stays in the notification centre, and Windows draws the picture, the buttons and the
/// text fields. The toolkit registers the app with Windows for the current user on first
/// use, which a desktop app without a package has to do itself.
/// </summary>
internal sealed class ToastPresenter
{
    private const string ActionKey = "action";

    private readonly FileLog _log;
    private bool _listening;

    public ToastPresenter(FileLog log)
    {
        _log = log;
    }

    /// <summary>A button of a notification was pressed. Raised on a background thread.</summary>
    public event Action<string, IReadOnlyDictionary<string, string>>? ActionSelected;

    /// <summary>
    /// Starts taking button presses. Also needed with no notification of this run on screen:
    /// one from before a restart can still be pressed in the notification centre.
    /// </summary>
    public void Listen()
    {
        if (_listening)
        {
            return;
        }

        try
        {
            ToastNotificationManagerCompat.OnActivated += OnActivated;
            _listening = true;
        }
        catch (Exception ex)
        {
            _log.Warning($"Windows notifications are not available: {ex.Message}");
        }
    }

    /// <summary>
    /// The name above the app's notifications: the given text, or the app's own name for
    /// null/empty. Windows takes it from the app's registration, which the toolkit writes
    /// again at each start (after <see cref="Listen"/>), so this runs after that and after
    /// every save of the settings. It applies to new notifications at once.
    /// </summary>
    public void ApplyName(string? name)
    {
        var displayName = string.IsNullOrWhiteSpace(name) ? AppIdentity.DisplayName : name.Trim();
        try
        {
            using var key = FindRegistration();
            if (key is null)
            {
                _log.Warning("The app's notification registration was not found; the notification name stays as it is.");
                return;
            }

            if (!string.Equals(key.GetValue("DisplayName") as string, displayName, StringComparison.Ordinal))
            {
                key.SetValue("DisplayName", displayName);
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Unable to set the notification name: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes the app's notifications from the notification centre. Windows keeps showing
    /// the name it read for the app while the app has notifications there, so a changed name
    /// shows only once they are gone.
    /// </summary>
    public void ClearNotificationCentre()
    {
        try
        {
            ToastNotificationManagerCompat.History.Clear();
        }
        catch (Exception ex)
        {
            _log.Warning($"Unable to clear the app's notifications: {ex.Message}");
        }
    }

    // HKCU\Software\Classes\AppUserModelId\<AUMID>: the one whose COM activator (registered
    // by the toolkit) starts this exe. Other installs and other apps are left alone.
    private static RegistryKey? FindRegistration()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return null;
        }

        using var aumids = Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId");
        foreach (var aumid in aumids?.GetSubKeyNames() ?? [])
        {
            using var candidate = aumids!.OpenSubKey(aumid);
            if (candidate?.GetValue("CustomActivator") is not string clsid || !Guid.TryParse(clsid, out var activator))
            {
                continue;
            }

            using var server = Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{{{activator}}}\LocalServer32");
            if (server?.GetValue(null) is string command && CommandStarts(command, exe))
            {
                return aumids.OpenSubKey(aumid, writable: true);
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a LocalServer32 command starts this exe: the quoted program path, or the
    /// whole command when it has no arguments. Not a substring match, which another install
    /// whose path contains this one would pass.
    /// </summary>
    internal static bool CommandStarts(string command, string exe)
    {
        var trimmed = command.Trim();
        string program;
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            if (end < 0)
            {
                return false;
            }

            program = trimmed[1..end];
        }
        else
        {
            program = trimmed;
        }

        return string.Equals(program, exe, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Hands the notification to Windows. False with the reason when Windows will not show
    /// it; <paramref name="failed"/> is called when Windows gives up on it afterwards.
    /// </summary>
    public bool TryShow(NotificationPayload notification, string? imageFile, Action failed, out string reason)
    {
        reason = string.Empty;
        try
        {
            var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
            if (notifier.Setting != NotificationSetting.Enabled)
            {
                reason = $"notifications are turned off for the app in Windows ({notifier.Setting})";
                return false;
            }

            var toast = new ToastNotification(BuildContent(notification, imageFile).GetXml());
            toast.Failed += (_, args) =>
            {
                _log.Warning($"Windows could not show the notification: {args.ErrorCode.Message}");
                failed();
            };

            Listen();
            notifier.Show(toast);
            return true;
        }
        catch (Exception ex)
        {
            reason = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
            return false;
        }
    }

    /// <summary>The notification in the form Windows takes it: texts, picture, text fields, buttons.</summary>
    internal static ToastContent BuildContent(NotificationPayload notification, string? imageFile)
    {
        var builder = new ToastContentBuilder()
            .AddText(string.IsNullOrWhiteSpace(notification.Title) ? "Home Assistant" : notification.Title.Trim())
            .AddText(notification.Message?.Trim() ?? string.Empty);

        if (imageFile is not null)
        {
            // As a file:/// address, which is what Windows expects from a desktop app.
            builder.AddInlineImage(new Uri(new Uri(imageFile).AbsoluteUri));
        }

        var inputs = notification.Inputs;
        foreach (var input in inputs)
        {
            builder.AddInputTextBox(input.Id!, input.Title);
        }

        foreach (var action in notification.Actions)
        {
            var name = action.Action!.Trim();
            builder.AddButton(new ToastButton()
                .SetContent(string.IsNullOrWhiteSpace(action.Title) ? name : action.Title.Trim())
                .AddArgument(ActionKey, name)
                .SetBackgroundActivation());
        }

        // Text fields need a button to send them with.
        if (inputs.Count > 0 && !notification.HasActions)
        {
            builder.AddButton(new ToastButton()
                .SetContent(Strings.Get("Btn.Send"))
                .AddArgument(ActionKey, NotificationPayload.ReplyAction)
                .SetBackgroundActivation());
        }

        // Windows knows two lengths: about seven seconds and about twenty-five.
        if (notification.TimeoutMilliseconds > 10_000)
        {
            builder.SetToastDuration(ToastDuration.Long);
        }

        return builder.GetToastContent();
    }

    /// <summary>Removes what the toolkit registered for the current user. For the uninstaller.</summary>
    public static void Unregister()
    {
        ToastNotificationManagerCompat.Uninstall();
    }

    private void OnActivated(ToastNotificationActivatedEventArgsCompat activation)
    {
        try
        {
            // A click on the notification itself carries no action; only buttons do.
            var arguments = ToastArguments.Parse(activation.Argument);
            if (!arguments.TryGetValue(ActionKey, out var action) || string.IsNullOrWhiteSpace(action))
            {
                return;
            }

            var input = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in activation.UserInput)
            {
                if (pair.Value is string text)
                {
                    input[pair.Key] = text;
                }
            }

            ActionSelected?.Invoke(action, input);
        }
        catch (Exception ex)
        {
            _log.Warning($"Unable to handle the pressed notification button: {ex.Message}");
        }
    }
}
