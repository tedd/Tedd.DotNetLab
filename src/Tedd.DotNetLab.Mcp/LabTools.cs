using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using ModelContextProtocol.Server;

namespace Tedd.DotNetLab.Mcp;

public record LabResponse(string SchemaVersion,string Status,JsonElement? Data,string? Code,string? Message,string? Recovery);

public sealed class LabTools(LabService service)
{
    private static long Integer(string input) => long.TryParse(input,NumberStyles.None,CultureInfo.InvariantCulture,out long result)
        ?result:throw new LabException("invalid_request","Expected a nonnegative decimal Int64 string.");
    private static long? Revision(string? input)=>input is null?null:Integer(input);
    private async Task<LabResponse> Respond(Func<Task<object>> action)
    {
        try
        {
            object value=await action();
            var result=new LabResponse(Wire.Version,"ok",JsonSerializer.SerializeToElement(value,Wire.Json),null,null,null);
            byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(result,Wire.Json);
            if(bytes.Length>service.Limits.InlineBytes)
                result=result with { Data=await ArtifactReference(bytes,"lab_response") };
            if(Encoding.UTF8.GetByteCount(Wire.Serialize(result))>service.Limits.InlineBytes)
                throw new LabException("resource_limit","Response artifact reference exceeds the inline budget.");
            return result;
        }
        catch(LabException e) { return Error(e.Code,e.Message,e.Recovery); }
        catch(Exception e) when(e is JsonException or FormatException or OverflowException or ArgumentException)
        { return Error("invalid_request","Malformed request."); }
    }
    private LabResponse Error(string code,string message,string? recovery=null)
    {
        string hint=recovery??RecoveryHints.ForCode(code);
        string text=message.Length<=4096?message:message[..4096];
        hint=hint.Length<=RecoveryHints.MaximumLength?hint:hint[..RecoveryHints.MaximumLength];
        var response=new LabResponse(Wire.Version,"error",null,code.Length<=128?code:"invalid_error",text,hint);
        while(JsonSerializer.SerializeToUtf8Bytes(response,Wire.Json).Length>service.Limits.InlineBytes)
        {
            if(text.Length>0) text=text.Length>1?text[..(text.Length/2)]:"";
            else hint=hint.Length>1?hint[..(hint.Length/2)]:"";
            response=response with { Message=text,Recovery=hint };
        }
        return response;
    }
    private Task<LabResponse> Respond(Func<object> action)=>Respond(()=>Task.FromResult(action()));
    private async Task<JsonElement> ArtifactReference(byte[] bytes,string resultKind)
    {
        var artifact=await service.RetainBoundedResultAsync(bytes);
        return JsonSerializer.SerializeToElement(new
        {
            representation="artifact",result_kind=resultKind,truncated=true,artifact_id=artifact.Id,
            artifact_hash=artifact.Hash,artifact_length=artifact.Length,content_type=artifact.MediaType,
            resource_uri=$"dotnetlab://artifacts/{artifact.Id}/0/{Math.Max(1,(service.Limits.InlineBytes-512)/4)}",
            read=new { tool="lab.query",request=new { view="artifact_slice",artifact_id=artifact.Id,offset="0",count=Math.Max(1,(service.Limits.InlineBytes-512)/4) } }
        },Wire.Json);
    }

        [Description("Discover local targets, qualified capabilities, capture profiles, or registered scenarios. Discovery does not grant execution permission.")]
    public Task<LabResponse> Discover(DiscoverRequest request)=>Respond(async()=>request switch
    {
        DiscoverTargets targets=>TargetPage(targets),
        DiscoverProfiles=>Profiles.Names.Select(name=>new { name,capability=name=="targeted-probes"?"requires_verified_probe_plan":"managed",
            manifest=Profiles.Resolve(name) }).ToArray(),
        DiscoverScenarios scenarios=>await service.ScenariosAsync(scenarios.TargetId,CancellationToken.None),
        DiscoverCapabilities capabilities=>service.Target(capabilities.TargetId),
        DiscoverProfiler profiler=>await service.ProfilerAsync(profiler.TargetId,CancellationToken.None),
        DiscoverWorkspace=>service.Workspace,
        DiscoverCompatibility compatibility=>service.Compatibility(compatibility.TargetId),
        _=>throw new LabException("invalid_request","Invalid discovery action.")
    });

