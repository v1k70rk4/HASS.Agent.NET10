using System.Diagnostics;
using HASS.Agent.Companion.Logging;

namespace HASS.Agent.Companion.Runtime;

/// <summary>
/// Launches the update installer detached from the tray app's process tree.
///
/// The installer closes the running app with <c>taskkill /IM HASS.Agent.NET10.exe /T /F</c>
/// (see the installer script). The <c>/T</c> kills the whole process tree, so if the
/// installer were started as a direct child of the tray app it would kill itself when it
/// closes the app. Running it from a one-shot scheduled task (via a small batch) gives it
/// no parent link to the tray app, so it survives — exactly like the service-role and
/// relaunch-watchdog paths already do.
///
/// The installer runs its normal (visible) wizard — not silently — so the user can see
/// what is happening, and it relaunches the app itself when it finishes (its own
/// post-install step, which runs because we do not pass /SILENTUPDATE).
/// </summary>
internal static class DetachedUpdateLauncher
{
    private const string TaskName = "HASSAgentNet10AppUpdate";

    public static bool TryLaunchInstaller(string installerPath, FileLog log)
    {
        try
        {
            var directory = Path.GetDirectoryName(installerPath);
            if (string.IsNullOrEmpty(directory))
            {
                return false;
            }

            // A tiny batch launches the installer detached; `start` returns immediately so
            // the brief console window closes on its own and the installer's wizard shows.
            var batchPath = Path.Combine(directory, "hassagent-update.cmd");
            File.WriteAllText(
                batchPath,
                "@echo off" + Environment.NewLine +
                $"start \"\" \"{installerPath}\"" + Environment.NewLine);

            RunSchtasks($"/delete /tn \"{TaskName}\" /f", log, ignoreErrors: true);
            // /z deletes the task after it runs (it needs an end boundary, hence /et) so
            // one-shot update tasks do not accumulate in Task Scheduler.
            if (!RunSchtasks($"/create /tn \"{TaskName}\" /tr \"\\\"{batchPath}\\\"\" /sc once /st 00:00 /et 23:59 /z /f", log))
            {
                return false;
            }

            if (!RunSchtasks($"/run /tn \"{TaskName}\"", log))
            {
                return false;
            }

            log.Info("Update installer launched via detached scheduled task.");
            return true;
        }
        catch (Exception ex)
        {
            log.Warning($"Detached installer launch failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Removes one-shot update tasks left behind by earlier versions. They served their
    /// purpose long before the app is running again, and leaving them in Task Scheduler
    /// invites users to run them by hand — which is not something they are built for.
    /// </summary>
    public static void CleanUpLeftoverTasks(FileLog log)
    {
        foreach (var name in new[] { TaskName, "HASSAgentNet10Update", "HASSAgentNet10Relaunch" })
        {
            RunSchtasks($"/delete /tn \"{name}\" /f", log, ignoreErrors: true);
        }
    }

    private static bool RunSchtasks(string arguments, FileLog log, bool ignoreErrors = false)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);
            if (process.ExitCode != 0)
            {
                if (!ignoreErrors)
                {
                    log.Warning($"schtasks {arguments} exited with code {process.ExitCode}: {process.StandardError.ReadToEnd().Trim()}");
                }

                return false;
            }

            return true;
        }
        catch (Exception ex) when (ignoreErrors)
        {
            log.Debug($"schtasks {arguments} failed (ignored): {ex.Message}");
            return false;
        }
    }
}
