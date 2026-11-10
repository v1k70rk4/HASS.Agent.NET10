using System.Security.AccessControl;
using System.Security.Principal;
using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Security;
using HASS.Agent.Companion.SystemCommands;
using HASS.Agent.Companion.SystemService;
using HASS.Agent.Companion.SystemStatus;
using HASS.Agent.Companion.Tray;

namespace HASS.Agent.Companion.Tests;

/// <summary>
/// A program's path typed without quotes, and a program or script that can be changed after
/// it was approved for the service.
/// </summary>
public class ProgramPathTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("hass-agent-paths-").FullName;
    private readonly string _program;

    public ProgramPathTests()
    {
        // What "C:\Program Files\App\app.exe" is in small: a folder with a space in its name.
        _program = Path.Combine(Directory.CreateDirectory(Path.Combine(_folder, "My Tools", "App")).FullName, "app.exe");
        File.WriteAllText(_program, "x");
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    // Two of the tests name a program in a folder without a space. Under a temp folder that
    // has one itself (a user name of two words), every path here has a space above it.
    private void SkipWhenTheTempFolderHasASpace() =>
        Assert.SkipWhen(_folder.Contains(' '), $"The temp folder has a space in its path: {_folder}");

    [Fact]
    public void A_path_with_a_space_and_no_quotes_is_not_taken_for_a_shorter_one()
    {
        SkipWhenTheTempFolderHasASpace();
        // Split at the first space this would be "...\My" with the rest as its arguments,
        // and Windows starts a "...\My.exe" when someone has put one there.
        var typed = $"{_program} /x";

        Assert.True(SystemCommandService.HasUnquotedSpaceInPath(typed));
        Assert.True(SystemCommandService.HasUnquotedSpaceInPath($"{Path.Combine(_folder, "My Tools", "App", "missing.exe")} /x"));
        Assert.Equal(Path.Combine(_folder, "My"), SystemCommandService.SplitProcessCommand(typed).FileName);
    }

    [Fact]
    public void Quoted_or_whole_or_without_a_space_it_is_run_as_before()
    {
        SkipWhenTheTempFolderHasASpace();
        var plain = Path.Combine(Directory.CreateDirectory(Path.Combine(_folder, "Tools")).FullName, "app.exe");
        File.WriteAllText(plain, "x");

        Assert.False(SystemCommandService.HasUnquotedSpaceInPath($"\"{_program}\" /x"));
        Assert.False(SystemCommandService.HasUnquotedSpaceInPath(_program));               // the whole line is the file
        Assert.False(SystemCommandService.HasUnquotedSpaceInPath($"{plain} /x"));
        Assert.False(SystemCommandService.HasUnquotedSpaceInPath($"{plain} \"{_program}\""));  // a path with a space as an argument
        Assert.False(SystemCommandService.HasUnquotedSpaceInPath($"{plain} {_folder}"));
        Assert.False(SystemCommandService.HasUnquotedSpaceInPath(Path.Combine(_folder, "Tools", "app") + " --flag"));
    }

    [Theory]
    [InlineData("taskkill /F /IM notepad.exe /T")]
    [InlineData("shutdown.exe /s /t 0")]
    [InlineData("notepad")]
    [InlineData("")]
    [InlineData(null)]
    public void A_command_by_its_name_is_not_a_path(string? typed)
    {
        Assert.False(SystemCommandService.HasUnquotedSpaceInPath(typed));
    }

    [Fact]
    public async Task Such_a_command_is_not_run()
    {
        // Were the line split and run, this would be what starts.
        var planted = Path.Combine(_folder, "My.cmd");
        var marker = Path.Combine(_folder, "ran.txt");
        File.WriteAllText(planted, $"@echo ran> \"{marker}\"");
        var command = new CustomCommandDefinition { Id = "c", Name = "c", Type = CustomCommandTypes.Process, Command = $"{_program} /x" };

        var result = await SystemCommandService.ExecuteCustomCommandAsync(command);

        Assert.Equal(CustomCommandOutcome.Failed, result.Outcome);
        Assert.Contains("quotes", result.Detail);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void What_windows_installed_cannot_be_changed_without_an_administrator()
    {
        Assert.False(FileTrust.CanBeChangedWithoutAdministrator(Path.Combine(Environment.SystemDirectory, "cmd.exe")));
        // Nothing to say about a file that is not there.
        Assert.False(FileTrust.CanBeChangedWithoutAdministrator(Path.Combine(_folder, "missing.exe")));
    }

    [Fact]
    public void A_file_a_user_may_write_can()
    {
        // Whoever owns it, the rule lets an ordinary user in: as C:\Scripts does by default.
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var info = new FileInfo(_program);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Modify, AccessControlType.Allow));
        info.SetAccessControl(security);

        Assert.True(FileTrust.CanBeChangedWithoutAdministrator(_program));
    }

    [Fact]
    public void The_approval_prompt_warns_about_a_script_that_can_be_changed_afterwards()
    {
        var script = Path.Combine(_folder, "backup.ps1");
        File.WriteAllText(script, "Write-Output ok");
        var info = new FileInfo(script);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Modify, AccessControlType.Allow));
        info.SetAccessControl(security);
        var settings = new CompanionSettings
        {
            CustomCommands =
            [
                new CustomCommandDefinition { Id = "1", Name = "Backup", Type = CustomCommandTypes.PowerShell, Command = script, Service = true },
                new CustomCommandDefinition { Id = "2", Name = "Flush", Type = CustomCommandTypes.Process, Command = Path.Combine(Environment.SystemDirectory, "ipconfig.exe"), Arguments = "/flushdns", Service = true },
                new CustomCommandDefinition { Id = "3", Name = "Typed out", Type = CustomCommandTypes.PowerShell, Command = "Restart-Service Spooler", Service = true },
            ],
        };

        var shown = ServiceApproval.PendingDescriptions(settings, new ServicePolicy());

        Assert.Equal(3, shown.Count);
        Assert.Contains(script, shown[0].Split(Environment.NewLine)[^1]);
        Assert.Equal(3, shown[0].Split(Environment.NewLine).Length);   // the name, what it runs, the warning
        Assert.Equal(2, shown[1].Split(Environment.NewLine).Length);
        Assert.Equal(2, shown[2].Split(Environment.NewLine).Length);
    }

    [Fact]
    public void The_warning_names_the_file_with_nothing_hidden_in_its_path()
    {
        // The path is from the command, which others can write. U+202E in a name has what
        // follows it written from right to left: the rest of the warning's line with it.
        var script = Path.Combine(_folder, "backup\u202E1sp.ps1");
        File.WriteAllText(script, "Write-Output ok");
        var info = new FileInfo(script);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Modify, AccessControlType.Allow));
        info.SetAccessControl(security);
        var settings = new CompanionSettings
        {
            CustomCommands = [new CustomCommandDefinition { Id = "1", Name = "Backup", Type = CustomCommandTypes.PowerShell, Command = script, Service = true }],
        };

        var lines = ServiceApproval.PendingDescriptions(settings, new ServicePolicy()).Single().Split(Environment.NewLine);

        Assert.Equal(3, lines.Length);   // the name, what it runs, the warning
        // Ordinal: compared the way a language does, a character that is not shown counts as
        // nothing, and is found in every text.
        Assert.DoesNotContain("\u202E", lines[^1], StringComparison.Ordinal);
        Assert.Contains(@"backup\u202E1sp.ps1", lines[^1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\Tools\app.exe /x", false, @"C:\Tools\app.exe")]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" /x", false, @"C:\Program Files\App\app.exe")]
    [InlineData("taskkill /F", false, null)]                    // found on the PATH, not named by a path
    [InlineData(@"C:\Scripts\backup.ps1", true, @"C:\Scripts\backup.ps1")]
    [InlineData("\"C:\\My Scripts\\backup.ps1\"", true, @"C:\My Scripts\backup.ps1")]
    [InlineData("Restart-Service Spooler", true, null)]         // a command typed out names no file
    [InlineData(@"scripts\backup.ps1", true, null)]
    [InlineData("", false, null)]
    public void The_file_an_entry_names(string runs, bool script, string? file)
    {
        Assert.Equal(file, ServiceApproval.TargetFile(runs, script));
    }
}
