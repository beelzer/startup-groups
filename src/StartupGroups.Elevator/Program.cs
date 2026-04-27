using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using StartupGroups.Core.Elevation;
using StartupGroups.Core.Models;
using StartupGroups.Core.Services;
using StartupGroups.Core.WindowsStartup;

namespace StartupGroups.Elevator;

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

        try
        {
            DeleteRunValue(request.RegistryEdit.Source, request.RegistryEdit.OriginalName);
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    private static void DeleteRunValue(StartupEntrySource source, string name)
    {
        const string runPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string approvedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        const string approvedRun32 = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";

        var (root, path, approvedRoot, approvedPath) = source switch
        {
            StartupEntrySource.RegistryRunUser => (Registry.CurrentUser, runPath, Registry.CurrentUser, approvedRun),
            StartupEntrySource.RegistryRunUser32 => (RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32), runPath, Registry.CurrentUser, approvedRun32),
            StartupEntrySource.RegistryRunMachine => (Registry.LocalMachine, runPath, Registry.LocalMachine, approvedRun),
            StartupEntrySource.RegistryRunMachine32 => (RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32), runPath, Registry.LocalMachine, approvedRun32),
            _ => (null!, string.Empty, null!, string.Empty)
        };

        if (root is null) return;

        using (var runKey = root.OpenSubKey(path, writable: true))
        {
            runKey?.DeleteValue(name, throwOnMissingValue: false);
        }

        using var approved = approvedRoot.OpenSubKey(approvedPath, writable: true);
        approved?.DeleteValue(name, throwOnMissingValue: false);
    }

    private static ElevationRequest? ParsePayload(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--payload", StringComparison.OrdinalIgnoreCase))
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
