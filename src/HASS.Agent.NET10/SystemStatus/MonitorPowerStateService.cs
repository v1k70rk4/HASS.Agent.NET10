using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace HASS.Agent.Companion.SystemStatus;

internal sealed class MonitorPowerStateService : NativeWindow, IDisposable
{
    private const uint DeviceNotifyWindowHandle = 0x0;
    private const int WmPowerBroadcast = 0x0218;
    private const int PbtPowerSettingChange = 0x8013;
    private static readonly Guid ConsoleDisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");

    private IntPtr _registrationHandle;

    public MonitorPowerStateService()
    {
        CreateHandle(new CreateParams());
        _registrationHandle = RegisterPowerSettingNotification(Handle, ConsoleDisplayState, DeviceNotifyWindowHandle);
    }

    /// <summary>Raised when the monitor power state actually changes (on/off/dimmed) — drives push updates.</summary>
    public event EventHandler? StateChanged;

    public string State { get; private set; } = "unknown";

    public void Dispose()
    {
        if (_registrationHandle != IntPtr.Zero)
        {
            _ = UnregisterPowerSettingNotification(_registrationHandle);
            _registrationHandle = IntPtr.Zero;
        }

        DestroyHandle();
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmPowerBroadcast && message.WParam == (IntPtr)PbtPowerSettingChange)
        {
            var settings = Marshal.PtrToStructure<PowerBroadcastSetting>(message.LParam);
            if (settings.PowerSetting == ConsoleDisplayState)
            {
                var newState = settings.Data switch
                {
                    0 => "off",
                    1 => "on",
                    2 => "dimmed",
                    _ => "unknown"
                };

                if (newState != State)
                {
                    State = newState;
                    StateChanged?.Invoke(this, EventArgs.Empty);
                }

                message.Result = (IntPtr)1;
                return;
            }
        }

        base.WndProc(ref message);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr handle, [In] Guid powerSettingGuid, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PowerBroadcastSetting
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data;
    }
}
