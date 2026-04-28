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
    DateTimeOffset CheckedAt,
    long? DownloadSizeBytes = null,
    UpdateChannel Channel = UpdateChannel.Stable);

public interface IUpdateService
{
    /// <summary>
    /// True only when the running process is a Velopack-installed copy. False in dev
    /// (running from bin/), or when running from a non-Velopack install (e.g. the
    /// legacy MSI). Callers should fall back to opening the release page in the
    /// browser when this is false.
    /// </summary>
    bool CanUpdate { get; }

    /// <summary>
    /// Checks the current channel for an update. <paramref name="force"/> skips
    /// the on-disk feed cache so manual "Check now" clicks always hit the network.
    /// </summary>
    Task<UpdateCheckResult?> CheckAsync(bool force = false, CancellationToken cancellationToken = default);

    Task DownloadAndApplyAsync(IProgress<int>? progress, CancellationToken cancellationToken = default);
}

public sealed class VelopackUpdateService : IUpdateService
{
    private readonly ILogger<VelopackUpdateService> _logger;
    private readonly ISettingsStore _settings;
    private readonly object _gate = new();
    private UpdateManager _manager;
    private UpdateChannel _activeChannel;
    private UpdateInfo? _pending;

    public VelopackUpdateService(ILogger<VelopackUpdateService> logger, ISettingsStore settings)
    {
        _logger = logger;
        _settings = settings;
        _activeChannel = settings.Current.UpdateChannel;
        _manager = BuildManager(_activeChannel, bypassCache: false);

        settings.Changed += OnSettingsChanged;
    }

    public bool CanUpdate => _manager.IsInstalled;

    public async Task<UpdateCheckResult?> CheckAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        // Settings change events rebuild the manager; if the user toggled the
        // channel between the last call and now, ensure we're on the latest.
        var manager = AcquireManager(force);
        var current = manager.CurrentVersion?.ToString() ?? AppBranding.Version;
        var channel = _activeChannel;

        if (!manager.IsInstalled)
        {
            _logger.LogDebug("Not running under a Velopack install; skipping update check.");
            return new UpdateCheckResult(
                CurrentVersion: current,
                LatestVersion: null,
                ReleaseUrl: null,
                ReleaseNotesMarkdown: null,
                IsUpdateAvailable: false,
                CheckedAt: DateTimeOffset.Now,
                Channel: channel);
        }

        try
        {
            var info = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info is null)
            {
                _pending = null;
                return new UpdateCheckResult(
                    CurrentVersion: current,
                    LatestVersion: current,
                    ReleaseUrl: null,
                    ReleaseNotesMarkdown: null,
                    IsUpdateAvailable: false,
                    CheckedAt: DateTimeOffset.Now,
                    Channel: channel);
            }

            _pending = info;
            var latest = info.TargetFullRelease.Version.ToString();
            return new UpdateCheckResult(
                CurrentVersion: current,
                LatestVersion: latest,
                ReleaseUrl: $"{AppBranding.SupportUrl}/releases/tag/v{latest}",
                ReleaseNotesMarkdown: info.TargetFullRelease.NotesMarkdown,
                IsUpdateAvailable: true,
                CheckedAt: DateTimeOffset.Now,
                DownloadSizeBytes: info.TargetFullRelease.Size,
                Channel: channel);
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
                CheckedAt: DateTimeOffset.Now,
                Channel: channel);
        }
    }

    public async Task DownloadAndApplyAsync(IProgress<int>? progress, CancellationToken cancellationToken = default)
    {
        var manager = AcquireManager(force: false);
        if (!manager.IsInstalled)
        {
            throw new InvalidOperationException("App is not running under a Velopack install; cannot apply updates.");
        }

        if (_pending is null)
        {
            // Re-check in case CheckAsync wasn't called recently.
            _pending = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (_pending is null)
            {
                _logger.LogDebug("No update available to download.");
                return;
            }
        }

        await manager.DownloadUpdatesAsync(
            _pending,
            p => progress?.Report(p),
            cancelToken: cancellationToken).ConfigureAwait(false);

        // Restarts the process; we don't return from this in the normal case.
        // Pass a marker arg so the new instance knows it came from an update and
        // should open the main window (otherwise our launcher app would only
        // restart the tray icon, which is confusing right after the user clicked
        // "Install now").
        manager.ApplyUpdatesAndRestart(_pending, restartArgs: new[] { RestartedAfterUpdateArg });
    }

    public const string RestartedAfterUpdateArg = "--restarted-after-update";

    private UpdateManager AcquireManager(bool force)
    {
        var desired = _settings.Current.UpdateChannel;
        lock (_gate)
        {
            if (force || desired != _activeChannel)
            {
                _logger.LogInformation(
                    "Rebuilding UpdateManager for channel {Channel} (force={Force})", desired, force);
                _manager = BuildManager(desired, bypassCache: force);
                _activeChannel = desired;
                _pending = null;
            }
            return _manager;
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        // Cheap fast-path: if the channel didn't change, do nothing.
        if (settings.UpdateChannel == _activeChannel) return;
        // Defer rebuild to the next CheckAsync — avoids races with an
        // in-flight check on the old manager.
        _logger.LogDebug("Settings changed; channel {Old} → {New} will rebuild on next check.",
            _activeChannel, settings.UpdateChannel);
    }

    private static UpdateManager BuildManager(UpdateChannel channel, bool bypassCache)
    {
        // Velopack's --channel mechanism produces channel-suffixed manifest
        // assets (releases.win-<channel>.json). Stable rides the default
        // channel ("win") so existing v0.2.x installs — which shipped
        // without ExplicitChannel — keep finding stable updates. Beta and
        // nightly use distinct named channels.
        var explicitChannel = ToVelopackChannel(channel);
        var prerelease = channel != UpdateChannel.Stable;

        var source = new CachelessGithubSource(
            AppBranding.SupportUrl,
            accessToken: null,
            prerelease: prerelease,
            channel: explicitChannel ?? "stable",
            bypassCache: bypassCache,
            downloader: new CleanHttpClientFileDownloader());

        var options = new UpdateOptions
        {
            // Critical: without this, switching back from beta/nightly to
            // stable leaves users stranded on the higher beta version.
            // See ROADMAP "Phase 2 — Channels".
            AllowVersionDowngrade = true,
            ExplicitChannel = explicitChannel,
        };

        return new UpdateManager(source, options);
    }

    /// <summary>
    /// Maps our enum to Velopack channel names. Stable returns null so
    /// the UpdateManager uses Velopack's default channel ("win") — that's
    /// what every shipped v0.2.x installer was built against, and switching
    /// to a named "stable" channel would orphan those users.
    /// </summary>
    public static string? ToVelopackChannel(UpdateChannel channel) => channel switch
    {
        UpdateChannel.Beta => "beta",
        UpdateChannel.Nightly => "nightly",
        _ => null,
    };
}
