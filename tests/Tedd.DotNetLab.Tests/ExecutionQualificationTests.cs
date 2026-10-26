using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;
using System.Text.Json;
using Xunit;
using Tedd.DotNetLab.Transport.Local;

namespace Tedd.DotNetLab.Tests;

public sealed class ExecutionQualificationTests
{
    [Fact,Case("UT-LINUX-LEGACY-FOOTPRINT","FR-021,FR-026,FR-074","M7","Contract")]
    public void HistoricalFootprintRemainsHashStableWithoutAuthorizingNewLinuxMeasurement()
    {
        const string legacy="""
            {"entry_assembly_hash":"legacy","assembly_hashes":{},"operating_system":"Ubuntu fixture","architecture":"X64","stopwatch_frequency":"1000000000","debugger_attached":false,"native_profiler_present":false,"diagnostics_enabled":"","profiling_enabled":"","startup_hooks":"","additional_dependencies":"","owned_process":false}
            """;
        var runtime=Wire.Deserialize<RuntimeFootprint>(legacy); Assert.Null(runtime.OperatingSystemFamily);
        using var document=JsonDocument.Parse(legacy);
        Assert.Equal(Wire.Hash(document.RootElement),Wire.Hash(runtime));
        Assert.DoesNotContain("operating_system_family",Wire.Serialize(runtime));
        var handshake=Linux() with { Runtime=runtime };
        ExecutionQualification.Validate(ExecutionHostPlatform.Windows,handshake,"quiet-validation",false);
        Assert.Equal("capability_unsupported",Assert.Throws<LabException>(()=>
            ExecutionQualification.Validate(ExecutionHostPlatform.Linux,handshake,"quiet-validation",false)).Code);
    }
    private static HarnessHandshake Linux()=>new(new("target",1,2,"runtime","10.0.12","X64"),[],false,
        Runtime:new("build",new Dictionary<string,string>(),"Ubuntu fixture","X64",1000000000,false,false,"","","","",OperatingSystemFamily:"linux"));
    private static (HarnessHandshake,ProfilerHandshake) NativeProbe()
    {
        var baseline=Linux(); var target=baseline.Target with { Epoch=1 };
        var harness=baseline with { Target=target,Runtime=baseline.Runtime! with { NativeProfilerPresent=true,ProfilingEnabled="1" },
            ProbeHelper=new("1","hash","mvid",true,"plan","scope") };
        var native=new ProfilerHandshake(Wire.Version,target.Id,target.ProcessId,target.StartTimeUtcTicks,"10.0.12","CoreCLR","x64",1,
            true,"fixture","ICorProfilerInfo12","experimental","experimental","experimental","experimental",InlinerHandling:"experimental");
        return(harness,native);
    }
    [Fact,Case("UT-LINUX-NATIVE-PROBE-ADMISSION","FR-001,FR-032,FR-037,FR-050,FR-070,FR-074","M7","Unit")]
    public void IdentityMatchedStartupAndInstalledProbePlanPermitOnlySelectedProbeExecution()
    {
        var (harness,native)=NativeProbe();
        ExecutionQualification.Validate(ExecutionHostPlatform.Linux,harness,"targeted-probes",false,native);
        Assert.Equal("capability_unsupported",Assert.Throws<LabException>(()=>
            ExecutionQualification.Validate(ExecutionHostPlatform.Linux,harness,"quiet-validation",false,native)).Code);
    }
    [Theory,InlineData(false),InlineData(true),Case("UT-LINUX-NATIVE-CONTROL-ADMISSION","FR-001,FR-032,FR-052,FR-055,FR-070,FR-074","M7","Unit")]
    public void VerifiedNativeQuietAdmissionRetainsDownstreamCompilationControlGate(bool hasControl)
    {
        var (harness,native)=NativeProbe(); harness=harness with { ProbeHelper=harness.ProbeHelper! with { Active=false } };
        ExecutionQualification.Validate(ExecutionHostPlatform.Linux,harness,"quiet-validation",hasControl,native);
    }
    [Theory,InlineData("missing_native"),InlineData("identity"),InlineData("epoch"),InlineData("runtime"),InlineData("active_probe"),
        InlineData("hooks"),InlineData("other_profile"),Case("UT-LINUX-NATIVE-CONTROL-ADMISSION","FR-001,FR-032,FR-052,FR-055,FR-070,FR-074","M7","Unit")]
    public void UnverifiedNativeQuietFootprintCannotAuthorizeCompilationWork(string defect)
    {
        var (harness,native)=NativeProbe(); harness=harness with { ProbeHelper=harness.ProbeHelper! with { Active=defect=="active_probe" } };
        native=defect switch { "identity"=>native with { ProcessId=2 },"epoch"=>native with { Epoch=0 },"runtime"=>native with { Runtime="9.0.11" },_=>native };
        if(defect=="hooks") harness=harness with { Runtime=harness.Runtime! with { StartupHooks="foreign.dll" } };
        Assert.Equal("capability_unsupported",Assert.Throws<LabException>(()=>ExecutionQualification.Validate(ExecutionHostPlatform.Linux,
            harness,defect=="other_profile"?"cpu-sampling":"quiet-validation",true,defect=="missing_native"?null:native)).Code);
    }
    [Theory,InlineData("missing_native"),InlineData("pid"),InlineData("start"),InlineData("target"),InlineData("epoch"),InlineData("runtime"),
        InlineData("interface"),InlineData("startup"),InlineData("probe_capability"),InlineData("caller_capability"),InlineData("inactive"),
        InlineData("missing_plan"),InlineData("missing_scope"),InlineData("missing_footprint"),InlineData("hooks"),InlineData("dependencies"),
        InlineData("profiling"),InlineData("control"),InlineData("other_profile"),Case("UT-LINUX-NATIVE-PROBE-ADMISSION","FR-001,FR-032,FR-037,FR-050,FR-070,FR-074","M7","Unit")]
    public void NativeOrHelperLabelsCannotBypassExactProbeAdmission(string defect)
    {
        var (harness,native)=NativeProbe();
        native=defect switch
        {
            "pid"=>native with { ProcessId=2 },"start"=>native with { StartTimeUtcTicks=3 },"target"=>native with { TargetId="foreign" },
            "epoch"=>native with { Epoch=2 },"runtime"=>native with { Runtime="9.0.11" },"interface"=>native with { Interface="unknown" },
            "startup"=>native with { Startup=false },"probe_capability"=>native with { Probes="unsupported" },
            "caller_capability"=>native with { InlinerHandling="unsupported" },_=>native
        };
        harness=defect switch
        {
            "inactive"=>harness with { ProbeHelper=harness.ProbeHelper! with { Active=false } },
            "missing_plan"=>harness with { ProbeHelper=harness.ProbeHelper! with { ActivePlanHash=null } },
            "missing_scope"=>harness with { ProbeHelper=harness.ProbeHelper! with { MethodScope=null } },
            "missing_footprint"=>harness with { Runtime=null },"hooks"=>harness with { Runtime=harness.Runtime! with { StartupHooks="foreign.dll" } },
            "dependencies"=>harness with { Runtime=harness.Runtime! with { AdditionalDependencies="foreign.deps.json" } },
            "profiling"=>harness with { Runtime=harness.Runtime! with { ProfilingEnabled="0" } },_=>harness
        };
        Assert.Equal("capability_unsupported",Assert.Throws<LabException>(()=>ExecutionQualification.Validate(ExecutionHostPlatform.Linux,
            harness,defect=="other_profile"?"cpu-sampling":"targeted-probes",defect=="control",defect=="missing_native"?null:native)).Code);
    }

