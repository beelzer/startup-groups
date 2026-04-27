using System.Runtime.Versioning;
using static StartupGroups.Core.Native.Toolhelp32Interop;

namespace StartupGroups.Core.Native;

[SupportedOSPlatform("windows")]
internal static class ProcessTreeSnapshot
{
    public readonly record struct ProcessEntry(int Pid, int ParentPid, string ExeName);

    public static IReadOnlyList<ProcessEntry> CaptureAll()
    {
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == INVALID_HANDLE_VALUE)
        {
            return Array.Empty<ProcessEntry>();
        }

        var entries = new List<ProcessEntry>(256);
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry))
            {
                return entries;
            }
            do
            {
                entries.Add(new ProcessEntry((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile ?? string.Empty));
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return entries;
    }

    public static IReadOnlyList<int> GetDescendantPids(int rootPid)
    {
        if (rootPid <= 0)
        {
            return Array.Empty<int>();
        }

        var entries = CaptureAll();
        if (entries.Count == 0)
        {
            return Array.Empty<int>();
        }

        var childrenByParent = new Dictionary<int, List<int>>(entries.Count);
        foreach (var e in entries)
        {
            if (!childrenByParent.TryGetValue(e.ParentPid, out var list))
            {
                list = new List<int>(2);
                childrenByParent[e.ParentPid] = list;
            }
            list.Add(e.Pid);
        }

        var result = new List<int>();
        var stack = new Stack<int>();
        stack.Push(rootPid);
        var visited = new HashSet<int> { rootPid };

        while (stack.Count > 0)
        {
            var pid = stack.Pop();
            if (!childrenByParent.TryGetValue(pid, out var children))
            {
                continue;
            }
            foreach (var child in children)
            {
                if (visited.Add(child))
                {
                    result.Add(child);
                    stack.Push(child);
                }
            }
        }

        return result;
    }
}
