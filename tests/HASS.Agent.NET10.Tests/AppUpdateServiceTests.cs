using HASS.Agent.Companion.Runtime;

namespace HASS.Agent.Companion.Tests;

public class VersionComparisonTests
{
    [Theory]
    [InlineData("v10.9.0", "10.8.0")]
    [InlineData("v10.10.0", "10.9.0")]
    [InlineData("v11.0.0", "10.99.99")]
    [InlineData("v10.9.0", "10.9.0-beta.3")]   // the stable release outranks its betas
    [InlineData("v10.9.0-beta.3", "10.9.0-beta.2")]
    [InlineData("v10.9.0-beta.10", "10.9.0-beta.9")] // numeric, not text order
    [InlineData("v10.9.1-beta.1", "10.9.0")]
    public void Newer_release_is_offered(string latest, string installed)
    {
        Assert.True(AppUpdateService.IsNewerVersion(latest, installed));
    }

    [Theory]
    [InlineData("v10.9.0", "10.9.0")]
    [InlineData("v10.8.0", "10.9.0")]
    [InlineData("v10.9.0-beta.3", "10.9.0")]   // a beta of the installed stable is older
    [InlineData("v10.9.0-beta.2", "10.9.0-beta.3")]
    [InlineData("v10.9.0", "10.9.0+3c05c50fc296eb85")] // build metadata of the installed version
    [InlineData("V10.9.0", "v10.9.0")]
    public void Same_or_older_release_is_not_offered(string latest, string installed)
    {
        Assert.False(AppUpdateService.IsNewerVersion(latest, installed));
    }
}

public class RequestedUpdateCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, false)]      // asked again at once: the last answer is sent
    [InlineData(299, false)]
    [InlineData(300, true)]
    [InlineData(3600, true)]
    public void A_check_asked_for_from_home_assistant_goes_to_github_once_in_five_minutes(int secondsSinceTheLast, bool asksGitHub)
    {
        Assert.Equal(asksGitHub, AppUpdateService.IsRequestedCheckDue(Now.AddSeconds(-secondsSinceTheLast), Now));
    }

    [Fact]
    public void The_first_check_is_always_made()
    {
        Assert.True(AppUpdateService.IsRequestedCheckDue(DateTimeOffset.MinValue, Now));
    }
}

public class InstallerIdentityTests
{
    private const string Product = "HASS.Agent .NET10";
    private const string Setup = "HASS.Agent .NET10 Setup";

    [Theory]
    [InlineData("10.13.0", "v10.13.0")]
    [InlineData("10.13.0-beta.1", "v10.13.0-beta.1")]
    [InlineData("10.13.0-beta.1", "10.13.0-beta.1")]
    [InlineData("10.13.0+3eb2d9de0b474adb", "v10.13.0")]         // build metadata
    [InlineData("10.13.0-beta.1          ", "v10.13.0-beta.1")]  // padded, as Inno Setup pads the uninstaller's
    public void The_installer_of_the_release_is_taken(string fileVersion, string releaseTag)
    {
        Assert.True(Security.InstallerIdentity.IsTheRelease(Product, Setup, fileVersion, releaseTag, out var reason), reason);
    }

    [Theory]
    [InlineData("10.9.0", "v10.13.0")]              // an old installer put under a new tag
    [InlineData("10.13.0-beta.1", "v10.13.0")]      // a beta under the stable release's tag
    [InlineData("10.14.0", "v10.13.0")]
    [InlineData("", "v10.13.0")]
    [InlineData(null, "v10.13.0")]
    public void An_installer_of_another_version_is_refused(string? fileVersion, string releaseTag)
    {
        Assert.False(Security.InstallerIdentity.IsTheRelease(Product, Setup, fileVersion, releaseTag, out var reason));
        Assert.Contains("not of release", reason);
    }

    [Theory]
    [InlineData("HASS.Agent.NET10", "HASS.Agent.NET10")]         // the app itself, signed too
    [InlineData(Product, "Setup/Uninstall")]                     // the uninstaller of the same version
    [InlineData("libsodium (x64)", "The Sodium crypto library (libsodium)")]
    [InlineData(null, null)]
    public void Another_file_the_publisher_signed_is_refused(string? productName, string? description)
    {
        Assert.False(Security.InstallerIdentity.IsTheRelease(productName, description, "10.13.0", "v10.13.0", out var reason));
        Assert.Contains("not the installer", reason);
    }

    [Fact]
    public void A_file_is_asked_what_it_is()
    {
        // This test assembly: a program, with version information, and not the installer.
        Assert.False(Security.InstallerIdentity.IsTheRelease(typeof(InstallerIdentityTests).Assembly.Location, "v10.13.0", out var reason));
        Assert.Contains("not the installer", reason);
    }

