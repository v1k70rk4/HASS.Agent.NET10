using System.Diagnostics;
using HASS.Agent.Companion.Logging;

namespace HASS.Agent.Companion.Runtime;

/// <summary>
/// Kiosk mode in the Windows event log (Application), where an administrator looks, and where
/// the kiosk task listens: the app closed in a kiosk session (or crashed, which Windows writes
/// there itself) starts it again at once. The source is registered by the app run as
/// administrator, when kiosk mode is turned on; after that any user's app can write to it.
/// </summary>
internal static class KioskEvents
{
    public const string Source = AppIdentity.ExecutableName;

    public const int TurnedOn = 100;
    public const int TurnedOff = 101;
    public const int Started = 102;
    public const int CameBack = 103;
    public const int Closed = 104;

    // Every Windows has it, and it gives each event ID the text it was written with. Without
    // a message file the Event Viewer shows each entry as "the description cannot be found".
    private static string MessageFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Microsoft.NET\Framework64\v4.0.30319\EventLogMessages.dll");

    /// <summary>Registers the source. Needs an administrator.</summary>
    public static void Register()
    {
        if (EventLog.SourceExists(Source))
        {
            return;
        }

        EventLog.CreateEventSource(new EventSourceCreationData(Source, "Application") { MessageResourceFile = MessageFile });
    }

    /// <summary>Writes the event; the app's own log says so when it cannot (the source not registered yet).</summary>
    public static void Write(int id, string message, FileLog log, EventLogEntryType type = EventLogEntryType.Information)
    {
        try
        {
            EventLog.WriteEntry(Source, message, type, id);
        }
        catch (Exception ex)
        {
            log.Warning($"Unable to write to the Windows event log ({message}): {ex.Message}");
        }
    }
}
