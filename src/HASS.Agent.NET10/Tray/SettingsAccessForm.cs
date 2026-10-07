using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Localization;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.Runtime;

namespace HASS.Agent.Companion.Tray;

/// <summary>
/// "Who may use HASS.Agent on this PC?" Asked once on a PC whose settings every user can still
/// read and change (installs from before 10.9.1), and reachable later from the Danger Zone.
/// Limiting runs the app as administrator (UAC) with --settings-access.
/// </summary>
internal sealed class SettingsAccessForm : Form
{
    private const int ErrorCancelled = 1223;

    private readonly AppPaths _paths;
    private readonly CompanionSettings _settings;
    private readonly FileLog _log;
    private readonly CheckedListBox _users = new() { CheckOnClick = true, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };

    private SettingsAccessForm(AppPaths paths, CompanionSettings settings, FileLog log, bool firstTime)
    {
        _paths = paths;
        _settings = settings;
        _log = log;

        Text = AppIdentity.DisplayName;
        Icon = LoadIcon();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = new Font("Segoe UI", 9.5F);
        ClientSize = new Size(520, 420);
        Padding = new Padding(16);

        var restricted = SettingsAccess.IsRestricted(paths.ConfigDirectory);
        var heading = new Label
        {
            Dock = DockStyle.Top,
            Height = 30,
            Font = new Font("Segoe UI Semibold", 12F),
            Text = Strings.Get("Access.Heading"),
        };
        var text = new Label
        {
            Dock = DockStyle.Top,
            Height = 110,
            Text = (firstTime ? Strings.Get("Access.FirstTime") + "\n\n" : string.Empty)
                + Strings.Get(restricted ? "Access.NowRestricted" : "Access.NowEveryone"),
        };

        var current = Environment.UserName;
        var members = SettingsAccess.Members();
        foreach (var user in SettingsAccess.LocalUsers())
        {
            var isCurrent = string.Equals(user, current, StringComparison.OrdinalIgnoreCase);
            _users.Items.Add(user, isCurrent || members.Contains(user));
        }

        if (!_users.Items.Contains(current))
        {
            _users.Items.Insert(0, current);
            _users.SetItemChecked(0, true);
        }

        _users.Dock = DockStyle.Fill;

        var limit = new Button { Text = Strings.Get("Access.Limit"), AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
        var everyone = new Button { Text = Strings.Get("Access.KeepEveryone"), AutoSize = true, Padding = new Padding(8, 2, 8, 2) };
        var later = new Button { Text = Strings.Get(firstTime ? "Access.Later" : "Access.Close"), AutoSize = true, Padding = new Padding(8, 2, 8, 2), DialogResult = DialogResult.Cancel };
        limit.Click += (_, _) => Limit();
        everyone.Click += (_, _) => KeepForEveryone();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 8, 0, 0),
        };
        buttons.Controls.AddRange([later, everyone, limit]);

        Controls.Add(_users);
        Controls.Add(buttons);
        Controls.Add(text);
        Controls.Add(heading);
        AcceptButton = limit;
        CancelButton = later;
    }

    /// <summary>
    /// When the app's window is opened, on a PC whose settings every user may still use and
    /// that has a user who is not an administrator (one with only administrators is limited by
    /// the update itself). "Ask me later" asks again the next time the window opens.
    /// </summary>
    public static void AskOnceIfOpen(IWin32Window owner, AppPaths paths, CompanionSettings settings, FileLog log)
    {
        try
        {
            if (settings.SettingsAccessDecided
                || SettingsAccess.IsRestricted(paths.ConfigDirectory)
                || SettingsAccess.NonAdministratorUsers().Count == 0)
            {
                return;
            }
        }
        catch (Exception ex)
        {
            log.Warning($"Unable to check who may use the settings: {ex.Message}");
            return;
        }

        using var form = new SettingsAccessForm(paths, settings, log, firstTime: true);
        form.ShowDialog(owner);
    }

    public static void Show(IWin32Window owner, AppPaths paths, CompanionSettings settings, FileLog log)
    {
        using var form = new SettingsAccessForm(paths, settings, log, firstTime: false);
        form.ShowDialog(owner);
    }

    private void Limit()
    {
        var users = _users.CheckedItems.Cast<string>().ToList();
        if (!users.Contains(Environment.UserName, StringComparer.OrdinalIgnoreCase))
        {
            // Leaving yourself out would lock this app out of its own settings.
            users.Insert(0, Environment.UserName);
        }

        if (!RunElevated("--settings-access only " + string.Join(' ', users.Select(Quote))))
        {
            return;
        }

        if (!SettingsAccess.IsRestricted(_paths.ConfigDirectory))
        {
            MessageBox.Show(this, Strings.Get("Access.NotChanged"), AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Remember();
        MessageBox.Show(this, string.Format(Strings.Get("Access.Restricted"), string.Join(", ", users)), AppIdentity.DisplayName,
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        DialogResult = DialogResult.OK;
    }

    private void KeepForEveryone()
    {
        if (SettingsAccess.IsRestricted(_paths.ConfigDirectory) && !RunElevated("--settings-access everyone"))
        {
            return;
        }

        Remember();
        DialogResult = DialogResult.OK;
    }

    private void Remember()
    {
        _settings.SettingsAccessDecided = true;
        try
        {
            SettingsStore.Save(_paths, _settings);
        }
        catch (Exception ex)
        {
            _log.Warning($"Unable to save the settings access decision: {ex.Message}");
        }
    }

    private bool RunElevated(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? Application.ExecutablePath,
                Arguments = arguments + " --quiet",
                UseShellExecute = true,
                Verb = "runas",
            });
            process?.WaitForExit(TimeSpan.FromSeconds(60));
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
        catch (Exception ex)
        {
            _log.Warning($"Unable to change the settings access: {ex.Message}");
            MessageBox.Show(this, string.Format(Strings.Get("Access.Failed"), ex.Message), AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", string.Empty)}\"";

    private static Icon? LoadIcon()
    {
        try
        {
            using var stream = typeof(SettingsAccessForm).Assembly.GetManifestResourceStream("hassagent.ico");
            return stream is null ? null : new Icon(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
