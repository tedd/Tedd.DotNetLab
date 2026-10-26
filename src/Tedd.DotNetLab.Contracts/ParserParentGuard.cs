using System.Runtime.InteropServices;

namespace Tedd.DotNetLab.Contracts;

public sealed class ParserParentGuard : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct PollDescriptor { public int Descriptor; public short Events,Returned; }
    [DllImport("libc",EntryPoint="syscall",SetLastError=true)] private static extern int PidFdOpen(long number,int pid,uint flags);
    [DllImport("libc",EntryPoint="poll",SetLastError=true)] private static extern int Poll(ref PollDescriptor descriptor,nuint count,int timeout);
    [DllImport("libc",EntryPoint="close")] private static extern int Close(int descriptor);
    private readonly int descriptor;
    private readonly Thread thread;
    private volatile bool stopping;
    private int disposed;
    private ParserParentGuard(int descriptor,string group)
    {
        this.descriptor=descriptor;
        thread=new Thread(()=>
        {
            var poll=new PollDescriptor { Descriptor=descriptor,Events=1 };
            while(!stopping)
            {
                int ready=Poll(ref poll,1,100);
                if(ready<0&&Marshal.GetLastPInvokeError()==4) continue;
                if(ready==0) continue;
                if(stopping) return;
                // Controller death kills the complete owned worker cgroup,
                // including any descendants, rather than only this thread.
                try { File.WriteAllText(group+"/cgroup.kill","1"); }
                finally { Environment.Exit(125); }
            }
        }) { IsBackground=true,Name="Tedd.DotNetLab parser parent guard" };
        thread.Start();
    }
    public static ParserParentGuard? Start()
    {
        if(!OperatingSystem.IsLinux()) return null;
        if(RuntimeInformation.ProcessArchitecture!=Architecture.X64||
            !int.TryParse(Environment.GetEnvironmentVariable("DOTNETLAB_PARSER_PARENT_PID"),out int pid)||pid<=0||
            !long.TryParse(Environment.GetEnvironmentVariable("DOTNETLAB_PARSER_PARENT_START"),out long start)||start<=0)
            throw new LabException("capability_unsupported","Linux parser requires verified controller lifetime metadata.");
        string group=Environment.GetEnvironmentVariable("DOTNETLAB_PARSER_OWNED_GROUP")??"";
        if(!group.StartsWith("/sys/fs/cgroup/",StringComparison.Ordinal)||!Path.GetFileName(group).StartsWith("dotnetlab-worker-",StringComparison.Ordinal)||
            !File.Exists(group+"/cgroup.kill")||!File.ReadAllText(group+"/cgroup.procs").Split('\n').Contains(Environment.ProcessId.ToString()))
            throw new LabException("capability_unsupported","Linux parser lifetime guard requires its owned resource group.");
        int descriptor=PidFdOpen(434,pid,0); // Linux x64 pidfd_open
        if(descriptor<0) throw new LabException("capability_unsupported","Linux pidfd controller lifetime tracking is unavailable.");
        try
        {
            using var controller=System.Diagnostics.Process.GetProcessById(pid);
            if(ProcessIdentity.StartTimeUtcTicks(controller)!=start) throw new LabException("target_identity_changed","Parser controller identity changed.");
            return new(descriptor,group);
        }
        catch { Close(descriptor); throw; }
    }
    public void Dispose() { if(Interlocked.Exchange(ref disposed,1)!=0) return; stopping=true; thread.Join(); Close(descriptor); }
}
