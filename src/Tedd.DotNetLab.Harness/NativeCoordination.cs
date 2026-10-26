using System.Runtime.InteropServices;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Harness;

// Resolve only the profiler already loaded into this process. Never load a profiler as a measurement side effect.
internal static class NativeCoordination
{
    [DllImport("kernel32",CharSet=CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string name);
    [DllImport("libdl.so.2",EntryPoint="dlopen")]
    private static extern nint OpenLoaded([MarshalAs(UnmanagedType.LPUTF8Str)] string path,int flags);
    private static readonly Lazy<nint> LinuxModule=new(()=>
    {
        string? path=Environment.GetEnvironmentVariable("CORECLR_PROFILER_PATH_64");
        if(string.IsNullOrEmpty(path)) path=Environment.GetEnvironmentVariable("CORECLR_PROFILER_PATH");
        return OperatingSystem.IsLinux()&&!string.IsNullOrEmpty(path)&&Path.GetFileName(path)=="Tedd.DotNetLab.Profiler.so"?OpenLoaded(path,2|4):0;
    });
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Enter();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void Leave();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate long GetEpoch();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void Report(long epoch,int correct,[MarshalAs(UnmanagedType.LPUTF8Str)] string scope);
    private sealed class Lease(Leave? release) : IDisposable
    {
        private Leave? exit=release;
        public void Dispose()=>Interlocked.Exchange(ref exit,null)?.Invoke();
    }
    private static nint Module=>OperatingSystem.IsWindows()?GetModuleHandleW("Tedd.DotNetLab.Profiler.dll"):
        OperatingSystem.IsLinux()?LinuxModule.Value:0;
    public static bool Present=>Module!=0;
    public static long Epoch(long fallback)
    {
        var module=Module;
        return module!=0&&NativeLibrary.TryGetExport(module,"DotNetLabGetEpoch",out var address)
            ?Marshal.GetDelegateForFunctionPointer<GetEpoch>(address)():fallback;
    }
    public static IDisposable Acquire(bool setup=false)
    {
        var module=Module;
        if(module==0) return new Lease(null);
        if(!NativeLibrary.TryGetExport(module,setup?"DotNetLabEnterSetup":"DotNetLabEnterExecution",out var enter)||
            !NativeLibrary.TryGetExport(module,"DotNetLabLeaveExecution",out var leave))
            throw new LabException("capability_unsupported","Loaded profiler lacks execution coordination.");
        if(Marshal.GetDelegateForFunctionPointer<Enter>(enter)()==0)
            throw new LabException("lease_conflict","Native inspection or mutation is active.");
        return new Lease(Marshal.GetDelegateForFunctionPointer<Leave>(leave));
    }
    public static void ReportSetup(long epoch,bool correct,string? scope)
    {
        var module=Module;
        if(module!=0&&NativeLibrary.TryGetExport(module,"DotNetLabReportSetup",out var report))
            Marshal.GetDelegateForFunctionPointer<Report>(report)(epoch,correct?1:0,scope??"");
    }
}
