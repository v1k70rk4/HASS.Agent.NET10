using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Forms;
using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Localization;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.Runtime;
using HASS.Agent.Companion.SystemService;

namespace HASS.Agent.Companion.Tray;

/// <summary>
/// After the settings are saved: when they ask the service to run a command or a command
/// sensor that no administrator has approved yet, the app says so and asks Windows to run
/// itself as administrator to approve it (see <see cref="ServicePolicy"/>). Declining keeps
/// everything working except that the service skips those.
/// </summary>
internal static class ServiceApproval
{
    public static async Task RequestIfNeededAsync(IWin32Window owner, CompanionSettings settings, FileLog log)
    {
        if (!CompanionServiceManager.IsInstalled())
        {
            return;
        }

        var current = ServicePolicy.Load(ServicePolicy.DefaultPath);
        var pending = PendingNames(settings, current);
        if (pending.Count == 0)
        {
            return;
        }

        var names = string.Join(", ", pending);
        // What each one runs, in full and line by line, not only its name: the settings can be
        // written by others, and the administrator approves exactly what is shown here.
        var shown = string.Join(Environment.NewLine + Environment.NewLine, PendingDescriptions(settings, current));
        if (!Ask(owner, Strings.Get("SvcMgr.ApprovalNeeded"), shown))
        {
            log.Info($"Service approval skipped by the user: {names}.");
            return;
        }

        var (outcome, error) = await ElevatedRun.StartAsync($"--approve-service-commands --quiet --expect {current.PendingKey(settings)}");
        if (error is not null)
        {
            log.Warning($"Unable to start the service approval: {error}");
        }

        if (outcome == ElevatedOutcome.StillRunning)
        {
            // Not a refusal: the approval may still finish, and the service picks it up when it does.
            log.Warning($"The service approval has not finished yet: {names}.");
            MessageBox.Show(owner, string.Format(Strings.Get("SvcMgr.ApprovalPending"), names), AppIdentity.DisplayName,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var approved = ServicePolicy.Load(ServicePolicy.DefaultPath);
        if (PendingNames(settings, approved).Count == 0)
        {
            log.Info($"Approved for the service: {names}.");
            return;
        }

        log.Warning($"Not approved for the service: {names}.");
        MessageBox.Show(owner, string.Format(Strings.Get("SvcMgr.ApprovalDeclined"), names), AppIdentity.DisplayName,
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>
    /// "Name:" and below it everything it runs, whole and with its own line breaks: a script
    /// cut short, or put on one line, could hide the part after a comment sign.
    /// </summary>
    internal static IReadOnlyList<string> PendingDescriptions(CompanionSettings settings, ServicePolicy policy) =>
        settings.CustomCommands.Where(command => ServicePolicy.NeedsApproval(command) && !policy.Allows(command))
            .Select(command => Describe(command.Name, string.IsNullOrEmpty(command.Arguments) ? command.Command : $"{command.Command} {command.Arguments}"))
            .Concat(settings.CustomSensors.Where(sensor => ServicePolicy.NeedsApproval(sensor) && !policy.Allows(sensor))
                .Select(sensor => Describe(sensor.Name, sensor.Parameter)))
            .ToList();

    private static string Describe(string name, string? runs)
    {
        var lines = (runs ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        return $"{name}:{Environment.NewLine}{string.Join(Environment.NewLine, lines.Select(line => "    " + line))}";
    }

    /// <summary>The question above, and what is to be approved in a box that scrolls, as long as it is.</summary>
    private static bool Ask(IWin32Window owner, string question, string details)
    {
        using var form = new Form
        {
            Text = AppIdentity.DisplayName,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.Sizable,
            MinimizeBox = false,
            ShowInTaskbar = false,
            Font = new Font("Segoe UI", 9.5F),
            BackColor = Color.White,
        };
        int D(int value) => (int)(value * form.DeviceDpi / 96f);
        form.ClientSize = new Size(D(620), D(420));
        form.MinimumSize = new Size(D(420), D(300));

        var label = new Label { Text = question, Dock = DockStyle.Top, AutoSize = false, Height = D(92), Padding = new Padding(D(4)) };
        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9.5F),
            BackColor = Color.FromArgb(248, 250, 252),
            Text = details,
        };
        var ok = new Button { Text = Strings.Get("Editor.Ok"), DialogResult = DialogResult.OK, Size = new Size(D(96), D(30)) };
        var cancel = new Button { Text = Strings.Get("Btn.Cancel"), DialogResult = DialogResult.Cancel, Size = new Size(D(96), D(30)) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = D(44), Padding = new Padding(0, D(8), 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(D(12)) };
        body.Controls.Add(box);
        body.Controls.Add(buttons);
        body.Controls.Add(label);
        form.Controls.Add(body);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.Shown += (_, _) => box.Select(0, 0);

        return form.ShowDialog(owner) == DialogResult.OK;
    }

    /// <summary>The names of what the settings ask the service to run and the policy does not approve.</summary>
    internal static IReadOnlyList<string> PendingNames(CompanionSettings settings, ServicePolicy policy) =>
        settings.CustomCommands.Where(command => ServicePolicy.NeedsApproval(command) && !policy.Allows(command)).Select(command => command.Name)
            .Concat(settings.CustomSensors.Where(sensor => ServicePolicy.NeedsApproval(sensor) && !policy.Allows(sensor)).Select(sensor => sensor.Name))
            .ToList();
}
