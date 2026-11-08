using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HASS.Agent.Companion.Configuration;
using HASS.Agent.Companion.Security;
using Sendspin.SDK.Client;
using Sendspin.SDK.Connection.Noise;

namespace HASS.Agent.Companion.Media.Sendspin;

/// <summary>
/// Where the Sendspin player keeps what it must remember: its identity (a private key; the
/// player's id is its public key), the pairing records (a PSK per paired Music Assistant), the
/// output delay the server set and the server it last played from.
///
/// They are the PC's, not a Windows user's: the PC is the player, whoever is signed in at its
/// screen. They live next to the settings, in ProgramData\HASS.Agent.NET10\sendspin, under the
/// same access (see SettingsAccess): those who may use the settings may use the player's keys,
/// and a kiosk user, who may only read the settings, plays with the keys and pairings made from
/// an account that may change them. Protected with DPAPI for the machine, as the other secrets
/// of the settings. They are not in the settings file, so neither an export nor a backup takes
/// the identity to another PC (two PCs with one identity would be one player to Music Assistant);
/// a factory reset, which empties the folder, makes a new player.
/// </summary>
internal static class SendspinStorage
{
    public const string FolderName = "sendspin";

    /// <summary>Every file the player writes, for a factory reset.</summary>
    public static readonly string[] FileNames = ["identity.bin", "pairing.bin", "output-delay.txt", "last-server.txt"];

    public static string Directory => DirectoryOverride ?? Path.Combine(AppPaths.ConfigDirectoryPath, FolderName);

    /// <summary>For the tests: a folder of their own instead of the PC's.</summary>
    internal static string? DirectoryOverride { get; set; }

    public static string IdentityFile => Path.Combine(Directory, FileNames[0]);

    public static string PairingFile => Path.Combine(Directory, FileNames[1]);

    public static string OutputDelayFile => Path.Combine(Directory, FileNames[2]);

    public static string LastPlayedFile => Path.Combine(Directory, FileNames[3]);

    /// <summary>
    /// Writes the file whole or not at all: a crash halfway must not leave half a key behind. The
    /// folder is shared with the other users of the PC, so the write goes through SafeFiles (a link
    /// planted under the temp name is not followed), the temp name is this write's own, and a link
    /// in place of the folder itself is refused.
    /// </summary>
    internal static void WriteAtomic(string path, byte[] content)
    {
        var folder = System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException($"{folder.FullName} is a link, not a folder of the player's own.");
        }

        var temp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            SafeFiles.WriteAllBytes(temp, content);
            File.Move(temp, path, overwrite: true);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            // Access denied: an account that may only read the settings (a kiosk user).
            throw new UnauthorizedAccessException(ex.Message, ex);
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    internal static byte[] Protect(byte[] data, string purpose) =>
        ProtectedData.Protect(data, Entropy(purpose), DataProtectionScope.LocalMachine);

    /// <summary>Null when the file cannot be read back, which the callers treat as absent.</summary>
    internal static byte[]? Unprotect(byte[] data, string purpose)
    {
        try
        {
            return ProtectedData.Unprotect(data, Entropy(purpose), DataProtectionScope.LocalMachine);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static byte[] Entropy(string purpose) => Encoding.UTF8.GetBytes($"HASS.Agent.NET10:sendspin:{purpose}");
}

/// <summary>The player's identity, protected for the machine. The SDK owns the blob's format.</summary>
internal sealed class ProtectedSendspinIdentityStore(string path) : ISendspinIdentityStore
{
    private const string Purpose = "identity";

    public byte[]? Load() =>
        File.Exists(path) ? SendspinStorage.Unprotect(File.ReadAllBytes(path), Purpose) : null;

    public void Save(byte[] identityBlob) =>
        SendspinStorage.WriteAtomic(path, SendspinStorage.Protect(identityBlob, Purpose));
}

/// <summary>
/// The pairing records, protected for the machine: the player's own Pairing PSK (what the
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
            // Written on another PC (a copied folder): of no use here, and the player pairs
            // again as if it had never been paired.
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
