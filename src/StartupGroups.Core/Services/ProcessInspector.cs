using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using StartupGroups.Core.Models;

namespace StartupGroups.Core.Services;

[SupportedOSPlatform("windows")]
public sealed class ProcessInspector : IProcessInspector
{
    public bool IsRunning(IReadOnlyList<ProcessMatcher> matchers)
    {
        if (matchers.Count == 0) return false;

        var exeNames = CollectExeNames(matchers);
        foreach (var name in exeNames)
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                if (processes.Length > 0) return true;
            }
            finally
            {
                DisposeAll(processes);
            }
        }

        var aumids = CollectAumids(matchers);
        if (aumids.Count == 0) return false;

        return FindProcessesByAumid(aumids).HasAny;
    }

    public IReadOnlyList<int> FindMatchingPids(IReadOnlyList<ProcessMatcher> matchers)
    {
        if (matchers.Count == 0) return Array.Empty<int>();

        var pids = new HashSet<int>();

        foreach (var name in CollectExeNames(matchers))
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                foreach (var p in processes)
                {
                    try { if (!p.HasExited) pids.Add(p.Id); }
                    catch (InvalidOperationException) { }
                }
            }
            finally
            {
                DisposeAll(processes);
            }
        }

        var aumids = CollectAumids(matchers);
        if (aumids.Count > 0)
        {
            var found = FindProcessesByAumid(aumids);
            try
            {
                foreach (var p in found.Processes)
                {
                    try { if (!p.HasExited) pids.Add(p.Id); }
                    catch (InvalidOperationException) { }
                }
            }
            finally
            {
                DisposeAll(found.Processes);
            }
        }

        return pids.Count == 0 ? Array.Empty<int>() : pids.ToArray();
    }

    public bool TryKill(IReadOnlyList<ProcessMatcher> matchers, out string message)
    {
        if (matchers.Count == 0)
        {
            message = "No process identifier";
            return false;
        }

        var killed = 0;
        var failed = 0;
        var killedPids = new HashSet<int>();

        foreach (var name in CollectExeNames(matchers))
        {
            var processes = Process.GetProcessesByName(name);
            KillProcesses(processes, killedPids, ref killed, ref failed);
        }

        var aumids = CollectAumids(matchers);
        if (aumids.Count > 0)
        {
            var found = FindProcessesByAumid(aumids);
            KillProcesses(found.Processes, killedPids, ref killed, ref failed);
        }

        if (killed == 0 && failed == 0)
        {
            message = "Not running";
            return true;
        }

        if (failed > 0 && killed == 0)
        {
            message = "Failed to stop process";
            return false;
        }

        message = killed == 1 ? "Stopped" : $"Stopped {killed} processes";
        return true;
    }

    private static HashSet<string> CollectExeNames(IReadOnlyList<ProcessMatcher> matchers) =>
        matchers
            .Where(m => !string.IsNullOrEmpty(m.ExeName))
            .Select(m => m.ExeName!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> CollectAumids(IReadOnlyList<ProcessMatcher> matchers) =>
        matchers
            .Where(m => !string.IsNullOrEmpty(m.Aumid))
            .Select(m => m.Aumid!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static (bool HasAny, List<Process> Processes) FindProcessesByAumid(HashSet<string> aumids)
    {
        var matches = new List<Process>();
        var all = Process.GetProcesses();
        try
        {
            foreach (var process in all)
            {
                try
                {
                    var aumid = AumidForProcess(process.Id);
                    if (aumid is not null && aumids.Contains(aumid))
                    {
                        matches.Add(process);
                        continue;
                    }
                }
                catch
                {
                }

                process.Dispose();
            }
        }
        catch
        {
        }
        return (matches.Count > 0, matches);
    }

    private static void KillProcesses(IEnumerable<Process> processes, HashSet<int> killedPids, ref int killed, ref int failed)
    {
        foreach (var process in processes)
        {
            try
            {
                if (!killedPids.Add(process.Id))
                {
                    continue;
                }

                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5_000);
                    killed++;
                }
                catch (Win32Exception)
                {
                    failed++;
                }
                catch (InvalidOperationException)
                {
                    killed++;
                }
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void DisposeAll(IEnumerable<Process> processes)
    {
        foreach (var process in processes)
        {
            process.Dispose();
        }
    }

    private static string? AumidForProcess(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            int len = 256;
            var buffer = new char[len];
            int rc = GetApplicationUserModelId(handle, ref len, buffer);
            if (rc != 0 || len <= 1) return null;
            return new string(buffer, 0, len - 1);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(IntPtr hProcess, ref int AppModelIDLength, [Out] char[] AppModelID);
}
