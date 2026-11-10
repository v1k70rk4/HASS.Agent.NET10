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
    /// Holds the folder against the other apps of this PC (another user's session) for one
    /// read-modify-write of a file here: a lock file, opened by one process at a time, waited
    /// for a few seconds. An account that may only read the folder cannot make the lock file
    /// and gets a lock that holds nothing: it cannot write either, so there is nothing to race.
    /// </summary>
    internal static IDisposable Lock(string name = "sendspin.lock")
    {
        var path = Path.Combine(Directory, name);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try
            {
                // Outside the wait below: a folder that is a link will not stop being one.
                OwnFolder(Directory);
            }
            catch (UnauthorizedAccessException)
            {
                return EmptyLock.Instance;
            }

            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (UnauthorizedAccessException)
            {
                return EmptyLock.Instance;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
        }
    }

    // The player's folder, made when it is not there. The folder is shared with the other users
    // of the PC: one of them can put a link in its place, and whatever is then made in it (a lock
    // file as much as a key) would be made where the link points, with the rights of whoever runs this.
    private static void OwnFolder(string path)
    {
        var folder = System.IO.Directory.CreateDirectory(path);
        if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException($"{folder.FullName} is a link, not a folder of the player's own.");
        }
    }

    private sealed class EmptyLock : IDisposable
    {
        public static readonly EmptyLock Instance = new();

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Writes the file whole or not at all: a crash halfway must not leave half a key behind. The
    /// folder is shared with the other users of the PC, so the write goes through SafeFiles (a link
    /// planted under the temp name is not followed), the temp name is this write's own, and a link
    /// in place of the folder itself is refused.
    /// </summary>
    internal static void WriteAtomic(string path, byte[] content)
    {
        OwnFolder(Path.GetDirectoryName(path)!);

        var stamp = $"{Environment.ProcessId}.{Guid.NewGuid():N}";
        var temp = $"{path}.{stamp}.tmp";
        var aside = $"{path}.{stamp}{AsideSuffix}";
        var movedAside = false;
        try
        {
            SafeFiles.WriteAllBytes(temp, content);

            // Another app of the PC may be reading the file this moment. Windows lets no one
            // write over a file that is open, but a reader that shares deletion (ReadShared)
            // lets it be renamed: the old file is put aside, the new one takes its name, and the
            // old one goes once the reader is done with it. A failed step puts the old one back.
            if (File.Exists(path))
            {
                MoveWithRetry(path, aside);
                movedAside = true;
            }

            BeforeFinalMove?.Invoke(path);
            MoveWithRetry(temp, path);
            movedAside = false;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            // Access denied: an account that may only read the settings (a kiosk user).
            throw new UnauthorizedAccessException(ex.Message, ex);
        }
        finally
        {
            var oldKept = false;
            if (movedAside && !File.Exists(path))
            {
                try
                {
                    File.Move(aside, path);
                }
                catch
                {
                    // The old file could not go back under its name: it stays as it is, next to
                    // the name, rather than be deleted with the new one not in place.
                    oldKept = true;
                }
            }

            if (!oldKept)
            {
                try { File.Delete(aside); } catch { }
            }

            try { File.Delete(temp); } catch { }
        }
    }

    // A slower reader, or a scanner, holds the file for a moment: tried again a few times.
    /// <summary>The end of the name the old file is put aside under while a replace runs.</summary>
    internal const string AsideSuffix = ".old";

    /// <summary>Tests only: runs just before the new file takes the name.</summary>
    internal static Action<string>? BeforeFinalMove { get; set; }

    /// <summary>Tests only: runs when a read finds the name empty, before it looks again.</summary>
    internal static Action<string>? WhenNameMissing { get; set; }

    private static void MoveWithRetry(string from, string to)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(from, to);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(20);
            }
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

    /// <summary>
    /// The file, read so that another app may replace it meanwhile (the reader keeps the old
    /// one), or null when there is none. A replace by another app has the name empty between
    /// its two renames, with the old file put aside next to it: a name found empty is read
    /// from the aside file, the content of a moment ago, and looked at again for a moment
    /// before it counts as no file. So a read-only app too never takes a replace for an empty store.
    /// No folder yet (a kiosk app up before any app that may write) is no file either.
    /// </summary>
    internal static byte[]? ReadShared(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return ReadWhole(path);
            }
            catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                WhenNameMissing?.Invoke(path);
                if (ReadAside(path) is { } aside)
                {
                    return aside;
                }

                if (attempt >= 10)
                {
                    return null;
                }

                Thread.Sleep(20);
            }
        }
    }

    private static byte[] ReadWhole(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    // The old file of a replace in progress (the newest, should a crashed one have left its own),
    // or null when there is none or it went while being read.
    private static byte[]? ReadAside(string path)
    {
        try
        {
            var folder = Path.GetDirectoryName(path)!;
            var newest = System.IO.Directory.EnumerateFiles(folder, $"{Path.GetFileName(path)}.*{AsideSuffix}")
                .Select(file => new FileInfo(file))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
            return newest is null ? null : ReadWhole(newest.FullName);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
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
        SendspinStorage.ReadShared(path) is { } data ? SendspinStorage.Unprotect(data, Purpose) : null;

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
        using (SendspinStorage.Lock())
        {
            var records = Read();
            var existing = records.FirstOrDefault(r => r.PskId == record.PskId);
            records.RemoveAll(r => r.PskId == record.PskId);
            records.Add(record);
            try
            {
                Write(records);
            }
            catch (UnauthorizedAccessException) when (existing is not null && SameCredential(existing, record))
            {
                // Only the time the record was last used changed, which an account that may
                // only read the folder (a kiosk user) cannot note down. The connection goes
                // on: the pairing itself is there. A new or changed pairing is not swallowed.
            }
        }
    }

    public void Remove(string pskId)
    {
        lock (_gate)
        using (SendspinStorage.Lock())
        {
            var records = Read();
            var kept = records.Where(r => r.PskId != pskId).ToList();
            if (kept.Count != records.Count)
            {
                Write(kept);
            }
        }
    }

    private static bool SameCredential(PairingRecord a, PairingRecord b) =>
        a.Category == b.Category && a.ServerId == b.ServerId && a.Psk.Span.SequenceEqual(b.Psk.Span);

    /// <summary>Whether a pairing with this server is stored: if not, the player still has to be paired in Music Assistant.</summary>
    public bool IsPairedWith(string serverId) =>
        List().Any(r => r.Category == PskCategory.LongTerm && r.ServerId == serverId);

    private List<PairingRecord> Read()
    {
        var data = SendspinStorage.ReadShared(path);
        if (data is null)
        {
            return [];
        }

        var json = SendspinStorage.Unprotect(data, Purpose);
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
