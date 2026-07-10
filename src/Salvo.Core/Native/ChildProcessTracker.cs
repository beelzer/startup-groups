using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static Salvo.Core.Native.JobObjectInterop;

namespace Salvo.Core.Native;

[SupportedOSPlatform("windows")]
internal sealed class ChildProcessTracker : IDisposable
{
    // Guards the job handle against the dispose race: a cancelled probe
    // draining out can still call EnumerateDescendantPids while the
    // session tears down, and querying a closed (OS-recyclable) handle
    // is a check-then-use bug. All handle use happens under this lock.
    private readonly object _lock = new();
    private readonly ILogger _logger;
    private IntPtr _jobHandle;
    private bool _disposed;

    public bool IsActive { get { lock (_lock) return _jobHandle != IntPtr.Zero; } }

    // The job is deliberately created with NO limit flags. In particular,
    // JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK must never be set here: its
    // semantics are that every child of a job member is created OUTSIDE the
    // job, which reduces EnumerateDescendantPids to "the root pid" and blinds
    // readiness/telemetry to the real app behind launcher stubs. Children
    // staying in the job has no side effects — we set no KILL_ON_JOB_CLOSE,
    // and nested jobs are supported on our OS floor, so apps that create
    // their own jobs still work.
    public ChildProcessTracker(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _jobHandle = CreateJobObjectW(IntPtr.Zero, null);
        if (_jobHandle == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            _logger.LogDebug("CreateJobObjectW failed: Win32={Error}", err);
        }
    }

    public bool TryAssign(IntPtr processHandle)
    {
        lock (_lock)
        {
            if (_jobHandle == IntPtr.Zero || processHandle == IntPtr.Zero)
            {
                return false;
            }

            if (AssignProcessToJobObject(_jobHandle, processHandle))
            {
                return true;
            }

            var err = Marshal.GetLastWin32Error();
            _logger.LogDebug("AssignProcessToJobObject failed: Win32={Error}", err);
            return false;
        }
    }

    public IReadOnlyList<int> EnumerateDescendantPids()
    {
        lock (_lock)
        {
            return _jobHandle == IntPtr.Zero ? Array.Empty<int>() : EnumerateDescendantPidsCore();
        }
    }

    private IReadOnlyList<int> EnumerateDescendantPidsCore()
    {
        var capacity = 64;
        while (true)
        {
            var headerSize = Marshal.SizeOf<JOBOBJECT_BASIC_PROCESS_ID_LIST>();
            var bufferSize = headerSize + (capacity * IntPtr.Size);
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                if (!QueryInformationJobObject(_jobHandle, JobObjectBasicProcessIdList, buffer, (uint)bufferSize, out _))
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err == ERROR_MORE_DATA)
                    {
                        capacity *= 2;
                        continue;
                    }
                    _logger.LogDebug("QueryInformationJobObject failed: Win32={Error}", err);
                    return Array.Empty<int>();
                }

                var header = Marshal.PtrToStructure<JOBOBJECT_BASIC_PROCESS_ID_LIST>(buffer);
                var count = (int)header.NumberOfProcessIdsInList;
                if (count == 0)
                {
                    return Array.Empty<int>();
                }

                if (count > capacity)
                {
                    capacity = count;
                    continue;
                }

                var arrayBase = IntPtr.Add(buffer, headerSize);
                var pids = new int[count];
                for (var i = 0; i < count; i++)
                {
                    var value = Marshal.ReadIntPtr(arrayBase, i * IntPtr.Size);
                    pids[i] = (int)value.ToInt64();
                }
                return pids;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            if (_jobHandle != IntPtr.Zero)
            {
                CloseHandle(_jobHandle);
                _jobHandle = IntPtr.Zero;
            }
        }
    }
}
