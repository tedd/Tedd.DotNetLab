using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Tedd.DotNetLab.Analysis.Statistics;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Harness;

namespace Tedd.DotNetLab.Application;

public sealed partial class LabService
{
    public ConfirmationPlan FreezeConfirmation(ConfirmationPlan request,string? baselineId=null,string? baselineAliasId=null)
    {
        if(request.Experiment.CompilationControl is not null)
            throw new LabException("invalid_plan","A target-scoped live compilation control cannot authorize fresh normal-build confirmation.");
        request=request with { Experiment=FreezeDiagnosticWindows(request.Experiment) };
        Policies.Validate(request.Experiment);
        var experiment=FreezeBaseline(request.Experiment,baselineId,baselineAliasId);
        Policies.Validate(experiment);
        if(!experiment.FreshNormalBuild||!experiment.Confirmatory||!experiment.IndependentBlocks||experiment.Profile!="quiet-validation"||
            experiment.Variants.Count!=2||experiment.Variants[0]!=request.BaselineVariant||experiment.Variants[1]!=request.CandidateVariant||
            experiment.Blocks<experiment.Analysis.MinimumBlocks||experiment.Blocks<Metrics.Primary(experiment).MinimumBlocks||experiment.Blocks>100||experiment.WarmupBatches<1)
            throw new LabException("invalid_plan","Confirmation requires two normal-build arms, independent quiet blocks, warmup, and a complete predeclared primary-metric budget.");
        if(string.IsNullOrWhiteSpace(request.SourceEquivalenceRationale)||request.SourceEquivalenceRationale.Length>8192||
            request.ExploratoryEvidenceIds.Count is <1 or >128||request.ExploratoryEvidenceIds.Distinct().Count()!=request.ExploratoryEvidenceIds.Count)
            throw new LabException("invalid_plan","Selection requires retained exploratory evidence and an explicit source-equivalence rationale.");
        if(Catalog.Get(request.SelectedCandidateId).Kind is not ("candidate" or "cil_candidate"))
            throw new LabException("invalid_plan","Selection must identify a registered candidate.");
        var source=Record<Artifact>(request.SourcePatchArtifactId,"artifact");
        if(!source.Complete||source.MediaType is not ("application/json" or "text/x-diff"))
            throw new LabException("invalid_plan","Source change requires a retained complete patch or source manifest.");
        var baseline=Record<NormalBuild>(request.BaselineBuildId,"normal_build");
        var candidate=Record<NormalBuild>(request.CandidateBuildId,"normal_build");
        if(experiment.TargetRuntimeVersion is { } version&&(baseline.RuntimeVersion!=version||candidate.RuntimeVersion!=version))
            throw new LabException("not_comparable","Both normal builds must match target_runtime_version in the frozen plan.");
        if(baseline.Id==candidate.Id||baseline.ContentHash==candidate.ContentHash)
            throw new LabException("invalid_plan","Confirmation requires distinct normal builds.");
        string EntryHash(NormalBuild b)=>Record<Artifact>(b.Files.Single(f=>f.Name==b.EntryAssembly).ArtifactId,"artifact").Hash;
        if(EntryHash(baseline)==EntryHash(candidate)) throw new LabException("invalid_plan","Entry code is identical in both registered builds.");
        Dictionary<string,string> Dependencies(NormalBuild b)=>b.Files.Where(f=>f.Name.EndsWith(".dll",StringComparison.Ordinal)&&f.Name!=b.EntryAssembly)
            .ToDictionary(f=>f.Name,f=>Record<Artifact>(f.ArtifactId,"artifact").Hash,StringComparer.Ordinal);
        if(Wire.Hash(Dependencies(baseline))!=Wire.Hash(Dependencies(candidate)))
            throw new LabException("invalid_plan","Runtime and harness dependency code must match across both arms.");
        string ConfigHash(NormalBuild b)=>Record<Artifact>(b.Files.Single(f=>f.Name==Path.GetFileNameWithoutExtension(b.EntryAssembly)+".runtimeconfig.json").ArtifactId,"artifact").Hash;
        if(ConfigHash(baseline)!=ConfigHash(candidate)) throw new LabException("invalid_plan","Runtime settings differ across build arms.");
        foreach(string evidence in request.ExploratoryEvidenceIds)
            if(Catalog.Get(evidence).Kind is not ("comparison" or "execution" or "mutation" or "hypothesis"))
                throw new LabException("invalid_plan","Selection evidence must precede this confirmation and identify retained exploratory records.");
        string id=Wire.Id("confirm-plan");
        var references=request.ExploratoryEvidenceIds.Select(evidence=>Catalog.Get(evidence)).Select(record=>new FrozenRecordReference(record.Id,record.Revision,record.Hash)).ToArray();
        var plan=request with { Id=id,Experiment=experiment with { Id=id },FrozenAt=DateTimeOffset.UtcNow,FrozenEvidence=references };
        Catalog.Put(plan.Id,"confirmation_plan",plan); Catalog.Pin(source.Id,"confirmation_source");
        return plan;
    }