        [Description("Inspect retained records or artifact manifests. Live native inspection requires independent runtime certification.")]
    public Task<LabResponse> Inspect(InspectRequest request)=>Respond(async()=>request switch
    {
        InspectRecord record=>service.Catalog.Get(record.Id,Revision(record.Revision)),
        InspectArtifact artifact=>service.Catalog.Get<Artifact>(artifact.ArtifactId),
        InspectMethods methods=>await service.MethodsAsync(methods.ArtifactId,methods.Filter,methods.Limit,methods.Cursor),
        InspectIl il=>await service.StoredIlAsync(il.ArtifactId,il.MethodToken),
        InspectOfflineNative native=>await service.DecodeAsync(native.ArtifactId),
        InspectNativeDiff diff=>await service.DiffAsync(diff.OriginalArtifactId,diff.CandidateArtifactId,diff.Limit),
        InspectNative native=>await service.InspectLiveAsync(native.TargetId,native.MethodId,Integer(native.ExpectedTargetEpoch),
            native.GrantId,native.ModuleLoadId,CancellationToken.None),
        _=>throw new LabException("invalid_request","Invalid inspection action.")
    });

        [Description("Create or revise hypotheses and work items; register immutable candidate metadata and plans; record manual decisions separately from engine evidence.")]
    public Task<LabResponse> Experiment(ExperimentRequest request)=>Respond(async()=>request switch
    {
        PrepareProfilerAttachment attach=>service.PrepareProfilerAttachment(attach.TargetId,Integer(attach.ExpectedTargetEpoch)),
        CreateHypothesis hypothesis=>service.Hypothesis(hypothesis.Mechanism,hypothesis.Prediction,hypothesis.EvidenceIds),
        ReviseHypothesis hypothesis=>service.Hypothesis(hypothesis.Mechanism,hypothesis.Prediction,hypothesis.EvidenceIds,
            hypothesis.Id,Integer(hypothesis.ExpectedRevision),hypothesis.State),
        CreateCandidate candidate=>service.Candidate(candidate.Name,candidate.BuildIdentity,candidate.Components),
        CreatePlan plan=>service.Plan(new("",plan.ScenarioId,plan.ScenarioVersion,plan.InputHash,plan.Variants,
            plan.Blocks,plan.OperationsPerRun,plan.WarmupBatches,ulong.Parse(plan.Seed,CultureInfo.InvariantCulture),plan.Profile,
            plan.IndependentBlocks,plan.IndependenceJustification,plan.Confirmatory,plan.MaximumDurationSeconds,
            plan.Analysis??new(),plan.PrimaryMetric,GuardrailMetrics:plan.GuardrailMetrics,GuardrailPolicies:plan.GuardrailPolicies,Factorial:plan.Factorial,PrimaryPolicy:plan.PrimaryPolicy,DiagnosticWindows:plan.DiagnosticWindows,TargetRuntimeVersion:plan.TargetRuntimeVersion),plan.BaselineId,plan.BaselineAliasId,plan.CompilationControlId),
        CreateBaseline baseline=>await service.CreateBaselineAsync(baseline.RunIds,baseline.Role),
        SetBaselineAlias alias=>service.SetBaselineAlias(alias.Name,alias.BaselineId,Revision(alias.ExpectedRevision)),
        PrepareExport export=>await service.PrepareExportAsync(export.RecordIds,export.ArtifactIds,export.PrivacyFiltered,Integer(export.MaximumBundleBytes),export.MaximumDurationSeconds),
        PrepareBundleImport import=>await service.PrepareBundleImportAsync(import.ArtifactId,Integer(import.MaximumDecompressedBytes),import.MaximumDurationSeconds),
        PrepareDeletion deletion=>service.PrepareDeletion(deletion.ArtifactIds,deletion.Reason),
        PrepareRetention retention=>service.PrepareRetention(new(Integer(retention.MaximumRawTraceBytes),retention.MaximumAgeDays)),
        PinEvidence pin=>service.PinEvidence(pin.ArtifactId,pin.OwnerId),
        PrepareReferenceRelease release=>service.PrepareReferenceRelease(release.Selection,release.Reason),
        CreateQuantilePlan quantile=>service.CreateQuantilePlan(quantile.Design),
        RegisterQuantileObservations observations=>await service.RegisterQuantileObservationsAsync(observations.PlanId,observations.ArtifactId),
        RegisterQuantileHarnessRuns runs=>await service.RegisterQuantileHarnessRunsAsync(runs.PlanId,runs.Blocks),
        CreateCompilationControl control=>await service.CreateCompilationControlAsync(control.CandidateId,control.ScenarioId,control.HarnessVariant,control.Blocks,control.OperationsPerRun,control.WarmupBatches,control.MaximumDurationSeconds),
        CreateNativeTrial trial=>await service.CreateNativeTrialAsync(trial.CandidateId,trial.CompilationControlId,trial.Blocks,ulong.Parse(trial.Seed,CultureInfo.InvariantCulture),trial.MaximumDurationSeconds),
        CreateNativeSeries series=>await service.CreateNativeSeriesAsync(series.TrialPlanIds,series.Analysis,series.IndependentSessions,series.IndependenceJustification,series.CarryoverJustification,series.MaximumDurationSeconds),
        CreateIndividualLatencyPlan latency=>service.CreateIndividualLatencyPlan(latency.ScenarioId,latency.ScenarioVersion,latency.InputHash,latency.Variant,latency.Operations,latency.WarmupBatches,latency.MaximumDurationSeconds,latency.CompilationControlId,latency.TargetRuntimeVersion),
        CreateWorkItem work=>service.WorkItem(work.Question,work.Priority,work.Dependencies,work.EvidenceIds),
        ReviseWorkItem work=>service.WorkItem(work.Question,work.Priority,work.Dependencies,work.EvidenceIds,
            work.Id,Integer(work.ExpectedRevision),work.State),
        RecordDecision decision=>service.Decision(decision.ComparisonId,decision.Decision,decision.Rationale),
        RegisterCilCandidate candidate=>await service.RegisterCilAsync(candidate.BodyArtifactId,candidate.InspectionId,candidate.BaseIlHash,candidate.Components),
        RegisterCompiledCilCandidate candidate=>await service.RegisterCompiledCilAsync(candidate.CompiledArtifactId,candidate.MethodToken,candidate.InspectionId,candidate.BaseIlHash,candidate.Components),
        RegisterNormalBuild build=>await service.RegisterNormalBuildAsync(build.EntryAssembly,build.Files),
        CreateConfirmationPlan plan=>service.FreezeConfirmation(new("",plan.SelectedCandidateId,plan.SourcePatchArtifactId,plan.SourceEquivalenceRationale,
            plan.BaselineBuildId,plan.CandidateBuildId,plan.BaselineVariant,plan.CandidateVariant,plan.Experiment,plan.ExploratoryEvidenceIds,default),plan.BaselineId,plan.BaselineAliasId),
        _=>throw new LabException("invalid_request","Invalid experiment action.")
    });
    public Task<LabResponse> Patch(PatchRequest request)=>Respond(async()=>request switch
    {
        PreflightCil preflight=>await service.PreflightCilAsync(preflight.TargetId,Integer(preflight.ExpectedTargetEpoch),preflight.CandidateId,preflight.GrantId),
        ApplyCil apply=>await service.ApplyCilAsync(apply.TargetId,Integer(apply.ExpectedTargetEpoch),apply.CandidateId,apply.GrantId,
            apply.ExecutionGrantId,apply.ScenarioId,apply.IdempotencyKey,apply.SetupOperations,apply.MaximumDurationSeconds),
        RestoreCil restore=>await service.RestoreCilAsync(restore.TransactionId,Integer(restore.ExpectedTargetEpoch),restore.GrantId,restore.ExecutionGrantId,restore.IdempotencyKey),
        RecoverMutation recovery=>await service.RecoverMutationAsync(recovery.TransactionId,Integer(recovery.ExpectedTargetEpoch),recovery.GrantId,recovery.ExecutionGrantId,recovery.IdempotencyKey),
        PrepareProbe probe=>await service.RegisterProbeAsync(probe.InspectionId,probe.BaseIlHash,probe.Timing,probe.Threads,probe.MaximumNesting,probe.TrackExceptions),
        FlushProbe probe=>await service.FlushProbesAsync(probe.TargetId,Integer(probe.ExpectedTargetEpoch),probe.CandidateId,probe.GrantId,probe.Restart),
        PrepareOriginalControl control=>await service.PrepareOriginalBodyControlAsync(control.InspectionId),
        RunCompilationControl control=>await service.StartCompilationControlAsync(control.PlanId,control.MutationGrantId,control.ExecutionGrantId,control.InspectionGrantId,control.IdempotencyKey),
        RunNativeTrial trial=>await service.StartNativeTrialAsync(trial.PlanId,trial.OriginalGrantId,trial.CandidateGrantId,trial.ExecutionGrantId,trial.InspectionGrantId,trial.IdempotencyKey),
        _=>throw new LabException("invalid_request","Invalid patch action.")
    });

