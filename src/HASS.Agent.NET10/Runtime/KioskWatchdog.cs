using System.Security;
using System.Security.Principal;
using System.Text;
using HASS.Agent.Companion.Logging;

namespace HASS.Agent.Companion.Runtime;

/// <summary>
/// The scheduled task that keeps the tray app running for the kiosk users. It runs for the
/// kiosk group (KioskMode), in the session of each member who is logged on, and runs the app
/// with --kiosk-start, which starts the tray app when it is not running there yet. It runs at
/// logon; when the app writes that it closed, and when Windows writes that it crashed, so it
/// is back at once; and once a minute, for an app ended in Task Manager, which leaves no trace.
/// Created by the app run as administrator, so the users cannot change or delete it.
/// </summary>
internal static class KioskWatchdog
{
    public const string TaskName = @"HASS.Agent.NET10\Kiosk";

    public static bool Register(string executable, SecurityIdentifier kioskGroup, FileLog log)
    {
        var xmlPath = Path.Combine(Path.GetTempPath(), $"hass-agent-kiosk-{Guid.NewGuid():N}.xml");
        try
        {
            // schtasks only accepts the XML when the file encoding matches its declaration.
            File.WriteAllText(xmlPath, TaskXml(executable, kioskGroup, DateTime.Now), Encoding.Unicode);
            return DetachedTaskRunner.RunSchtasks($"/create /tn \"{TaskName}\" /xml \"{xmlPath}\" /f", log);
        }
        catch (Exception ex)
        {
            log.Warning($"Unable to create the kiosk task: {ex.Message}");
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
                // Best effort; a stray file in the temp folder is harmless.
            }
        }
    }

    public static void Remove(FileLog log) =>
        DetachedTaskRunner.RunSchtasks($"/delete /tn \"{TaskName}\" /f", log, ignoreErrors: true);

    /// <summary>What the app closing or crashing writes to the Application log.</summary>
    internal static string EventQuery(string executableFileName) =>
        "<QueryList><Query Id=\"0\" Path=\"Application\">" +
        $"<Select Path=\"Application\">*[System[Provider[@Name='{KioskEvents.Source}'] and EventID={KioskEvents.Closed}]]</Select>" +
        $"<Select Path=\"Application\">*[System[Provider[@Name='Application Error'] and EventID=1000]] and *[EventData[Data='{executableFileName}']]</Select>" +
        "</Query></QueryList>";

    internal static string TaskXml(string executable, SecurityIdentifier kioskGroup, DateTime now) =>
        "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
        "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
        "  <RegistrationInfo>\n" +
        "    <Description>HASS.Agent .NET10 kiosk mode: starts the tray app for the kiosk users when they log on, and again when it is closed. Removed when kiosk mode is turned off.</Description>\n" +
        "  </RegistrationInfo>\n" +
        "  <Triggers>\n" +
        "    <LogonTrigger>\n" +
        "      <Enabled>true</Enabled>\n" +
        "    </LogonTrigger>\n" +
        "    <EventTrigger>\n" +
        "      <Enabled>true</Enabled>\n" +
        $"      <Subscription>{SecurityElement.Escape(EventQuery(Path.GetFileName(executable)))}</Subscription>\n" +
        "    </EventTrigger>\n" +
        "    <TimeTrigger>\n" +
        "      <Repetition>\n" +
        "        <Interval>PT1M</Interval>\n" +
        "        <StopAtDurationEnd>false</StopAtDurationEnd>\n" +
        "      </Repetition>\n" +
        $"      <StartBoundary>{now:yyyy-MM-ddTHH:mm:ss}</StartBoundary>\n" +
        "      <Enabled>true</Enabled>\n" +
        "    </TimeTrigger>\n" +
        "  </Triggers>\n" +
        "  <Principals>\n" +
        "    <Principal id=\"Users\">\n" +
        $"      <GroupId>{kioskGroup.Value}</GroupId>\n" +
        "      <RunLevel>LeastPrivilege</RunLevel>\n" +
        "    </Principal>\n" +
        "  </Principals>\n" +
        "  <Settings>\n" +
        "    <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>\n" +
        "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\n" +
        "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\n" +
        "    <AllowHardTerminate>true</AllowHardTerminate>\n" +
        "    <StartWhenAvailable>false</StartWhenAvailable>\n" +
        "    <AllowStartOnDemand>true</AllowStartOnDemand>\n" +
        "    <Enabled>true</Enabled>\n" +
        "    <Hidden>false</Hidden>\n" +
        "    <RunOnlyIfIdle>false</RunOnlyIfIdle>\n" +
        "    <WakeToRun>false</WakeToRun>\n" +
        // --kiosk-start only starts the tray app and returns: the app is not the task's.
        "    <ExecutionTimeLimit>PT1M</ExecutionTimeLimit>\n" +
        "  </Settings>\n" +
        "  <Actions Context=\"Users\">\n" +
        "    <Exec>\n" +
        $"      <Command>\"{SecurityElement.Escape(executable)}\"</Command>\n" +
        "      <Arguments>--kiosk-start</Arguments>\n" +
        "    </Exec>\n" +
        "  </Actions>\n" +
        "</Task>\n";
}
