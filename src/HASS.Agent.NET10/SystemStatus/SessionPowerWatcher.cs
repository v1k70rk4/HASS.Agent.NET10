using Microsoft.Win32;

namespace HASS.Agent.Companion.SystemStatus;

/// <summary>
/// Watches interactive state that Windows reports via events rather than polling:
/// session lock/unlock/logon/logoff and AC/battery power-source changes.
/// Raises <see cref="StateChanged"/> so the MQTT/WS sensor loop can publish immediately.
/// </summary>
internal sealed class SessionPowerWatcher : IDisposable
{
    private bool _disposed;

    public SessionPowerWatcher()
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    /// <summary>Raised on lock/unlock, logon/logoff, or AC/battery change.</summary>
    public event EventHandler? StateChanged;

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        // Lock, unlock, logon, logoff, remote connect/disconnect — all presence-relevant.
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // StatusChange covers AC/battery source and charge-state changes.
        if (e.Mode == PowerModes.StatusChange)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }
}
