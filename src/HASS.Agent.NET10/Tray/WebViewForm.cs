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
/// custom command. Nothing is loaded until one is opened, and closing it frees the page.
/// </summary>
internal sealed class WebViewForm : Form
{
    private readonly FileLog _log;
    private readonly string _url;
    private readonly bool _popup;
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };

    /// <param name="size">Logical (96 DPI) pixels; clamped to the screen.</param>
    public WebViewForm(string url, Size size, bool popup, FileLog log)
    {
        _url = url;
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
            // Like a flyout: a click anywhere else, or Alt+Tab, closes it.
            Deactivate += (_, _) => Close();
        }

        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        var scale = DeviceDpi / 96f;
        var width = Math.Min((int)(Math.Clamp(size.Width, WebViewOptions.MinimumEdge, WebViewOptions.MaximumEdge) * scale), area.Width);
        var height = Math.Min((int)(Math.Clamp(size.Height, WebViewOptions.MinimumEdge, WebViewOptions.MaximumEdge) * scale), area.Height);
        var margin = popup ? (int)(12 * scale) : 0;
        Bounds = popup
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

        Controls.Add(_webView);
        Load += async (_, _) => await NavigateAsync();
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

            _webView.CoreWebView2.Navigate(_url);
        }
        catch (Exception ex)
        {
            if (IsDisposed)
            {
                // Closed while the browser was still starting; nothing went wrong.
                return;
            }

            _log.Warning($"Unable to show the web view for {_url}: {ex.Message}");

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
            _webView.Dispose();
        }

        base.Dispose(disposing);
    }
}
