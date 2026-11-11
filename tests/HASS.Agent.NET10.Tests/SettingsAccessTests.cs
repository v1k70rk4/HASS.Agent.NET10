using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using HASS.Agent.Companion.Configuration;

namespace HASS.Agent.Companion.Tests;

/// <summary>
/// The folder rules, on a folder of the test's own: setting the rules of a folder you own
/// needs no administrator. Creating the group and changing its members does, so that part is
/// tried by hand (RV-NOTE, mediaserver).
/// </summary>
[Collection(SettingsFolderCollection.Name)]
public class SettingsAccessTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("hass-agent-access-").FullName;

    private static SecurityIdentifier Me => WindowsIdentity.GetCurrent().User!;

    public void Dispose()
    {
        // Give the folder back to its owner first, whatever a test left on it.
        SettingsAccess.SetFolderRule(_folder, [Me]);
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Folder_open_to_every_user_is_not_restricted()
    {
        SettingsAccess.OpenToEveryone(_folder);

        Assert.False(SettingsAccess.IsRestricted(_folder));
        Assert.True(SettingsAccess.CurrentUserCanUse(_folder));
    }

    [Fact]
    public void Folder_for_named_users_is_restricted_and_they_can_use_it()
    {
        SettingsAccess.SetFolderRule(_folder, [Me]);

        Assert.True(SettingsAccess.IsRestricted(_folder));
        Assert.True(SettingsAccess.CurrentUserCanUse(_folder));
    }

    [Fact]
    public void Restricted_folder_inherits_nothing_and_holds_exactly_its_rules()
    {
        SettingsAccess.SetFolderRule(_folder, [Me]);

        var security = new DirectoryInfo(_folder).GetAccessControl();
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToList();

        Assert.True(security.AreAccessRulesProtected);
        Assert.All(rules, rule => Assert.False(rule.IsInherited));
        Assert.Equal(
            new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), Me }
                .Select(sid => sid.Value).Order(),
            rules.Select(rule => ((SecurityIdentifier)rule.IdentityReference).Value).Distinct().Order());
    }

    [Fact]
    public void Users_cannot_delete_or_replace_the_folder_itself()
    {
        SettingsAccess.SetFolderRule(_folder, [Me]);

        var mine = new DirectoryInfo(_folder).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => (SecurityIdentifier)rule.IdentityReference == Me)
            .ToList();

        // On the folder itself: list, read and create, no delete.
        Assert.All(mine.Where(rule => !rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)), rule =>
        {
            Assert.False(rule.FileSystemRights.HasFlag(FileSystemRights.Delete));
            Assert.True(rule.FileSystemRights.HasFlag(FileSystemRights.CreateFiles));
        });
        // On what is in it: change, replace and delete, as the app does with its files.
        Assert.Contains(mine, rule => rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)
            && rule.FileSystemRights.HasFlag(FileSystemRights.Modify));
    }

    [Fact]
    public void The_app_can_still_save_its_settings_in_the_folder()
    {
        SettingsAccess.SetFolderRule(_folder, [Me]);
        var file = Path.Combine(_folder, "settings.json");
        File.WriteAllText(file, "{}");

        // What SettingsStore does: a new file next to it, then replaced with a backup.
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, """{"a":1}""");
        File.Replace(temporary, file, file + ".bak");

        Assert.Equal("""{"a":1}""", File.ReadAllText(file));
    }

    [Fact]
    public void A_link_in_the_folder_is_removed_not_followed()
    {
        var outside = Directory.CreateTempSubdirectory("hass-agent-outside-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "keep.txt"), "x");
            var link = Path.Combine(_folder, "link");
            Directory.CreateSymbolicLink(link, outside);
        }
        catch (IOException)
        {
            // Creating a symbolic link needs Developer Mode or an administrator.
            Directory.Delete(outside, recursive: true);
            return;
        }
        catch (UnauthorizedAccessException)
        {
            Directory.Delete(outside, recursive: true);
            return;
        }

        try
        {
            SettingsAccess.SetFolderRule(_folder, [Me]);

            Assert.False(Directory.Exists(Path.Combine(_folder, "link")));
            Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void A_hard_link_in_the_folder_is_removed_and_the_file_it_names_keeps_its_rule()
    {
        // The other name of a file elsewhere: a rule set through the link would be that file's
        // rule too, which is how a user could have had the folder's rule put on a file of their
        // choosing. Hard links need no privilege, only the same volume (both under %TEMP%).
        var outside = Directory.CreateTempSubdirectory("hass-agent-outside-").FullName;
        try
        {
            var target = Path.Combine(outside, "target.txt");
            File.WriteAllText(target, "x");
            var before = new FileInfo(target).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            var link = Path.Combine(_folder, "settings.json");
            Assert.True(CreateHardLink(link, target, IntPtr.Zero), $"CreateHardLink failed: {Marshal.GetLastWin32Error()}");
            Assert.True(SettingsAccess.HasOtherNames(link));
            Assert.True(SettingsAccess.HasOtherNames(target)); // the same file, seen from its other name
            File.WriteAllText(Path.Combine(_folder, "plain.txt"), "y");

            SettingsAccess.SetFolderRule(_folder, [Me]);

            Assert.False(File.Exists(link));
            Assert.True(File.Exists(target));
            Assert.Equal(before, new FileInfo(target).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            Assert.False(SettingsAccess.HasOtherNames(target));
            // The ordinary file next to it took the rule.
            var plain = new FileInfo(Path.Combine(_folder, "plain.txt")).GetAccessControl();
            Assert.False(plain.AreAccessRulesProtected);
            Assert.All(plain.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), rule => Assert.True(rule.IsInherited));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void The_rule_a_folder_passes_on_is_what_windows_would_inherit()
    {
        var security = new DirectorySecurity();
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        const InheritanceFlags both = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, both, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Me, SettingsAccess.UsersOnTheFolder, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Me, FileSystemRights.Modify | FileSystemRights.Synchronize, both, PropagationFlags.InheritOnly, AccessControlType.Allow));
        var folderRule = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl!;

        var toFolder = SettingsAccess.InheritedFrom(folderRule, toFolder: true).Cast<CommonAce>().ToList();
        var toFile = SettingsAccess.InheritedFrom(folderRule, toFolder: false).Cast<CommonAce>().ToList();

        // The folder-only rule is not passed on; the rest arrives marked inherited, a subfolder
        // keeps passing it on, a file gets it flat.
        Assert.Equal(2, toFolder.Count);
        Assert.All(toFolder, ace => Assert.Equal(AceFlags.Inherited | AceFlags.ContainerInherit | AceFlags.ObjectInherit, ace.AceFlags));
        Assert.Equal(2, toFile.Count);
        Assert.All(toFile, ace => Assert.Equal(AceFlags.Inherited, ace.AceFlags));
        Assert.Contains(toFile, ace => ace.SecurityIdentifier == Me && (FileSystemRights)ace.AccessMask == (FileSystemRights.Modify | FileSystemRights.Synchronize));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    [Fact]
    public void Files_already_there_take_the_folder_rule()
    {
        var file = Path.Combine(_folder, "settings.json");
        File.WriteAllText(file, "{}");
        SettingsAccess.OpenToEveryone(_folder);

        SettingsAccess.SetFolderRule(_folder, [Me]);

        var everyone = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        var rules = new FileInfo(file).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>();
        Assert.DoesNotContain(rules, rule => (SecurityIdentifier)rule.IdentityReference == everyone);
    }

    [Fact]
    public void An_update_leaves_a_restricted_folder_as_it_is()
    {
        SettingsAccess.SetFolderRule(_folder, [Me]);

        SettingsAccess.KeepAsIs(_folder);

        Assert.True(SettingsAccess.IsRestricted(_folder));
    }

    // What an update decides, without doing it: doing it on an open folder would create the
    // local group and change its members on the machine that runs the tests.
    [Theory]
    [InlineData(true, 3, 1, 0, "LeaveAsItIs")]       // already limited
    [InlineData(true, 1, 0, 0, "LeaveAsItIs")]
    [InlineData(false, 1, 0, 0, "LimitToTheUsers")]  // administrators only
    [InlineData(false, 2, 0, 0, "LimitToTheUsers")]
    [InlineData(false, 2, 1, 0, "OpenToEveryone")]   // someone else's choice
    [InlineData(false, 0, 0, 0, "OpenToEveryone")]   // no users found: as before
    [InlineData(false, 1, 0, 1, "OpenToEveryone")]   // a domain or Entra user has logged on who is not known to be an administrator
    public void What_an_update_does_with_the_folder(bool restricted, int users, int nonAdministrators, int directoryUsersNotKnown, string expected)
    {
        Assert.Equal(expected, SettingsAccess.OnUpdate(restricted, users, nonAdministrators, directoryUsersNotKnown).ToString());
    }

    [Fact]
    public void The_pc_s_own_sid_comes_from_windows()
    {
        // Also on a PC with no enabled local account, where it cannot be taken from one.
        var machine = SettingsAccess.AccountDomainSid();

        Assert.NotNull(machine);
        Assert.StartsWith("S-1-5-21-", machine.Value);
        if (string.Equals(Environment.UserDomainName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            Assert.Equal(Me.AccountDomainSid, machine);
        }
    }

    [Fact]
    public void A_directory_user_in_the_administrators_group_by_their_own_account_is_an_administrator()
    {
        // By SID, as one whose name no longer resolves is listed: no domain controller is asked.
        var user = new SecurityIdentifier("S-1-5-21-9999-8888-7777-1105");
        var members = new HashSet<SecurityIdentifier> { user };

        Assert.True(SettingsAccess.IsAdministratorHere(user.Value, "Administrators", members));
        Assert.False(SettingsAccess.IsAdministratorHere("S-1-5-21-9999-8888-7777-1106", "Administrators", members));
    }

    [Fact]
    public void The_directory_users_of_this_pc_are_split_by_role()
    {
        var (administrators, notKnown) = SettingsAccess.DirectoryUsersByRole();

        Assert.Equal(
            SettingsAccess.DirectoryUsersWithProfiles().Order(StringComparer.OrdinalIgnoreCase),
            administrators.Concat(notKnown).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("S-1-5-21-1111-2222-3333-1001", false)]   // this PC's own account
    [InlineData("S-1-5-21-1111-2222-3333-500", false)]
    [InlineData("S-1-5-21-9999-8888-7777-1105", true)]    // a domain account
    [InlineData("S-1-12-1-123-456-789-1011", true)]       // an Entra ID account
    [InlineData("S-1-5-18", false)]                       // SYSTEM and the service accounts have profiles too
    [InlineData("S-1-5-19", false)]
    [InlineData("S-1-5-20", false)]
    [InlineData("not-a-sid", false)]
    [InlineData("S-1-12-1-broken", false)]                // shaped like an Entra one, not a SID
    [InlineData("S-1-5-21-", false)]
    public void A_profile_from_a_domain_or_entra_is_told_from_a_local_one(string sid, bool expected)
    {
        var machine = new SecurityIdentifier("S-1-5-21-1111-2222-3333");

        Assert.Equal(expected, SettingsAccess.IsDirectoryAccount(sid, machine));
    }

    [Fact]
    public void Without_the_machine_sid_a_domain_shaped_profile_counts_as_not_local()
    {
        Assert.True(SettingsAccess.IsDirectoryAccount("S-1-5-21-1111-2222-3333-1001", machine: new SecurityIdentifier("S-1-5-21-4-5-6")));
    }

    [Fact]
    public void The_profiles_of_this_pc_can_be_listed()
    {
        // Whatever this PC has: the call works, and a local account never shows up as a directory one.
        var users = SettingsAccess.DirectoryUsersWithProfiles();

        if (string.Equals(Environment.UserDomainName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            Assert.DoesNotContain(users, user => user.EndsWith("\\" + Environment.UserName, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void An_update_does_not_open_a_folder_whose_rule_it_cannot_read()
    {
        // Default-deny: unknown is not "open", so an update leaves it alone (and creates nothing).
        var missing = Path.Combine(_folder, "unreadable");

        Assert.Null(SettingsAccess.RestrictionState(missing));
        SettingsAccess.KeepAsIs(missing);

        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void A_folder_that_is_not_there_yet_can_be_used()
    {
        Assert.True(SettingsAccess.CurrentUserCanUse(Path.Combine(_folder, "not-yet")));
    }

    [Fact]
    public void Users_who_are_not_administrators_are_among_the_local_users()
    {
        var users = SettingsAccess.LocalUsers();

        Assert.All(SettingsAccess.NonAdministratorUsers(), user => Assert.Contains(user, users, StringComparer.OrdinalIgnoreCase));
    }

    private string[] Anchors() =>
        Directory.GetFiles(_folder).Where(file => SettingsAccess.IsAnchorName(Path.GetFileName(file))).ToArray();

    [Fact]
    public void The_folder_gets_an_anchor_that_is_not_its_users_to_touch()
    {
        File.WriteAllText(Path.Combine(_folder, "settings.json"), "{}");

        SettingsAccess.SetFolderRule(_folder, [Me]);

        var security = new FileInfo(Assert.Single(Anchors())).GetAccessControl();
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToList();
        // SYSTEM, the administrators and its owner, by a rule of its own: the user the folder
        // is for (the test's own, by name) is not among them, and nothing comes from the folder.
        Assert.True(security.AreAccessRulesProtected);
        Assert.All(rules, rule => Assert.False(rule.IsInherited));
        Assert.Equal(
            new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.WinCreatorOwnerRightsSid }
                .Select(sid => new SecurityIdentifier(sid, null).Value).Order(),
            rules.Select(rule => ((SecurityIdentifier)rule.IdentityReference).Value).Distinct().Order());
    }

    [Fact]
    public void With_its_anchor_the_folder_cannot_be_turned_into_a_link()
    {
        // What a user of the folder could do before: delete what is in it, then make the empty
        // folder a junction, and have the service write wherever it points.
        var elsewhere = Directory.CreateTempSubdirectory("hass-agent-elsewhere-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(_folder, "settings.json"), "{}");
            SettingsAccess.SetFolderRule(_folder, [Me]);
            File.Delete(Path.Combine(_folder, "settings.json"));

            Assert.Equal(ErrorDirectoryNotEmpty, SetJunction(_folder, elsewhere));
            Assert.False(SettingsAccess.IsLink(_folder));

            // And the anchor is what holds it: without it, the same call makes the link.
            File.Delete(Assert.Single(Anchors()));
            Assert.Equal(0, SetJunction(_folder, elsewhere));
            Assert.True(SettingsAccess.IsLink(_folder));
        }
        finally
        {
            // The link only; setting the rule again (Dispose) then starts from a folder.
            if (SettingsAccess.IsLink(_folder))
            {
                Directory.Delete(_folder);
            }

            Directory.Delete(elsewhere, recursive: true);
        }
    }

    [Fact]
    public void Setting_the_rule_again_leaves_one_anchor_a_new_one()
    {
        SettingsAccess.SetFolderRule(_folder, [Me]);
        var first = Assert.Single(Anchors());

        SettingsAccess.OpenToEveryone(_folder);

        Assert.NotEqual(first, Assert.Single(Anchors()));
    }

    [Fact]
    public void A_file_put_there_under_an_anchor_s_name_is_not_the_anchor()
    {
        // A user's own file under such a name would be theirs to delete again.
        var planted = Path.Combine(_folder, "folder-000000000000.anchor");
        File.WriteAllText(planted, "x");

        SettingsAccess.SetFolderRule(_folder, [Me]);

        Assert.False(File.Exists(planted));
        Assert.Single(Anchors());
    }

    [Fact]
    public void A_folder_made_for_the_settings_is_nobody_else_s_from_its_first_moment()
    {
        // Made the usual way under ProgramData it would let every user create files in it,
        // and be empty: all it takes to turn it into a link before anything is written there.
        var folder = Path.Combine(_folder, "not-there-yet");

        SettingsAccess.MakeSafe(folder, out var putAside);

        Assert.Null(putAside);
        var security = new DirectoryInfo(folder).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(
            new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.WinCreatorOwnerRightsSid }
                .Select(sid => new SecurityIdentifier(sid, null).Value).Order(),
            security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().Select(rule => ((SecurityIdentifier)rule.IdentityReference).Value).Distinct().Order());
        // And it is not empty when this returns.
        Assert.Single(Directory.GetFiles(folder), file => SettingsAccess.IsAnchorName(Path.GetFileName(file)));
    }

    [Fact]
    public void Someone_else_s_hold_on_the_folder_goes_when_it_is_made_safe()
    {
        // A folder of ours whose rule gives someone else full control of it (set by hand, or by
        // an early version): with that they could delete the anchor, whatever its own rule, or
        // rename the folder out of the way.
        var someone = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var info = new DirectoryInfo(_folder);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(someone, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute | FileSystemRights.CreateFiles, AccessControlType.Allow));
        info.SetAccessControl(security);

        SettingsAccess.MakeSafe(_folder);

        var rules = new DirectoryInfo(_folder).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToList();
        // What lets them replace the folder is gone; the rest of what they had is not.
        var theirs = rules.Where(rule => (SecurityIdentifier)rule.IdentityReference == someone).ToList();
        Assert.NotEmpty(theirs);
        Assert.All(theirs, rule => Assert.Equal(
            (FileSystemRights)0,
            rule.FileSystemRights & (FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership)));
        Assert.Contains(theirs, rule => rule.FileSystemRights.HasFlag(FileSystemRights.ReadData));
        // What lets nobody replace the folder stays: an update reads from it who the folder was open to.
        Assert.Contains(rules, rule => (SecurityIdentifier)rule.IdentityReference == users
            && rule.FileSystemRights.HasFlag(FileSystemRights.CreateFiles));
        Assert.Single(Anchors());
    }

    [Fact]
    public void A_folder_with_the_rule_of_an_old_version_stays_open_to_its_users()
    {
        // Before 10.9.1-beta.5 the users had Modify on the folder itself and on what is in it,
        // in one entry. Modify has the right to delete in it: taken from the folder, with the
        // rest of the entry kept, or an update would read the folder as one nobody may use.
        var users = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        var info = new DirectoryInfo(_folder);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            users, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);

        SettingsAccess.MakeSafe(_folder);

        Assert.False(SettingsAccess.RestrictionState(_folder));
        var rules = new DirectoryInfo(_folder).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Where(rule => (SecurityIdentifier)rule.IdentityReference == users).ToList();
        var onTheFolder = rules.Where(rule => rule.PropagationFlags != PropagationFlags.InheritOnly).ToList();
        Assert.NotEmpty(onTheFolder);
        Assert.All(onTheFolder, rule => Assert.False(rule.FileSystemRights.HasFlag(FileSystemRights.Delete)));
        Assert.Contains(onTheFolder, rule => rule.FileSystemRights.HasFlag(FileSystemRights.ReadData | FileSystemRights.CreateFiles));
        // What is in the folder is theirs to change as before.
        Assert.Contains(rules, rule => rule.PropagationFlags == PropagationFlags.InheritOnly && rule.FileSystemRights.HasFlag(FileSystemRights.Modify));
    }

    [Fact]
    public void An_update_keeps_the_users_of_a_folder_limited_by_an_old_version()
    {
        // The same old entry on a folder that was limited to its users: what an update runs
        // must leave the folder theirs to use, in the shape the rule has today.
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(Me, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Someone, FileSystemRights.Modify, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(_folder).SetAccessControl(security);
        Assert.True(SettingsAccess.RestrictionState(_folder));

        SettingsAccess.KeepAsIs(_folder);

        Assert.True(SettingsAccess.RestrictionState(_folder));
        var theirs = new DirectoryInfo(_folder).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Where(rule => (SecurityIdentifier)rule.IdentityReference == Someone).ToList();
        var onTheFolder = theirs.Where(rule => rule.PropagationFlags != PropagationFlags.InheritOnly).ToList();
        Assert.Contains(onTheFolder, rule => rule.FileSystemRights.HasFlag(SettingsAccess.UsersOnTheFolder));
        Assert.All(onTheFolder, rule => Assert.False(rule.FileSystemRights.HasFlag(FileSystemRights.Delete)));
        Assert.Contains(theirs, rule => rule.PropagationFlags == PropagationFlags.InheritOnly && rule.FileSystemRights.HasFlag(FileSystemRights.Modify));
    }

    [Fact]
    public void A_folder_that_is_not_there_is_made_with_its_rule_and_its_anchor()
    {
        // What the service does at its start when the settings folder is missing.
        var folder = Path.Combine(_folder, "made-by-the-service");

        SettingsAccess.OpenToEveryone(folder);

        var security = new DirectoryInfo(folder).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        Assert.False(SettingsAccess.RestrictionState(folder));
        Assert.True(SettingsAccess.IsOfThePc(folder));
        Assert.Single(Directory.GetFiles(folder), file => SettingsAccess.IsAnchorName(Path.GetFileName(file)));
    }

    [Fact]
    public void A_folder_made_for_a_command_is_open_to_every_user()
    {
        // What "--install-service", "--kiosk on" or "--settings-access keep" find when the
        // settings folder is not there (deleted, or the app from its zip). Made the
        // administrators' alone and left so, the app would start for nobody, and an update
        // would take the one rule it has for a limit someone chose.
        var folder = Path.Combine(_folder, "made-for-a-command");

        var opened = SettingsAccess.MakeSafeForCommand(folder, setsTheFoldersRule: false, out var putAside, out var made);

        Assert.True(made);
        Assert.True(opened);
        Assert.Null(putAside);
        Assert.False(SettingsAccess.RestrictionState(folder));
        Assert.True(SettingsAccess.IsOfThePc(folder));
        Assert.Single(Directory.GetFiles(folder), file => SettingsAccess.IsAnchorName(Path.GetFileName(file)));
    }

    [Fact]
    public void Only_a_folder_made_for_the_command_is_opened_and_not_for_one_that_sets_the_rule()
    {
        // A folder that was there keeps the rule it has: limited stays limited.
        SettingsAccess.SetFolderRule(_folder, [Me]);

        Assert.False(SettingsAccess.IsAsItWasMade(_folder));

        var opened = SettingsAccess.MakeSafeForCommand(_folder, setsTheFoldersRule: false, out _, out var made);

        Assert.False(made);
        Assert.False(opened);
        Assert.True(SettingsAccess.RestrictionState(_folder));

        // A command that sets the rule itself gets the new folder as it was made.
        var folder = Path.Combine(_folder, "made-for-a-command-with-a-rule");

        opened = SettingsAccess.MakeSafeForCommand(folder, setsTheFoldersRule: true, out _, out made);

        Assert.True(made);
        Assert.False(opened);
        Assert.True(SettingsAccess.RestrictionState(folder));
    }

    [Theory]
    [InlineData(true, true, false, false, true)]     // its groups are known, the administrators among them
    [InlineData(true, false, true, true, false)]     // known, and not among them: no, also for one from a domain
    [InlineData(false, false, true, true, null)]     // from a domain, has logged on here, the domain not reached: not known
    [InlineData(false, true, true, true, null)]      // what an answer that is not one holds does not count
    [InlineData(false, false, true, false, false)]   // a SID from elsewhere that never logged on here
    [InlineData(false, false, false, true, false)]   // an account of this PC
    public void An_owner_from_a_domain_whose_groups_are_not_known_is_not_taken_for_no_administrator(
        bool groupsKnown, bool administratorsAmongThem, bool directoryAccount, bool hasAProfileHere, bool? administrator)
    {
        // Not known (null): the folder is neither used nor put aside. A laptop off the
        // network, updated as SYSTEM, with a folder its user from the domain made, who is an
        // administrator through a domain group: put aside, the settings were gone from the app.
        Assert.Equal(administrator, SettingsAccess.AdministratorThroughAGroup(
            groupsKnown, administratorsAmongThem, directoryAccount, hasAProfileHere));
    }

    [Fact]
    public void A_folder_left_with_the_rule_it_was_made_with_is_opened_by_the_next_command()
    {
        // "--settings-access only" made the folder and could not set its rule (a name that is
        // nobody's), or 10.13.0 made it for a command and left it: the administrators' alone,
        // with nobody having chosen that. What the folder is like says so, not which command
        // made it: "--install-service" or the next update's "keep" finds it there already.
        // Started from a shell that makes the user the owner of what an elevated program
        // creates (Git Bash), the new folder is given to the administrators, and Windows drops
        // the owner's entry with the change of owner: nothing tells it from a rule set by hand.
        Assert.SkipWhen(
            IsElevated && WindowsIdentity.GetCurrent().Owner == Me,
            "Run elevated from a shell that makes the user the owner of what is created.");
        var folder = Path.Combine(_folder, "left-as-it-was-made");
        SettingsAccess.MakeSafeForCommand(folder, setsTheFoldersRule: true, out _, out _);
        Assert.True(SettingsAccess.IsAsItWasMade(folder));

        var opened = SettingsAccess.MakeSafeForCommand(folder, setsTheFoldersRule: false, out var putAside, out var made);

        Assert.False(made);
        Assert.Null(putAside);
        Assert.True(opened);
        Assert.False(SettingsAccess.RestrictionState(folder));
        Assert.False(SettingsAccess.IsAsItWasMade(folder));

        // Open, it is left alone from then on.
        Assert.False(SettingsAccess.MakeSafeForCommand(folder, setsTheFoldersRule: false, out _, out _));
    }

    [Fact]
    public void A_rule_with_a_deny_someone_set_is_not_the_rule_a_folder_is_made_with()
    {
        // Set by hand: the owner's entry as a new folder has it, and a deny for a group (the
        // guests here, so that the test can still read its own folder). Nobody is named besides
        // the owner, and still someone chose this: it is not opened.
        Assert.SkipWhen(
            IsElevated && WindowsIdentity.GetCurrent().Owner == Me,
            "Run elevated from a shell that makes the user the owner of what is created.");
        var folder = Path.Combine(_folder, "denied-by-hand");
        SettingsAccess.MakeSafeForCommand(folder, setsTheFoldersRule: true, out _, out _);
        Assert.True(SettingsAccess.IsAsItWasMade(folder));
        var info = new DirectoryInfo(folder);
        var rule = info.GetAccessControl();
        rule.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null), FileSystemRights.ReadData, AccessControlType.Deny));
        info.SetAccessControl(rule);

        Assert.False(SettingsAccess.IsAsItWasMade(folder));
        Assert.False(SettingsAccess.MakeSafeForCommand(folder, setsTheFoldersRule: false, out _, out var made));
        Assert.False(made);
        Assert.True(SettingsAccess.RestrictionState(folder));
    }

    [Fact]
    public void A_rule_that_only_names_the_same_accounts_is_not_the_rule_a_folder_is_made_with()
    {
        // The owner's entry is there and nobody else is named, but with other rights, or
        // passed on to the contents: someone wrote this, it is not what a new folder has.
        Assert.SkipWhen(
            IsElevated && WindowsIdentity.GetCurrent().Owner == Me,
            "Run elevated from a shell that makes the user the owner of what is created.");
        var ownerRights = new SecurityIdentifier(WellKnownSidType.WinCreatorOwnerRightsSid, null);
        const InheritanceFlags contents = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        (string Name, FileSystemAccessRule Entry)[] cases =
        [
            ("other-rights", new FileSystemAccessRule(
                ownerRights, FileSystemRights.FullControl & ~FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Allow)),
            ("passed-on", new FileSystemAccessRule(
                ownerRights, FileSystemRights.FullControl, contents, PropagationFlags.None, AccessControlType.Allow)),
        ];

        foreach (var (name, entry) in cases)
        {
            var folder = Path.Combine(_folder, name);
            SettingsAccess.MakeSafeForCommand(folder, setsTheFoldersRule: true, out _, out _);
            Assert.True(SettingsAccess.IsAsItWasMade(folder));
            var info = new DirectoryInfo(folder);
            var rule = info.GetAccessControl();
            rule.PurgeAccessRules(ownerRights);
            rule.AddAccessRule(entry);
            info.SetAccessControl(rule);

            Assert.False(SettingsAccess.IsAsItWasMade(folder));
            Assert.False(SettingsAccess.MakeSafeForCommand(folder, setsTheFoldersRule: false, out _, out _));
            Assert.True(SettingsAccess.RestrictionState(folder));
        }
    }

    [Fact]
    public void What_keep_of_10_13_0_made_of_a_new_folder_is_still_one_nobody_chose()
    {
        // 10.13.0 made the folder for a command, left it, and its "keep" at a repair wrote the
        // owner's entry again in the shape of a user's: still nobody's choice. The rule as
        // "keep" writes it for the owner's entry alone, which is how it reads back from a
        // folder (looked at once on a real one); a test run as the owner cannot go on from
        // such a folder, since that entry takes the owner's own rights on it.
        var ownerRights = new SecurityIdentifier(WellKnownSidType.WinCreatorOwnerRightsSid, null);
        var kiosk = new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null);
        const InheritanceFlags contents = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        DirectorySecurity Kept()
        {
            var rule = new DirectorySecurity();
            rule.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            rule.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, contents, PropagationFlags.None, AccessControlType.Allow));
            rule.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, contents, PropagationFlags.None, AccessControlType.Allow));
            rule.AddAccessRule(new FileSystemAccessRule(ownerRights, SettingsAccess.UsersOnTheFolder, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
            rule.AddAccessRule(new FileSystemAccessRule(ownerRights, FileSystemRights.Modify | FileSystemRights.Synchronize, contents, PropagationFlags.InheritOnly, AccessControlType.Allow));
            return rule;
        }

        Assert.True(SettingsAccess.HasTheRuleOfANewFolder(Kept(), kioskGroup: null));

        // With the kiosk group's deny, which the app writes itself; anyone else's is a choice.
        var denied = Kept();
        denied.AddAccessRule(new FileSystemAccessRule(kiosk, FileSystemRights.Write, contents, PropagationFlags.None, AccessControlType.Deny));
        Assert.True(SettingsAccess.HasTheRuleOfANewFolder(denied, kiosk));
        Assert.False(SettingsAccess.HasTheRuleOfANewFolder(denied, kioskGroup: null));

        // One more account in it, or what is above passed down into it: not this rule.
        var withAUser = Kept();
        withAUser.AddAccessRule(new FileSystemAccessRule(Me, FileSystemRights.Read, AccessControlType.Allow));
        Assert.False(SettingsAccess.HasTheRuleOfANewFolder(withAUser, kioskGroup: null));
        var open = Kept();
        open.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
        Assert.False(SettingsAccess.HasTheRuleOfANewFolder(open, kioskGroup: null));
    }

    [Fact]
    public void A_folder_is_made_by_one_call_only()
    {
        // Two elevated commands at the same moment: the one that did not make the folder
        // must not take it for one nobody set a rule on.
        var folder = Path.Combine(_folder, "made-once");

        Assert.True(SettingsAccess.CreateForAdministrators(folder));
        Assert.False(SettingsAccess.CreateForAdministrators(folder));
        Assert.True(SettingsAccess.IsAsItWasMade(folder));

        var there = Directory.CreateDirectory(Path.Combine(_folder, "there-already")).FullName;
        Assert.False(SettingsAccess.CreateForAdministrators(there));
    }

    [Fact]
    public void A_folder_whose_rule_lets_nobody_replace_it_keeps_its_rule()
    {
        SettingsAccess.SetFolderRule(_folder, [Me]);
        var before = new DirectoryInfo(_folder).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);

        SettingsAccess.MakeSafe(_folder);

        Assert.Equal(before, new DirectoryInfo(_folder).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
    }

    [Fact]
    public void A_folder_is_anchored_before_anything_is_read_or_written_in_it()
    {
        // An empty folder that was there already, as the installer makes it: what is written
        // into it next (the log) must not land where a link put in its place would point.
        var elsewhere = Directory.CreateTempSubdirectory("hass-agent-elsewhere-").FullName;
        try
        {
            SettingsAccess.MakeSafe(_folder);

            Assert.Single(Anchors());
            Assert.Equal(ErrorDirectoryNotEmpty, SetJunction(_folder, elsewhere));

            // Again, and the anchor is a new one; there is still only one.
            var first = Anchors()[0];
            SettingsAccess.MakeSafe(_folder);
            Assert.NotEqual(first, Assert.Single(Anchors()));
        }
        finally
        {
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    [Fact]
    public void Another_user_s_folder_is_told_from_one_of_our_own()
    {
        // The test's own folder is its own.
        SettingsAccess.MakeSafe(_folder, out var putAside);
        Assert.Null(putAside);

        var file = Path.Combine(_folder, "settings.json");
        File.WriteAllText(file, "{}");
        Assert.True(SettingsAccess.IsOwnOrAdministrators(_folder, file));
        Assert.False(SettingsAccess.IsOwnOrAdministrators(_folder, Path.Combine(_folder, "missing.json")));
    }

    [Fact]
    public void A_file_with_another_name_elsewhere_is_not_taken_for_settings_to_start_from()
    {
        var outside = Directory.CreateTempSubdirectory("hass-agent-outside-").FullName;
        try
        {
            var target = Path.Combine(outside, "target.txt");
            File.WriteAllText(target, "x");
            var link = Path.Combine(_folder, "settings.json");
            Assert.True(CreateHardLink(link, target, IntPtr.Zero), $"CreateHardLink failed: {Marshal.GetLastWin32Error()}");

            Assert.False(SettingsAccess.IsOwnOrAdministrators(_folder, link));
            Assert.Null(AppPaths.LegacySource(_folder, link, Path.Combine(_folder, "no-user-file.json")));
            File.Delete(link);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Settings_of_an_earlier_version_are_taken_from_the_pc_first_then_from_the_user()
    {
        var pc = Directory.CreateDirectory(Path.Combine(_folder, "pc")).FullName;
        var pcFile = Path.Combine(pc, "settings.json");
        var userFile = Path.Combine(_folder, "user-settings.json");

        Assert.Null(AppPaths.LegacySource(pc, pcFile, userFile));
        File.WriteAllText(userFile, "{}");
        Assert.Equal(userFile, AppPaths.LegacySource(pc, pcFile, userFile));
        File.WriteAllText(pcFile, "{}");
        Assert.Equal(pcFile, AppPaths.LegacySource(pc, pcFile, userFile));
    }

    [Fact]
    public void A_folder_someone_else_made_is_put_aside_and_a_new_one_made_in_its_place()
    {
        Assert.SkipUnless(IsElevated, "Giving a folder to another user takes an administrator.");
        var folder = Path.Combine(_folder, "theirs");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "settings.json"), """{"mqttHost":"their.broker"}""");
        GiveTo(folder, Someone);
        // What its maker kept from before: a handle opened with the right to change the
        // folder's rule. Windows checks that right when the handle is opened, not when it is used.
        using var kept = CreateFile(folder, ReadControl | WriteDac, 7, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        Assert.False(kept.IsInvalid);

        SettingsAccess.MakeSafe(folder, out var putAside);

        Assert.NotNull(putAside);
        Assert.StartsWith(folder + ".made-by-another-user-", putAside);
        // Nothing of theirs is in the new folder; it is all still there, in the old one.
        Assert.True(SettingsAccess.IsAnchorName(Path.GetFileName(Assert.Single(Directory.GetFileSystemEntries(folder)))));
        Assert.Equal("""{"mqttHost":"their.broker"}""", File.ReadAllText(Path.Combine(putAside, "settings.json")));
        Assert.Equal(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), OwnerOf(folder));

        // Through the handle from before they give themselves the folder again: the one put aside.
        var theirs = new RawSecurityDescriptor($"D:P(A;OICI;FA;;;{Someone})(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)");
        var bytes = new byte[theirs.BinaryLength];
        theirs.GetBinaryForm(bytes, 0);
        Assert.True(SetKernelObjectSecurity(kept, 0x4, bytes), $"SetKernelObjectSecurity failed: {Marshal.GetLastWin32Error()}");
        Assert.Contains(Someone, WhoIsNamedOn(putAside));
        Assert.DoesNotContain(Someone, WhoIsNamedOn(folder));

        // The new one is ours: nothing is put aside a second time.
        SettingsAccess.MakeSafe(folder, out putAside);
        Assert.Null(putAside);
    }

    [Fact]
    public void A_folder_someone_else_made_that_is_in_use_stops_it_and_the_message_says_so()
    {
        Assert.SkipUnless(IsElevated, "Giving a folder to another user takes an administrator.");
        var folder = Path.Combine(_folder, "theirs");
        Directory.CreateDirectory(folder);
        GiveTo(folder, Someone);

        // Held open, to list it, without letting anyone rename it. (A handle that asks for no
        // more than to read or change the rule holds nothing: sharing is not checked for it.)
        using (var held = CreateFile(folder, ReadControl | FileListDirectory, 3, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero))
        {
            Assert.False(held.IsInvalid);
            var ex = Assert.Throws<IOException>(() => SettingsAccess.MakeSafe(folder));
            Assert.Contains("could not be put aside", ex.Message);
            // Left as it was: not taken over, nothing made in it.
            Assert.Equal(Someone, OwnerOf(folder));
            Assert.Empty(Directory.GetFileSystemEntries(folder));
        }

        SettingsAccess.MakeSafe(folder, out var putAside);
        Assert.NotNull(putAside);
    }

    [Fact]
    public void A_folder_an_administrator_s_own_account_made_stays_and_becomes_the_administrators()
    {
        // An administrator's app runs without elevation: the folder it makes is theirs by name,
        // not the Administrators group's. A silent update runs as SYSTEM and must not take it
        // for a stranger's and lose the settings. The built-in Administrator stands for such
        // an account here: an administrator who is not the one running the test.
        Assert.SkipUnless(IsElevated, "Giving a folder to another user takes an administrator.");
        Assert.SkipUnless(
            Me.AccountDomainSid is not null && string.Equals(Environment.UserDomainName, Environment.MachineName, StringComparison.OrdinalIgnoreCase),
            "The account running the tests is not a local one.");
        var administrator = new SecurityIdentifier(WellKnownSidType.AccountAdministratorSid, Me.AccountDomainSid);
        var folder = Path.Combine(_folder, "an-administrators");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "settings.json"), """{"deviceName":"kept"}""");
        GiveTo(folder, administrator);
        Assert.True(SettingsAccess.IsOfThePc(folder));

        SettingsAccess.MakeSafe(folder, out var putAside);

        Assert.Null(putAside);
        Assert.Equal(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), OwnerOf(folder));
        Assert.Equal("""{"deviceName":"kept"}""", File.ReadAllText(Path.Combine(folder, "settings.json")));
        Assert.Empty(Directory.GetDirectories(_folder, "an-administrators.made-by-another-user-*"));
    }

    [Fact]
    public void An_elevated_command_leaves_the_folder_owned_by_the_administrators_also_when_it_was_its_own_user_s()
    {
        // Whoever looks at the folder later (the service, an update as SYSTEM) then need not
        // find out whether its owner is an administrator.
        Assert.SkipUnless(IsElevated, "Without elevation a folder of the user's own stays theirs.");
        var folder = Path.Combine(_folder, "my-own");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "settings.json"), """{"deviceName":"kept"}""");
        GiveTo(folder, Me);

        SettingsAccess.MakeSafe(folder, out var putAside);

        Assert.Null(putAside);
        Assert.Equal(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), OwnerOf(folder));
        Assert.Equal("""{"deviceName":"kept"}""", File.ReadAllText(Path.Combine(folder, "settings.json")));
    }

    [Fact]
    public void Without_elevation_a_folder_of_the_user_s_own_stays_theirs()
    {
        Assert.SkipWhen(IsElevated, "For a run without an administrator's rights.");

        SettingsAccess.MakeSafe(_folder, out var putAside);

        Assert.Null(putAside);
        Assert.Equal(Me, OwnerOf(_folder));
    }

    [Fact]
    public void The_settings_folder_is_the_pc_s_when_it_is_ours_or_not_there_and_not_when_it_is_a_link()
    {
        Assert.True(SettingsAccess.IsOfThePc(_folder));
        Assert.True(SettingsAccess.IsOfThePc(Path.Combine(_folder, "not-there")));

        var elsewhere = Directory.CreateTempSubdirectory("hass-agent-elsewhere-").FullName;
        var link = Path.Combine(_folder, "a-link");
        try
        {
            Directory.CreateDirectory(link);
            Assert.Equal(0, SetJunction(link, elsewhere));
            Assert.False(SettingsAccess.IsOfThePc(link));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    [Fact]
    public void A_settings_folder_another_user_made_is_not_the_pc_s()
    {
        // What the service and the app ask before they write or read anything there.
        Assert.SkipUnless(IsElevated, "Giving a folder to another user takes an administrator.");
        var folder = Path.Combine(_folder, "theirs");
        Directory.CreateDirectory(folder);
        GiveTo(folder, Someone);

        Assert.False(SettingsAccess.IsOfThePc(folder));
    }

    [Fact]
    public void A_link_put_under_a_subfolder_s_name_while_it_is_being_done_leads_nowhere()
    {
        // The users may rename a subfolder of theirs and put a link under its name. Done while
        // the rule is being set on what was in it, the names listed before would be opened
        // where the link points, and a file there would get the settings' rule.
        var outside = Directory.CreateTempSubdirectory("hass-agent-outside-").FullName;
        var sub = Path.Combine(_folder, "a");
        try
        {
            var victim = Path.Combine(outside, "victim.txt");
            File.WriteAllText(victim, "not the settings'");
            var before = RuleOf(victim);

            Directory.CreateDirectory(sub);
            for (var i = 0; i < 3000; i++)
            {
                File.WriteAllText(Path.Combine(sub, $"{i:000000}.pad"), string.Empty);
            }

            File.WriteAllText(Path.Combine(sub, "victim.txt"), "a file of that name");
            // A rule of its own on the first file: it goes when the folder's rule reaches it.
            var first = new FileInfo(Path.Combine(sub, "000000.pad"));
            var security = first.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
            first.SetAccessControl(security);
            var marked = RuleOf(first.FullName);

            var swapped = false;
            var swapper = new Thread(() =>
            {
                var until = Environment.TickCount64 + 60_000;
                while (RuleOf(first.FullName) == marked)
                {
                    if (Environment.TickCount64 > until)
                    {
                        return;
                    }
                }

                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        Directory.Move(sub, sub + "-real");
                        break;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 100_000)
                    {
                    }
                }

                Directory.CreateDirectory(sub);
                swapped = SetJunction(sub, outside) == 0;
            });
            swapper.Start();

            try
            {
                SettingsAccess.SetFolderRule(_folder, [Me]);
            }
            catch (IOException)
            {
                // What could no longer be opened is reported; not what this is about.
            }

            swapper.Join();
            Assert.True(swapped, "The subfolder was not swapped for a link; the test proves nothing.");
            Assert.Equal(before, RuleOf(victim));
        }
        finally
        {
            if (Directory.Exists(sub) && new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(sub);
            }

            Directory.Delete(outside, recursive: true);
        }
    }

    private static string RuleOf(string file) =>
        new FileInfo(file).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);

    private static readonly SecurityIdentifier Someone = new("S-1-5-21-1-2-3-1001");

    private static bool IsElevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    private static SecurityIdentifier? OwnerOf(string folder) =>
        new DirectoryInfo(folder).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;

    private static List<SecurityIdentifier> WhoIsNamedOn(string folder) =>
        new DirectoryInfo(folder).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Select(rule => (SecurityIdentifier)rule.IdentityReference).ToList();

    // Another user's, the way a folder they made in ProgramData is theirs.
    private static void GiveTo(string folder, SecurityIdentifier owner)
    {
        using (HASS.Agent.Companion.Security.TokenPrivileges.Enable(
            HASS.Agent.Companion.Security.TokenPrivileges.Restore, HASS.Agent.Companion.Security.TokenPrivileges.TakeOwnership))
        {
            var info = new DirectoryInfo(folder);
            var security = info.GetAccessControl();
            security.SetOwner(owner);
            info.SetAccessControl(security);
        }

        Assert.Equal(owner, OwnerOf(folder));
    }

    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint FileListDirectory = 0x0001;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetKernelObjectSecurity(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint information, byte[] descriptor);

    private const int ErrorDirectoryNotEmpty = 145;

    // Makes an existing folder a junction, the way a user of the folder would: with no more
    // than the right to create files in it. 0, or the Windows error.
    private static int SetJunction(string folder, string target)
    {
        using var handle = CreateFile(folder, FileWriteData, 7, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return Marshal.GetLastWin32Error();
        }

        var name = System.Text.Encoding.Unicode.GetBytes(@"\??\" + target);
        var buffer = new byte[8 + 8 + name.Length + 2 + 2];
        BitConverter.GetBytes(MountPointTag).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)(8 + name.Length + 4)).CopyTo(buffer, 4);
        BitConverter.GetBytes((ushort)name.Length).CopyTo(buffer, 10);
        BitConverter.GetBytes((ushort)(name.Length + 2)).CopyTo(buffer, 12);
        name.CopyTo(buffer, 16);
        return DeviceIoControl(handle, SetReparsePoint, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero) ? 0 : Marshal.GetLastWin32Error();
    }

    private const uint FileWriteData = 0x0002;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint MountPointTag = 0xA0000003;
    private const uint SetReparsePoint = 0x000900A4;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
        string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        Microsoft.Win32.SafeHandles.SafeFileHandle device, uint code, byte[] input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);

    [Fact]
    public void Local_users_include_the_one_running_this()
    {
        // The list is of local accounts; one from a domain or Entra ID is not in it by design.
        Assert.SkipUnless(
            string.Equals(Environment.UserDomainName, Environment.MachineName, StringComparison.OrdinalIgnoreCase),
            "The account running the tests is not a local one.");

        Assert.Contains(Environment.UserName, SettingsAccess.LocalUsers(), StringComparer.OrdinalIgnoreCase);
    }
}
