using System.Drawing;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.SystemCommands;

namespace HASS.Agent.Companion.Tray;

/// <summary>What is done with a window that is asked for while one of the same kind may be there.</summary>
internal enum PopupReuse
{
    /// <summary>There is none: a new one is made.</summary>
    Create,

    /// <summary>The one that is there, hidden with its page or still open, is brought up as it is.</summary>
    ShowAgain,

    /// <summary>The one that is there goes for good and a new one is made, with the page loaded afresh.</summary>
    Replace
}

/// <summary>
/// The web view windows of the tray app: the dashboard popup above the tray, and one window
/// for each Popup window custom command. A window that keeps its page loaded is hidden when
/// it goes away and brought up again as it was; the others are closed, and load their page
/// afresh the next time. Everything here runs on the UI thread.
/// </summary>
internal sealed class PopupWindows : IDisposable
{
    // The click on the tray icon that took the focus away has just sent the popup away;
    // the same click must not bring it back.
    private static readonly TimeSpan SameClick = TimeSpan.FromMilliseconds(400);

    private readonly FileLog _log;
    private readonly Dictionary<string, (WebViewForm Window, string Name)> _byCommand = new(StringComparer.OrdinalIgnoreCase);
    private WebViewForm? _dashboard;
    private DateTime _dashboardDismissedAt;

    public PopupWindows(FileLog log)
    {
        _log = log;
    }

    /// <summary>
    /// Whether a window is made, brought up again or replaced. One that keeps its page loaded
    /// is brought up again while it is still asked for the same way: the same address, the
    /// same size, and still to be kept. Anything else is a new window with a fresh page.
    /// </summary>
    internal static PopupReuse Decide(bool thereIsOne, bool itKeepsItsPage, bool keepNow, bool sameAddress, bool sameSize) =>
        !thereIsOne ? PopupReuse.Create
        : itKeepsItsPage && keepNow && sameAddress && sameSize ? PopupReuse.ShowAgain
        : PopupReuse.Replace;

    /// <summary>Whether a "close" names no command: it is for every window there is.</summary>
    internal static bool IsForEveryWindow(string? target) =>
        string.IsNullOrWhiteSpace(target) || target.Trim() == WebViewOptions.EveryWindow;

    /// <summary>
    /// Which of the open command windows a "close" is for: the one whose command has this
    /// name or id, or every one of them when none is named.
    /// </summary>
    internal static IReadOnlyList<string> Targets(IEnumerable<(string Id, string Name)> open, string? target)
    {
        var all = IsForEveryWindow(target);
        var wanted = target?.Trim();
        return open
            .Where(window => all
                || string.Equals(window.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(window.Id, wanted, StringComparison.OrdinalIgnoreCase))
            .Select(window => window.Id)
            .ToList();
    }

    /// <summary>A click on the tray icon: the popup comes up, or goes away when it is up.</summary>
    public void ToggleDashboard(string url, Size size, bool keepLoaded)
    {
        if (_dashboard is { IsDisposed: false, Visible: true })
        {
            _dashboard.Dismiss();
            return;
        }

        if (DateTime.UtcNow - _dashboardDismissedAt < SameClick)
        {
            return;
        }

        ShowDashboard(url, size, keepLoaded);
    }

    /// <summary>Shows the dashboard popup: the one kept loaded when it still fits, a new one otherwise.</summary>
    public void ShowDashboard(string url, Size size, bool keepLoaded)
    {
        var existing = _dashboard is { IsDisposed: false } ? _dashboard : null;
        var reuse = Decide(
            existing is not null,
            existing?.KeepLoaded == true,
            keepLoaded,
            existing is not null && string.Equals(existing.Url, url, StringComparison.Ordinal),
            existing?.RequestedSize == size);

        if (reuse == PopupReuse.ShowAgain)
        {
            existing!.ShowAgain();
            return;
        }

        existing?.CloseForGood();
        var popup = new WebViewForm(url, size, popup: true, _log, keepLoaded);
        popup.Dismissed += (_, _) =>
        {
            if (ReferenceEquals(_dashboard, popup))
            {
                _dashboardDismissedAt = DateTime.UtcNow;
            }
        };
        popup.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_dashboard, popup))
            {
                _dashboard = null;
                _dashboardDismissedAt = DateTime.UtcNow;
            }

            popup.Dispose();
        };
        _dashboard = popup;
        popup.Show();
        popup.Activate();
    }

    /// <summary>
    /// What the settings now say about the dashboard popup: a page kept loaded for an address
    /// or a size that is no longer set, or that is no longer to be kept, is let go at once and
    /// not at the next click, so nothing stays in memory that was switched off.
    /// </summary>
    public void SettingsChanged(string url, Size size, bool keepLoaded)
    {
        if (_dashboard is not { IsDisposed: false, KeepLoaded: true } kept)
        {
            return;
        }

        if (!keepLoaded || !string.Equals(kept.Url, url, StringComparison.Ordinal) || kept.RequestedSize != size)
        {
            kept.CloseForGood();
        }
    }

    /// <summary>
    /// The window of a Popup window command. A command has one window: asked for again, a
    /// window that keeps its page comes up as it was, and any other is closed and made anew,
    /// so the page is fresh and windows do not pile up.
    /// </summary>
    public void ShowForCommand(PopupWindowRequest request)
    {
        var existing = _byCommand.TryGetValue(request.CommandId, out var entry) && !entry.Window.IsDisposed ? entry.Window : null;
        var reuse = Decide(
            existing is not null,
            existing?.KeepLoaded == true,
            request.Options.KeepLoaded,
            existing is not null && string.Equals(existing.Url, request.Url, StringComparison.Ordinal),
            existing?.RequestedSize == request.Options.Size && existing?.CloseAfter == request.Options.CloseAfter);

        if (reuse == PopupReuse.ShowAgain)
        {
            _byCommand[request.CommandId] = (existing!, request.CommandName);
            existing!.ShowAgain();
            return;
        }

        existing?.CloseForGood();
        var window = new WebViewForm(
            request.Url, request.Options.Size, popup: false, _log, request.Options.KeepLoaded, request.Options.CloseAfter);
        window.FormClosed += (_, _) =>
        {
            if (_byCommand.TryGetValue(request.CommandId, out var current) && ReferenceEquals(current.Window, window))
            {
                _byCommand.Remove(request.CommandId);
            }

            window.Dispose();
        };
        _byCommand[request.CommandId] = (window, request.CommandName);
        window.Show();
    }

    /// <summary>
    /// Sends command windows away, as their own X would: the one of the command with this name
    /// or id, or all of them and the dashboard popup when none is named. Returns how many were up.
    /// </summary>
    public int Close(string? target)
    {
        var open = _byCommand
            .Where(pair => !pair.Value.Window.IsDisposed)
            .Select(pair => (Id: pair.Key, pair.Value.Name))
            .ToList();
        var closed = 0;
        foreach (var id in Targets(open, target))
        {
            var window = _byCommand[id].Window;
            if (window.Visible)
            {
                closed++;
                window.Dismiss();
            }
        }

        if (IsForEveryWindow(target) && _dashboard is { IsDisposed: false, Visible: true } dashboard)
        {
            closed++;
            dashboard.Dismiss();
        }

        return closed;
    }

    public void Dispose()
    {
        foreach (var (window, _) in _byCommand.Values.ToList())
        {
            if (!window.IsDisposed)
            {
                window.CloseForGood();
            }
        }

        _byCommand.Clear();
        if (_dashboard is { IsDisposed: false } dashboard)
        {
            dashboard.CloseForGood();
        }

        _dashboard = null;
    }
}
