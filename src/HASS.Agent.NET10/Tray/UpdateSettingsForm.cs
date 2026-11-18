using System.Drawing;
using System.Windows.Forms;
using HASS.Agent.Companion.Localization;
using HASS.Agent.Companion.Runtime;

namespace HASS.Agent.Companion.Tray;

/// <summary>
/// The About page's Update settings: the beta channel and "updates only at this PC". Only
/// asks; the About page saves what was chosen (and tells Home Assistant of the second one).
/// </summary>
internal sealed class UpdateSettingsForm : Form
{
    private readonly CheckBox _beta;
    private readonly CheckBox _localOnly;

    private UpdateSettingsForm(bool beta, bool localOnly)
    {
        Text = AppIdentity.DisplayName;
        Icon = DialogLook.LoadIcon();
        DialogLook.Apply(this);
        var width = LogicalToDeviceUnits(480);
        var gap = LogicalToDeviceUnits(10);

        _beta = DialogLook.WrappingCheck(this, Strings.Get("Danger.BetaUpdates"), beta, width);
        _localOnly = DialogLook.WrappingCheck(this, Strings.Get("Danger.LocalUpdatesOnly"), localOnly, width);
        _localOnly.Margin = new Padding(0, 0, 0, gap * 2);

        var save = DialogLook.Button(this, "Btn.Save");
        save.DialogResult = DialogResult.OK;
        var cancel = DialogLook.Button(this, "Btn.Cancel");
        cancel.DialogResult = DialogResult.Cancel;

        Controls.Add(DialogLook.Layout(this,
            DialogLook.Heading(this, Strings.Get("About.UpdateSettingsTitle"), width),
            _beta,
            _localOnly,
            DialogLook.Buttons(cancel, save)));
        AcceptButton = save;
        CancelButton = cancel;
    }

    /// <summary>What was chosen, or null when the dialog was cancelled.</summary>
    public static (bool Beta, bool LocalOnly)? Ask(IWin32Window owner, bool beta, bool localOnly)
    {
        using var form = new UpdateSettingsForm(beta, localOnly);
        return form.ShowDialog(owner) == DialogResult.OK
            ? (form._beta.Checked, form._localOnly.Checked)
            : null;
    }
}

/// <summary>What the proxy dialog was saved with.</summary>
internal sealed record UpdateProxyChoice(bool Custom, string Address, bool Login, string User, string Password)
{
    /// <summary>Never the password: a record would print it.</summary>
    public override string ToString() => UpdateProxy.Create(Custom, Address, Login, User, Password).ToString();
}

/// <summary>
/// The About page's Proxy settings: how the update check and download reach GitHub
/// (<see cref="UpdateProxy"/>). Test tries what the fields say; Save hands it back to the About
/// page, which keeps it. An address that cannot be used is not taken.
/// </summary>
internal sealed class UpdateProxyForm : Form
{
    private readonly RadioButton _windows = new() { AutoSize = true };
    private readonly RadioButton _custom = new() { AutoSize = true };
    private readonly TextBox _address = new() { PlaceholderText = "http://127.0.0.1:7890" };
    private readonly CheckBox _login = new() { AutoSize = true };
    private readonly TextBox _user = new();
    private readonly TextBox _password = new() { UseSystemPasswordChar = true };
    private readonly string _installedVersion;
    private readonly bool _beta;
    private readonly Button _save;

