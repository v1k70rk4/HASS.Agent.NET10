using System.Security.AccessControl;
using System.Security.Principal;
using System.Xml.Linq;
using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.Runtime;

namespace HASS.Agent.Companion.Tests;

/// <summary>
/// Kiosk mode: the policy, the folder rule on a folder of the test's own (run without
/// elevation, the test is the user the rule is set for), and the kiosk task's definition.
/// Writing the policy next to the program, the event source and the task itself need an
/// administrator, so they are tried by hand (RV-NOTE).
/// </summary>
public class KioskTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("hass-agent-kiosk-").FullName;

    private static SecurityIdentifier Me => WindowsIdentity.GetCurrent().User!;

    private static readonly SecurityIdentifier Someone = new("S-1-5-21-1-2-3-1001");

    public void Dispose()
    {
        // The full rule back first: a kiosk rule leaves the test unable to delete what it made.
        SettingsAccess.SetFolderRule(_folder, [Me]);
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void Without_the_file_kiosk_mode_is_off()
    {
        Assert.Null(KioskPolicy.Load(Path.Combine(_folder, KioskPolicy.FileName)));
    }

    [Fact]
    public void A_broken_file_keeps_kiosk_mode_on_with_nobody_named()
    {
        var path = Path.Combine(_folder, KioskPolicy.FileName);
        File.WriteAllText(path, "{ not json");

        var policy = KioskPolicy.Load(path);

        Assert.NotNull(policy);
        Assert.Empty(policy.FullAppSids());
        Assert.True(policy.IsKioskUser(Me, administrator: false));
    }

    [Fact]
    public void The_policy_is_read_back_as_written()
    {
        var path = Path.Combine(_folder, KioskPolicy.FileName);
        new KioskPolicy { FullApp = [Me.Value] }.Save(path);

        Assert.Equal([Me], KioskPolicy.Load(path)!.FullAppSids());
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Theory]
    [InlineData(true, false, false)]   // named
    [InlineData(false, true, false)]   // an administrator, named or not
    [InlineData(false, false, true)]   // anyone else
    public void Who_gets_the_kiosk_tray(bool named, bool administrator, bool kiosk)
    {
        var policy = new KioskPolicy { FullApp = named ? [Me.Value] : [Someone.Value] };

        Assert.Equal(kiosk, policy.IsKioskUser(Me, administrator));
    }

    [Fact]
    public void An_entry_that_is_not_a_sid_names_nobody()
    {
        var policy = new KioskPolicy { FullApp = ["Administrator", Me.Value, ""] };

        Assert.Equal([Me], policy.FullAppSids());
        Assert.True(policy.IsKioskUser(null, administrator: false));
    }

    [Fact]
    public void A_kiosk_user_can_read_the_settings_but_not_change_replace_or_delete_them()
    {
        var file = Path.Combine(_folder, "settings.json");
        File.WriteAllText(file, """{"a":1}""");

        SettingsAccess.SetFolderRule(_folder, [Me], kioskFullApp: [Someone]);

        Assert.Equal("""{"a":1}""", File.ReadAllText(file));
        Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(file, "{}"));
        Assert.Throws<UnauthorizedAccessException>(() => File.Delete(file));
        Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(file + ".tmp", "{}"));
        Assert.True(SettingsAccess.CurrentUserCanUse(_folder));
    }

    [Fact]
    public void A_user_who_keeps_the_full_app_can_still_save()
    {
        SettingsAccess.SetFolderRule(_folder, [Someone], kioskFullApp: [Me]);
        var file = Path.Combine(_folder, "settings.json");
        File.WriteAllText(file, "{}");

        // What SettingsStore does: a new file next to it, then replaced with a backup.
        File.WriteAllText(file + ".tmp", """{"a":1}""");
        File.Replace(file + ".tmp", file, file + ".bak");

        Assert.Equal("""{"a":1}""", File.ReadAllText(file));
    }

    [Fact]
    public void The_kiosk_users_only_read_on_the_folder_and_in_it()
    {
        SettingsAccess.SetFolderRule(_folder, [Someone], kioskFullApp: [Me]);

        var rules = new DirectoryInfo(_folder).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => (SecurityIdentifier)rule.IdentityReference == Someone)
            .ToList();

        // The same rights on the folder and on what is in it: Windows keeps them as one rule.
        Assert.NotEmpty(rules);
        Assert.All(rules, rule => Assert.Equal(SettingsAccess.KioskUsersOnTheFolder, rule.FileSystemRights));
        Assert.Contains(rules, rule => rule.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit));
        Assert.Contains(rules, rule => !rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly));
    }

    [Fact]
    public void Settings_that_cannot_be_written_back_are_still_loaded()
    {
        var paths = new AppPaths(_folder, Path.Combine(_folder, "settings.json"), Path.Combine(_folder, "log"),
            Path.Combine(_folder, "device-id"), []);
        // Something the load normalizes, so it would write the file back.
        File.WriteAllText(paths.SettingsFile, """{"deviceName":"KIOSK-PC","port":0}""");
        File.WriteAllText(paths.DeviceIdFile, "serial-1");
        SettingsAccess.SetFolderRule(_folder, [Me], kioskFullApp: [Someone]);
        using var log = new FileLog(Path.Combine(Path.GetTempPath(), "hass-agent-tests.log"));

        var settings = SettingsStore.LoadOrCreate(paths, log);

        Assert.Equal("KIOSK-PC", settings.DeviceName);
        Assert.Equal("""{"deviceName":"KIOSK-PC","port":0}""", File.ReadAllText(paths.SettingsFile));
    }

    [Theory]
    [InlineData("10.10.2", true)]
    [InlineData("10.11.0-beta.1", true)]
    [InlineData("10.11.0-beta.1+b89999e", true)]
    [InlineData("", false)]
    [InlineData("10.10", false)]
    [InlineData("10.10.2 **click here**", false)]
    [InlineData("10.10.2\n[link](http://example.com)", false)]
    public void The_record_every_user_may_write_is_used_only_when_it_is_a_version(string text, bool used)
    {
        // A kiosk user writes it too, and it ends up in a notification in Home Assistant.
        Assert.Equal(used, LastRunRecord.IsVersion(text));
    }

    [Fact]
    public void The_kiosk_task_runs_for_every_user_at_logon_when_the_app_closes_and_every_minute()
    {
        var xml = XDocument.Parse(KioskWatchdog.TaskXml(@"C:\Program Files\A & B\HASS.Agent.NET10.exe", new DateTime(2026, 10, 29, 12, 0, 0)));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        var triggers = xml.Root!.Element(ns + "Triggers")!.Elements().Select(trigger => trigger.Name.LocalName).ToList();
        Assert.Equal(["LogonTrigger", "EventTrigger", "TimeTrigger"], triggers);
        Assert.Equal("PT1M", xml.Descendants(ns + "Interval").Single().Value);
        Assert.Equal("S-1-5-32-545", xml.Descendants(ns + "GroupId").Single().Value);
        Assert.Equal("LeastPrivilege", xml.Descendants(ns + "RunLevel").Single().Value);
        Assert.Equal("\"C:\\Program Files\\A & B\\HASS.Agent.NET10.exe\"", xml.Descendants(ns + "Command").Single().Value);
        Assert.Equal("--kiosk-start", xml.Descendants(ns + "Arguments").Single().Value);

        // The subscription is a query of its own, inside the task's XML.
        var query = XDocument.Parse(xml.Descendants(ns + "Subscription").Single().Value);
        var selects = query.Descendants("Select").Select(select => select.Value).ToList();
        Assert.Contains(selects, select => select.Contains($"@Name='{KioskEvents.Source}'") && select.Contains($"EventID={KioskEvents.Closed}"));
        Assert.Contains(selects, select => select.Contains("@Name='Application Error'") && select.Contains("Data='HASS.Agent.NET10.exe'"));
    }
}
