using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Salvo.Core.Branding;
using Salvo.Core.Models;

namespace Salvo.Core.Elevation;

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
            Arguments = $"{AppIdentifiers.PayloadCommandLineFlag} {payloadBase64}",
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
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED — the user declined the UAC prompt. Expected, not an error.
            _logger.LogInformation("Elevation declined by user (UAC cancelled)");
            return Task.FromResult(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Elevator helper failed to start");
            return Task.FromResult(false);
        }
    }

    private async Task<bool> WaitAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                _logger.LogError("Elevator exited with code {ExitCode}", process.ExitCode);
                return false;
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
