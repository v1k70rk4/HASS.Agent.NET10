using System.Net.Http.Json;
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
    /// Flattens GitHub release notes (Markdown) into a few plain lines that fit a
    /// message box: headings and emphasis markers dropped, bullets kept, long
    /// notes cut after a handful of lines.
    /// </summary>
    public static string SummarizeReleaseNotes(string? markdown)
    {
        const int maxLines = 12;
        const int maxLineLength = 160;

        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var lines = new List<string>();
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("<!--", StringComparison.Ordinal))
            {
                continue;
            }

            line = line.TrimStart('#').Trim();
            line = line.Replace("**", string.Empty).Replace("`", string.Empty);
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                line = "• " + line[2..];
            }

            if (line.Length > maxLineLength)
            {
                line = line[..(maxLineLength - 1)] + "…";
            }

            // Mark the cut only when a line is actually left out.
            if (lines.Count == maxLines)
            {
                lines.Add("…");
                break;
            }

            lines.Add(line);
        }

        return string.Join('\n', lines);
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

        await using var source = await http.GetStreamAsync(update.DownloadUrl, cancellationToken);
        await using var destination = File.Create(targetPath);
        await source.CopyToAsync(destination, cancellationToken);
        return targetPath;
    }

    private static GitHubReleaseAsset? SelectReleaseAsset(IReadOnlyList<GitHubReleaseAsset>? assets)
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

    private static bool IsNewerVersion(string latestTag, string currentVersion)
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

    private static string SanitizeFileName(string name)
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

    private sealed record GitHubReleaseAsset(
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
