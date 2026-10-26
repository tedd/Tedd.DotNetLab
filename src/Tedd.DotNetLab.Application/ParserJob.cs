using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Tedd.DotNetLab.Application;

[SupportedOSPlatform("windows")]
internal sealed class ParserJob : IDisposable
{
    private readonly SafeFileHandle handle;
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessUserTime,JobUserTime; public uint Flags; public nuint MinimumWorkingSet,MaximumWorkingSet;
        public uint ActiveProcesses; public nuint Affinity; public uint Priority,Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    {
        public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern SafeFileHandle CreateJobObject(nint attributes,string? name);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job,int kind,ref ExtendedLimits data,uint length);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(SafeFileHandle job,int kind,out ExtendedLimits data,uint length,nint returnedLength);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle job,nint process);
    public ParserJob(Process process,long maximumBytes)
    {
        handle=CreateJobObject(0,null);
        if(handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastPInvokeError()); }
        try
        {
            var limits=new ExtendedLimits { Basic=new() { Flags=0x2000|0x100|0x200|0x8|0x400,ActiveProcesses=1 },ProcessMemory=(nuint)maximumBytes,JobMemory=(nuint)maximumBytes };
            if(!SetInformationJobObject(handle,9,ref limits,(uint)Marshal.SizeOf<ExtendedLimits>())||!AssignProcessToJobObject(handle,process.Handle))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        catch { handle.Dispose(); throw; }
    }
    public long PeakCommittedBytes()
    {
        if(!QueryInformationJobObject(handle,9,out var limits,(uint)Marshal.SizeOf<ExtendedLimits>(),0)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return checked((long)limits.PeakProcessMemory);
    }
    public void Dispose()=>handle.Dispose();
}
