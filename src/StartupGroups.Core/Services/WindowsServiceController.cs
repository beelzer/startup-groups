using System.ComponentModel;
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace StartupGroups.Core.Services;

[SupportedOSPlatform("windows")]
public sealed class WindowsServiceController : IServiceController
{
    public ServiceState QueryStatus(string serviceName)
    {
        try
        {
            using var controller = new System.ServiceProcess.ServiceController(serviceName);
            _ = controller.Status;
            return controller.Status switch
            {
                ServiceControllerStatus.Running => ServiceState.Running,
                ServiceControllerStatus.Stopped => ServiceState.Stopped,
                _ => ServiceState.Pending
            };
        }
        catch (InvalidOperationException)
        {
            return ServiceState.NotFound;
        }
    }

    public bool TryStart(string serviceName, TimeSpan timeout, out string message)
    {
        try
        {
            using var controller = new System.ServiceProcess.ServiceController(serviceName);
            if (controller.Status == ServiceControllerStatus.Running)
            {
                message = "Already running";
                return true;
            }

            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, timeout);
            message = "Started";
            return true;
        }
        catch (InvalidOperationException ex) when (IsAccessDenied(ex))
        {
            message = "Needs admin";
            return false;
        }
        catch (InvalidOperationException ex)
        {
            message = ex.Message;
            return false;
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            message = "Start timed out";
            return false;
        }
    }

    public bool TryStop(string serviceName, TimeSpan timeout, out string message)
    {
        try
        {
            using var controller = new System.ServiceProcess.ServiceController(serviceName);
            if (controller.Status == ServiceControllerStatus.Stopped)
            {
                message = "Already stopped";
                return true;
            }

            controller.Stop();
            controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
            message = "Stopped";
            return true;
        }
        catch (InvalidOperationException ex) when (IsAccessDenied(ex))
        {
            message = "Needs admin";
            return false;
        }
        catch (InvalidOperationException ex)
        {
            message = ex.Message;
            return false;
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            message = "Stop timed out";
            return false;
        }
    }

    private static bool IsAccessDenied(Exception ex)
    {
        var current = ex;
        while (current is not null)
        {
            if (current is Win32Exception win32 && win32.NativeErrorCode == 5)
            {
                return true;
            }
            current = current.InnerException;
        }
        return false;
    }
}
