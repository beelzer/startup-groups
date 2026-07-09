using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Salvo.Core.Branding;
using Salvo.Core.Services;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Management.Deployment;

namespace Salvo.App.Services;

/// <summary>
/// IUpdateService implementation for the MSIX-packaged build of Salvo.
/// Used when the running process is hosted inside a packaged context — i.e.
/// installed via App Installer (sideloaded) or the Microsoft Store. The
/// existing <see cref="VelopackUpdateService"/> stays as the implementation
/// for unpackaged builds (today's canary track) until that pipeline is
/// retired in Phase 3 of the migration plan.
///
/// Update mechanism:
/// - <see cref="CheckAsync"/> queries the GitHub Releases API directly
///   for the latest tagged release. The same approach Velopack uses, just
///   without Velopack's release-feed indirection layer.
/// - <see cref="DownloadAndApplyAsync"/> calls
///   <see cref="PackageManager.AddPackageByAppInstallerFileAsync"/>
///   pointing at the stable <c>releases/latest/download/...appinstaller</c>
///   URL. Windows handles download, signature verification, and apply.
///
/// Channels: MSIX builds are single-track. The
/// <see cref="UpdateChannel"/> enum is preserved on the interface for
/// backwards-compat with the flyout, but always reports Stable here.
/// </summary>
public sealed class MsixUpdateService : IUpdateService
{
    private static readonly HttpClient ReleaseClient = CreateReleaseClient();

    private readonly ILogger<MsixUpdateService> _logger;
    private readonly Package _package;
    private readonly PackageManager _packageManager = new();

    /// <summary>
    /// Stable URL pointing at the latest released <c>.appinstaller</c>. The
    /// GitHub-native <c>releases/latest/download/&lt;asset&gt;</c> redirect
    /// follows the most recent non-prerelease release automatically, which
    /// is exactly what App Installer wants for tracking the live channel.
    /// </summary>
    private static readonly Uri AppInstallerUri = new(
        $"{AppBranding.SupportUrl}/releases/latest/download/Salvo.appinstaller");

    public MsixUpdateService(ILogger<MsixUpdateService> logger, Package package)
    {
        _logger = logger;
        _package = package;
    }

    public bool CanUpdate => true;

    public string CurrentVersion
    {
        get
        {
            var v = _package.Id.Version;
            // MSIX stores 4-part W.X.Y.Z; we present semver-ish 3-part to
            // match how the rest of the UI labels versions (and how the
            // Velopack-built canary did it). Z stays 0 by Store convention,
            // so trimming it is safe.
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public async Task<UpdateCheckResult?> CheckAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        var current = CurrentVersion;

        try
        {
            var release = await FetchLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            if (release is null || string.IsNullOrEmpty(release.TagName))
            {
                _logger.LogDebug("No latest release returned by GitHub.");
                return new UpdateCheckResult(
                    CurrentVersion: current,
                    LatestVersion: current,
                    ReleaseUrl: null,
                    ReleaseNotesMarkdown: null,
                    IsUpdateAvailable: false,
                    CheckedAt: DateTimeOffset.Now,
                    Channel: UpdateChannel.Stable);
            }

            var latest = release.TagName.TrimStart('v');
            var available = IsNewer(latest, current);
            return new UpdateCheckResult(
                CurrentVersion: current,
                LatestVersion: latest,
                ReleaseUrl: release.HtmlUrl,
                ReleaseNotesMarkdown: release.Body,
                IsUpdateAvailable: available,
                CheckedAt: DateTimeOffset.Now,
                Channel: UpdateChannel.Stable);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MSIX update check failed.");
            return new UpdateCheckResult(
                CurrentVersion: current,
                LatestVersion: null,
                ReleaseUrl: null,
                ReleaseNotesMarkdown: null,
                IsUpdateAvailable: false,
                CheckedAt: DateTimeOffset.Now,
                Channel: UpdateChannel.Stable);
        }
    }

    public async Task DownloadAndApplyAsync(IProgress<int>? progress, CancellationToken cancellationToken = default)
    {
        // AddPackageByAppInstallerFileAsync drives the entire install flow
        // (download, signature verify, register, restart). We attach a
        // progress callback so the existing Update flyout's progress bar
        // mirrors what App Installer would show. Cancellation propagates
        // via the CancellationToken into the WinRT operation.
        _logger.LogInformation("Triggering MSIX self-update from {Uri}", AppInstallerUri);

        var operation = _packageManager.AddPackageByAppInstallerFileAsync(
            AppInstallerUri,
            AddPackageByAppInstallerOptions.None,
            null);

        operation.Progress = (_, p) =>
        {
            // DeploymentProgress.percentage is uint 0..100. Cast to int
            // to match Velopack's int-percent contract on IUpdateService.
            progress?.Report((int)p.percentage);
        };

        await operation.AsTask(cancellationToken).ConfigureAwait(false);

        // Whether App Installer chose to silently restart or asked the user
        // is its own decision; either way the work is done from our side.
        _logger.LogInformation("MSIX self-update operation completed.");
    }

    internal static bool IsNewer(string latest, string current)
    {
        // Compare only Major.Minor.Build. GitHub release tags are natively
        // 4-part (e.g. "0.2.14.0") while CurrentVersion is the trimmed 3-part
        // form ("0.2.14"), and Version.CompareTo treats an unset 4th component
        // (-1) as less than an explicit 0 — so "0.2.14.0" would compare as
        // newer than the identical installed "0.2.14", surfacing a perpetual,
        // unclearable "update available". Truncating both to 3 components
        // (stripping any -canary.N prerelease suffix first) removes that.
        if (!Version.TryParse(StripPrerelease(latest), out var l)) return false;
        if (!Version.TryParse(StripPrerelease(current), out var c)) return false;
        return ToMajorMinorBuild(l).CompareTo(ToMajorMinorBuild(c)) > 0;
    }

    private static Version ToMajorMinorBuild(Version v) =>
        // Build is -1 when the source string had only two components ("0.2");
        // clamp so the 3-arg Version ctor (which rejects negatives) is safe.
        new(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build);

    private static string StripPrerelease(string version)
    {
        // Strip any prerelease suffix (e.g. "-canary.3") the upstream tag
        // might carry so the numeric core parses via Version; component-count
        // normalisation happens in IsNewer.
        var dash = version.IndexOf('-');
        return dash >= 0 ? version[..dash] : version;
    }

    private async Task<GithubRelease?> FetchLatestReleaseAsync(CancellationToken cancellationToken)
    {
        var repoUri = new Uri(AppBranding.SupportUrl);
        var repoPath = repoUri.AbsolutePath.Trim('/');
        var url = GitHubApi.ReleasesUrl(repoPath, "/latest");

        using var response = await ReleaseClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("Latest release fetch returned {Status}", response.StatusCode);
            return null;
        }
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<GithubRelease>(json);
    }

    private static HttpClient CreateReleaseClient()
    {
        // Same proxy-bypass posture as VelopackUpdateService — keeps
        // misconfigured WinHTTP / WPAD from intercepting the call.
        var handler = new HttpClientHandler
        {
            UseProxy = false,
            UseDefaultCredentials = false,
            UseCookies = false,
            Credentials = null,
        };
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppBranding.AppName, AppBranding.Version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(GitHubApi.AcceptMediaType));
        client.Timeout = TimeSpan.FromSeconds(15);
        return client;
    }

    private sealed class GithubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }
    }
}
