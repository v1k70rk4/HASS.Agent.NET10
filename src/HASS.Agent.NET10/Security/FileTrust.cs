using System.Security.AccessControl;
using System.Security.Principal;

namespace HASS.Agent.Companion.Security;

/// <summary>
/// Whether a program or a script can be changed without an administrator's rights. The service
/// policy approves a command by its text: a script's path, not what the script says. A script
/// in a folder every user may write (C:\Scripts, made in the root of the drive, is one by
/// default) stays approved whatever is put into it afterwards, and the service runs it as
/// SYSTEM. The approval prompt says so when it sees it.
/// </summary>
internal static class FileTrust
{
    private static readonly SecurityIdentifier[] Trusted =
    [
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        // TrustedInstaller, the owner of what Windows itself installed.
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"),
    ];

    // Changing the file, removing it, or taking the right to.
    private const FileSystemRights OnTheFile =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    // On its folder, also putting another file there (in its place, or a library beside it).
    private const FileSystemRights OnTheFolder = OnTheFile | FileSystemRights.DeleteSubdirectoriesAndFiles;

    /// <summary>
    /// True when someone who is neither SYSTEM nor an administrator owns the file or its folder,
    /// or may change them. False for a file that is not there or cannot be looked at: there is
    /// nothing to say about it.
    /// </summary>
    public static bool CanBeChangedWithoutAdministrator(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                return false;
            }

            return IsOpen(file.GetAccessControl(), OnTheFile)
                || (file.Directory is { } folder && IsOpen(folder.GetAccessControl(), OnTheFolder));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    internal static bool IsOpen(FileSystemSecurity security, FileSystemRights rights)
    {
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !Trusted.Contains(owner))
        {
            return true;
        }

        return security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Any(rule => rule.AccessControlType == AccessControlType.Allow
                // A rule only for what is made in the folder later says nothing about the folder itself.
                && !rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)
                && (rule.FileSystemRights & rights) != 0
                && (rule.IdentityReference is not SecurityIdentifier sid || !Trusted.Contains(sid)));
    }
}
