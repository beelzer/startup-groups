using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using StartupGroups.Core.Models;

namespace StartupGroups.Core.Services;

[SupportedOSPlatform("windows")]
public sealed class ProcessMatcherResolver : IProcessMatcherResolver
{
    private const string AppsFolderPrefix = "shell:AppsFolder\\";

    private readonly IPathResolver _pathResolver;
    private readonly ConcurrentDictionary<string, IReadOnlyList<ProcessMatcher>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ProcessMatcherResolver(IPathResolver pathResolver)
    {
        _pathResolver = pathResolver;
    }

    public IReadOnlyList<ProcessMatcher> GetMatchers(AppEntry app)
    {
        if (app.Kind != AppKind.Executable || string.IsNullOrWhiteSpace(app.Path))
        {
            return Array.Empty<ProcessMatcher>();
        }

        return _cache.GetOrAdd(app.Path!, Resolve);
    }

    private IReadOnlyList<ProcessMatcher> Resolve(string path)
    {
        var result = new List<ProcessMatcher>();

        if (path.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var parseName = path[AppsFolderPrefix.Length..];

            if (parseName.Contains('!'))
            {
                result.Add(ProcessMatcher.ByAumid(parseName));
            }
            else if (parseName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                var exe = Path.GetFileNameWithoutExtension(parseName);
                if (!string.IsNullOrEmpty(exe))
                {
                    result.Add(ProcessMatcher.ByExe(exe));
                }
            }

            if (result.Count == 0)
            {
                ResolveViaShell(parseName, result);
            }

            return result;
        }

        var resolved = _pathResolver.Resolve(path) ?? Environment.ExpandEnvironmentVariables(path);
        if (resolved.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return Resolve(resolved);
        }

        var name = Path.GetFileNameWithoutExtension(resolved);
        if (!string.IsNullOrEmpty(name))
        {
            result.Add(ProcessMatcher.ByExe(name));
        }
        return result;
    }

    private static void ResolveViaShell(string parseName, List<ProcessMatcher> result)
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null) return;

        dynamic? shell = null;
        dynamic? folder = null;
        dynamic? item = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell is null) return;

            folder = shell.NameSpace("shell:AppsFolder");
            if (folder is null) return;

            item = folder.ParseName(parseName);
            if (item is null) return;

            var aumid = SafeExtendedProperty(item, "System.AppUserModel.ID");
            if (!string.IsNullOrEmpty(aumid) && aumid.Contains('!'))
            {
                result.Add(ProcessMatcher.ByAumid(aumid));
            }

            var target = SafeExtendedProperty(item, "System.Link.TargetParsingPath");
            var args = SafeExtendedProperty(item, "System.Link.Arguments");

            var processStartName = ParseProcessStartArg(args);
            if (!string.IsNullOrEmpty(processStartName))
            {
                result.Add(ProcessMatcher.ByExe(processStartName));
            }

            if (!string.IsNullOrEmpty(target))
            {
                var targetExe = Path.GetFileNameWithoutExtension(target);
                if (!string.IsNullOrEmpty(targetExe))
                {
                    var matcher = ProcessMatcher.ByExe(targetExe);
                    if (!result.Contains(matcher))
                    {
                        result.Add(matcher);
                    }
                }
            }
        }
        catch
        {
        }
        finally
        {
            ReleaseCom(item);
            ReleaseCom(folder);
            ReleaseCom(shell);
        }
    }

    private static string? SafeExtendedProperty(dynamic item, string key)
    {
        try
        {
            return (string?)item.ExtendedProperty(key);
        }
        catch
        {
            return null;
        }
    }

    private static string? ParseProcessStartArg(string? args)
    {
        if (string.IsNullOrWhiteSpace(args)) return null;

        const string flag = "--processStart";
        var idx = args.IndexOf(flag, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var rest = args[(idx + flag.Length)..].TrimStart();
        if (rest.Length == 0) return null;

        var end = 0;
        while (end < rest.Length && !char.IsWhiteSpace(rest[end]))
        {
            end++;
        }

        var token = rest[..end].Trim('"');
        return string.IsNullOrWhiteSpace(token)
            ? null
            : Path.GetFileNameWithoutExtension(token);
    }

    private static void ReleaseCom(object? obj)
    {
        if (obj is null) return;
        try
        {
            if (Marshal.IsComObject(obj))
            {
                Marshal.FinalReleaseComObject(obj);
            }
        }
        catch
        {
        }
    }
}
