using System.Runtime.InteropServices;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Harness;

internal sealed class BatchTelemetrySession : IDisposable
{
    private const nint WindowsCurrentThreadPseudoHandle = -2;
    private const int LinuxClockThreadCpuTimeId = 3;
    private const long LinuxSysPerfEventOpenX64 = 298;
    private const uint PerfTypeHardware = 0;
    private const uint PerfAttrSizeVer0 = 64;
    private const ulong PerfFormatGroup = 1UL << 3;
    private const ulong PerfFlagsDisabledExcludeKernelHv = 1UL | (1UL << 5) | (1UL << 6);
    private const ulong PerfCountHwCpuCycles = 0;
    private const ulong PerfCountHwInstructions = 1;
    private const ulong PerfCountHwCacheMisses = 3;
    private const ulong PerfCountHwBranchMisses = 5;
    private const ulong PerfEventIocEnable = 0x2400;
    private const ulong PerfEventIocDisable = 0x2401;
    private const ulong PerfEventIocReset = 0x2403;
    private const ulong PerfIocFlagGroup = 1;
    private const ulong PerfFlagFdCloexec = 1UL << 3;

    private static volatile bool linuxPmuUnavailable;

    private readonly bool captureThreadCounters;
    private readonly int leaderFd = -1;
    private readonly int instructionsFd = -1;
    private readonly int cacheMissesFd = -1;
    private readonly int branchMissesFd = -1;

