using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StartupGroups.Core.Branding;
using Velopack;
using Velopack.Sources;

namespace StartupGroups.App.Services;

/// <summary>
/// HttpClient handler config that bypasses any system-level interception:
/// no proxy (disregard WinHTTP / WPAD config), no Windows credentials, no
/// cookies. Without this, some Windows systems return 401 from a proxy
/// before the request ever reaches GitHub.
/// </summary>
internal sealed class CleanHttpClientFileDownloader : HttpClientFileDownloader
{
    protected override HttpClientHandler CreateHttpClientHandler() =>
        new()
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            UseProxy = false,
            UseDefaultCredentials = false,
            UseCookies = false,
            Credentials = null,
        };
}

public sealed record UpdateCheckResult(
    string CurrentVersion,
    string? LatestVersion,
    string? ReleaseUrl,
    string? ReleaseNotesMarkdown,
    bool IsUpdateAvailable,
    DateTimeOffset CheckedAt);

public interface IUpdateService
{
    /// <summary>
    /// True only when the running process is a Velopack-installed copy. False in dev
    /// (running from bin/), or when running from a non-Velopack install (e.g. the
    /// legacy MSI). Callers should fall back to opening the release page in the
    /// browser when this is false.
    /// </summary>
    bool CanUpdate { get; }

    Task<UpdateCheckResult?> CheckAsync(CancellationToken cancellationToken = default);

    Task DownloadAndApplyAsync(IProgress<int>? progress, CancellationToken cancellationToken = default);
}

public sealed class VelopackUpdateService : IUpdateService
{
    private readonly UpdateManager _manager;
    private readonly ILogger<VelopackUpdateService> _logger;
    private UpdateInfo? _pending;

    public VelopackUpdateService(ILogger<VelopackUpdateService> logger)
    {
        _logger = logger;
        var source = new GithubSource(
            AppBranding.SupportUrl,
            accessToken: null,
            prerelease: false,
            downloader: new CleanHttpClientFileDownloader());
        _manager = new UpdateManager(source);
    }

    public bool CanUpdate => _manager.IsInstalled;

    public async Task<UpdateCheckResult?> CheckAsync(CancellationToken cancellationToken = default)
    {
        var current = _manager.CurrentVersion?.ToString() ?? AppBranding.Version;

        if (!_manager.IsInstalled)
        {
            _logger.LogDebug("Not running under a Velopack install; skipping update check.");
            return new UpdateCheckResult(
                CurrentVersion: current,
                LatestVersion: null,
                ReleaseUrl: null,
                ReleaseNotesMarkdown: null,
                IsUpdateAvailable: false,
                CheckedAt: DateTimeOffset.Now);
        }

        try
        {
            var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info is null)
            {
                _pending = null;
                return new UpdateCheckResult(
                    CurrentVersion: current,
                    LatestVersion: current,
                    ReleaseUrl: null,
                    ReleaseNotesMarkdown: null,
                    IsUpdateAvailable: false,
                    CheckedAt: DateTimeOffset.Now);
            }

            _pending = info;
            var latest = info.TargetFullRelease.Version.ToString();
            return new UpdateCheckResult(
                CurrentVersion: current,
                LatestVersion: latest,
                ReleaseUrl: $"{AppBranding.SupportUrl}/releases/tag/v{latest}",
                ReleaseNotesMarkdown: info.TargetFullRelease.NotesMarkdown,
                IsUpdateAvailable: true,
                CheckedAt: DateTimeOffset.Now);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Update check failed.");
            // Still surface a result so the UI can display "Last checked at"
            // even on failure — silent null returns leave the field blank.
            return new UpdateCheckResult(
                CurrentVersion: current,
                LatestVersion: null,
                ReleaseUrl: null,
                ReleaseNotesMarkdown: null,
                IsUpdateAvailable: false,
                CheckedAt: DateTimeOffset.Now);
        }
    }

    public async Task DownloadAndApplyAsync(IProgress<int>? progress, CancellationToken cancellationToken = default)
    {
        if (!_manager.IsInstalled)
        {
            throw new InvalidOperationException("App is not running under a Velopack install; cannot apply updates.");
        }

        if (_pending is null)
        {
            // Re-check in case CheckAsync wasn't called recently.
            _pending = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (_pending is null)
            {
                _logger.LogDebug("No update available to download.");
                return;
            }
        }

        await _manager.DownloadUpdatesAsync(
            _pending,
            p => progress?.Report(p),
            cancelToken: cancellationToken).ConfigureAwait(false);

        // Restarts the process; we don't return from this in the normal case.
        _manager.ApplyUpdatesAndRestart(_pending);
    }
}
