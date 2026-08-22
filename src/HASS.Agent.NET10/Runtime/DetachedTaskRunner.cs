using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;
using HASS.Agent.Companion.Logging;

namespace HASS.Agent.Companion.Runtime;

/// <summary>
/// Creates and starts a one-shot scheduled task detached from this process tree.
///
/// The task is registered from an XML definition instead of plain schtasks switches,
/// because the switches offer no way to clear the scheduler's default battery
/// conditions ("start only on AC power", "stop when switching to battery") — which
/// left update tasks sitting "Queued" forever on notebooks running on battery.
/// The XML also sets DeleteExpiredTaskAfter with an end boundary, so the task removes
/// itself instead of piling up in Task Scheduler.
/// </summary>
internal static class DetachedTaskRunner
{
    public static bool RunOnce(string taskName, string commandPath, bool asSystem, FileLog log)
    {
        Delete(taskName, log);

        var now = DateTime.Now;
        // SYSTEM tasks authenticate by the well-known SID; user tasks run with the
        // interactive token of the logged-on user who created them.
        var principal = asSystem
            ? "      <UserId>S-1-5-18</UserId>\n      <RunLevel>HighestAvailable</RunLevel>"
            : "      <UserId>" + (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName) + "</UserId>\n" +
              "      <LogonType>InteractiveToken</LogonType>\n      <RunLevel>LeastPrivilege</RunLevel>";

        var xml =
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
            "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
            "  <RegistrationInfo>\n" +
            "    <Description>HASS.Agent .NET10 one-shot task. Created automatically; safe to delete.</Description>\n" +
            "  </RegistrationInfo>\n" +
            "  <Triggers>\n" +
            "    <TimeTrigger>\n" +
            $"      <StartBoundary>{now:yyyy-MM-ddTHH:mm:ss}</StartBoundary>\n" +
            $"      <EndBoundary>{now.AddDays(2):yyyy-MM-ddTHH:mm:ss}</EndBoundary>\n" +
            "      <Enabled>true</Enabled>\n" +
            "    </TimeTrigger>\n" +
            "  </Triggers>\n" +
            "  <Principals>\n" +
            "    <Principal id=\"Author\">\n" +
            principal + "\n" +
            "    </Principal>\n" +
            "  </Principals>\n" +
            "  <Settings>\n" +
            "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\n" +
            "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\n" +
            "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\n" +
            "    <AllowHardTerminate>true</AllowHardTerminate>\n" +
            "    <StartWhenAvailable>false</StartWhenAvailable>\n" +
            "    <AllowStartOnDemand>true</AllowStartOnDemand>\n" +
            "    <Enabled>true</Enabled>\n" +
            "    <Hidden>false</Hidden>\n" +
            "    <RunOnlyIfIdle>false</RunOnlyIfIdle>\n" +
            "    <WakeToRun>false</WakeToRun>\n" +
            "    <ExecutionTimeLimit>PT2H</ExecutionTimeLimit>\n" +
            "    <DeleteExpiredTaskAfter>PT0S</DeleteExpiredTaskAfter>\n" +
            "  </Settings>\n" +
            "  <Actions Context=\"Author\">\n" +
            "    <Exec>\n" +
            // XML-escaped: & or similar in the path (user-profile names can contain
            // them, and the batch lives under the user's temp) would break the XML.
            $"      <Command>\"{SecurityElement.Escape(commandPath)}\"</Command>\n" +
            "    </Exec>\n" +
            "  </Actions>\n" +
            "</Task>\n";

        var xmlPath = Path.Combine(
            Path.GetDirectoryName(commandPath) ?? Path.GetTempPath(),
            $"{taskName}.xml");

        try
        {
            // schtasks only accepts the XML when the file encoding matches its
            // declaration, so this must be written as UTF-16.
            File.WriteAllText(xmlPath, xml, Encoding.Unicode);

            if (!RunSchtasks($"/create /tn \"{taskName}\" /xml \"{xmlPath}\" /f", log))
            {
                return false;
            }

            return RunSchtasks($"/run /tn \"{taskName}\"", log);
        }
        catch (Exception ex)
        {
            log.Warning($"Detached task '{taskName}' failed: {ex.Message}");
            return false;
        }
        finally
        {
            try
            {
                File.Delete(xmlPath);
            }
            catch
            {
                // Best effort; a stray xml file in the update directory is harmless.
            }
        }
    }

    public static void Delete(string taskName, FileLog log)
    {
        RunSchtasks($"/delete /tn \"{taskName}\" /f", log, ignoreErrors: true);
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

            // On timeout the process is still running, so ExitCode would throw.
            if (!process.WaitForExit(10_000))
            {
                if (!ignoreErrors)
                {
                    log.Warning($"schtasks {arguments} timed out.");
                }

                return false;
            }

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
