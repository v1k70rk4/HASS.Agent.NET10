using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HASS.Agent.Companion.Runtime;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;

namespace HASS.Agent.Companion.Media.Sendspin;

/// <summary>
/// Where the Sendspin player keeps what it must remember: its identity (a private key; the
/// player's id is its public key), the pairing records (a PSK per paired Music Assistant), the
/// output delay the server set and the server it last played from.
///
/// They are the Windows user's, in %LOCALAPPDATA%\HASS.Agent.NET10\sendspin, not the PC's: the
/// shared settings folder is read-only for a kiosk user, and pairing has to write. The keys are
/// protected with DPAPI for the current user, so another user of the PC cannot read them, and
/// they are not in the settings, so a backup taken to another PC does not take the identity
/// along (two PCs with one identity would be one player to Music Assistant).
/// </summary>
internal static class SendspinStorage
{
    public static string Directory => DirectoryOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppIdentity.ConfigurationDirectoryName,
        "sendspin");

    /// <summary>For the tests: a folder of their own instead of the user's.</summary>
    internal static string? DirectoryOverride { get; set; }

    public static string IdentityFile => Path.Combine(Directory, "identity.bin");

    public static string PairingFile => Path.Combine(Directory, "pairing.bin");

    public static string OutputDelayFile => Path.Combine(Directory, "output-delay.txt");

    public static string LastPlayedFile => Path.Combine(Directory, "last-server.txt");

    /// <summary>Writes the file whole or not at all: a crash halfway must not leave half a key behind.</summary>
    internal static void WriteAtomic(string path, byte[] content)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    internal static byte[] Protect(byte[] data, string purpose) =>
        ProtectedData.Protect(data, Entropy(purpose), DataProtectionScope.CurrentUser);

    /// <summary>Null when the file cannot be read back, which the callers treat as absent.</summary>
    internal static byte[]? Unprotect(byte[] data, string purpose)
    {
        try
        {
            return ProtectedData.Unprotect(data, Entropy(purpose), DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static byte[] Entropy(string purpose) => Encoding.UTF8.GetBytes($"HASS.Agent.NET10:sendspin:{purpose}");
}

/// <summary>The player's identity, protected for the Windows user. The SDK owns the blob's format.</summary>
internal sealed class ProtectedSendspinIdentityStore(string path) : ISendspinIdentityStore
{
    private const string Purpose = "identity";

    public byte[]? Load() =>
        File.Exists(path) ? SendspinStorage.Unprotect(File.ReadAllBytes(path), Purpose) : null;

    public void Save(byte[] identityBlob) =>
        SendspinStorage.WriteAtomic(path, SendspinStorage.Protect(identityBlob, Purpose));
}

/// <summary>
/// The pairing records, protected for the Windows user: the player's own Pairing PSK (what the
/// pairing token carries) and one long-term PSK per Music Assistant it is paired with. The SDK
/// calls it from the UI and from a connection's receive thread at once, so every call holds the
/// lock and reads or writes the file whole.
/// </summary>
internal sealed class ProtectedPairingRecordStore(string path) : IPairingRecordStore
{
    private const string Purpose = "pairing";
    private readonly object _gate = new();

    public IReadOnlyList<PairingRecord> List()
    {
        lock (_gate)
        {
            return Read();
        }
    }

    public void Upsert(PairingRecord record)
    {
        lock (_gate)
        {
            var records = Read().Where(r => r.PskId != record.PskId).ToList();
            records.Add(record);
            Write(records);
        }
    }

    public void Remove(string pskId)
    {
        lock (_gate)
        {
            var records = Read();
            var kept = records.Where(r => r.PskId != pskId).ToList();
            if (kept.Count != records.Count)
            {
                Write(kept);
            }
        }
    }

    /// <summary>Whether a pairing with this server is stored: if not, the player still has to be paired in Music Assistant.</summary>
    public bool IsPairedWith(string serverId) =>
        List().Any(r => r.Category == PskCategory.LongTerm && r.ServerId == serverId);

    private List<PairingRecord> Read()
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var json = SendspinStorage.Unprotect(File.ReadAllBytes(path), Purpose);
        if (json is null)
        {
            // Written by another Windows user or on another PC: of no use here, and the player
            // pairs again as if it had never been paired.
            return [];
        }

        var entries = JsonSerializer.Deserialize<List<Entry>>(json) ?? [];
        return entries
            .Where(e => e.Psk is { Length: > 0 })
            .Select(e => new PairingRecord(Convert.FromBase64String(e.Psk!), e.Category, e.ServerId, e.LastUsedUtc))
            .ToList();
    }

    private void Write(List<PairingRecord> records)
    {
        var entries = records.Select(r => new Entry
        {
            Psk = Convert.ToBase64String(r.Psk.Span),
            Category = r.Category,
            ServerId = r.ServerId,
            LastUsedUtc = r.LastUsedUtc
        }).ToList();
        SendspinStorage.WriteAtomic(path, SendspinStorage.Protect(JsonSerializer.SerializeToUtf8Bytes(entries), Purpose));
    }

    private sealed class Entry
    {
        public string? Psk { get; set; }

        public PskCategory Category { get; set; }

        public string? ServerId { get; set; }

        public DateTimeOffset? LastUsedUtc { get; set; }
    }
}

/// <summary>The output delay a server set for this player (extra speakers, an amplifier), kept across restarts.</summary>
internal sealed class SendspinOutputDelayStore(string path) : IOutputDelayStore
{
    public double? Load() =>
        File.Exists(path) && double.TryParse(File.ReadAllText(path), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    public void Save(double outputDelayMs) =>
        SendspinStorage.WriteAtomic(path, Encoding.UTF8.GetBytes(outputDelayMs.ToString(CultureInfo.InvariantCulture)));
}

/// <summary>The server this player last played from: when two servers reach it at once, that one wins a tie.</summary>
internal sealed class SendspinLastPlayedServerStore(string path) : ILastPlayedServerStore
{
    public string? Load() => File.Exists(path) ? File.ReadAllText(path).Trim() : null;

    public void Save(string serverId) => SendspinStorage.WriteAtomic(path, Encoding.UTF8.GetBytes(serverId));
}
