using System.Runtime.InteropServices;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;
using Tedd.DotNetLab.Transport.Local;

namespace Tedd.DotNetLab.Application;

public enum ExecutionHostPlatform { Windows, Linux, Other }

public static class ExecutionQualification
{
    public static void Validate(HarnessHandshake handshake,string profile,bool hasCompilationControl,ProfilerHandshake? native=null,string? targetRuntimeVersion=null)
    {
        if(!RuntimeSupport.ControllerSupported)
            throw new LabException("capability_unsupported","Execution requires a compatible Windows/Linux x64 controller runtime (.NET 10.0 or later).");
        Validate(OperatingSystem.IsWindows()?ExecutionHostPlatform.Windows:
            OperatingSystem.IsLinux()?ExecutionHostPlatform.Linux:ExecutionHostPlatform.Other,handshake,profile,hasCompilationControl,native,targetRuntimeVersion);
    }

    public static void Validate(ExecutionHostPlatform platform,HarnessHandshake handshake,string profile,bool hasCompilationControl,ProfilerHandshake? native=null,string? targetRuntimeVersion=null)
    {
        if(targetRuntimeVersion is not null)
        {
            if(!RuntimeSupport.IsSelectableManagedVersion(targetRuntimeVersion))
                throw new LabException("invalid_plan","Target runtime requires an exact .NET CoreCLR version (6.0.0 or later), including any preview/RC suffix.");
            if(targetRuntimeVersion!=handshake.Target.RuntimeVersion)
                throw new LabException("not_comparable","The target runtime differs from target_runtime_version in the frozen plan.");
        }

        string os=platform==ExecutionHostPlatform.Windows?"windows":platform==ExecutionHostPlatform.Linux?"linux":"other";
        if(!RuntimeSupport.SupportsManagedExecution(handshake.Target.RuntimeVersion,os)||
            !handshake.Target.Architecture.Equals("x64",StringComparison.OrdinalIgnoreCase)||
            platform is not (ExecutionHostPlatform.Windows or ExecutionHostPlatform.Linux))
            throw new LabException("capability_unsupported","Execution requires a supported CoreCLR runtime on Windows or Linux x64.");

        if(handshake.Target.RuntimeVersion==RuntimeSupport.Net12Development)
        {
            var footprint=handshake.Runtime;
            static bool Hash(string? value)=>value is {Length:64}&&value.All(character=>character is >= '0' and <= '9' or >= 'a' and <= 'f');
            if(platform!=ExecutionHostPlatform.Windows||profile!="quiet-validation"||hasCompilationControl||native is not null||
                handshake.Target.Epoch!=0||handshake.ProbeHelper?.Active==true||footprint is null||
                footprint.OperatingSystemFamily!="windows"||!footprint.Architecture.Equals("x64",StringComparison.OrdinalIgnoreCase)||
                footprint.NativeProfilerPresent||footprint.ProfilingEnabled is not ("" or "0")||footprint.DebuggerAttached||
                footprint.StartupHooks!=""||footprint.AdditionalDependencies!=""||footprint.RuntimeImages is not { } images||
                !Hash(images.CoreLibHash)||!Hash(images.CoreClrHash)||!Hash(images.JitHash)||
                !Guid.TryParse(images.CoreLibModuleVersionId,out var mvid)||mvid==Guid.Empty)
                throw new LabException("capability_unsupported","CoreRun development targets require quiet Windows x64 execution, epoch zero, no profiler/probes/hooks/debugger, and leased CoreLib/CoreCLR/JIT image identities.");
            return;
        }

        if(platform==ExecutionHostPlatform.Windows) return;

        var runtime=handshake.Runtime;
        bool diagnostic=profile is "baseline-context" or "cpu-diagnostic" or "allocation-diagnostic" or "runtime-metrics-context" or "jit-and-contention-diagnostic";
        if(runtime is not null&&runtime.OperatingSystemFamily=="linux"&&runtime.Architecture.Equals("x64",StringComparison.OrdinalIgnoreCase)&&
            runtime.StartupHooks==""&&runtime.AdditionalDependencies==""&&runtime.NativeProfilerPresent&&runtime.ProfilingEnabled=="1"&&
            native is { Startup:true,NativeInspection:"experimental",Rejit:"experimental",Restore:"experimental",Probes:"experimental",InlinerHandling:"experimental",Interface:"ICorProfilerInfo12" }&&
            RuntimeSupport.Supports(native.Runtime,"linux")&&native.Runtime==handshake.Target.RuntimeVersion&&
            native.Architecture=="x64"&&native.TargetId==handshake.Target.Id&&
            native.ProcessId==handshake.Target.ProcessId&&native.StartTimeUtcTicks==handshake.Target.StartTimeUtcTicks&&native.Epoch==handshake.Target.Epoch)
        {
            if(profile=="quiet-validation"&&handshake.ProbeHelper?.Active!=true) return;
            if(diagnostic&&runtime.DiagnosticsEnabled is "" or "1"&&handshake.ProbeHelper?.Active!=true) return;
            if(profile=="targeted-probes"&&!hasCompilationControl&&handshake.ProbeHelper is { Active:true } helper&&
                !string.IsNullOrEmpty(helper.ActivePlanHash)&&!string.IsNullOrEmpty(helper.MethodScope)) return;
        }
        if((profile!="quiet-validation"&&!diagnostic)||hasCompilationControl||handshake.Target.Epoch!=0||handshake.ProbeHelper?.Active==true||
            runtime is null||runtime.OperatingSystemFamily!="linux"||
            !runtime.Architecture.Equals("x64",StringComparison.OrdinalIgnoreCase)||runtime.NativeProfilerPresent||
            runtime.ProfilingEnabled is not ("" or "0")||runtime.StartupHooks!=""||runtime.AdditionalDependencies!=""||
            diagnostic&&runtime.DiagnosticsEnabled is not ("" or "1"))
            throw new LabException("capability_unsupported","Linux execution requires a clean x64 managed footprint or an identity-matched startup profiler for quiet native or installed selected-probe work. Managed diagnostic execution requires enabled diagnostics.");
    }
}
