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
    // Whoever owns the file or folder the rule is on.
    private static readonly SecurityIdentifier OwnerRights = new(WellKnownSidType.WinCreatorOwnerRightsSid, null);

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

        if (string.Equals(Environment.UserDomainName, MachineName, StringComparison.OrdinalIgnoreCase))
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
    /// The users known to be administrators of this PC: the local ones, and those from a domain
    /// or Entra ID shown to be. The Kiosk mode window marks them, since kiosk mode cannot keep
    /// an administrator out, only hide the app from them.
    /// </summary>
    public static IReadOnlyList<string> AdministratorUsers()
    {
        var (_, administrators) = AdministratorsGroup();
        return LocalUsers().Where(user => TrySid(user) is { } sid && administrators.Contains(sid))
            .Concat(DirectoryUsersByRole().Administrators)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
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
    private static List<string> LocalGroupsOf(string user) => LocalGroupsOf(user, out _);

    // The same, saying whether Windows could tell (known): an empty list is then no groups.
    private static List<string> LocalGroupsOf(string user, out bool known)
    {
        var groups = new List<string>();
        var status = NetUserGetLocalGroups(null, user, 0, LgIncludeIndirect, out var buffer, MaxPreferredLength, out var read, out _);
        known = status == 0;
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
        var group = EnsureGroup(GroupName, GroupComment);
        var wanted = users.Select(Sid).ToHashSet();
        SetMembers(GroupName, wanted);
        SetFolderRule(configDirectory, wanted.Append(group), KioskMode.GroupSid());
    }

    /// <summary>
    /// Makes exactly these the members of the group. Only users left unticked are taken out: the
    /// local accounts and the domain or Entra ID users with a profile, which is the list this was
    /// chosen from. Someone an administrator added by other means (another group, an account
    /// that never logged on here) is not in that list, so it stays a member. A directory user
    /// whose name no longer resolves is listed by SID, and by that SID goes out as well.
    /// </summary>
    private static void SetMembers(string group, IReadOnlySet<SecurityIdentifier> wanted)
    {
        var current = GroupMemberSids(group);
        foreach (var sid in wanted.Except(current))
        {
            ChangeMember(group, sid, add: true);
        }

        var listed = LocalUsers().Concat(DirectoryUsersWithProfiles())
            .Select(TrySid).OfType<SecurityIdentifier>().ToHashSet();
        foreach (var sid in current.Except(wanted).Where(listed.Contains))
        {
            ChangeMember(group, sid, add: false);
        }
    }

    /// <summary>Every user of the PC may use the settings again, as before.</summary>
    public static void OpenToEveryone(string configDirectory) => SetFolderRule(configDirectory, [AuthenticatedUsers], KioskMode.GroupSid());

    /// <summary>
    /// Kiosk mode for these users, the first step: checks them and makes the kiosk group if it
    /// is not there yet, without changing who is in it. The members come last
    /// (<see cref="SetKioskMembers"/>), once the folder rule and the task are in place. Needs an
    /// administrator. An administrator may be one: the group's deny on the settings folder holds
    /// for them too, and they can undo all of it, which the window says before it is done.
    /// </summary>
    public static (SecurityIdentifier Group, IReadOnlySet<SecurityIdentifier> Users) PrepareKioskGroup(IReadOnlyCollection<string> users)
    {
        var wanted = users.Select(Sid).ToHashSet();
        return (EnsureGroup(KioskMode.GroupName, KioskMode.GroupComment), wanted);
    }

    /// <summary>Makes exactly these the members of the kiosk group, as <see cref="SetMembers"/> does.</summary>
    public static void SetKioskMembers(IReadOnlySet<SecurityIdentifier> users) => SetMembers(KioskMode.GroupName, users);

    /// <summary>Kiosk mode off: the kiosk group goes, with its members. Needs an administrator.</summary>
    public static void RemoveKioskGroup()
    {
        var status = NetLocalGroupDel(null, KioskMode.GroupName);
        if (status is not (0 or NerrGroupNotFound or ErrorNoSuchAlias))
        {
            throw new InvalidOperationException($"The local group \"{KioskMode.GroupName}\" could not be removed (error {status}).");
        }
    }

    /// <summary>The members of the kiosk group, by name, the way the lists name users; empty when there is no group.</summary>
    public static IReadOnlySet<string> KioskUsers() =>
        GroupMemberSids(KioskMode.GroupName).Select(NameOf).ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Bare for a local account, DOMAIN\user otherwise, the SID when Windows cannot name it.
    private static string NameOf(SecurityIdentifier sid)
    {
        try
        {
            var name = ((NTAccount)sid.Translate(typeof(NTAccount))).Value;
            var machine = MachineName + "\\";
            return name.StartsWith(machine, StringComparison.OrdinalIgnoreCase) ? name[machine.Length..] : name;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            return sid.Value;
        }
    }

    /// <summary>
    /// Kiosk mode turned on (the kiosk group) or off (null) on the settings folder: who may use
    /// the settings stays the same, and the kiosk group is denied changing them. Needs an
    /// administrator.
    /// </summary>
    public static void ApplyKiosk(string configDirectory, SecurityIdentifier? kioskGroup)
    {
        if (Directory.Exists(configDirectory))
        {
            MakeSafe(configDirectory);
        }

        var users = RestrictionState(configDirectory) switch
        {
            true => ExplicitUsers(configDirectory),
            false => ExplicitUsers(configDirectory).Append(AuthenticatedUsers).Distinct().ToList(),
            // Nobody knows who the folder is for: it is left as it is, and the caller is told.
            null => throw new InvalidOperationException($"The rule of {configDirectory} could not be read; it was not changed."),
        };
        SetFolderRule(configDirectory, users, kioskGroup);
    }

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
                SetFolderRule(configDirectory, limitedTo, KioskMode.GroupSid());
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
    // In kiosk mode (kioskGroup given) the kiosk group is denied, on the folder and on what is
    // in it, everything that would change something there: the kiosk users only list and read,
    // whatever else lets them in. A deny goes before every allow.
    internal static void SetFolderRule(string configDirectory, IEnumerable<SecurityIdentifier> users, SecurityIdentifier? kioskGroup = null)
    {
        // The anchor is held open until the rest is done, so nothing can remove it meanwhile.
        var notTakenOver = MakeSafe(configDirectory, out var anchor, out var anchorName, out _, out _);
        try
        {
            SetFolderRule(configDirectory, users, kioskGroup, notTakenOver, ref anchor, anchorName);
        }
        finally
        {
            anchor?.Dispose();
        }
    }

    private static void SetFolderRule(
        string configDirectory, IEnumerable<SecurityIdentifier> users, SecurityIdentifier? kioskGroup,
        IReadOnlyList<string> notTakenOver, ref Microsoft.Win32.SafeHandles.SafeFileHandle? anchor, string? anchorName)
    {
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

        var ruleWithoutDeny = security.GetSecurityDescriptorBinaryForm();
        if (kioskGroup is not null)
        {
            security.AddAccessRule(new FileSystemAccessRule(kioskGroup, KioskDenied, inherit, PropagationFlags.None, AccessControlType.Deny));
        }

        var rule = security.GetSecurityDescriptorBinaryForm();
        using var folder = OpenTheFolderItself(configDirectory);
        if (anchor is null)
        {
            // MakeSafe could not make one: whoever sets the rule is kept out of the folder by
            // the deny of its rule so far, and has no restore privilege to go past it (a test,
            // run as the folder's owner; an administrator has the privilege). Made under the
            // new rule without the deny, then.
            SetRule(folder, ruleWithoutDeny);
            anchor = CreateAnchor(folder, out var made);
            anchorName = made;
        }

        SetRule(folder, rule);
        if (IsLink(folder))
        {
            throw new IOException($"{configDirectory} was replaced with a link while its rule was being set.");
        }

        // With the restore privilege a file whose own rule would keep even an administrator
        // out (one made so on purpose) still opens for its rule to be set. What still cannot
        // be done is reported at the end, after everything else has been: the folder's rule
        // stands, but the caller must not take the limiting for complete.
        var failures = new List<string>(notTakenOver);
        using (TokenPrivileges.Enable(TokenPrivileges.Backup, TokenPrivileges.Restore))
        using (var contents = OpenToList(folder, configDirectory))
        {
            ResetContents(contents, configDirectory, new RawSecurityDescriptor(rule, 0).DiscretionaryAcl!, failures, anchorName);
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
    /// icacls /reset did until 10.9.3.
    /// Nothing in the folder is reached by its path. A path is looked up again from the top
    /// each time it is used, through whatever stands under each of its names at that moment,
    /// and the users may rename a subfolder of theirs and put a link under its name while what
    /// was in it is being done: the names listed before would then be opened where the link
    /// points, and files there, anywhere on the PC, would get the settings' rule. A handle
    /// stays on what it was opened on. So a folder's names are listed through its handle
    /// (<see cref="NamesIn"/>), each is opened under that handle (<see cref="OpenIn"/>), and
    /// what is done is done through the handle that was checked.
    /// A file another process holds open still takes the rule; one that cannot be opened or
    /// changed is left as it is, named among the failures, and the rest is still done.
    /// The folder's anchor (<paramref name="anchor"/>, in the settings folder itself) keeps the
    /// rule it was made with; the anchors of earlier runs go, or become ordinary files.
    /// </summary>
    private static void ResetContents(
        Microsoft.Win32.SafeHandles.SafeFileHandle folder, string path, RawAcl folderRule, List<string> failures, string? anchor = null)
    {
        foreach (var (name, isFolder) in NamesIn(folder, path))
        {
            var entryPath = Path.Combine(path, name);
            if (anchor is not null && !isFolder && IsAnchorName(name)
                && (string.Equals(name, anchor, StringComparison.OrdinalIgnoreCase) || TryDeleteIn(folder, name, isFolder, entryPath)))
            {
                continue;
            }

            try
            {
                using var entry = OpenIn(folder, name, isFolder, ReadControl | WriteDac | (isFolder ? FileListDirectory : 0), entryPath);
                if (IsLinkOrHasOtherNames(entry, isFolder))
                {
                    entry.Dispose();
                    DeleteIn(folder, name, isFolder, entryPath);
                    continue;
                }

                // Not protected, so the folder's rule keeps flowing to it later; what the kernel
                // is handed is the rule as inherited, since it propagates nothing on its own.
                var inherited = InheritedFrom(folderRule, isFolder);
                var descriptor = new CommonSecurityDescriptor(
                    isFolder, false, ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclAutoInherited,
                    null, null, null, new DiscretionaryAcl(isFolder, false, inherited));
                var bytes = new byte[descriptor.BinaryLength];
                descriptor.GetBinaryForm(bytes, 0);
                if (!SetKernelObjectSecurity(entry, DaclSecurityInformation, bytes))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"{entryPath} did not take the rule");
                }

                if (isFolder)
                {
                    // What cannot be listed keeps what it has; the folder's own rule stands,
                    // and the entries after it are still done.
                    ResetContents(entry, entryPath, inherited, failures);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                failures.Add($"{entryPath}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The names in a folder and whether each is a folder, read through the folder's handle
    /// (opened to list it). What a name is, is only what the list said: whoever opens it asks
    /// for a folder or for a file accordingly, and is refused if it has become the other.
    /// </summary>
    private static List<(string Name, bool IsFolder)> NamesIn(Microsoft.Win32.SafeHandles.SafeFileHandle folder, string path)
    {
        const int size = 64 * 1024;
        var names = new List<(string, bool)>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var kind = FileFullDirectoryRestartInfo;
            while (GetFileInformationByHandleEx(folder, kind, buffer, size))
            {
                kind = FileFullDirectoryInfo;
                for (var offset = 0; ;)
                {
                    // FILE_FULL_DIR_INFO: the next entry's offset, then at 56 the attributes,
                    // at 60 the name's length in bytes, at 68 the name.
                    var next = Marshal.ReadInt32(buffer, offset);
                    var attributes = (FileAttributes)Marshal.ReadInt32(buffer, offset + 56);
                    var name = Marshal.PtrToStringUni(buffer + offset + 68, Marshal.ReadInt32(buffer, offset + 60) / 2);
                    if (name is not ("." or ".."))
                    {
                        names.Add((name, attributes.HasFlag(FileAttributes.Directory)));
                    }

                    if (next == 0)
                    {
                        break;
                    }

                    offset += next;
                }
            }

            var error = Marshal.GetLastWin32Error();
            return error == ErrorNoMoreFiles ? names : throw new Win32Exception(error, $"{path} could not be listed");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// What is under the name in the open folder, opened as it is: a link is not followed, and
    /// the name is looked up in that folder, wherever the folder is by now. Shared with
    /// everyone, so nothing is held up by it.
    /// </summary>
    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenIn(
        Microsoft.Win32.SafeHandles.SafeFileHandle folder, string name, bool isFolder, uint access, string path)
    {
        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var nameString = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        var addedRef = false;
        try
        {
            Marshal.StructureToPtr(new UnicodeString { Length = (ushort)(name.Length * 2), MaximumLength = (ushort)(name.Length * 2 + 2), Buffer = nameBuffer }, nameString, false);
            folder.DangerousAddRef(ref addedRef);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = folder.DangerousGetHandle(),
                ObjectName = nameString,
                Attributes = ObjCaseInsensitive,
            };
            var status = NtCreateFile(out var handle, access | FileReadAttributes | Synchronize, ref attributes, out _, IntPtr.Zero,
                0, FileShareRead | FileShareWrite | FileShareDelete, FileOpen,
                (isFolder ? FileDirectoryFile : FileNonDirectoryFile) | FileSynchronousIoNonAlert | FileOpenReparsePointOption | FileOpenForBackupIntent,
                IntPtr.Zero, 0);
            if (status != 0)
            {
                handle.Dispose();
                throw new Win32Exception(RtlNtStatusToDosError(status), $"{path} could not be opened");
            }

            return handle;
        }
        finally
        {
            if (addedRef)
            {
                folder.DangerousRelease();
            }

            Marshal.FreeHGlobal(nameString);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    /// <summary>A link (a junction, a symbolic link), or a file with another name elsewhere: to be removed, not changed.</summary>
    private static bool IsLinkOrHasOtherNames(Microsoft.Win32.SafeHandles.SafeFileHandle entry, bool isFolder) =>
        GetFileInformationByHandle(entry, out var information)
            ? (information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0 || (!isFolder && information.NumberOfLinks > 1)
            : throw new Win32Exception(Marshal.GetLastWin32Error());

    /// <summary>
    /// Removes the link, or the file's name here, under the name in the open folder: the name
    /// only, through a handle on it that was looked at again, never what a link points to.
    /// </summary>
    private static void DeleteIn(Microsoft.Win32.SafeHandles.SafeFileHandle folder, string name, bool isFolder, string path)
    {
        using var entry = OpenIn(folder, name, isFolder, DeleteAccess, path);
        if (isFolder && !IsLinkOrHasOtherNames(entry, isFolder))
        {
            throw new IOException($"{path} is a folder again; it was not removed.");
        }

        byte delete = 1;
        if (!SetFileInformationByHandle(entry, FileDispositionInfo, ref delete, 1))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"{path} could not be removed");
        }
    }

    private static bool TryDeleteIn(Microsoft.Win32.SafeHandles.SafeFileHandle folder, string name, bool isFolder, string path)
    {
        try
        {
            DeleteIn(folder, name, isFolder, path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The same folder as <paramref name="folder"/> is open on, opened to list what is in it.
    /// Opened by its path and then compared, since a handle cannot be given more rights: it is
    /// the folder that was checked, or nothing is listed.
    /// </summary>
    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenToList(Microsoft.Win32.SafeHandles.SafeFileHandle folder, string path)
    {
        var list = CreateFile(path, ReadControl | FileListDirectory, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero,
            OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (list.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            list.Dispose();
            throw new Win32Exception(error, $"{path} could not be opened to list it");
        }

        try
        {
            if (!IsSameFile(folder, list) || IsLink(list))
            {
                throw new IOException($"{path} was replaced while it was being made safe.");
            }
        }
        catch
        {
            list.Dispose();
            throw;
        }

        return list;
    }

    /// <summary>Whether the name is one <see cref="CreateAnchor"/> gives: folder-(random).anchor.</summary>
    internal static bool IsAnchorName(string name) =>
        name.StartsWith(AnchorPrefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(AnchorSuffix, StringComparison.OrdinalIgnoreCase);

    private const string AnchorPrefix = "folder-";
    private const string AnchorSuffix = ".anchor";

    /// <summary>
    /// The folder's anchor: an empty file in it that its users cannot delete, rename or change.
    /// The users may create files in the folder and delete what is in it, and that is all it
    /// takes to turn an empty folder into a link to a folder of their choosing: every write of
    /// the service and of an administrator would then go there. Windows makes no link of a
    /// folder with something in it, so with the anchor there it stays a folder.
    /// A new one at every call, under a name of its own: it is this call's by the way it was
    /// made, whatever a user put there under such a name before. Made through the folder's
    /// handle, with its rule from the start: SYSTEM, the administrators and its owner (one of
    /// those two, or a test's own user) and nobody else, nothing inherited.
    /// </summary>
    internal static Microsoft.Win32.SafeHandles.SafeFileHandle CreateAnchor(Microsoft.Win32.SafeHandles.SafeFileHandle folder, out string name)
    {
        name = $"{AnchorPrefix}{Guid.NewGuid():N}"[..(AnchorPrefix.Length + 12)] + AnchorSuffix;

        var acl = new RawAcl(GenericAcl.AclRevision, 3);
        foreach (var sid in new[] { LocalSystem, Administrators, new SecurityIdentifier(WellKnownSidType.WinCreatorOwnerRightsSid, null) })
        {
            acl.InsertAce(acl.Count, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, (int)FileSystemRights.FullControl, sid, false, null));
        }

        var descriptor = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected, null, null, null, acl);
        var descriptorBytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(descriptorBytes, 0);

        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var nameString = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        var pinnedDescriptor = GCHandle.Alloc(descriptorBytes, GCHandleType.Pinned);
        var addedRef = false;
        try
        {
            Marshal.StructureToPtr(new UnicodeString { Length = (ushort)(name.Length * 2), MaximumLength = (ushort)(name.Length * 2 + 2), Buffer = nameBuffer }, nameString, false);
            folder.DangerousAddRef(ref addedRef);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = folder.DangerousGetHandle(),
                ObjectName = nameString,
                Attributes = ObjCaseInsensitive,
                SecurityDescriptor = pinnedDescriptor.AddrOfPinnedObject(),
            };
            var status = NtCreateFile(out var handle, ReadControl | FileReadAttributes | Synchronize, ref attributes, out _, IntPtr.Zero,
                (uint)FileAttributes.Normal, FileShareRead, FileCreate,
                FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePointOption | FileOpenForBackupIntent, IntPtr.Zero, 0);
            if (status != 0)
            {
                handle.Dispose();
                throw new Win32Exception(RtlNtStatusToDosError(status), "The folder's anchor could not be made");
            }

            return handle;
        }
        finally
        {
            if (addedRef)
            {
                folder.DangerousRelease();
            }

            pinnedDescriptor.Free();
            Marshal.FreeHGlobal(nameString);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    // Whoever makes the folder safe may be kept out of it by a deny (they are in the kiosk
    // group themselves). An administrator's restore privilege goes past that; without the
    // privilege (a test, run as the folder's owner) there is no anchor yet, and SetFolderRule
    // makes it once the rule lets it.
    private static Microsoft.Win32.SafeHandles.SafeFileHandle? TryCreateAnchor(
        Microsoft.Win32.SafeHandles.SafeFileHandle folder, out string? name)
    {
        try
        {
            var anchor = CreateAnchor(folder, out var made);
            name = made;
            return anchor;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorAccessDenied)
        {
            name = null;
            return null;
        }
    }

    // The settings folder, when it is not there, made with its rule from the start: SYSTEM,
    // the administrators and whoever makes it. Made the usual way it would take ProgramData's
    // rule, under which every user may create files in it, and be empty: a user waiting for
    // that moment could turn it into a link before anything else is done with it.
    /// <returns>Whether it was made here: false when a folder was there already.</returns>
    private static bool CreateForAdministrators(string configDirectory)
    {
        var folder = new DirectoryInfo(configDirectory);
        if (folder.Exists)
        {
            return false;
        }

        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(OwnerRights, FileSystemRights.FullControl, AccessControlType.Allow));
        folder.Create(security);
        return true;
    }

    private static void SetRule(Microsoft.Win32.SafeHandles.SafeFileHandle folder, byte[] rule)
    {
        if (!SetKernelObjectSecurity(folder, DaclSecurityInformation, rule))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    /// <summary>Whether what the handle is open on is a link now.</summary>
    private static bool IsLink(Microsoft.Win32.SafeHandles.SafeFileHandle handle) =>
        GetFileInformationByHandle(handle, out var information)
            ? (information.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0
            : throw new Win32Exception(Marshal.GetLastWin32Error());

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
    /// is, not followed if it is a link (and refused if it became one since MakeSafe). Under
    /// the rule of an earlier version its users could delete or rename it, and swap in a link
    /// to a folder of their choosing between the check and the write: the rule is set through
    /// this handle, on the folder that was checked, and MakeSafe has taken that right away.
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
    /// What the kiosk group is denied on the settings folder and on what is in it: creating,
    /// writing and deleting anything. Their files are not theirs (MakeSafe takes over what a
    /// user owns), so they cannot change a rule either.
    /// </summary>
    internal const FileSystemRights KioskDenied =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles;

    /// <summary>
    /// Before an administrator sets the folder's rule: it must be a real folder, owned by
    /// SYSTEM, the administrators or whoever is setting the rule, with no links in it.
    /// ProgramData lets every user create a folder, and the creator owns it, so another user
    /// could have made this one before the first install (and kept the right to change its rule
    /// later), or made it a junction to a folder of their choosing, whose rule the administrator
    /// would then set. A link, here or under it, is removed (the link only, not where it
    /// points); a folder someone else owns is put aside and a new one made
    /// (<see cref="PutAside"/>); what someone else owns in a folder of ours is taken over by
    /// the administrators.
    /// </summary>
    /// <remarks>
    /// When this returns, the folder is a folder and stays one: it holds its anchor
    /// (<see cref="CreateAnchor"/>), made through the folder's own handle before anything in it
    /// is looked at by its path. What an administrator then reads and writes there by path, the
    /// log first of all, is in this folder and not where a link put in its place would point.
    /// </remarks>
    /// <returns>The entries that could not be looked at or taken over, for the caller to report.</returns>
    internal static IReadOnlyList<string> MakeSafe(string configDirectory) => MakeSafe(configDirectory, out _);

    /// <summary>
    /// <see cref="MakeSafe(string)"/>, and where the folder that was there went when it was
    /// someone else's (<see cref="PutAside"/>); null when it was not.
    /// </summary>
    internal static IReadOnlyList<string> MakeSafe(string configDirectory, out string? putAside)
    {
        var failures = MakeSafe(configDirectory, out var anchor, out _, out putAside, out _);
        anchor?.Dispose();
        return failures;
    }

    /// <summary>
    /// What an elevated command does before it reads or writes anything in the settings folder:
    /// <see cref="MakeSafe(string)"/>, and a folder nobody chose a rule for is opened to every
    /// user, as a silent first install leaves it, so the app asks who it is for. That is a
    /// folder made here, when there was none or what was there was a link; the folder in the
    /// place of another user's, which was put aside; and one that still has the rule it was
    /// made with (<see cref="IsAsItWasMade"/>), left so before by a command that was to set
    /// the rule and failed, or by 10.13.0, which did not open it. A folder made here is the
    /// administrators' alone: left so, the app would start for nobody, and an update would
    /// keep it that way, taking the one rule it has for a limit someone chose. Not when the
    /// command sets the folder's rule itself (<paramref name="setsTheFoldersRule"/>).
    /// </summary>
    /// <param name="putAside">Where another user's folder went; null when there was none.</param>
    /// <param name="made">Whether the folder is a new one, made here.</param>
    /// <returns>Whether the folder was opened to every user.</returns>
    internal static bool MakeSafeForCommand(string configDirectory, bool setsTheFoldersRule, out string? putAside, out bool made)
    {
        // The anchor is held open until the rule is set, as SetFolderRule does.
        var notTakenOver = MakeSafe(configDirectory, out var anchor, out var anchorName, out putAside, out made);
        try
        {
            if (setsTheFoldersRule || (!made && putAside is null && !IsAsItWasMade(configDirectory)))
            {
                // What could not be taken over in the folder is the caller's to find, as
                // before: a command that sets the rule reports it, the others go on.
                return false;
            }

            SetFolderRule(configDirectory, [AuthenticatedUsers], KioskMode.GroupSid(), notTakenOver, ref anchor, anchorName);
            return true;
        }
        finally
        {
            anchor?.Dispose();
        }
    }

    /// <summary>
    /// Whether the folder's rule is still the one a new folder is made with
    /// (<see cref="CreateForAdministrators"/>): it names nobody but SYSTEM, the administrators
    /// and the folder's owner. No choice leaves that: a limit names its users or their group.
    /// It is also what "--settings-access keep" of 10.13.0 made of such a folder, keeping the
    /// owner's entry as if it were a user's. A rule that names nobody at all is not this:
    /// someone may have set it by hand, and it stays. (A folder made by a command started
    /// from a shell that makes the user the owner of what it creates, as Git Bash does, is
    /// given to the administrators, and Windows drops the owner's entry when the owner
    /// changes: it is opened by the command that made it, and not found here later.)
    /// </summary>
    internal static bool IsAsItWasMade(string configDirectory)
    {
        try
        {
            if (RestrictionState(configDirectory) is not true)
            {
                return false;
            }

            var users = ExplicitUsers(configDirectory);
            return users.Count == 1 && users[0] == OwnerRights;
        }
        catch (Exception)
        {
            // Not to be read: not taken for a folder nobody chose a rule for.
            return false;
        }
    }

    private static IReadOnlyList<string> MakeSafe(
        string configDirectory, out Microsoft.Win32.SafeHandles.SafeFileHandle? anchor, out string? anchorName, out string? putAside, out bool made)
    {
        putAside = null;
        made = false;
        var failures = new List<string>();
        // Backup for the read-only opens past any rule, take-ownership for the owner, restore
        // for the rule later: what an administrator has, and a test run as the owner does without.
        using (TokenPrivileges.Enable(TokenPrivileges.Backup, TokenPrivileges.TakeOwnership, TokenPrivileges.Restore))
        {
            for (var attempt = 0; ; attempt++)
            {
                var folder = new DirectoryInfo(configDirectory);
                if (folder.Exists && folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    folder.Delete();
                }

                made |= CreateForAdministrators(configDirectory);

                // The folder itself, held open while its contents are done.
                using var root = OpenEntryItself(configDirectory, isFolder: true, ReadControl);
                if (root is null)
                {
                    // A link again, put there since the lines above.
                    if (attempt == 4)
                    {
                        throw new IOException($"{configDirectory} keeps being replaced with a link; it was not made safe.");
                    }

                    continue;
                }

                var owner = OwnerOf(root);
                var ownedByWhoeverRunsThis = owner is not null && owner == CurrentUser.Value;
                if (owner == LocalSystem || owner == Administrators)
                {
                    // As it should be.
                }
                else if ((ownedByWhoeverRunsThis ? IsElevated.Value : AdministratorState(owner)) is not { } ofAnAdministrator)
                {
                    // Not known, and it could be an administrator's: a folder with someone's
                    // settings is not put aside on a guess. Nothing is run over it either.
                    throw new IOException(
                        $"{configDirectory} was made by {NameOf(owner!)}, an account from a domain or Entra ID, and whether that is an administrator of this PC could not be found out: it takes the domain to tell. " +
                        "The folder was left as it is. Run this again when the domain can be reached; if it never can, give the folder to the Administrators group to keep its settings, or delete it.");
                }
                else if (ofAnAdministrator)
                {
                    // An administrator's own: made by their app, which runs without elevation,
                    // so what it makes is theirs by name and not the group's. It is the PC's
                    // folder all the same, and stays, with what is in it; only its owner
                    // becomes the group, so that changing its rule takes elevation. Also when
                    // it is the administrator's who runs this: once an elevated command has
                    // been here the owner is SYSTEM or the group, and nobody who looks at the
                    // folder later (the service, a silent update as SYSTEM) has to find out
                    // whether an account is an administrator, which for one from a domain
                    // takes a domain controller.
                    GiveToAdministrators(root, configDirectory);
                }
                else if (!ownedByWhoeverRunsThis)
                {
                    if (attempt == 4)
                    {
                        throw new IOException($"{configDirectory} keeps being made again by a user who is no administrator; it was not made safe.");
                    }

                    // Our own handle would keep it from being renamed.
                    root.Dispose();
                    putAside = PutAside(configDirectory) ?? putAside;
                    continue;
                }

                // Before the anchor: whoever could still delete it would make it worth nothing.
                KeepOthersFromReplacing(configDirectory);
                anchor = TryCreateAnchor(root, out anchorName);
                if (IsLink(root))
                {
                    anchor?.Dispose();
                    throw new IOException($"{configDirectory} was replaced with a link while it was made safe.");
                }

                using (var contents = OpenToList(root, configDirectory))
                {
                    TakeOverContents(contents, configDirectory, failures, anchorName);
                }

                return failures;
            }
        }
    }

    // The folder that was checked, given to the Administrators group. Opened again for the
    // right to set the owner, which an administrator has by the take-ownership privilege, and
    // compared: it is that folder, or nothing is changed.
    private static void GiveToAdministrators(Microsoft.Win32.SafeHandles.SafeFileHandle folder, string path)
    {
        using var owner = OpenEntryItself(path, isFolder: true, ReadControl | WriteOwner)
            ?? throw new IOException($"{path} became a link while its owner was being set.");
        if (!IsSameFile(folder, owner))
        {
            throw new IOException($"{path} was replaced while its owner was being set.");
        }

        var descriptor = new RawSecurityDescriptor(ControlFlags.None, Administrators, null, null, null);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        if (!SetKernelObjectSecurity(owner, OwnerSecurityInformation, bytes))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"{path} could not be taken over");
        }
    }

    private static bool IsSameFile(Microsoft.Win32.SafeHandles.SafeFileHandle one, Microsoft.Win32.SafeHandles.SafeFileHandle other)
    {
        if (!GetFileInformationByHandle(one, out var first) || !GetFileInformationByHandle(other, out var second))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return first.VolumeSerialNumber == second.VolumeSerialNumber
            && first.FileIndexHigh == second.FileIndexHigh && first.FileIndexLow == second.FileIndexLow;
    }

    /// <summary>
    /// A settings folder whose owner is neither SYSTEM, an administrator nor whoever runs this
    /// is not made ours: it is renamed out of the way, with all it holds, for a new one to be
    /// made in its place. Windows checks a right when a handle is opened, not when it is used.
    /// Whoever made the folder owned it, and may hold a handle on it, opened then, with the
    /// right to change its rule; a new owner and a new rule take nothing back from that
    /// handle, through which they would give themselves the folder again whenever they liked.
    /// No handle from before can be open on a folder that was not there before. Nothing is
    /// taken over from the old one, a file moved would bring the handles open on it along, and
    /// nothing is deleted: an administrator can look at it and take from it what is theirs.
    /// </summary>
    /// <returns>Where it is now; null when it was gone by the time it was to be renamed.</returns>
    private static string? PutAside(string configDirectory)
    {
        var aside = $"{configDirectory}.made-by-another-user-{Guid.NewGuid():N}"[..^24];
        try
        {
            Directory.Move(configDirectory, aside);
            return aside;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"{configDirectory} was made by a user who is no administrator, and something has it or a file in it open, " +
                $"so it could not be put aside for a new one to be made ({ex.Message.TrimEnd('.')}). " +
                "Sign the other users of this PC out, or restart it, and try again.", ex);
        }
    }

    // What lets its holder replace the folder, or what is in it whatever the rule of that is:
    // delete or rename the folder, delete anything in it, change the folder's rule or owner.
    private const FileSystemRights ReplacesTheFolder =
        FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>
    /// Takes those rights on the folder itself away from everyone but SYSTEM, the administrators
    /// and whoever runs this, on a folder that is ours but whose rule gives them to others
    /// (set by hand, or by a version before 10.9.1-beta.5). Until the folder's rule is set they
    /// could still delete the anchor (the rule of a file does not protect it from someone who
    /// may delete anything in its folder), or rename the folder out of the way, and put a link
    /// in its place. Only those rights go, and only on the folder itself: what the entry gave
    /// besides them stays, and so does what it passes on to what is in the folder. An update
    /// reads from the rule who the folder was open to, and the rule of a version before
    /// 10.9.1-beta.5 gave its users Modify on the folder, which has the right to delete in it:
    /// with the whole entry gone, such a folder would read as one that nobody may use.
    /// </summary>
    private static void KeepOthersFromReplacing(string configDirectory)
    {
        using var folder = OpenEntryItself(configDirectory, isFolder: true, ReadControl | WriteDac)
            ?? throw new IOException($"{configDirectory} was replaced with a link while it was made safe.");
        GetKernelObjectSecurity(folder, DaclSecurityInformation, null, 0, out var needed);
        var buffer = new byte[Math.Max(needed, 1u)];
        if (!GetKernelObjectSecurity(folder, DaclSecurityInformation, buffer, (uint)buffer.Length, out _))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        // No rule at all lets everyone do everything: SYSTEM and the administrators it is, then.
        var rule = new RawSecurityDescriptor(buffer, 0).DiscretionaryAcl;
        var kept = new RawAcl(GenericAcl.AclRevision, rule?.Count ?? 2);
        var changed = rule is null;
        if (rule is null)
        {
            const AceFlags inherit = AceFlags.ContainerInherit | AceFlags.ObjectInherit;
            kept.InsertAce(0, new CommonAce(inherit, AceQualifier.AccessAllowed, (int)FileSystemRights.FullControl, LocalSystem, false, null));
            kept.InsertAce(1, new CommonAce(inherit, AceQualifier.AccessAllowed, (int)FileSystemRights.FullControl, Administrators, false, null));
        }

        foreach (var ace in rule?.Cast<GenericAce>() ?? [])
        {
            if (ace is not CommonAce common)
            {
                kept.InsertAce(kept.Count, ace);
                continue;
            }

            // The folder's own from here on, not what ProgramData passes down to it.
            var flags = common.AceFlags & ~AceFlags.Inherited;
            var onTheFolder = WithoutGenericRights(common.AccessMask);
            if (common.AceQualifier == AceQualifier.AccessAllowed
                && !flags.HasFlag(AceFlags.InheritOnly)
                && ((FileSystemRights)onTheFolder & ReplacesTheFolder) != 0
                && !OwnerIsTrusted(common.SecurityIdentifier) && common.SecurityIdentifier != OwnerRights)
            {
                changed = true;
                // On the folder itself: what the entry gave, without those rights.
                var left = onTheFolder & ~(int)ReplacesTheFolder;
                if (left != 0)
                {
                    kept.InsertAce(kept.Count, new CommonAce(
                        flags & ~(AceFlags.ContainerInherit | AceFlags.ObjectInherit | AceFlags.NoPropagateInherit),
                        common.AceQualifier, left, common.SecurityIdentifier, common.IsCallback, common.GetOpaque()));
                }

                // For what is in the folder: as it was.
                if ((flags & (AceFlags.ContainerInherit | AceFlags.ObjectInherit)) != 0)
                {
                    kept.InsertAce(kept.Count, new CommonAce(
                        flags | AceFlags.InheritOnly, common.AceQualifier, common.AccessMask, common.SecurityIdentifier, common.IsCallback, common.GetOpaque()));
                }

                continue;
            }

            kept.InsertAce(kept.Count, new CommonAce(
                flags, common.AceQualifier, common.AccessMask, common.SecurityIdentifier, common.IsCallback, common.GetOpaque()));
        }

        if (!changed)
        {
            return;
        }

        var descriptor = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected, null, null, null, kept);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        SetRule(folder, bytes);
    }

    // The rights an entry gives on a folder, with the generic ones (all, read, write, execute)
    // written out as what they stand for on a file or folder: "all" has the rights that
    // replace the folder in it, and would pass for having none.
    private static int WithoutGenericRights(int mask)
    {
        const int genericRead = unchecked((int)0x80000000), genericWrite = 0x40000000, genericExecute = 0x20000000, genericAll = 0x10000000;
        var rights = mask & ~(genericRead | genericWrite | genericExecute | genericAll);
        if ((mask & genericAll) != 0)
        {
            rights |= (int)FileSystemRights.FullControl;
        }

        if ((mask & genericRead) != 0)
        {
            rights |= (int)(FileSystemRights.Read | FileSystemRights.Synchronize);
        }

        if ((mask & genericWrite) != 0)
        {
            rights |= (int)(FileSystemRights.Write | FileSystemRights.ReadPermissions | FileSystemRights.Synchronize);
        }

        if ((mask & genericExecute) != 0)
        {
            rights |= (int)(FileSystemRights.ExecuteFile | FileSystemRights.ReadAttributes | FileSystemRights.ReadPermissions | FileSystemRights.Synchronize);
        }

        return rights;
    }

    // Entry by entry through a handle on the entry itself, opened under the folder's handle and
    // never by path, like ResetContents: a link (a junction, a symbolic link) is not followed
    // and a hard link, a file under another name elsewhere whose owner is that file's, is not
    // touched; both go, the name only. One entry that cannot be looked at or taken over is
    // named among the failures and the rest is still done; SetFolderRule reports them with
    // its own.
    // The anchor just made (in the settings folder itself) is left as it is; the anchors of
    // earlier runs, and anything a user put there under such a name, go.
    private static void TakeOverContents(Microsoft.Win32.SafeHandles.SafeFileHandle folder, string path, List<string> failures, string? anchor = null)
    {
        foreach (var (name, isFolder) in NamesIn(folder, path))
        {
            var entryPath = Path.Combine(path, name);
            if (anchor is not null && !isFolder && IsAnchorName(name)
                && (string.Equals(name, anchor, StringComparison.OrdinalIgnoreCase) || TryDeleteIn(folder, name, isFolder, entryPath)))
            {
                continue;
            }

            try
            {
                using var entry = OpenIn(folder, name, isFolder, ReadControl | (isFolder ? FileListDirectory : 0), entryPath);
                if (IsLinkOrHasOtherNames(entry, isFolder))
                {
                    entry.Dispose();
                    DeleteIn(folder, name, isFolder, entryPath);
                    continue;
                }

                if (!OwnerIsTrusted(OwnerOf(entry)))
                {
                    // The right to set the owner is asked for only now, when it is needed: an
                    // administrator has it by the take-ownership privilege, and an entry whose
                    // rule grants no such right still opens for the check above. Owner only:
                    // the rule itself is set afterwards, inherited from the folder's.
                    using var owner = OpenIn(folder, name, isFolder, ReadControl | WriteOwner, entryPath);
                    if (IsLinkOrHasOtherNames(owner, isFolder))
                    {
                        throw new IOException($"{entryPath} became a link while its owner was being set.");
                    }

                    var descriptor = new RawSecurityDescriptor(ControlFlags.None, Administrators, null, null, null);
                    var bytes = new byte[descriptor.BinaryLength];
                    descriptor.GetBinaryForm(bytes, 0);
                    if (!SetKernelObjectSecurity(owner, OwnerSecurityInformation, bytes))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), $"{entryPath} could not be taken over");
                    }
                }

                if (isFolder)
                {
                    TakeOverContents(entry, entryPath, failures);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                failures.Add($"{entryPath}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The entry under the path, opened as it is, or null (and nothing open) when it is a link
    /// or a hard link. For the settings folder itself and what is asked about by its path;
    /// what is in a folder is opened under the folder's handle (<see cref="OpenIn"/>).
    /// The handle keeps nobody from renaming or deleting what it is open on, whatever it
    /// shares: Windows checks sharing only for a handle that reads, writes or deletes.
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

    /// <summary>Whether a link (a junction, a symbolic link) stands where the folder should be.</summary>
    internal static bool IsLink(string directory)
    {
        var folder = new DirectoryInfo(directory);
        return folder.Exists && folder.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    /// <summary>
    /// Whether the file, and the folder it is in, are SYSTEM's, the administrators' or the
    /// user's who runs this, and plain: no link, no file with another name elsewhere. For a
    /// file in a place where every user of the PC can make a folder (ProgramData): another
    /// user's is not taken for this PC's.
    /// </summary>
    internal static bool IsOwnOrAdministrators(string directory, string file)
    {
        try
        {
            using var folder = OpenEntryItself(directory, isFolder: true, ReadControl);
            if (folder is null || !OwnerIsOfThePc(OwnerOf(folder)))
            {
                return false;
            }

            using var entry = OpenEntryItself(file, isFolder: false, ReadControl);
            return entry is not null && OwnerIsOfThePc(OwnerOf(entry));
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            // Not there, or not to be looked at.
            return false;
        }
    }

    private static bool OwnerIsTrusted(FileSystemSecurity security) =>
        OwnerIsTrusted(security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier);

    /// <summary>
    /// Whether the settings folder is the PC's to use: not there yet, or a folder (no link)
    /// whose owner is SYSTEM, the administrators, an administrator, or whoever runs this.
    /// ProgramData lets every user make a folder: one that another user made, who is no
    /// administrator, is theirs to change as they like, whatever is done to it later through
    /// a handle they kept. The service writes nothing into such a folder and the app loads no
    /// settings from it; an elevated command puts it aside (<see cref="MakeSafe(string)"/>).
    /// </summary>
    internal static bool IsOfThePc(string configDirectory)
    {
        try
        {
            if (!Directory.Exists(configDirectory))
            {
                return true;
            }

            using var folder = OpenEntryItself(configDirectory, isFolder: true, ReadControl);
            return folder is not null && OwnerIsOfThePc(OwnerOf(folder));
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
        {
            // Not to be looked at: not taken on trust.
            return false;
        }
    }

    // What is left as it is: SYSTEM's, the Administrators group's, and what whoever runs this
    // owns themselves (a test's own folder; their own files).
    private static bool OwnerIsTrusted(SecurityIdentifier? owner) =>
        owner is not null && (owner == LocalSystem || owner == Administrators || owner == CurrentUser.Value);

    // The above, or an administrator's by name: what their app made without elevation.
    private static bool OwnerIsOfThePc(SecurityIdentifier? owner) => OwnerIsTrusted(owner) || IsAnAdministrator(owner);

    // A member of this PC's Administrators group, itself or through a group from a domain.
    // Not known counts as no: nothing is run over a folder on the strength of a guess.
    private static bool IsAnAdministrator(SecurityIdentifier? account) => AdministratorState(account) is true;

    // The same, with what is not known kept apart (null), for the one place that acts on a
    // "no" by putting a folder aside (MakeSafe). Asked once for an account whose answer is
    // known: the owners of everything in the folder are looked at.
    private static bool? AdministratorState(SecurityIdentifier? account)
    {
        if (account is null)
        {
            return false;
        }

        if (KnownAdministrators.TryGetValue(account, out var known))
        {
            return known;
        }

        var state = FindAdministratorState(account);
        if (state is { } found)
        {
            KnownAdministrators[account] = found;
        }

        return state;
    }

    private static bool? FindAdministratorState(SecurityIdentifier account)
    {
        var groupsKnown = false;
        var administratorsAmongThem = false;
        try
        {
            var (group, members) = AdministratorsGroup();
            if (members.Contains(account))
            {
                return true;
            }

            var name = ((NTAccount)account.Translate(typeof(NTAccount))).Value;
            administratorsAmongThem = LocalGroupsOf(name, out groupsKnown).Contains(group, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SystemException)
        {
            // No name for the account, or no answer: its groups are not known.
            groupsKnown = false;
        }

        return AdministratorThroughAGroup(
            groupsKnown, administratorsAmongThem, IsDirectoryAccount(account.Value), HasAProfileHere(account));
    }

    /// <summary>
    /// Whether an account that the Administrators group does not name itself is an
    /// administrator through a group, by what Windows said of its groups. Null when that is
    /// not known and could be yes: an account from a domain or Entra ID that has logged on to
    /// this PC, whose groups come from a domain controller that was not reached (a laptop off
    /// the network, an update run as SYSTEM). An account of this PC whose groups Windows does
    /// not tell, and one that never logged on here (a deleted account's leftover, a SID from
    /// another PC), is no administrator.
    /// </summary>
    internal static bool? AdministratorThroughAGroup(bool groupsKnown, bool administratorsAmongThem, bool directoryAccount, bool hasAProfileHere) =>
        groupsKnown ? administratorsAmongThem
        : directoryAccount && hasAProfileHere ? null
        : false;

    // Whether the account has logged on to this PC: its profile is listed.
    private static bool HasAProfileHere(SecurityIdentifier account)
    {
        try
        {
            using var profile = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{account.Value}");
            return profile is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<SecurityIdentifier, bool> KnownAdministrators = new();

    // Whether this runs with an administrator's rights (elevated, or as SYSTEM).
    private static readonly Lazy<bool> IsElevated = new(() =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator));

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

    private static SecurityIdentifier EnsureGroup(string name, string comment)
    {
        var info = new LocalGroupInfo1 { Name = name, Comment = comment };
        var status = NetLocalGroupAdd(null, 1, ref info, out _);
        if (status is not (0 or ErrorAliasExists or NerrGroupExists))
        {
            throw new InvalidOperationException($"The local group \"{name}\" could not be created (error {status}).");
        }

        return TryGroupSid(name) ?? throw new InvalidOperationException($"The local group \"{name}\" was not found after creating it.");
    }

    /// <summary>
    /// The PC's name, for looking up its own accounts and groups. Not Environment.MachineName:
    /// Windows answers that from the variable _CLUSTER_NETWORK_NAME_ when the process has one,
    /// and a user sets the variables of their own processes. A kiosk user did, the lookup of
    /// the kiosk group then found nothing, and they got the full app.
    /// </summary>
    internal static string MachineName => PhysicalMachineName.Value;

    private static readonly Lazy<string> PhysicalMachineName = new(() =>
    {
        var size = 0u;
        GetComputerNameEx(ComputerNamePhysicalNetBios, null, ref size);
        var buffer = new char[size];
        if (!GetComputerNameEx(ComputerNamePhysicalNetBios, buffer, ref size))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return new string(buffer, 0, (int)size);
    });

    /// <summary>The SID of a local group of this PC; null when there is no such group.</summary>
    internal static SecurityIdentifier? TryGroupSid(string name)
    {
        try
        {
            return (SecurityIdentifier)new NTAccount(MachineName, name).Translate(typeof(SecurityIdentifier));
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
    internal static SecurityIdentifier Sid(string user) =>
        user.StartsWith("S-1-", StringComparison.Ordinal)
            ? new SecurityIdentifier(user)
            : (SecurityIdentifier)new NTAccount(user.Contains('\\') ? user : $"{MachineName}\\{user}").Translate(typeof(SecurityIdentifier));

    private static void ChangeMember(string group, SecurityIdentifier sid, bool add)
    {
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        var sidPointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, sidPointer, bytes.Length);
            var member = new LocalGroupMembersInfo0 { Sid = sidPointer };
            var status = add
                ? NetLocalGroupAddMembers(null, group, 0, ref member, 1)
                : NetLocalGroupDelMembers(null, group, 0, ref member, 1);
            if (status is not (0 or ErrorMemberInAlias or ErrorMemberNotInAlias))
            {
                throw new InvalidOperationException($"{(add ? "Adding" : "Removing")} {sid} {(add ? "to" : "from")} \"{group}\" failed (error {status}).");
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
    private const int NerrGroupNotFound = 2220;
    private const int ErrorNoSuchAlias = 1376;
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
    private static extern int NetLocalGroupDel(string? server, string group);

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

    // The name of this PC itself; the variable that renames a process's computer does not reach it.
    private const int ComputerNamePhysicalNetBios = 4;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetComputerNameEx(int nameType, char[]? buffer, ref uint size);

    // The descriptor carries the "protected" flag set by SetAccessRuleProtection; nothing is
    // propagated to what is in the folder here, ResetContents does that afterwards.
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetKernelObjectSecurity(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint information, byte[] descriptor);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetKernelObjectSecurity(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint information, byte[]? descriptor, uint length, out uint needed);

    private const uint Synchronize = 0x00100000;
    private const uint ObjCaseInsensitive = 0x40;
    private const uint FileCreate = 2;
    private const uint FileNonDirectoryFile = 0x40;
    private const uint FileSynchronousIoNonAlert = 0x20;
    private const uint FileOpenReparsePointOption = 0x00200000;
    private const uint FileOpenForBackupIntent = 0x00004000;
    private const int ErrorAccessDenied = 5;

    // Creates a file under a name relative to an open folder: CreateFile only takes a path,
    // and a path is looked up again from the top, through whatever the folder has become.
    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(
        out Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint access, ref ObjectAttributes attributes, out IoStatusBlock status,
        IntPtr allocationSize, uint fileAttributes, uint share, uint disposition, uint options, IntPtr eaBuffer, uint eaLength);

    [DllImport("ntdll.dll")]
    private static extern int RtlNtStatusToDosError(int status);

    private const uint FileListDirectory = 0x0001;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileOpen = 1;
    private const uint FileDirectoryFile = 0x01;
    private const int ErrorNoMoreFiles = 18;
    private const int FileDispositionInfo = 4;
    private const int FileFullDirectoryInfo = 14;
    private const int FileFullDirectoryRestartInfo = 15;

    // A folder's entries, read through its handle.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(Microsoft.Win32.SafeHandles.SafeFileHandle file, int informationClass, IntPtr buffer, uint size);

    // Deletes what the handle is open on (FILE_DISPOSITION_INFO: one byte).
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, int informationClass, ref byte information, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
    }

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