    private int gen0Before;
    private int gen1Before;
    private int gen2Before;
    private ulong windowsStartCycles;
    private ulong windowsEndCycles;
    private bool windowsStartValid;
    private bool windowsEndValid;
    private long linuxStartThreadNs = -1;
    private long linuxEndThreadNs = -1;
    private bool pmuMeasured;

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long Sec;
        public long Nsec;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerfEventAttrVer0
    {
        public uint Type;
        public uint Size;
        public ulong Config;
        public ulong SamplePeriod;
        public ulong SampleType;
        public ulong ReadFormat;
        public ulong Flags;
        public uint WakeupEvents;
        public uint BpType;
        public ulong Config1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PmuGroupValues
    {
        public ulong Count;
        public ulong CpuCycles;
        public ulong Instructions;
        public ulong CacheMisses;
        public ulong BranchMisses;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool QueryThreadCycleTime(nint threadHandle, out ulong cycleTime);

    [DllImport("libc", EntryPoint = "clock_gettime", SetLastError = true)]
    private static extern int ClockGetTime(int clockId, out Timespec tp);

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern nint LinuxSyscallPerfOpen(long number, ref PerfEventAttrVer0 attr, int pid, int cpu, int groupFd, nuint flags);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int LinuxIoctl(int fd, nuint request, nuint arg);

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint LinuxRead(int fd, out PmuGroupValues values, nuint count);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int LinuxClose(int fd);

    public BatchTelemetrySession(bool captureThreadCounters)
    {
        this.captureThreadCounters = captureThreadCounters;
        if (!captureThreadCounters || !OperatingSystem.IsLinux() ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64 || linuxPmuUnavailable)
        {
            return;
        }

        try
        {
            leaderFd = OpenHardwareCounter(PerfCountHwCpuCycles, -1);
            if (leaderFd < 0)
            {
                linuxPmuUnavailable = true;
                return;
            }
            instructionsFd = OpenHardwareCounter(PerfCountHwInstructions, leaderFd);
            cacheMissesFd = OpenHardwareCounter(PerfCountHwCacheMisses, leaderFd);
            branchMissesFd = OpenHardwareCounter(PerfCountHwBranchMisses, leaderFd);
            if (instructionsFd < 0 || cacheMissesFd < 0 || branchMissesFd < 0)
            {
                ClosePmuFds(ref leaderFd, ref instructionsFd, ref cacheMissesFd, ref branchMissesFd);
                linuxPmuUnavailable = true;
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            linuxPmuUnavailable = true;
        }
    }

    private static int OpenHardwareCounter(ulong config, int groupFd)
    {
        var attr = new PerfEventAttrVer0
        {
            Type = PerfTypeHardware,
            Size = PerfAttrSizeVer0,
            Config = config,
            ReadFormat = PerfFormatGroup,
            Flags = groupFd < 0 ? PerfFlagsDisabledExcludeKernelHv : (1UL << 5) | (1UL << 6)
        };
        nint fd = LinuxSyscallPerfOpen(LinuxSysPerfEventOpenX64, ref attr, 0, -1, groupFd, (nuint)PerfFlagFdCloexec);
        return fd >= 0 && fd <= int.MaxValue ? (int)fd : -1;
    }

    public void CapturePreBatchGcSnapshot()
    {
        gen0Before = GC.CollectionCount(0);
        gen1Before = GC.CollectionCount(1);
        gen2Before = GC.CollectionCount(2);
    }

    public void BeginThreadWindow()
    {
        if (!captureThreadCounters) return;
        if (OperatingSystem.IsWindows())
        {
            windowsStartValid = QueryThreadCycleTime(WindowsCurrentThreadPseudoHandle, out windowsStartCycles);
        }
        else if (OperatingSystem.IsLinux())
        {
            if (ClockGetTime(LinuxClockThreadCpuTimeId, out var ts) == 0)
                linuxStartThreadNs = ts.Sec * 1_000_000_000L + ts.Nsec;
            if (leaderFd >= 0)
            {
                _ = LinuxIoctl(leaderFd, (nuint)PerfEventIocReset, (nuint)PerfIocFlagGroup);
                pmuMeasured = LinuxIoctl(leaderFd, (nuint)PerfEventIocEnable, (nuint)PerfIocFlagGroup) == 0;
            }
        }
    }

    public void EndThreadWindow()
    {
        if (!captureThreadCounters) return;
        if (OperatingSystem.IsWindows())
        {
            windowsEndValid = QueryThreadCycleTime(WindowsCurrentThreadPseudoHandle, out windowsEndCycles);
        }
        else if (OperatingSystem.IsLinux())
        {
            if (leaderFd >= 0 && pmuMeasured)
                _ = LinuxIoctl(leaderFd, (nuint)PerfEventIocDisable, (nuint)PerfIocFlagGroup);
            if (ClockGetTime(LinuxClockThreadCpuTimeId, out var ts) == 0)
                linuxEndThreadNs = ts.Sec * 1_000_000_000L + ts.Nsec;
        }
    }

    public BatchTelemetry BuildSnapshot(bool sameThread)
    {
        int gen0 = Math.Max(0, GC.CollectionCount(0) - gen0Before);
        int gen1 = Math.Max(0, GC.CollectionCount(1) - gen1Before);
        int gen2 = Math.Max(0, GC.CollectionCount(2) - gen2Before);
        var info = GC.GetGCMemoryInfo();
        long heapSize = Math.Max(0L, info.HeapSizeBytes);
        long fragmented = Math.Max(0L, info.FragmentedBytes);
        long committed = Math.Max(0L, info.TotalCommittedBytes);
        long pinned = Math.Max(0L, info.PinnedObjectsCount);

        long? threadCycles = null;
        long? threadNs = null;
        long? instructions = null;
        long? hwCycles = null;
        long? branchMisses = null;
        long? cacheMisses = null;

        if (captureThreadCounters && sameThread)
        {
            if (OperatingSystem.IsWindows() && windowsStartValid && windowsEndValid && windowsEndCycles >= windowsStartCycles)
            {
                ulong delta = windowsEndCycles - windowsStartCycles;
                threadCycles = (long)Math.Min(delta, (ulong)long.MaxValue);
            }
            else if (OperatingSystem.IsLinux())
            {
                if (linuxStartThreadNs >= 0 && linuxEndThreadNs >= linuxStartThreadNs)
                    threadNs = linuxEndThreadNs - linuxStartThreadNs;

                if (leaderFd >= 0 && pmuMeasured && LinuxRead(leaderFd, out var group, 40) == 40 && group.Count == 4)
                {
                    hwCycles = ClampU64(group.CpuCycles);
                    threadCycles = hwCycles;
                    instructions = ClampU64(group.Instructions);
                    cacheMisses = ClampU64(group.CacheMisses);
                    branchMisses = ClampU64(group.BranchMisses);
                }
            }
        }

        return new BatchTelemetry(
            gen0, gen1, gen2,
            heapSize, fragmented, committed, pinned,
            ThreadCpuCycles: threadCycles,
            ThreadCpuNanoseconds: threadNs,
            InstructionsRetired: instructions,
            HardwareCpuCycles: hwCycles,
            BranchMisses: branchMisses,
            CacheMisses: cacheMisses);
    }

    private static long ClampU64(ulong value) => (long)Math.Min(value, (ulong)long.MaxValue);

    private static void ClosePmuFds(ref int leader, ref int inst, ref int cache, ref int branch)
    {
        if (branch >= 0) { _ = LinuxClose(branch); branch = -1; }
        if (cache >= 0) { _ = LinuxClose(cache); cache = -1; }
        if (inst >= 0) { _ = LinuxClose(inst); inst = -1; }
        if (leader >= 0) { _ = LinuxClose(leader); leader = -1; }
    }

    public void Dispose()
    {
        int l = leaderFd, i = instructionsFd, c = cacheMissesFd, b = branchMissesFd;
        ClosePmuFds(ref l, ref i, ref c, ref b);
    }
}
