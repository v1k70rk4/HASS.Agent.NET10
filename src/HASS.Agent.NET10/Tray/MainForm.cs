using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Localization;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.Mqtt;
using HASS.Agent.Companion.Networking;
using HASS.Agent.Companion.Runtime;
using HASS.Agent.Companion.SystemCommands;
using HASS.Agent.Companion.SystemService;
using HASS.Agent.Companion.SystemStatus;
using MQTTnet;

namespace HASS.Agent.Companion.Tray;

internal sealed class MainForm : Form
{
    private static readonly Color SidebarBg = Color.FromArgb(15, 23, 42);
    private static readonly Color SidebarHover = Color.FromArgb(30, 41, 59);
    private static readonly Color SidebarActiveBg = Color.FromArgb(30, 41, 59);
    private static readonly Color SidebarText = Color.FromArgb(148, 163, 184);
    private static readonly Color SidebarTextActive = Color.White;
    private static readonly Color Accent = Color.FromArgb(56, 189, 248);
    private static readonly Color PageBg = Color.FromArgb(241, 245, 249);
    private static readonly Color CardBg = Color.White;
    private static readonly Color BorderClr = Color.FromArgb(226, 232, 240);
    private static readonly Color TextDark = Color.FromArgb(15, 23, 42);
    private static readonly Color TextBody = Color.FromArgb(51, 65, 85);
    private static readonly Color TextMuted = Color.FromArgb(100, 116, 139);
    private static readonly Color BtnBlue = Color.FromArgb(37, 99, 235);
    private static readonly Color BtnBlueHover = Color.FromArgb(29, 78, 216);

    private readonly CompanionSettings _settings;
    private readonly AppPaths _paths;
    private readonly FileLog _log;

    private int _selectedPage;
    private readonly List<NavEntry> _nav = [];
    private readonly List<Panel> _pages = [];
    private readonly Panel _content;

