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
    public void Another_user_s_folder_is_told_from_one_of_our_own()
    {
        // The test's own folder is its own; whose a folder is that cannot be looked at is not known.
        Assert.False(SettingsAccess.WasMadeBySomeoneElse(_folder));
        Assert.False(SettingsAccess.WasMadeBySomeoneElse(Path.Combine(_folder, "not-there")));

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
    public void What_another_user_left_in_the_folder_is_put_aside_not_used_and_not_deleted()
    {
        File.WriteAllText(Path.Combine(_folder, "settings.json"), """{"mqttHost":"their.broker"}""");
        File.WriteAllText(Path.Combine(_folder, "device-id"), "their-serial");
        Directory.CreateDirectory(Path.Combine(_folder, "sendspin"));
        File.WriteAllText(Path.Combine(_folder, "sendspin", "pairing.bin"), "their-pairing");
        var log = Path.Combine(_folder, "hass-agent-net10.log");
        File.WriteAllText(log, "line");
        SettingsAccess.SetFolderRule(_folder, [Me]);

        var aside = SettingsAccess.SetAside(_folder, keep: log);

        Assert.Equal(["device-id", "sendspin", "settings.json"], aside.Order());
        Assert.False(File.Exists(Path.Combine(_folder, "settings.json")));
        Assert.False(File.Exists(Path.Combine(_folder, "device-id")));
        Assert.False(Directory.Exists(Path.Combine(_folder, "sendspin")));
        Assert.True(File.Exists(log));
        Assert.Single(Anchors());
        // Still there, under names the app does not read.
        var kept = Directory.GetFiles(_folder, "settings.json.found-before-install-*");
        Assert.Equal("""{"mqttHost":"their.broker"}""", File.ReadAllText(Assert.Single(kept)));
        Assert.Equal("their-pairing", File.ReadAllText(Path.Combine(Assert.Single(Directory.GetDirectories(_folder, "sendspin.found-before-install-*")), "pairing.bin")));
        // A second time finds nothing left to put aside.
        Assert.Empty(SettingsAccess.SetAside(_folder, keep: log));
    }

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
