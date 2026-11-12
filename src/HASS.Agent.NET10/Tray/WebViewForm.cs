using System.Drawing;
using System.Windows.Forms;
using HASS.Agent.Companion.Localization;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.Runtime;
using HASS.Agent.Companion.SystemCommands;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace HASS.Agent.Companion.Tray;

/// <summary>
/// A web page (typically a Home Assistant dashboard) in a window of its own, on the
/// WebView2 runtime that ships with Windows 11. Two shapes: a borderless popup above the
/// tray that goes away when it loses focus, and an ordinary window for the WebView
/// custom command. Nothing is loaded until one is opened. Going away frees the page, unless
/// the window keeps its page loaded: then it is only hidden, and shown again as it was.
/// </summary>
internal sealed class WebViewForm : Form
{
    private readonly FileLog _log;
    private readonly bool _popup;
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer? _closeTimer;
    private bool _closingForGood;

    /// <param name="size">Logical (96 DPI) pixels; clamped to the screen.</param>
    /// <param name="keepLoaded">Going away hides the window with its page, instead of closing it.</param>
    /// <param name="closeAfter">The window goes away by itself this long after it is shown; null for never.</param>
    public WebViewForm(string url, Size size, bool popup, FileLog log, bool keepLoaded = false, TimeSpan? closeAfter = null)
    {
        Url = url;
        RequestedSize = size;
        KeepLoaded = keepLoaded;
        CloseAfter = closeAfter;
        _popup = popup;
        _log = log;

        Text = AppIdentity.DisplayName;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.White;
        ShowInTaskbar = !popup;
        StartPosition = FormStartPosition.Manual;
        var stream = typeof(WebViewForm).Assembly.GetManifestResourceStream("hassagent.ico");
        if (stream is not null)
        {
            Icon = new Icon(stream);
        }

        if (popup)
        {
            FormBorderStyle = FormBorderStyle.None;
            TopMost = true;
            Padding = new Padding(1);
            BackColor = Color.FromArgb(148, 163, 184);
            // Like a flyout: a click anywhere else, or Alt+Tab, sends it away.
            Deactivate += (_, _) => Dismiss();
        }

        if (closeAfter is { } after)
        {
            _closeTimer = new System.Windows.Forms.Timer { Interval = (int)Math.Clamp(after.TotalMilliseconds, 1000, int.MaxValue) };
            _closeTimer.Tick += (_, _) => Dismiss();
        }

        Place();
        Controls.Add(_webView);
        Load += async (_, _) => await NavigateAsync();
    }

    /// <summary>The address it was opened with.</summary>
    public string Url { get; }

    /// <summary>The size it was asked for, before it was fitted to the screen.</summary>
    public Size RequestedSize { get; }

    public bool KeepLoaded { get; }

    /// <summary>The borderless shape above the tray, not a window of the usual kind.</summary>
    public bool IsPopup => _popup;

    public TimeSpan? CloseAfter { get; }

    /// <summary>Raised when the window goes out of sight: closed, or hidden with its page kept.</summary>
    public event EventHandler? Dismissed;

    /// <summary>
    /// Sends the window away: hidden with its page when it keeps it loaded, closed otherwise.
    /// What losing the focus does to the popup, what the time set for a window does, and what
    /// closing it from Home Assistant does.
    /// </summary>
    public void Dismiss()
    {
        if (IsDisposed || !Visible)
        {
            return;
        }

        if (KeepLoaded)
        {
            Hide();
        }
        else
        {
            Close();
        }
    }

    /// <summary>Shows a window that was hidden with its page, where it belongs on the screen now.</summary>
    public void ShowAgain()
    {
        if (IsDisposed)
        {
            return;
        }

        if (!Visible)
        {
            // The screen may be another one by now, or its taskbar somewhere else.
            Place();
            Show();
        }

        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Activate();
        RaiseWithoutFocus();
        RestartCloseTimer();
    }

    /// <summary>Closes the window and frees its page, also when it keeps its page loaded.</summary>
    public void CloseForGood()
    {
        _closingForGood = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The X of a window that keeps its page loaded only hides it. Windows shutting down,
        // the app exiting and CloseForGood go through.
        if (KeepLoaded && !_closingForGood && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            RaiseWithoutFocus();
            RestartCloseTimer();
        }
        else
        {
            _closeTimer?.Stop();
            Dismissed?.Invoke(this, EventArgs.Empty);
        }
    }

