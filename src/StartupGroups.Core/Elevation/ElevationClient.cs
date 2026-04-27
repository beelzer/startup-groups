using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StartupGroups.Core.Models;

namespace StartupGroups.Core.Elevation;

[SupportedOSPlatform("windows")]
public sealed class ElevationClient : IElevationClient
{
    private readonly string _elevatorExecutablePath;
    private readonly ILogger<ElevationClient> _logger;

    public ElevationClient(string elevatorExecutablePath, ILogger<ElevationClient>? logger = null)
    {
        _elevatorExecutablePath = elevatorExecutablePath;
        _logger = logger ?? NullLogger<ElevationClient>.Instance;
    }

    public bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public Task<bool> InvokeAsync(ElevationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!File.Exists(_elevatorExecutablePath))
        {
            _logger.LogError("Elevator helper missing at {Path}", _elevatorExecutablePath);
            return Task.FromResult(false);
        }

        var isServiceAction = request.Action is ElevationAction.Start or ElevationAction.Stop;
        if (isServiceAction && request.ServiceNames.Count == 0)
        {
            return Task.FromResult(true);
        }

        var isRegistryAction = request.Action
            is ElevationAction.WriteRegistryRunValue
            or ElevationAction.DeleteRegistryRunValue;
        if (isRegistryAction && request.RegistryEdit is null)
        {
            return Task.FromResult(false);
        }

        var payload = JsonSerializer.Serialize(request, ConfigurationJsonContext.Default.ElevationRequest);
        var payloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));

        var startInfo = new ProcessStartInfo
        {
            FileName = _elevatorExecutablePath,
            Arguments = $"--payload {payloadBase64}",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return Task.FromResult(false);
            }

            return WaitAsync(process, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "User cancelled elevation or helper failed to start");
            return Task.FromResult(false);
        }
    }

    private static async Task<bool> WaitAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
