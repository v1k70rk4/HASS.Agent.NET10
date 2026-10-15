using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HASS.Agent.Companion.SystemStatus;

/// <summary>One installed display adapter, with the LUID its performance counters carry.</summary>
internal sealed record GpuAdapterIdentity(string Name, long? MemoryMb, string? Luid);

/// <summary>
/// The installed display adapters, in the order of the display class in the registry (the
/// order gpu_usage.adapters[N] has always had), each matched by name to a DXGI adapter for
/// its LUID: the GPU performance counters name adapters by LUID only.
/// </summary>
internal static class GpuAdapterList
{
    private const string DisplayAdapterClassPath = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const uint DxgiAdapterFlagSoftware = 2;
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);

    public static IReadOnlyList<GpuAdapterIdentity> Read(Func<string, string> limit)
    {
        var dxgi = ReadDxgiAdapters();
        var used = new HashSet<int>();
        var adapters = new List<GpuAdapterIdentity>();
        foreach (var (name, memoryMb) in ReadRegistryAdapters())
        {
            // Two cards of the same model take the DXGI adapters of that name in turn.
            var match = dxgi.FindIndex(adapter =>
                !used.Contains(adapter.Index) &&
                string.Equals(adapter.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
            string? luid = null;
            long? dxgiMemoryMb = null;
            if (match >= 0)
            {
                used.Add(dxgi[match].Index);
                luid = dxgi[match].Luid;
                dxgiMemoryMb = dxgi[match].MemoryMb;
            }

            adapters.Add(new GpuAdapterIdentity(limit(name), memoryMb ?? dxgiMemoryMb, luid));
        }

        return adapters;
    }

    /// <summary>The LUID as the GPU counters write it: luid_0x{high}_0x{low}.</summary>
    internal static string LuidKey(int high, uint low) => $"0x{(uint)high:X8}_0x{low:X8}";

    private static List<(string Name, long? MemoryMb)> ReadRegistryAdapters()
    {
        var adapters = new List<(string, long?)>();
        using var displayClass = Registry.LocalMachine.OpenSubKey(DisplayAdapterClassPath);
        foreach (var keyName in displayClass?.GetSubKeyNames() ?? [])
        {
            // 0000, 0001, ... are the adapters; the rest ("Properties") is not readable anyway.
            if (keyName.Length != 4 || !keyName.All(char.IsAsciiDigit))
            {
                continue;
            }

            using var adapter = displayClass!.OpenSubKey(keyName);
            if (adapter?.GetValue("DriverDesc") is not string { Length: > 0 } name)
            {
                continue;
            }

            // The real size: WMI's AdapterRAM is a 32-bit field and stops at 4 GB.
            var memoryMb = adapter.GetValue("HardwareInformation.qwMemorySize") is long bytes and > 0
                ? bytes / (1024 * 1024)
                : (long?)null;
            adapters.Add((name, memoryMb));
        }

        return adapters;
    }

    private static List<(int Index, string Name, string Luid, long? MemoryMb)> ReadDxgiAdapters()
    {
        var adapters = new List<(int, string, string, long?)>();
        var factoryId = typeof(IDXGIFactory1).GUID;
        if (CreateDXGIFactory1(ref factoryId, out var factoryPointer) < 0 || factoryPointer == IntPtr.Zero)
        {
            return adapters;
        }

        var factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(factoryPointer);
        Marshal.Release(factoryPointer);
        try
        {
            for (uint index = 0; ; index++)
            {
                var hr = factory.EnumAdapters1(index, out var adapter);
                if (hr == DxgiErrorNotFound || hr < 0 || adapter is null)
                {
                    break;
                }

                try
                {
                    if (adapter.GetDesc1(out var desc) < 0 || (desc.Flags & DxgiAdapterFlagSoftware) != 0)
                    {
                        continue;
                    }

                    var memoryMb = desc.DedicatedVideoMemory > 0 ? (long)(desc.DedicatedVideoMemory / (1024 * 1024)) : (long?)null;
                    adapters.Add(((int)index, desc.Description ?? string.Empty, LuidKey(desc.LuidHighPart, desc.LuidLowPart), memoryMb));
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }

        return adapters;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLowPart;
        public int LuidHighPart;
        public uint Flags;
    }

    // dxgi.h. Only the slots that are called have real signatures; the others keep the vtable order.
    [ComImport]
    [Guid("770aae78-f26f-4dba-a829-253c83d1b387")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        [PreserveSig] int SetPrivateData();
        [PreserveSig] int SetPrivateDataInterface();
        [PreserveSig] int GetPrivateData();
        [PreserveSig] int GetParent();
        [PreserveSig] int EnumAdapters();
        [PreserveSig] int MakeWindowAssociation();
        [PreserveSig] int GetWindowAssociation();
        [PreserveSig] int CreateSwapChain();
        [PreserveSig] int CreateSoftwareAdapter();
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1? adapter);
        [PreserveSig] int IsCurrent();
    }

    [ComImport]
    [Guid("29038f61-3839-4626-91fd-086879011a05")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        [PreserveSig] int SetPrivateData();
        [PreserveSig] int SetPrivateDataInterface();
        [PreserveSig] int GetPrivateData();
        [PreserveSig] int GetParent();
        [PreserveSig] int EnumOutputs();
        [PreserveSig] int GetDesc();
        [PreserveSig] int CheckInterfaceSupport();
        [PreserveSig] int GetDesc1(out DxgiAdapterDesc1 desc);
    }
}
