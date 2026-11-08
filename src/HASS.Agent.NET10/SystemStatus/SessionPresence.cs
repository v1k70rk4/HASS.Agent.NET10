using System.Diagnostics;
using HASS.Agent.Companion.Logging;

namespace HASS.Agent.Companion.SystemStatus;

/// <summary>
/// The mark a tray app leaves for the other apps of the PC: this session runs one that takes
/// part in the handover of the connection. A named event per session, held open for the app's
/// lifetime and gone with it, so the election (<see cref="SessionRole"/>) counts the apps that
/// may talk and not, say, one that only shows the note about the settings being limited to
/// other users. Open to the users who may use the settings, like the handover itself.
/// </summary>
internal sealed class SessionPresence : IDisposable
{
    private const string Prefix = @"Global\HASS.Agent.NET10.Tray.";
    private readonly EventWaitHandle? _mark;

    public SessionPresence(string configDirectory, FileLog log)
    {
        try
        {
            var session = (uint)Process.GetCurrentProcess().SessionId;
            _mark = EventWaitHandleAcl.Create(false, EventResetMode.ManualReset, Name(session), out _, HomeAssistantLink.EventSecurity(HomeAssistantLink.Participants(configDirectory)));
        }
        catch (Exception ex)
        {
            log.Warning($"Unable to mark this session as one that runs the app: {ex.Message}");
        }
    }

    internal static string Name(uint session) => $"{Prefix}{session}";

    /// <summary>Whether the session runs a tray app that takes part in the handover.</summary>
    public static bool Exists(uint session)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(Name(session), out var mark))
            {
                mark.Dispose();
                return true;
            }
        }
        catch (Exception)
        {
            // Made by someone who may not use the settings: not a mark of ours.
        }

        return false;
    }

    public void Dispose() => _mark?.Dispose();
}
