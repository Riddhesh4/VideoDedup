using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VideoDedup.Application;

namespace VideoDedup.Infrastructure;

internal sealed class WindowsProcessLimits : IDisposable
{
    private readonly SafeFileHandle _job;
    public WindowsProcessLimits(long memoryBytes, TimeSpan cpuTime)
    {
        if (!OperatingSystem.IsWindows()) throw new MediaProcessingException(MediaFailure.Unsupported);
        _job = CreateJobObject(IntPtr.Zero, null);
        if (_job.IsInvalid) { _job.Dispose(); throw new MediaProcessingException(MediaFailure.ToolUnavailable); }
        var limits = new ExtendedLimits
        {
            Basic = new BasicLimits { Flags = 0x2 | 0x100 | 0x2000, ProcessTime = cpuTime.Ticks },
            ProcessMemory = (UIntPtr)(ulong)memoryBytes,
        };
        if (!SetInformationJobObject(_job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        {
            _job.Dispose(); throw new MediaProcessingException(MediaFailure.ToolUnavailable);
        }
    }
    public void Attach(Process process)
    {
        // The process may already have exited before assignment (e.g. an invalid option).
        if (!AssignProcessToJobObject(_job, process.Handle) && !process.HasExited)
            throw new MediaProcessingException(MediaFailure.ToolUnavailable);
    }
    public void Dispose() => _job.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public UIntPtr Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimits information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