    // The same file the signature tests use: CI points HASS_AGENT_SIGNED_INSTALLER at the latest
    // release's installer. This is where what Inno Setup really writes into it is checked.
    [Fact]
    public void The_release_installer_says_it_is_the_installer_of_its_version()
    {
        var installer = Environment.GetEnvironmentVariable("HASS_AGENT_SIGNED_INSTALLER") is { Length: > 0 } path && File.Exists(path) ? path : null;
        Assert.SkipWhen(installer is null, "HASS_AGENT_SIGNED_INSTALLER is not set.");

        var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(installer!).ProductVersion?.Trim();

        Assert.False(string.IsNullOrEmpty(version), "The installer carries no product version.");
        Assert.True(Security.InstallerIdentity.IsTheRelease(installer!, $"v{version}", out var reason), reason);
        Assert.False(Security.InstallerIdentity.IsTheRelease(installer!, "v999.0.0", out _));
    }
}

public class ReleaseAssetTests
{
    private static AppUpdateService.GitHubReleaseAsset Asset(string name) =>
        new(name, $"https://github.com/v1k70rk4/HASS.Agent.NET10/releases/download/v10.9.0/{name}");

    [Fact]
    public void Installer_is_preferred_over_the_zip_and_the_signatures()
    {
        var picked = AppUpdateService.SelectReleaseAsset(
        [
            Asset("HASS.Agent.NET10-win-x64-10.9.0.zip"),
            Asset("HASS.Agent.NET10-Setup-10.9.0.exe.sigstore.json"),
            Asset("HASS.Agent.NET10-win-x64-10.9.0.zip.sigstore.json"),
            Asset("HASS.Agent.NET10-Setup-10.9.0.exe"),
        ]);

        Assert.Equal("HASS.Agent.NET10-Setup-10.9.0.exe", picked?.Name);
    }

    [Fact]
    public void Signature_files_alone_are_never_picked()
    {
        var picked = AppUpdateService.SelectReleaseAsset(
        [
            Asset("HASS.Agent.NET10-Setup-10.9.0.exe.sigstore.json"),
            Asset("checksums.txt"),
        ]);

        Assert.Null(picked);
    }

    [Fact]
    public void Assets_of_another_program_are_ignored()
    {
        var picked = AppUpdateService.SelectReleaseAsset([Asset("HASS.Agent.Installer.exe")]);

        Assert.Null(picked);
    }

    [Theory]
    [InlineData("HASS.Agent.NET10-Setup-10.9.0.exe", "HASS.Agent.NET10-Setup-10.9.0.exe")]
    [InlineData("..\\..\\Windows\\evil.exe", ".._.._Windows_evil.exe")]
    [InlineData("a/b:c*d?.exe", "a_b_c_d_.exe")]
    public void Asset_name_cannot_leave_the_download_folder(string name, string expected)
    {
        Assert.Equal(expected, AppUpdateService.SanitizeFileName(name));
    }
}

public class ReleaseNotesTests
{
    [Fact]
    public void Hard_wrapped_lines_of_a_paragraph_are_joined()
    {
        var notes = AppUpdateService.SummarizeReleaseNotes(
            "Fixed: a message could get lost on the HA API when two were\nsent at the same moment.");

        Assert.Equal("Fixed: a message could get lost on the HA API when two were sent at the same moment.", notes);
    }

    [Fact]
    public void Title_line_with_the_app_name_is_left_out()
    {
        var notes = AppUpdateService.SummarizeReleaseNotes(
            $"{AppIdentity.DisplayName} 10.9.0\n\nA bigger release.");

        Assert.Equal("A bigger release.", notes);
    }

    [Fact]
    public void Bullets_stay_on_lines_of_their_own_and_lose_their_markdown()
    {
        var notes = AppUpdateService.SummarizeReleaseNotes(
            "**Notifications**\n\n- **Pictures** in `image`.\n- Text fields\n  that wrap.");

        Assert.Equal("Notifications\n\n• Pictures in image.\n• Text fields that wrap.", notes);
    }

    [Fact]
    public void Long_notes_are_cut_at_a_paragraph()
    {
        var paragraph = new string('x', 600);
        var notes = AppUpdateService.SummarizeReleaseNotes($"{paragraph}\n\n{paragraph}\n\n{paragraph}", maxLength: 1400);

        Assert.Equal($"{paragraph}\n\n{paragraph}\n\n…", notes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void Empty_notes_give_nothing(string? markdown)
    {
        Assert.Equal(string.Empty, AppUpdateService.SummarizeReleaseNotes(markdown));
    }
}

public class UpdateCheckErrorTests
{
    [Fact]
    public void An_empty_answer_from_github_points_to_its_status_page()
    {
        Assert.Contains("githubstatus.com", AppUpdateService.Explain(AppUpdateService.NoRelease));
        // Any other error is shown as it is.
        Assert.Equal("The operation was canceled.", AppUpdateService.Explain("The operation was canceled."));
        Assert.Equal(string.Empty, AppUpdateService.Explain(null));
    }
}
