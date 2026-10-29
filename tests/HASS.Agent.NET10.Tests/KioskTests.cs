using System.Security.AccessControl;
using System.Security.Principal;
using System.Xml.Linq;
using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Logging;
using HASS.Agent.Companion.Runtime;

namespace HASS.Agent.Companion.Tests;

/// <summary>
/// Kiosk mode: who gets the kiosk tray, the folder rule on a folder of the test's own (run
/// without elevation; the test's own user stands in for the kiosk group where the deny is
/// tried), and the kiosk task's definition. The group, the event source and the task itself
/// need an administrator, so they are tried by hand (mediaserver).
/// </summary>
public class KioskTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("hass-agent-kiosk-").FullName;

    private static SecurityIdentifier Me => WindowsIdentity.GetCurrent().User!;

    private static readonly SecurityIdentifier Someone = new("S-1-5-21-1-2-3-1001");

    public void Dispose()
    {
        // The rule without the deny first: a kiosk rule leaves the test unable to delete what it made.
        SettingsAccess.SetFolderRule(_folder, [Me]);
        Directory.Delete(_folder, recursive: true);
    }

    [Theory]
    [InlineData(true, false, true)]    // in the group
    [InlineData(true, true, false)]    // an administrator, in the group or not
    [InlineData(false, false, false)]  // anyone else
    public void Who_gets_the_kiosk_tray(bool inGroup, bool administrator, bool kiosk)
    {
        Assert.Equal(kiosk, KioskMode.IsKioskUser(inGroup, administrator));
    }

    [Fact]
    public void A_kiosk_user_can_read_the_settings_but_not_change_replace_or_delete_them()
    {
        var file = Path.Combine(_folder, "settings.json");
        File.WriteAllText(file, """{"a":1}""");

        SettingsAccess.SetFolderRule(_folder, [Me], kioskGroup: Me);

        Assert.Equal("""{"a":1}""", File.ReadAllText(file));
        Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(file, "{}"));
        Assert.Throws<UnauthorizedAccessException>(() => File.Delete(file));
        Assert.Throws<UnauthorizedAccessException>(() => File.WriteAllText(file + ".tmp", "{}"));
        Assert.Throws<UnauthorizedAccessException>(() => Directory.CreateDirectory(Path.Combine(_folder, "sub")));
        Assert.True(SettingsAccess.CurrentUserCanUse(_folder));
    }

    [Fact]
    public void Everyone_else_can_still_save()
    {
        SettingsAccess.SetFolderRule(_folder, [Me], kioskGroup: Someone);
        var file = Path.Combine(_folder, "settings.json");
        File.WriteAllText(file, "{}");

        // What SettingsStore does: a new file next to it, then replaced with a backup.
        File.WriteAllText(file + ".tmp", """{"a":1}""");
        File.Replace(file + ".tmp", file, file + ".bak");

        Assert.Equal("""{"a":1}""", File.ReadAllText(file));
    }

    [Fact]
    public void The_kiosk_group_is_denied_on_the_folder_and_on_what_is_in_it()
    {
        var file = Path.Combine(_folder, "settings.json");
        File.WriteAllText(file, "{}");

        SettingsAccess.SetFolderRule(_folder, [Me], kioskGroup: Someone);

        var onFolder = new DirectoryInfo(_folder).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Single(rule => (SecurityIdentifier)rule.IdentityReference == Someone);
        Assert.Equal(AccessControlType.Deny, onFolder.AccessControlType);
        Assert.Equal(SettingsAccess.KioskDenied, onFolder.FileSystemRights);
        Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, onFolder.InheritanceFlags);
        Assert.Equal(PropagationFlags.None, onFolder.PropagationFlags);

        var onFile = new FileInfo(file).GetAccessControl()
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToList();
        // Inherited, and first: a deny is read before every allow.
        Assert.Equal(Someone, onFile[0].IdentityReference);
        Assert.Equal(AccessControlType.Deny, onFile[0].AccessControlType);
        Assert.True(onFile[0].IsInherited);
    }

    [Fact]
    public void Without_kiosk_mode_the_folder_has_no_deny()
    {
        SettingsAccess.SetFolderRule(_folder, [Me]);

        Assert.DoesNotContain(
            new DirectoryInfo(_folder).GetAccessControl()
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>(),
            rule => rule.AccessControlType == AccessControlType.Deny);
    }

    [Fact]
    public void Settings_that_cannot_be_written_back_are_still_loaded()
    {
        var paths = new AppPaths(_folder, Path.Combine(_folder, "settings.json"), Path.Combine(_folder, "log"),
            Path.Combine(_folder, "device-id"), []);
        // Something the load normalizes, so it would write the file back.
        File.WriteAllText(paths.SettingsFile, """{"deviceName":"KIOSK-PC","port":0}""");
        File.WriteAllText(paths.DeviceIdFile, "serial-1");
        SettingsAccess.SetFolderRule(_folder, [Me], kioskGroup: Me);
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
    public void The_kiosk_task_runs_for_the_kiosk_group_at_logon_when_the_app_closes_and_every_minute()
    {
        var group = new SecurityIdentifier("S-1-5-21-1-2-3-1009");
        var xml = XDocument.Parse(KioskWatchdog.TaskXml(@"C:\Program Files\A & B\HASS.Agent.NET10.exe", group, new DateTime(2026, 10, 29, 12, 0, 0)));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        var triggers = xml.Root!.Element(ns + "Triggers")!.Elements().Select(trigger => trigger.Name.LocalName).ToList();
        Assert.Equal(["LogonTrigger", "EventTrigger", "TimeTrigger"], triggers);
        Assert.Equal("PT1M", xml.Descendants(ns + "Interval").Single().Value);
        Assert.Equal(group.Value, xml.Descendants(ns + "GroupId").Single().Value);
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
