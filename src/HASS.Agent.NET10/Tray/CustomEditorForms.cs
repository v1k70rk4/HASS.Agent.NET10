using System.Diagnostics;
using System.Drawing;
using System.ServiceProcess;
using System.Windows.Forms;
using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Localization;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.SystemCommands;
using HASS.Agent.Companion.SystemStatus;

namespace HASS.Agent.Companion.Tray;

/// <summary>
/// Shared look and layout of the two editor dialogs (custom command, custom sensor):
/// a label column, a field column, a hint that follows the chosen type, a result line
/// for the Test button, and Test / OK / Cancel at the bottom.
/// All layout values are logical (96 DPI) pixels, like in MainForm.
/// </summary>
internal abstract class CustomEditorForm : Form
{
    protected static readonly Color PageBg = Color.FromArgb(241, 245, 249);
    protected static readonly Color CardBg = Color.White;
    protected static readonly Color BorderClr = Color.FromArgb(226, 232, 240);
    protected static readonly Color TextDark = Color.FromArgb(15, 23, 42);
    protected static readonly Color TextBody = Color.FromArgb(51, 65, 85);
    protected static readonly Color TextMuted = Color.FromArgb(100, 116, 139);
    protected static readonly Color BtnBlue = Color.FromArgb(37, 99, 235);
    protected static readonly Color BtnBlueHover = Color.FromArgb(29, 78, 216);
    protected static readonly Color ErrorRed = Color.FromArgb(185, 28, 28);
    protected static readonly Color OkGreen = Color.FromArgb(21, 128, 61);

    protected const int LabelX = 20;
    protected const int FieldX = 170;
    protected const int FieldWidth = 430;
    protected const int FormWidth = 620;
    protected const int RowHeight = 36;

    protected readonly Label ResultLabel = new();
    protected readonly Button TestButton;
    private readonly Button _okButton;
    private readonly Button _cancelButton;