        [Description("Start an authorized capture or frozen scenario plan; poll, cancel, or stop by operation ID. Issue scoped grants through the local CLI within the authorized task; manual approval is optional.")]
    public Task<LabResponse> Run(RunRequest request)=>Respond(async()=>request switch
    {
        StartProfilerAttachment attach=>service.StartProfilerAttachment(attach.PlanId,attach.GrantId,attach.IdempotencyKey),
        ReconcileProfilerAttachment attach=>await service.ReconcileProfilerAttachmentAsync(attach.OperationId,attach.GrantId),
        StartCapture capture=>service.StartCapture(capture.TargetId,Integer(capture.ExpectedTargetEpoch),capture.Profile,
            capture.DurationSeconds,capture.GrantId,capture.IdempotencyKey),
        StartExecution execution=>await service.StartExecutionAsync(execution.TargetId,Integer(execution.ExpectedTargetEpoch),
            execution.PlanId,execution.GrantId,execution.IdempotencyKey,CancellationToken.None,execution.ObservationGrantId),
        OperationStatus status=>service.Status(status.OperationId),
        CancelOperation cancel=>service.Cancel(cancel.OperationId),
        StopCapture stop=>service.StopCapture(stop.OperationId),
        StartConfirmation confirmation=>service.StartConfirmation(confirmation.PlanId,confirmation.GrantId,confirmation.IdempotencyKey),
        StartExport export=>service.StartExport(export.PlanId,export.GrantId,export.IdempotencyKey),
        StartBundleImport import=>service.StartBundleImport(import.PlanId,import.IdempotencyKey),
        StartImportedCaptureAnalysis import=>service.StartImportedCaptureAnalysis(import.MemberId,import.IdempotencyKey),
        StartDeletion deletion=>service.StartDeletion(deletion.PlanId,deletion.GrantId,deletion.IdempotencyKey),
        StartArtifactRecovery recovery=>service.StartArtifactRecovery(recovery.IdempotencyKey,recovery.MaximumEntries,Integer(recovery.MaximumVerifiedBytes),recovery.MaximumDurationSeconds),
        StartCatalogBackup backup=>service.StartCatalogBackup(backup.IdempotencyKey),
        CheckpointCatalog checkpoint=>service.Catalog.Checkpoint(checkpoint.Truncate),
        StartReferenceRelease release=>service.StartReferenceRelease(release.PlanId,release.GrantId,release.IdempotencyKey),
        StartStackAnalysis stacks=>service.StartStackAnalysis(stacks.CaptureId,stacks.IdempotencyKey,stacks.Policy),
        StartTraceAnalysis trace=>service.StartTraceAnalysis(trace.CaptureId,trace.IdempotencyKey),
        StartQuantileComparison quantile=>service.StartQuantileComparison(quantile.DatasetId,quantile.IdempotencyKey),
        StartIndividualLatencies latency=>await service.StartIndividualLatenciesAsync(latency.TargetId,Integer(latency.ExpectedTargetEpoch),latency.PlanId,latency.GrantId,latency.IdempotencyKey,CancellationToken.None),
        StartNativeSeriesComparison series=>service.StartNativeSeriesComparison(series.DatasetId,series.IdempotencyKey),
        RegisterNativeSeriesResults series=>service.StartNativeSeriesRegistration(series.PlanId,series.ResultIds,series.IdempotencyKey),
        StartJitDisasm disasm=>service.StartJitDisasm(disasm.MethodFilter,disasm.ScenarioId,disasm.Variant,disasm.GrantId,disasm.IdempotencyKey,disasm.BuildId,disasm.ExecutablePath,disasm.Operations,disasm.WarmupBatches,disasm.TieredPgo,disasm.MaximumDurationSeconds),
        _=>throw new LabException("invalid_request","Invalid run action.")
    });

