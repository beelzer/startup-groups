using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using StartupGroups.Core.Models;

namespace StartupGroups.Core.Services;

[SupportedOSPlatform("windows")]
public sealed class WindowsServicesProvider : IInstalledAppsProvider
{
    private readonly ILogger<WindowsServicesProvider> _logger;

    public WindowsServicesProvider(ILogger<WindowsServicesProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<WindowsServicesProvider>.Instance;
    }

    public Task<IReadOnlyList<InstalledApp>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<InstalledApp>>(() => EnumerateCore(cancellationToken), cancellationToken);
    }

    private IReadOnlyList<InstalledApp> EnumerateCore(CancellationToken cancellationToken)
    {
        var results = new List<InstalledApp>(capacity: 256);

        try
        {
            var services = System.ServiceProcess.ServiceController.GetServices();
            foreach (var controller in services)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (!IsUserRelevant(controller))
                    {
                        continue;
                    }

                    var displayName = string.IsNullOrWhiteSpace(controller.DisplayName)
                        ? controller.ServiceName
                        : controller.DisplayName;

                    var imagePath = TryResolveImagePath(controller.ServiceName);

                    results.Add(new InstalledApp(
                        Name: displayName,
                        Launch: controller.ServiceName,
                        ExecutablePath: imagePath,
                        IconPath: imagePath,
                        Source: InstalledAppSource.Service,
                        ParsingName: controller.ServiceName,
                        ServiceName: controller.ServiceName));
                }
                catch (Exception ex)
                {
                    _logger.LogTrace(ex, "Skipped service {Name}", controller.ServiceName);
                }
                finally
                {
                    controller.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate Windows services");
        }

        results.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return results;
    }

    public static string? TryResolveImagePath(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            if (key?.GetValue("ImagePath") is not string raw || string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var expanded = Environment.ExpandEnvironmentVariables(raw);
            var exe = ExtractExecutable(expanded);
            return exe is not null && File.Exists(exe) ? exe : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractExecutable(string imagePath)
    {
        var trimmed = imagePath.TrimStart();

        if (trimmed.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            trimmed = trimmed[4..];
        }

        if (trimmed.StartsWith('"'))
        {
            var close = trimmed.IndexOf('"', 1);
            return close > 1 ? trimmed[1..close] : null;
        }

        var space = trimmed.IndexOf(' ');
        return space < 0 ? trimmed : trimmed[..space];
    }

    private static bool IsUserRelevant(System.ServiceProcess.ServiceController controller)
    {
        var type = controller.ServiceType;
        if (type.HasFlag(ServiceType.KernelDriver)
            || type.HasFlag(ServiceType.FileSystemDriver)
            || type.HasFlag(ServiceType.Adapter)
            || type.HasFlag(ServiceType.RecognizerDriver))
        {
            return false;
        }

        return type.HasFlag(ServiceType.Win32OwnProcess)
            || type.HasFlag(ServiceType.Win32ShareProcess)
            || type.HasFlag(ServiceType.InteractiveProcess);
    }
}
