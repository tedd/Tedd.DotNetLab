using System.Diagnostics;
using System.Security.Cryptography;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;
using Tedd.DotNetLab.Mcp;
using System.Text.Json;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class Net12ManagedExecutionTests
{
    private static HarnessHandshake Qualified()=>new(new("target",1,2,"runtime",RuntimeSupport.Net12Development,"X64"),[],false,
        Runtime:new("build",new Dictionary<string,string>(),"Windows fixture","X64",10000000,false,false,"","","","",
            OperatingSystemFamily:"windows",RuntimeImages:new(new('a',64),new('b',64),new('c',64),"00000000-0000-0000-0000-000000000001")));

    [Fact,Case("UT-NET12-MANAGED","FR-001,FR-037,FR-074","M7","Unit")]
    public void ManagedQualificationDoesNotExpandProfilerOrNormalBuildTargetSupportForRawCoreRun()
    {
        Assert.True(RuntimeSupport.SupportsManagedExecution(RuntimeSupport.Net12Development,"windows"));
        Assert.False(RuntimeSupport.SupportsManagedExecution(RuntimeSupport.Net12Development,"linux"));
        Assert.False(RuntimeSupport.SupportsManagedExecution("5.0.17","windows"));
        Assert.False(RuntimeSupport.Supports(RuntimeSupport.Net12Development,"windows"));
        Assert.Equal("capability_unsupported",Assert.Throws<LabException>(()=>RuntimeSupport.StartInfo(RuntimeSupport.Net12Development)).Code);
        Assert.Equal(CapabilityState.Supported,CompatibilityReports.Controller(CompatibilityReports.BuildPins(),"windows","x64",
            RuntimeSupport.Net12Development,"fixture","coreclr").State);
        ExecutionQualification.Validate(ExecutionHostPlatform.Windows,Qualified(),"quiet-validation",false);
    }

    [Theory,InlineData("6.0.36"),InlineData("8.0.33"),InlineData("9.0.22"),InlineData("10.1.0"),InlineData("11.0.0-rc.1.26425.128"),InlineData("11.0.0-rc.2.26504.105"),InlineData("12.0.0-dev"),InlineData("12.1.23"),InlineData("13.0.0-preview.1+build.42"),
        Case("UT-RUNTIME-SELECTION","FR-001,FR-037,FR-074","M7","Unit")]
    public void ExplicitExactSelectionAdmitsAllSupportedCoreClrVersionsAndEnforcesMatch(string version)
    {
        var handshake=Qualified() with { Target=Qualified().Target with { RuntimeVersion=version } };
        Assert.True(RuntimeSupport.IsSelectableManagedVersion(version));
        Assert.Equal(version!=RuntimeSupport.Net12Development,RuntimeSupport.Supports(version,"windows"));
        ExecutionQualification.Validate(ExecutionHostPlatform.Windows,handshake,"quiet-validation",false);
        ExecutionQualification.Validate(ExecutionHostPlatform.Windows,handshake,"quiet-validation",false,targetRuntimeVersion:version);
        Assert.Equal("not_comparable",Assert.Throws<LabException>(()=>ExecutionQualification.Validate(ExecutionHostPlatform.Windows,
            handshake,"quiet-validation",false,targetRuntimeVersion:version==RuntimeSupport.Net12Development?"12.0.1-dev":RuntimeSupport.Net12Development)).Code);
        if(version==RuntimeSupport.Net12Development)
            Assert.Equal("capability_unsupported",Assert.Throws<LabException>(()=>ExecutionQualification.Validate(ExecutionHostPlatform.Linux,
                handshake,"quiet-validation",false,targetRuntimeVersion:version)).Code);
    }

    [Theory,InlineData("8.0.33"),InlineData("9.0.22"),InlineData("10.0.14"),InlineData("10.0.99"),InlineData("11.0.0-rc.2.26504.105"),InlineData("12.0.0-preview.3")]
    public void NewerAndPriorCoreClrVersionsSupportManagedAndNativeExecutionAcrossPlatforms(string version)
    {
        Assert.True(RuntimeSupport.SupportsManagedExecution(version,"windows"));
        Assert.True(RuntimeSupport.SupportsManagedExecution(version,"linux"));
        Assert.True(RuntimeSupport.SupportsNormalBuild(version,"windows"));
        Assert.True(RuntimeSupport.SupportsNormalBuild(version,"linux"));
        Assert.True(RuntimeSupport.Supports(version,"windows"));
        Assert.True(RuntimeSupport.Supports(version,"linux"));
        var windowsHandshake=new HarnessHandshake(new("target",1,2,"runtime",version,"X64"),[],false);
        ExecutionQualification.Validate(ExecutionHostPlatform.Windows,windowsHandshake,"quiet-validation",false);
        ExecutionQualification.Validate(ExecutionHostPlatform.Windows,windowsHandshake,"cpu-diagnostic",false);
        ExecutionQualification.Validate(ExecutionHostPlatform.Windows,windowsHandshake,"quiet-validation",true);
        var linuxHandshake=new HarnessHandshake(new("target",1,2,"runtime",version,"X64"),[],false,
            Runtime:new("build",new Dictionary<string,string>(),"Linux fixture","X64",10000000,false,false,"1","0","","",OperatingSystemFamily:"linux"));
        ExecutionQualification.Validate(ExecutionHostPlatform.Linux,linuxHandshake,"quiet-validation",false);
        ExecutionQualification.Validate(ExecutionHostPlatform.Linux,linuxHandshake,"cpu-diagnostic",false);
        Assert.Equal("capability_unsupported",Assert.Throws<LabException>(()=>
            ExecutionQualification.Validate(ExecutionHostPlatform.Linux,linuxHandshake,"targeted-probes",false)).Code);
    }

    [Theory,InlineData(""),InlineData("12"),InlineData("12.0"),InlineData("12.*"),InlineData("latest"),InlineData("5.0.17"),
        InlineData("12.0.0-"),InlineData("12.0.0-preview..1"),InlineData("12.0.0+"),InlineData("12.0.0\n"),InlineData("012.0.0"),
        Case("UT-RUNTIME-SELECTION-INVALID","FR-001,FR-037,FR-074","M7","Unit")]
    public void SelectionRequiresAnExactCompatibleVersion(string version)
    {
        Assert.False(RuntimeSupport.IsSelectableManagedVersion(version));
        Assert.Equal("invalid_plan",Assert.Throws<LabException>(()=>ExecutionQualification.Validate(ExecutionHostPlatform.Windows,
            Qualified(),"quiet-validation",false,targetRuntimeVersion:version)).Code);
    }

    [Fact,Case("UT-RUNTIME-SELECTION-PIN","FR-001,FR-037,FR-074","M7","Unit")]
    public void QualifiedTargetsAlsoEnforceSelectedVersionWithoutNeedingDevelopmentImages()
    {
        var handshake=Qualified() with { Target=Qualified().Target with { RuntimeVersion=RuntimeSupport.Net10 },Runtime=null };
        ExecutionQualification.Validate(ExecutionHostPlatform.Windows,handshake,"quiet-validation",false,targetRuntimeVersion:RuntimeSupport.Net10);
        Assert.Equal("not_comparable",Assert.Throws<LabException>(()=>ExecutionQualification.Validate(ExecutionHostPlatform.Windows,
            handshake,"quiet-validation",false,targetRuntimeVersion:RuntimeSupport.Net11)).Code);
    }

    [Theory,InlineData("missing_footprint"),InlineData("missing_images"),InlineData("empty_hash"),InlineData("invalid_hash"),
        InlineData("null_hash"),InlineData("empty_mvid"),InlineData("linux"),InlineData("architecture"),InlineData("footprint_architecture"),
        InlineData("epoch"),InlineData("profiler"),InlineData("profiling_flag"),InlineData("debugger"),InlineData("hooks"),
        InlineData("dependencies"),InlineData("probes"),InlineData("control"),InlineData("diagnostic"),InlineData("footprint_os"),
        Case("UT-NET12-ADMISSION","FR-001,FR-032,FR-037,FR-070,FR-074","M7","Unit")]
    public void VersionLabelCannotAuthorizeMissingIdentityOrUnqualifiedObservers(string defect)
    {
        var handshake=Qualified(); var runtime=handshake.Runtime!;
        runtime=defect switch
        {
            "missing_images"=>runtime with { RuntimeImages=null },
            "empty_hash"=>runtime with { RuntimeImages=runtime.RuntimeImages! with { CoreLibHash="" } },
            "invalid_hash"=>runtime with { RuntimeImages=runtime.RuntimeImages! with { JitHash=new('x',64) } },
            "null_hash"=>runtime with { RuntimeImages=runtime.RuntimeImages! with { CoreClrHash=null! } },
            "empty_mvid"=>runtime with { RuntimeImages=runtime.RuntimeImages! with { CoreLibModuleVersionId=Guid.Empty.ToString() } },
            "profiler"=>runtime with { NativeProfilerPresent=true },"profiling_flag"=>runtime with { ProfilingEnabled="1" },
            "debugger"=>runtime with { DebuggerAttached=true },"hooks"=>runtime with { StartupHooks="foreign.dll" },
            "dependencies"=>runtime with { AdditionalDependencies="foreign.json" },
            "footprint_architecture"=>runtime with { Architecture="Arm64" },
            "footprint_os"=>runtime with { OperatingSystemFamily="linux" },_=>runtime
        };
        handshake=handshake with { Runtime=defect=="missing_footprint"?null:runtime };
        if(defect=="epoch") handshake=handshake with { Target=handshake.Target with { Epoch=1 } };
        if(defect=="architecture") handshake=handshake with { Target=handshake.Target with { Architecture="Arm64" } };
        if(defect=="probes") handshake=handshake with { ProbeHelper=new("1","hash","mvid",true,"plan","scope") };
        Assert.Equal("capability_unsupported",Assert.Throws<LabException>(()=>ExecutionQualification.Validate(
            defect=="linux"?ExecutionHostPlatform.Linux:ExecutionHostPlatform.Windows,handshake,
            defect=="diagnostic"?"cpu-diagnostic":"quiet-validation",defect=="control")).Code);
        Assert.Equal("capability_unsupported",Assert.Throws<LabException>(()=>ExecutionQualification.Validate(
            defect=="linux"?ExecutionHostPlatform.Linux:ExecutionHostPlatform.Windows,handshake,
            defect=="diagnostic"?"cpu-diagnostic":"quiet-validation",defect=="control",targetRuntimeVersion:RuntimeSupport.Net12Development)).Code);
    }

    [Fact,Case("CT-NET12-MATRIX","FR-001,FR-004,FR-074","M7","Contract")]
    public void CompatibilityListsManagedExecutionSeparatelyFromUnqualifiedFeatures()
    {
        var report=CompatibilityReports.Create("missing-profiler.dll");
        var target=Assert.Single(report.Platforms,item=>item.RuntimeVersion==RuntimeSupport.Net12Development);
        Assert.Equal("windows",target.OperatingSystem); Assert.Equal("x64",target.Architecture);
        Assert.Equal(CapabilityState.Supported,Assert.Single(target.Features,item=>item.Name=="registered_scenarios").State);
        Assert.All(target.Features.Where(item=>item.Name!="registered_scenarios"),item=>Assert.Equal(CapabilityState.Unsupported,item.State));
        Assert.Equal("target_runtime_version",report.ManagedRuntimeSelection!.PlanField);
        Assert.Equal("all-supported-profiles",report.ManagedRuntimeSelection.Profile);
    }

    [Fact,Case("CT-RUNTIME-SELECTION-PLAN","FR-001,FR-037,FR-074","M7","Contract")]
    public async Task McpPlansFreezeSelectionAndRejectInvalidVersions()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var tools=new LabTools(service);
        var request=new CreatePlan("scenario","1","input",["baseline","candidate"],TargetRuntimeVersion:"12.1.23");
        var response=await tools.Experiment(request); Assert.True(response.Status=="ok",Wire.Serialize(response));
        var plan=response.Data!.Value.Deserialize<ExperimentPlan>(Wire.Json)!;
        Assert.Equal("12.1.23",service.Catalog.Get<ExperimentPlan>(plan.Id).TargetRuntimeVersion);
        Assert.NotEqual(Wire.Hash(plan),Wire.Hash(plan with { TargetRuntimeVersion=null }));
        var latency=await tools.Experiment(new CreateIndividualLatencyPlan("scenario","1","input","baseline",TargetRuntimeVersion:"12.1.23"));
        Assert.True(latency.Status=="ok",Wire.Serialize(latency));
        var individual=latency.Data!.Value.Deserialize<IndividualLatencyPlan>(Wire.Json)!;
        Assert.Equal("12.1.23",service.Catalog.Get<IndividualLatencyPlan>(individual.Id).TargetRuntimeVersion);
        Assert.Equal("invalid_plan",(await tools.Experiment(request with { TargetRuntimeVersion="12.*" })).Code);
        Assert.Equal("invalid_plan",(await tools.Experiment(new CreateIndividualLatencyPlan("scenario","1","input","baseline",TargetRuntimeVersion:"12.*"))).Code);
        Assert.DoesNotContain("target_runtime_version",Wire.Serialize(plan with { TargetRuntimeVersion=null }));
    }

    [SelectedManagedRuntimeFact,Case("IT-RUNTIME-SELECTION","FR-001,FR-037,FR-070,FR-074","M7","Integration")]
    public async Task InstalledRuntimeRetainsImagesAndEnforcesOptionalExactSelection()
    {
        string version=Environment.GetEnvironmentVariable("DOTNETLAB_MANAGED_RUNTIME_VERSION")!;
        Assert.True(RuntimeSupport.Supports(version));
        using var workspace=new TestWorkspace();
        var start=new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNETLAB_MANAGED_DOTNET")!)
        { UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true };
        start.ArgumentList.Add("exec"); start.ArgumentList.Add("--fx-version"); start.ArgumentList.Add(version);
        start.ArgumentList.Add(TestWorkspace.Workload()); start.ArgumentList.Add("--harness"); start.ArgumentList.Add(workspace.Root);
        start.Environment["DOTNETLAB_EXIT_ON_STDIN"]="1";
        using var process=Process.Start(start)!;
        try
        {
            Assert.NotNull(await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)));
            await using var service=new LabService(workspace.Root);
            var target=service.Discover().Single(item=>item.Identity.ProcessId==process.Id).Identity;
            var handshake=await service.ScenariosAsync(target.Id,CancellationToken.None);
            Assert.Equal(version,handshake.Target.RuntimeVersion); Assert.NotNull(handshake.Runtime!.RuntimeImages);
            string runtimeRoot=Path.Combine(Path.GetDirectoryName(start.FileName)!,"shared","Microsoft.NETCore.App",version);
            string Hash(string name)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(runtimeRoot,name))));
            Assert.Equal(Hash("System.Private.CoreLib.dll"),handshake.Runtime.RuntimeImages.CoreLibHash);
            Assert.Equal(Hash("coreclr.dll"),handshake.Runtime.RuntimeImages.CoreClrHash);
            Assert.Equal(Hash("clrjit.dll"),handshake.Runtime.RuntimeImages.JitHash);
            var scenario=handshake.Scenarios.Single(item=>item.Id=="cpu-sum");
            var plan=service.Plan(new("",scenario.Id,scenario.Version,scenario.InputHash,scenario.Variants,1,100,1,42,
                "quiet-validation",false,"",false,60,new(),TargetRuntimeVersion:version));
            var grant=service.IssueGrant(target.Id,Permission.ExecuteRegisteredScenario,60,4);
            var operation=await service.StartExecutionAsync(target.Id,0,plan.Id,grant.Id,"selected-installed",CancellationToken.None);
            var completed=await ServiceTests.Wait(service,operation.Id);
            Assert.True(completed.State==OperationState.Completed,completed.ErrorCode);
            var mismatched=service.Plan(plan with { TargetRuntimeVersion="99.0.0" });
            Assert.Equal("not_comparable",(await Assert.ThrowsAsync<LabException>(()=>service.StartExecutionAsync(target.Id,0,
                mismatched.Id,grant.Id,"mismatched-installed",CancellationToken.None))).Code);
            await process.StandardInput.WriteLineAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(process.ExitCode==0,await process.StandardError.ReadToEndAsync());
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }

    [Net12CoreRunFact,Case("IT-NET12-CORE-RUN","FR-001,FR-037,FR-070,FR-074","M7","Integration")]
    public async Task RealCoreRunHandshakeRetainsLoadedRuntimeImagesAndManagedAdmission()
    {
        string coreRoot=Path.GetFullPath(Environment.GetEnvironmentVariable("DOTNETLAB_NET12_CORE_ROOT")!);
        using var workspace=new TestWorkspace();
        var start=new ProcessStartInfo(Path.Combine(coreRoot,"corerun.exe"))
        { UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true };
        start.ArgumentList.Add(TestWorkspace.Workload()); start.ArgumentList.Add("--harness"); start.ArgumentList.Add(workspace.Root);
        start.Environment["DOTNETLAB_EXIT_ON_STDIN"]="1";
        using var process=Process.Start(start)!;
        try
        {
            string? line=await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.NotNull(line);
            var identity=new Tedd.DotNetLab.Collector.EventPipe.Targets().Discover().Single(item=>item.Identity.ProcessId==process.Id).Identity;
            var client=new HarnessClient(); var handshake=await client.HandshakeAsync(identity,CancellationToken.None);
            Assert.Equal(RuntimeSupport.Net12Development,handshake.Target.RuntimeVersion);
            var images=handshake.Runtime!.RuntimeImages; Assert.NotNull(images);
            string Hash(string name)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(coreRoot,name))));
            Assert.Equal(Hash("System.Private.CoreLib.dll"),images.CoreLibHash);
            Assert.Equal(Hash("coreclr.dll"),images.CoreClrHash); Assert.Equal(Hash("clrjit.dll"),images.JitHash);
            Assert.True(Guid.TryParse(images.CoreLibModuleVersionId,out _));
            ExecutionQualification.Validate(ExecutionHostPlatform.Windows,handshake,"quiet-validation",false);
            var again=await client.HandshakeAsync(identity,CancellationToken.None);
            Assert.Equal(Wire.Hash(handshake.Runtime),Wire.Hash(again.Runtime));
            await using(var service=new LabService(workspace.Root))
            {
                var target=service.Discover().Single(item=>item.Identity.ProcessId==process.Id).Identity;
                var scenarios=await service.ScenariosAsync(target.Id,CancellationToken.None);
                var scenario=scenarios.Scenarios.Single(item=>item.Id=="cpu-sum");
                var tools=new LabTools(service);
                var response=await tools.Experiment(new CreatePlan(scenario.Id,scenario.Version,scenario.InputHash,scenario.Variants,
                    Blocks:1,OperationsPerRun:100,TargetRuntimeVersion:RuntimeSupport.Net12Development));
                Assert.True(response.Status=="ok",Wire.Serialize(response));
                var plan=response.Data!.Value.Deserialize<ExperimentPlan>(Wire.Json)!;
                var grant=service.IssueGrant(target.Id,Permission.ExecuteRegisteredScenario,60,10);
                var operation=await service.StartExecutionAsync(target.Id,0,plan.Id,grant.Id,"selected-core-run",CancellationToken.None);
                var completed=await ServiceTests.Wait(service,operation.Id);
                Assert.True(completed.State==OperationState.Completed,completed.ErrorCode);
                var execution=service.Catalog.Get<ExecutionRecord>(completed.ResultId!);
                Assert.Equal(RuntimeSupport.Net12Development,execution.Target.RuntimeVersion); Assert.Equal(2,execution.RunIds.Count);
                var individual=service.CreateIndividualLatencyPlan(scenario.Id,scenario.Version,scenario.InputHash,"baseline",3,
                    targetRuntimeVersion:RuntimeSupport.Net12Development);
                var individualOperation=await service.StartIndividualLatenciesAsync(target.Id,0,individual.Id,grant.Id,"selected-core-run-individual",CancellationToken.None);
                var individualCompleted=await ServiceTests.Wait(service,individualOperation.Id);
                Assert.True(individualCompleted.State==OperationState.Completed,individualCompleted.ErrorCode);
                Assert.Equal(images,service.Catalog.Get<IndividualLatencyRun>(individualCompleted.ResultId!).Runtime.RuntimeImages);
                var mismatch=service.Plan(plan with { TargetRuntimeVersion="12.0.1-dev" });
                Assert.Equal("not_comparable",(await Assert.ThrowsAsync<LabException>(()=>service.StartExecutionAsync(target.Id,0,mismatch.Id,grant.Id,
                    "mismatched-core-run",CancellationToken.None))).Code);
                var mismatchedIndividual=service.CreateIndividualLatencyPlan(scenario.Id,scenario.Version,scenario.InputHash,"baseline",3,
                    targetRuntimeVersion:"12.0.1-dev");
                Assert.Equal("not_comparable",(await Assert.ThrowsAsync<LabException>(()=>service.StartIndividualLatenciesAsync(target.Id,0,
                    mismatchedIndividual.Id,grant.Id,"mismatched-core-run-individual",CancellationToken.None))).Code);
            }
            await process.StandardInput.WriteLineAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(process.ExitCode==0,await process.StandardError.ReadToEndAsync());
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }
}

public sealed class Net12CoreRunFactAttribute : FactAttribute
{
    public Net12CoreRunFactAttribute()
    {
        if(!OperatingSystem.IsWindows()||string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNETLAB_NET12_CORE_ROOT")))
            Skip="Set DOTNETLAB_NET12_CORE_ROOT to an explicit Windows x64 .NET 12 development Core_Root for qualification.";
    }
}

public sealed class SelectedManagedRuntimeFactAttribute : FactAttribute
{
    public SelectedManagedRuntimeFactAttribute()
    {
        if(!OperatingSystem.IsWindows()||string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNETLAB_MANAGED_RUNTIME_VERSION"))||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNETLAB_MANAGED_DOTNET")))
            Skip="Set DOTNETLAB_MANAGED_DOTNET and DOTNETLAB_MANAGED_RUNTIME_VERSION to an explicit unlisted Windows x64 runtime for qualification.";
    }
}
