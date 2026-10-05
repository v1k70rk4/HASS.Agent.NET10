using System.Drawing;
using System.Windows.Forms;
using HASS.Agent.Companion.Http;
using HASS.Agent.Companion.Runtime;

namespace HASS.Agent.Companion.Tray;

/// <summary>
/// The app's own notification window: always on top, also while Windows holds its own
/// notifications back (Do not disturb, a full-screen game). Shows the picture, the text
/// fields and the buttons of the notification.
/// </summary>
internal sealed class ActionNotificationForm : Form
{
    private const int WsExTopmost = 0x00000008;
    private const int LogicalWidth = 420;
    private const int SideMargin = 18;

    private static readonly Color SurfaceColor = Color.FromArgb(28, 31, 34);
    private static readonly Color BorderColor = Color.FromArgb(64, 68, 74);
    private static readonly Color PrimaryTextColor = Color.White;
    private static readonly Color SecondaryTextColor = Color.FromArgb(226, 229, 233);
    private static readonly Color ButtonColor = Color.FromArgb(52, 57, 63);
    private static readonly Color ButtonHoverColor = Color.FromArgb(66, 72, 80);
    private static readonly Color ButtonBorderColor = Color.FromArgb(96, 104, 114);
    private static readonly Color InputColor = Color.FromArgb(40, 44, 49);
    private static readonly Color AccentColor = Color.FromArgb(0, 120, 212);

    private readonly Action<string, IReadOnlyDictionary<string, string>> _actionSelected;
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly List<(string Id, TextBox Box)> _inputs = [];
    private readonly Image? _image;
    private Button? _firstAction;

    public ActionNotificationForm(
        NotificationPayload notification,
        string? imageFile,
        Action<string, IReadOnlyDictionary<string, string>> actionSelected)
    {
        _actionSelected = actionSelected;
        _image = LoadImage(imageFile);

        Text = AppIdentity.DisplayName;
        if (LoadAppIcon() is { } icon)
        {
            Icon = icon;
        }

        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = SurfaceColor;
        ForeColor = PrimaryTextColor;
        Font = new Font("Segoe UI", 9F);
        AutoScaleMode = AutoScaleMode.None;
        Padding = new Padding(0);
        ClientSize = new Size(D(LogicalWidth), D(200));

        var y = AddTitle(notification, D(16));
        y = AddMessage(notification, y);
        y = AddImage(y);
        y = AddInputs(notification, y);
        y = AddActionButtons(notification, y);
        y = AddDismissButton(y);
        ClientSize = new Size(D(LogicalWidth), y + D(14));

        PositionFromBottom(16);

        _timer.Interval = notification.TimeoutMilliseconds;
        _timer.Tick += (_, _) => Close();
        _timer.Start();
    }

