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

    [Fact]
    public void An_update_keeps_an_open_folder_open()
    {
        SettingsAccess.OpenToEveryone(_folder);

        SettingsAccess.KeepAsIs(_folder);

        Assert.False(SettingsAccess.IsRestricted(_folder));
    }

    [Fact]
    public void A_folder_that_is_not_there_yet_can_be_used()
    {
        Assert.True(SettingsAccess.CurrentUserCanUse(Path.Combine(_folder, "not-yet")));
    }

    [Fact]
    public void Local_users_include_the_one_running_this()
    {
        Assert.Contains(Environment.UserName, SettingsAccess.LocalUsers(), StringComparer.OrdinalIgnoreCase);
    }
}
