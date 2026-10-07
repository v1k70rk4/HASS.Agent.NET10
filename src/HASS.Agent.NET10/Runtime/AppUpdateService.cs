using System.Net.Http.Json;
using HASS.Agent.Companion.Security;
using System.Text;
using System.Text.Json.Serialization;

namespace HASS.Agent.Companion.Runtime;

internal static class AppUpdateService
{
    public static async Task<AppUpdateState> CheckAsync(string installedVersion, bool includePrereleases = false, CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppIdentity.ExecutableName}/{installedVersion}");

            // The releases/latest endpoint never returns pre-releases; the beta
            // channel reads the release list instead (newest first, drafts skipped).
            GitHubRelease? release;
            if (includePrereleases)
            {
                var listUrl = $"https://api.github.com/repos/{AppIdentity.GitHubRepository}/releases?per_page=10";
                var releases = await http.GetFromJsonAsync<List<GitHubRelease>>(listUrl, cancellationToken);
                release = releases?.FirstOrDefault(item => !item.Draft && !string.IsNullOrWhiteSpace(item.TagName));
            }
            else
            {
                var latestUrl = $"https://api.github.com/repos/{AppIdentity.GitHubRepository}/releases/latest";
                release = await http.GetFromJsonAsync<GitHubRelease>(latestUrl, cancellationToken);
            }

            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            {
                return AppUpdateState.Failed(installedVersion, "latest_release_unavailable");
            }

