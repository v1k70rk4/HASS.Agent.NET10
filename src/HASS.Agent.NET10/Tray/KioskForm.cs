using System.Drawing;
using System.Windows.Forms;
using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Localization;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.Runtime;

namespace HASS.Agent.Companion.Tray;

/// <summary>
/// Kiosk mode, from the Danger Zone: who keeps the full app. Turning it on, changing the list
/// and turning it off run the app as administrator (UAC) with --kiosk. The administrators keep
/// the full app whatever is ticked, and so does whoever opened this.
/// </summary>
internal sealed class KioskForm : Form
{
    private readonly FileLog _log;
    private readonly CheckedListBox _users = new() { CheckOnClick = true, IntegralHeight = false, BorderStyle = BorderStyle.FixedSingle };
    private readonly List<string> _names = [];
    private readonly HashSet<int> _alwaysFull = [];

    private KioskForm(FileLog log)
    {
        _log = log;

        Text = AppIdentity.DisplayName;
        Icon = LoadIcon();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9.5F);

        // Sized to its text and to the DPI of the screen, like the Users of this PC dialog.
        AutoScaleMode = AutoScaleMode.None;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var width = LogicalToDeviceUnits(480);
        var gap = LogicalToDeviceUnits(10);

        var policy = KioskPolicy.Load(KioskPolicy.DefaultPath);
        var heading = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(width, 0),
            Margin = new Padding(0, 0, 0, gap),
            Font = new Font("Segoe UI Semibold", 12F),
            Text = Strings.Get("Kiosk.Heading"),
        };
        var text = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(width, 0),
            Margin = new Padding(0, 0, 0, gap),
            Text = Strings.Get("Kiosk.Text") + Environment.NewLine + Environment.NewLine
                + Strings.Get(policy is null ? "Kiosk.NowOff" : "Kiosk.NowOn"),
        };

        // The same users as the Users of this PC dialog lists, ticked when they keep the full
        // app. An administrator is ticked for good, and so is whoever opened this: leaving
        // yourself out would turn your own app into the kiosk one.
        var fullApp = policy?.FullAppSids() ?? [];
        var administrators = SafeList(SettingsAccess.AdministratorUsers);
        var current = CurrentUserName;
        var listed = SafeList(SettingsAccess.LocalUsers)
            .Concat(SafeList(SettingsAccess.DirectoryUsersWithProfiles).Where(user => !user.StartsWith("S-1-", StringComparison.Ordinal)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!listed.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            listed.Insert(0, current);
        }

        foreach (var user in listed)
        {
            var administrator = administrators.Contains(user, StringComparer.OrdinalIgnoreCase);
            var always = administrator || string.Equals(user, current, StringComparison.OrdinalIgnoreCase);
            var index = _users.Items.Add(
                administrator ? string.Format(Strings.Get("Kiosk.Administrator"), user) : user,
                always || IsIn(user, fullApp));
            _names.Add(user);
            if (always)
            {
                _alwaysFull.Add(index);
            }
        }

        _users.ItemCheck += (_, e) =>
        {
            if (_alwaysFull.Contains(e.Index))
            {
                e.NewValue = CheckState.Checked;
            }
        };
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
                using var shield = SystemIcons.GetStockIcon(StockIconId.Shield, LogicalToDeviceUnits(16));
                button.Image = shield.ToBitmap();
                button.TextImageRelation = TextImageRelation.ImageBeforeText;
            }

            return button;
        }

        var turnOn = MakeButton(policy is null ? "Kiosk.TurnOn" : "Kiosk.Save", elevates: true);
        var close = MakeButton("Access.Close", elevates: false);
        close.DialogResult = DialogResult.Cancel;
        turnOn.Click += async (_, _) => await WhileWaitingAsync(TurnOnAsync);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            Margin = Padding.Empty,
        };
        buttons.Controls.Add(close);
        if (policy is not null)
        {
            var turnOff = MakeButton("Kiosk.TurnOff", elevates: true);
            turnOff.Click += async (_, _) => await WhileWaitingAsync(TurnOffAsync);
            buttons.Controls.Add(turnOff);
        }

        buttons.Controls.Add(turnOn);

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(LogicalToDeviceUnits(18)),
        };
        layout.Controls.Add(heading);
        layout.Controls.Add(text);
        layout.Controls.Add(_users);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = turnOn;
        CancelButton = close;
    }

    public static void Show(IWin32Window owner, FileLog log)
    {
        using var form = new KioskForm(log);
        form.ShowDialog(owner);
    }

    private static IReadOnlyList<string> SafeList(Func<IReadOnlyList<string>> list)
    {
        try
        {
            return list();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static bool IsIn(string user, IReadOnlyList<System.Security.Principal.SecurityIdentifier> sids)
    {
        try
        {
            return sids.Contains(SettingsAccess.Sid(user));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The user running this, the way the list names users: bare for a local account, DOMAIN\user otherwise.</summary>
    private static string CurrentUserName =>
        string.Equals(Environment.UserDomainName, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            ? Environment.UserName
            : $"{Environment.UserDomainName}\\{Environment.UserName}";

    private async Task TurnOnAsync()
    {
        var users = _users.CheckedIndices.Cast<int>().Select(index => _names[index]).ToList();
        if (!await RunElevatedAsync("--kiosk on " + string.Join(' ', users.Select(Quote))))
        {
            return;
        }

        if (KioskPolicy.Load(KioskPolicy.DefaultPath) is null)
        {
            MessageBox.Show(this, Strings.Get("Kiosk.NotChanged"), AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        MessageBox.Show(this, string.Format(Strings.Get("Kiosk.On"), string.Join(", ", users)), AppIdentity.DisplayName,
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        DialogResult = DialogResult.OK;
    }

    private async Task TurnOffAsync()
    {
        if (!await RunElevatedAsync("--kiosk off"))
        {
            return;
        }

        if (KioskPolicy.Load(KioskPolicy.DefaultPath) is not null)
        {
            MessageBox.Show(this, Strings.Get("Kiosk.NotChanged"), AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        MessageBox.Show(this, Strings.Get("Kiosk.Off"), AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        DialogResult = DialogResult.OK;
    }

    // The dialog stays responsive while the administrator's run is waited for, with its
    // buttons off so that nobody starts a second one meanwhile.
    private async Task WhileWaitingAsync(Func<Task> action)
    {
        UseWaitCursor = true;
        foreach (var button in Controls.OfType<Control>().SelectMany(Descendants).OfType<Button>())
        {
            button.Enabled = false;
        }

        try
        {
            await action();
        }
        finally
        {
            if (!IsDisposed)
            {
                UseWaitCursor = false;
                foreach (var button in Controls.OfType<Control>().SelectMany(Descendants).OfType<Button>())
                {
                    button.Enabled = true;
                }
            }
        }

        static IEnumerable<Control> Descendants(Control control) =>
            new[] { control }.Concat(control.Controls.OfType<Control>().SelectMany(Descendants));
    }

    /// <summary>True once the administrator's run has ended without an error.</summary>
    private async Task<bool> RunElevatedAsync(string arguments)
    {
        var (outcome, error) = await ElevatedRun.StartAsync(arguments + " --quiet");
        switch (outcome)
        {
            case ElevatedOutcome.Finished:
                return true;
            case ElevatedOutcome.StillRunning:
                _log.Warning($"The kiosk mode change has not finished yet ({arguments}).");
                MessageBox.Show(this, Strings.Get("Access.StillRunning"), AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            case ElevatedOutcome.Busy:
                _log.Warning($"The kiosk mode change was not started: an earlier administrator run is still going ({arguments}).");
                MessageBox.Show(this, Strings.Get("Access.Busy"), AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            case ElevatedOutcome.NotStarted:
            case ElevatedOutcome.Failed:
                _log.Warning($"Unable to change kiosk mode: {error}");
                MessageBox.Show(this, string.Format(Strings.Get("Kiosk.Failed"), error), AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            using var stream = typeof(KioskForm).Assembly.GetManifestResourceStream("hassagent.ico");
            return stream is null ? null : new Icon(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
