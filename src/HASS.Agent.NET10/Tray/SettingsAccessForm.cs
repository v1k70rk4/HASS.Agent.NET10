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
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9.5F);

        // Everything sizes itself to its text and to the DPI of the screen: a TV at 300 %
        // cut the fixed sizes this had at first (and hid two of the three buttons).
        AutoScaleMode = AutoScaleMode.None;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var width = LogicalToDeviceUnits(480);
        var gap = LogicalToDeviceUnits(10);

        var restricted = SettingsAccess.IsRestricted(paths.ConfigDirectory);
        var heading = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(width, 0),
            Margin = new Padding(0, 0, 0, gap),
            Font = new Font("Segoe UI Semibold", 12F),
            Text = Strings.Get("Access.Heading"),
        };
        var text = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(width, 0),
            Margin = new Padding(0, 0, 0, gap),
            Text = (firstTime ? Strings.Get("Access.FirstTime") + Environment.NewLine + Environment.NewLine : string.Empty)
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

        _users.Width = width;
        _users.Height = _users.ItemHeight * Math.Clamp(_users.Items.Count, 3, 8) + LogicalToDeviceUnits(6);
        _users.Margin = new Padding(0, 0, 0, gap);

        Button MakeButton(string key, bool elevates)
        {
            var button = new Button
            {
                Text = Strings.Get(key),
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(LogicalToDeviceUnits(10), LogicalToDeviceUnits(4), LogicalToDeviceUnits(10), LogicalToDeviceUnits(4)),
                Margin = new Padding(LogicalToDeviceUnits(8), 0, 0, 0),
            };
            if (elevates)
            {
                // The UAC shield, as Windows puts it on every button that asks for an administrator.
                using var shield = SystemIcons.GetStockIcon(StockIconId.Shield, LogicalToDeviceUnits(16));
                button.Image = shield.ToBitmap();
                button.TextImageRelation = TextImageRelation.ImageBeforeText;
            }

            return button;
        }

        var limit = MakeButton("Access.Limit", elevates: true);
        var everyone = MakeButton("Access.KeepEveryone", elevates: true);
        var later = MakeButton(firstTime ? "Access.Later" : "Access.Close", elevates: false);
        later.DialogResult = DialogResult.Cancel;
        limit.Click += (_, _) => Limit();
        everyone.Click += (_, _) => KeepForEveryone();

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            Margin = Padding.Empty,
        };
        buttons.Controls.AddRange([later, everyone, limit]);

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Margin = Padding.Empty,
            // The form sizes itself to this panel, so the space around the content lives here.
            Padding = new Padding(LogicalToDeviceUnits(18)),
        };
        layout.Controls.Add(heading);
        layout.Controls.Add(text);
        layout.Controls.Add(_users);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
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
        // Also when nothing changes on the folder: it is a decision for an administrator, the
        // same as limiting, and once made the app does not ask again.
        if (!RunElevated("--settings-access everyone"))
        {
            return;
        }

        // The administrator's process records the decision; without it nothing was decided.
        if (!SettingsStore.LoadOrCreate(_paths, _log).SettingsAccessDecided)
        {
            MessageBox.Show(this, Strings.Get("Access.NotChanged"), AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _settings.SettingsAccessDecided = true;
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

    /// <summary>True once the administrator's run has ended; it says what it changed on its own.</summary>
    private bool RunElevated(string arguments)
    {
        switch (ElevatedRun.Start(arguments + " --quiet", out var error))
        {
            case ElevatedOutcome.Finished:
                return true;
            case ElevatedOutcome.StillRunning:
                // It may still finish: not reported as a failure, and the dialog stays open.
                _log.Warning($"The settings access change has not finished yet ({arguments}).");
                MessageBox.Show(this, Strings.Get("Access.StillRunning"), AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            case ElevatedOutcome.Failed:
                _log.Warning($"Unable to change the settings access: {error}");
                MessageBox.Show(this, string.Format(Strings.Get("Access.Failed"), error), AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            default:
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
