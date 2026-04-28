using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using StartupGroups.Core.Services;
using Velopack.Sources;

namespace StartupGroups.App.Services;

/// <summary>
/// GitHub source that bypasses the <c>/releases</c> listing endpoint and
/// uses <c>/releases/latest</c> instead, plus a 1-hour on-disk cache.
///
/// Why "cacheless" (CDN sense): GitHub serves the anonymous <c>/releases</c>
/// listing through Fastly with 15–30 minute staleness. New releases vanish
/// from that listing for the duration of the cache TTL even though they're
/// already published and reachable via <c>/releases/latest</c>. Since the
/// default <see cref="GithubSource"/> walks <c>/releases</c> to enumerate
/// every Velopack release, all anonymous users miss new updates for that
/// window. <c>/releases/latest</c> is fresh, and Velopack only needs the
/// highest version in the feed to decide whether an update is available.
///
/// Why we cache locally despite the name: GitHub's anonymous API rate
/// limit is 60 req/hour. Background checks plus manual "Check now"
/// clicks could add up. We cache the GetReleases response on disk per
/// channel for one hour; manual "Check now" passes a bypass flag.
/// </summary>
internal sealed class CachelessGithubSource : GithubSource
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    private readonly string _cachePath;
    private readonly bool _bypassCache;

    public CachelessGithubSource(
        string repoUrl,
        string? accessToken,
        bool prerelease,
        string channel,
        bool bypassCache,
        IFileDownloader? downloader = null)
        : base(repoUrl, accessToken, prerelease, downloader)
    {
        _bypassCache = bypassCache;
        var safeChannel = string.IsNullOrEmpty(channel) ? "default" : channel;
        var cacheDir = Path.Combine(AppPaths.LocalDataFolder, "cache");
        Directory.CreateDirectory(cacheDir);
        _cachePath = Path.Combine(cacheDir, $"releases.{safeChannel}.json");
    }

    protected override async Task<GithubRelease[]> GetReleases(bool includePrereleases)
    {
        if (!_bypassCache && TryReadCache(out var cached))
        {
            return cached;
        }

        GithubRelease[] releases;
        if (includePrereleases)
        {
            // /releases/latest excludes prereleases. For beta/nightly users
            // we need the full listing; accept the Fastly cache lag (it
            // still beats not finding the release at all).
            releases = await base.GetReleases(includePrereleases).ConfigureAwait(false);
        }
        else
        {
            var repoPath = RepoUri.AbsolutePath.Trim('/');
            var url = $"https://api.github.com/repos/{repoPath}/releases/latest";

            var headers = new Dictionary<string, string>
            {
                ["Accept"] = "application/vnd.github.v3+json",
                ["User-Agent"] = "Velopack",
            };
            if (!string.IsNullOrEmpty(AccessToken))
            {
                headers["Authorization"] = $"Bearer {AccessToken}";
            }

            var json = await Downloader.DownloadString(url, headers, timeout: 30).ConfigureAwait(false);
            var release = JsonSerializer.Deserialize<GithubRelease>(json);
            releases = release is null ? Array.Empty<GithubRelease>() : new[] { release };
        }

        TryWriteCache(releases);
        return releases;
    }

    private bool TryReadCache(out GithubRelease[] releases)
    {
        releases = Array.Empty<GithubRelease>();
        try
        {
            if (!File.Exists(_cachePath)) return false;
            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(_cachePath);
            if (age > CacheTtl) return false;

            var json = File.ReadAllText(_cachePath);
            var parsed = JsonSerializer.Deserialize<GithubRelease[]>(json);
            if (parsed is null || parsed.Length == 0) return false;
            releases = parsed;
            return true;
        }
        catch
        {
            // Corrupt cache: ignore and re-fetch.
            return false;
        }
    }

    private void TryWriteCache(GithubRelease[] releases)
    {
        try
        {
            var json = JsonSerializer.Serialize(releases);
            File.WriteAllText(_cachePath, json);
        }
        catch
        {
            // Cache is best-effort — never fail an update check on write errors.
        }
    }
}
