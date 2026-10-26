using System.Diagnostics;
using System.Text.Json;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Mcp;
using Xunit;

namespace Tedd.DotNetLab.Tests;

[CollectionDefinition("ConfirmationEnvironment",DisableParallelization=true)]
public sealed class ConfirmationEnvironment;

[Collection("ConfirmationEnvironment")]
public sealed class ConfirmationTests
{
    [Theory,InlineData("io"),InlineData("cancel"),InlineData("corrupt"),Case("UT-EXP-STAGING-CLEANUP","FR-016,FR-037,FR-055,FR-072","M6","Integration")]
    public async Task PartialBuildStagingFailureCannotLaunchOrRetainOwnedFiles(string failure)
    {
        using var workspace=new TestWorkspace(); string? staged=null;
        await using var service=new LabService(workspace.Root,normalBuildFileStaged:path=>
        {
            if(staged is not null) return; staged=path; Assert.True(File.Exists(path));
            if(failure=="io") throw new IOException("controlled staged-copy failure");
            if(failure=="cancel") throw new OperationCanceledException("controlled staging cancellation");
            File.WriteAllText(path,"controlled staged corruption");
        });
        var plan=await Plan(service); var grant=service.IssueConfirmationGrant(plan.Id,120,20);
        var done=await ServiceTests.Wait(service,service.StartConfirmation(plan.Id,grant.Id,"staging-failure").Id);
        Assert.Equal(OperationState.Failed,done.State); Assert.NotNull(staged); Assert.False(File.Exists(staged));
        Assert.Empty(Directory.GetDirectories(Path.Combine(workspace.Root,"launches")));
        Assert.Empty(service.Catalog.List("normal_build_launch").Rows);
        var result=service.Catalog.Get<ConfirmationExecution>(done.ResultId!);
        Assert.Equal(failure=="cancel"?"cancelled":failure=="corrupt"?"artifact_corrupt":"confirmation_failed",result.Failure);
        Assert.Empty(result.SessionIds);
        foreach(string id in new[]{plan.BaselineBuildId,plan.CandidateBuildId})
            foreach(var file in service.Catalog.Get<NormalBuild>(id).Files)
            {
                var artifact=service.Catalog.Get<Artifact>(file.ArtifactId); await service.Artifacts.VerifyAsync(artifact.Hash,artifact.Length);
            }
    }
    [Fact,Case("UT-CONFIRM-RUNTIME-SELECTION","FR-001,FR-037,FR-074","M7","Integration")]
    public async Task ConfirmationRequiresBothBuildsToMatchSelectedRuntime()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var plan=await Plan(service);
        string version=service.Catalog.Get<NormalBuild>(plan.BaselineBuildId).RuntimeVersion;
        var selected=service.FreezeConfirmation(plan with { Experiment=plan.Experiment with { TargetRuntimeVersion=version } });
        Assert.Equal(version,selected.Experiment.TargetRuntimeVersion);
        Assert.Equal("not_comparable",Assert.Throws<LabException>(()=>service.FreezeConfirmation(plan with
        { Experiment=plan.Experiment with { TargetRuntimeVersion="12.0.0-dev" } })).Code);
    }

    private static async Task<NormalBuild> ImportBuild(LabService service,string project,string framework="net10.0")
    {
        string directory=Path.Combine(TestWorkspace.TaskRoot,"bin",project,"Release",framework);
        var files=new List<BuildFile>();
        foreach(string path in Directory.GetFiles(directory).Where(p=>p.EndsWith(".dll",StringComparison.Ordinal)||p.EndsWith(".deps.json",StringComparison.Ordinal)||p.EndsWith(".runtimeconfig.json",StringComparison.Ordinal)))
        {
            var artifact=await service.ImportAsync(path,path.EndsWith(".dll",StringComparison.Ordinal)?"application/vnd.microsoft.portable-executable":"application/json");
            files.Add(new(Path.GetFileName(path),artifact.Id));
        }
        var reply=await new LabTools(service).Experiment(new RegisterNormalBuild(project+".dll",files));
        Assert.True(reply.Status=="ok",Wire.Serialize(reply));
        return reply.Data!.Value.Deserialize<NormalBuild>(Wire.Json)!;
    }
    private static async Task<ConfirmationPlan> Plan(LabService service,string scenario="normal-sum",int seconds=60,IReadOnlyList<MetricPolicy>? guardrails=null,MetricPolicy? primary=null,string framework="net10.0")
    {
        var baseline=await ImportBuild(service,"Tedd.DotNetLab.NormalBaseline",framework);
        var candidate=await ImportBuild(service,"Tedd.DotNetLab.NormalCandidate",framework);
        var selected=service.Candidate("Closed-form sum",candidate.ContentHash,[]);
        var evidence=service.Hypothesis("Summation loop is replaced by an equivalent integer formula.","Cost per operation decreases.",[]);
        var source=await service.Artifacts.WriteJsonAsync(Wire.Id("source"),new { source="tests/Fixtures/normal-build/Program.cs",baseline="loop",candidate="integer formula",input="1024..1031" },"test-source/1");
        service.Catalog.Put(source.Id,"artifact",source);
        var experiment=new ExperimentPlan("",scenario,"1",Wire.Hash(new { input=1024 }),["baseline","candidate"],10,4000,1,42,
            "quiet-validation",true,"Each arm starts in an independent owned process; matched blocks are independent sessions.",true,seconds,
            new(FamilySize:1+(guardrails?.Count(guardrail=>!guardrail.DescriptiveOnly)??0)),FreshNormalBuild:true,
            GuardrailMetrics:guardrails?.Select(guardrail=>guardrail.Metric).ToArray(),GuardrailPolicies:guardrails,
            PrimaryMetric:primary?.Metric??"scenario.ns_per_operation",PrimaryPolicy:primary);
        var reply=await new LabTools(service).Experiment(new CreateConfirmationPlan(selected.Id,source.Id,
            "For each retained integer input, n(n+1)/2 equals the loop result without overflow.",baseline.Id,candidate.Id,"baseline","candidate",experiment,[evidence.Id]));
        Assert.True(reply.Status=="ok",Wire.Serialize(reply));
        return reply.Data!.Value.Deserialize<ConfirmationPlan>(Wire.Json)!;
    }
    [Fact,Case("IT-NET11-CONFIRMATION","FR-033,FR-037,FR-055","M7","Integration")]
    public async Task Net11NormalBuildConfirmationLaunchesTheManifestRuntimeAcrossControllerVersions()
    {
        if(!OperatingSystem.IsWindows()) return;
        string baselineDir=Path.Combine(TestWorkspace.TaskRoot,"bin","Tedd.DotNetLab.NormalBaseline","Release","net11.0");
        if(!Directory.Exists(baselineDir)) return;
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var plan=await Plan(service,framework:"net11.0");
        string manifestVersion=service.Catalog.Get<NormalBuild>(plan.BaselineBuildId).RuntimeVersion;
        Assert.StartsWith("11.0.",manifestVersion,StringComparison.Ordinal);
        RuntimeSupport.ResolveRuntimeDirectory(manifestVersion,"Minor",out string expectedRuntime);
        var grant=service.IssueConfirmationGrant(plan.Id,120,20);
        var complete=await ServiceTests.Wait(service,service.StartConfirmation(plan.Id,grant.Id,"net11-confirmation").Id);
        Assert.True(complete.State==OperationState.Completed,Wire.Serialize(complete));
        var result=service.Catalog.Get<ConfirmationExecution>(complete.ResultId!); Assert.Equal(20,result.SessionIds.Count);
        foreach(string id in result.SessionIds)
        {
            var session=service.Catalog.Get<NormalBuildSession>(id);
            Assert.Equal(expectedRuntime,session.Target.RuntimeVersion); Assert.False(session.Runtime.NativeProfilerPresent);
            Assert.Equal(0,session.ExitCode); Assert.True(session.Measurement.Correct);
        }
    }
    [Theory,InlineData("scenario.throughput_ops_s"),InlineData("allocation.process_bytes"),InlineData("allocation.thread_bytes"),
        Case("IT-E2E-002,UT-STAT-028","FR-033,FR-036,FR-055","M6","Integration")]
    public async Task FreshNormalBuild_UsesFrozenPrimaryMetricAndNeverProjectsAbsoluteBytesAsRatios(string metric)
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var policy=PrimaryObjectiveTests.Policy(metric); var plan=await Plan(service,primary:policy); var grant=service.IssueConfirmationGrant(plan.Id,120,20);
        var complete=await ServiceTests.Wait(service,service.StartConfirmation(plan.Id,grant.Id,"primary-holdout").Id);
        Assert.True(complete.State==OperationState.Completed,Wire.Serialize(complete)); var decision=await service.CompareConfirmationAsync(complete.ResultId!);
        var comparison=service.Catalog.Get<Comparison>(decision.ComparisonId); Assert.Equal(policy,comparison.PrimaryEffect!.Policy);
        var vector=await service.ReadObjectivesAsync(decision.ObjectiveVectorId!); Assert.Equal(vector.PromotionEligible,decision.PromotionEligible);
        Assert.Equal(vector.Effects[0].Effect,comparison.PrimaryEffect.Effect);
        if(policy.Scale==EffectScale.AbsoluteDifference)
        { Assert.Null(comparison.Ratio); Assert.Null(comparison.ChangePercent); Assert.Null(comparison.DifferenceIntervalRatio); Assert.Equal("bytes",comparison.PrimaryEffect.EffectUnit); }
        else
        { Assert.Equal(EffectClass.Improved,comparison.EffectClass); Assert.True(comparison.DifferenceIntervalRatio!.Lower>policy.UsefulImprovement); Assert.True(decision.PromotionEligible); }
    }
    [Fact,Case("UT-EXP-008","FR-035,FR-055","M6","Integration")]
    public async Task FreshNormalBuild_FrozenHoldout_RecordsTwentyOwnedSessionsAndPromotesOnlyUsefulBenefit()
    {
        using var workspace=new TestWorkspace(); string executionId,decisionId,comparisonId;
        await using(var service=new LabService(workspace.Root))
        {
            var plan=await Plan(service); var tools=new LabTools(service);
            var reference=Assert.Single(plan.FrozenEvidence!);
            var hypothesis=service.Catalog.Get<Hypothesis>(reference.Id);
            service.Hypothesis(hypothesis.Mechanism,"Revised exploratory narrative.",[],hypothesis.Id,reference.Revision);
            Assert.Equal(reference.Hash,service.Catalog.Get(reference.Id,reference.Revision).Hash);
            var grant=service.IssueConfirmationGrant(plan.Id,120,20);
            // Controller environment injection must not propagate into any owned child.
            string? prior=Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS");
            Operation started;
            try
            {
                Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS",Path.Combine(workspace.Root,"unregistered-hook.dll"));
                var reply=await tools.Run(new StartConfirmation(plan.Id,grant.Id,"holdout"));
                Assert.True(reply.Status=="ok",Wire.Serialize(reply));
                started=reply.Data!.Value.Deserialize<Operation>(Wire.Json)!;
                var done=await ServiceTests.Wait(service,started.Id);
                Assert.True(done.State==OperationState.Completed,Wire.Serialize(done)); executionId=done.ResultId!;
            }
            finally { Environment.SetEnvironmentVariable("DOTNET_STARTUP_HOOKS",prior); }
            var execution=service.Catalog.Get<ConfirmationExecution>(executionId);
            Assert.Equal(20,execution.SessionIds.Count);
            Assert.Empty(Directory.GetDirectories(Path.Combine(workspace.Root,"launches")));
            var sessions=execution.SessionIds.Select(service.Catalog.Get<NormalBuildSession>).ToArray();
            Assert.Equal(20,sessions.Select(s=>(s.Target.ProcessId,s.Target.StartTimeUtcTicks)).Distinct().Count());
            Assert.All(sessions,s=>
            {
                Assert.True(s.Measurement.Correct&&s.Measurement.Quiescent);
                Assert.True(s.StartedAt>=plan.FrozenAt&&s.ExitedAt>s.StartedAt);
                Assert.Equal("0",s.Runtime.DiagnosticsEnabled); Assert.Equal("",s.Runtime.StartupHooks);
                Assert.False(s.Runtime.NativeProfilerPresent||s.Runtime.DebuggerAttached);
                Assert.Equal("normal-build/fresh-process",s.CompilationRegime);
                Assert.True(service.Catalog.IsPinned(s.MeasurementArtifactId));
            });
            var simultaneous=await Task.WhenAll(tools.Compare(new(executionId,true)),tools.Compare(new(executionId,true)));
            Assert.All(simultaneous,reply=>Assert.True(reply.Status=="ok",Wire.Serialize(reply)));
            Assert.Equal(Wire.Hash(simultaneous[0].Data),Wire.Hash(simultaneous[1].Data));
            var comparisonReply=simultaneous[0];
            Assert.True(comparisonReply.Status=="ok",Wire.Serialize(comparisonReply));
            var decision=comparisonReply.Data!.Value.Deserialize<ConfirmationDecision>(Wire.Json)!;
            var comparison=service.Catalog.Get<Comparison>(decision.ComparisonId);
            Assert.Equal(EffectClass.Improved,comparison.EffectClass); Assert.True(decision.PromotionEligible);
            Assert.True(comparison.DifferenceIntervalRatio!.Upper<plan.Experiment.Analysis.UsefulImprovementRatio);
            decisionId=decision.Id; comparisonId=comparison.Id;
            Assert.Equal(started.Id,service.StartConfirmation(plan.Id,grant.Id,"holdout").Id);
            Assert.Equal("idempotency_conflict",Assert.Throws<LabException>(()=>service.StartConfirmation(plan.Id,grant.Id,"extra-samples")).Code);
            Assert.Throws<LabException>(()=>service.Catalog.Put(plan.Id,"confirmation_plan",plan with { Experiment=plan.Experiment with { Blocks=20 } },1));
        }
        await using var reopened=new LabService(workspace.Root);
        var retained=await reopened.CompareConfirmationAsync(executionId);
        Assert.Equal(decisionId,retained.Id); Assert.Equal(comparisonId,retained.ComparisonId);
        var source=reopened.Catalog.Get<Artifact>(retained.SourcePatchArtifactId);
        reopened.Artifacts.Delete(source);
        Assert.Equal("artifact_expired",(await Assert.ThrowsAsync<LabException>(()=>reopened.CompareConfirmationAsync(executionId))).Code);
    }
    [Fact,Case("UT-SEC-004","FR-055,FR-070","M6","Integration")]
    public async Task Confirmation_GrantScopeAndUnsupportedGuardrails_CannotBypassFrozenPolicy()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var plan=await Plan(service);
        Assert.Equal("invalid_plan",Assert.Throws<LabException>(()=>service.Plan(plan.Experiment)).Code);
        Assert.Equal("invalid_plan",Assert.Throws<LabException>(()=>service.FreezeConfirmation(plan with { Experiment=plan.Experiment with { GuardrailMetrics=["gc.pause"] } })).Code);
        Assert.Equal("invalid_request",Assert.Throws<LabException>(()=>service.IssueConfirmationGrant(plan.Id,60,19)).Code);
        var expired=new Grant(Wire.Id("grant"),service.Catalog.Root,plan.Id,0,Permission.LaunchRegisteredBuild,DateTimeOffset.UtcNow.AddSeconds(-1),20,plan.ContentHash);
        service.Catalog.Put(expired.Id,"grant",expired);
        Assert.Equal("permission_denied",Assert.Throws<LabException>(()=>service.StartConfirmation(plan.Id,expired.Id,"expired")).Code);
        var wrong=expired with { Id=Wire.Id("grant"),ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(1),Permission=Permission.ExecuteRegisteredScenario };
        service.Catalog.Put(wrong.Id,"grant",wrong);
        Assert.Equal("permission_denied",Assert.Throws<LabException>(()=>service.StartConfirmation(plan.Id,wrong.Id,"wrong-class")).Code);
        var wrongHash=wrong with { Id=Wire.Id("grant"),Permission=Permission.LaunchRegisteredBuild,ContentHash=Wire.Hash("another-plan") };
        service.Catalog.Put(wrongHash.Id,"grant",wrongHash);
        Assert.Equal("permission_denied",Assert.Throws<LabException>(()=>service.StartConfirmation(plan.Id,wrongHash.Id,"wrong-hash")).Code);
        Assert.Empty(service.Catalog.List("normal_build_launch").Rows);
    }
    [Theory,InlineData("normal-wrong"),InlineData("normal-slow"),Case("UT-HAR-004","FR-037,FR-055","M6","Integration")]
    public async Task Confirmation_FailedCorrectnessOrCancellation_JoinsOwnedProcessAndRejectsInference(string scenario)
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var plan=await Plan(service,scenario); var grant=service.IssueConfirmationGrant(plan.Id,120,20);
        var operation=service.StartConfirmation(plan.Id,grant.Id,"failed-holdout");
        if(scenario=="normal-slow")
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while(service.Catalog.List("normal_build_launch").Rows.Count==0) await Task.Delay(10,timeout.Token);
            await Task.Delay(150); service.Cancel(operation.Id);
        }
        var done=await ServiceTests.Wait(service,operation.Id);
        Assert.Equal(OperationState.Failed,done.State);
        var execution=service.Catalog.Get<ConfirmationExecution>(done.ResultId!);
        Assert.Equal("failed",execution.State);
        Assert.Equal("not_comparable",(await Assert.ThrowsAsync<LabException>(()=>service.CompareConfirmationAsync(execution.Id))).Code);
        foreach(var launch in service.Catalog.List("normal_build_launch").Rows)
        {
            var row=Wire.Deserialize<JsonElement>(launch.Json); int pid=row.GetProperty("process_id").GetInt32();
            try { using var process=Process.GetProcessById(pid); Assert.True(process.HasExited||ProcessIdentity.StartTimeUtcTicks(process)!=long.Parse(row.GetProperty("start_time_utc_ticks").GetString()!)); }
            catch(ArgumentException) { }
        }
        Assert.Empty(service.Catalog.List("comparison").Rows);
    }
    [Theory,InlineData("../outside.dll"),InlineData("bad.cmd"),Case("UT-SEC-006","FR-071","M6","Unit")]
    public async Task NormalBuild_ArbitraryPathsOrExecutableNames_AreRejected(string entry)
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        Assert.Equal("invalid_build",(await Assert.ThrowsAsync<LabException>(()=>service.RegisterNormalBuildAsync(entry,
            [new(entry,"unknown"),new("a.deps.json","unknown"),new("a.runtimeconfig.json","unknown")]))).Code);
    }
    [Fact,Case("UT-EXP-007","FR-035,FR-055","M6","Integration")]
    public async Task OwnedHarness_RejectsASecondMeasurementAndExitsOnControllerPipeClosure()
    {
        using var workspace=new TestWorkspace();
        var start=new ProcessStartInfo("dotnet") { UseShellExecute=false,RedirectStandardInput=true,
            RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true };
        start.ArgumentList.Add(Path.Combine(TestWorkspace.TaskRoot,"bin","Tedd.DotNetLab.NormalBaseline","Release","net10.0","Tedd.DotNetLab.NormalBaseline.dll"));
        start.ArgumentList.Add("--harness"); start.ArgumentList.Add(workspace.Root);
        using var process=Process.Start(TestWorkspace.PinRuntime(start))!;
        try
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var target=Wire.Deserialize<TargetIdentity>((await process.StandardOutput.ReadLineAsync(deadline.Token))!);
            var client=new Tedd.DotNetLab.Harness.HarnessClient();
            Assert.True((await client.HandshakeAsync(target,deadline.Token)).Runtime!.OwnedProcess);
            var grant=new Grant(Wire.Id("grant"),workspace.Root,target.Id,target.StartTimeUtcTicks,Permission.ExecuteRegisteredScenario,DateTimeOffset.UtcNow.AddSeconds(10),3);
            var request=new Tedd.DotNetLab.Contracts.RunRequest("exec","run","block","normal-sum","baseline",64,1,true,true,0,target,grant,DateTimeOffset.UtcNow.AddSeconds(5));
            Assert.True((await client.ExecuteAsync(request,deadline.Token)).Correct);
            Assert.Equal("process_budget_exhausted",(await Assert.ThrowsAsync<LabException>(()=>client.ExecuteAsync(request with { RunId="extra-sample" },deadline.Token))).Code);
            process.StandardInput.Close(); // EOF also occurs when the controller exits abruptly.
            await process.WaitForExitAsync(deadline.Token); Assert.Equal(0,process.ExitCode);
        }
        finally { if(!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
    [Fact,Case("UT-ART-005","FR-072,FR-055","M6","Integration")]
    public async Task NormalBuild_ModifiedSealedCode_IsRejectedBeforeProcessLaunch()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var plan=await Plan(service); var grant=service.IssueConfirmationGrant(plan.Id,120,20);
        var build=service.Catalog.Get<NormalBuild>(plan.BaselineBuildId);
        var artifact=service.Catalog.Get<Artifact>(build.Files.Single(f=>f.Name==build.EntryAssembly).ArtifactId);
        string path=await service.Artifacts.VerifiedPathAsync(artifact);
        await File.WriteAllBytesAsync(path,[0,1,2]);
        var operation=service.StartConfirmation(plan.Id,grant.Id,"corrupted-build");
        var done=await ServiceTests.Wait(service,operation.Id);
        Assert.Equal(OperationState.Failed,done.State); Assert.Equal("artifact_corrupt",done.ErrorCode);
        Assert.Empty(service.Catalog.List("normal_build_launch").Rows);
    }
    [Theory,InlineData(false),InlineData(true),Case("UT-STAT-027","FR-036,FR-055","M6","Integration")]
    public async Task Confirmation_UnknownGuardrail_BlocksPromotionUnlessDescriptiveBeforeSampling(bool descriptive)
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var policy=ObjectiveTests.Gc(descriptive);
        var plan=await Plan(service,guardrails:[policy]);
        var grant=service.IssueConfirmationGrant(plan.Id,120,20);
        var operation=service.StartConfirmation(plan.Id,grant.Id,"guarded-holdout");
        var completed=await ServiceTests.Wait(service,operation.Id);
        Assert.True(completed.State==OperationState.Completed,Wire.Serialize(completed));
        var decision=await service.CompareConfirmationAsync(completed.ResultId!);
        var vector=await service.ReadObjectivesAsync(decision.ObjectiveVectorId!);
        Assert.Equal(EffectClass.Improved,vector.Effects[0].EffectClass);
        var guardrail=vector.Effects[1];
        Assert.Equal(Availability.NotCollected,guardrail.Availability); Assert.Null(guardrail.Effect);
        Assert.Equal(descriptive,decision.PromotionEligible);
        Assert.Equal(descriptive,service.Catalog.Get<Comparison>(decision.ComparisonId).PromotionEligible);
        Assert.Equal(descriptive?1:2,guardrail.FamilySize);
        Assert.Equal(ParetoRelation.Unresolved,vector.Pareto);
        var index=service.Catalog.Get<ObjectiveVectorIndex>(vector.Id);
        Assert.Equal(20,index.InputRunCount); Assert.All(index.EffectsWithoutRunIds,effect=>Assert.Empty(effect.RunIds));
        Assert.True(service.Catalog.IsPinned(index.DetailArtifactId)); Assert.All(vector.Effects,effect=>Assert.Equal(20,effect.RunIds.Count));
        Assert.Throws<LabException>(()=>service.Catalog.Put(plan.Id,"confirmation_plan",plan with { Experiment=plan.Experiment with {
            GuardrailPolicies=[policy with { DescriptiveOnly=!descriptive }] } },1));
    }
    [Fact,Case("UT-EXP-001","FR-031,FR-055","M6","Integration")]
    public async Task NormalBuildBaselineAlias_FreezeBindsOriginalSelectionAndCollectsNewSessions()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var initial=await Plan(service); var firstGrant=service.IssueConfirmationGrant(initial.Id,120,20);
        var first=await ServiceTests.Wait(service,service.StartConfirmation(initial.Id,firstGrant.Id,"baseline-source").Id);
        Assert.True(first.State==OperationState.Completed,Wire.Serialize(first));
        var execution=service.Catalog.Get<ConfirmationExecution>(first.ResultId!);
        var controls=execution.SessionIds.Where(id=>service.Catalog.Get<NormalBuildSession>(id).Variant==initial.BaselineVariant).ToArray();
        var baseline=await service.CreateBaselineAsync(controls,"normal_build_control");
        var alias=service.SetBaselineAlias("normal-control",baseline.Id);
        var frozen=service.FreezeConfirmation(initial,baselineAliasId:alias.Id);
        var changed=await service.CreateBaselineAsync(controls,"reviewed_normal_control");
        service.SetBaselineAlias("normal-control",changed.Id,alias.Revision);
        var grant=service.IssueConfirmationGrant(frozen.Id,120,20);
        var completed=await ServiceTests.Wait(service,service.StartConfirmation(frozen.Id,grant.Id,"frozen-normal-control").Id);
        Assert.True(completed.State==OperationState.Completed,Wire.Serialize(completed));
        var retained=service.Catalog.Get<ConfirmationExecution>(completed.ResultId!);
        Assert.Equal(baseline.Id,service.Catalog.Get<ConfirmationPlan>(frozen.Id).Experiment.Baseline!.Id);
        Assert.Empty(retained.SessionIds.Intersect(execution.SessionIds)); Assert.Equal(20,retained.SessionIds.Count);
        var decision=await service.CompareConfirmationAsync(retained.Id); Assert.True(decision.PromotionEligible);
    }
}
