using System.Diagnostics;
using HASS.Agent.Companion.Runtime;

namespace HASS.Agent.Companion.Security;

/// <summary>
/// Checks that a downloaded file is the installer of the release it was downloaded for, after
/// <see cref="InstallerSignature"/> has found the publisher's signature on it. The signature
/// says who made the file, not which file it is: every installer the publisher ever signed
/// carries it, and so do the uninstaller and the libraries. Someone with access to the GitHub
/// releases could put an old installer under a new tag, and the PCs would install it without
/// a word, as SYSTEM: back to a version whose faults are known. The file says what it is in
/// its version information, which the signature covers.
/// </summary>
internal static class InstallerIdentity
{
    // What Inno Setup writes for the installer: AppName, and "AppName Setup".
    private const string ProductName = AppIdentity.DisplayName;
    private const string Description = AppIdentity.DisplayName + " Setup";

    public static bool IsTheRelease(string path, string releaseTag, out string reason)
    {
        FileVersionInfo info;
        try
        {
            info = FileVersionInfo.GetVersionInfo(path);
        }
        catch (FileNotFoundException)
        {
            reason = "it is not there";
            return false;
        }

        return IsTheRelease(info.ProductName, info.FileDescription, info.ProductVersion, releaseTag, out reason);
    }

    internal static bool IsTheRelease(string? productName, string? description, string? version, string releaseTag, out string reason)
    {
        // The uninstaller's texts are padded with spaces; the installer's may be too.
        productName = productName?.Trim();
        description = description?.Trim();
        version = version?.Trim();

        if (!string.Equals(productName, ProductName, StringComparison.Ordinal)
            || !string.Equals(description, Description, StringComparison.Ordinal))
        {
            reason = $"it is \"{description}\" of \"{productName}\", not the installer of {ProductName}";
            return false;
        }

        if (string.IsNullOrEmpty(version) || !AppUpdateService.IsSameVersion(version, releaseTag))
        {
            reason = $"it is the installer of version \"{version}\", not of release {releaseTag}";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
