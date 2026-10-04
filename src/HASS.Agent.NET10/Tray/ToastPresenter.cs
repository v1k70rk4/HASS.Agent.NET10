using HASS.Agent.Companion.Http;
using HASS.Agent.Companion.Localization;
using HASS.Agent.Companion.Logging;
using Microsoft.Toolkit.Uwp.Notifications;
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
