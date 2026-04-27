using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StartupGroups.Core.Branding;
using StartupGroups.Core.Services;

namespace StartupGroups.App.Services;

public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    string CurrentVersion,
    string? LatestVersion,
    string? ReleaseUrl,
    string? InstallerAssetUrl,
    DateTimeOffset CheckedAt);

public interface IUpdateChecker
{
    Task<UpdateCheckResult?> CheckAsync(CancellationToken cancellationToken = default);
}

public sealed class GitHubUpdateChecker : IUpdateChecker
{
    private static readonly HttpClient Http = CreateHttpClient();
    private readonly ILogger<GitHubUpdateChecker> _logger;

    public GitHubUpdateChecker(ILogger<GitHubUpdateChecker> logger)
    {
        _logger = logger;
    }

    public async Task<UpdateCheckResult?> CheckAsync(CancellationToken cancellationToken = default)
    {
        var current = AppBranding.Version;
        var releasesEndpoint = BuildReleasesEndpoint(AppBranding.SupportUrl);
        if (releasesEndpoint is null)
        {
            _logger.LogDebug("Support URL is not a GitHub repo; skipping update check.");
            return new UpdateCheckResult(false, current, null, null, null, DateTimeOffset.Now);
        }

        try
        {
            var release = await Http
                .GetFromJsonAsync<GitHubRelease>(releasesEndpoint, cancellationToken)
                .ConfigureAwait(false);

            if (release?.TagName is null)
            {
                return new UpdateCheckResult(false, current, null, null, null, DateTimeOffset.Now);
            }

            var latest = release.TagName.TrimStart('v', 'V');
            var isNewer = CompareVersions(latest, current) > 0;
            var installerUrl = FindInstallerAssetUrl(release);

            return new UpdateCheckResult(
                isNewer,
                current,
                latest,
                release.HtmlUrl,
                installerUrl,
                DateTimeOffset.Now);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Update check network failure (no repo or offline).");
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed.");
            return null;
        }
    }

    private static string? BuildReleasesEndpoint(string supportUrl)
    {
        if (!Uri.TryCreate(supportUrl, UriKind.Absolute, out var uri) ||
            !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 2) return null;

        return $"https://api.github.com/repos/{segments[0]}/{segments[1]}/releases/latest";
    }

    private static string? FindInstallerAssetUrl(GitHubRelease release)
    {
        if (release.Assets is null) return null;
        foreach (var asset in release.Assets)
        {
            if (asset.BrowserDownloadUrl is null) continue;
            if (asset.Name?.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) == true)
            {
                return asset.BrowserDownloadUrl;
            }
        }
        return null;
    }

    private static int CompareVersions(string latest, string current)
    {
        if (Version.TryParse(latest, out var vLatest) &&
            Version.TryParse(current, out var vCurrent))
        {
            return vLatest.CompareTo(vCurrent);
        }
        return 0;
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = Timeouts.UpdateCheckerHttp,
        };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(AppBranding.AppId, AppBranding.Version));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("assets")]
        public GitHubAsset[]? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
