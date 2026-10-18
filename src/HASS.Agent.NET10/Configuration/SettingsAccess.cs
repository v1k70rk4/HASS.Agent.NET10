using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using HASS.Agent.Companion.Runtime;
using HASS.Agent.Companion.Security;
using Microsoft.Win32;

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

    /// <summary>
    /// Whether access is limited: no "every user" rule lets anyone else in. For the prompts and
    /// the notice only; an unreadable rule counts as open there, which at worst asks once more.
    /// </summary>
    public static bool IsRestricted(string configDirectory) => RestrictionState(configDirectory) ?? false;

    /// <summary>Limited (true), open to every user (false), or unknown when the rule cannot be read.</summary>
    internal static bool? RestrictionState(string configDirectory)
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
            return null;
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
    /// The users who have logged on to this PC with an account that is not a local one: from a
    /// domain, or from Entra ID (a work PC). They are known from their profiles, since the local
    /// account list does not have them, and whether they are administrators is not known either.
    /// Named as DOMAIN\user when Windows can name them, and by their SID when it cannot.
    /// </summary>
    public static IReadOnlyList<string> DirectoryUsersWithProfiles()
    {
        var users = new List<string>();
        using var profiles = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
        if (profiles is null)
        {
            return users;
        }

        foreach (var name in profiles.GetSubKeyNames())
        {
            if (!IsDirectoryAccount(name))
            {
                continue;
            }

            try
            {
                users.Add(new SecurityIdentifier(name).Translate(typeof(NTAccount)).Value);
            }
            catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
            {
                users.Add(name);
            }
        }

        return users.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// A domain account (S-1-5-21-…, with a domain other than this PC's) or an Entra ID one
    /// (S-1-12-1-…). A deleted local account's leftover profile is neither.
    /// </summary>
    internal static bool IsDirectoryAccount(string sid, SecurityIdentifier? machine = null)
    {
        // A profile key that only looks like a SID (ProfileList is system data, but not trusted
        // to be well formed) is nobody.
        SecurityIdentifier parsed;
        try
        {
            parsed = new SecurityIdentifier(sid);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (sid.StartsWith("S-1-12-1-", StringComparison.Ordinal))
        {
            return true;
        }

        if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal))
        {
            return false;
        }

        machine ??= MachineSid.Value;

        // The PC's own SID unknown: a domain account cannot be told from a local one, and an
        // update must not take an unknown one for a local administrator.
        return machine is null || parsed.AccountDomainSid != machine;
    }

    // The SID of the PC itself, the domain part of every local account's SID.
    private static readonly Lazy<SecurityIdentifier?> MachineSid = new(FindMachineSid);

    private static SecurityIdentifier? FindMachineSid()
    {
        // Asked of Windows itself: the account domain of the PC's security authority is the PC.
        // Looking up the PC's name never gives it, and a domain-joined PC often has no enabled
        // local account to take it from, which made every profile there a domain one.
        if (AccountDomainSid() is { } sid)
        {
            return sid;
        }

        if (string.Equals(Environment.UserDomainName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return CurrentUser.Value?.AccountDomainSid;
        }

        return LocalUsers().Select(TrySid).OfType<SecurityIdentifier>().FirstOrDefault()?.AccountDomainSid;
    }

    /// <summary>The PC's account domain SID, as its local security authority keeps it; null when it cannot be read.</summary>
    internal static SecurityIdentifier? AccountDomainSid()
    {
        var attributes = new LsaObjectAttributes { Length = Marshal.SizeOf<LsaObjectAttributes>() };
        if (LsaOpenPolicy(IntPtr.Zero, ref attributes, PolicyViewLocalInformation, out var policy) != 0)
        {
            return null;
        }

        try
        {
            if (LsaQueryInformationPolicy(policy, PolicyAccountDomainInformation, out var buffer) != 0 || buffer == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var info = Marshal.PtrToStructure<PolicyAccountDomainInfo>(buffer);
                return info.DomainSid == IntPtr.Zero ? null : new SecurityIdentifier(info.DomainSid);
            }
            finally
            {
                LsaFreeMemory(buffer);
            }
        }
        finally
        {
            LsaClose(policy);
        }
    }

    /// <summary>
    /// Everyone the settings should not be limited to without asking: the local users who are
    /// not administrators, and the users from a domain or Entra ID, whom the local list does
    /// not know. While there are none, limiting the settings to the users of the PC takes
    /// nothing from anyone, so an update does it without asking.
    /// </summary>
    public static IReadOnlyList<string> UsersNotKnownToBeAdministrators() =>
        NonAdministratorUsers().Concat(DirectoryUsersByRole().NotKnown).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The local users who are not administrators.</summary>
    public static IReadOnlyList<string> NonAdministratorUsers()
    {
        var (_, administrators) = AdministratorsGroup();
        return LocalUsers().Where(user => TrySid(user) is not { } sid || !administrators.Contains(sid)).ToList();
    }

    /// <summary>
    /// The users from a domain or Entra ID with a profile here, split by whether Windows shows
    /// them to be administrators of this PC: members of its Administrators group, by their own
    /// account or through a domain group in it (Domain Admins, say). The members of a domain
    /// group are known from a domain controller; with none answering, and for Entra ID groups,
    /// only the users in the group by their own account count.
    /// </summary>
    internal static (IReadOnlyList<string> Administrators, IReadOnlyList<string> NotKnown) DirectoryUsersByRole()
    {
        var (group, members) = AdministratorsGroup();
        var administrators = new List<string>();
        var notKnown = new List<string>();
        foreach (var user in DirectoryUsersWithProfiles())
        {
            (IsAdministratorHere(user, group, members) ? administrators : notKnown).Add(user);
        }

        return (administrators, notKnown);
    }

    internal static bool IsAdministratorHere(string user, string group, IReadOnlySet<SecurityIdentifier> members)
    {
        if (TrySid(user) is { } sid && members.Contains(sid))
        {
            return true;
        }

        // A user listed by SID has no name left to ask Windows about.
        return !user.StartsWith("S-1-", StringComparison.Ordinal)
            && LocalGroupsOf(user).Contains(group, StringComparer.OrdinalIgnoreCase);
    }

    // The Administrators group: its name depends on the language of Windows ("Rendszergazdák"),
    // so it is looked up by its SID.
    private static (string Name, HashSet<SecurityIdentifier> Members) AdministratorsGroup()
    {
        var name = ((NTAccount)Administrators.Translate(typeof(NTAccount))).Value;
        name = name[(name.LastIndexOf('\\') + 1)..];
        return (name, GroupMemberSids(name));
    }

    // The local groups the user is in, also through a domain group (the domain groups come from
    // a domain controller). Empty when Windows cannot tell.
    private static List<string> LocalGroupsOf(string user)
    {
        var groups = new List<string>();
        var status = NetUserGetLocalGroups(null, user, 0, LgIncludeIndirect, out var buffer, MaxPreferredLength, out var read, out _);
        try
        {
            if (status != 0)
            {
                return groups;
            }

            var size = Marshal.SizeOf<LocalGroupUsersInfo0>();
            for (var index = 0; index < read; index++)
            {
                var info = Marshal.PtrToStructure<LocalGroupUsersInfo0>(buffer + index * size);
                if (!string.IsNullOrEmpty(info.Name))
                {
                    groups.Add(info.Name);
                }
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                NetApiBufferFree(buffer);
            }
        }

        return groups;
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
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            // Not a name here, or a domain that cannot be asked right now.
            return null;
        }
    }

    /// <summary>The members of the group, as DOMAIN\name (the PC's name for a local account); empty when there is no group.</summary>
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
                if (!string.IsNullOrEmpty(info.DomainAndName))
                {
                    members.Add(info.DomainAndName);
                }
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
        var current = GroupMemberSids(GroupName);
        foreach (var sid in wanted.Except(current))
        {
            ChangeMember(sid, add: true);
        }

        // Only users left unticked are taken out: the local accounts and the domain or Entra ID
        // users with a profile, which is the list this was chosen from. Someone an administrator
        // added by other means (another group, an account that never logged on here) is not in
        // that list, so it stays a member.
        // A directory user whose name no longer resolves is listed by SID, and by that SID
        // goes out as well.
        var listed = LocalUsers().Concat(DirectoryUsersWithProfiles())
            .Select(TrySid).OfType<SecurityIdentifier>().ToHashSet();
        foreach (var sid in current.Except(wanted).Where(listed.Contains))
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
        if (Directory.Exists(configDirectory))
        {
            MakeSafe(configDirectory);
        }

        var state = RestrictionState(configDirectory);
        if (state is true)
        {
            // Limited: the same users, in the rule's current shape (one from before 10.9.1-beta.5
            // also let them delete or replace the folder itself).
            var limitedTo = ExplicitUsers(configDirectory);
            if (limitedTo.Count > 0)
            {
                SetFolderRule(configDirectory, limitedTo);
            }

            return;
        }

        if (state is null)
        {
            // The rule could not be read: an update never opens a folder it is not sure about.
            return;
        }

        // The administrators from a domain are named too: an administrator's app runs without
        // elevation, where the Administrators group does not count, so the folder lets each of
        // them in by name, as the local ones.
        var (directoryAdministrators, notKnown) = DirectoryUsersByRole();
        var users = LocalUsers().Concat(directoryAdministrators).ToList();
        switch (OnUpdate(restricted: false, users.Count, NonAdministratorUsers().Count, notKnown.Count))
        {
            case UpdateAction.LimitToTheUsers:
                RestrictTo(configDirectory, users);
                break;
            case UpdateAction.OpenToEveryone:
                OpenToEveryone(configDirectory);
                break;
        }
    }

    internal enum UpdateAction
    {
        LeaveAsItIs,
        LimitToTheUsers,
        OpenToEveryone,
    }

    /// <summary>
    /// What an update does with the folder. A limited one stays as it is. One open to every
    /// user on a PC whose users are all administrators (the local ones, and those from a domain
    /// shown to be) is limited to them: it changes nothing for anyone today and keeps out a user
    /// added later. Otherwise, and on a PC where someone from a domain or Entra ID has logged on
    /// who is not known to be an administrator, the choice is someone's to make (the app asks
    /// when its window is opened, and Home Assistant is told), and the folder gets the rule
    /// every install had.
    /// </summary>
    internal static UpdateAction OnUpdate(bool restricted, int users, int nonAdministrators, int directoryUsersNotKnown) =>
        restricted ? UpdateAction.LeaveAsItIs
        : users > 0 && nonAdministrators == 0 && directoryUsersNotKnown == 0 ? UpdateAction.LimitToTheUsers
        : UpdateAction.OpenToEveryone;

    // Exactly this on the folder: SYSTEM and administrators full control, nothing inherited
    // from ProgramData (which lets every user read), and the given users Modify on what is in
    // it, but on the folder itself only list, read and create: they cannot delete, rename or
    // replace the folder the service writes into. Then the files under it lose any rule of
    // their own and inherit only that.
    internal static void SetFolderRule(string configDirectory, IEnumerable<SecurityIdentifier> users)
    {
        var notTakenOver = MakeSafe(configDirectory);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        foreach (var user in users.Distinct())
        {
            security.AddAccessRule(new FileSystemAccessRule(user, UsersOnTheFolder, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.Modify | FileSystemRights.Synchronize, inherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
        }

        var rule = security.GetSecurityDescriptorBinaryForm();
        using var folder = OpenTheFolderItself(configDirectory);
        if (!SetKernelObjectSecurity(folder, DaclSecurityInformation, rule))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        // With the restore privilege a file whose own rule would keep even an administrator
        // out (one made so on purpose) still opens for its rule to be set. What still cannot
        // be done is reported at the end, after everything else has been: the folder's rule
        // stands, but the caller must not take the limiting for complete.
        var failures = new List<string>(notTakenOver);
        using (TokenPrivileges.Enable(TokenPrivileges.Backup, TokenPrivileges.Restore))
        {
            ResetContents(configDirectory, new RawSecurityDescriptor(rule, 0).DiscretionaryAcl!, failures);
        }

        if (failures.Count > 0)
        {
            throw new IOException($"The rule is set on the folder, but not on everything in it: {string.Join("; ", failures)}");
        }
    }

    /// <summary>
    /// What is in the folder loses any rule of its own and gets the folder's, inherited, entry
    /// by entry through a handle opened on the entry itself. A link made since MakeSafe is
    /// removed, not followed; so is a file that has another name elsewhere (a hard link), since
    /// a rule set on it here would be the rule of that other file too, which is what
    /// icacls /reset did until 10.9.3. A subfolder is held open, with no one allowed to delete
    /// or rename it, for as long as its contents are being done: its path cannot be swapped for
    /// a junction in between, the way the folder itself is held by SetFolderRule. A file
    /// another process holds open still takes the rule; one that cannot be opened or changed
    /// is left as it is, named among the failures, and the rest is still done.
    /// </summary>
    private static void ResetContents(string directory, RawAcl folderRule, List<string> failures)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            var isFolder = entry is DirectoryInfo;
            var inherited = InheritedFrom(folderRule, isFolder);
            Microsoft.Win32.SafeHandles.SafeFileHandle? held;
            try
            {
                if (!TryResetEntry(entry.FullName, isFolder, inherited, out held))
                {
                    entry.Delete();
                    continue;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                failures.Add($"{entry.FullName}: {ex.Message}");
                continue;
            }

            using (held)
            {
                if (isFolder)
                {
                    try
                    {
                        ResetContents(entry.FullName, inherited, failures);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
                    {
                        // What is in it keeps what it has; the folder's own rule stands, and
                        // the entries after it are still done.
                        failures.Add($"{entry.FullName}: {ex.Message}");
                    }
                }
            }
        }
    }

    /// <summary>
    /// False when the entry is a link or a hard link: to be removed, not changed. A folder's
    /// handle is handed back open, to be held while its contents are done.
    /// </summary>
    private static bool TryResetEntry(string path, bool isFolder, RawAcl inherited, out Microsoft.Win32.SafeHandles.SafeFileHandle? held)
    {
        held = null;
        var share = isFolder ? FileShareRead | FileShareWrite : FileShareRead | FileShareWrite | FileShareDelete;
        var handle = CreateFile(path, ReadControl | WriteDac, share, IntPtr.Zero,
            OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"{path} could not be opened");
        }

        if (!GetFileInformationByHandle(handle, out var information))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }

        if ((information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0 || (!isFolder && information.NumberOfLinks > 1))
        {
            handle.Dispose();
            return false;
        }

        // Not protected, so the folder's rule keeps flowing to it later; what the kernel is
        // handed is the rule as inherited, since it propagates nothing on its own.
        var descriptor = new CommonSecurityDescriptor(
            isFolder, false, ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclAutoInherited,
            null, null, null, new DiscretionaryAcl(isFolder, false, inherited));
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        if (!SetKernelObjectSecurity(handle, DaclSecurityInformation, bytes))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"{path} did not take the rule");
        }

        if (isFolder)
        {
            held = handle;
        }
        else
        {
            handle.Dispose();
        }

        return true;
    }

    /// <summary>The rule a folder passes on to a folder or a file in it, as Windows would inherit it.</summary>
    internal static RawAcl InheritedFrom(RawAcl folderRule, bool toFolder)
    {
        var acl = new RawAcl(folderRule.Revision, folderRule.Count);
        foreach (var ace in folderRule.OfType<CommonAce>())
        {
            var flags = ace.AceFlags;
            var toFolders = flags.HasFlag(AceFlags.ContainerInherit);
            var toFiles = flags.HasFlag(AceFlags.ObjectInherit);
            AceFlags inherited;
            if (toFolder)
            {
                if (!toFolders && !toFiles)
                {
                    continue;
                }

                // Applies to the folder when meant for folders; passed on unless told not to.
                inherited = AceFlags.Inherited;
                if (!flags.HasFlag(AceFlags.NoPropagateInherit))
                {
                    inherited |= flags & (AceFlags.ContainerInherit | AceFlags.ObjectInherit);
                }

                if (!toFolders)
                {
                    inherited |= AceFlags.InheritOnly;
                }
            }
            else
            {
                if (!toFiles)
                {
                    continue;
                }

                inherited = AceFlags.Inherited;
            }

            acl.InsertAce(acl.Count, new CommonAce(inherited, ace.AceQualifier, ace.AccessMask, ace.SecurityIdentifier, ace.IsCallback, ace.GetOpaque()));
        }

        return acl;
    }

    /// <summary>
    /// The folder itself, opened so that what is checked is what gets the rule. Opened as it
    /// is, not followed if it is a link (and refused if it became one since MakeSafe), and
    /// without letting anyone delete or rename it while the handle is open: under the rule of
    /// an earlier version its users could do both, and swap in a link to a folder of their
    /// choosing between the check and the write, which would then get the rule.
    /// </summary>
    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenTheFolderItself(string configDirectory)
    {
        for (var attempt = 0; ; attempt++)
        {
            var handle = CreateFile(configDirectory, ReadControl | WriteDac, FileShareRead | FileShareWrite, IntPtr.Zero,
                OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error, $"{configDirectory} could not be opened");
            }

            if (!GetFileInformationByHandle(handle, out var information))
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }

            if ((information.FileAttributes & (uint)FileAttributes.ReparsePoint) == 0)
            {
                return handle;
            }

            handle.Dispose();
            if (attempt == 2)
            {
                throw new IOException($"{configDirectory} keeps being replaced with a link; its rule was not set.");
            }

            MakeSafe(configDirectory);
        }
    }

    /// <summary>What the users get on the settings folder itself: list, read, create files and folders.</summary>
    internal const FileSystemRights UsersOnTheFolder =
        FileSystemRights.ReadAndExecute | FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories | FileSystemRights.Synchronize;

    /// <summary>
    /// Before an administrator sets the folder's rule: it must be a real folder, owned by
    /// SYSTEM, the administrators or whoever is setting the rule, with no links in it.
    /// ProgramData lets every user create a folder, and the creator owns it, so another user
    /// could have made this one before the first install (and kept the right to change its rule
    /// later), or made it a junction to a folder of their choosing, whose rule the administrator
    /// would then set. A link, here or under it, is removed (the link only, not where it
    /// points); what someone else owns is taken over by the administrators.
    /// </summary>
    /// <returns>The entries that could not be looked at or taken over, for the caller to report.</returns>
    internal static IReadOnlyList<string> MakeSafe(string configDirectory)
    {
        var folder = new DirectoryInfo(configDirectory);
        if (folder.Exists && folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            folder.Delete();
        }

        Directory.CreateDirectory(configDirectory);
        var failures = new List<string>();
        // Backup for the read-only opens past any rule, take-ownership for the owner, restore
        // for the rule later: what an administrator has, and a test run as the owner does without.
        using (TokenPrivileges.Enable(TokenPrivileges.Backup, TokenPrivileges.TakeOwnership, TokenPrivileges.Restore))
        {
            // The folder itself first, held open while its contents are done; one that became
            // a link in the meantime is left to OpenTheFolderItself, which comes back here.
            using var root = OpenEntryItself(configDirectory, isFolder: true, ReadControl);
            if (root is not null)
            {
                TakeOver(root, isFolder: true, configDirectory);
                TakeOverContents(configDirectory, failures);
            }
        }

        return failures;
    }

    // Entry by entry through a handle on the entry itself, like ResetContents: a link (a
    // junction, a symbolic link) is not followed and a hard link, a file under another name
    // elsewhere whose owner is that file's, is not touched; both go, the name only. A folder
    // is held open, with no one allowed to delete or rename it, while what is in it is done.
    // One entry that cannot be looked at or taken over is named among the failures and the
    // rest is still done; SetFolderRule reports them with its own.
    private static void TakeOverContents(string directory, List<string> failures)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            var isFolder = entry is DirectoryInfo;
            try
            {
                using var handle = OpenEntryItself(entry.FullName, isFolder, ReadControl);
                if (handle is null)
                {
                    entry.Delete();
                    continue;
                }

                TakeOver(handle, isFolder, entry.FullName);
                if (isFolder)
                {
                    TakeOverContents(entry.FullName, failures);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                failures.Add($"{entry.FullName}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The entry under the name, opened as it is, or null (and nothing open) when it is a link
    /// or a hard link. A folder is opened without delete sharing, to be held while its
    /// contents are done.
    /// </summary>
    private static Microsoft.Win32.SafeHandles.SafeFileHandle? OpenEntryItself(string path, bool isFolder, uint access)
    {
        var share = isFolder ? FileShareRead | FileShareWrite : FileShareRead | FileShareWrite | FileShareDelete;
        var handle = CreateFile(path, access, share, IntPtr.Zero,
            OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"{path} could not be opened");
        }

        if (!GetFileInformationByHandle(handle, out var information))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error);
        }

        if ((information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0 || (!isFolder && information.NumberOfLinks > 1))
        {
            handle.Dispose();
            return null;
        }

        return handle;
    }

    // Owner only, through the handle: the rule itself is set afterwards, for the folder and
    // (inherited) for the rest.
    private static void TakeOver(Microsoft.Win32.SafeHandles.SafeFileHandle handle, bool isFolder, string path)
    {
        if (OwnerIsTrusted(OwnerOf(handle)))
        {
            return;
        }

        // The right to set the owner is asked for only now, when it is needed: an administrator
        // has it by the take-ownership privilege, and a folder of the users' own rule, which
        // grants no such right, still opens for the check above. The first handle stays open,
        // so a folder cannot be swapped in between; a file that became a link is refused.
        using var owner = OpenEntryItself(path, isFolder, ReadControl | WriteOwner)
            ?? throw new IOException($"{path} became a link while its owner was being set.");
        var descriptor = new RawSecurityDescriptor(ControlFlags.None, Administrators, null, null, null);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        if (!SetKernelObjectSecurity(owner, OwnerSecurityInformation, bytes))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"{path} could not be taken over");
        }
    }

    private static SecurityIdentifier? OwnerOf(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        GetKernelObjectSecurity(handle, OwnerSecurityInformation, null, 0, out var needed);
        var buffer = new byte[Math.Max(needed, 1u)];
        if (!GetKernelObjectSecurity(handle, OwnerSecurityInformation, buffer, (uint)buffer.Length, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return new RawSecurityDescriptor(buffer, 0).Owner;
    }

    /// <summary>Whether the file has more than one name (a hard link); the handle is opened on the name itself.</summary>
    internal static bool HasOtherNames(string path)
    {
        using var handle = CreateFile(path, FileReadAttributes, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero,
            OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"{path} could not be opened");
        }

        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return information.NumberOfLinks > 1;
    }

    private static bool OwnerIsTrusted(FileSystemSecurity security) =>
        OwnerIsTrusted(security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier);

    private static bool OwnerIsTrusted(SecurityIdentifier? owner) =>
        owner is not null && (owner == LocalSystem || owner == Administrators || owner == CurrentUser.Value);

    private static readonly Lazy<SecurityIdentifier?> CurrentUser = new(() => WindowsIdentity.GetCurrent().User);

    /// <summary>The users an explicit rule on the folder lets in, besides SYSTEM and the administrators.</summary>
    private static IReadOnlyList<SecurityIdentifier> ExplicitUsers(string configDirectory) =>
        new DirectoryInfo(configDirectory).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow)
            .Select(rule => rule.IdentityReference).OfType<SecurityIdentifier>()
            .Where(sid => sid != LocalSystem && sid != Administrators)
            .Distinct()
            .ToList();

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

    // A name (bare for a local account, DOMAIN\user otherwise) or a SID as text, which is how
    // a directory user whose name no longer resolves is listed. A name that does not resolve
    // throws: a user asked for on the command line must not be dropped in silence, and
    // RestrictTo resolves every name before it changes anything.
    private static SecurityIdentifier Sid(string user) =>
        user.StartsWith("S-1-", StringComparison.Ordinal)
            ? new SecurityIdentifier(user)
            : (SecurityIdentifier)new NTAccount(user.Contains('\\') ? user : $"{Environment.MachineName}\\{user}").Translate(typeof(SecurityIdentifier));

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
    private const int LgIncludeIndirect = 0x1;
    private const int PolicyViewLocalInformation = 0x1;
    private const int PolicyAccountDomainInformation = 5;

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetLocalGroups(string? server, string user, int level, int flags, out IntPtr buffer, int maxLength, out int entriesRead, out int totalEntries);

    [DllImport("advapi32.dll")]
    private static extern int LsaOpenPolicy(IntPtr system, ref LsaObjectAttributes attributes, int access, out IntPtr policy);

    [DllImport("advapi32.dll")]
    private static extern int LsaQueryInformationPolicy(IntPtr policy, int informationClass, out IntPtr buffer);

    [DllImport("advapi32.dll")]
    private static extern int LsaFreeMemory(IntPtr buffer);

    [DllImport("advapi32.dll")]
    private static extern int LsaClose(IntPtr policy);

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

    private const uint FileReadAttributes = 0x00000080;
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint WriteOwner = 0x00080000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint FileShareDelete = 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint OwnerSecurityInformation = 0x1;
    private const uint DaclSecurityInformation = 0x4;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
        string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out ByHandleFileInformation information);

    // The descriptor carries the "protected" flag set by SetAccessRuleProtection; nothing is
    // propagated to what is in the folder here, ResetContents does that afterwards.
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetKernelObjectSecurity(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint information, byte[] descriptor);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetKernelObjectSecurity(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint information, byte[]? descriptor, uint length, out uint needed);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LocalGroupUsersInfo0
    {
        public string Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public int Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PolicyAccountDomainInfo
    {
        public LsaUnicodeString DomainName;
        public IntPtr DomainSid;
    }
}
