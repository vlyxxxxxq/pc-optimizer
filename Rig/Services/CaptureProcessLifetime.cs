using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Rig.Services;

/// <summary>Windows closes this job if Rig crashes, killing its capture children rather than leaving a hidden recorder.</summary>
internal static class CaptureProcessLifetime
{
    [StructLayout(LayoutKind.Sequential)]
    struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public UIntPtr Affinity;
        public uint Priority, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    sealed class JobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public JobHandle(IntPtr handle) : base(true) => SetHandle(handle);
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(JobHandle job, int infoClass, ref ExtendedLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AssignProcessToJobObject(JobHandle job, IntPtr process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    static readonly Lazy<JobHandle> Job = new(() =>
    {
        var job = new JobHandle(CreateJobObject(IntPtr.Zero, null));
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        { var error = Marshal.GetLastWin32Error(); job.Dispose(); throw new Win32Exception(error); }
        return job;
    });
    public static void Attach(Process process)
    {
        if (!AssignProcessToJobObject(Job.Value, process.Handle))
        {
            int error = Marshal.GetLastWin32Error();
            try { process.Kill(true); } catch { }
            throw new Win32Exception(error, "Windows couldn't attach the capture process to Rig's lifetime. Capture was not started.");
        }
    }
}
