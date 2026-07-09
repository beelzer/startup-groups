using System.Text;
using System.Text.Json;
using Salvo.Core.Branding;
using Salvo.Core.Elevation;
using Salvo.Core.Models;
using Salvo.Core.Services;
using Salvo.Core.WindowsStartup;

namespace Salvo.Elevator;

internal static class Program
{
    // Exit codes returned to ElevationClient (which today only distinguishes
    // 0 from non-0, but the codes are kept meaningful for the failure log):
    private const int Success = 0;       // action completed
    private const int Failed = 1;        // action attempted but one or more parts failed
    private const int BadRequest = 2;    // payload missing/unparseable or unknown action
    private const int Unhandled = 3;     // unexpected exception

    // Overall budget for the whole elevated request, shared across all
    // services — bounds worst-case elevated runtime instead of granting a
    // fresh per-service timeout that scales with the service count.
    private static readonly TimeSpan RequestBudget = Timeouts.ElevatorServiceOperation;

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var request = ParsePayload(args);
            if (request is null)
            {
                return BadRequest;
            }

            return request.Action switch
            {
                ElevationAction.Start or ElevationAction.Stop => RunServiceAction(request),
                ElevationAction.WriteRegistryRunValue => RunRegistryWrite(request),
                ElevationAction.DeleteRegistryRunValue => RunRegistryDelete(request),
                _ => BadRequest
            };
        }
        catch (Exception ex)
        {
            LogFailure($"Unhandled elevator exception: {ex}");
            return Unhandled;
        }
    }

    private static int RunServiceAction(ElevationRequest request)
    {
        if (request.ServiceNames.Count == 0)
        {
            return Success;
        }

        var controller = new WindowsServiceController();
        var deadline = DateTimeOffset.UtcNow + RequestBudget;
        var failures = 0;

        foreach (var service in request.ServiceNames)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                LogFailure($"{request.Action} '{service}' skipped: overall elevation budget exhausted");
                failures++;
                continue;
            }

            string message;
            var ok = request.Action == ElevationAction.Start
                ? controller.TryStart(service, remaining, out message)
                : controller.TryStop(service, remaining, out message);

            if (!ok)
            {
                LogFailure($"{request.Action} '{service}' failed: {message}");
                failures++;
            }
        }

        return failures == 0 ? Success : Failed;
    }

    // Best-effort failure log for the otherwise-silent hidden elevated process.
    // A plain text file under %LOCALAPPDATA% is used rather than an EventLog
    // source (registering one is itself a privileged registry op that adds
    // failure surface). Never lets a logging failure change the exit code.
    private static void LogFailure(string message)
    {
        try
        {
            var dir = AppPaths.LogFolder;
            Directory.CreateDirectory(dir);
            var line = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow:O}\t{message}{Environment.NewLine}");
            File.AppendAllText(Path.Combine(dir, "elevator.log"), line);
        }
        catch
        {
            // Diagnostics are best-effort.
        }
    }

    private static int RunRegistryWrite(ElevationRequest request)
    {
        if (request.RegistryEdit is null)
        {
            return BadRequest;
        }

        var result = RegistryRunValueWriter.Write(request.RegistryEdit);
        if (!result.Succeeded)
        {
            LogFailure($"Registry write '{request.RegistryEdit.NewName}' failed: {result.Message}");
        }
        return result.Succeeded ? Success : Failed;
    }

    private static int RunRegistryDelete(ElevationRequest request)
    {
        if (request.RegistryEdit is null)
        {
            return BadRequest;
        }

        // Same shared writer the in-process HKCU path uses, so the elevator's
        // post-UAC delete can't drift from it (and inherits the HKCU 32-bit fix).
        var result = RegistryRunValueWriter.Delete(request.RegistryEdit.Source, request.RegistryEdit.OriginalName);
        if (!result.Succeeded)
        {
            LogFailure($"Registry delete '{request.RegistryEdit.OriginalName}' failed: {result.Message}");
        }
        return result.Succeeded ? Success : Failed;
    }

    private static ElevationRequest? ParsePayload(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], AppIdentifiers.PayloadCommandLineFlag, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var bytes = Convert.FromBase64String(args[i + 1]);
                    var json = Encoding.UTF8.GetString(bytes);
                    return JsonSerializer.Deserialize(json, ConfigurationJsonContext.Default.ElevationRequest);
                }
                catch (FormatException)
                {
                    return null;
                }
                catch (JsonException)
                {
                    return null;
                }
            }
        }

        return null;
    }
}
