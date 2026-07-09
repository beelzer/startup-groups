using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Windows.Media.Imaging;

namespace Salvo.App.Services;

[SupportedOSPlatform("windows")]
internal static class AppIconCache
{
    private static readonly ConcurrentDictionary<string, BitmapSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public static BitmapSource? Get(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        if (_cache.TryGetValue(source, out var cached)) return cached;

        var bmp = ShellIconExtractor.GetImage(source, 32);
        _cache[source] = bmp;
        return bmp;
    }
}