    /// <summary>
    /// Shown without taking the focus: whoever is typing somewhere else keeps typing there,
    /// and a stray Enter does not press a button of a window that has just appeared.
    /// </summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            // Always on top through the window style: the TopMost property would activate
            // the window when it is shown.
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExTopmost;
            return parameters;
        }
    }

    public void PositionFromBottom(int bottomOffset)
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.WorkingArea;
        Location = new Point(area.Right - Width - D(16), area.Bottom - Height - bottomOffset);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var border = new Pen(BorderColor);
        e.Graphics.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);

        using var accent = new SolidBrush(AccentColor);
        e.Graphics.FillRectangle(accent, 0, 0, D(4), ClientSize.Height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _image?.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Logical (96 DPI) pixels to the pixels of the screen the window is on.</summary>
    private int D(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

    private int ContentWidth => ClientSize.Width - D(SideMargin) * 2;

    private int AddTitle(NotificationPayload notification, int y)
    {
        Controls.Add(new Label
        {
            AutoSize = false,
            Location = new Point(D(SideMargin), y),
            Size = new Size(ContentWidth, D(24)),
            Text = string.IsNullOrWhiteSpace(notification.Title) ? "Home Assistant" : notification.Title.Trim(),
            Font = new Font(Font, FontStyle.Bold),
            ForeColor = PrimaryTextColor,
            BackColor = BackColor,
            AutoEllipsis = true,
            UseMnemonic = false
        });

        return y + D(30);
    }

    private int AddMessage(NotificationPayload notification, int y)
    {
        var text = notification.Message?.Trim() ?? string.Empty;
        var measured = TextRenderer.MeasureText(
            text,
            Font,
            new Size(ContentWidth, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);

        // As tall as the text needs, up to a point; the rest is cut with an ellipsis.
        var height = Math.Clamp(measured.Height + D(4), D(22), D(150));
        Controls.Add(new Label
        {
            AutoSize = false,
            Location = new Point(D(SideMargin), y),
            Size = new Size(ContentWidth, height),
            Text = text,
            ForeColor = SecondaryTextColor,
            BackColor = BackColor,
            AutoEllipsis = true,
            UseMnemonic = false
        });

        return y + height + D(14);
    }

    private int AddImage(int y)
    {
        if (_image is null)
        {
            return y;
        }

        var scale = Math.Min((double)ContentWidth / _image.Width, (double)D(220) / _image.Height);
        scale = Math.Min(scale, 1.0 * DeviceDpi / 96.0);
        var size = new Size(
            Math.Max(1, (int)Math.Round(_image.Width * scale)),
            Math.Max(1, (int)Math.Round(_image.Height * scale)));

        Controls.Add(new PictureBox
        {
            Image = _image,
            SizeMode = PictureBoxSizeMode.Zoom,
            Location = new Point(D(SideMargin) + (ContentWidth - size.Width) / 2, y),
            Size = size,
            BackColor = BackColor
        });

        return y + size.Height + D(12);
    }

    private int AddInputs(NotificationPayload notification, int y)
    {
        foreach (var input in notification.Inputs)
        {
            // The hint stands above the field, not in it: a placeholder disappears the
            // moment the field has the focus, and with several fields nobody could tell
            // which is which any more.
            if (!string.IsNullOrWhiteSpace(input.Title))
            {
                var caption = new Label
                {
                    AutoSize = false,
                    Location = new Point(D(SideMargin), y),
                    Size = new Size(ContentWidth, D(18)),
                    Text = input.Title,
                    Font = new Font("Segoe UI", 8.5F),
                    ForeColor = SecondaryTextColor,
                    BackColor = BackColor,
                    AutoEllipsis = true,
                    UseMnemonic = false
                };
                Controls.Add(caption);
                y += caption.Height + D(2);
            }

            var box = new TextBox
            {
                Location = new Point(D(SideMargin), y),
                Width = ContentWidth,
                BackColor = InputColor,
                ForeColor = PrimaryTextColor,
                BorderStyle = BorderStyle.FixedSingle
            };

            // Somebody is answering: the window must not close under their hands.
            box.Enter += (_, _) => _timer.Stop();
            box.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Enter && _firstAction is not null)
                {
                    e.SuppressKeyPress = true;
                    _firstAction.PerformClick();
                }
            };

            _inputs.Add((input.Id!, box));
            Controls.Add(box);
            y += box.Height + D(8);
        }

        return _inputs.Count > 0 ? y + D(4) : y;
    }

    private int AddActionButtons(NotificationPayload notification, int y)
    {
        var actions = notification.Actions.ToList();
        if (actions.Count == 0 && _inputs.Count > 0)
        {
            // Text fields need a button to send them with.
            actions.Add(new NotificationActionPayload
            {
                Action = NotificationPayload.ReplyAction,
                Title = Localization.Strings.Get("Btn.Send")
            });
        }

        if (actions.Count == 0)
        {
            return y;
        }

        var x = D(SideMargin);
        foreach (var action in actions)
        {
            var button = CreateActionButton(action);
            if (x > D(SideMargin) && x + button.Width > ClientSize.Width - D(SideMargin))
            {
                x = D(SideMargin);
                y += D(42);
            }

            button.Location = new Point(x, y);
            Controls.Add(button);
            _firstAction ??= button;

            x += button.Width + D(8);
        }

        return y + D(42);
    }

    private int AddDismissButton(int y)
    {
        var dismiss = new Button
        {
            Text = Localization.Strings.Get("Btn.Close"),
            Size = new Size(D(92), D(32)),
            Location = new Point(D(SideMargin), y + D(4))
        };
        StyleButton(dismiss, isPrimary: false);
        dismiss.Click += (_, _) => Close();
        Controls.Add(dismiss);

        return dismiss.Bottom;
    }

    private Button CreateActionButton(NotificationActionPayload action)
    {
        var actionName = action.Action!.Trim();
        var title = string.IsNullOrWhiteSpace(action.Title) ? actionName : action.Title.Trim();

        var button = new Button
        {
            Text = title,
            UseMnemonic = false,
            AutoEllipsis = true,
            Size = new Size(
                Math.Clamp(TextRenderer.MeasureText(title, Font).Width + D(34), D(92), D(170)),
                D(34))
        };
        StyleButton(button, isPrimary: true);

        button.Click += (_, _) =>
        {
            _timer.Stop();
            _actionSelected(actionName, _inputs.ToDictionary(input => input.Id, input => input.Box.Text));
            Close();
        };

        return button;
    }

    private static void StyleButton(Button button, bool isPrimary)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = isPrimary ? ButtonColor : Color.FromArgb(40, 44, 49);
        button.ForeColor = PrimaryTextColor;
        button.FlatAppearance.BorderColor = isPrimary ? ButtonBorderColor : Color.FromArgb(72, 78, 86);
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = isPrimary ? ButtonHoverColor : Color.FromArgb(52, 57, 63);
        button.FlatAppearance.MouseDownBackColor = AccentColor;
        button.Font = new Font("Segoe UI", 9F, isPrimary ? FontStyle.Bold : FontStyle.Regular);
        button.Cursor = Cursors.Hand;
    }

    private static Icon? LoadAppIcon()
    {
        var stream = typeof(ActionNotificationForm).Assembly.GetManifestResourceStream("hassagent.ico");
        return stream is not null ? new Icon(stream) : null;
    }

    /// <summary>
    /// The picture from the cache file, read into memory so that the file is not kept
    /// open. Null when there is none or it cannot be read.
    /// </summary>
    private static Image? LoadImage(string? file)
    {
        if (file is null)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(File.ReadAllBytes(file));
            using var loaded = Image.FromStream(stream);
            return new Bitmap(loaded);
        }
        catch
        {
            return null;
        }
    }
}