    private UpdateProxyForm(UpdateProxyChoice current, string installedVersion, bool beta)
    {
        _installedVersion = installedVersion;
        _beta = beta;
        Text = AppIdentity.DisplayName;
        Icon = DialogLook.LoadIcon();
        DialogLook.Apply(this);
        var width = LogicalToDeviceUnits(480);
        var gap = LogicalToDeviceUnits(8);

        _windows.Text = Strings.Get("About.ProxyWindows");
        _windows.Margin = new Padding(0, 0, 0, gap);
        _custom.Text = Strings.Get("About.ProxyCustom");
        _custom.Anchor = AnchorStyles.Left;
        _custom.Margin = new Padding(0, 0, gap, gap);
        _address.Width = LogicalToDeviceUnits(300);
        _address.Anchor = AnchorStyles.Left;
        _address.Margin = new Padding(0, 0, 0, gap);
        // Both radio buttons in one container: WinForms makes a group of the ones that share a parent.
        var mode = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Margin = Padding.Empty,
        };
        mode.Controls.Add(_windows, 0, 0);
        mode.SetColumnSpan(_windows, 2);
        mode.Controls.Add(_custom, 0, 1);
        mode.Controls.Add(_address, 1, 1);

        _login.Text = Strings.Get("About.ProxyLogin");
        _login.Margin = new Padding(0, 0, 0, gap);
        var fields = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Margin = new Padding(LogicalToDeviceUnits(20), 0, 0, gap),
        };
        foreach (var (key, box) in new[] { ("Mqtt.Username", _user), ("Mqtt.Password", _password) })
        {
            fields.Controls.Add(new Label { Text = Strings.Get(key), AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, gap, gap) });
            box.Width = LogicalToDeviceUnits(240);
            box.Margin = new Padding(0, 0, 0, gap);
            fields.Controls.Add(box);
        }

        var hint = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(width, 0),
            Margin = new Padding(0, gap, 0, gap * 2),
            ForeColor = SystemColors.GrayText,
            Text = Strings.Get("About.ProxyHint"),
        };

        _windows.Checked = !current.Custom;
        _custom.Checked = current.Custom;
        _address.Text = current.Address;
        _login.Checked = current.Login;
        _user.Text = current.User;
        _password.Text = current.Password;
        _custom.CheckedChanged += (_, _) => UpdateEnabled();
        _login.CheckedChanged += (_, _) => UpdateEnabled();
        UpdateEnabled();

        var save = DialogLook.Button(this, "Btn.Save");
        _save = save;
        save.Click += (_, _) =>
        {
            if (_custom.Checked && !UpdateProxy.TryParseAddress(_address.Text, out _))
            {
                MessageBox.Show(this, Strings.Get("About.ProxyInvalid"), AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _address.Focus();
                return;
            }

            DialogResult = DialogResult.OK;
        };
        var test = DialogLook.Button(this, "About.ProxyTest");
        test.Click += async (_, _) => await TestAsync(test);
        var cancel = DialogLook.Button(this, "Btn.Cancel");
        cancel.DialogResult = DialogResult.Cancel;

        Controls.Add(DialogLook.Layout(this,
            DialogLook.Heading(this, Strings.Get("About.Proxy"), width),
            mode,
            _login,
            fields,
            hint,
            DialogLook.Buttons(cancel, save, test)));
        AcceptButton = save;
        CancelButton = cancel;
    }

    private UpdateProxyChoice Choice => new(_custom.Checked, _address.Text.Trim(), _login.Checked, _user.Text.Trim(), _password.Text);

    /// <summary>What was saved, or null when the dialog was cancelled.</summary>
    public static UpdateProxyChoice? Ask(IWin32Window owner, UpdateProxyChoice current, string installedVersion, bool beta)
    {
        using var form = new UpdateProxyForm(current, installedVersion, beta);
        return form.ShowDialog(owner) == DialogResult.OK ? form.Choice : null;
    }

    private void UpdateEnabled()
    {
        _address.Enabled = _custom.Checked;
        _user.Enabled = _login.Checked;
        _password.Enabled = _login.Checked;
    }

    /// <summary>The update check through what the fields say, saved or not.</summary>
    private async Task TestAsync(Button test)
    {
        var choice = Choice;
        var proxy = UpdateProxy.Create(choice.Custom, choice.Address, choice.Login, choice.User, choice.Password);
        if (proxy.Problem is not null)
        {
            MessageBox.Show(this, Strings.Get("About.ProxyInvalid"), AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Nothing to change or save while it runs: what Save keeps is what was tried. Cancel stays.
        UseWaitCursor = true;
        SetFieldsEnabled(false, test);
        try
        {
            var state = await AppUpdateService.CheckAsync(_installedVersion, _beta, proxy);
            if (IsDisposed)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(state.Error))
            {
                MessageBox.Show(this, string.Format(Strings.Get("About.ProxyTestOk"), state.LatestVersion), AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(this, string.Format(Strings.Get("About.ProxyTestFailed"), AppUpdateService.Explain(state.Error)), AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            if (!IsDisposed)
            {
                UseWaitCursor = false;
                SetFieldsEnabled(true, test);
            }
        }
    }

    private void SetFieldsEnabled(bool enabled, Button test)
    {
        foreach (var control in new Control[] { _windows, _custom, _login, _save, test })
        {
            control.Enabled = enabled;
        }

        if (enabled)
        {
            UpdateEnabled();
        }
        else
        {
            _address.Enabled = _user.Enabled = _password.Enabled = false;
        }
    }
}

/// <summary>The look of the About page's small dialogs: the one of the Kiosk mode and Users of this PC dialogs.</summary>
internal static class DialogLook
{
    public static void Apply(Form form)
    {
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.MaximizeBox = false;
        form.MinimizeBox = false;
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.CenterParent;
        form.Font = new Font("Segoe UI", 9.5F);
        // Sized to its text and to the DPI of the screen.
        form.AutoScaleMode = AutoScaleMode.None;
        form.AutoSize = true;
        form.AutoSizeMode = AutoSizeMode.GrowAndShrink;
    }

    public static Label Heading(Control owner, string text, int width) => new()
    {
        AutoSize = true,
        MaximumSize = new Size(width, 0),
        Margin = new Padding(0, 0, 0, owner.LogicalToDeviceUnits(12)),
        Font = new Font("Segoe UI Semibold", 12F),
        Text = text,
    };

    /// <summary>A tick box whose text wraps at the width, with the box at its first line.</summary>
    public static CheckBox WrappingCheck(Control owner, string text, bool isChecked, int width)
    {
        var check = new CheckBox
        {
            Text = text,
            Checked = isChecked,
            TextAlign = ContentAlignment.TopLeft,
            CheckAlign = ContentAlignment.TopLeft,
            Margin = new Padding(0, 0, 0, owner.LogicalToDeviceUnits(10)),
        };
        var textHeight = TextRenderer.MeasureText(text, owner.Font, new Size(width - owner.LogicalToDeviceUnits(24), int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height;
        check.Size = new Size(width, Math.Max(owner.LogicalToDeviceUnits(24), textHeight + owner.LogicalToDeviceUnits(6)));
        return check;
    }

    public static Button Button(Control owner, string key) => new()
    {
        Text = Strings.Get(key),
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(owner.LogicalToDeviceUnits(88), 0),
        Padding = new Padding(owner.LogicalToDeviceUnits(10), owner.LogicalToDeviceUnits(4), owner.LogicalToDeviceUnits(10), owner.LogicalToDeviceUnits(4)),
        Margin = new Padding(owner.LogicalToDeviceUnits(8), 0, 0, 0),
    };

    /// <summary>The buttons at the bottom right, the first one rightmost.</summary>
    public static FlowLayoutPanel Buttons(params Button[] buttons)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            Margin = Padding.Empty,
        };
        row.Controls.AddRange(buttons);
        return row;
    }

    public static TableLayoutPanel Layout(Control owner, params Control[] rows)
    {
        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(owner.LogicalToDeviceUnits(18)),
        };
        layout.Controls.AddRange(rows);
        return layout;
    }

    public static Icon? LoadIcon()
    {
        try
        {
            using var stream = typeof(DialogLook).Assembly.GetManifestResourceStream("hassagent.ico");
            return stream is null ? null : new Icon(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
