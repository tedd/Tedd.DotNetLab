using System.Globalization;
using System.Runtime.InteropServices;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Application;

internal sealed class LinuxParserGroup : IDisposable
{
    public const string Accounting="cgroup_v2_charged_memory; includes_anon_file_kernel; not_windows_private_commit; kernel_transient_overshoot_possible";
    public const int MaximumTasks=256;
    public string Path { get; }
    private readonly string parent;
    private readonly long maximum;
    private bool disposed;
    [StructLayout(LayoutKind.Sequential,Size=120)] private struct FileSystemStatus { public long Type; }
    [DllImport("libc",EntryPoint="statfs",SetLastError=true)]
    private static extern int StatFs([MarshalAs(UnmanagedType.LPUTF8Str)] string path,out FileSystemStatus status);
    public LinuxParserGroup(long maximumBytes)
    {
        maximum=maximumBytes;
        string configured=Environment.GetEnvironmentVariable("DOTNETLAB_PARSER_CGROUP_ROOT")??"";
        if(!System.IO.Path.IsPathFullyQualified(configured)) throw Unsupported();
        parent=System.IO.Path.GetFullPath(configured).TrimEnd('/');
        if(!parent.StartsWith("/sys/fs/cgroup/",StringComparison.Ordinal)||parent=="/sys/fs/cgroup"||
            !Directory.Exists(parent)||StatFs(parent,out var filesystem)!=0||filesystem.Type!=0x63677270||!File.Exists(parent+"/cgroup.controllers")||
            !File.ReadAllText(parent+"/cgroup.controllers").Split(' ',StringSplitOptions.RemoveEmptyEntries).Select(x=>x.Trim()).Contains("memory"))
            throw Unsupported();
        for(var directory=new DirectoryInfo(parent);directory is not null;directory=directory.Parent)
            if((directory.Attributes&FileAttributes.ReparsePoint)!=0) throw Unsupported();
        // Delegation is provisioned externally; the application never elevates or
        // changes limits outside its fresh, empty worker leaf.
        Path=parent+"/dotnetlab-worker-"+Guid.NewGuid().ToString("N");
        if(Directory.Exists(Path)) throw Unsupported();
        Directory.CreateDirectory(Path);
        try
        {
            if(File.ReadAllText(Path+"/cgroup.type").Trim()!="domain"||!File.Exists(Path+"/memory.peak")||!File.Exists(Path+"/cgroup.kill")) throw Unsupported();
            Write("memory.max",maximum.ToString(CultureInfo.InvariantCulture)); Write("memory.swap.max","0");
            Write("memory.oom.group","1"); Write("pids.max",MaximumTasks.ToString(CultureInfo.InvariantCulture));
            if(ReadLong("memory.max")!=maximum||ReadLong("memory.swap.max")!=0||ReadLong("memory.oom.group")!=1||ReadLong("pids.max")!=MaximumTasks)
                throw Unsupported();
        }
        catch { Directory.Delete(Path); throw; }
    }
    private static LabException Unsupported()=>new("capability_unsupported","Linux parser containment requires delegated cgroup v2 memory/task controls.",
        "Configure a writable delegated cgroup root for the controller before isolated parsing.");
    private void Write(string file,string value)=>File.WriteAllText(Path+"/"+file,value);
    private long ReadLong(string file)
    {
        string value=File.ReadAllText(Path+"/"+file).Trim();
        if(value.Length>32||!long.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out long parsed)||parsed<0)
            throw new LabException("parser_shutdown_unverified","Kernel parser accounting is invalid.");
        return parsed;
    }
    public long PeakBytes()=>ReadLong("memory.peak");
    public long Event(string key)
    {
        string text=File.ReadAllText(Path+"/memory.events");
        if(text.Length>4096) throw new LabException("parser_shutdown_unverified","Kernel parser events exceed their read bound.");
        foreach(string row in text.Split('\n',StringSplitOptions.RemoveEmptyEntries))
        {
            var fields=row.Split(' ',StringSplitOptions.RemoveEmptyEntries);
            if(fields.Length==2&&fields[0]==key&&long.TryParse(fields[1],NumberStyles.None,CultureInfo.InvariantCulture,out long value)&&value>=0) return value;
        }
        throw new LabException("parser_shutdown_unverified","Kernel parser limit event is missing.");
    }
    public async Task VerifyProcessAsync(int pid,CancellationToken cancellation)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while(!File.ReadAllText(Path+"/cgroup.procs").Split('\n',StringSplitOptions.RemoveEmptyEntries).Contains(pid.ToString(CultureInfo.InvariantCulture)))
            await Task.Delay(10,deadline.Token);
    }
    public async Task StopAsync()
    {
        Write("cgroup.kill","1");
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while(true)
        {
            var events=File.ReadAllText(Path+"/cgroup.events");
            if(events.Split('\n').Contains("populated 0")) return;
            try { await Task.Delay(10,deadline.Token); }
            catch(OperationCanceledException) { throw new LabException("parser_shutdown_unverified","Parser resource group did not become empty."); }
        }
    }
    public void Dispose()
    {
        if(disposed) return;
        if(!Path.StartsWith(parent+"/dotnetlab-worker-",StringComparison.Ordinal)||File.ReadAllText(Path+"/cgroup.procs").Trim().Length!=0)
            throw new LabException("parser_shutdown_unverified","Parser group cannot be removed while occupied.");
        Directory.Delete(Path); disposed=true;
    }
}
