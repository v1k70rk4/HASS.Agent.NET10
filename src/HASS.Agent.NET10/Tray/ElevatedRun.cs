using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Forms;

namespace HASS.Agent.Companion.Tray;

/// <summary>How a run of this app as administrator ended, as far as the app that started it can tell.</summary>
internal enum ElevatedOutcome
{
    Finished,
    Cancelled,
    StillRunning,
    Failed,
}

/// <summary>
/// Runs this app as administrator (UAC) with the given arguments and waits for it. The wait is
/// long, since someone may take a while at the prompt or type an administrator's password, and
/// a run that has not ended by then is reported as such, not as a failure: it may still finish.
/// </summary>
internal static class ElevatedRun
{
    private const int ErrorCancelled = 1223;
    private static readonly TimeSpan Wait = TimeSpan.FromMinutes(5);

    public static ElevatedOutcome Start(string arguments, out string? error)
    {
        error = null;
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? Application.ExecutablePath,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
            });
            if (process is null)
            {
                return ElevatedOutcome.Failed;
            }

            return process.WaitForExit(Wait) ? ElevatedOutcome.Finished : ElevatedOutcome.StillRunning;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return ElevatedOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return ElevatedOutcome.Failed;
        }
    }
}
