using HASS.Agent.Companion.Http;
using HASS.Agent.Companion.Security;

namespace HASS.Agent.Companion.Tests;

public class LocalApiKeyTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("Bearer " + Key)]
    [InlineData("bearer " + Key)]
    [InlineData("Bearer   " + Key + "  ")]
    public void Right_key_is_let_in(string header)
    {
        Assert.True(LocalApiServer.IsAuthorized(header, Key));
    }

    [Theory]
    [InlineData("Bearer 0123456789abcdef0123456789abcdee")] // one character off
    [InlineData("Bearer " + Key + "0")]                      // longer
    [InlineData("Bearer 0123")]                              // a prefix of the key
    [InlineData("Bearer ")]
    [InlineData("Basic " + Key)]
    [InlineData(Key)]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_refused(string? header)
    {
        Assert.False(LocalApiServer.IsAuthorized(header, Key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Without_a_key_nothing_is_let_in(string? key)
    {
        Assert.False(LocalApiServer.IsAuthorized("Bearer ", key));
        Assert.False(LocalApiServer.IsAuthorized("Bearer anything", key));
    }
}

public class InstallerSignatureTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("hass-agent-tests-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Unsigned_program_is_refused()
    {
        // This test assembly is built here and never signed.
        var path = Path.Combine(_folder, "unsigned.dll");
        File.Copy(typeof(InstallerSignatureTests).Assembly.Location, path);

        Assert.False(InstallerSignature.IsTrusted(path, out var reason));
        Assert.Contains("no signature", reason);
        Assert.Throws<InvalidOperationException>(() => InstallerSignature.EnsureTrusted(path));
    }

    [Fact]
    public void Something_that_is_not_a_program_is_refused()
    {
        var path = Path.Combine(_folder, "HASS.Agent.NET10-Setup-10.9.1.exe");
        File.WriteAllText(path, "<html>Not Found</html>");

        Assert.False(InstallerSignature.IsTrusted(path, out _));
    }

    [Fact]
    public void Missing_file_is_refused()
    {
        Assert.False(InstallerSignature.IsTrusted(Path.Combine(_folder, "nothing.exe"), out _));
    }

    // The real installers are too big for the repository. CI points HASS_AGENT_SIGNED_INSTALLER at
    // the latest release's installer; locally, point it at one in artifacts\installer (build-exe.ps1).
    private static string? SignedInstaller =>
        Environment.GetEnvironmentVariable("HASS_AGENT_SIGNED_INSTALLER") is { Length: > 0 } path && File.Exists(path) ? path : null;

    [Fact]
    public void Signed_release_installer_is_trusted()
    {
        Assert.SkipWhen(SignedInstaller is null, "HASS_AGENT_SIGNED_INSTALLER is not set.");

        Assert.True(InstallerSignature.IsTrusted(SignedInstaller!, out var reason), reason);
    }

    [Fact]
    public void Release_installer_carries_a_timestamp()
    {
        Assert.SkipWhen(SignedInstaller is null, "HASS_AGENT_SIGNED_INSTALLER is not set.");

        Assert.True(InstallerSignature.HasTimestamp(SignedInstaller!));
    }

    [Fact]
    public void Program_without_a_signature_has_no_timestamp()
    {
        Assert.False(InstallerSignature.HasTimestamp(typeof(InstallerSignatureTests).Assembly.Location));
    }

    [Fact]
    public void Truncated_program_has_no_timestamp()
    {
        Assert.SkipWhen(SignedInstaller is null, "HASS_AGENT_SIGNED_INSTALLER is not set.");

        // The first 4 KB of a signed installer: the headers point at a certificate table
        // that is not there any more.
        var path = Path.Combine(_folder, "truncated.exe");
        using (var source = File.OpenRead(SignedInstaller!))
        using (var target = File.Create(path))
        {
            var head = new byte[4096];
            target.Write(head, 0, source.Read(head, 0, head.Length));
        }

        Assert.False(InstallerSignature.HasTimestamp(path));
        Assert.False(InstallerSignature.IsTrusted(path, out _));
    }

    [Fact]
    public void Signed_installer_changed_by_one_byte_is_refused()
    {
        Assert.SkipWhen(SignedInstaller is null, "HASS_AGENT_SIGNED_INSTALLER is not set.");

        var path = Path.Combine(_folder, "tampered.exe");
        var bytes = File.ReadAllBytes(SignedInstaller!);
        bytes[bytes.Length / 3] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        Assert.False(InstallerSignature.IsTrusted(path, out var reason));
        Assert.Contains("changed after it was signed", reason);
    }
}
