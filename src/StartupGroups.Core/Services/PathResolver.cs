namespace StartupGroups.Core.Services;

public sealed class PathResolver : IPathResolver
{
    public string? Resolve(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        if (rawPath.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            return rawPath;
        }

        var expanded = Environment.ExpandEnvironmentVariables(rawPath);

        if (expanded.Contains('*') || expanded.Contains('?'))
        {
            return ResolveGlob(expanded);
        }

        return File.Exists(expanded) || Directory.Exists(expanded) ? expanded : null;
    }

    private static string? ResolveGlob(string pattern)
    {
        var segments = pattern.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        var root = Path.IsPathRooted(pattern) ? Path.GetPathRoot(pattern)! : Environment.CurrentDirectory;
        var candidates = new List<string> { root };

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            var isLast = i == segments.Length - 1;
            var next = new List<string>();

            foreach (var current in candidates)
            {
                if (!Directory.Exists(current))
                {
                    continue;
                }

                if (segment.Contains('*') || segment.Contains('?'))
                {
                    if (isLast)
                    {
                        try
                        {
                            next.AddRange(Directory.EnumerateFileSystemEntries(current, segment, SearchOption.TopDirectoryOnly));
                        }
                        catch (DirectoryNotFoundException)
                        {
                        }
                    }
                    else
                    {
                        try
                        {
                            next.AddRange(Directory.EnumerateDirectories(current, segment, SearchOption.TopDirectoryOnly));
                        }
                        catch (DirectoryNotFoundException)
                        {
                        }
                    }
                }
                else
                {
                    var combined = Path.Combine(current, segment);
                    if (isLast)
                    {
                        if (File.Exists(combined) || Directory.Exists(combined))
                        {
                            next.Add(combined);
                        }
                    }
                    else if (Directory.Exists(combined))
                    {
                        next.Add(combined);
                    }
                }
            }

            candidates = next;
            if (candidates.Count == 0)
            {
                return null;
            }
        }

        candidates.Sort(static (a, b) => string.Compare(b, a, StringComparison.OrdinalIgnoreCase));
        return candidates[0];
    }
}
