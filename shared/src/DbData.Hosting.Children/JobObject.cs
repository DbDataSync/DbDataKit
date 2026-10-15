using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DbData.Hosting.Children;

/// <summary>Windows Job Object with KILL_ON_JOB_CLOSE: closing the handle (or the host dying) kills every process in it.</summary>
[SupportedOSPlatform("windows")]
public sealed class JobObject : IDisposable
{
    private const uint KillOnJobClose = 0x2000;
    private const uint ProcessMemoryLimit = 0x100;
    private IntPtr _handle;

    public JobObject(long? memoryLimitBytes = null, int? cpuPercent = null)
    {
        _handle = CreateJobObjectW(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        var info = new ExtendedLimitInformation();
        info.Basic.LimitFlags = KillOnJobClose;
        if (memoryLimitBytes is { } m)
        {
            info.Basic.LimitFlags |= ProcessMemoryLimit;
            info.ProcessMemoryLimit = (UIntPtr)(ulong)m;
        }

        Set(9, info);
        if (cpuPercent is { } cpu)
        {
            // ENABLE | HARD_CAP; CpuRate is in 1/100 of a percent.
            Set(15, new CpuRateControl { ControlFlags = 0x1 | 0x4, CpuRate = (uint)(Math.Clamp(cpu, 1, 100) * 100) });
        }
    }

    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(_handle, process.Handle))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Dispose()
    {
        var h = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (h != IntPtr.Zero) CloseHandle(h); // kills the members
    }

    private void Set<T>(int infoClass, T value) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var p = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, p, false);
            if (!SetInformationJobObject(_handle, infoClass, p, (uint)size))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong a, b, c, d, e, f;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CpuRateControl
    {
        public uint ControlFlags;
        public uint CpuRate;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
