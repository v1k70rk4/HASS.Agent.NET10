namespace HASS.Agent.Companion.Logging;

internal sealed class FileLog : IDisposable
{
    // The log used to grow without limit (hundreds of MB after a few months). The current
    // file keeps its name; a finished day - or a file that grew too large within a day - is
    // moved aside as an archive, and archives are removed after a week, or sooner if together
    // they pass the size cap.
    private const long MaxFileBytes = 10L * 1024 * 1024;
    private const long MaxArchiveBytes = 100L * 1024 * 1024;
    private static readonly TimeSpan ArchiveRetention = TimeSpan.FromDays(7);
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(1);

    private readonly string _path;
    private readonly object _gate = new();
    private DateTime _nextMaintenanceUtc = DateTime.MinValue;
    private bool _disposed;

    public FileLog(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    public string FilePath => _path;

    /// <summary>When enabled, Debug() lines are written too. Runtime-only, not persisted.</summary>
    public bool VerboseEnabled { get; set; }

    public void Debug(string message)
    {
        if (VerboseEnabled)
        {
            Write("DEBUG", message);
        }
    }

    public void Info(string message)
    {
        Write("INFO", message);
    }

    public void Warning(string message)
    {
        Write("WARN", message);
    }

    public void Error(Exception exception, string message)
    {
        Write("ERROR", $"{message}{Environment.NewLine}{exception}");
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private void Write(string level, string message)
    {
        if (_disposed)
        {
            return;
        }

        var line = $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}";
        lock (_gate)
        {
            try
            {
                Maintain();
                File.AppendAllText(_path, line);
            }
            catch
            {
                // The tray app and the service share this file, so a write can lose a race
                // for it. A lost log line must never take the app down.
            }
        }
    }

    private void Maintain()
    {
        var nowUtc = DateTime.UtcNow;
        if (nowUtc < _nextMaintenanceUtc)
        {
            return;
        }

        _nextMaintenanceUtc = nowUtc + MaintenanceInterval;

        try
        {
            var rotatedTo = Rotate();
            Prune();
            if (rotatedTo is not null)
            {
                var outcome = File.Exists(rotatedTo)
                    ? $"the previous file is {Path.GetFileName(rotatedTo)}"
                    : $"the previous file was over the {MaxArchiveBytes / (1024 * 1024)} MB archive cap and was removed";
                File.AppendAllText(_path, $"{DateTimeOffset.Now:O} [INFO] Log rotated; {outcome}. Archives are kept for {ArchiveRetention.Days} days.{Environment.NewLine}");
            }
        }
        catch
        {
            // Both processes rotate the same file: whoever comes second finds it already
            // moved, or still open in the other one. The next round tries again.
        }
    }

    private string? Rotate()
    {
        var file = new FileInfo(_path);
        if (!file.Exists || file.Length == 0)
        {
            return null;
        }

        // The file's own last write tells which day it belongs to, so the process that comes
        // second sees a file written today and leaves it alone.
        var lastWrite = file.LastWriteTime;
        var finishedDay = lastWrite.Date < DateTime.Today;
        if (!finishedDay && file.Length <= MaxFileBytes)
        {
            return null;
        }

        var archive = ArchivePath($"{lastWrite:yyyy-MM-dd}");
        if (!finishedDay || File.Exists(archive))
        {
            archive = ArchivePath($"{lastWrite:yyyy-MM-dd}-{DateTime.Now:HHmmss}");
        }

        File.Move(_path, archive);
        return archive;
    }

    private void Prune()
    {
        var directory = Path.GetDirectoryName(_path)!;
        var pattern = $"{Path.GetFileNameWithoutExtension(_path)}-*{Path.GetExtension(_path)}";
        var oldest = DateTime.UtcNow - ArchiveRetention;
        long kept = 0;

        foreach (var archive in new DirectoryInfo(directory).GetFiles(pattern).OrderByDescending(item => item.LastWriteTimeUtc))
        {
            // Newest first: a file that does not fit is dropped without using up the room
            // of the older, smaller ones behind it.
            if (archive.LastWriteTimeUtc >= oldest && kept + archive.Length <= MaxArchiveBytes)
            {
                kept += archive.Length;
                continue;
            }

            try
            {
                archive.Delete();
            }
            catch
            {
                // Open in a viewer, or the other process was quicker.
            }
        }
    }

    private string ArchivePath(string suffix)
    {
        return Path.Combine(
            Path.GetDirectoryName(_path)!,
            $"{Path.GetFileNameWithoutExtension(_path)}-{suffix}{Path.GetExtension(_path)}");
    }
}
