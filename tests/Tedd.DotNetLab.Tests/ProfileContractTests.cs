using System.Text.Json;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Mcp;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class ProfileContractTests
{
    [Fact,Case("UT-CAP-001","FR-010,FR-017","M1","Contract")]
    public void BaselineManifestHasExactDeclaredProviderConfiguration()
    {
        var profile=Profiles.Resolve("baseline-context");
        Assert.Equal("net10/1",profile.Version); Assert.Equal(32,profile.BufferMiB); Assert.False(profile.Rundown);
        Assert.Equal(new[] {"Microsoft-Windows-DotNETRuntime","Tedd.DotNetLab-Harness","System.Runtime"},profile.Providers.Select(p=>p.Name));
        Assert.Equal(new long[] {1,-1,0},profile.Providers.Select(p=>p.Keywords));
        Assert.All(profile.Providers,p=>Assert.Equal(4,p.Level));
        Assert.Empty(profile.Providers[0].Arguments); Assert.Empty(profile.Providers[1].Arguments);
        Assert.Equal("1",Assert.Single(profile.Providers[2].Arguments).Value);
        Assert.True(profile.Providers[2].Arguments.ContainsKey("EventCounterIntervalSec"));
        Assert.Equal(new[] {"gc","suspension","counters","markers"},profile.DataClasses);
    }

    [Fact,Case("UT-CAP-002","FR-010,FR-017,FR-024","M1","Contract")]
    public void AllocationAndSamplingProfilesHaveDistinctQualifiedManifests()
    {
        var allocation=Profiles.Resolve("allocation-diagnostic"); var cpu=Profiles.Resolve("cpu-diagnostic");
        Assert.Equal(1,allocation.Providers[0].Keywords); Assert.Equal(5,allocation.Providers[0].Level);
        Assert.Contains("allocation_samples",allocation.DataClasses); Assert.DoesNotContain("samples",allocation.DataClasses);
        Assert.False(allocation.Rundown); Assert.DoesNotContain(allocation.Providers,p=>p.Name=="Microsoft-DotNETCore-SampleProfiler");
        Assert.Equal(0x19,cpu.Providers[0].Keywords); Assert.Equal(4,cpu.Providers[0].Level); Assert.True(cpu.Rundown);
        var sampler=Assert.Single(cpu.Providers,p=>p.Name=="Microsoft-DotNETCore-SampleProfiler");
        Assert.Equal(0,sampler.Keywords); Assert.Equal(4,sampler.Level); Assert.Empty(sampler.Arguments);
        Assert.Contains("samples",cpu.DataClasses); Assert.DoesNotContain("allocation_samples",cpu.DataClasses);
        Assert.NotEqual(allocation.Hash,cpu.Hash);
    }

    [Fact,Case("UT-CAP-003,UT-CAP-004","FR-010,FR-074","M1","Contract")]
    public void CanonicalProfileHashIgnoresPropertyOrderAndBindsProviderArgumentsAndLimits()
    {
        var profile=Profiles.Resolve("baseline-context");
        var reordered=new { profile.DataClasses,profile.Rundown,profile.BufferMiB,profile.Providers,profile.Version,profile.Name };
        Assert.Equal(profile.Hash,Wire.Hash(reordered));
        Assert.NotEqual(profile.Hash,(profile with { BufferMiB=64 }).Hash);
        Assert.NotEqual(profile.Hash,(profile with { Rundown=true }).Hash);
        var providers=profile.Providers.ToArray(); providers[0]=providers[0] with { Keywords=3 };
        Assert.NotEqual(profile.Hash,(profile with { Providers=providers }).Hash);
        providers=profile.Providers.ToArray(); providers[2]=providers[2] with { Arguments=new Dictionary<string,string> { ["EventCounterIntervalSec"]="2" } };
        Assert.NotEqual(profile.Hash,(profile with { Providers=providers }).Hash);
        Assert.Equal(profile.Hash,Profiles.Resolve(profile.Name).Hash);
    }

    [Fact,Case("UT-PROFILE-001","FR-010,FR-017,FR-060","M3","Contract")]
    public async Task PublicInventoryContainsRequiredProfilesAndDoesNotGrantProbeInstallation()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var response=await new LabTools(service).Discover(new DiscoverProfiles()); Assert.Equal("ok",response.Status);
        var profiles=response.Data!.Value.EnumerateArray().ToDictionary(p=>p.GetProperty("name").GetString()!);
        foreach(string name in new[] {"quiet-validation","baseline-context","cpu-diagnostic","allocation-diagnostic","targeted-probes"})
        {
            Assert.True(profiles.ContainsKey(name));
            var manifest=profiles[name].GetProperty("manifest").Deserialize<CaptureProfile>(Wire.Json)!;
            Assert.Equal(Profiles.Resolve(name).Hash,manifest.Hash); Assert.Equal(32,manifest.BufferMiB);
        }
        var quiet=Profiles.Resolve("quiet-validation"); Assert.Empty(quiet.Providers); Assert.Equal(new[] {"harness"},quiet.DataClasses);
        var targeted=Profiles.Resolve("targeted-probes"); Assert.Empty(targeted.Providers); Assert.Contains("selected_probes",targeted.DataClasses);
        Assert.Equal("requires_verified_probe_plan",profiles["targeted-probes"].GetProperty("capability").GetString());
        Assert.Empty(service.Catalog.List("grant").Rows); Assert.Empty(service.Catalog.List("operation").Rows);
    }

    [Fact,Case("UT-CAP-009","FR-015,FR-017","M1","Integration")]
    public async Task QuietPlanDoesNotErasePreviouslyLoadedProfilerFootprint()
    {
        using var workspace=new TestWorkspace(); using var process=await ProfilerTests.StartNativeTarget(workspace.Root);
        try
        {
            await using var service=new LabService(workspace.Root); var target=service.Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity;
            await service.ProfilerAsync(target.Id,CancellationToken.None);
            var handshake=await service.ScenariosAsync(target.Id,CancellationToken.None);
            var scenario=handshake.Scenarios.Single(s=>s.Id=="cpu-sum");
            var plan=service.Plan(new("",scenario.Id,scenario.Version,scenario.InputHash,scenario.Variants,1,1,0,
                42,"quiet-validation",false,"descriptive fixture",false,30,new()));
            Assert.Equal("quiet-validation",plan.Profile); Assert.Empty(Profiles.Resolve(plan.Profile).Providers);
            Assert.True(handshake.Runtime!.NativeProfilerPresent); Assert.Equal("1",handshake.Runtime.ProfilingEnabled);
            var grant=service.IssueGrant(target.Id,Permission.ExecuteRegisteredScenario,60,2);
            var completed=await ServiceTests.Wait(service,(await service.StartExecutionAsync(target.Id,0,plan.Id,grant.Id,"quiet-loaded-profiler",CancellationToken.None)).Id);
            Assert.Equal(OperationState.Completed,completed.State);
            foreach(string runId in service.Catalog.Get<ExecutionRecord>(completed.ResultId!).RunIds)
            {
                var run=service.Catalog.Get<RunRecord>(runId); Assert.True(run.Runtime!.NativeProfilerPresent);
                Assert.Equal("1",run.Runtime.ProfilingEnabled); Assert.Contains("other_observers_unknown",run.ObserverState);
                Assert.NotNull(run.Compilation); Assert.Equal("original_normal_compile",run.Compilation.Regime);
            }
            Assert.Empty(service.Catalog.List("capture").Rows);
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }
}
