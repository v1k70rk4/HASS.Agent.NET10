using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace HASS.Agent.Companion.Security;

/// <summary>
/// Checks a downloaded update installer before anything runs it. The installer is started
/// elevated, by the service even as SYSTEM, so a file that is not exactly what the publisher
/// signed must never get that far: not a corrupted download, not a release asset replaced by
/// someone with access to the GitHub account. Windows checks the Authenticode signature (the
/// file is unchanged since it was signed, by a certificate chaining to a trusted root, with a
/// valid timestamp), the signer must be the publisher of the releases, and its certificate
/// must not have been revoked (as it would be if the signing key were stolen). The publisher is
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

        // WinVerifyTrust accepts a signature without a timestamp as long as the certificate is
        // valid today, and checks a timestamp only when there is one. Every release is
        // timestamped (time.certum.pl), so a signature without one is not a release.
        if (!HasTimestamp(path))
        {
            reason = "its signature carries no timestamp";
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

            if (IsRevoked(signer))
            {
                reason = "the certificate it is signed with has been revoked";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Whether the certificate authority says the signing certificate is revoked, as it would
    /// be if the publisher's signing key were stolen. Asked online, briefly; an answer that
    /// does not come (no network, the authority unreachable) does not stop an update: only a
    /// definite "revoked" does.
    /// </summary>
    private static bool IsRevoked(X509Certificate2 signer)
    {
        try
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.EndCertificateOnly;
            chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(10);
            // An expired certificate is fine: the timestamp says it was valid at signing.
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;
            _ = chain.Build(signer);
            return chain.ChainStatus.Any(status => status.Status.HasFlag(X509ChainStatusFlags.Revoked));
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    // RFC 3161 timestamp (what signtool /tr adds) and the older Authenticode countersignature.
    private const string Rfc3161TimestampOid = "1.3.6.1.4.1.311.3.3.1";
    private const string CountersignatureOid = "1.2.840.113549.1.9.6";

    /// <summary>
    /// Whether the Authenticode signature embedded in the program carries a timestamp: the
    /// PKCS #7 blob from the PE certificate table, decoded, its signer's unsigned attributes.
    /// WinVerifyTrust has already checked that the signature, and a timestamp if there is
    /// one, are valid; this only makes sure the timestamp is there.
    /// </summary>
    internal static bool HasTimestamp(string path)
    {
        try
        {
            var signature = ReadEmbeddedSignature(path);
            if (signature is null)
            {
                return false;
            }

            var cms = new SignedCms();
            cms.Decode(signature);
            return cms.SignerInfos.Count > 0
                && cms.SignerInfos[0].UnsignedAttributes.Cast<CryptographicAttributeObject>().Any(attribute =>
                    attribute.Oid.Value is Rfc3161TimestampOid or CountersignatureOid);
        }
        catch (Exception exception) when (exception is IOException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    // The certificate table of a PE file: data directory 4 of the optional header, whose
    // address is a file offset. It holds WIN_CERTIFICATE entries; the Authenticode one
    // (type 2, PKCS #7 signed data) follows its 8-byte header.
    private static byte[]? ReadEmbeddedSignature(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D) // "MZ"
        {
            return null;
        }

        stream.Position = 0x3C;
        var peHeader = reader.ReadInt32();
        if (peHeader <= 0 || peHeader > stream.Length - 24)
        {
            return null;
        }

        stream.Position = peHeader;
        if (reader.ReadUInt32() != 0x00004550) // "PE\0\0"
        {
            return null;
        }

        var optionalHeader = peHeader + 4 + 20;
        stream.Position = optionalHeader;
        var directories = reader.ReadUInt16() switch
        {
            0x10B => optionalHeader + 96,  // PE32
            0x20B => optionalHeader + 112, // PE32+
            _ => -1,
        };
        if (directories < 0)
        {
            return null;
        }

        stream.Position = directories + 4 * 8;
        var tableOffset = reader.ReadUInt32();
        var tableSize = reader.ReadUInt32();
        if (tableOffset == 0 || tableSize < 8 || tableOffset + (long)tableSize > stream.Length)
        {
            return null;
        }

        stream.Position = tableOffset;
        var length = reader.ReadUInt32();
        reader.ReadUInt16(); // revision
        var type = reader.ReadUInt16();
        if (type != 0x0002 || length <= 8 || length > tableSize)
        {
            return null;
        }

        return reader.ReadBytes((int)length - 8);
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
