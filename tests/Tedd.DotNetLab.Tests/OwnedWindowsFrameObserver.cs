using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Tedd.DotNetLab.Contracts;
using Microsoft.Win32.SafeHandles;

namespace Tedd.DotNetLab.Tests;

// Test-only observer. The job kills the entire owned target if the observer dies.
[SupportedOSPlatform("windows")]
internal sealed class OwnedWindowsFrameObserver : IDisposable
{
    private readonly Process target;
    private readonly TargetIdentity identity;
    private readonly SafeFileHandle job;
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    { public long ProcessUserTime,JobUserTime; public uint Flags; public nuint MinimumWorkingSet,MaximumWorkingSet; public uint ActiveProcesses; public nuint Affinity; public uint Priority,Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    { public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    { public BasicLimits Basic; public IoCounters Io; public nuint ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory; }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern SafeFileHandle CreateJobObjectW(nint attributes,nint name);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job,int kind,ref ExtendedLimits limits,uint length);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle job,nint process);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern SafeFileHandle OpenThread(uint access,[MarshalAs(UnmanagedType.Bool)] bool inherit,uint id);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern uint GetProcessIdOfThread(SafeFileHandle thread);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern uint SuspendThread(SafeFileHandle thread);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetThreadContext(SafeFileHandle thread,nint context);
    public OwnedWindowsFrameObserver(Process target,TargetIdentity identity)
    {
        ProfilerAttachmentIdentity.RequireHost(); this.target=target; this.identity=identity; Verify();
        job=CreateJobObjectW(0,0);
        if(job.IsInvalid) { job.Dispose(); throw new Win32Exception(Marshal.GetLastPInvokeError()); }
        try
        {
            var limits=new ExtendedLimits { Basic=new() { Flags=0x2000|0x8,ActiveProcesses=1 } };
            if(!SetInformationJobObject(job,9,ref limits,(uint)Marshal.SizeOf<ExtendedLimits>())||!AssignProcessToJobObject(job,target.Handle))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        catch { job.Dispose(); throw; }
    }
    private void Verify()
    {
        if(target.Id!=identity.ProcessId||target.HasExited||ProcessIdentity.StartTimeUtcTicks(target)!=identity.StartTimeUtcTicks)
            throw new LabException("target_identity_changed","Frame observer lost its owned target identity.");
    }
    public ulong InstructionPointer(uint threadId)
    {
        Verify(); using var thread=OpenThread(0x0008|0x0002|0x0800,false,threadId); // GET_CONTEXT | SUSPEND_RESUME | QUERY_LIMITED_INFORMATION
        if(thread.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        uint owner=GetProcessIdOfThread(thread);
        if(owner==0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        if(owner!=(uint)identity.ProcessId) throw new LabException("target_identity_changed","Frame thread belongs to another process.");
        nint allocation=Marshal.AllocHGlobal(1232+15),context=(nint)((allocation.ToInt64()+15)&~15L);
        bool suspended=false;
        try
        {
            uint prior=SuspendThread(thread);
            if(prior==uint.MaxValue) throw new Win32Exception(Marshal.GetLastPInvokeError());
            suspended=true;
            if(prior!=0) throw new InvalidOperationException("Owned frame thread was already suspended.");
            // SDK layout/alignment is pinned by HeaderContract. No CLR API runs while suspended.
            Marshal.WriteInt32(context,48,0x00100001); // AMD64 CONTEXT_CONTROL
            if(!GetThreadContext(thread,context)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            return unchecked((ulong)Marshal.ReadInt64(context,248));
        }
        finally
        {
            try { if(suspended&&ResumeThread(thread)==uint.MaxValue) throw new Win32Exception(Marshal.GetLastPInvokeError()); }
            finally { Marshal.FreeHGlobal(allocation); }
        }
    }
    public void Dispose()=>job.Dispose();
}
