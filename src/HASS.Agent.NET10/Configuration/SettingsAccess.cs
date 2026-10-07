using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using HASS.Agent.Companion.Runtime;

namespace HASS.Agent.Companion.Configuration;

/// <summary>
/// Who may use the shared settings (ProgramData\HASS.Agent.NET10): their tokens and
/// commands. Either every user of the PC, as it always was, or the members of a local group,
/// "HASS.Agent Users", the way Docker Desktop has docker-users. The Windows service runs as
/// SYSTEM and administrators keep full access either way. Changing it needs an administrator.
/// </summary>
internal static class SettingsAccess
{
    public const string GroupName = "HASS.Agent Users";
    private const string GroupComment = "May use HASS.Agent .NET10 and read its settings.";

    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier AuthenticatedUsers = new(WellKnownSidType.AuthenticatedUserSid, null);
    private static readonly SecurityIdentifier BuiltinUsers = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);

    /// <summary>Whether access is limited to the group: no "every user" rule lets anyone else in.</summary>
    public static bool IsRestricted(string configDirectory)
    {
        try
        {
            var rules = new DirectoryInfo(configDirectory).GetAccessControl()
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>();
            return !rules.Any(rule => rule.AccessControlType == AccessControlType.Allow
                && rule.IdentityReference is SecurityIdentifier sid
                && (sid == AuthenticatedUsers || sid == BuiltinUsers || sid == Everyone)
                && (rule.FileSystemRights & (FileSystemRights.ReadData | FileSystemRights.WriteData)) != 0);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Whether the user running this may use the settings folder (there is none yet: yes).</summary>
    public static bool CurrentUserCanUse(string configDirectory)
    {
        try
        {
            if (!Directory.Exists(configDirectory))
            {
                return true;
            }

            _ = Directory.EnumerateFileSystemEntries(configDirectory).Any();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The enabled local accounts that can log on, by name.</summary>
    public static IReadOnlyList<string> LocalUsers()
    {
        var users = new List<string>();
        var resume = 0;
        int status;
        do
        {
            status = NetUserEnum(null, 1, FilterNormalAccount, out var buffer, MaxPreferredLength, out var read, out _, ref resume);
            if (status is not (0 or ErrorMoreData))
            {
                break;
            }

            try
            {
                var size = Marshal.SizeOf<UserInfo1>();
                for (var index = 0; index < read; index++)
                {
                    var info = Marshal.PtrToStructure<UserInfo1>(buffer + index * size);
                    if ((info.Flags & AccountDisabled) == 0 && !string.IsNullOrEmpty(info.Name))
                    {
                        users.Add(info.Name);
                    }
                }
            }
            finally
            {
                NetApiBufferFree(buffer);
            }
        }
        while (status == ErrorMoreData);

        return users.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The local users who are not administrators. While there are none, limiting the settings
    /// to the users of the PC takes nothing from anyone, so an update does it without asking.
    /// </summary>
    public static IReadOnlyList<string> NonAdministratorUsers()
    {
        // The group's name depends on the language of Windows ("Rendszergazdák"): looked up by its SID.
        var administratorsGroup = ((NTAccount)Administrators.Translate(typeof(NTAccount))).Value;
        var administrators = GroupMemberSids(administratorsGroup[(administratorsGroup.LastIndexOf('\\') + 1)..]);
        return LocalUsers().Where(user => TrySid(user) is not { } sid || !administrators.Contains(sid)).ToList();
    }

    private static HashSet<SecurityIdentifier> GroupMemberSids(string group)
    {
        var sids = new HashSet<SecurityIdentifier>();
        var resume = IntPtr.Zero;
        if (NetLocalGroupGetMembers(null, group, 0, out var buffer, MaxPreferredLength, out var read, out _, ref resume) != 0)
        {
            return sids;
        }

        try
        {
            var size = Marshal.SizeOf<LocalGroupMembersInfo0>();
            for (var index = 0; index < read; index++)
            {
                var info = Marshal.PtrToStructure<LocalGroupMembersInfo0>(buffer + index * size);
                sids.Add(new SecurityIdentifier(info.Sid));
            }
        }
        finally
        {
            NetApiBufferFree(buffer);
        }

        return sids;
    }

    private static SecurityIdentifier? TrySid(string user)
    {
        try
        {
            return Sid(user);
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
    }

    /// <summary>The members of the group, by name; empty when there is no group.</summary>
    public static IReadOnlySet<string> Members()
    {
        var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resume = IntPtr.Zero;
        if (NetLocalGroupGetMembers(null, GroupName, 3, out var buffer, MaxPreferredLength, out var read, out _, ref resume) != 0)
        {
            return members;
        }

        try
        {
            var size = Marshal.SizeOf<LocalGroupMembersInfo3>();
            for (var index = 0; index < read; index++)
            {
                var info = Marshal.PtrToStructure<LocalGroupMembersInfo3>(buffer + index * size);
                var name = info.DomainAndName ?? string.Empty;
                members.Add(name[(name.LastIndexOf('\\') + 1)..]);
            }
        }
        finally
        {
            NetApiBufferFree(buffer);
        }

        return members;
    }

    /// <summary>
    /// Limits the settings to these users and makes exactly them the members of the group.
    /// Needs an administrator. The users also get the folder by name: a new group membership
    /// only counts from the next logon, and the app of whoever asked for this runs now. The
    /// group is for later, for an administrator adding someone without this app.
    /// </summary>
    public static void RestrictTo(string configDirectory, IReadOnlyCollection<string> users)
    {
        var group = EnsureGroup();
        var wanted = users.Select(Sid).ToHashSet();
        var current = Members().Select(Sid).ToHashSet();
        foreach (var sid in wanted.Except(current))
        {
            ChangeMember(sid, add: true);
        }

        foreach (var sid in current.Except(wanted))
        {
            ChangeMember(sid, add: false);
        }

        SetFolderRule(configDirectory, wanted.Append(group));
    }

    /// <summary>Every user of the PC may use the settings again, as before.</summary>
    public static void OpenToEveryone(string configDirectory) => SetFolderRule(configDirectory, [AuthenticatedUsers]);

    /// <summary>
    /// After an update: a folder limited to some users stays as it is (files added later
    /// inherit its rule); one open to every user gets that rule again, as every install did.
    /// </summary>
    public static void KeepAsIs(string configDirectory)
    {
        if (IsRestricted(configDirectory))
        {
            return;
        }

        // Only administrators log on to this PC: limiting the settings to them changes nothing
        // for anyone today, and keeps out a user added later. Otherwise the choice is someone's
        // to make (the app asks when its window is opened, and Home Assistant is told).
        var users = LocalUsers();
        if (users.Count > 0 && NonAdministratorUsers().Count == 0)
        {
            RestrictTo(configDirectory, users);
        }
        else
        {
            OpenToEveryone(configDirectory);
        }
    }

    // Exactly this on the folder: SYSTEM and administrators full control, the given users
    // Modify, nothing inherited from ProgramData (which lets every user read). Then the files
    // under it lose any rule of their own and inherit only that.
    internal static void SetFolderRule(string configDirectory, IEnumerable<SecurityIdentifier> users)
    {
        Directory.CreateDirectory(configDirectory);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        foreach (var user in users.Distinct())
        {
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.Modify | FileSystemRights.Synchronize, inherit, PropagationFlags.None, AccessControlType.Allow));
        }

        new DirectoryInfo(configDirectory).SetAccessControl(security);
        if (Directory.EnumerateFileSystemEntries(configDirectory).Any())
        {
            // A file another process holds open still takes the new rule; one that cannot is
            // reported but does not undo what the folder already has.
            Icacls($"\"{Path.Combine(configDirectory, "*")}\" /reset /T /C", mustSucceed: false);
        }
    }

    private static void Icacls(string arguments, bool mustSucceed = true)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = SystemTools.InSystem32("icacls.exe"),
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("icacls.exe did not start.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (mustSucceed && process.ExitCode != 0)
        {
            throw new InvalidOperationException($"icacls {arguments} failed ({process.ExitCode}): {output.Trim()}");
        }
    }

    private static SecurityIdentifier EnsureGroup()
    {
        var info = new LocalGroupInfo1 { Name = GroupName, Comment = GroupComment };
        var status = NetLocalGroupAdd(null, 1, ref info, out _);
        if (status is not (0 or ErrorAliasExists or NerrGroupExists))
        {
            throw new InvalidOperationException($"The local group \"{GroupName}\" could not be created (error {status}).");
        }

        return TryGroupSid() ?? throw new InvalidOperationException($"The local group \"{GroupName}\" was not found after creating it.");
    }

    private static SecurityIdentifier? TryGroupSid()
    {
        try
        {
            return (SecurityIdentifier)new NTAccount(Environment.MachineName, GroupName).Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
    }

    private static SecurityIdentifier Sid(string user) =>
        (SecurityIdentifier)new NTAccount(user.Contains('\\') ? user : $"{Environment.MachineName}\\{user}").Translate(typeof(SecurityIdentifier));

    private static void ChangeMember(SecurityIdentifier sid, bool add)
    {
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        var sidPointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, sidPointer, bytes.Length);
            var member = new LocalGroupMembersInfo0 { Sid = sidPointer };
            var status = add
                ? NetLocalGroupAddMembers(null, GroupName, 0, ref member, 1)
                : NetLocalGroupDelMembers(null, GroupName, 0, ref member, 1);
            if (status is not (0 or ErrorMemberInAlias or ErrorMemberNotInAlias))
            {
                throw new InvalidOperationException($"{(add ? "Adding" : "Removing")} {sid} {(add ? "to" : "from")} \"{GroupName}\" failed (error {status}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(sidPointer);
        }
    }

    private const int FilterNormalAccount = 2;
    private const int MaxPreferredLength = -1;
    private const int ErrorMoreData = 234;
    private const int AccountDisabled = 0x0002;
    private const int ErrorAliasExists = 1379;
    private const int NerrGroupExists = 2223;
    private const int ErrorMemberInAlias = 1378;
    private const int ErrorMemberNotInAlias = 1377;

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserEnum(string? server, int level, int filter, out IntPtr buffer, int maxLength, out int entriesRead, out int totalEntries, ref int resumeHandle);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupGetMembers(string? server, string group, int level, out IntPtr buffer, int maxLength, out int entriesRead, out int totalEntries, ref IntPtr resumeHandle);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupAdd(string? server, int level, ref LocalGroupInfo1 info, out int errorIndex);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupAddMembers(string? server, string group, int level, ref LocalGroupMembersInfo0 member, int count);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupDelMembers(string? server, string group, int level, ref LocalGroupMembersInfo0 member, int count);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo1
    {
        public string Name;
        public string Password;
        public int PasswordAge;
        public int Privilege;
        public string HomeDirectory;
        public string Comment;
        public int Flags;
        public string ScriptPath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LocalGroupInfo1
    {
        public string Name;
        public string Comment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LocalGroupMembersInfo0
    {
        public IntPtr Sid;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LocalGroupMembersInfo3
    {
        public string DomainAndName;
    }
}
