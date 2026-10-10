using System.Security.AccessControl;
using System.Security.Principal;
using HASS.Agent.Companion.Configuration;

namespace HASS.Agent.Companion.Tests;

/// <summary>
/// The folder rules, on a folder of the test's own: setting the rules of a folder you own
/// needs no administrator. Creating the group and changing its members does, so that part is
/// tried by hand (RV-NOTE, mediaserver).
/// </summary>
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
    [InlineData(true, 3, 1, "LeaveAsItIs")]       // already limited
    [InlineData(true, 1, 0, "LeaveAsItIs")]
    [InlineData(false, 1, 0, "LimitToTheUsers")]  // administrators only
    [InlineData(false, 2, 0, "LimitToTheUsers")]
    [InlineData(false, 2, 1, "OpenToEveryone")]   // someone else's choice
    [InlineData(false, 0, 0, "OpenToEveryone")]   // no users found: as before
    public void What_an_update_does_with_the_folder(bool restricted, int users, int nonAdministrators, string expected)
    {
        Assert.Equal(expected, SettingsAccess.OnUpdate(restricted, users, nonAdministrators).ToString());
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

    [Fact]
    public void Local_users_include_the_one_running_this()
    {
        Assert.Contains(Environment.UserName, SettingsAccess.LocalUsers(), StringComparer.OrdinalIgnoreCase);
    }
}
