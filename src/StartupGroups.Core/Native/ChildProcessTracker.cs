using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static StartupGroups.Core.Native.JobObjectInterop;

namespace StartupGroups.Core.Native;

[SupportedOSPlatform("windows")]
internal sealed class ChildProcessTracker : IDisposable
{
    private readonly ILogger _logger;
    private IntPtr _jobHandle;
    private bool _disposed;

    public bool IsActive => _jobHandle != IntPtr.Zero;

    public ChildProcessTracker(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _jobHandle = CreateJobObjectW(IntPtr.Zero, null);
        if (_jobHandle == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            _logger.LogDebug("CreateJobObjectW failed: Win32={Error}", err);
            return;
        }

        TryConfigureBreakawayOk();
    }

    public bool TryAssign(IntPtr processHandle)
    {
        if (!IsActive || processHandle == IntPtr.Zero)
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

    public IReadOnlyList<int> EnumerateDescendantPids()
    {
        if (!IsActive)
        {
            return Array.Empty<int>();
        }

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

    private void TryConfigureBreakawayOk()
    {
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_SILENT_BREAKAWAY_OK,
            },
        };

        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(_jobHandle, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                var err = Marshal.GetLastWin32Error();
                _logger.LogDebug("SetInformationJobObject failed: Win32={Error}", err);
            }
        }
        catch (Win32Exception ex)
        {
            _logger.LogDebug(ex, "SetInformationJobObject threw");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
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
