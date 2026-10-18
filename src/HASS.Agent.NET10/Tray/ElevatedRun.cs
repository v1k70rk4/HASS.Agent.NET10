using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Forms;

namespace HASS.Agent.Companion.Tray;

/// <summary>How a run of this app as administrator ended, as far as the app that started it can tell.</summary>
internal enum ElevatedOutcome
{
    Finished,
    Cancelled,
    /// <summary>Started, and not over by the end of the wait: it may still finish.</summary>
    StillRunning,
    /// <summary>Not started: an earlier run is still going.</summary>
    Busy,
    /// <summary>Not started: the prompt or the start itself failed, so no run of its own has said anything.</summary>
    NotStarted,
    /// <summary>Started and ended, but not as asked: the run has shown or logged why.</summary>
    Failed,
}

/// <summary>
/// Runs this app as administrator (UAC) with the given arguments and waits for it without
/// blocking the window that asked: the wait is awaited, so its UI keeps working. The wait is
/// long, since someone may take a while at the prompt or type an administrator's password, and
/// a run that has not ended by then is reported as such, not as a failure: it may still finish.
/// Only one run at a time: two administrator runs must never change the same folder or file in
/// an order nobody chose.
/// </summary>
internal static class ElevatedRun
{
    private const int ErrorCancelled = 1223;
    private static readonly TimeSpan Wait = TimeSpan.FromMinutes(5);

    // 1 from the moment a run is reserved until it is known to have ended (or never started).
    private static int _busy;

    // The run in progress, kept past the wait until it has ended.
    private static Process? _running;

    public static async Task<(ElevatedOutcome Outcome, string? Error)> StartAsync(string arguments)
    {
        if (_running is { } earlier && HasEnded(earlier))
        {
            earlier.Dispose();
            _running = null;
            Interlocked.Exchange(ref _busy, 0);
        }

        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return (ElevatedOutcome.Busy, null);
        }

        Process? process;
        try
        {
            // Starting it shows the UAC prompt and returns once it is answered.
            process = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? Application.ExecutablePath,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            Interlocked.Exchange(ref _busy, 0);
            return (ElevatedOutcome.Cancelled, null);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _busy, 0);
            return (ElevatedOutcome.NotStarted, ex.Message);
        }

        if (process is null)
        {
            Interlocked.Exchange(ref _busy, 0);
            return (ElevatedOutcome.NotStarted, "the administrator run did not start");
        }

        _running = process;
        using var timeout = new CancellationTokenSource(Wait);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // Still reserved: the next call checks whether it has ended by then.
            return (ElevatedOutcome.StillRunning, null);
        }

        // Finished means done as asked: the control commands exit with 1 when they did not do
        // what was asked (a rule that could not be set on every file, say). A code that cannot
        // be read is not taken for 0.
        int? exitCode;
        try
        {
            exitCode = process.ExitCode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            exitCode = null;
        }

        _running = null;
        process.Dispose();
        Interlocked.Exchange(ref _busy, 0);
        return exitCode switch
        {
            0 => (ElevatedOutcome.Finished, null),
            null => (ElevatedOutcome.Failed, "the result of the administrator run could not be read; the log says what it did"),
            _ => (ElevatedOutcome.Failed, $"the administrator run ended with code {exitCode}; the log says what went wrong"),
        };
    }

    // A zero wait on the handle the start gave us, which can always wait on the process: it says
    // whether the process has ended without asking for its exit code, which an elevated process
    // may not give an ordinary one.
    private static bool HasEnded(Process process) => process.WaitForExit(0);
}