    protected CustomEditorForm(string title)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 9.5F);
        AutoScaleMode = AutoScaleMode.None;
        BackColor = CardBg;

        TestButton = MakeButton(S("Editor.Test"), 110, primary: false);
        _okButton = MakeButton(S("Editor.Ok"), 90, primary: true);
        _cancelButton = MakeButton(S("Btn.Cancel"), 90, primary: false);
        _cancelButton.DialogResult = DialogResult.Cancel;
        _okButton.Click += (_, _) =>
        {
            if (TryAccept())
            {
                DialogResult = DialogResult.OK;
            }
        };
        TestButton.Click += async (_, _) =>
        {
            TestButton.Enabled = false;
            try
            {
                await RunTestAsync();
            }
            finally
            {
                if (!IsDisposed)
                {
                    TestButton.Enabled = true;
                }
            }
        };

        AcceptButton = _okButton;
        CancelButton = _cancelButton;
    }

    protected static string S(string key) => Strings.Get(key);

    protected int D(int v) => (int)(v * DeviceDpi / 96f);
    protected Point Pt(int x, int y) => new(D(x), D(y));
    protected Size Sz(int w, int h) => new(D(w), D(h));

    /// <summary>Checks the fields; false keeps the dialog open (and says why in the result line).</summary>
    protected abstract bool TryAccept();

    protected abstract Task RunTestAsync();

    /// <summary>Places the result line and the buttons below the last field row.</summary>
    protected void FinishLayout(int y)
    {
        ResultLabel.Location = Pt(LabelX, y);
        ResultLabel.Size = Sz(FormWidth - LabelX * 2, 40);
        ResultLabel.ForeColor = TextMuted;
        ResultLabel.Font = new Font("Segoe UI", 9F);
        Controls.Add(ResultLabel);

        var buttonY = y + 48;
        TestButton.Location = Pt(LabelX, buttonY);
        _cancelButton.Location = Pt(FormWidth - LabelX - 90, buttonY);
        _okButton.Location = Pt(FormWidth - LabelX - 90 - 8 - 90, buttonY);
        Controls.Add(TestButton);
        Controls.Add(_okButton);
        Controls.Add(_cancelButton);

        ClientSize = Sz(FormWidth, buttonY + 32 + 18);
    }

    protected void ShowResult(string text, Color color)
    {
        if (IsDisposed)
        {
            return;
        }

        ResultLabel.ForeColor = color;
        ResultLabel.Text = text;
    }

    protected Label AddLabel(string text, int y)
    {
        var label = new Label
        {
            Text = text, Location = Pt(LabelX, y + 4), Size = Sz(FieldX - LabelX - 8, 22),
            ForeColor = TextDark
        };
        Controls.Add(label);
        return label;
    }

    protected TextBox AddTextBox(int y, int width = FieldWidth)
    {
        var box = new TextBox { Location = Pt(FieldX, y), Size = Sz(width, 26) };
        Controls.Add(box);
        return box;
    }

    protected ComboBox AddOptionBox(int y, IEnumerable<Option> options, int width = FieldWidth)
    {
        var box = new ComboBox
        {
            Location = Pt(FieldX, y), Size = Sz(width, 26),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        foreach (var option in options)
        {
            box.Items.Add(option);
        }

        Controls.Add(box);
        return box;
    }

    /// <summary>The hint under the type list: what this type does and what to type, with an example.</summary>
    protected Label AddHint(int y, int height)
    {
        var hint = new Label
        {
            Location = Pt(FieldX, y), Size = Sz(FieldWidth, height),
            ForeColor = TextBody, BackColor = Color.FromArgb(239, 246, 255),
            Padding = new Padding(D(8), D(6), D(8), D(6)),
            Font = new Font("Segoe UI", 8.75F)
        };
        Controls.Add(hint);
        return hint;
    }

    protected (CheckBox Enabled, CheckBox TrayApp, CheckBox Service) AddRoleBoxes(int y)
    {
        var enabled = new CheckBox { Text = S("Sensors.Active"), Location = Pt(FieldX, y), Size = Sz(110, 24), ForeColor = TextBody };
        var trayApp = new CheckBox { Text = S("Editor.RunsInTray"), Location = Pt(FieldX + 120, y), Size = Sz(140, 24), ForeColor = TextBody };
        var service = new CheckBox { Text = S("Editor.RunsInService"), Location = Pt(FieldX + 270, y), Size = Sz(160, 24), ForeColor = TextBody };
        Controls.Add(enabled);
        Controls.Add(trayApp);
        Controls.Add(service);
        return (enabled, trayApp, service);
    }

    protected Button MakeButton(string text, int width, bool primary)
    {
        var button = new Button
        {
            Text = text, Size = Sz(width, 32), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            BackColor = primary ? BtnBlue : CardBg,
            ForeColor = primary ? Color.White : TextBody,
            Font = new Font("Segoe UI", 9F, primary ? FontStyle.Bold : FontStyle.Regular)
        };
        button.FlatAppearance.BorderColor = BorderClr;
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.MouseOverBackColor = primary ? BtnBlueHover : PageBg;
        return button;
    }

    protected static string SelectedKey(ComboBox box) => (box.SelectedItem as Option)?.Key ?? string.Empty;

    protected static void SelectKey(ComboBox box, string key)
    {
        foreach (var item in box.Items)
        {
            if (item is Option option && string.Equals(option.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }

        if (box.Items.Count > 0)
        {
            box.SelectedIndex = 0;
        }
    }

    protected string? BrowseForFile(string filter)
    {
        using var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    internal sealed record Option(string Key, string Text)
    {
        public override string ToString() => Text;
    }
}

/// <summary>Adds or edits one custom command.</summary>
internal sealed class CustomCommandEditorForm : CustomEditorForm
{
    private static readonly TimeSpan KeyTestDelay = TimeSpan.FromSeconds(3);

    private readonly string _id;
    private readonly ComboBox _type;
    private readonly Label _hint;
    private readonly TextBox _name;
    private readonly Label _commandLabel;
    private readonly TextBox _command;
    private readonly Button _browse;
    private readonly Label _argumentsLabel;
    private readonly TextBox _arguments;
    private readonly CheckBox _enabled;
    private readonly CheckBox _trayApp;
    private readonly CheckBox _service;
    private bool _serviceBeforeLock;

    public CustomCommandEditorForm(CustomCommandDefinition command)
        : base(S("Editor.CommandTitle"))
    {
        _id = command.Id;

        var y = 20;
        AddLabel(S("Sensors.Type"), y);
        _type = AddOptionBox(y, new[]
        {
            CustomCommandTypes.Process,
            CustomCommandTypes.PowerShell,
            CustomCommandTypes.Pwsh,
            CustomCommandTypes.Key,
            CustomCommandTypes.Url,
            CustomCommandTypes.WebView
        }.Select(type => new Option(type, S($"CmdType.{type}"))));

        y += RowHeight;
        _hint = AddHint(y, 76);

        y += 76 + 12;
        AddLabel(S("Editor.NameInHa"), y);
        _name = AddTextBox(y);

        y += RowHeight;
        _commandLabel = AddLabel(string.Empty, y);
        _command = AddTextBox(y);
        _browse = MakeButton(S("Editor.Browse"), 90, primary: false);
        _browse.Size = Sz(90, 27);
        _browse.Location = Pt(FieldX + FieldWidth - 90, y - 1);
        _browse.Click += (_, _) =>
        {
            var file = BrowseForFile(SelectedKey(_type) == CustomCommandTypes.Process
                ? S("Editor.FilterPrograms")
                : S("Editor.FilterScripts"));
            if (file is not null)
            {
                _command.Text = file.Contains(' ') && SelectedKey(_type) == CustomCommandTypes.Process ? $"\"{file}\"" : file;
            }
        };
        Controls.Add(_browse);

        y += RowHeight;
        _argumentsLabel = AddLabel(S("Cap.ArgumentsColumn"), y);
        _arguments = AddTextBox(y);

        y += RowHeight + 4;
        AddLabel(S("Editor.Where"), y);
        (_enabled, _trayApp, _service) = AddRoleBoxes(y);

        FinishLayout(y + RowHeight);

        _name.Text = command.Name;
        _command.Text = command.Command;
        _arguments.Text = command.Arguments;
        _enabled.Checked = command.Enabled;
        _trayApp.Checked = command.TrayApp;
        _service.Checked = command.Service;
        _serviceBeforeLock = command.Service;

        _type.SelectedIndexChanged += (_, _) => ApplyType();
        SelectKey(_type, CustomCommandTypes.Normalize(command.Type));
    }

    public CustomCommandDefinition Result { get; private set; } = new();

    private void ApplyType()
    {
        var type = SelectedKey(_type);
        var needsUserSession = NeedsUserSession(type);
        var canBrowse = !needsUserSession;

        _hint.Text = S($"CmdHint.{type}");
        _commandLabel.Text = S($"CmdField.{type}");
        _browse.Visible = canBrowse;
        _command.Width = D(canBrowse ? FieldWidth - 98 : FieldWidth);
        _argumentsLabel.Text = type == CustomCommandTypes.WebView ? S("Editor.WindowSize") : S("Cap.ArgumentsColumn");
        _argumentsLabel.Visible = HasArguments(type);
        _arguments.Visible = HasArguments(type);
        _arguments.PlaceholderText = type == CustomCommandTypes.WebView ? "1024x720" : string.Empty;

        // Keys, addresses and windows act on the user's desktop; the service cannot run them.
        if (needsUserSession)
        {
            if (_service.Enabled)
            {
                _serviceBeforeLock = _service.Checked;
            }

            _service.Checked = false;
            _service.Enabled = false;
            _trayApp.Checked = true;
        }
        else if (!_service.Enabled)
        {
            _service.Enabled = true;
            _service.Checked = _serviceBeforeLock;
        }

        ShowResult(string.Empty, TextMuted);
    }

    private static bool NeedsUserSession(string type) =>
        type is CustomCommandTypes.Key or CustomCommandTypes.Url or CustomCommandTypes.WebView;

    private static bool HasArguments(string type) =>
        type is not (CustomCommandTypes.Key or CustomCommandTypes.Url);

    private CustomCommandDefinition Build()
    {
        var type = SelectedKey(_type);
        var needsUserSession = NeedsUserSession(type);
        var command = _command.Text.Trim();
        return new CustomCommandDefinition
        {
            Id = _id,
            Type = type,
            Name = string.IsNullOrWhiteSpace(_name.Text) ? command : _name.Text.Trim(),
            Command = command,
            Arguments = HasArguments(type) ? _arguments.Text.Trim() : string.Empty,
            Enabled = _enabled.Checked,
            TrayApp = _trayApp.Checked,
            Service = !needsUserSession && _service.Checked
        };
    }

    /// <summary>What is wrong with the fields, or null. Checks what can be checked without running anything.</summary>
    private string? FindProblem(CustomCommandDefinition command)
    {
        if (command.Command.Length == 0)
        {
            return string.Format(S("Editor.FieldMissing"), _commandLabel.Text);
        }

        if (command.IsKey && !KeySender.TryParse(command.Command, out _, out var unknownKey))
        {
            return unknownKey.Length > 0
                ? string.Format(S("Editor.UnknownKey"), unknownKey)
                : S("Editor.NoKeys");
        }

        if (command.IsUrl && !SystemCommandService.IsOpenableAddress(command.Command))
        {
            return S("Editor.InvalidAddress");
        }

        if (command.IsWebView && !WebViewOptions.IsWebAddress(command.Command))
        {
            return S("Editor.InvalidWebAddress");
        }

        if (command.IsWebView && command.Arguments.Length > 0 && !WebViewOptions.TryParseSize(command.Arguments, out _))
        {
            return S("Editor.InvalidSize");
        }

        return null;
    }

    protected override bool TryAccept()
    {
        var command = Build();
        if (FindProblem(command) is { } problem)
        {
            ShowResult(problem, ErrorRed);
            return false;
        }

        Result = command;
        return true;
    }

    protected override async Task RunTestAsync()
    {
        var command = Build();
        if (FindProblem(command) is { } problem)
        {
            ShowResult(problem, ErrorRed);
            return;
        }

        if (command.IsKey)
        {
            // The keys go to whichever window is in front, and right now that is this dialog.
            ShowResult(string.Format(S("Editor.KeyTestCountdown"), (int)KeyTestDelay.TotalSeconds), TextBody);
            await Task.Delay(KeyTestDelay);
        }

        var result = await SystemCommandService.ExecuteCustomCommandAsync(command);
        ShowResult(DescribeOutcome(result), result.Ok ? OkGreen : ErrorRed);
    }

    private static string DescribeOutcome(CustomCommandResult result)
    {
        return result.Outcome switch
        {
            CustomCommandOutcome.Done => S("Editor.TestDone"),
            CustomCommandOutcome.UnknownKey => string.Format(S("Editor.UnknownKey"), result.Detail),
            CustomCommandOutcome.NoKeys => S("Editor.NoKeys"),
            CustomCommandOutcome.InputRefused => S("Editor.InputRefused"),
            CustomCommandOutcome.InvalidAddress => S("Editor.InvalidAddress"),
            CustomCommandOutcome.InvalidSize => S("Editor.InvalidSize"),
            CustomCommandOutcome.NeedsUserSession => S("Editor.NeedsUserSession"),
            CustomCommandOutcome.NotStarted => S("Editor.NotStarted"),
            _ => string.Format(S("Sensors.ValueError"), result.Detail)
        };
    }
}

/// <summary>Adds or edits one custom sensor.</summary>
internal sealed class CustomSensorEditorForm : CustomEditorForm
{
    private readonly string _id;
    private readonly FileLog _log;
    private readonly ComboBox _type;
    private readonly Label _hint;
    private readonly TextBox _name;
    private readonly Label _parameterLabel;
    private readonly ComboBox _parameter;
    private readonly Button _browse;
    private readonly Button _connection;
    private readonly CompanionSettings _settings;
    private IReadOnlyList<LibreHardwareMonitorClient.Reading> _hardwareReadings = [];
    private int _hardwareLoad;
    private readonly TextBox _unit;
    private readonly ComboBox _profile;
    private readonly CheckBox _enabled;
    private readonly CheckBox _trayApp;
    private readonly CheckBox _service;

    public CustomSensorEditorForm(CustomSensorDefinition sensor, CompanionSettings settings, FileLog log, IEnumerable<Option> pollingProfiles)
        : base(S("Editor.SensorTitle"))
    {
        _id = sensor.Id;
        _settings = settings;
        _log = log;

        var y = 20;
        AddLabel(S("Sensors.Type"), y);
        _type = AddOptionBox(y, new[]
        {
            CustomSensorTypes.ProcessRunning,
            CustomSensorTypes.ServiceStatus,
            CustomSensorTypes.DiskFree,
            CustomSensorTypes.BuiltInAttribute,
            CustomSensorTypes.Command,
            CustomSensorTypes.CommandPowerShell,
            CustomSensorTypes.CommandPwsh,
            CustomSensorTypes.LibreHardwareMonitor
        }.Select(type => new Option(type, S($"SensorType.{type}"))));

        y += RowHeight;
        _hint = AddHint(y, 76);

        y += 76 + 12;
        AddLabel(S("Editor.NameInHa"), y);
        _name = AddTextBox(y);

        y += RowHeight;
        _parameterLabel = AddLabel(string.Empty, y);
        // Editable, with what the PC has to offer in the list: running processes,
        // installed services, drives, or the attributes of the built-in sensors.
        _parameter = new ComboBox
        {
            Location = Pt(FieldX, y), Size = Sz(FieldWidth, 26),
            DropDownStyle = ComboBoxStyle.DropDown,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.ListItems
        };
        Controls.Add(_parameter);
        _parameter.SelectionChangeCommitted += (_, _) => FillFromHardwareSensor(_parameter.SelectedItem as string);
        _browse = MakeButton(S("Editor.Browse"), 90, primary: false);
        _browse.Size = Sz(90, 27);
        _browse.Location = Pt(FieldX + FieldWidth - 90, y - 1);
        _browse.Click += (_, _) =>
        {
            var file = BrowseForFile(SelectedKey(_type) == CustomSensorTypes.Command
                ? S("Editor.FilterPrograms")
                : S("Editor.FilterScripts"));
            if (file is not null)
            {
                _parameter.Text = file.Contains(' ') && SelectedKey(_type) == CustomSensorTypes.Command ? $"\"{file}\"" : file;
            }
        };
        Controls.Add(_browse);

        // LibreHardwareMonitor: where it listens, and the login when it asks for one.
        _connection = MakeButton(S("Editor.Connection"), 90, primary: false);
        _connection.Size = Sz(90, 27);
        _connection.Location = _browse.Location;
        _connection.Click += (_, _) =>
        {
            using var dialog = new HardwareMonitorConnectionForm(_settings);
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                ApplyType(keepParameter: true);
            }
        };
        Controls.Add(_connection);

        y += RowHeight;
        AddLabel(S("Editor.Unit"), y);
        _unit = AddTextBox(y, 120);
        Controls.Add(new Label
        {
            Text = S("Editor.UnitHint"), Location = Pt(FieldX + 130, y + 4), Size = Sz(FieldWidth - 130, 22),
            ForeColor = TextMuted, Font = new Font("Segoe UI", 8.75F)
        });

        y += RowHeight;
        AddLabel(S("Editor.Refresh"), y);
        _profile = AddOptionBox(y, pollingProfiles, 160);

        y += RowHeight + 4;
        AddLabel(S("Editor.Where"), y);
        (_enabled, _trayApp, _service) = AddRoleBoxes(y);

        FinishLayout(y + RowHeight);

        _name.Text = sensor.Name;
        _unit.Text = sensor.Unit;
        _enabled.Checked = sensor.Enabled;
        _trayApp.Checked = sensor.TrayApp;
        _service.Checked = sensor.Service;
        SelectKey(_profile, SensorPollingProfiles.NormalizeKey(sensor.PollingProfile, SensorPollingProfile.Normal));

        _type.SelectedIndexChanged += (_, _) => ApplyType(keepParameter: false);
        SelectKey(_type, CustomSensorTypes.Normalize(sensor.Type));
        ApplyType(keepParameter: true);
        _parameter.Text = sensor.Parameter;
    }

    public CustomSensorDefinition Result { get; private set; } = new();

    private void ApplyType(bool keepParameter)
    {
        var type = SelectedKey(_type);
        var isCommand = type is CustomSensorTypes.Command or CustomSensorTypes.CommandPowerShell or CustomSensorTypes.CommandPwsh;
        var text = _parameter.Text;

        var isHardwareMonitor = type == CustomSensorTypes.LibreHardwareMonitor;

        _hint.Text = S($"SensorHint.{type}");
        _parameterLabel.Text = S($"SensorField.{type}");
        _browse.Visible = isCommand;
        _connection.Visible = isHardwareMonitor;
        _parameter.Width = D(isCommand || isHardwareMonitor ? FieldWidth - 98 : FieldWidth);

        _parameter.BeginUpdate();
        _parameter.Items.Clear();
        foreach (var suggestion in Suggestions(type))
        {
            _parameter.Items.Add(suggestion);
        }

        _parameter.EndUpdate();
        // The hardware sensor lines are long; the list may be wider than the field.
        _parameter.DropDownWidth = D(isHardwareMonitor ? 760 : isCommand ? FieldWidth - 98 : FieldWidth);
        // What was typed for another type means nothing here.
        _parameter.Text = keepParameter ? text : string.Empty;
        ShowResult(string.Empty, TextMuted);

        _hardwareLoad++;
        if (isHardwareMonitor)
        {
            _ = LoadHardwareSensorsAsync(_hardwareLoad);
        }
    }

    // The list of hardware sensors comes over HTTP, and a LibreHardwareMonitor that is not
    // running only says so after a timeout: read off the UI thread, fill the list after.
    private async Task LoadHardwareSensorsAsync(int load)
    {
        ShowResult(S("Sensors.ValueLoading"), TextMuted);
        IReadOnlyList<LibreHardwareMonitorClient.Reading> readings = [];
        string? error = null;
        try
        {
            readings = await Task.Run(LibreHardwareMonitorClient.ReadAll);
        }
        catch (Exception ex)
        {
            // An empty list needs a reason: not running, web server off, or the login.
            error = ex.Message;
        }

        // Closed, or switched to another type (or reloaded) in the meantime.
        if (IsDisposed || load != _hardwareLoad)
        {
            return;
        }

        _hardwareReadings = readings;
        var text = _parameter.Text;
        _parameter.BeginUpdate();
        _parameter.Items.Clear();
        // "hardware / name = value | id": only the id is stored (see Build).
        foreach (var line in readings
            .Select(reading => $"{HardwareSensorTitle(reading)} = {reading.Text} | {reading.Id}")
            .Order(StringComparer.CurrentCultureIgnoreCase))
        {
            _parameter.Items.Add(line);
        }

        _parameter.EndUpdate();
        _parameter.Text = text;
        ShowResult(error ?? string.Empty, error is null ? TextMuted : ErrorRed);
    }

    private static IEnumerable<string> Suggestions(string type)
    {
        try
        {
            switch (type)
            {
                case CustomSensorTypes.ProcessRunning:
                    var processes = Process.GetProcesses();
                    try
                    {
                        return processes
                            .Select(process => process.ProcessName)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Order(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                    }
                    finally
                    {
                        foreach (var process in processes)
                        {
                            process.Dispose();
                        }
                    }

                case CustomSensorTypes.ServiceStatus:
                    var services = ServiceController.GetServices();
                    try
                    {
                        return services
                            .Select(service => service.ServiceName)
                            .Order(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                    }
                    finally
                    {
                        foreach (var service in services)
                        {
                            service.Dispose();
                        }
                    }

                case CustomSensorTypes.DiskFree:
                    return DriveInfo.GetDrives()
                        .Where(drive => drive.DriveType == DriveType.Fixed)
                        .Select(drive => drive.Name.TrimEnd('\\'))
                        .ToList();

                case CustomSensorTypes.BuiltInAttribute:
                    return BuiltInSensorCatalog.Sensors
                        .SelectMany(sensor => sensor.AttributePaths ?? [])
                        .ToList();
            }
        }
        catch
        {
            // The list is only a convenience; the field can always be typed into.
        }

        return [];
    }

    private static string HardwareSensorTitle(LibreHardwareMonitorClient.Reading reading) =>
        reading.Hardware.Length > 0 ? $"{reading.Hardware} / {reading.Name}" : reading.Name;

    // Picking a hardware sensor from the list brings its unit, and a name when there is none yet.
    private void FillFromHardwareSensor(string? listItem)
    {
        if (listItem is null || SelectedKey(_type) != CustomSensorTypes.LibreHardwareMonitor)
        {
            return;
        }

        var id = LibreHardwareMonitorClient.ParseSensorId(listItem);
        var reading = _hardwareReadings.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        if (reading is null)
        {
            return;
        }

        _unit.Text = reading.Unit;
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            _name.Text = HardwareSensorTitle(reading);
        }
    }

    private CustomSensorDefinition Build()
    {
        var type = SelectedKey(_type);
        var parameter = _parameter.Text.Trim();
        if (type == CustomSensorTypes.LibreHardwareMonitor)
        {
            parameter = LibreHardwareMonitorClient.ParseSensorId(parameter);
        }

        return new CustomSensorDefinition
        {
            Id = _id,
            Type = type,
            Name = string.IsNullOrWhiteSpace(_name.Text) ? type : _name.Text.Trim(),
            Parameter = parameter,
            Unit = _unit.Text.Trim(),
            PollingProfile = SensorPollingProfiles.NormalizeKey(SelectedKey(_profile), SensorPollingProfile.Normal),
            Enabled = _enabled.Checked,
            TrayApp = _trayApp.Checked,
            Service = _service.Checked
        };
    }

    protected override bool TryAccept()
    {
        var sensor = Build();
        if (sensor.Parameter.Length == 0)
        {
            ShowResult(string.Format(S("Editor.FieldMissing"), _parameterLabel.Text), ErrorRed);
            return false;
        }

        Result = sensor;
        return true;
    }

    protected override async Task RunTestAsync()
    {
        var sensor = Build();
        if (sensor.Parameter.Length == 0)
        {
            ShowResult(string.Format(S("Editor.FieldMissing"), _parameterLabel.Text), ErrorRed);
            return;
        }

        ShowResult(S("Sensors.ValueLoading"), TextMuted);
        sensor.Enabled = true;
        sensor.TrayApp = true;
        try
        {
            var value = await Task.Run(() => SystemMetricsService.TestCustomSensorValue(sensor, _log));
            ShowResult(
                string.Format(S("Editor.TestValue"), MainForm.FormatSensorValue(value), sensor.Unit).TrimEnd(),
                value is null ? ErrorRed : OkGreen);
        }
        catch (Exception ex)
        {
            ShowResult(string.Format(S("Sensors.ValueError"), ex.Message), ErrorRed);
        }
    }
}

/// <summary>Where LibreHardwareMonitor's web server is, and its login when it has one.</summary>
internal sealed class HardwareMonitorConnectionForm : Form
{
    private readonly CompanionSettings _settings;
    private readonly TextBox _url = new();
    private readonly TextBox _user = new();
    private readonly TextBox _password = new() { UseSystemPasswordChar = true };

    public HardwareMonitorConnectionForm(CompanionSettings settings)
    {
        _settings = settings;

        Text = "LibreHardwareMonitor";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Segoe UI", 9.5F);
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.White;
        ClientSize = Sz(460, 216);

        AddRow(Strings.Get("Editor.ConnectionUrl"), _url, 20);
        AddRow(Strings.Get("Editor.ConnectionUser"), _user, 56);
        AddRow(Strings.Get("Editor.ConnectionPassword"), _password, 92);
        var hint = new Label
        {
            Location = Pt(20, 122), Size = Sz(420, 32), Font = new Font("Segoe UI", 8.75F)
        };
        Controls.Add(hint);

        // LibreHardwareMonitor's web server speaks plain HTTP only, so a login sent to
        // another machine travels unencrypted. On this PC it never leaves the machine.
        void UpdateHint()
        {
            var exposed = _user.Text.Trim().Length > 0 && !IsLoopback(_url.Text);
            hint.Text = Strings.Get(exposed ? "Editor.ConnectionWarning" : "Editor.ConnectionHint");
            hint.ForeColor = exposed ? Color.FromArgb(185, 28, 28) : Color.FromArgb(100, 116, 139);
        }

        _url.TextChanged += (_, _) => UpdateHint();
        _user.TextChanged += (_, _) => UpdateHint();

        var ok = new Button { Text = Strings.Get("Editor.Ok"), Location = Pt(256, 168), Size = Sz(88, 30) };
        var cancel = new Button { Text = Strings.Get("Btn.Cancel"), Location = Pt(352, 168), Size = Sz(88, 30), DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) =>
        {
            // In memory right away, so the sensor list can be read with it; written to
            // disk with the rest of the settings when the settings window is saved.
            _settings.LibreHardwareMonitorUrl = string.IsNullOrWhiteSpace(_url.Text)
                ? LibreHardwareMonitorClient.DefaultUrl
                : _url.Text.Trim();
            _settings.LibreHardwareMonitorUser = _user.Text.Trim();
            _settings.SetLibreHardwareMonitorPassword(_password.Text);
            _settings.ApplyLibreHardwareMonitor();
            DialogResult = DialogResult.OK;
        };
        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;

        _url.Text = settings.LibreHardwareMonitorUrl;
        _user.Text = settings.LibreHardwareMonitorUser;
        _password.Text = settings.GetLibreHardwareMonitorPassword();
        UpdateHint();
    }

    private static bool IsLoopback(string url)
    {
        var text = string.IsNullOrWhiteSpace(url) ? LibreHardwareMonitorClient.DefaultUrl : url.Trim();
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.IsLoopback;
    }

    private int D(int v) => (int)(v * DeviceDpi / 96f);
    private Point Pt(int x, int y) => new(D(x), D(y));
    private Size Sz(int w, int h) => new(D(w), D(h));

    private void AddRow(string label, TextBox box, int y)
    {
        Controls.Add(new Label { Text = label, Location = Pt(20, y + 4), Size = Sz(130, 22) });
        box.Location = Pt(156, y);
        box.Size = Sz(284, 26);
        Controls.Add(box);
    }
}
