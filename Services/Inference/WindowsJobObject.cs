using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Singularity.Services.Inference;

/// <summary>
/// A Windows job object with KILL_ON_JOB_CLOSE: every process assigned to it, and every process
/// those start, is terminated when the handle closes. That includes the app crashing. Used so a
/// worker stuck in a CUDA call can't outlive the app and hold the GPU.
/// </summary>
internal sealed class WindowsJobObject : IDisposable
{
    private IntPtr _handle;

    private WindowsJobObject(IntPtr handle) => _handle = handle;

    /// <summary>Null off Windows or when the job can't be created; callers then rely on Process.Kill.</summary>
    public static WindowsJobObject? TryCreateKillOnClose()
    {
        if (!OperatingSystem.IsWindows()) return null;

        var handle = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero) return null;

        var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = { LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE },
        };
        if (!NativeMethods.SetInformationJobObject(handle, NativeMethods.JobObjectExtendedLimitInformation,
                ref info, (uint)Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            NativeMethods.CloseHandle(handle);
            return null;
        }
        return new WindowsJobObject(handle);
    }

    /// <summary>
    /// Adds a started process. Processes it has already started stay outside the job. For a venv's
    /// python.exe launcher that is a few milliseconds of startup, after which the real interpreter
    /// inherits the job.
    /// </summary>
    public bool TryAssign(Process process) =>
        _handle != IntPtr.Zero && NativeMethods.AssignProcessToJobObject(_handle, process.Handle);

    public bool Contains(Process process) =>
        _handle != IntPtr.Zero && NativeMethods.IsProcessInJob(process.Handle, _handle, out var result) && result;

    public void Dispose()
    {
        var handle = _handle;
        _handle = IntPtr.Zero;
        if (handle != IntPtr.Zero) NativeMethods.CloseHandle(handle);
    }

    private static class NativeMethods
    {
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        public const int JobObjectExtendedLimitInformation = 9;

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
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
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetInformationJobObject(IntPtr hJob, int infoClass,
            ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint infoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsProcessInJob(IntPtr hProcess, IntPtr hJob, [MarshalAs(UnmanagedType.Bool)] out bool result);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