        [Description("Read bounded retained evidence, metrics, notebook history, GC timelines, counters, or artifact slices. Missing metrics return typed availability.")]
    public Task<LabResponse> Query(QueryRequest request)=>Respond(async()=>request switch
    {
        QueryRecord record=>service.Catalog.Get(record.Id,Revision(record.Revision)),
        QueryHistory history=>History(history),
        QueryMetric metric=>await service.MetricAsync(metric.AnalysisId,metric.Metric),
        QueryGc gc=>Slice((await service.AnalysisAsync(gc.AnalysisId)).GcCycles,gc.AnalysisId,"gc",gc.Limit,gc.Cursor),
        QueryRunWindow window=>await service.RunWindowAsync(window.AnalysisId,window.RunId,window.ResidualToleranceMilliseconds),
        QuerySuspensions suspensions=>Slice((await service.AnalysisAsync(suspensions.AnalysisId)).SuspensionEvidence??[],suspensions.AnalysisId,"suspensions",suspensions.Limit,suspensions.Cursor),
        QueryExport export=>await service.VerifyExportAsync(export.ResultId),
        QueryBundleImport import=>await service.VerifyBundleImportAsync(import.ResultId),
        QueryImportedMembers import=>service.ImportedMembers(import.ImportId,import.Limit,import.Cursor),
        QueryControllerResources resource=>resource.OperationId is null?service.ControllerResources():await service.ControllerResourcesAsync(resource.OperationId),
        QueryOperationPolicy policy=>service.Catalog.OperationPolicy(policy.OperationId),
        QueryRetention retention=>service.Catalog.Retention(service.Catalog.Get<Artifact>(retention.ArtifactId).Id),
        QueryArtifactRecovery recovery=>Slice((await service.ArtifactRecoveryAsync(recovery.ResultId)).Findings,recovery.ResultId,"artifact_recovery",recovery.Limit,recovery.Cursor),
        QueryCatalogBackup backup=>service.VerifyCatalogBackup(backup.BackupId),
        QueryArtifactReferences references=>service.Catalog.References(service.Catalog.Get<Artifact>(references.ArtifactId).Id,references.Limit,references.Cursor),
        QueryHotspots hotspots=>await Hotspots(hotspots),
        QuerySampledMethod method=>await service.SampledMethodAsync(method.ResultId,method.Selector),
        QueryIndividualLatencies latency=>await service.IndividualLatencySliceAsync(latency.RunId,latency.Offset,latency.Count),
        QueryNativeTrial trial=>await service.VerifyNativeTrialAsync(trial.ResultId),
        QueryNativeSeries series=>await service.ReadNativeSeriesComparisonAsync(series.ResultId),
        QueryJitInlining inlining=>await JitInlining(inlining),
        QueryJitMethods methods=>await JitMethods(methods),
        QueryContentions contentions=>await Contentions(contentions),
        QueryJitDisasm disasm=>await service.QueryJitDisasmAsync(disasm.ResultId,disasm.MethodSubstring,disasm.TierFilter,disasm.IncludeAsm,disasm.MaxAsmChars),
        QueryCounters counters=>Slice((await service.AnalysisAsync(counters.AnalysisId)).Counters,counters.AnalysisId,"counters",counters.Limit,counters.Cursor),
        QueryCounterWindow counter=>await service.CounterWindowAsync(counter.AnalysisId,counter.Name,counter.StartMilliseconds,counter.EndMilliseconds,counter.Kind,counter.Source,counter.Series),
        QueryAvailability availability=>Available(availability.CaptureId),
        QueryArtifact artifact=>Wire.Deserialize<JsonElement>(await service.ArtifactSliceAsync(artifact.ArtifactId,Integer(artifact.Offset),artifact.Count)),
        _=>throw new LabException("invalid_request","Invalid query view.")
    });
    private object TargetPage(DiscoverTargets request)
    {
        string snapshotId;
        JsonElement[] rows;
        if(request.Cursor is null)
        {
            rows=service.Discover().OrderBy(t=>t.Identity.Id).Select(t=>JsonSerializer.SerializeToElement(new {
                target_id=t.Identity.Id,process_id=t.Identity.ProcessId,name=t.Name,runtime_version=t.Identity.RuntimeVersion,
                architecture=t.Identity.Architecture,epoch=t.Identity.Epoch,eventpipe=t.Capabilities["eventpipe"].State,
                harness=t.Capabilities["harness"].State,native_inspection=t.Capabilities["native_inspection"].State
            },Wire.Json)).ToArray();
            snapshotId=Wire.Id("discovery");
            service.Catalog.Put(snapshotId,"discovery_snapshot",rows);
        }
        else
        {
            snapshotId=request.Cursor.Split(':')[0];
            if(service.Catalog.Get(snapshotId).Kind!="discovery_snapshot") throw new LabException("invalid_cursor","Unknown discovery snapshot.");
            rows=service.Catalog.Get<JsonElement[]>(snapshotId);
        }
        return Slice(rows,snapshotId,"targets",request.Limit,request.Cursor);
    }
    private async Task<object> Hotspots(QueryHotspots request)
    {
        if(request.Ranking is not ("self" or "inclusive")) throw new LabException("invalid_request","Hotspot ranking must be self or inclusive.");
        var report=await service.StackAnalysisAsync(request.ResultId);
        var rows=report.Evidence.Methods.OrderByDescending(m=>request.Ranking=="self"?m.SelfCount:m.InclusiveCount)
            .ThenBy(m=>m.Frame.Id,StringComparer.Ordinal).ToArray();
        return Slice(rows,request.ResultId,"hotspots-"+request.Ranking,request.Limit,request.Cursor);
    }
    private async Task<object> JitInlining(QueryJitInlining request)
    {
        var summary=await service.AnalysisAsync(request.AnalysisId);
        var source=(summary.JitInlinings??[]).Where(e=>
            (!request.FailuresOnly||!e.Succeeded)&&
            (string.IsNullOrEmpty(request.Filter)||
             e.Inliner.Contains(request.Filter,StringComparison.OrdinalIgnoreCase)||
             e.Inlinee.Contains(request.Filter,StringComparison.OrdinalIgnoreCase)||
             (e.FailReason?.Contains(request.Filter,StringComparison.OrdinalIgnoreCase)==true))).ToArray();
        string viewKey="jit-inlining-"+(request.FailuresOnly?"failed":"all")+(string.IsNullOrEmpty(request.Filter)?"":"-"+Wire.Hash(request.Filter)[..8]);
        return new { summary=summary.JitSummary,page=Slice(source,request.AnalysisId,viewKey,request.Limit,request.Cursor) };
    }
    private async Task<object> JitMethods(QueryJitMethods request)
    {
        var summary=await service.AnalysisAsync(request.AnalysisId);
        var source=(summary.JitMethods??[]).Where(m=>
            string.IsNullOrEmpty(request.Filter)||
            m.MethodName.Contains(request.Filter,StringComparison.OrdinalIgnoreCase)||
            m.MethodNamespace.Contains(request.Filter,StringComparison.OrdinalIgnoreCase)||
            m.OptimizationTier.Contains(request.Filter,StringComparison.OrdinalIgnoreCase)).ToArray();
        string viewKey="jit-methods"+(string.IsNullOrEmpty(request.Filter)?"":"-"+Wire.Hash(request.Filter)[..8]);
        return new { summary=summary.JitSummary,page=Slice(source,request.AnalysisId,viewKey,request.Limit,request.Cursor) };
    }
    private async Task<object> Contentions(QueryContentions request)
    {
        var summary=await service.AnalysisAsync(request.AnalysisId);
        var ordered=(summary.Contentions??[]).OrderByDescending(c=>c.DurationMilliseconds).ThenBy(c=>c.StartMilliseconds).ToArray();
        return new { summary=summary.ContentionSummary,page=Slice(ordered,request.AnalysisId,"contentions",request.Limit,request.Cursor) };
    }
    private object History(QueryHistory request)
    {
        if(!new[]{"hypothesis","candidate","plan","execution","run","comparison","work_item","decision","capture","analysis","baseline","baseline_alias","analysis_failure",
            "normal_build","confirmation_plan","confirmation_execution","normal_build_session","confirmation_decision","objective_vector","factorial_comparison","execution_window","export_plan","export_result","bundle_import_plan","bundle_import_result","imported_evidence_member","controller_resource_snapshot","controller_resource_unavailable","deletion_plan","deletion_result","artifact_recovery","catalog_backup","reference_release_plan","reference_release_result","stack_analysis","stack_analysis_failure","quantile_plan","quantile_dataset","quantile_comparison","individual_latency_plan","individual_latency_run","compilation_control_plan","compilation_control_measurement","compilation_control_result","native_trial_plan","native_trial_run","native_trial_result","native_series_plan","native_series_dataset","native_series_comparison","jit_disasm_result"}.Contains(request.Kind))
            throw new LabException("invalid_request","Unknown notebook/evidence view.");
        return service.Catalog.List(request.Kind,request.Limit,request.Cursor,includeOversizedFirst:true);
    }
    private object Available(string captureId)
    {
        var capture=service.Catalog.Get<Capture>(captureId);
        return new { capture_id=capture.Id,profile=capture.Profile.Name,profile_hash=capture.Profile.Hash,
            data_classes=capture.Profile.DataClasses,state=capture.State,quality=capture.Quality,
            artifact_id=capture.ArtifactId,limitations=new[]{"Sampling does not establish exact method timing or call counts.","Loss is quantified only by supported parser indicators."} };
    }
    private static Page<T> Slice<T>(IReadOnlyList<T> source,string analysisId,string view,int limit,string? cursor)
    {
        if(limit is <1 or >1000) throw new LabException("resource_limit","Page limit must be 1..1000.");
        int offset=0;
        string prefix=analysisId+":"+view+":";
        if(cursor is not null && (!cursor.StartsWith(prefix,StringComparison.Ordinal)||
            !int.TryParse(cursor[prefix.Length..],out offset)||offset<0||offset>=source.Count))
            throw new LabException("invalid_cursor","Cursor is invalid for this immutable analysis/view.");
        var rows=source.Skip(offset).Take(limit).ToArray();
        bool more=offset+rows.Length<source.Count;
        return new(rows,more,more?prefix+(offset+rows.Length):null);
    }
        [Description("Compare the exact completed execution under its frozen plan. Conclusions and intervals are computed by the engine and persisted immutably.")]
    public Task<LabResponse> Compare(CompareRequest request)=>Respond(async()=>
    {
        if(new[]{request.NormalBuildConfirmation,request.IncludeObjectives,request.IncludeFactorial}.Count(value=>value)>1)
            throw new LabException("invalid_request","Choose one confirmation, objective-vector, or factorial result mode.");
        return request.NormalBuildConfirmation?(object)await service.CompareConfirmationAsync(request.ExecutionId):
            request.IncludeObjectives?await service.CompareObjectivesAsync(request.ExecutionId):
            request.IncludeFactorial?await service.CompareFactorialAsync(request.ExecutionId):await service.CompareAsync(request.ExecutionId);
    });

