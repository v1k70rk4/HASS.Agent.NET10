using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HASS.Agent.Companion.SystemStatus;

/// <summary>
/// Reads and sets the display brightness, in percent, with what Windows itself offers:
/// the built-in panel of a laptop through the LCD device, external monitors through
/// DDC/CI. A display that supports neither (many TVs, some docks and adapters) is left
/// alone; with no adjustable display at all there is simply no brightness.
/// Runs in the tray app: the service has no desktop and sees no monitors.
/// </summary>
internal static class DisplayBrightness
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;
    private const uint QueryDisplayBrightness = 0x230498;
    private const uint SetDisplayBrightness = 0x23049C;
    private const byte PolicyAc = 1;
    private const byte PolicyDc = 2;
    private static readonly object Gate = new();

    /// <summary>The brightness of the first adjustable display, or null when there is none.</summary>
    public static int? Get()
    {
        lock (Gate)
        {
            return GetPanel() ?? GetFirstMonitor();
        }
    }

    /// <summary>Sets every adjustable display. False when there was none to set.</summary>
    public static bool Set(int percent)
    {
        var value = Math.Clamp(percent, 0, 100);
        lock (Gate)
        {
            var panel = SetPanel(value);
            var monitors = SetMonitors(value);
            return panel || monitors;
        }
    }

    // --- The built-in panel -------------------------------------------------------------

    private static int? GetPanel()
    {
        using var lcd = CreateFile(@"\\.\LCD", GenericRead, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (lcd.IsInvalid)
        {
            return null;
        }

        // { policy, AC brightness, DC brightness }; the policy says which one applies now.
        var state = new byte[3];
        if (!DeviceIoControl(lcd, QueryDisplayBrightness, null, 0, state, (uint)state.Length, out var returned, IntPtr.Zero) || returned < 3)
        {
            return null;
        }

        return Math.Clamp((int)(state[0] == PolicyAc ? state[1] : state[2]), 0, 100);
    }

    private static bool SetPanel(int percent)
    {
        using var lcd = CreateFile(@"\\.\LCD", GenericRead | GenericWrite, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (lcd.IsInvalid)
        {
            return false;
        }

        // Both power sources, so the value does not jump back when the charger is (un)plugged.
        var state = new[] { (byte)(PolicyAc | PolicyDc), (byte)percent, (byte)percent };
        return DeviceIoControl(lcd, SetDisplayBrightness, state, (uint)state.Length, null, 0, out _, IntPtr.Zero);
    }

    // --- External monitors (DDC/CI) -----------------------------------------------------

    private static int? GetFirstMonitor()
    {
        int? result = null;
        ForEachPhysicalMonitor(handle =>
        {
            if (result is null && GetMonitorBrightness(handle, out var minimum, out var current, out var maximum) && maximum > minimum)
            {
                result = (int)Math.Round((current - minimum) * 100d / (maximum - minimum));
            }
        });
        return result;
    }

    private static bool SetMonitors(int percent)
    {
        var any = false;
        ForEachPhysicalMonitor(handle =>
        {
            // The range is the monitor's own; most use 0..100, not all.
            if (GetMonitorBrightness(handle, out var minimum, out _, out var maximum) && maximum > minimum)
            {
                var target = minimum + (uint)Math.Round((maximum - minimum) * percent / 100d);
                any |= SetMonitorBrightness(handle, target);
            }
        });
        return any;
    }

    private static void ForEachPhysicalMonitor(Action<IntPtr> action)
    {
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out var count) || count == 0)
            {
                return true;
            }

            var physical = new PhysicalMonitor[count];
            if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, physical))
            {
                return true;
            }

            try
            {
                foreach (var item in physical)
                {
                    action(item.Handle);
                }
            }
            finally
            {
                DestroyPhysicalMonitors(count, physical);
            }

            return true;
        }, IntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        public IntPtr Handle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr deviceContext, IntPtr rectangle, IntPtr data);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, uint inputSize, byte[]? output, uint outputSize, out uint returned, IntPtr overlapped);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr deviceContext, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PhysicalMonitor[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetMonitorBrightness(IntPtr monitor, out uint minimum, out uint current, out uint maximum);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetMonitorBrightness(IntPtr monitor, uint brightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitors(uint count, PhysicalMonitor[] monitors);
}