    [Fact,Case("UT-ID-012","FR-001,FR-037,FR-074","M7","Unit")]
    public void SeparatePlatformAdmissionAllowsOnlyTheQualifiedExecutionScope()
    {
        ExecutionQualification.Validate(ExecutionHostPlatform.Linux,Linux(),"quiet-validation",false);
        ExecutionQualification.Validate(ExecutionHostPlatform.Windows,Linux() with { Runtime=null },"cpu-diagnostic",true);
        Assert.Equal("capability_unsupported",Assert.Throws<LabException>(()=>
            ExecutionQualification.Validate(ExecutionHostPlatform.Other,Linux(),"quiet-validation",false)).Code);
    }
    [Theory,InlineData("baseline-context"),InlineData("cpu-diagnostic"),InlineData("allocation-diagnostic"),InlineData("runtime-metrics-context"),
        Case("UT-LINUX-DIAGNOSTIC-ADMISSION","FR-001,FR-010,FR-037,FR-070","M7","Unit")]
    public void ManagedDiagnosticsRequireEnabledTransportAndPreserveFootprintRestrictions(string profile)
    {
        foreach(string enabled in new[]{"","1"})
            ExecutionQualification.Validate(ExecutionHostPlatform.Linux,Linux() with { Runtime=Linux().Runtime! with { DiagnosticsEnabled=enabled } },profile,false);
        foreach(string disabled in new[]{"0","unknown"})
            Assert.Equal("capability_unsupported",Assert.Throws<LabException>(()=>ExecutionQualification.Validate(ExecutionHostPlatform.Linux,
                Linux() with { Runtime=Linux().Runtime! with { DiagnosticsEnabled=disabled } },profile,false)).Code);
        Assert.Equal("capability_unsupported",Assert.Throws<LabException>(()=>ExecutionQualification.Validate(ExecutionHostPlatform.Linux,Linux(),profile,true)).Code);
        var (nativeHarness,native)=NativeProbe();
        nativeHarness=nativeHarness with { ProbeHelper=nativeHarness.ProbeHelper! with { Active=false } };
        foreach(bool control in new[]{false,true}) ExecutionQualification.Validate(ExecutionHostPlatform.Linux,nativeHarness,profile,control,native);
        Assert.Equal("capability_unsupported",Assert.Throws<LabException>(()=>ExecutionQualification.Validate(ExecutionHostPlatform.Linux,nativeHarness,profile,false)).Code);
        Assert.Equal("capability_unsupported",Assert.Throws<LabException>(()=>ExecutionQualification.Validate(ExecutionHostPlatform.Linux,
            nativeHarness with { ProbeHelper=nativeHarness.ProbeHelper! with { Active=true } },profile,false,native)).Code);
        Assert.Equal("capability_unsupported",Assert.Throws<LabException>(()=>ExecutionQualification.Validate(ExecutionHostPlatform.Linux,
            nativeHarness with { Runtime=nativeHarness.Runtime! with { DiagnosticsEnabled="0" } },profile,false,native)).Code);
    }
    [Theory,InlineData("profile"),InlineData("control"),InlineData("epoch"),InlineData("runtime"),InlineData("architecture"),
        InlineData("missing_footprint"),InlineData("footprint_os"),InlineData("footprint_arch"),InlineData("native"),InlineData("profiling"),
        InlineData("hooks"),InlineData("dependencies"),InlineData("unknown_os"),Case("UT-LINUX-EXECUTION-SCOPE","FR-001,FR-032,FR-037,FR-070,FR-074","M7","Unit")]
    public void UnqualifiedLinuxEnvironmentCannotEnterMeasurement(string defect)
    {
        var handshake=Linux(); var target=handshake.Target; var runtime=handshake.Runtime!;
        target=defect switch { "epoch"=>target with { Epoch=1 },"runtime"=>target with { RuntimeVersion="5.0.17" },
            "architecture"=>target with { Architecture="Arm64" },_=>target };
        runtime=defect switch { "footprint_os"=>runtime with { OperatingSystemFamily="windows" },
            "footprint_arch"=>runtime with { Architecture="X86" },"native"=>runtime with { NativeProfilerPresent=true },
            "profiling"=>runtime with { ProfilingEnabled="1" },"hooks"=>runtime with { StartupHooks="external.dll" },
            "dependencies"=>runtime with { AdditionalDependencies="external.deps.json" },_=>runtime };
        if(defect=="unknown_os") runtime=runtime with { OperatingSystemFamily=null };
        Assert.Equal("capability_unsupported",Assert.Throws<LabException>(()=>ExecutionQualification.Validate(ExecutionHostPlatform.Linux,
            handshake with { Target=target,Runtime=defect=="missing_footprint"?null:runtime },defect=="profile"?"targeted-probes":"quiet-validation",defect=="control")).Code);
    }
}