    private TargetIdentity ConfirmationTarget(ConfirmationPlan plan)=>new(plan.Id,0,0,plan.Id,Record<NormalBuild>(plan.BaselineBuildId,"normal_build").RuntimeVersion,"X64");
    // Human CLI only: authorizes startup of the exact retained application bundles, including their startup effects.
    public Grant IssueConfirmationGrant(string planId,int seconds,int maximumOperations)
    {
        var plan=Record<ConfirmationPlan>(planId,"confirmation_plan");
        if(seconds is <1 or >3600||maximumOperations<plan.Experiment.Blocks*2||maximumOperations>100000)
            throw new LabException("invalid_request","Launch grant must cover the frozen process budget and a bounded lifetime.");
        var grant=new Grant(Wire.Id("grant"),Catalog.Root,plan.Id,0,Permission.LaunchRegisteredBuild,
            DateTimeOffset.UtcNow.AddSeconds(seconds),maximumOperations,plan.ContentHash);
        Catalog.Put(grant.Id,"grant",grant); return grant;
    }
    private Grant AuthorizeConfirmation(ConfirmationPlan plan,string grantId)
    {
        var grant=Record<Grant>(grantId,"grant");
        Policies.Authorize(grant,Catalog.Root,ConfirmationTarget(plan),Permission.LaunchRegisteredBuild,DateTimeOffset.UtcNow,plan.ContentHash);
        return grant;
    }
    public Operation StartConfirmation(string planId,string grantId,string idempotencyKey)
    {
        if(!RuntimeSupport.ControllerSupported)
            throw new LabException("capability_unsupported","Owned normal-build confirmation requires a compatible Windows/Linux x64 controller runtime (.NET 10.0.12 or later).");
        var plan=Record<ConfirmationPlan>(planId,"confirmation_plan");
        if(plan.Experiment.MaximumDurationSeconds>Limits.MaximumDurationSeconds)
            throw new LabException("resource_limit","Confirmation exceeds this workspace's duration policy.");
        var grant=AuthorizeConfirmation(plan,grantId);
        if(string.IsNullOrWhiteSpace(idempotencyKey)||idempotencyKey.Length>128) throw new LabException("invalid_request","Invalid idempotency key.");
        string hash=Wire.Hash(new { plan_id=plan.Id,plan_hash=plan.ContentHash,grantId,idempotencyKey });
        // One durable acceptance per frozen plan prevents extra sampling or choosing among repeated holdouts.
        return Start("confirmation",ConfirmationTarget(plan),"confirmation-"+plan.Id,hash,async(operation,cancellation)=>
        {
            var sessions=new List<string>(); string id=operation.Id+"-confirmation";
            var root=new ConfirmationExecution(id,plan.Id,plan.ContentHash,"running",[],DateTimeOffset.UtcNow,OperationId:operation.Id);
            Catalog.Put(id+"-root","confirmation_root",root);
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(plan.Experiment.MaximumDurationSeconds));
            var random=new DeterministicRandom(plan.Experiment.Seed);
            bool joined=true;
            try
            {
                if(plan.Experiment.Baseline is not null) await VerifyBaselineAsync(plan.Experiment.Baseline,deadline.Token);
                for(int block=0;block<plan.Experiment.Blocks;block++)
                {
                    var order=random.Shuffle(new[]{plan.BaselineVariant,plan.CandidateVariant});
                    for(int arm=0;arm<order.Length;arm++)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        Policies.Authorize(grant,Catalog.Root,ConfirmationTarget(plan),Permission.LaunchRegisteredBuild,DateTimeOffset.UtcNow,plan.ContentHash);
                        Catalog.UseGrant(grant);
                        string buildId=order[arm]==plan.BaselineVariant?plan.BaselineBuildId:plan.CandidateBuildId;
                        var session=await RunNormalSessionAsync(plan,Record<NormalBuild>(buildId,"normal_build"),id,block,arm,order[arm],grant,deadline.Token);
                        sessions.Add(session.Id);
                    }
                }
                var complete=root with { State="completed",SessionIds=sessions.ToArray(),EndedAt=DateTimeOffset.UtcNow };
                await SealConfirmationAsync(operation.Id,complete);
                Catalog.Transition(operation.Id,OperationState.Completed,complete.Id);
            }
            catch(Exception e) when(e is not OutOfMemoryException)
            {
                string code=e is LabException error?error.Code:e is OperationCanceledException?"cancelled":"confirmation_failed";
                joined=code!="process_quiescence_unknown";
                var failed=root with { State="failed",SessionIds=sessions.ToArray(),EndedAt=DateTimeOffset.UtcNow,Failure=code };
                await SealConfirmationAsync(operation.Id,failed);
                Catalog.Transition(operation.Id,joined?OperationState.Failed:OperationState.Tainted,failed.Id,code);
            }
            return joined;
        });
    }
    private async Task SealConfirmationAsync(string operationId,ConfirmationExecution execution)
    {
        Catalog.Put(execution.Id,"confirmation_execution",execution);
        var artifact=await Artifacts.WriteJsonAsync(operationId,execution,"normal-build-confirmation/1");
        Upsert(artifact.Id,"artifact",artifact); Catalog.Pin(artifact.Id,"confirmation_manifest");
        Catalog.Put(execution.Id+"-manifest","confirmation_manifest",new { artifact_id=artifact.Id });
    }
    private static async Task<string> BoundedLineAsync(StreamReader reader,CancellationToken cancellation)
    {
        var line=new System.Text.StringBuilder(); char[] character=new char[1];
        while(await reader.ReadAsync(character.AsMemory(),cancellation)>0)
        {
            if(character[0]=='\n') return line.ToString().TrimEnd('\r');
            if(line.Length>=8192) throw new LabException("resource_limit","Process readiness output exceeds 8 KiB.");
            line.Append(character[0]);
        }
        throw new LabException("process_exited","Owned process exited before harness readiness.");
    }
    private static async Task DrainOutputAsync(StreamReader reader,CancellationToken cancellation)
    {
        char[] buffer=new char[4096]; long total=0; int count;
        while((count=await reader.ReadAsync(buffer.AsMemory(),cancellation))>0)
            if((total+=count)>1048576) throw new LabException("resource_limit","Owned process output exceeds 1 MiB.");
    }
    private async Task<NormalBuildSession> RunNormalSessionAsync(ConfirmationPlan plan,NormalBuild build,string executionId,
        int block,int arm,string variant,Grant parent,CancellationToken cancellation)
    {
        using var measurementPermit=await activityScheduler.EnterMeasurementAsync(cancellation);
        string? ipc=null;
        if(OperatingSystem.IsLinux())
        {
            ipc=Path.GetFullPath(Path.GetTempPath()); VerifyDirectoryAncestors(ipc);
            const UnixFileMode exposed=UnixFileMode.GroupRead|UnixFileMode.GroupWrite|UnixFileMode.GroupExecute|
                UnixFileMode.OtherRead|UnixFileMode.OtherWrite|UnixFileMode.OtherExecute;
            if(!OwnedScratchPath(ipc)||System.Text.Encoding.UTF8.GetByteCount(ipc)>48||(File.GetUnixFileMode(ipc)&exposed)!=0)
                throw new LabException("invalid_path","Linux confirmation requires a private task-owned IPC directory within the Unix socket path budget.");
        }
        string sessionId=Wire.Id("normal-session"); string directory=await StageBuildAsync(build,sessionId,cancellation);
        string stagedConfigPath=Path.Combine(directory,Path.GetFileNameWithoutExtension(build.EntryAssembly)+".runtimeconfig.json");
        string rollForward="Minor";
        using(var stagedConfig=JsonDocument.Parse(await File.ReadAllTextAsync(stagedConfigPath,cancellation),new JsonDocumentOptions { MaxDepth=32 }))
        {
            if(stagedConfig.RootElement.GetProperty("runtimeOptions").TryGetProperty("rollForward",out var rfProp))
                rollForward=rfProp.GetString()??"Minor";
        }
        var start=RuntimeSupport.StartInfo(build.RuntimeVersion,rollForward,out string resolvedRuntimeVersion); start.WorkingDirectory=directory;
        start.RedirectStandardInput=true; start.RedirectStandardOutput=true; start.RedirectStandardError=true;
        start.ArgumentList.Add(Path.Combine(directory,build.EntryAssembly)); start.ArgumentList.Add("--harness"); start.ArgumentList.Add(Catalog.Root);
        start.Environment.Clear();
        foreach(string key in new[]{"SystemRoot","WINDIR","ComSpec"})
            if(Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key]=value;
        string temporary=Path.Combine(directory,"tmp"); Directory.CreateDirectory(temporary);
        start.Environment["TEMP"]=temporary; start.Environment["TMP"]=temporary;
        if(ipc is not null) start.Environment["TMPDIR"]=ipc;
        start.Environment["DOTNET_EnableDiagnostics"]="0"; start.Environment["CORECLR_ENABLE_PROFILING"]="0";
        start.Environment["DOTNET_ENABLE_PROFILING"]="0"; start.Environment["DOTNET_MULTILEVEL_LOOKUP"]="0";
        using var process=new Process { StartInfo=start };
        using var outputCancellation=new CancellationTokenSource();
        Task? stdout=null,stderr=null; Measurement? measurement=null; HarnessHandshake? handshake=null;
        DateTimeOffset began=DateTimeOffset.UtcNow;
        bool started=false;
        try
        {
            if(!process.Start()) throw new LabException("process_start_failed","Owned normal-build process could not start.");
            started=true;
            Catalog.Put(sessionId+"-launch","normal_build_launch",new { session_id=sessionId,execution_id=executionId,
                process_id=process.Id,start_time_utc_ticks=ProcessIdentity.StartTimeUtcTicks(process),build_id=build.Id,started_at=began });
            stderr=DrainOutputAsync(process.StandardError,outputCancellation.Token);
            using var startup=CancellationTokenSource.CreateLinkedTokenSource(cancellation); startup.CancelAfter(TimeSpan.FromSeconds(10));
            var ready=Wire.Deserialize<TargetIdentity>(await BoundedLineAsync(process.StandardOutput,startup.Token));
            long ticks=ProcessIdentity.StartTimeUtcTicks(process);
            string expectedId="target-"+Wire.Hash(new { pid=process.Id,start=ticks })[..24];
            if(ready.ProcessId!=process.Id||ready.StartTimeUtcTicks!=ticks||ready.Id!=expectedId||ready.Epoch!=0||
                ready.RuntimeVersion!=resolvedRuntimeVersion||ready.Architecture!="X64")
                throw new LabException("target_identity_changed","Owned process readiness differs from kernel process identity.");
            stdout=DrainOutputAsync(process.StandardOutput,outputCancellation.Token);
            handshake=await harness.HandshakeAsync(ready,startup.Token);
            VerifyNormalHandshake(build,ready,handshake);
            var scenario=handshake.Scenarios.SingleOrDefault(s=>s.Id==plan.Experiment.ScenarioId)
                ??throw new LabException("scenario_not_found","Normal build does not register the frozen scenario.");
            if(scenario.Version!=plan.Experiment.ScenarioVersion||scenario.InputHash!=plan.Experiment.InputHash||!scenario.Variants.Contains(variant))
                throw new LabException("invalid_plan","Normal-build scenario differs from the frozen input/variant.");
            if(variant==plan.BaselineVariant&&plan.Experiment.Baseline is not null)
            {
                var baseline=FrozenSelection(plan.Experiment.Baseline);
                var fingerprint=Fingerprint(plan.Experiment,scenario,ready,handshake.Runtime,variant,Profiles.Resolve(plan.Experiment.Profile).Hash,
                    "diagnostics_disabled/owned_process","normal-build/fresh-process");
                if(Wire.Hash(fingerprint)!=Wire.Hash(baseline.Fingerprint))
                    throw new LabException("baseline_mismatch","Normal build or runtime differs from the frozen baseline selection.");
            }
            var grant=new Grant(parent.Id+"-"+sessionId,Catalog.Root,ready.Id,ready.StartTimeUtcTicks,
                Permission.ExecuteRegisteredScenario,parent.ExpiresAt,1);
            var request=new Tedd.DotNetLab.Contracts.RunRequest(executionId,sessionId,executionId+"-block-"+block,
                scenario.Id,variant,plan.Experiment.OperationsPerRun,plan.Experiment.WarmupBatches,true,!scenario.IsAsync,
                0,ready,grant,DateTimeOffset.UtcNow.AddSeconds(plan.Experiment.MaximumDurationSeconds)<parent.ExpiresAt?
                    DateTimeOffset.UtcNow.AddSeconds(plan.Experiment.MaximumDurationSeconds):parent.ExpiresAt);
            measurement=await ExecuteMeasuredAsync(request,cancellation,measurementPermit);
            Policies.Validate(measurement);
            if(!measurement.Correct||!measurement.Quiescent||measurement.CompletedOperations!=request.OperationCount||
                measurement.RunId!=sessionId||measurement.ExecutionId!=executionId||measurement.BlockId!=request.BlockId||
                measurement.Variant!=variant||measurement.InputHash!=scenario.InputHash||measurement.ScenarioVersion!=scenario.Version||
                measurement.Frequency!=handshake.Runtime!.StopwatchFrequency)
                throw new LabException("invalid_measurement","Normal-build measurement failed its frozen identity, correctness, or quiescence gate.");
            VerifyNormalHandshake(build,ready,await harness.HandshakeAsync(ready,cancellation));
            await process.StandardInput.WriteLineAsync("shutdown"); process.StandardInput.Close();
            await process.WaitForExitAsync(cancellation).WaitAsync(TimeSpan.FromSeconds(5),cancellation);
            await Task.WhenAll(stdout,stderr).WaitAsync(TimeSpan.FromSeconds(5),cancellation);
            if(process.ExitCode!=0) throw new LabException("process_exited","Owned process failed during orderly shutdown.");
            var artifact=await Artifacts.WriteJsonAsync(sessionId,new { build,plan_hash=plan.ContentHash,target=ready,runtime=handshake.Runtime,request,measurement,
                started_at=began,exited_at=DateTimeOffset.UtcNow,exit_code=process.ExitCode },"normal-build-session/1");
            Upsert(artifact.Id,"artifact",artifact); Catalog.Pin(artifact.Id,"confirmation_input");
            var session=new NormalBuildSession(sessionId,executionId,plan.Id,block,arm,build.Id,variant,ready,handshake.Runtime!,
                measurement,artifact.Id,began,DateTimeOffset.UtcNow,process.ExitCode);
            Catalog.Put(session.Id,"normal_build_session",session); return session;
        }
        finally
        {
            try { if(started&&!process.HasExited) { process.Kill(true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); } }
            catch(Exception error) when(error is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
            {
                if(started&&!process.HasExited) throw new LabException("process_quiescence_unknown","Owned process termination could not be verified.");
            }
            outputCancellation.Cancel();
            foreach(var task in new[]{stdout,stderr})
                if(task is not null) { try { await task; } catch(Exception e) when(e is OperationCanceledException or IOException or LabException) { } }
            await RemoveOwnedLaunchDirectoryAsync(directory);
        }
    }
    private void VerifyNormalHandshake(NormalBuild build,TargetIdentity expected,HarnessHandshake handshake)
    {
        if(handshake.Target!=expected||handshake.Tainted||handshake.ProbeHelper?.Active==true||handshake.Runtime is not { } runtime||
            !runtime.OwnedProcess||runtime.DebuggerAttached||runtime.NativeProfilerPresent||runtime.DiagnosticsEnabled!="0"||runtime.ProfilingEnabled!="0"||
            runtime.StartupHooks!=""||runtime.AdditionalDependencies!=""||runtime.Architecture!="X64"||runtime.StopwatchFrequency<=0||
            runtime.OperatingSystemFamily!=(OperatingSystem.IsWindows()?"windows":"linux"))
            throw new LabException("observer_mismatch","Confirmation requires a verified quiet normal runtime with diagnostics, debugger, and native profiler absent.");
        var hashes=build.Files.Where(f=>f.Name.EndsWith(".dll",StringComparison.Ordinal))
            .ToDictionary(f=>f.Name,f=>Record<Artifact>(f.ArtifactId,"artifact").Hash,StringComparer.OrdinalIgnoreCase);
        if(runtime.EntryAssemblyHash!=hashes[build.EntryAssembly]||Wire.Hash(runtime.AssemblyHashes)!=Wire.Hash(hashes))
            throw new LabException("build_identity_changed","Executing managed files differ from the sealed build.");
    }

    public async Task<ConfirmationDecision> CompareConfirmationAsync(string executionId)
    {
        var execution=Record<ConfirmationExecution>(executionId,"confirmation_execution");
        var plan=Record<ConfirmationPlan>(execution.PlanId,"confirmation_plan");
        var root=Record<ConfirmationExecution>(executionId+"-root","confirmation_root");
        if(plan.FrozenEvidence is null||plan.FrozenEvidence.Count!=plan.ExploratoryEvidenceIds.Count||
            plan.FrozenEvidence.Any(reference=>!plan.ExploratoryEvidenceIds.Contains(reference.Id)||Catalog.Get(reference.Id,reference.Revision).Hash!=reference.Hash))
            throw new LabException("not_comparable","Frozen exploratory provenance is incomplete or differs from its retained revision.");
        var operation=Catalog.GetOperation(execution.OperationId??"");
        if(execution.State!="completed"||execution.PlanHash!=plan.ContentHash||root.PlanHash!=plan.ContentHash||root.StartedAt!=execution.StartedAt||
            operation.State!=OperationState.Completed||operation.ResultId!=executionId||execution.StartedAt<plan.FrozenAt||
            execution.SessionIds.Count!=plan.Experiment.Blocks*2||execution.SessionIds.Distinct().Count()!=execution.SessionIds.Count)
            throw new LabException("not_comparable","Confirmation must complete its original frozen budget after selection.");
        var manifest=Record<JsonElement>(executionId+"-manifest","confirmation_manifest");
        string manifestPath=await Artifacts.VerifiedPathAsync(Record<Artifact>(manifest.GetProperty("artifact_id").GetString()!,"artifact"));
        if(Wire.Hash(Wire.Deserialize<ConfirmationExecution>(await File.ReadAllTextAsync(manifestPath)))!=Wire.Hash(execution))
            throw new LabException("not_comparable","Retained execution differs from its sealed completion manifest.");
        var sessions=execution.SessionIds.Select(id=>Record<NormalBuildSession>(id,"normal_build_session")).ToArray();
        if(sessions.Select(s=>(s.Target.ProcessId,s.Target.StartTimeUtcTicks)).Distinct().Count()!=sessions.Length)
            throw new LabException("not_comparable","Every arm must originate from a distinct process session.");
        var blockers=new List<string>(); var pairs=new List<CostPair>(); var measurements=new List<MeasurementPair>();
        string Fingerprint(NormalBuildSession s)=>Wire.Hash(new { s.Target.RuntimeVersion,s.Runtime.OperatingSystem,s.Runtime.Architecture,
            s.Runtime.StopwatchFrequency,s.Runtime.DiagnosticsEnabled,s.Runtime.ProfilingEnabled,s.Runtime.StartupHooks,s.Runtime.AdditionalDependencies });
        if(sessions.Select(Fingerprint).Distinct().Count()!=1) blockers.Add("runtime_fingerprint_mismatch");
        foreach(var session in sessions)
        {
            var build=Record<NormalBuild>(session.BuildId,"normal_build");
            VerifyNormalHandshake(build,session.Target,new(session.Target,[],false,null,session.Runtime));
            var artifact=Record<Artifact>(session.MeasurementArtifactId,"artifact");
            string path=await Artifacts.VerifiedPathAsync(artifact);
            using var evidence=JsonDocument.Parse(await File.ReadAllTextAsync(path));
            if(evidence.RootElement.GetProperty("plan_hash").GetString()!=plan.ContentHash||
                Wire.Hash(evidence.RootElement.GetProperty("measurement").Deserialize<Measurement>(Wire.Json))!=Wire.Hash(session.Measurement)||
                session.ExecutionId!=executionId||session.PlanId!=plan.Id||session.StartedAt<plan.FrozenAt||session.ExitedAt<session.StartedAt||session.ExitCode!=0||
                !session.Measurement.Correct||!session.Measurement.Quiescent||session.Measurement.CompletedOperations!=plan.Experiment.OperationsPerRun||
                session.Measurement.InputHash!=plan.Experiment.InputHash||session.Measurement.ScenarioVersion!=plan.Experiment.ScenarioVersion||
                session.CompilationRegime!="normal-build/fresh-process")
                blockers.Add("confirmation_provenance_or_correctness_mismatch");
        }
        for(int block=0;block<plan.Experiment.Blocks;block++)
        {
            var rows=sessions.Where(s=>s.BlockOrdinal==block).ToArray();
            if(rows.Length!=2||rows.Select(s=>s.ArmOrdinal).Order().SequenceEqual(new[]{0,1})==false)
                throw new LabException("not_comparable","Matched block does not contain two complete distinct arms.");
            var baseline=rows.SingleOrDefault(s=>s.Variant==plan.BaselineVariant&&s.BuildId==plan.BaselineBuildId);
            var candidate=rows.SingleOrDefault(s=>s.Variant==plan.CandidateVariant&&s.BuildId==plan.CandidateBuildId);
            if(baseline is null||candidate is null) throw new LabException("not_comparable","Matched block differs from the registered build treatment.");
            string blockId=executionId+"-block-"+block;
            bool legacyElapsed=plan.Experiment.PrimaryPolicy is null&&plan.Experiment.PrimaryMetric is "scenario.ns_per_operation" or "scenario.elapsed_ns";
            if(legacyElapsed)
            {
                double Value(NormalBuildSession s)=>plan.Experiment.PrimaryMetric=="scenario.elapsed_ns"?s.Measurement.ElapsedNanoseconds:s.Measurement.NanosecondsPerOperation;
                pairs.Add(new(blockId,blockId,Value(baseline),Value(candidate),baseline.Id,candidate.Id));
            }
            measurements.Add(new(blockId,blockId,baseline.Measurement,candidate.Measurement,baseline.Id,candidate.Id));
        }
        await Artifacts.VerifiedPathAsync(Record<Artifact>(plan.SourcePatchArtifactId,"artifact"));
        string decisionId="confirm-decision-"+Wire.Hash(new { execution,plan_hash=plan.ContentHash,inputs=sessions.Select(Wire.Hash),algorithm=StatisticalEngine.Version,objectives=ObjectiveEngine.Version });
        try { return Record<ConfirmationDecision>(decisionId,"confirmation_decision"); }
        catch(LabException e) when(e.Code=="record_not_found") { }
        var vector=EvaluateObjectives(executionId,plan.Experiment,plan.ContentHash,plan.BaselineVariant,plan.CandidateVariant,measurements,true,blockers);
        var vectorIndex=await StoreObjectivesAsync(vector);
        bool useLegacyPrimary=plan.Experiment.PrimaryPolicy is null&&plan.Experiment.PrimaryMetric is "scenario.ns_per_operation" or "scenario.elapsed_ns";
        var primaryComparison=useLegacyPrimary?StatisticalEngine.Paired(pairs,plan.Experiment.Analysis,true,true,blockers):
            PrimaryComparison(vector,"cmp-"+decisionId["confirm-decision-".Length..]);
        var comparison=primaryComparison
            with { Id="cmp-"+decisionId["confirm-decision-".Length..],PromotionEligible=vector.PromotionEligible,
                ReasonCodes=vector.ReasonCodes,ObjectiveVectorId=vector.Id };
        var decision=new ConfirmationDecision(decisionId,plan.Id,executionId,plan.SelectedCandidateId,plan.BaselineBuildId,plan.CandidateBuildId,
            plan.SourcePatchArtifactId,comparison.Id,comparison.PromotionEligible,comparison.ReasonCodes,ObjectiveVectorId:vector.Id);
        var artifactDecision=await Artifacts.WriteJsonAsync(decisionId,new { plan,execution,comparison,objective_vector=vectorIndex,decision },"confirmation-decision/2");
        lock(gate)
        {
            try { return Record<ConfirmationDecision>(decisionId,"confirmation_decision"); }
            catch(LabException e) when(e.Code=="record_not_found") { }
            Upsert(comparison.Id,"comparison",comparison);
            Upsert(artifactDecision.Id,"artifact",artifactDecision); Catalog.Pin(artifactDecision.Id,"confirmation_decision");
            Upsert(decision.Id+"-manifest","confirmation_manifest",new { artifact_id=artifactDecision.Id });
            Catalog.Put(decision.Id,"confirmation_decision",decision);
            return decision;
        }
    }
}
