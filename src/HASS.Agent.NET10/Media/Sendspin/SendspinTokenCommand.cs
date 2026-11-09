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
/// <c>--new</c> makes a new one, as the page's other button. The exit code is 0 with a token and
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
        var quiet = args.Any(arg => string.Equals(arg, "--quiet", StringComparison.OrdinalIgnoreCase));
        var rotate = args.Any(arg => string.Equals(arg, "--new", StringComparison.OrdinalIgnoreCase));
        try
        {
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
