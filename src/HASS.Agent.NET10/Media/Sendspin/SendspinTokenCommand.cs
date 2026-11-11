using System.Windows.Forms;
using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Localization;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.Runtime;

namespace HASS.Agent.Companion.Media.Sendspin;

/// <summary>
/// <c>HASS.Agent.NET10.exe --sendspin-token [--new] [--quiet]</c>: the pairing token of the PC's
/// Sendspin player, as the Media player page's button gives it, for a PC set up by a script or
/// by Intune, or a kiosk PC whose app has no window. The token goes to standard output (for a
/// script that captures it); without <c>--quiet</c> it is also put on the clipboard and said so.
/// <c>--new</c> makes a new one, as the page's other button. Where no window can show (a script
/// run as SYSTEM by Intune, a task in a disconnected session), it is quiet by itself. The exit code is 0 with a token and
/// 1 without, with the reason on standard error. Whoever runs it needs to be allowed to use the
/// settings of the PC; the token itself is not logged.
/// </summary>
internal static class SendspinTokenCommand
{
    public const string Switch = "--sendspin-token";

    public static bool Applies(string[] args) =>
        args.Any(arg => string.Equals(arg, Switch, StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        // Quiet by itself where no one could see a window: a script Intune runs as SYSTEM, in the
        // session of the services (a dialog there throws, after the token went out), or a task in
        // a disconnected session (a dialog there waits for no one).
        var quiet = !Environment.UserInteractive
            || SystemStatus.SystemMetricsService.IsSessionDisconnected((uint)System.Diagnostics.Process.GetCurrentProcess().SessionId)
            || args.Any(arg => string.Equals(arg, "--quiet", StringComparison.OrdinalIgnoreCase));
        var rotate = args.Any(arg => string.Equals(arg, "--new", StringComparison.OrdinalIgnoreCase));
        try
        {
            // Run as SYSTEM by Intune, or by an administrator: like the service, it writes
            // nothing (the log, the pairing) into a folder another user made and has a hold on.
            // As the service at its start: a folder that is not there is made with its rule
            // and its anchor from the start, open to every user as a silent first install
            // leaves it. Made the usual way, by the log below, it would take ProgramData's
            // rule and be empty for a moment: a user waiting for that could make it a link
            // before the log's first line is written as SYSTEM. Only with an administrator's
            // rights, which setting the rule takes; run by a user, it is their own folder,
            // made as their app makes it.
            if (!Directory.Exists(AppPaths.ConfigDirectoryPath) && SystemService.CompanionServiceManager.IsAdministrator())
            {
                SettingsAccess.OpenToEveryone(AppPaths.ConfigDirectoryPath);
            }

            if (!SettingsAccess.IsOfThePc(AppPaths.ConfigDirectoryPath))
            {
                throw new IOException(string.Format(Strings.Get("Access.FolderNotOfThePc"), AppPaths.ConfigDirectoryPath));
            }

            using var log = new FileLog(Path.Combine(AppPaths.ConfigDirectoryPath, "hass-agent-net10.log"));
            log.Info($"Sendspin: the pairing token was asked for on the command line{(rotate ? ", a new one" : string.Empty)}.");
            var player = new SendspinPlayerService(new CompanionSettings(), log);
            var token = rotate ? player.RotatePairingToken() : player.GetPairingToken();

            Console.Out.WriteLine(token);
            Console.Out.Flush();
            if (!quiet)
            {
                Clipboard.SetText(token);
                MessageBox.Show(Strings.Get("Mp.SendspinTokenCopied"), AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.Flush();
            if (!quiet)
            {
                MessageBox.Show(string.Format(Strings.Get("Mp.SendspinTokenFailed"), ex.Message), AppIdentity.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            return 1;
        }
    }
}
