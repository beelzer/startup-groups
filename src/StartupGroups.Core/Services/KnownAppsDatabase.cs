using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StartupGroups.Core.Models;

namespace StartupGroups.Core.Services;

public interface IKnownAppsDatabase
{
    KnownApp? FindMatch(AppEntry app, IReadOnlyList<ProcessMatcher> matchers);
}

public sealed class KnownAppsDatabase : IKnownAppsDatabase
{
    private const string ResourceName = "StartupGroups.Core.Data.KnownApps.json";

    private readonly Lazy<List<KnownApp>> _entries;
    private readonly ILogger<KnownAppsDatabase> _logger;

    public KnownAppsDatabase(ILogger<KnownAppsDatabase>? logger = null)
    {
        _logger = logger ?? NullLogger<KnownAppsDatabase>.Instance;
        _entries = new Lazy<List<KnownApp>>(Load, isThreadSafe: true);
    }

    public KnownApp? FindMatch(AppEntry app, IReadOnlyList<ProcessMatcher> matchers)
    {
        var exeNames = matchers
            .Where(m => !string.IsNullOrEmpty(m.ExeName))
            .Select(m => m.ExeName!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var aumids = matchers
            .Where(m => !string.IsNullOrEmpty(m.Aumid))
            .Select(m => m.Aumid!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var shellParse = TryGetShellParseName(app.Path);

        foreach (var entry in _entries.Value)
        {
            var m = entry.Match;

            if (!string.IsNullOrEmpty(m.ExeName) && exeNames.Contains(m.ExeName))
            {
                return entry;
            }

            if (!string.IsNullOrEmpty(m.Aumid) && aumids.Contains(m.Aumid))
            {
                return entry;
            }

            if (!string.IsNullOrEmpty(m.ShellParseName)
                && !string.IsNullOrEmpty(shellParse)
                && string.Equals(m.ShellParseName, shellParse, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private static string? TryGetShellParseName(string? path)
    {
        const string prefix = "shell:AppsFolder\\";
        return !string.IsNullOrWhiteSpace(path) && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? path[prefix.Length..]
            : null;
    }

    private List<KnownApp> Load()
    {
        try
        {
            var assembly = typeof(KnownAppsDatabase).Assembly;
            using var stream = assembly.GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                _logger.LogWarning("Embedded KnownApps.json not found");
                return [];
            }

            var file = JsonSerializer.Deserialize<KnownAppsFile>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return file?.Entries ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load KnownApps.json");
            return [];
        }
    }
}
