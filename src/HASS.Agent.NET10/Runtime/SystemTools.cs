namespace HASS.Agent.Companion.Runtime;

/// <summary>
/// Full paths of the Windows tools the app starts. A bare name ("sc.exe") is looked up by
/// Windows in the current directory before System32, so an app started elevated from a
/// folder someone else can write to would run a planted copy with its rights.
/// </summary>
internal static class SystemTools
{
    /// <summary>A tool that ships in System32 (sc.exe, schtasks.exe, shutdown.exe, ...).</summary>
    public static string InSystem32(string fileName) => Path.Combine(Environment.SystemDirectory, fileName);

    /// <summary>Windows PowerShell 5.1, part of Windows.</summary>
    public static string WindowsPowerShell => InSystem32(@"WindowsPowerShell\v1.0\powershell.exe");

    /// <summary>
    /// PowerShell 7 is installed separately. The service (SYSTEM) takes only its install folder
    /// under Program Files: a PATH entry ordinary users can write to would let one of them plant
    /// a pwsh.exe that runs with the service's rights. The tray app runs as its user anyway and
    /// also looks on PATH (absolute entries only), for a PowerShell installed elsewhere.
    /// </summary>
    public static string Pwsh
    {
        get
        {
            var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (identity.IsSystem || File.Exists(installed))
            {
                return installed;
            }

            return FindOnPath("pwsh.exe", Environment.GetEnvironmentVariable("PATH")) ?? installed;
        }
    }

    /// <summary>
    /// The first PATH entry that holds the file. Relative entries (".", "tools") are skipped:
    /// they resolve against the current directory, which is what this class avoids.
    /// </summary>
    internal static string? FindOnPath(string fileName, string? pathVariable)
    {
        foreach (var raw in (pathVariable ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(entry))
            {
                continue;
            }

            var candidate = Path.Combine(entry, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
