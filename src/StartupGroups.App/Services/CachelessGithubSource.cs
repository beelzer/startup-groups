using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Velopack.Sources;

namespace StartupGroups.App.Services;

/// <summary>
/// GitHub source that bypasses the <c>/releases</c> listing endpoint and
/// uses <c>/releases/latest</c> instead.
///
/// Why: GitHub serves <c>/releases</c> through a Fastly CDN with
/// 15–30 minute staleness. New releases vanish from that listing for the
/// duration of the cache TTL even though they're already published and
/// reachable via <c>/releases/latest</c> and <c>/releases/tags/&lt;tag&gt;</c>.
/// Since the default <see cref="GithubSource"/> walks <c>/releases</c> to
/// enumerate every Velopack release, all anonymous users (i.e. all our
/// users — we ship without a token) miss new updates for that window.
/// <c>/releases/latest</c> is fresh, and Velopack only needs the highest
/// version in the feed to decide whether an update is available.
/// </summary>
internal sealed class CachelessGithubSource : GithubSource
{
    public CachelessGithubSource(
        string repoUrl,
        string? accessToken,
        bool prerelease,
        IFileDownloader? downloader = null)
        : base(repoUrl, accessToken, prerelease, downloader)
    {
    }

    protected override async Task<GithubRelease[]> GetReleases(bool includePrereleases)
    {
        // /releases/latest does not return prereleases. If the caller asked
        // for prereleases, fall back to the base implementation (which we
        // accept as best-effort given GitHub's cache; we don't ship
        // prereleases anyway).
        if (includePrereleases)
        {
            return await base.GetReleases(includePrereleases).ConfigureAwait(false);
        }

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
        return release is null ? Array.Empty<GithubRelease>() : new[] { release };
    }
}