    // A window asked for from Home Assistant comes while another program is in front, and
    // Windows then shows it behind that one: a camera nobody sees. This puts it over the other
    // windows without taking the keyboard from the one that is being typed into. The popup is
    // over everything anyway.
    private void RaiseWithoutFocus()
    {
        if (_popup || !IsHandleCreated)
        {
            return;
        }

        const uint keepPlaceSizeAndFocus = 0x0001 | 0x0002 | 0x0010; // SWP_NOSIZE, SWP_NOMOVE, SWP_NOACTIVATE
        SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, keepPlaceSizeAndFocus); // HWND_TOPMOST
        SetWindowPos(Handle, new IntPtr(-2), 0, 0, 0, 0, keepPlaceSizeAndFocus); // HWND_NOTOPMOST
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    private void RestartCloseTimer()
    {
        if (_closeTimer is null)
        {
            return;
        }

        _closeTimer.Stop();
        _closeTimer.Start();
    }

    // Where the window goes: the popup above the tray of the screen the pointer is on, a
    // window in the middle of that screen.
    private void Place()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var scale = DeviceDpi / 96f;
        var width = Math.Min((int)(Math.Clamp(RequestedSize.Width, WebViewOptions.MinimumEdge, WebViewOptions.MaximumEdge) * scale), area.Width);
        var height = Math.Min((int)(Math.Clamp(RequestedSize.Height, WebViewOptions.MinimumEdge, WebViewOptions.MaximumEdge) * scale), area.Height);
        var margin = _popup ? (int)(12 * scale) : 0;
        Bounds = _popup
            ? new Rectangle(
                Math.Max(area.Left, area.Right - width - margin),
                Math.Max(area.Top, area.Bottom - height - margin),
                width,
                height)
            : new Rectangle(
                area.Left + (area.Width - width) / 2,
                area.Top + (area.Height - height) / 2,
                width,
                height);
    }

    /// <summary>
    /// The browser profile (the Home Assistant login among other things). Per Windows user,
    /// and outside the config folder, which is shared with the service.
    /// </summary>
    private static string UserDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppIdentity.ConfigurationDirectoryName,
        "WebView2");

    private async Task NavigateAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, UserDataFolder);
            if (IsDisposed)
            {
                return;
            }

            await _webView.EnsureCoreWebView2Async(environment);
            if (IsDisposed)
            {
                return;
            }

            if (!_popup)
            {
                _webView.CoreWebView2.DocumentTitleChanged += (_, _) =>
                {
                    var title = _webView.CoreWebView2.DocumentTitle;
                    Text = string.IsNullOrWhiteSpace(title) ? AppIdentity.DisplayName : title;
                };
            }

            // A link that wants a new window goes to the real browser, not to a second
            // embedded one without an address bar.
            _webView.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (WebViewOptions.IsWebAddress(e.Uri))
                {
                    try
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri) { UseShellExecute = true });
                    }
                    catch
                    {
                        // Nothing to do about a browser that will not start.
                    }
                }
            };

            // The zoom the page was last shown at comes back as the browser's default (the
            // level Ctrl+0 returns to), and a zoom the user sets is kept for the next time.
            // The store is the app's one, shared with every other open web view.
            var zoom = WebViewZoom.ForUser(_log);
            if (zoom.Get(Url) is { } level)
            {
                _webView.ZoomFactor = level;
            }

            _webView.ZoomFactorChanged += (_, _) => zoom.Set(Url, _webView.ZoomFactor);

            _webView.CoreWebView2.Navigate(Url);
        }
        catch (Exception ex)
        {
            if (IsDisposed)
            {
                // Closed while the browser was still starting; nothing went wrong.
                return;
            }

            _log.Warning($"Unable to show the web view for {Url}: {ex.Message}");

            var runtimeMissing = ex is WebView2RuntimeNotFoundException;
            _webView.Visible = false;
            Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(51, 65, 85),
                Font = new Font("Segoe UI", 9.5F),
                Padding = new Padding(16),
                TextAlign = ContentAlignment.MiddleCenter,
                Text = runtimeMissing
                    ? Strings.Get("WebView.RuntimeMissing")
                    : string.Format(Strings.Get("WebView.Failed"), ex.Message)
            });
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closeTimer?.Dispose();
            _webView.Dispose();
        }

        base.Dispose(disposing);
    }
}