    [McpServerResource(UriTemplate="dotnetlab://records/{id}",Name="record",MimeType="application/json")]
    public async Task<string> ReadRecord(string id)
    {
        string text=Wire.Serialize(service.Catalog.Get(id));
        byte[] bytes=Encoding.UTF8.GetBytes(text);
        if(bytes.Length>service.Limits.InlineBytes) return (await ArtifactReference(bytes,"notebook_record")).GetRawText();
        return text;
    }
    [McpServerResource(UriTemplate="dotnetlab://operations/{id}",Name="operation",MimeType="application/json")]
    public string ReadOperation(string id)=>Wire.Serialize(service.Status(id));
    [McpServerResource(UriTemplate="dotnetlab://artifacts/{id}/{offset}/{count}",Name="artifact_slice",MimeType="application/json")]
    public Task<string> ReadArtifact(string id,string offset,int count)=>service.ArtifactSliceAsync(id,Integer(offset),count);

    public Task<LabResponse> InvokeAsync(string tool,string json)
    {
        try
        {
            return tool switch
            {
                "lab.discover"=>Discover(Wire.Deserialize<DiscoverRequest>(json)),
                "lab.inspect"=>Inspect(Wire.Deserialize<InspectRequest>(json)),
                "lab.experiment"=>Experiment(Wire.Deserialize<ExperimentRequest>(json)),
                "lab.run"=>Run(Wire.Deserialize<RunRequest>(json)),
                "lab.query"=>Query(Wire.Deserialize<QueryRequest>(json)),
                "lab.compare"=>Compare(Wire.Deserialize<CompareRequest>(json)),
                "lab.patch"=>Patch(Wire.Deserialize<PatchRequest>(json)),
                _=>Task.FromResult(Error("invalid_request","Unknown tool."))
            };
        }
        catch(JsonException) { return Task.FromResult(Error("invalid_request","Request does not match the action schema.")); }
    }
}
