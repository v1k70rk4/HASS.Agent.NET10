using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HASS.Agent.Companion.Security;

/// <summary>
/// Turns on privileges an administrator's token has but keeps off, for as long as the returned
/// object lives: taking ownership of a file someone else owns (and may have locked the
/// administrators out of) needs SeTakeOwnershipPrivilege, and making the administrators its
/// owner needs SeRestorePrivilege. A privilege the token does not have is skipped; the file
/// operation then fails on its own, with the usual access denied.
/// </summary>
internal sealed class TokenPrivileges : IDisposable
{
    public const string TakeOwnership = "SeTakeOwnershipPrivilege";
    public const string Restore = "SeRestorePrivilege";

    private readonly IntPtr _token;
    private readonly List<Luid> _enabled = [];

    private TokenPrivileges(IntPtr token) => _token = token;

    public static TokenPrivileges Enable(params string[] names)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var privileges = new TokenPrivileges(token);
        foreach (var name in names)
        {
            if (LookupPrivilegeValue(null, name, out var luid) && privileges.Set(luid, SePrivilegeEnabled))
            {
                privileges._enabled.Add(luid);
            }
        }

        return privileges;
    }

    public void Dispose()
    {
        foreach (var luid in _enabled)
        {
            _ = Set(luid, 0);
        }

        _ = CloseHandle(_token);
    }

    // AdjustTokenPrivileges "succeeds" for a privilege the token lacks; the last error says so.
    private bool Set(Luid luid, int attributes)
    {
        var state = new TokenPrivilege { Count = 1, Luid = luid, Attributes = attributes };
        return AdjustTokenPrivileges(_token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero)
            && Marshal.GetLastWin32Error() != ErrorNotAllAssigned;
    }

    private const int TokenAdjustPrivileges = 0x0020;
    private const int TokenQuery = 0x0008;
    private const int SePrivilegeEnabled = 0x0002;
    private const int ErrorNotAllAssigned = 1300;

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivilege
    {
        public int Count;
        public Luid Luid;
        public int Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivilege newState, int length, IntPtr previous, IntPtr returnLength);
}
