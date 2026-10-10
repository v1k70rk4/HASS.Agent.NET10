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
        // What each one runs, not only its name: the settings can be written by others, and
        // the administrator approves what is shown here.
        var shown = Environment.NewLine + string.Join(Environment.NewLine, PendingDescriptions(settings, current));
        if (MessageBox.Show(owner, string.Format(Strings.Get("SvcMgr.ApprovalNeeded"), shown), AppIdentity.DisplayName,
                MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
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

    /// <summary>"Name: what it runs" for each entry <see cref="PendingNames"/> lists, cut to one short line.</summary>
    internal static IReadOnlyList<string> PendingDescriptions(CompanionSettings settings, ServicePolicy policy) =>
        settings.CustomCommands.Where(command => ServicePolicy.NeedsApproval(command) && !policy.Allows(command))
            .Select(command => Describe(command.Name, $"{command.Command} {command.Arguments}"))
            .Concat(settings.CustomSensors.Where(sensor => ServicePolicy.NeedsApproval(sensor) && !policy.Allows(sensor))
                .Select(sensor => Describe(sensor.Name, sensor.Parameter)))
            .ToList();

    private static string Describe(string name, string? runs)
    {
        const int Longest = 120;
        var line = string.Join(' ', (runs ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return $"• {name}: {(line.Length > Longest ? line[..Longest] + "…" : line)}";
    }

    /// <summary>The names of what the settings ask the service to run and the policy does not approve.</summary>
    internal static IReadOnlyList<string> PendingNames(CompanionSettings settings, ServicePolicy policy) =>
        settings.CustomCommands.Where(command => ServicePolicy.NeedsApproval(command) && !policy.Allows(command)).Select(command => command.Name)
            .Concat(settings.CustomSensors.Where(sensor => ServicePolicy.NeedsApproval(sensor) && !policy.Allows(sensor)).Select(sensor => sensor.Name))
            .ToList();
}