            var asset = SelectReleaseAsset(release.Assets);
            return new AppUpdateState(
                AppIdentity.DisplayName,
                installedVersion,
                release.TagName,
                IsNewerVersion(release.TagName, installedVersion),
                release.HtmlUrl ?? $"{AppIdentity.GitHubRepositoryUrl}/releases/latest",
                asset?.DownloadUrl,
                asset?.Name,
                DateTimeOffset.UtcNow,
                null,
                release.Body);
        }
        catch (Exception ex)
        {
            return AppUpdateState.Failed(installedVersion, ex.Message);
        }
    }

    /// <summary>
    /// Turns GitHub release notes into plain paragraphs for the update prompt: Markdown
    /// markers dropped, bullets kept, the hard-wrapped lines of a paragraph joined again
    /// (the notes come from the tag message, wrapped at 75 columns, and a dialog wraps
    /// them itself), the title line left out when it only repeats the app's name, and
    /// the text cut at a paragraph once it gets long. The dialog links to the full notes.
    /// </summary>
    public static string SummarizeReleaseNotes(string? markdown, int maxLength = 1400)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var paragraphs = new List<(string Text, bool Bullet)>();
        var current = new StringBuilder();
        var currentIsBullet = false;

        void Flush()
        {
            if (current.Length > 0)
            {
                paragraphs.Add((current.ToString(), currentIsBullet));
                current.Clear();
            }

            currentIsBullet = false;
        }

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("<!--", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            var bullet = line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal);
            var heading = line.StartsWith('#');
            line = line.TrimStart('#').Trim().Replace("**", string.Empty).Replace("`", string.Empty);
            if (bullet || heading)
            {
                Flush();
                currentIsBullet = bullet;
                current.Append(bullet ? "• " + line[2..] : line);
                if (heading)
                {
                    Flush();
                }

                continue;
            }

            // A line that is not blank and not a bullet continues the paragraph before it.
            if (current.Length > 0)
            {
                current.Append(' ');
            }

            current.Append(line);
        }

        Flush();

        // The first line of the tag message is the app's name and the version, which the
        // dialog's heading already says.
        if (paragraphs.Count > 0
            && paragraphs[0].Text.StartsWith(AppIdentity.DisplayName, StringComparison.OrdinalIgnoreCase)
            && paragraphs[0].Text.Length < AppIdentity.DisplayName.Length + 24)
        {
            paragraphs.RemoveAt(0);
        }

        var text = new StringBuilder();
        var previousWasBullet = false;
        foreach (var (paragraph, isBullet) in paragraphs)
        {
            if (text.Length > 0 && text.Length + paragraph.Length > maxLength)
            {
                text.Append("\n\n…");
                break;
            }

            if (text.Length > 0)
            {
                text.Append(isBullet && previousWasBullet ? "\n" : "\n\n");
            }

            text.Append(paragraph);
            previousWasBullet = isBullet;
        }

        return text.ToString();
    }

    public static async Task<string> DownloadAsync(AppUpdateState update, string targetDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(update.DownloadUrl) || string.IsNullOrWhiteSpace(update.AssetName))
        {
            throw new InvalidOperationException("No downloadable update asset is available.");
        }

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppIdentity.ExecutableName}/{update.InstalledVersion}");

        Directory.CreateDirectory(targetDirectory);
        var targetPath = Path.Combine(targetDirectory, SanitizeFileName(update.AssetName));

        await using (var source = await http.GetStreamAsync(update.DownloadUrl, cancellationToken))
        await using (var destination = File.Create(targetPath))
        {
            await source.CopyToAsync(destination, cancellationToken);
        }

        // Every way an update is installed (the About page, the tray app or the service on a
        // request from Home Assistant) goes through here, so nothing starts an installer that
        // is not signed by the publisher.
        if (!InstallerSignature.IsTrusted(targetPath, out var reason))
        {
            TryDelete(targetPath);
            throw new InvalidOperationException($"The downloaded installer was not run: {reason}.");
        }

        return targetPath;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Left behind in the download folder; it is never started.
        }
    }

    internal static GitHubReleaseAsset? SelectReleaseAsset(IReadOnlyList<GitHubReleaseAsset>? assets)
    {
        return assets?
            .Where(asset => !string.IsNullOrWhiteSpace(asset.DownloadUrl))
            .OrderBy(asset => GetAssetPreference(asset.Name))
            .FirstOrDefault(asset => IsNet10Asset(asset.Name) && GetAssetPreference(asset.Name) < 100);
    }

    private static bool IsNet10Asset(string name)
    {
        return name.Contains("HASS.Agent.NET10", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetAssetPreference(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.EndsWith(".msi", StringComparison.Ordinal)) return 0;
        if (lower.EndsWith(".exe", StringComparison.Ordinal)) return 1;
        if (lower.EndsWith(".zip", StringComparison.Ordinal)) return 2;
        return 100;
    }

    internal static bool IsNewerVersion(string latestTag, string currentVersion)
    {
        var (latestCore, latestPre) = SplitVersion(latestTag);
        var (currentCore, currentPre) = SplitVersion(currentVersion);

        if (latestCore is null || currentCore is null)
        {
            return !string.Equals(latestTag.Trim(), currentVersion.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        var coreCompare = latestCore.CompareTo(currentCore);
        if (coreCompare != 0)
        {
            return coreCompare > 0;
        }

        // Same core version: the stable release outranks any of its pre-releases
        // (10.3.0 > 10.3.0-beta.2), and pre-releases compare SemVer style.
        if (string.IsNullOrEmpty(latestPre))
        {
            return !string.IsNullOrEmpty(currentPre);
        }

        if (string.IsNullOrEmpty(currentPre))
        {
            return false;
        }

        return ComparePrerelease(latestPre, currentPre) > 0;
    }

    private static (Version? Core, string Prerelease) SplitVersion(string value)
    {
        var normalized = value.Trim().TrimStart('v', 'V');
        var prerelease = string.Empty;
        var suffixIndex = normalized.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
        {
            if (normalized[suffixIndex] == '-')
            {
                prerelease = normalized[(suffixIndex + 1)..];
                var buildIndex = prerelease.IndexOf('+');
                if (buildIndex >= 0)
                {
                    prerelease = prerelease[..buildIndex];
                }
            }

            normalized = normalized[..suffixIndex];
        }

        return (Version.TryParse(normalized, out var version) ? version : null, prerelease);
    }

    private static int ComparePrerelease(string left, string right)
    {
        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        for (var index = 0; index < Math.Max(leftParts.Length, rightParts.Length); index++)
        {
            if (index >= leftParts.Length)
            {
                return -1;
            }

            if (index >= rightParts.Length)
            {
                return 1;
            }

            var leftIsNumber = long.TryParse(leftParts[index], out var leftNumber);
            var rightIsNumber = long.TryParse(rightParts[index], out var rightNumber);
            var compare = (leftIsNumber, rightIsNumber) switch
            {
                (true, true) => leftNumber.CompareTo(rightNumber),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.Compare(leftParts[index], rightParts[index], StringComparison.OrdinalIgnoreCase)
            };

            if (compare != 0)
            {
                return compare;
            }
        }

        return 0;
    }

    internal static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("assets")] IReadOnlyList<GitHubReleaseAsset>? Assets);

    internal sealed record GitHubReleaseAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string DownloadUrl);
}

internal sealed record AppUpdateState(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("installed_version")] string InstalledVersion,
    [property: JsonPropertyName("latest_version")] string LatestVersion,
    [property: JsonPropertyName("update_available")] bool UpdateAvailable,
    [property: JsonPropertyName("release_url")] string? ReleaseUrl,
    [property: JsonPropertyName("download_url")] string? DownloadUrl,
    [property: JsonPropertyName("asset_name")] string? AssetName,
    [property: JsonPropertyName("checked_at")] DateTimeOffset CheckedAt,
    [property: JsonPropertyName("error")] string? Error,
    // Shown in the in-app update prompt only; the state published to Home
    // Assistant (MQTT / WebSocket) stays as it was.
    [property: JsonIgnore] string? ReleaseNotes = null)
{
    public static AppUpdateState Failed(string installedVersion, string error)
    {
        return new AppUpdateState(
            AppIdentity.DisplayName,
            installedVersion,
            installedVersion,
            false,
            $"{AppIdentity.GitHubRepositoryUrl}/releases/latest",
            null,
            null,
            DateTimeOffset.UtcNow,
            error);
    }
}
