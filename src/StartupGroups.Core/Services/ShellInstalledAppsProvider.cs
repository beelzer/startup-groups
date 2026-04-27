using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StartupGroups.Core.Models;

namespace StartupGroups.Core.Services;

[SupportedOSPlatform("windows")]
public sealed class ShellInstalledAppsProvider : IInstalledAppsProvider
{
    private const string AppsFolderPath = "shell:AppsFolder";

    private readonly ILogger<ShellInstalledAppsProvider> _logger;

    public ShellInstalledAppsProvider(ILogger<ShellInstalledAppsProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<ShellInstalledAppsProvider>.Instance;
    }

    public Task<IReadOnlyList<InstalledApp>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        return RunOnStaAsync(() => EnumerateCore(cancellationToken), cancellationToken);
    }

    private IReadOnlyList<InstalledApp> EnumerateCore(CancellationToken cancellationToken)
    {
        var results = new List<InstalledApp>(capacity: 256);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null)
        {
            _logger.LogWarning("Shell.Application COM type unavailable");
            return results;
        }

        dynamic? shell = null;
        dynamic? appsFolder = null;
        try
        {
            shell = Activator.CreateInstance(shellType)!;
            appsFolder = shell.NameSpace(AppsFolderPath);
            if (appsFolder is null)
            {
                _logger.LogWarning("Shell AppsFolder namespace returned null");
                return results;
            }

            dynamic items = appsFolder.Items();
            int count = items.Count;
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                dynamic? item = null;
                try
                {
                    item = items.Item(i);
                    if (item is null)
                    {
                        continue;
                    }

                    string name = SafeString(() => (string)item.Name);
                    string parseName = SafeString(() => (string)item.Path);
                    bool isFolder = SafeBool(() => (bool)item.IsFolder);

                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(parseName))
                    {
                        continue;
                    }

                    if (parseName.StartsWith("::{", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!seen.Add(parseName))
                    {
                        continue;
                    }

                    var source = ClassifySource(parseName, out var executablePath);
                    var launch = source == InstalledAppSource.Desktop && executablePath is not null
                        ? executablePath
                        : $"shell:AppsFolder\\{parseName}";

                    results.Add(new InstalledApp(
                        Name: name,
                        Launch: launch,
                        ExecutablePath: executablePath,
                        IconPath: executablePath,
                        Source: source,
                        ParsingName: $"shell:AppsFolder\\{parseName}"));
                }
                catch (Exception ex)
                {
                    _logger.LogTrace(ex, "Skipped AppsFolder item at index {Index}", i);
                }
                finally
                {
                    ReleaseCom(item);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate shell:AppsFolder");
        }
        finally
        {
            ReleaseCom(appsFolder);
            ReleaseCom(shell);
        }

        results.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return results;
    }

    private static InstalledAppSource ClassifySource(string parseName, out string? executablePath)
    {
        executablePath = null;

        if (parseName.Contains('!') && !parseName.Contains(':') && !parseName.Contains('\\'))
        {
            return InstalledAppSource.Uwp;
        }

        if (File.Exists(parseName))
        {
            executablePath = parseName;
            return InstalledAppSource.Desktop;
        }

        var expanded = Environment.ExpandEnvironmentVariables(parseName);
        if (!ReferenceEquals(expanded, parseName) && File.Exists(expanded))
        {
            executablePath = expanded;
            return InstalledAppSource.Desktop;
        }

        return InstalledAppSource.Other;
    }

    private static string SafeString(Func<string?> getter)
    {
        try { return getter() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static bool SafeBool(Func<bool> getter)
    {
        try { return getter(); }
        catch { return false; }
    }

    private static void ReleaseCom(object? comObject)
    {
        if (comObject is null) return;
        try
        {
            if (System.Runtime.InteropServices.Marshal.IsComObject(comObject))
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(comObject);
            }
        }
        catch
        {
        }
    }

    private static Task<T> RunOnStaAsync<T>(Func<T> func, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(cancellationToken);
                    return;
                }
                tcs.TrySetResult(func());
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "ShellAppsFolder-STA"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