    private readonly TextBox _deviceName = new();
    private readonly TextBox _bindHost = new();
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535 };
    private readonly CheckBox _autoStart = new();
    private readonly CheckBox _showStartup = new();
    private readonly ComboBox _langCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _haLangCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _generalNotConfiguredError = new();
    private readonly Label _generalServiceWarning = new();
    private Control? _generalDeviceCard;
    private Control? _generalNetworkCard;
    private Control? _generalFilesCard;
    private Control? _generalDangerCard;
    private readonly Label _serviceWarning = new();
    private Control? _serviceStatusCard;
    private Control? _serviceActionsCard;

    private readonly CheckBox _mqttEnabled = new();
    private readonly Label _mqttWarning = new();
    private readonly TextBox _mqttHost = new();
    private readonly NumericUpDown _mqttPort = new() { Minimum = 1, Maximum = 65535 };
    private readonly TextBox _mqttUser = new();
    private readonly TextBox _mqttPass = new() { UseSystemPasswordChar = true };
    private readonly CheckBox _mqttTls = new();
    private readonly CheckBox _mqttRetain = new();
    private readonly Label _mqttTestResult = new();

    private readonly CheckBox _haApiEnabled = new();
    private readonly TextBox _haApiUrl = new();
    private readonly TextBox _haApiToken = new() { UseSystemPasswordChar = true };
    private readonly Label _haApiHttpWarningIcon = new();
    private readonly Label _haApiTestResult = new();
    private readonly Label _haApiDisabledWarning = new();

    private readonly CheckBox _capNotify = new();
    private readonly CheckBox _capMedia = new();
    private readonly CheckBox _capSensorsService = new();
    private readonly CheckBox _capSensorsApp = new();
    private readonly NumericUpDown _fastSensorInterval = new() { Minimum = 10, Maximum = 3600 };
    private readonly NumericUpDown _normalSensorInterval = new() { Minimum = 10, Maximum = 86400 };
    private readonly NumericUpDown _hourlySensorInterval = new() { Minimum = 10, Maximum = 86400 };
    private readonly List<CmdRow> _cmdRows = [];

    private readonly DataGridView _builtInGrid = new();
    private readonly DataGridView _customGrid = new();

    private readonly CheckBox _dangerZoneCheck = new();
    private readonly ListView _dangerList = new();
    private readonly Label _dangerStatus = new();
    private readonly List<Button> _dangerButtons = [];
    private readonly TextBox _debugLogBox = new();
    private readonly TextBox _debugFilter = new();
    private readonly System.Windows.Forms.Timer _debugTimer = new() { Interval = 1000 };
    private string _debugRendered = string.Empty;
    private readonly ListView _monitorList = new();
    private readonly TextBox _monitorPayload = new();
    private MqttLiveMonitor? _liveMonitor;

    public event EventHandler? SettingsSaved;

    /// <summary>Wired by TrayApplicationContext to the MQTT service's discovery republish.</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<Task<bool>>? DiscoveryRepublishHandler { get; set; }

    public MainForm(CompanionSettings settings, AppPaths paths, FileLog log, int initialPage = 0)
    {
        _settings = settings;
        _paths = paths;
        _log = log;

        Text = AppIdentity.DisplayName;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5F);
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = Sz(900, 600);
        MinimumSize = Sz(700, 480);
        BackColor = PageBg;
        Icon = LoadIcon();

        var sidebar = new Panel { Dock = DockStyle.Left, Width = D(200), BackColor = SidebarBg };
        BuildSidebar(sidebar);

        var bottomBar = BuildBottomBar();

        _content = new Panel { Dock = DockStyle.Fill, BackColor = PageBg };

        Controls.Add(_content);
        Controls.Add(bottomBar);
        Controls.Add(sidebar);

        _content.Controls.Add(BuildGeneralPage());
        _content.Controls.Add(BuildMqttPage());
        _content.Controls.Add(BuildHaApiPage());
        _content.Controls.Add(BuildCapabilitiesPage());
        _content.Controls.Add(BuildSensorsPage());
        _content.Controls.Add(BuildServicePage());
        _content.Controls.Add(BuildAboutPage());
        _content.Controls.Add(BuildDangerZonePage());

        LoadSettings();
        SelectPage(initialPage);

        FormClosed += (_, _) =>
        {
            _debugTimer.Stop();
            _ = _liveMonitor?.StopAsync();
            _liveMonitor = null;
        };
    }

    public void NavigateToPage(int index) => SelectPage(index);

    // ── DPI helpers ────────────────────────────────────────────────
    // All layout values in this file are authored at 96 DPI (100%).
    // D() scales a single int, Pt()/Sz() scale a Point/Size.
    // Font sizes are in points and are NOT scaled (points are DPI-independent).

    private int D(int v) => (int)(v * DeviceDpi / 96f);
    private Point Pt(int x, int y) => new(D(x), D(y));
    private Size Sz(int w, int h) => new(D(w), D(h));

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var screen = Screen.FromControl(this).WorkingArea;
        if (Width > screen.Width || Height > screen.Height)
        {
            var w = Math.Min(Width, screen.Width);
            var h = Math.Min(Height, screen.Height);
            SetBounds(
                screen.X + (screen.Width - w) / 2,
                screen.Y + (screen.Height - h) / 2,
                w, h);
        }
    }

    private static string S(string key) => Strings.Get(key);

    // ── Sidebar ────────────────────────────────────────────────────

    private void BuildSidebar(Panel sidebar)
    {
        var header = new Panel { Dock = DockStyle.Top, Height = D(72), BackColor = SidebarBg };
        header.Controls.Add(new Label
        {
            Text = ".NET 10", Font = new Font("Segoe UI", 9F), ForeColor = Accent,
            Location = Pt(20, 46), AutoSize = true
        });
        header.Controls.Add(new Label
        {
            Text = "HASS.Agent", Font = new Font("Segoe UI", 15F, FontStyle.Bold),
            ForeColor = SidebarTextActive, Location = Pt(20, 18), AutoSize = true
        });

        var sep = new Panel { Dock = DockStyle.Top, Height = D(1), BackColor = Color.FromArgb(51, 65, 85) };

        var version = new Label
        {
            Text = $"v{_settings.SoftwareVersion}", Dock = DockStyle.Bottom, Height = D(36),
            ForeColor = TextMuted, Font = new Font("Segoe UI", 8.5F), TextAlign = ContentAlignment.MiddleCenter
        };

        var navContainer = new Panel { Dock = DockStyle.Fill, BackColor = SidebarBg };
        string[] navKeys = ["Nav.General", "Nav.Mqtt", "Nav.HaApi", "Nav.Capabilities", "Nav.Sensors", "Nav.Service", "Nav.About", "Nav.DangerZone"];
        for (var i = 0; i < navKeys.Length; i++)
            navContainer.Controls.Add(CreateNavItem(S(navKeys[i]), i));

        // The Danger Zone tab is opt-in via the General page checkbox.
        _nav[^1].Panel.Visible = _settings.DangerZoneEnabled;

        sidebar.Controls.Add(navContainer);
        sidebar.Controls.Add(version);
        sidebar.Controls.Add(sep);
        sidebar.Controls.Add(header);
    }

    private Panel CreateNavItem(string text, int index)
    {
        var item = new Panel { Location = new Point(0, D(8 + index * 44)), Size = Sz(200, 44), BackColor = SidebarBg, Cursor = Cursors.Hand };
        var indicator = new Panel { Location = Point.Empty, Size = Sz(3, 44), BackColor = Color.Transparent };
        var label = new Label
        {
            Text = text, ForeColor = SidebarText, Font = new Font("Segoe UI", 10.5F),
            AutoSize = false, Location = Pt(22, 0), Size = Sz(174, 44),
            TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent
        };

        item.Controls.Add(indicator);
        item.Controls.Add(label);

        void onClick(object? s, EventArgs e) => SelectPage(index);
        item.Click += onClick;
        label.Click += onClick;

        void onEnter(object? s, EventArgs e) { if (_selectedPage != index) item.BackColor = SidebarHover; }
        void onLeave(object? s, EventArgs e)
        {
            if (!item.ClientRectangle.Contains(item.PointToClient(Cursor.Position)))
                if (_selectedPage != index) item.BackColor = SidebarBg;
        }
        item.MouseEnter += onEnter; item.MouseLeave += onLeave;
        label.MouseEnter += onEnter; label.MouseLeave += onLeave;
        indicator.MouseEnter += onEnter; indicator.MouseLeave += onLeave;

        _nav.Add(new NavEntry(item, indicator, label));
        return item;
    }

    private void SelectPage(int index)
    {
        _selectedPage = index;
        for (var i = 0; i < _nav.Count; i++)
        {
            var selected = i == index;
            _nav[i].Panel.BackColor = selected ? SidebarActiveBg : SidebarBg;
            _nav[i].Indicator.BackColor = selected ? Accent : Color.Transparent;
            _nav[i].Label.ForeColor = selected ? SidebarTextActive : SidebarText;
            _nav[i].Label.Font = new Font("Segoe UI", 10.5F, selected ? FontStyle.Bold : FontStyle.Regular);
        }
        for (var i = 0; i < _pages.Count; i++)
            _pages[i].Visible = i == index;
    }

    // ── Bottom bar ─────────────────────────────────────────────────

    private Panel BuildBottomBar()
    {
        var bar = new Panel { Dock = DockStyle.Bottom, Height = D(56), BackColor = CardBg };
        bar.Paint += (_, e) => { using var p = new Pen(BorderClr); e.Graphics.DrawLine(p, 0, 0, bar.Width, 0); };

        var save = MakePrimaryButton(S("Btn.Save"), 110, 36);
        save.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        save.Click += (_, _) => SaveSettings();

        var close = MakeSecondaryButton(S("Btn.Close"), 100, 36);
        close.Anchor = AnchorStyles.Right | AnchorStyles.Top;

        bar.Layout += (_, _) =>
        {
            close.Location = new Point(bar.ClientSize.Width - close.Width - D(20), D(10));
            save.Location = new Point(close.Left - save.Width - D(10), D(10));
        };
        close.Click += (_, _) => Close();

        bar.Controls.Add(save);
        bar.Controls.Add(close);
        return bar;
    }

    // ── Pages ──────────────────────────────────────────────────────

    private Panel BuildGeneralPage()
    {
        var page = MakePage();

        AddPageTitle(page, S("General.Title"));

        ConfigureStatusLabel(
            _generalNotConfiguredError,
            "⚠  " + S("General.NotConfiguredError"),
            Color.FromArgb(153, 27, 27),
            Color.White);
        ConfigureStatusLabel(
            _generalServiceWarning,
            "⚠  " + S("General.ServiceNotInstalledWarning"),
            Color.FromArgb(255, 243, 224),
            Color.FromArgb(180, 60, 0));
        page.Controls.Add(_generalNotConfiguredError);
        page.Controls.Add(_generalServiceWarning);
        page.Layout += (_, _) => LayoutGeneralStatusMessages();

        var card1 = MakeCard(page, 28, 64, 720, 320, S("General.Device"));
        _generalDeviceCard = card1;
        var y = 44;
        y = AddField(card1, S("General.DeviceName"), _deviceName, y);
        y = AddField(card1, S("General.BindHost"), _bindHost, y);
        y = AddField(card1, S("General.Port"), _port, y, inputWidth: 120);
        y = AddCheck(card1, _autoStart, S("General.AutoStart"), y + 4);
        y = AddCheck(card1, _showStartup, S("General.ShowStartup"), y);
        y += 6;

        card1.Controls.Add(new Label
        {
            Text = S("General.Language"), Location = Pt(20, y + 4),
            Size = Sz(160, 22), ForeColor = TextBody
        });
        foreach (var lang in Strings.AvailableLanguages)
        {
            _langCombo.Items.Add(Strings.GetDisplayName(lang));
            _haLangCombo.Items.Add(Strings.GetDisplayName(lang));
        }
        _langCombo.Location = Pt(188, y);
        _langCombo.Size = Sz(180, 28);
        card1.Controls.Add(_langCombo);
        y += 34;

        card1.Controls.Add(new Label
        {
            Text = S("General.HaLanguage"), Location = Pt(20, y + 4),
            Size = Sz(160, 22), ForeColor = TextBody
        });
        _haLangCombo.Location = Pt(188, y);
        _haLangCombo.Size = Sz(180, 28);
        card1.Controls.Add(_haLangCombo);

        var card2 = MakeCard(page, 28, 404, 720, 194, S("General.Network"));
        _generalNetworkCard = card2;
        var urls = string.Join(Environment.NewLine, NetworkInfo.GetLanUrls(_settings.Port));
        var urlBox = new TextBox
        {
            Text = urls, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical,
            Location = Pt(20, 44), Size = Sz(540, 52),
            BackColor = PageBg, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 9F)
        };
        card2.Controls.Add(urlBox);
        var copyBtn = MakeSecondaryButton(S("General.CopyUrl"), 120, 30);
        copyBtn.Location = Pt(20, 100);
        copyBtn.Click += (_, _) => Clipboard.SetText(NetworkInfo.GetPreferredLanUrl(_settings.Port));
        card2.Controls.Add(copyBtn);

        card2.Controls.Add(new Label
        {
            Text = S("General.ApiKey"), Location = Pt(20, 140),
            Size = Sz(80, 22), ForeColor = TextBody
        });
        var apiKeyBox = new TextBox
        {
            Text = _settings.ApiKey, ReadOnly = true,
            Location = Pt(104, 136), Size = Sz(400, 28),
            BackColor = PageBg, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 9F)
        };
        card2.Controls.Add(apiKeyBox);
        var copyKeyBtn = MakeSecondaryButton(S("General.CopyApiKey"), 100, 28);
        copyKeyBtn.Location = Pt(512, 136);
        copyKeyBtn.Click += (_, _) => Clipboard.SetText(_settings.ApiKey);
        card2.Controls.Add(copyKeyBtn);

        card2.Layout += (_, _) =>
        {
            var margin = D(20);
            urlBox.Width = Math.Max(D(160), Math.Min(D(540), card2.ClientSize.Width - urlBox.Left - margin));
            copyKeyBtn.Left = Math.Min(D(512), card2.ClientSize.Width - copyKeyBtn.Width - margin);
            apiKeyBox.Width = Math.Max(D(100), copyKeyBtn.Left - apiKeyBox.Left - D(8));
        };

        var card3 = MakeCard(page, 28, 618, 720, 120, S("General.Files"));
        _generalFilesCard = card3;
        card3.Controls.Add(new Label
        {
            Text = $"{S("General.Settings")}: {_paths.SettingsFile}", Location = Pt(20, 42),
            Size = Sz(660, 20), ForeColor = TextMuted, Font = new Font("Segoe UI", 8.5F)
        });
        card3.Controls.Add(new Label
        {
            Text = $"{S("General.Log")}: {_paths.LogFile}", Location = Pt(20, 60),
            Size = Sz(660, 20), ForeColor = TextMuted, Font = new Font("Segoe UI", 8.5F)
        });
        var openBtn = MakeSecondaryButton(S("General.OpenFolder"), 140, 28);
        openBtn.Location = Pt(20, 82);
        openBtn.Click += (_, _) => OpenFolder(_paths.ConfigDirectory);
        card3.Controls.Add(openBtn);

        var card4 = MakeCard(page, 28, 758, 720, 64, null);
        _generalDangerCard = card4;
        AddCheck(card4, _dangerZoneCheck, S("Danger.Title"), 18);
        _dangerZoneCheck.ForeColor = Color.FromArgb(185, 28, 28);
        _dangerZoneCheck.CheckedChanged += (_, _) => _nav[^1].Panel.Visible = _dangerZoneCheck.Checked;

        UpdateGeneralStatusMessages();
        return page;
    }

    private Panel BuildMqttPage()
    {
        var page = MakePage();

        AddPageTitle(page, S("Mqtt.Title"));

        ConfigureStatusLabel(
            _mqttWarning,
            "⚠  " + S("Mqtt.NotConfigured"),
            Color.FromArgb(255, 243, 224),
            Color.FromArgb(180, 60, 0));
        _mqttWarning.Location = Pt(28, 56);
        _mqttWarning.Size = Sz(600, 34);
        _mqttWarning.Visible = !_settings.MqttEnabled;
        page.Controls.Add(_mqttWarning);

        var cardTop = _settings.MqttEnabled ? 56 : 96;
        var card = MakeCard(page, 28, cardTop, 600, 352, S("Mqtt.Connection"));
        void LayoutMqttPage()
        {
            _mqttWarning.Location = Pt(28, 56);
            _mqttWarning.Size = new Size(card.Width, D(34));
            card.Top = _mqttWarning.Visible ? D(96) : D(56);
            _mqttWarning.BringToFront();
            _mqttTestResult.Width = Math.Max(D(220), card.ClientSize.Width - D(208));
        }

        page.Layout += (_, _) => LayoutMqttPage();
        var y = 44;
        y = AddCheck(card, _mqttEnabled, S("Mqtt.Enable"), y);
        _mqttEnabled.CheckedChanged += (_, _) =>
        {
            _mqttWarning.Visible = !_mqttEnabled.Checked;
            LayoutMqttPage();
        };
        y += 6;
        y = AddField(card, S("Mqtt.BrokerHost"), _mqttHost, y);
        y = AddField(card, S("Mqtt.Port"), _mqttPort, y, inputWidth: 120);
        y = AddField(card, S("Mqtt.Username"), _mqttUser, y);
        y = AddField(card, S("Mqtt.Password"), _mqttPass, y);
        y += 4;
        y = AddCheck(card, _mqttTls, S("Mqtt.UseTls"), y);
        y = AddCheck(card, _mqttRetain, S("Mqtt.RetainDiscovery"), y);
        y += 6;

        var testBtn = MakeSecondaryButton(S("Mqtt.TestButton"), 160, 32);
        testBtn.Location = Pt(20, y);
        testBtn.Click += async (_, _) => await TestMqttConnectionAsync();
        card.Controls.Add(testBtn);

        _mqttTestResult.Location = Pt(188, y);
        _mqttTestResult.Size = Sz(380, 32);
        _mqttTestResult.ForeColor = TextMuted;
        _mqttTestResult.Font = new Font("Segoe UI", 9F);
        _mqttTestResult.TextAlign = ContentAlignment.MiddleLeft;
        card.Controls.Add(_mqttTestResult);

        return page;
    }

    private async Task TestMqttConnectionAsync()
    {
        var host = _mqttHost.Text.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            _mqttTestResult.ForeColor = Color.FromArgb(153, 27, 27);
            _mqttTestResult.Text = S("Mqtt.HostRequired");
            return;
        }

        _mqttTestResult.ForeColor = TextMuted;
        _mqttTestResult.Text = "...";

        try
        {
            // Tests the values currently in the form, not the saved settings.
            var factory = new MQTTnet.MqttClientFactory();
            using var client = factory.CreateMqttClient();
            var builder = factory.CreateClientOptionsBuilder()
                .WithClientId(_settings.GetMqttClientId("test"))
                .WithTcpServer(host, (int)_mqttPort.Value)
                .WithCleanSession()
                .WithTimeout(TimeSpan.FromSeconds(10));

            if (!string.IsNullOrWhiteSpace(_mqttUser.Text))
            {
                builder.WithCredentials(_mqttUser.Text.Trim(), _mqttPass.Text);
            }

            if (_mqttTls.Checked)
            {
                builder.WithTlsOptions(tls => tls.UseTls());
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(builder.Build(), cts.Token);
            await client.DisconnectAsync();

            _mqttTestResult.ForeColor = Color.FromArgb(21, 128, 61);
            _mqttTestResult.Text = "✓  " + S("Mqtt.TestSuccess");
        }
        catch (OperationCanceledException)
        {
            _mqttTestResult.ForeColor = Color.FromArgb(153, 27, 27);
            _mqttTestResult.Text = string.Format(S("Mqtt.TestFailed"), S("Mqtt.TestTimeout"));
        }
        catch (Exception ex)
        {
            _mqttTestResult.ForeColor = Color.FromArgb(153, 27, 27);
            _mqttTestResult.Text = string.Format(S("Mqtt.TestFailed"), ex.Message);
        }
    }

    private Panel BuildHaApiPage()
    {
        var page = MakePage();

        AddPageTitle(page, S("HaApi.Title"));

        // Warning when HA API is disabled
        ConfigureStatusLabel(
            _haApiDisabledWarning,
            "⚠  " + S("HaApi.DisabledWarning"),
            Color.FromArgb(255, 243, 224),
            Color.FromArgb(180, 60, 0));
        _haApiDisabledWarning.Location = Pt(28, 56);
        _haApiDisabledWarning.Size = Sz(600, 34);
        _haApiDisabledWarning.Visible = !_settings.HaApiEnabled;
        page.Controls.Add(_haApiDisabledWarning);

        // Description
        var descLabel = new Label
        {
            Text = "ℹ  " + S("HaApi.Description"),
            Font = new Font("Segoe UI", 9.5F),
            ForeColor = Color.FromArgb(21, 128, 61),
            BackColor = Color.FromArgb(220, 252, 231),
            AutoSize = false,
            Location = Pt(28, _haApiDisabledWarning.Visible ? 98 : 56),
            Size = Sz(600, 32),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(D(12), 0, D(12), 0),
        };
        page.Controls.Add(descLabel);

        Label limLabel = null!;
        var cardTop = _haApiDisabledWarning.Visible ? 138 : 96;
        var card = MakeCard(page, 28, cardTop, 600, 300, S("HaApi.Connection"));
        void LayoutHaApiPage()
        {
            _haApiDisabledWarning.Location = Pt(28, 56);
            _haApiDisabledWarning.Size = new Size(card.Width, D(34));
            descLabel.Location = Pt(28, _haApiDisabledWarning.Visible ? 98 : 56);
            descLabel.Size = new Size(card.Width, D(32));
            card.Top = _haApiDisabledWarning.Visible ? D(138) : D(96);
            _haApiHttpWarningIcon.Location = new Point(_haApiUrl.Right + D(8), _haApiUrl.Top);
            _haApiTestResult.Width = Math.Max(D(220), card.ClientSize.Width - D(208));
            if (limLabel is not null)
            {
                limLabel.Location = new Point(D(28), card.Bottom + D(8));
                limLabel.Size = new Size(card.Width, D(44));
                limLabel.BringToFront();
            }

            _haApiDisabledWarning.BringToFront();
            descLabel.BringToFront();
        }

        page.Layout += (_, _) => LayoutHaApiPage();
        var y = 44;
        y = AddCheck(card, _haApiEnabled, S("HaApi.Enable"), y);
        _haApiEnabled.CheckedChanged += (_, _) =>
        {
            _haApiDisabledWarning.Visible = !_haApiEnabled.Checked;
            LayoutHaApiPage();
        };
        y += 6;
        y = AddField(card, S("HaApi.Url"), _haApiUrl, y);

        var httpWarningTip = new ToolTip
        {
            AutomaticDelay = 150,
            AutoPopDelay = 8000,
            InitialDelay = 150,
            ReshowDelay = 100,
            ShowAlways = true
        };
        _haApiHttpWarningIcon.Text = "⚠";
        _haApiHttpWarningIcon.Font = new Font("Segoe UI", 12F, FontStyle.Bold);
        _haApiHttpWarningIcon.ForeColor = Color.FromArgb(180, 60, 0);
        _haApiHttpWarningIcon.BackColor = Color.Transparent;
        _haApiHttpWarningIcon.AutoSize = false;
        _haApiHttpWarningIcon.Size = Sz(28, 28);
        _haApiHttpWarningIcon.Location = new Point(_haApiUrl.Right + D(8), _haApiUrl.Top);
        _haApiHttpWarningIcon.TextAlign = ContentAlignment.MiddleCenter;
        _haApiHttpWarningIcon.Cursor = Cursors.Help;
        _haApiHttpWarningIcon.Visible = false;
        httpWarningTip.SetToolTip(_haApiHttpWarningIcon, S("HaApi.HttpWarning"));
        httpWarningTip.SetToolTip(_haApiUrl, S("HaApi.HttpWarning"));
        card.Controls.Add(_haApiHttpWarningIcon);
        card.Layout += (_, _) => _haApiHttpWarningIcon.Location = new Point(_haApiUrl.Right + D(8), _haApiUrl.Top);

        _haApiUrl.TextChanged += (_, _) =>
        {
            var url = _haApiUrl.Text.Trim();
            _haApiHttpWarningIcon.Visible = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        };

        y += 4;
        y = AddField(card, S("HaApi.Token"), _haApiToken, y);
        y += 6;

        var testBtn = MakeSecondaryButton(S("HaApi.TestButton"), 160, 32);
        testBtn.Location = Pt(20, y);
        testBtn.Click += async (_, _) => await TestHaApiConnectionAsync();
        card.Controls.Add(testBtn);

        _haApiTestResult.Location = Pt(188, y);
        _haApiTestResult.Size = Sz(380, 46);
        _haApiTestResult.ForeColor = TextMuted;
        _haApiTestResult.Font = new Font("Segoe UI", 9F);
        _haApiTestResult.TextAlign = ContentAlignment.MiddleLeft;
        card.Controls.Add(_haApiTestResult);

        limLabel = new Label
        {
            Text = "⚠  " + S("HaApi.Limitations"),
            Font = new Font("Segoe UI", 8.5F),
            ForeColor = Color.FromArgb(120, 90, 0),
            BackColor = Color.FromArgb(255, 251, 235),
            AutoSize = false,
            Location = Pt(28, card.Bottom + D(8)),
            Size = Sz(600, 44),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(D(12), 0, D(12), 0),
        };
        page.Controls.Add(limLabel);
        LayoutHaApiPage();

        return page;
    }

    private async Task TestHaApiConnectionAsync()
    {
        var url = _haApiUrl.Text.Trim();
        var token = _haApiToken.Text.Trim();

        if (string.IsNullOrWhiteSpace(url))
        {
            _haApiTestResult.ForeColor = Color.FromArgb(153, 27, 27);
            _haApiTestResult.Text = S("HaApi.UrlRequired");
            return;
        }

        _haApiTestResult.ForeColor = TextMuted;
        _haApiTestResult.Text = "...";

        try
        {
            using var testWs = new Mqtt.HaWebSocketService(_settings, _log);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await testWs.TestConnectionAsync(url, token, cts.Token);
            if (!string.IsNullOrWhiteSpace(result.IntegrationVersionError))
            {
                _haApiTestResult.ForeColor = Color.FromArgb(194, 65, 12);
                _haApiTestResult.Text = string.Format(S("HaApi.TestIntegrationCheckFailed"), result.HomeAssistantVersion, result.IntegrationVersionError);
                return;
            }

            if (string.IsNullOrWhiteSpace(result.IntegrationVersion))
            {
                _haApiTestResult.ForeColor = Color.FromArgb(194, 65, 12);
                _haApiTestResult.Text = string.Format(S("HaApi.TestIntegrationMissing"), result.HomeAssistantVersion, Mqtt.HaWebSocketService.MinimumIntegrationVersion);
                return;
            }

            if (!Mqtt.HaWebSocketService.IsIntegrationVersionSupported(result.IntegrationVersion))
            {
                _haApiTestResult.ForeColor = Color.FromArgb(194, 65, 12);
                _haApiTestResult.Text = string.Format(S("HaApi.TestIntegrationOld"), result.HomeAssistantVersion, result.IntegrationVersion, Mqtt.HaWebSocketService.MinimumIntegrationVersion);
                return;
            }

            _haApiTestResult.ForeColor = Color.FromArgb(21, 128, 61);
            _haApiTestResult.Text = string.Format(S("HaApi.TestSuccess"), result.HomeAssistantVersion, result.IntegrationVersion);
        }
        catch (Exception ex)
        {
            _haApiTestResult.ForeColor = Color.FromArgb(153, 27, 27);
            _haApiTestResult.Text = string.Format(S("HaApi.TestFailed"), ex.Message);
        }
    }

    private void ConfigureStatusLabel(Label label, string text, Color background, Color foreground)
    {
        label.Text = text;
        label.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
        label.ForeColor = foreground;
        label.BackColor = background;
        label.BorderStyle = BorderStyle.None;
        label.AutoSize = false;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.Padding = new Padding(D(12), 0, D(12), 0);
    }

    private void UpdateGeneralStatusMessages()
    {
        _generalNotConfiguredError.Visible = !_settings.MqttEnabled && !_settings.HaApiEnabled;
        _generalServiceWarning.Visible = !IsServiceHealthy();
        LayoutGeneralStatusMessages();
    }

    private void UpdateServiceStatusMessage()
    {
        _serviceWarning.Visible = !IsServiceHealthy();
        LayoutServiceStatusMessage();
    }

    // Warn when the service is missing OR installed-but-stopped: a stopped
    // service can't run commands/sensors and forces the update UAC prompt.
    private static bool IsServiceHealthy()
    {
        try
        {
            return CompanionServiceManager.IsInstalled() && CompanionServiceManager.IsRunning();
        }
        catch
        {
            return false;
        }
    }

    private void LayoutGeneralStatusMessages()
    {
        var y = D(56);
        var statusWidth = _generalDeviceCard?.Width ?? D(720);
        foreach (var label in new[] { _generalNotConfiguredError, _generalServiceWarning })
        {
            if (!label.Visible)
            {
                continue;
            }

            label.Location = new Point(D(28), y);
            label.Size = new Size(statusWidth, D(34));
            label.BringToFront();
            y += D(42);
        }

        var firstCardTop = y == D(56) ? D(64) : y + D(8);
        if (_generalDeviceCard is not null)
        {
            _generalDeviceCard.Top = firstCardTop;
        }

        if (_generalNetworkCard is not null && _generalDeviceCard is not null)
        {
            _generalNetworkCard.Top = _generalDeviceCard.Bottom + D(20);
        }

        if (_generalFilesCard is not null && _generalNetworkCard is not null)
        {
            _generalFilesCard.Top = _generalNetworkCard.Bottom + D(20);
        }

        if (_generalDangerCard is not null && _generalFilesCard is not null)
        {
            _generalDangerCard.Top = _generalFilesCard.Bottom + D(20);
        }
    }

    private void LayoutServiceStatusMessage()
    {
        var firstCardTop = D(56);
        if (_serviceWarning.Visible)
        {
            _serviceWarning.Location = Pt(28, 56);
            _serviceWarning.Size = new Size(_serviceStatusCard?.Width ?? D(600), D(34));
            _serviceWarning.BringToFront();
            firstCardTop = D(98);
        }

        if (_serviceStatusCard is not null)
        {
            _serviceStatusCard.Top = firstCardTop;
        }

        if (_serviceActionsCard is not null && _serviceStatusCard is not null)
        {
            _serviceActionsCard.Top = _serviceStatusCard.Bottom + D(12);
        }
    }

    private Panel BuildCapabilitiesPage()
    {
        var page = MakePage();

        AddPageTitle(page, S("Cap.Title"));

        var card1 = MakeCard(page, 28, 56, 600, 300, S("Cap.Functions"));
        var y = 44;
        y = AddCheck(card1, _capNotify, S("Cap.Notifications"), y);
        y = AddCheck(card1, _capMedia, S("Cap.MediaPlayer"), y);
        y = AddCheck(card1, _capSensorsService, S("Cap.SensorsService"), y);
        y = AddCheck(card1, _capSensorsApp, S("Cap.SensorsApp"), y);
        y += 6;
        y = AddField(card1, S("Cap.FastSensorInterval"), _fastSensorInterval, y, inputWidth: 100);
        card1.Controls.Add(new Label
        {
            Text = S("Cap.Seconds"), Location = Pt(296, y - 30), AutoSize = true, ForeColor = TextMuted
        });
        y = AddField(card1, S("Cap.NormalSensorInterval"), _normalSensorInterval, y, inputWidth: 100);
        card1.Controls.Add(new Label
        {
            Text = S("Cap.Seconds"), Location = Pt(296, y - 30), AutoSize = true, ForeColor = TextMuted
        });
        y = AddField(card1, S("Cap.HourlySensorInterval"), _hourlySensorInterval, y, inputWidth: 100);
        card1.Controls.Add(new Label
        {
            Text = S("Cap.Seconds"), Location = Pt(296, y - 30), AutoSize = true, ForeColor = TextMuted
        });

        var cmdY = 368;
        var card2 = MakeCard(page, 28, cmdY, 600, 50 + SystemCommandCatalog.Commands.Count * 30 + 16, S("Cap.Commands"));
        y = 44;
        card2.Controls.Add(new Label { Text = "Tray", Font = new Font("Segoe UI", 8.5F, FontStyle.Bold), ForeColor = TextMuted, Location = Pt(320, y), AutoSize = true });
        card2.Controls.Add(new Label { Text = "Service", Font = new Font("Segoe UI", 8.5F, FontStyle.Bold), ForeColor = TextMuted, Location = Pt(410, y), AutoSize = true });
        y += 24;

        foreach (var cmd in SystemCommandCatalog.Commands)
        {
            card2.Controls.Add(new Label
            {
                Text = S($"Cmd.{cmd.Name}"), Location = Pt(20, y + 2),
                Size = Sz(280, 22), ForeColor = TextBody
            });

            var tray = new CheckBox { Location = Pt(324, y), AutoSize = true, Enabled = cmd.SupportsTrayApp };
            card2.Controls.Add(tray);

            CheckBox? service = null;
            if (cmd.SupportsService)
            {
                service = new CheckBox { Location = Pt(418, y), AutoSize = true };
                card2.Controls.Add(service);
            }
            else
            {
                card2.Controls.Add(new Label
                {
                    Text = "—", Location = Pt(420, y + 1), AutoSize = true, ForeColor = TextMuted
                });
            }

            _cmdRows.Add(new CmdRow(cmd, tray, service));
            y += 30;
        }

        return page;
    }

    private Panel BuildSensorsPage()
    {
        var page = MakePage();

        AddPageTitle(page, S("Sensors.Title"));

        var tabs = new TabControl
        {
            Location = Pt(28, 56),
            Size = Sz(600, 420),
            Font = new Font("Segoe UI", 9.5F)
        };
        page.Layout += (_, _) =>
        {
            var hPad = D(28);
            var bottom = D(12);
            tabs.Width = Math.Max(D(400), page.ClientSize.Width - hPad * 2);
            tabs.Height = Math.Max(D(200), page.ClientSize.Height - tabs.Top - bottom);
        };

        var builtInTab = new TabPage(S("Sensors.BuiltIn")) { BackColor = CardBg, Padding = new Padding(D(4)) };
        SetupBuiltInGrid();
        _builtInGrid.Dock = DockStyle.Fill;
        builtInTab.Controls.Add(_builtInGrid);
        tabs.TabPages.Add(builtInTab);

        var customTab = new TabPage(S("Sensors.Custom")) { BackColor = CardBg, Padding = new Padding(D(4)) };
        SetupCustomGrid();
        _customGrid.Dock = DockStyle.Fill;

        var infoPanel = BuildCustomSensorInfoPanel();

        var btnPanel = new Panel { Dock = DockStyle.Bottom, Height = D(40), BackColor = CardBg };
        var addBtn = MakeSecondaryButton(S("Sensors.Add"), 110, 30);
        addBtn.Location = Pt(4, 6);
        addBtn.Click += (_, _) =>
        {
            var rowIndex = AddCustomSensorRow(new CustomSensorDefinition
                {
                    Enabled = true,
                    TrayApp = true,
                    Service = true,
                    Type = CustomSensorTypes.ProcessRunning,
                    Name = S("Sensors.NewSensor"),
                    Parameter = "notepad",
                    PollingProfile = SensorPollingProfiles.ToKey(SensorPollingProfile.Normal)
                });
            MarkCustomSensorValueNotTested(_customGrid.Rows[rowIndex]);
        };

        var removeBtn = MakeSecondaryButton(S("Sensors.Remove"), 90, 30);
        removeBtn.Location = new Point(addBtn.Right + D(8), D(6));
        removeBtn.Click += (_, _) =>
        {
            if (_customGrid.CurrentRow is { IsNewRow: false } row)
                _customGrid.Rows.Remove(row);
        };
        var testBtn = MakeSecondaryButton(S("Sensors.TestValue"), 110, 30);
        testBtn.Location = new Point(removeBtn.Right + D(8), D(6));
        testBtn.Click += async (_, _) => await UpdateSelectedCustomSensorValueAsync();
        btnPanel.Controls.Add(addBtn);
        btnPanel.Controls.Add(removeBtn);
        btnPanel.Controls.Add(testBtn);

        _customGrid.CellEndEdit += (_, e) =>
        {
            if (e.RowIndex >= 0 &&
                _customGrid.Columns[e.ColumnIndex].Name is "Type" or "Parameter")
            {
                MarkCustomSensorValueNotTested(_customGrid.Rows[e.RowIndex]);
            }
        };
        _customGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_customGrid.IsCurrentCellDirty)
            {
                _customGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };

        _builtInGrid.CellClick += (_, e) =>
        {
            if (e.RowIndex < 0 ||
                e.ColumnIndex < 0 ||
                _builtInGrid.Columns[e.ColumnIndex].Name != "Attributes" ||
                _builtInGrid.Rows[e.RowIndex].Tag is not string key ||
                BuiltInSensorCatalog.Find(key) is not { HasMultipleValues: true } definition)
            {
                return;
            }

            var rowIndexes = new List<int>();
            foreach (var attributePath in definition.AttributePaths ?? [])
            {
                rowIndexes.Add(AddCustomSensorRow(new CustomSensorDefinition
                {
                    Enabled = true,
                    TrayApp = definition.SupportsTrayApp,
                    Service = definition.SupportsService,
                    Type = CustomSensorTypes.BuiltInAttribute,
                    Name = string.Format(S("Sensors.AttributeSensorName"), S($"Sensor.{definition.Key}"), GetAttributeDisplayName(attributePath)),
                    Parameter = attributePath,
                    PollingProfile = SensorPollingProfiles.ToKey(definition.PollingProfile)
                }));
            }

            if (rowIndexes.Count == 0)
            {
                return;
            }

            tabs.SelectedTab = customTab;
            _customGrid.ClearSelection();
            _customGrid.Rows[rowIndexes[0]].Selected = true;
            _customGrid.CurrentCell = _customGrid.Rows[rowIndexes[0]].Cells["Parameter"];
            foreach (var rowIndex in rowIndexes)
            {
                MarkCustomSensorValueNotTested(_customGrid.Rows[rowIndex]);
            }
        };

        customTab.Controls.Add(_customGrid);
        customTab.Controls.Add(btnPanel);
        customTab.Controls.Add(infoPanel);
        tabs.TabPages.Add(customTab);

        page.Controls.Add(tabs);
        return page;
    }

    private void SetupBuiltInGrid()
    {
        StyleGrid(_builtInGrid);
        _builtInGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "TrayApp", HeaderText = "Tray", Width = D(50) });
        _builtInGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Service", HeaderText = "Svc", Width = D(50) });
        _builtInGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Name", HeaderText = S("Sensors.Sensor"), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = D(200), ReadOnly = true
        });
        _builtInGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Profile", HeaderText = S("Sensors.Profile"), Width = D(92), ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _builtInGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Attributes", HeaderText = string.Empty, Width = D(36), ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter }
        });
    }

    private void SetupCustomGrid()
    {
        StyleGrid(_customGrid);
        _customGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Enabled", HeaderText = S("Sensors.Active"), Width = D(50) });
        _customGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "TrayApp", HeaderText = "Tray", Width = D(50) });
        _customGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Service", HeaderText = "Svc", Width = D(50) });
        var sensorTypes = new[]
        {
            CustomSensorTypes.ProcessRunning,
            CustomSensorTypes.ServiceStatus,
            CustomSensorTypes.DiskFree,
            CustomSensorTypes.BuiltInAttribute
        }
            .Select(t => new KeyValuePair<string, string>(t, S($"SensorType.{t}")))
            .ToArray();
        _customGrid.Columns.Add(new DataGridViewComboBoxColumn
        {
            Name = "Type", HeaderText = S("Sensors.Type"), Width = D(150),
            DataSource = sensorTypes, ValueMember = "Key", DisplayMember = "Value"
        });
        _customGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = S("Sensors.Name"), Width = D(130) });
        _customGrid.Columns.Add(new DataGridViewComboBoxColumn
        {
            Name = "Profile", HeaderText = S("Sensors.Profile"), Width = D(100),
            DataSource = BuildPollingProfileOptions(), ValueMember = "Key", DisplayMember = "Value"
        });
        _customGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Parameter", HeaderText = S("Sensors.Parameter"),
            Width = D(170), MinimumWidth = D(80)
        });
        _customGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Value", HeaderText = S("Sensors.Value"), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = D(110), ReadOnly = true
        });
    }

    private Panel BuildCustomSensorInfoPanel()
    {
        var panel = new Panel { Dock = DockStyle.Top, Height = D(78), BackColor = Color.FromArgb(239, 246, 255) };
        panel.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(191, 219, 254));
            e.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1);
        };

        panel.Controls.Add(new Label
        {
            Text = "i",
            Location = Pt(10, 10),
            Size = Sz(22, 22),
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = BtnBlue,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold)
        });
        panel.Controls.Add(new Label
        {
            Text = S("Sensors.CustomHelp"),
            Location = Pt(40, 8),
            Size = Sz(520, 64),
            ForeColor = TextBody,
            Font = new Font("Segoe UI", 8.5F)
        });
        return panel;
    }

    private static string GetAttributeDisplayName(string attributePath)
    {
        var lastDot = attributePath.LastIndexOf('.');
        var name = lastDot >= 0 ? attributePath[(lastDot + 1)..] : attributePath;
        return name.Replace("[0]", string.Empty);
    }

    private KeyValuePair<string, string>[] BuildPollingProfileOptions()
    {
        return Enum.GetValues<SensorPollingProfile>()
            .Select(profile => new KeyValuePair<string, string>(
                SensorPollingProfiles.ToKey(profile),
                GetPollingProfileDisplayName(profile)))
            .ToArray();
    }

    private string GetPollingProfileDisplayName(SensorPollingProfile profile)
    {
        return S($"PollingProfile.{SensorPollingProfiles.ToKey(profile)}");
    }

    private void StyleGrid(DataGridView grid)
    {
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AutoGenerateColumns = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.ScrollBars = ScrollBars.Both;
        grid.BackgroundColor = CardBg;
        grid.BorderStyle = BorderStyle.None;
        grid.GridColor = BorderClr;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(219, 234, 254);
        grid.DefaultCellStyle.SelectionForeColor = TextDark;
        grid.ColumnHeadersDefaultCellStyle.BackColor = PageBg;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = TextDark;
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        grid.RowTemplate.Height = D(24);
    }

    private Panel BuildServicePage()
    {
        var page = MakePage();
        AddPageTitle(page, S("Service.Title"));

        ConfigureStatusLabel(
            _serviceWarning,
            "⚠  " + S("General.ServiceNotInstalledWarning"),
            Color.FromArgb(255, 243, 224),
            Color.FromArgb(180, 60, 0));
        page.Controls.Add(_serviceWarning);
        page.Layout += (_, _) => LayoutServiceStatusMessage();

        var statusLabel = new Label
        {
            Text = CompanionServiceManager.GetStatusText(),
            Location = Pt(20, 44), Size = Sz(540, 64),
            ForeColor = TextBody, Font = new Font("Segoe UI", 9.5F)
        };

        var card1 = MakeCard(page, 28, 56, 600, 160, S("Service.Status"));
        _serviceStatusCard = card1;
        card1.Controls.Add(statusLabel);

        var refreshBtn = MakeSecondaryButton(S("Service.Refresh"), 100, 30);
        refreshBtn.Location = Pt(20, 116);
        refreshBtn.Click += (_, _) =>
        {
            statusLabel.Text = CompanionServiceManager.GetStatusText();
            UpdateGeneralStatusMessages();
            UpdateServiceStatusMessage();
        };
        card1.Controls.Add(refreshBtn);

        var card2 = MakeCard(page, 28, 228, 600, 140, S("Service.Actions"));
        _serviceActionsCard = card2;

        var installBtn = MakePrimaryButton(S("Service.Install"), 170, 36);
        installBtn.Location = Pt(20, 48);
        installBtn.Click += (_, _) =>
        {
            CompanionServiceManager.RunElevated("--install-service", _log);
            statusLabel.Text = CompanionServiceManager.GetStatusText();
            UpdateGeneralStatusMessages();
            UpdateServiceStatusMessage();
        };
        card2.Controls.Add(installBtn);

        var startBtn = MakeSecondaryButton(S("Service.Start"), 100, 36);
        startBtn.Location = Pt(200, 48);
        startBtn.Click += (_, _) =>
        {
            CompanionServiceManager.RunElevated("--start-service", _log);
            statusLabel.Text = CompanionServiceManager.GetStatusText();
            UpdateGeneralStatusMessages();
            UpdateServiceStatusMessage();
        };
        card2.Controls.Add(startBtn);

        var stopBtn = MakeSecondaryButton(S("Service.Stop"), 100, 36);
        stopBtn.Location = Pt(310, 48);
        stopBtn.Click += (_, _) =>
        {
            CompanionServiceManager.RunElevated("--stop-service", _log);
            statusLabel.Text = CompanionServiceManager.GetStatusText();
            UpdateGeneralStatusMessages();
            UpdateServiceStatusMessage();
        };
        card2.Controls.Add(stopBtn);

        var uninstallBtn = MakeSecondaryButton(S("Service.Uninstall"), 110, 36);
        uninstallBtn.Location = Pt(20, 96);
        uninstallBtn.ForeColor = Color.FromArgb(185, 28, 28);
        uninstallBtn.Click += (_, _) =>
        {
            if (MessageBox.Show(S("Service.ConfirmUninstall"), AppIdentity.DisplayName,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
            {
                CompanionServiceManager.RunElevated("--uninstall-service", _log);
                statusLabel.Text = CompanionServiceManager.GetStatusText();
                UpdateGeneralStatusMessages();
                UpdateServiceStatusMessage();
            }
        };
        card2.Controls.Add(uninstallBtn);

        UpdateServiceStatusMessage();
        return page;
    }

    private Panel BuildAboutPage()
    {
        var page = MakePage();

        var card = MakeCard(page, 28, 56, 600, 246, null);

        var icon = LoadIcon();
        if (icon is not null)
        {
            card.Controls.Add(new PictureBox
            {
                Image = icon.ToBitmap(),
                Location = Pt(28, 26),
                Size = Sz(72, 72),
                SizeMode = PictureBoxSizeMode.Zoom
            });
        }

        card.Controls.Add(new Label
        {
            Text = AppIdentity.DisplayName, Font = new Font("Segoe UI", 18F, FontStyle.Bold),
            ForeColor = TextDark, Location = Pt(120, 24), AutoSize = true
        });
        card.Controls.Add(new Label
        {
            Text = $"{S("About.Version")}: {_settings.SoftwareVersion}",
            ForeColor = TextBody, Location = Pt(122, 62), AutoSize = true
        });
        card.Controls.Add(new Label
        {
            Text = $"{S("About.Developer")}: {_settings.Manufacturer}",
            ForeColor = TextBody, Location = Pt(122, 86), AutoSize = true
        });
        card.Controls.Add(new Label
        {
            Text = S("About.Description"),
            ForeColor = TextMuted, Location = Pt(122, 120), AutoSize = true
        });

        var githubBtn = MakeSecondaryButton("GitHub", 110, 32);
        githubBtn.Location = Pt(28, 174);
        githubBtn.Click += (_, _) => OpenUrl(AppIdentity.GitHubRepositoryUrl);
        card.Controls.Add(githubBtn);

        var issueBtn = MakeSecondaryButton(S("About.ReportIssue"), 150, 32);
        issueBtn.Location = new Point(githubBtn.Right + D(8), D(174));
        issueBtn.Click += (_, _) => OpenUrl($"{AppIdentity.GitHubRepositoryUrl}/issues/new");
        card.Controls.Add(issueBtn);

        var updateBtn = MakePrimaryButton(S("About.CheckUpdates"), 160, 32);
        updateBtn.Location = new Point(issueBtn.Right + D(8), D(174));
        updateBtn.Click += async (_, _) => await CheckForUpdatesAsync(updateBtn);
        card.Controls.Add(updateBtn);

        return page;
    }

    // ── Settings load / save ───────────────────────────────────────

    private Panel BuildDangerZonePage()
    {
        var page = MakePage();

        AddPageTitle(page, S("Danger.Title"));

        var warning = new Label();
        ConfigureStatusLabel(
            warning,
            "⚠  " + S("Danger.Warning"),
            Color.FromArgb(153, 27, 27),
            Color.White);
        warning.Location = Pt(28, 56);
        warning.Size = Sz(720, 48);
        page.Controls.Add(warning);

        // Tool picker view — one button per tool, each opening its own sub-view.
        var toolsCard = MakeCard(page, 28, 116, 720, 344, S("Danger.Tools"));
        var subCards = new List<Panel>();

        void ShowView(Panel? sub)
        {
            toolsCard.Visible = sub is null;
            foreach (var item in subCards)
                item.Visible = ReferenceEquals(item, sub);
        }

        Panel AddToolCard(string titleKey, Action? onLeave)
        {
            var card = MakeCard(page, 28, 116, 720, 456, S(titleKey));
            card.Visible = false;
            subCards.Add(card);

            var back = MakeSecondaryButton(S("Danger.Back"), 90, 30);
            back.Location = Pt(20, 44);
            back.Click += (_, _) =>
            {
                onLeave?.Invoke();
                ShowView(null);
            };
            card.Controls.Add(back);
            return card;
        }

        var hintLabels = new List<Label>();

        Button AddToolRow(int row, string textKey, string hintKey)
        {
            var y = 44 + row * 42;
            var btn = MakeSecondaryButton(S(textKey), 230, 32);
            btn.Location = Pt(20, y);
            toolsCard.Controls.Add(btn);
            var hint = new Label
            {
                Text = S(hintKey), Location = Pt(262, y + 7),
                Size = Sz(430, 20), ForeColor = TextMuted, Font = new Font("Segoe UI", 8.5F)
            };
            toolsCard.Controls.Add(hint);
            hintLabels.Add(hint);
            return btn;
        }

        var mqttCard = BuildDangerMqttCard(AddToolCard);
        var debugCard = BuildDangerDebugCard(AddToolCard);
        var monitorCard = BuildDangerMonitorCard(AddToolCard);
        var backupCard = BuildDangerBackupCard(AddToolCard);

        AddToolRow(0, "Danger.MqttMaintenance", "Danger.MqttMaintenanceHint").Click += (_, _) => ShowView(mqttCard);
        var republishBtn = AddToolRow(1, "Danger.Republish", "Danger.RepublishHint");
        republishBtn.Click += (_, _) => _ = RepublishDiscoveryAsync(republishBtn);
        AddToolRow(2, "Danger.DebugLog", "Danger.DebugLogHint").Click += (_, _) =>
        {
            ShowView(debugCard);
            RefreshDebugLog();
            _debugTimer.Start();
        };
        AddToolRow(3, "Danger.Monitor", "Danger.MonitorHint").Click += (_, _) => ShowView(monitorCard);
        AddToolRow(4, "Danger.Backup", "Danger.BackupHint").Click += (_, _) => ShowView(backupCard);
        var factoryBtn = AddToolRow(5, "Danger.FactoryReset", "Danger.FactoryResetHint");
        factoryBtn.ForeColor = Color.FromArgb(185, 28, 28);
        factoryBtn.Click += (_, _) => FactoryReset();

        // Beta update channel — persists immediately, no Save button needed.
        var betaCheck = new CheckBox
        {
            Text = S("Danger.BetaUpdates"), Location = Pt(20, 298),
            Size = Sz(680, 26), ForeColor = TextBody, Checked = _settings.BetaUpdatesEnabled
        };
        betaCheck.CheckedChanged += (_, _) =>
        {
            if (_settings.BetaUpdatesEnabled == betaCheck.Checked)
            {
                return;
            }

            _settings.BetaUpdatesEnabled = betaCheck.Checked;
            SettingsStore.Save(_paths, _settings);
            _log.Info($"Beta update channel {(betaCheck.Checked ? "enabled" : "disabled")}.");
        };
        toolsCard.Controls.Add(betaCheck);

        toolsCard.Layout += (_, _) =>
        {
            var hintWidth = Math.Max(D(200), toolsCard.ClientSize.Width - D(262) - D(20));
            foreach (var hint in hintLabels)
            {
                hint.Width = hintWidth;
            }

            betaCheck.Width = Math.Max(D(420), toolsCard.ClientSize.Width - D(40));
        };

        page.Layout += (_, _) =>
        {
            warning.Size = new Size(toolsCard.Width, D(48));
            warning.BringToFront();
            foreach (var sub in subCards)
                sub.Height = Math.Max(D(320), page.ClientSize.Height - sub.Top - D(12));
        };

        return page;
    }

    private Panel BuildDangerMqttCard(Func<string, Action?, Panel> addToolCard)
    {
        var mqttCard = addToolCard("Danger.RetainedCard", null);

        var loadOwn = MakeSecondaryButton(S("Danger.LoadOwn"), 210, 30);
        loadOwn.Location = Pt(118, 44);
        loadOwn.Click += (_, _) => _ = LoadRetainedAsync(ownOnly: true);
        mqttCard.Controls.Add(loadOwn);

        var loadAll = MakeSecondaryButton(S("Danger.LoadAll"), 210, 30);
        loadAll.Location = Pt(336, 44);
        loadAll.Click += (_, _) => _ = LoadRetainedAsync(ownOnly: false);
        mqttCard.Controls.Add(loadAll);

        _dangerStatus.Location = Pt(20, 82);
        _dangerStatus.Size = Sz(680, 20);
        _dangerStatus.ForeColor = TextMuted;
        mqttCard.Controls.Add(_dangerStatus);

        _dangerList.View = View.Details;
        _dangerList.CheckBoxes = true;
        _dangerList.FullRowSelect = true;
        _dangerList.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _dangerList.Location = Pt(20, 108);
        _dangerList.Size = Sz(680, 290);
        _dangerList.Font = new Font("Consolas", 9F);
        _dangerList.Columns.Add(S("Danger.ColTopic"), D(440));
        _dangerList.Columns.Add(S("Danger.ColSize"), D(80));
        _dangerList.Columns.Add(S("Danger.ColDevice"), D(130));
        mqttCard.Controls.Add(_dangerList);

        var selectAll = new CheckBox
        {
            Text = S("Danger.SelectAll"), Location = Pt(20, 410),
            Size = Sz(220, 26), ForeColor = TextBody
        };
        selectAll.CheckedChanged += (_, _) =>
        {
            foreach (ListViewItem item in _dangerList.Items)
                item.Checked = selectAll.Checked;
        };
        mqttCard.Controls.Add(selectAll);

        var deleteBtn = MakePrimaryButton(S("Danger.DeleteSelected"), 190, 32);
        deleteBtn.BackColor = Color.FromArgb(220, 38, 38);
        deleteBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(185, 28, 28);
        deleteBtn.Location = Pt(510, 406);
        deleteBtn.Click += (_, _) => _ = DeleteRetainedSelectedAsync();
        mqttCard.Controls.Add(deleteBtn);

        mqttCard.Layout += (_, _) =>
        {
            var margin = D(20);
            _dangerStatus.Width = mqttCard.ClientSize.Width - margin * 2;
            _dangerList.Width = mqttCard.ClientSize.Width - margin * 2;
            _dangerList.Height = Math.Max(D(120), mqttCard.ClientSize.Height - _dangerList.Top - D(58));
            selectAll.Top = _dangerList.Bottom + D(14);
            deleteBtn.Top = _dangerList.Bottom + D(10);
            deleteBtn.Left = mqttCard.ClientSize.Width - deleteBtn.Width - margin;

            var topicWidth = _dangerList.ClientSize.Width
                - _dangerList.Columns[1].Width
                - _dangerList.Columns[2].Width
                - D(4);
            if (topicWidth > D(100))
            {
                _dangerList.Columns[0].Width = topicWidth;
            }
        };

        _dangerButtons.AddRange([loadOwn, loadAll, deleteBtn]);
        return mqttCard;
    }

    private Panel BuildDangerDebugCard(Func<string, Action?, Panel> addToolCard)
    {
        var card = addToolCard("Danger.DebugLog", () => _debugTimer.Stop());

        var verbose = new CheckBox
        {
            Text = S("Danger.DebugVerbose"), Location = Pt(118, 47),
            Size = Sz(560, 26), ForeColor = TextBody, Checked = _log.VerboseEnabled
        };
        verbose.CheckedChanged += (_, _) => _log.VerboseEnabled = verbose.Checked;
        card.Controls.Add(verbose);

        _debugFilter.PlaceholderText = S("Danger.DebugFilter");
        _debugFilter.Location = Pt(20, 82);
        _debugFilter.Size = Sz(300, 28);
        _debugFilter.TextChanged += (_, _) => RefreshDebugLog();
        card.Controls.Add(_debugFilter);

        _debugLogBox.ReadOnly = true;
        _debugLogBox.Multiline = true;
        _debugLogBox.ScrollBars = ScrollBars.Both;
        _debugLogBox.WordWrap = false;
        _debugLogBox.Font = new Font("Consolas", 8.5F);
        _debugLogBox.BackColor = Color.FromArgb(15, 23, 42);
        _debugLogBox.ForeColor = Color.FromArgb(226, 232, 240);
        _debugLogBox.Location = Pt(20, 118);
        _debugLogBox.Size = Sz(680, 300);
        card.Controls.Add(_debugLogBox);

        _debugTimer.Tick += (_, _) => RefreshDebugLog();

        card.Layout += (_, _) =>
        {
            var margin = D(20);
            _debugLogBox.Width = card.ClientSize.Width - margin * 2;
            _debugLogBox.Height = Math.Max(D(120), card.ClientSize.Height - _debugLogBox.Top - D(16));
        };

        return card;
    }

    private void RefreshDebugLog()
    {
        try
        {
            using var stream = new FileStream(_paths.LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            const int maxBytes = 128 * 1024;
            if (stream.Length > maxBytes)
            {
                stream.Seek(-maxBytes, SeekOrigin.End);
            }

            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();

            var filter = _debugFilter.Text.Trim();
            if (filter.Length > 0)
            {
                text = string.Join(
                    Environment.NewLine,
                    text.Split('\n')
                        .Where(line => line.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        .Select(line => line.TrimEnd('\r')));
            }

            if (text == _debugRendered)
            {
                return;
            }

            _debugRendered = text;
            _debugLogBox.Text = text;
            _debugLogBox.SelectionStart = _debugLogBox.TextLength;
            _debugLogBox.ScrollToCaret();
        }
        catch
        {
            // The log file may be momentarily locked — retry on the next tick.
        }
    }

    private Panel BuildDangerMonitorCard(Func<string, Action?, Panel> addToolCard)
    {
        Button? toggle = null;
        var card = addToolCard("Danger.Monitor", () => _ = StopLiveMonitorAsync(toggle));

        toggle = MakeSecondaryButton(S("Danger.MonitorStart"), 140, 30);
        toggle.Location = Pt(118, 44);
        var toggleButton = toggle;
        toggle.Click += (_, _) => _ = ToggleLiveMonitorAsync(toggleButton);
        card.Controls.Add(toggle);

        _monitorList.View = View.Details;
        _monitorList.FullRowSelect = true;
        _monitorList.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _monitorList.MultiSelect = false;
        _monitorList.Location = Pt(20, 84);
        _monitorList.Size = Sz(680, 220);
        _monitorList.Font = new Font("Consolas", 9F);
        _monitorList.Columns.Add(S("Danger.ColTime"), D(90));
        _monitorList.Columns.Add(S("Danger.ColTopic"), D(440));
        _monitorList.Columns.Add(S("Danger.ColSize"), D(80));
        _monitorList.Columns.Add(S("Danger.ColRetain"), D(36));
        _monitorList.SelectedIndexChanged += (_, _) =>
        {
            _monitorPayload.Text = _monitorList.SelectedItems.Count > 0
                ? (string)(_monitorList.SelectedItems[0].Tag ?? string.Empty)
                : string.Empty;
        };
        card.Controls.Add(_monitorList);

        _monitorPayload.ReadOnly = true;
        _monitorPayload.Multiline = true;
        _monitorPayload.ScrollBars = ScrollBars.Vertical;
        _monitorPayload.Font = new Font("Consolas", 8.5F);
        _monitorPayload.BackColor = PageBg;
        _monitorPayload.Location = Pt(20, 314);
        _monitorPayload.Size = Sz(680, 110);
        card.Controls.Add(_monitorPayload);

        card.Layout += (_, _) =>
        {
            var margin = D(20);
            var width = card.ClientSize.Width - margin * 2;
            _monitorPayload.Width = width;
            _monitorPayload.Top = card.ClientSize.Height - _monitorPayload.Height - D(16);
            _monitorList.Width = width;
            _monitorList.Height = Math.Max(D(100), _monitorPayload.Top - _monitorList.Top - D(10));

            var topicWidth = _monitorList.ClientSize.Width
                - _monitorList.Columns[0].Width
                - _monitorList.Columns[2].Width
                - _monitorList.Columns[3].Width
                - D(4);
            if (topicWidth > D(100))
            {
                _monitorList.Columns[1].Width = topicWidth;
            }
        };

        return card;
    }

    private async Task ToggleLiveMonitorAsync(Button toggle)
    {
        if (_liveMonitor is not null)
        {
            await StopLiveMonitorAsync(toggle);
            return;
        }

        if (!_settings.MqttEnabled || string.IsNullOrWhiteSpace(_settings.MqttHost))
        {
            MessageBox.Show(S("Danger.MqttRequired"), AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var monitor = new MqttLiveMonitor();
        toggle.Enabled = false;
        try
        {
            await monitor.StartAsync(_settings, OnMonitorMessage, CancellationToken.None);
            _liveMonitor = monitor;
            toggle.Text = S("Danger.MonitorStop");
        }
        catch (Exception ex)
        {
            monitor.Dispose();
            MessageBox.Show(string.Format(S("Danger.Error"), ex.Message), AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            toggle.Enabled = true;
        }
    }

    private async Task StopLiveMonitorAsync(Button? toggle)
    {
        var monitor = _liveMonitor;
        _liveMonitor = null;
        if (monitor is not null)
        {
            await monitor.StopAsync();
        }

        if (toggle is not null)
        {
            toggle.Text = S("Danger.MonitorStart");
        }
    }

    private void OnMonitorMessage(string topic, string payload, bool retained)
    {
        if (IsDisposed)
        {
            return;
        }

        try
        {
            BeginInvoke(() =>
            {
                var item = new ListViewItem(DateTime.Now.ToString("HH:mm:ss")) { Tag = payload };
                item.SubItems.Add(topic);
                item.SubItems.Add($"{Encoding.UTF8.GetByteCount(payload)} B");
                item.SubItems.Add(retained ? "R" : string.Empty);
                _monitorList.Items.Add(item);
                while (_monitorList.Items.Count > 500)
                {
                    _monitorList.Items.RemoveAt(0);
                }

                item.EnsureVisible();
            });
        }
        catch
        {
            // The form may be closing.
        }
    }

    private Panel BuildDangerBackupCard(Func<string, Action?, Panel> addToolCard)
    {
        var card = addToolCard("Danger.Backup", null);

        card.Controls.Add(new Label
        {
            Text = S("Danger.BackupNote"), Location = Pt(20, 84),
            Size = Sz(680, 36), ForeColor = TextMuted, Font = new Font("Segoe UI", 8.5F)
        });

        var exportBtn = MakeSecondaryButton(S("Danger.Export"), 210, 32);
        exportBtn.Location = Pt(20, 128);
        exportBtn.Click += (_, _) => ExportSettings();
        card.Controls.Add(exportBtn);

        var importBtn = MakeSecondaryButton(S("Danger.Import"), 210, 32);
        importBtn.Location = Pt(238, 128);
        importBtn.Click += (_, _) => ImportSettings();
        card.Controls.Add(importBtn);

        return card;
    }

    private void ExportSettings()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "JSON (*.json)|*.json",
            FileName = $"hassagent-settings-{DateTime.Now:yyyyMMdd}.json"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            SettingsStore.Export(_settings, dialog.FileName);
            _log.Info($"Settings exported to {dialog.FileName}.");
            MessageBox.Show(string.Format(S("Danger.ExportDone"), dialog.FileName), AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(S("Danger.Error"), ex.Message), AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ImportSettings()
    {
        using var dialog = new OpenFileDialog { Filter = "JSON (*.json)|*.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var confirm = MessageBox.Show(
            S("Danger.ImportConfirm"),
            S("Danger.Title"),
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes)
        {
            return;
        }

        try
        {
            var imported = SettingsStore.Import(dialog.FileName);
            SettingsStore.Save(_paths, imported);
            _log.Info("Settings imported from file; restarting.");
            MessageBox.Show(S("Danger.ImportDone"), AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            RestartApplication();
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(S("Danger.Error"), ex.Message), AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void FactoryReset()
    {
        var confirm1 = MessageBox.Show(
            S("Danger.FactoryConfirm1"),
            S("Danger.FactoryReset"),
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirm1 != DialogResult.Yes)
        {
            return;
        }

        var confirm2 = MessageBox.Show(
            S("Danger.FactoryConfirm2"),
            S("Danger.FactoryReset"),
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirm2 != DialogResult.Yes)
        {
            return;
        }

        try
        {
            File.Delete(_paths.SettingsFile);
            _log.Info("Factory reset: settings file deleted; restarting.");
            RestartApplication();
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(S("Danger.Error"), ex.Message), AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task RepublishDiscoveryAsync(Button button)
    {
        if (DiscoveryRepublishHandler is null)
        {
            return;
        }

        button.Enabled = false;
        try
        {
            var published = await DiscoveryRepublishHandler();
            MessageBox.Show(
                published ? S("Danger.RepublishDone") : S("Danger.RepublishNotConnected"),
                AppIdentity.DisplayName,
                MessageBoxButtons.OK,
                published ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(S("Danger.Error"), ex.Message), AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            button.Enabled = true;
        }
    }

    private static void RestartApplication()
    {
        // The single-instance mutex is still held while this process exits,
        // so relaunch after a short delay from a detached shell.
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c timeout /t 2 /nobreak >nul & start \"\" \"{Application.ExecutablePath}\"",
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            UseShellExecute = false
        });
        Application.Exit();
    }

    private async Task LoadRetainedAsync(bool ownOnly)
    {
        if (!_settings.MqttEnabled || string.IsNullOrWhiteSpace(_settings.MqttHost))
        {
            MessageBox.Show(S("Danger.MqttRequired"), AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetDangerBusy(true);
        _dangerStatus.Text = S("Danger.Loading");
        _dangerList.Items.Clear();

        try
        {
            var messages = await MqttRetainedBrowser.ScanAsync(_settings, CancellationToken.None);
            if (ownOnly)
            {
                messages = messages.Where(m => m.IsOwnDevice).ToList();
            }

            foreach (var message in messages)
            {
                var item = new ListViewItem(message.Topic) { Tag = message.Topic };
                item.SubItems.Add($"{message.PayloadSize} B");
                item.SubItems.Add(message.IsOwnDevice ? S("Danger.ThisDevice") : S("Danger.OtherDevice"));
                _dangerList.Items.Add(item);
            }

            _dangerStatus.Text = messages.Count == 0
                ? S("Danger.NoMessages")
                : string.Format(S("Danger.Found"), messages.Count);
        }
        catch (Exception ex)
        {
            _dangerStatus.Text = string.Empty;
            _log.Warning($"Danger Zone scan failed: {ex.Message}");
            MessageBox.Show(string.Format(S("Danger.Error"), ex.Message), AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetDangerBusy(false);
        }
    }

    private async Task DeleteRetainedSelectedAsync()
    {
        var topics = _dangerList.CheckedItems.Cast<ListViewItem>()
            .Select(item => (string)item.Tag!)
            .ToList();
        if (topics.Count == 0)
        {
            return;
        }

        var confirm = MessageBox.Show(
            string.Format(S("Danger.ConfirmDelete"), topics.Count),
            S("Danger.Title"),
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.Yes)
        {
            return;
        }

        SetDangerBusy(true);
        try
        {
            await MqttRetainedBrowser.DeleteAsync(_settings, topics, CancellationToken.None);
            foreach (var item in _dangerList.CheckedItems.Cast<ListViewItem>().ToList())
            {
                _dangerList.Items.Remove(item);
            }

            _dangerStatus.Text = string.Format(S("Danger.Deleted"), topics.Count);
            _log.Info($"Danger Zone: deleted {topics.Count} retained MQTT message(s).");
        }
        catch (Exception ex)
        {
            _log.Warning($"Danger Zone delete failed: {ex.Message}");
            MessageBox.Show(string.Format(S("Danger.Error"), ex.Message), AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetDangerBusy(false);
        }
    }

    private void SetDangerBusy(bool busy)
    {
        foreach (var button in _dangerButtons)
        {
            button.Enabled = !busy;
        }
    }

    private void LoadSettings()
    {
        _deviceName.Text = _settings.DeviceName;
        _bindHost.Text = _settings.BindHost;
        _port.Value = _settings.Port;
        _autoStart.Checked = _settings.AutoStartOnLogin || StartupManager.IsEnabled();
        _showStartup.Checked = _settings.ShowStartupNotification;
        _dangerZoneCheck.Checked = _settings.DangerZoneEnabled;

        var langIdx = Strings.AvailableLanguages.ToList().IndexOf(_settings.Language);
        _langCombo.SelectedIndex = langIdx >= 0 ? langIdx : 0;
        var haLangIdx = Strings.AvailableLanguages.ToList().IndexOf(_settings.HaLanguage);
        _haLangCombo.SelectedIndex = haLangIdx >= 0 ? haLangIdx : 0;

        _mqttEnabled.Checked = _settings.MqttEnabled;
        _mqttHost.Text = _settings.MqttHost;
        _mqttPort.Value = _settings.MqttPort;
        _mqttUser.Text = _settings.MqttUsername;
        _mqttPass.Text = _settings.GetMqttPassword();
        _mqttTls.Checked = _settings.MqttUseTls;
        _mqttRetain.Checked = _settings.MqttRetainDiscovery;

        _haApiEnabled.Checked = _settings.HaApiEnabled;
        _haApiUrl.Text = _settings.HaApiUrl;
        _haApiToken.Text = _settings.GetHaApiToken();

        _capNotify.Checked = _settings.MqttNotificationsEnabled;
        _capMedia.Checked = _settings.MqttMediaPlayerEnabled;
        _capSensorsService.Checked = _settings.MqttServiceSystemSensorsEnabled;
        _capSensorsApp.Checked = _settings.MqttSystemSensorsEnabled;
        _fastSensorInterval.Value = _settings.FastSensorIntervalSeconds;
        _normalSensorInterval.Value = _settings.NormalSensorIntervalSeconds;
        _hourlySensorInterval.Value = _settings.HourlySensorIntervalSeconds;

        foreach (var row in _cmdRows)
        {
            row.TrayApp.Checked = _settings.IsTrayAppCommandEnabled(row.Def.Name);
            if (row.Service is not null)
                row.Service.Checked = _settings.IsServiceCommandEnabled(row.Def.Name);
        }

        foreach (var def in BuiltInSensorCatalog.Sensors)
        {
            var s = _settings.BuiltInSensors.FirstOrDefault(x => x.Key == def.Key);
            var ri = _builtInGrid.Rows.Add(
                s?.TrayApp ?? def.SupportsTrayApp,
                s?.Service ?? def.SupportsService,
                S($"Sensor.{def.Key}"),
                def.PushDriven ? S("PollingProfile.push") : GetPollingProfileDisplayName(def.PollingProfile),
                def.HasMultipleValues ? "+" : string.Empty);
            _builtInGrid.Rows[ri].Tag = def.Key;
            if (!def.SupportsTrayApp) { _builtInGrid.Rows[ri].Cells["TrayApp"].ReadOnly = true; _builtInGrid.Rows[ri].Cells["TrayApp"].Style.BackColor = SystemColors.Control; }
            if (!def.SupportsService) { _builtInGrid.Rows[ri].Cells["Service"].ReadOnly = true; _builtInGrid.Rows[ri].Cells["Service"].Style.BackColor = SystemColors.Control; }
            if (def.HasMultipleValues)
            {
                _builtInGrid.Rows[ri].Cells["Attributes"].ToolTipText = S("Sensors.MultiValueTooltip");
                _builtInGrid.Rows[ri].Cells["Attributes"].Style.ForeColor = BtnBlue;
            }
        }

        foreach (var sensor in _settings.CustomSensors)
        {
            AddCustomSensorRow(sensor);
        }
        MarkAllCustomSensorValuesNotTested();
    }

    private int AddCustomSensorRow(CustomSensorDefinition sensor)
    {
        var rowIndex = _customGrid.Rows.Add(
            sensor.Enabled,
            sensor.TrayApp,
            sensor.Service,
            sensor.Type,
            sensor.Name,
            SensorPollingProfiles.NormalizeKey(sensor.PollingProfile, SensorPollingProfile.Normal),
            sensor.Parameter);
        _customGrid.Rows[rowIndex].Tag = sensor.Id;
        return rowIndex;
    }

    private async Task UpdateSelectedCustomSensorValueAsync()
    {
        _customGrid.EndEdit();
        if (_customGrid.CurrentRow is { IsNewRow: false } row)
        {
            await UpdateCustomSensorRowValueAsync(row);
        }
    }

    private void MarkAllCustomSensorValuesNotTested()
    {
        if (!_customGrid.Columns.Contains("Value"))
        {
            return;
        }

        foreach (var row in _customGrid.Rows.Cast<DataGridViewRow>().Where(row => !row.IsNewRow))
        {
            MarkCustomSensorValueNotTested(row);
        }
    }

    private void MarkCustomSensorValueNotTested(DataGridViewRow row)
    {
        if (row.IsNewRow || !_customGrid.Columns.Contains("Value"))
        {
            return;
        }

        var parameter = Convert.ToString(row.Cells["Parameter"].Value) ?? string.Empty;
        row.Cells["Value"].Value = string.IsNullOrWhiteSpace(parameter)
            ? S("Sensors.ValueMissingParameter")
            : S("Sensors.ValueNotTested");
    }

    private async Task UpdateCustomSensorRowValueAsync(DataGridViewRow row)
    {
        if (row.IsNewRow || !_customGrid.Columns.Contains("Value"))
        {
            return;
        }

        var valueCell = row.Cells["Value"];
        var parameter = Convert.ToString(row.Cells["Parameter"].Value) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(parameter))
        {
            valueCell.Value = S("Sensors.ValueMissingParameter");
            return;
        }

        valueCell.Value = S("Sensors.ValueLoading");
        try
        {
            var sensor = BuildCustomSensorFromRow(row, forceEnabled: true);
            var value = await Task.Run(() => SystemMetricsService.TestCustomSensorValue(sensor, _log));
            if (row.DataGridView is null || row.DataGridView.IsDisposed)
            {
                return;
            }

            valueCell.Value = FormatSensorValue(value);
        }
        catch (Exception ex)
        {
            if (row.DataGridView is null || row.DataGridView.IsDisposed)
            {
                return;
            }

            valueCell.Value = string.Format(S("Sensors.ValueError"), ex.Message);
        }
    }

    private CustomSensorDefinition BuildCustomSensorFromRow(DataGridViewRow row, bool forceEnabled)
    {
        var type = Convert.ToString(row.Cells["Type"].Value) ?? CustomSensorTypes.ProcessRunning;
        var name = Convert.ToString(row.Cells["Name"].Value) ?? string.Empty;
        var parameter = Convert.ToString(row.Cells["Parameter"].Value) ?? string.Empty;
        var pollingProfile = Convert.ToString(row.Cells["Profile"].Value) ?? SensorPollingProfiles.ToKey(SensorPollingProfile.Normal);
        return new CustomSensorDefinition
        {
            Id = Convert.ToString(row.Tag) ?? Guid.NewGuid().ToString("N"),
            Enabled = forceEnabled || Convert.ToBoolean(row.Cells["Enabled"].Value ?? true),
            Type = type,
            Name = string.IsNullOrWhiteSpace(name) ? type : name.Trim(),
            Parameter = parameter.Trim(),
            PollingProfile = SensorPollingProfiles.NormalizeKey(pollingProfile, SensorPollingProfile.Normal),
            Service = forceEnabled || Convert.ToBoolean(row.Cells["Service"].Value ?? false),
            TrayApp = forceEnabled || Convert.ToBoolean(row.Cells["TrayApp"].Value ?? false)
        };
    }

    private static string FormatSensorValue(object? value)
    {
        return value switch
        {
            null => "null",
            bool boolValue => boolValue ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
        };
    }

    private async Task CheckForUpdatesAsync(Button updateButton)
    {
        var originalText = updateButton.Text;
        updateButton.Enabled = false;
        updateButton.Text = S("About.CheckingUpdates");

        try
        {
            var update = await AppUpdateService.CheckAsync(_settings.SoftwareVersion, _settings.BetaUpdatesEnabled);
            if (!string.IsNullOrWhiteSpace(update.Error))
            {
                MessageBox.Show(S("About.UpdateCheckFailed"), AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!update.UpdateAvailable)
            {
                MessageBox.Show(string.Format(S("About.NoUpdates"), update.LatestVersion), AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var confirm = MessageBox.Show(
                string.Format(S("About.UpdateAvailable"), update.LatestVersion, update.InstalledVersion),
                AppIdentity.DisplayName,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
            if (confirm != DialogResult.Yes)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(update.DownloadUrl))
            {
                OpenUrl(update.ReleaseUrl ?? $"{AppIdentity.GitHubRepositoryUrl}/releases/latest");
                return;
            }

            updateButton.Text = S("About.DownloadingUpdate");
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            var targetPath = await AppUpdateService.DownloadAsync(update, downloads);

            var open = MessageBox.Show(
                string.Format(S("About.UpdateDownloaded"), targetPath),
                AppIdentity.DisplayName,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
            if (open == DialogResult.Yes)
            {
                Process.Start(new ProcessStartInfo(targetPath) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(S("About.UpdateError"), ex.Message), AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            updateButton.Text = originalText;
            updateButton.Enabled = true;
        }
    }

    private void SaveSettings()
    {
        _settings.DeviceName = _deviceName.Text.Trim();
        _settings.BindHost = _bindHost.Text.Trim();
        _settings.Port = (int)_port.Value;
        _settings.AutoStartOnLogin = _autoStart.Checked;
        StartupManager.SetEnabled(_settings.AutoStartOnLogin);
        _settings.ShowStartupNotification = _showStartup.Checked;
        _settings.DangerZoneEnabled = _dangerZoneCheck.Checked;

        var selectedLang = Strings.AvailableLanguages[_langCombo.SelectedIndex >= 0 ? _langCombo.SelectedIndex : 0];
        var selectedHaLang = Strings.AvailableLanguages[_haLangCombo.SelectedIndex >= 0 ? _haLangCombo.SelectedIndex : 0];
        var langChanged = _settings.Language != selectedLang;
        _settings.Language = selectedLang;
        _settings.HaLanguage = selectedHaLang;
        Strings.Language = selectedLang;
        Strings.HaLanguage = selectedHaLang;

        _settings.MqttEnabled = _mqttEnabled.Checked;
        _settings.MqttHost = _mqttHost.Text.Trim();
        _settings.MqttPort = (int)_mqttPort.Value;
        _settings.MqttUsername = _mqttUser.Text.Trim();
        _settings.SetMqttPassword(_mqttPass.Text);
        _settings.MqttUseTls = _mqttTls.Checked;
        _settings.MqttRetainDiscovery = _mqttRetain.Checked;

        _settings.HaApiEnabled = _haApiEnabled.Checked;
        _settings.HaApiUrl = _haApiUrl.Text.Trim();
        _settings.SetHaApiToken(_haApiToken.Text);

        _settings.MqttNotificationsEnabled = _capNotify.Checked;
        _settings.MqttMediaPlayerEnabled = _capMedia.Checked;
        _settings.MqttServiceSystemSensorsEnabled = _capSensorsService.Checked;
        _settings.MqttSystemSensorsEnabled = _capSensorsApp.Checked;
        _settings.FastSensorIntervalSeconds = (int)_fastSensorInterval.Value;
        _settings.NormalSensorIntervalSeconds = (int)_normalSensorInterval.Value;
        _settings.HourlySensorIntervalSeconds = (int)_hourlySensorInterval.Value;

        _settings.TrayAppCommands = _cmdRows
            .Where(r => r.TrayApp.Checked)
            .Select(r => r.Def.Name).ToList();
        _settings.ServiceCommands = _cmdRows
            .Where(r => r.Service is not null && r.Service.Checked && r.Def.SupportsService)
            .Select(r => r.Def.Name).ToList();
        _settings.MqttButtonsEnabled = _settings.TrayAppCommands.Count > 0;

        _builtInGrid.EndEdit();
        _settings.BuiltInSensors = _builtInGrid.Rows.Cast<DataGridViewRow>()
            .Where(r => !r.IsNewRow)
            .Select(r => new BuiltInSensorSetting
            {
                Key = Convert.ToString(r.Tag) ?? string.Empty,
                Service = Convert.ToBoolean(r.Cells["Service"].Value ?? false),
                TrayApp = Convert.ToBoolean(r.Cells["TrayApp"].Value ?? false)
            }).ToList();

        _customGrid.EndEdit();
        var sensors = new List<CustomSensorDefinition>();
        foreach (DataGridViewRow row in _customGrid.Rows)
        {
            if (row.IsNewRow) continue;
            var param = Convert.ToString(row.Cells["Parameter"].Value) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(param)) continue;
            var type = Convert.ToString(row.Cells["Type"].Value) ?? CustomSensorTypes.ProcessRunning;
            var name = Convert.ToString(row.Cells["Name"].Value) ?? string.Empty;
            var pollingProfile = Convert.ToString(row.Cells["Profile"].Value) ?? SensorPollingProfiles.ToKey(SensorPollingProfile.Normal);
            sensors.Add(new CustomSensorDefinition
            {
                Id = Convert.ToString(row.Tag) ?? Guid.NewGuid().ToString("N"),
                Enabled = Convert.ToBoolean(row.Cells["Enabled"].Value ?? true),
                Type = type,
                Name = string.IsNullOrWhiteSpace(name) ? type : name.Trim(),
                Parameter = param.Trim(),
                PollingProfile = SensorPollingProfiles.NormalizeKey(pollingProfile, SensorPollingProfile.Normal),
                Service = Convert.ToBoolean(row.Cells["Service"].Value ?? false),
                TrayApp = Convert.ToBoolean(row.Cells["TrayApp"].Value ?? false)
            });
        }
        _settings.CustomSensors = sensors;

        SettingsStore.Save(_paths, _settings);
        SettingsSaved?.Invoke(this, EventArgs.Empty);
        UpdateGeneralStatusMessages();
        UpdateServiceStatusMessage();

        var msg = langChanged
            ? $"{S("Msg.SettingsSaved")}\n\n{S("Msg.RestartRequired")}"
            : S("Msg.SettingsSaved");
        MessageBox.Show(msg, AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ── Layout helpers ─────────────────────────────────────────────
    // Card/field y-values are in LOGICAL pixels (96 DPI).
    // The helpers convert to device pixels internally.

    private Panel MakePage()
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = PageBg, AutoScroll = true, Visible = false };
        page.Layout += (_, _) =>
        {
            var padding = D(56);
            var w = Math.Max(D(400), page.ClientSize.Width - padding);
            foreach (Control c in page.Controls)
            {
                if (c is Label) continue;
                c.Width = w;
            }
        };
        _pages.Add(page);
        return page;
    }

    private void AddPageTitle(Panel page, string text)
    {
        page.Controls.Add(new Label
        {
            Text = text, Font = new Font("Segoe UI", 16F, FontStyle.Bold),
            ForeColor = TextDark, Location = Pt(28, 16), AutoSize = true
        });
    }

    /// <summary>All parameters are in logical (96 DPI) pixels.</summary>
    private Panel MakeCard(Panel page, int x, int y, int width, int height, string? title)
    {
        var card = new Panel
        {
            Location = Pt(x, y), Size = Sz(width, height),
            BackColor = CardBg
        };
        card.Paint += (_, e) =>
        {
            using var pen = new Pen(BorderClr);
            e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
        };
        if (title is not null)
        {
            card.Controls.Add(new Label
            {
                Text = title, Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                ForeColor = TextDark, Location = Pt(20, 14), AutoSize = true
            });
        }
        page.Controls.Add(card);
        return card;
    }

    /// <summary>y is logical. Returns the next logical y.</summary>
    private int AddField(Panel card, string label, Control input, int y, int labelWidth = 160, int inputWidth = 340)
    {
        card.Controls.Add(new Label
        {
            Text = label, Location = Pt(20, y + 4),
            Size = Sz(labelWidth, 22), ForeColor = TextBody
        });
        input.Location = Pt(28 + labelWidth, y);
        input.Size = Sz(inputWidth, 28);
        card.Controls.Add(input);

        // Shrink with the card on small windows; never grow past the design width.
        var designWidth = D(inputWidth);
        card.Layout += (_, _) =>
        {
            var available = card.ClientSize.Width - input.Left - D(20);
            input.Width = Math.Max(D(80), Math.Min(designWidth, available));
        };

        return y + 34;
    }

    /// <summary>y is logical. Returns the next logical y.</summary>
    private int AddCheck(Panel card, CheckBox cb, string text, int y)
    {
        cb.Text = text;
        cb.Location = Pt(20, y);
        cb.Size = new Size(Math.Max(D(420), card.ClientSize.Width - D(40)), D(26));
        cb.ForeColor = TextBody;
        card.Controls.Add(cb);
        return y + 30;
    }

    /// <summary>w and h are logical (96 DPI) pixels.</summary>
    private Button MakePrimaryButton(string text, int w, int h)
    {
        var btn = new Button
        {
            Text = text, Size = Sz(w, h), FlatStyle = FlatStyle.Flat,
            BackColor = BtnBlue, ForeColor = Color.White, Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
        };
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = BtnBlueHover;
        return btn;
    }

    /// <summary>w and h are logical (96 DPI) pixels.</summary>
    private Button MakeSecondaryButton(string text, int w, int h)
    {
        var btn = new Button
        {
            Text = text, Size = Sz(w, h), FlatStyle = FlatStyle.Flat,
            BackColor = CardBg, ForeColor = TextBody, Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 9F)
        };
        btn.FlatAppearance.BorderColor = BorderClr;
        btn.FlatAppearance.BorderSize = 1;
        btn.FlatAppearance.MouseOverBackColor = PageBg;
        return btn;
    }

    private static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { }
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    private static Icon? LoadIcon()
    {
        var stream = typeof(MainForm).Assembly.GetManifestResourceStream("hassagent.ico");
        return stream is not null ? new Icon(stream) : null;
    }

    private sealed record NavEntry(Panel Panel, Panel Indicator, Label Label);
    private sealed record CmdRow(SystemCommandDefinition Def, CheckBox TrayApp, CheckBox? Service);
}
