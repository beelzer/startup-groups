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
    private static readonly TimeSpan Timeout = Timeouts.ElevatorServiceOperation;

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            var request = ParsePayload(args);
            if (request is null)
            {
                return 2;
            }

            return request.Action switch
            {
                ElevationAction.Start or ElevationAction.Stop => RunServiceAction(request),
                ElevationAction.WriteRegistryRunValue => RunRegistryWrite(request),
                ElevationAction.DeleteRegistryRunValue => RunRegistryDelete(request),
                _ => 2
            };
        }
        catch
        {
            return 3;
        }
    }

    private static int RunServiceAction(ElevationRequest request)
    {
        if (request.ServiceNames.Count == 0)
        {
            return 0;
        }

        var controller = new WindowsServiceController();
        var failures = 0;

        foreach (var service in request.ServiceNames)
        {
            var ok = request.Action == ElevationAction.Start
                ? controller.TryStart(service, Timeout, out _)
                : controller.TryStop(service, Timeout, out _);

            if (!ok)
            {
                failures++;
            }
        }

        return failures == 0 ? 0 : 1;
    }

    private static int RunRegistryWrite(ElevationRequest request)
    {
        if (request.RegistryEdit is null)
        {
            return 2;
        }

        var result = RegistryRunValueWriter.Write(request.RegistryEdit);
        return result.Succeeded ? 0 : 1;
    }

    private static int RunRegistryDelete(ElevationRequest request)
    {
        if (request.RegistryEdit is null)
        {
            return 2;
        }

        // Same shared writer the in-process HKCU path uses, so the elevator's
        // post-UAC delete can't drift from it (and inherits the HKCU 32-bit fix).
        var result = RegistryRunValueWriter.Delete(request.RegistryEdit.Source, request.RegistryEdit.OriginalName);
        return result.Succeeded ? 0 : 1;
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
