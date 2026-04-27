using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32.TaskScheduler;
using StartupGroups.Core.Branding;
using StartupGroups.Core.Services;

namespace StartupGroups.App.Services;

[SupportedOSPlatform("windows")]
public sealed class TaskSchedulerAutoStartService : IAutoStartService
{
    private static readonly TimeSpan StartupDelay = Timeouts.AutoStartDelay;

    public bool IsEnabled()
    {
        using var ts = new TaskService();
        return ts.FindTask(AppIdentifiers.TrayTaskName) is not null;
    }

    public bool IsEnabledElevated()
    {
        using var ts = new TaskService();
        var task = ts.FindTask(AppIdentifiers.TrayTaskName);
        return task?.Definition.Principal.RunLevel == TaskRunLevel.Highest;
    }

    public void Enable(bool runElevated = false)
    {
        using var ts = new TaskService();
        var executablePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(executablePath))
        {
            return;
        }

        var definition = ts.NewTask();
        definition.RegistrationInfo.Description = $"{AppBranding.AppName} tray icon";
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.StartWhenAvailable = true;
        definition.Settings.ExecutionTimeLimit = TimeSpan.Zero;
        definition.Principal.LogonType = TaskLogonType.InteractiveToken;
        definition.Principal.RunLevel = runElevated ? TaskRunLevel.Highest : TaskRunLevel.LUA;

        definition.Triggers.Add(new LogonTrigger { UserId = Environment.UserName, Delay = StartupDelay });
        definition.Actions.Add(new ExecAction(executablePath, AppIdentifiers.TrayCommandLineFlag, Path.GetDirectoryName(executablePath)));

        ts.RootFolder.RegisterTaskDefinition(AppIdentifiers.TrayTaskName, definition);
    }

    public void Disable()
    {
        using var ts = new TaskService();
        var task = ts.FindTask(AppIdentifiers.TrayTaskName);
        if (task is not null)
        {
            ts.RootFolder.DeleteTask(AppIdentifiers.TrayTaskName, false);
        }
    }
}
