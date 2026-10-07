using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HASS.Agent.Companion.Security;

/// <summary>
/// Checks a downloaded update installer before anything runs it. The installer is started
/// elevated, by the service even as SYSTEM, so a file that is not exactly what the publisher
/// signed must never get that far: not a corrupted download, not a release asset replaced by
/// someone with access to the GitHub account. Windows checks the Authenticode signature (the
/// file is unchanged since it was signed, by a certificate chaining to a trusted root, with a
/// valid timestamp), and the signer must be the publisher of the releases. The publisher is
/// checked by name and issuing CA, not by thumbprint, so that a renewed certificate of the same
/// publisher keeps updates working for every installed version.
/// </summary>
internal static class InstallerSignature
{
    // README, Code Signing: every release since 10.6.8.
    private const string PublisherName = "Open Source Developer Viktor Révész";
    private const string IssuerName = "Certum Code Signing 2021 CA";

    /// <summary>Throws when the file is not an installer signed by the publisher.</summary>
    public static void EnsureTrusted(string path)
    {
        if (!IsTrusted(path, out var reason))
        {
            throw new InvalidOperationException($"The downloaded installer was not run: {reason}.");
        }
    }

    public static bool IsTrusted(string path, out string reason)
    {
        var status = VerifyTrust(path);
        if (status != 0)
        {
            reason = status switch
            {
                TrustENoSignature => "it carries no signature",
                TrustEBadDigest => "it was changed after it was signed",
                _ => $"Windows does not trust its signature (0x{status:X8})",
            };
            return false;
        }

        X509Certificate2 signer;
        try
        {
#pragma warning disable SYSLIB0057 // the signer of a signed file has no other loader
            signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
        }
        catch (CryptographicException)
        {
            reason = "its signer certificate cannot be read";
            return false;
        }

        using (signer)
        {
            var subject = signer.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            var issuer = signer.GetNameInfo(X509NameType.SimpleName, forIssuer: true);
            if (!string.Equals(subject, PublisherName, StringComparison.Ordinal)
                || !string.Equals(issuer, IssuerName, StringComparison.Ordinal))
            {
                reason = $"it is signed by \"{subject}\" ({issuer}), not by the publisher of HASS.Agent .NET10";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private static int VerifyTrust(string path)
    {
        var filePath = Marshal.StringToHGlobalUni(path);
        var fileInfo = new WinTrustFileInfo
        {
            Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
            FilePath = filePath,
        };
        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, fDeleteOld: false);
            var data = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = WtdUiNone,
                // No revocation lookup: it can hang for a long time on a PC without internet
                // access, and a revoked publisher certificate is out of reach for an attacker.
                RevocationChecks = WtdRevokeNone,
                UnionChoice = WtdChoiceFile,
                File = fileInfoPtr,
                StateAction = WtdStateActionIgnore,
                ProvFlags = WtdRevocationCheckNone | WtdCacheOnlyUrlRetrieval,
            };
            var action = WinTrustActionGenericVerifyV2;
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfoPtr);
            Marshal.FreeHGlobal(filePath);
        }
    }

    private static readonly Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionIgnore = 0;
    private const uint WtdRevocationCheckNone = 0x10;
    private const uint WtdCacheOnlyUrlRetrieval = 0x1000;
    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustEBadDigest = unchecked((int)0x80096010);

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
}
