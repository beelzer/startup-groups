using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StartupGroups.Core.Models;

namespace StartupGroups.Core.Services;

[SupportedOSPlatform("windows")]
public sealed class ScoopInstalledAppsProvider : IInstalledAppsProvider
{
    private readonly ILogger<ScoopInstalledAppsProvider> _logger;
    private readonly IReadOnlyList<string> _rootsOverride;

    public ScoopInstalledAppsProvider(ILogger<ScoopInstalledAppsProvider>? logger = null)
        : this(logger, rootsOverride: null)
    {
    }

    internal ScoopInstalledAppsProvider(
        ILogger<ScoopInstalledAppsProvider>? logger,
        IReadOnlyList<string>? rootsOverride)
    {
        _logger = logger ?? NullLogger<ScoopInstalledAppsProvider>.Instance;
        _rootsOverride = rootsOverride ?? [];
    }

    public Task<IReadOnlyList<InstalledApp>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<InstalledApp>>(() => EnumerateCore(cancellationToken), cancellationToken);
    }

    private IReadOnlyList<InstalledApp> EnumerateCore(CancellationToken cancellationToken)
    {
        var results = new List<InstalledApp>();
        var roots = _rootsOverride.Count > 0 ? _rootsOverride : DefaultRoots();

        foreach (var (root, kind) in roots.Select(r => (r, ClassifyRoot(r))))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var appsDir = Path.Combine(root, "apps");
            if (!Directory.Exists(appsDir))
            {
                continue;
            }

            var shimsDir = Path.Combine(root, "shims");

            string[] appFolders;
            try
            {
                appFolders = Directory.GetDirectories(appsDir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enumerate Scoop apps under {Root}", appsDir);
                continue;
            }

            foreach (var appFolder in appFolders)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var app = TryBuildApp(appFolder, shimsDir, kind);
                    if (app is not null)
                    {
                        results.Add(app);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogTrace(ex, "Skipped Scoop app at {Path}", appFolder);
                }
            }
        }

        results.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return results;
    }

    private static IReadOnlyList<string> DefaultRoots()
    {
        var roots = new List<string>(2);

        var userRoot = Environment.GetEnvironmentVariable("SCOOP");
        if (string.IsNullOrWhiteSpace(userRoot))
        {
            userRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "scoop");
        }
        roots.Add(userRoot);

        var globalRoot = Environment.GetEnvironmentVariable("SCOOP_GLOBAL");
        if (string.IsNullOrWhiteSpace(globalRoot))
        {
            globalRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "scoop");
        }
        if (!string.Equals(Path.GetFullPath(globalRoot), Path.GetFullPath(userRoot), StringComparison.OrdinalIgnoreCase))
        {
            roots.Add(globalRoot);
        }

        return roots;
    }

    private static string ClassifyRoot(string root)
    {
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return !string.IsNullOrEmpty(common)
               && root.StartsWith(common, StringComparison.OrdinalIgnoreCase)
            ? "global"
            : "user";
    }

    private InstalledApp? TryBuildApp(string appFolder, string shimsDir, string rootKind)
    {
        var appName = Path.GetFileName(appFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(appName) || string.Equals(appName, "scoop", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var currentDir = Path.Combine(appFolder, "current");
        if (!Directory.Exists(currentDir))
        {
            return null;
        }

        var manifestPath = Path.Combine(currentDir, "manifest.json");
        var manifest = TryReadManifest(manifestPath);

        var launchExe = ResolveLaunchTarget(currentDir, shimsDir, appName, manifest);
        if (launchExe is null)
        {
            return null;
        }

        return new InstalledApp(
            Name: appName,
            Launch: launchExe,
            ExecutablePath: launchExe,
            IconPath: launchExe,
            Source: InstalledAppSource.Scoop,
            ParsingName: $"scoop:{rootKind}:{appName}",
            Publisher: null);
    }

    private string? ResolveLaunchTarget(string currentDir, string shimsDir, string appName, ScoopManifest? manifest)
    {
        // Prefer the real exe from manifest's first bin entry — gives real icon + working directory.
        foreach (var bin in EnumerateBinCandidates(manifest))
        {
            var full = Path.Combine(currentDir, bin);
            if (File.Exists(full) && full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return full;
            }
        }

        // Fall back to the shim (handles script-based manifests where bin is a .ps1/.cmd/.bat).
        if (Directory.Exists(shimsDir))
        {
            var shim = Path.Combine(shimsDir, appName + ".exe");
            if (File.Exists(shim))
            {
                return shim;
            }

            // Some apps shim under the bin's alias rather than the app name.
            foreach (var bin in EnumerateBinCandidates(manifest))
            {
                var stem = Path.GetFileNameWithoutExtension(bin);
                var shimByBin = Path.Combine(shimsDir, stem + ".exe");
                if (File.Exists(shimByBin))
                {
                    return shimByBin;
                }
            }
        }

        // Last resort: any exe directly under current\ matching the app name.
        var guess = Path.Combine(currentDir, appName + ".exe");
        return File.Exists(guess) ? guess : null;
    }

    private static IEnumerable<string> EnumerateBinCandidates(ScoopManifest? manifest)
    {
        if (manifest?.Bin is null)
        {
            yield break;
        }
        foreach (var b in manifest.Bin)
        {
            if (!string.IsNullOrWhiteSpace(b))
            {
                yield return b.Replace('/', Path.DirectorySeparatorChar);
            }
        }
    }

    private ScoopManifest? TryReadManifest(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            var bins = new List<string>();
            if (root.TryGetProperty("bin", out var binEl))
            {
                CollectBinStrings(binEl, bins);
            }

            return new ScoopManifest(bins);
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "Could not parse Scoop manifest at {Path}", path);
            return null;
        }
    }

    private static void CollectBinStrings(JsonElement el, List<string> into)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                var s = el.GetString();
                if (!string.IsNullOrWhiteSpace(s)) into.Add(s);
                break;
            case JsonValueKind.Array:
                foreach (var child in el.EnumerateArray())
                {
                    if (child.ValueKind == JsonValueKind.String)
                    {
                        var cs = child.GetString();
                        if (!string.IsNullOrWhiteSpace(cs)) into.Add(cs);
                    }
                    else if (child.ValueKind == JsonValueKind.Array)
                    {
                        // Nested form: [path, alias, args...] — first element is the real path.
                        foreach (var inner in child.EnumerateArray())
                        {
                            if (inner.ValueKind == JsonValueKind.String)
                            {
                                var ins = inner.GetString();
                                if (!string.IsNullOrWhiteSpace(ins)) into.Add(ins);
                            }
                            break;
                        }
                    }
                }
                break;
        }
    }

    private sealed record ScoopManifest(IReadOnlyList<string> Bin);
}
