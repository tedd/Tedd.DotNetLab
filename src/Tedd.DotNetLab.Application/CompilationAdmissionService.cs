using System.Text.Json;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;
using Tedd.DotNetLab.Transport.Local;

namespace Tedd.DotNetLab.Application;

public sealed partial class LabService
{
    private sealed record VerifiedCompilationControl(CompilationControlResult Result,CompilationControlPlan Plan,CilCandidateRecord Candidate,string RuntimeHash);
    private CompilationControlBinding ControlBinding(string resultId)
    {
        var result=Record<CompilationControlResult>(resultId,"compilation_control_result"); var record=Catalog.Get(resultId);
        var artifact=Record<Artifact>(result.ArtifactId,"artifact");
        return new(resultId,record.Revision,record.Hash,artifact.Id,artifact.Hash);
    }
    private ExperimentPlan FreezeCompilationControl(ExperimentPlan plan,string? resultId)
    {
        if(resultId is not null&&plan.CompilationControl is not null) throw new LabException("invalid_plan","Select one compilation-control result.");
        if(resultId is not null) plan=plan with { CompilationControl=ControlBinding(resultId) };
        if(plan.CompilationControl is { } selected&&selected!=ControlBinding(selected.ResultId))
            throw new LabException("invalid_plan","Compilation control differs from its exact retained revision/hash.");
        if(plan.CompilationControl is { } binding)
        {
            if(plan.FreshNormalBuild) throw new LabException("invalid_plan","Live compilation controls are scoped to their original target session.");
            var result=Record<CompilationControlResult>(binding.ResultId,"compilation_control_result");
            var control=Record<CompilationControlPlan>(result.PlanId,"compilation_control_plan");
            if(!result.ProtocolVerified||!result.RestorationVerified||control.ScenarioId!=plan.ScenarioId||control.ScenarioVersion!=plan.ScenarioVersion||control.InputHash!=plan.InputHash||
                control.WarmupBatches!=plan.WarmupBatches||control.OperationsPerRun!=plan.OperationsPerRun)
                throw new LabException("invalid_plan","Trial workload, input, work count, or warm-up differs from the completed control.");
        }
        return plan;
    }
    private async Task<VerifiedCompilationControl> VerifyCompilationControlAsync(CompilationControlBinding binding,CancellationToken cancellation)
    {
        if(binding!=ControlBinding(binding.ResultId)) throw new LabException("control_unverified","Frozen compilation-control revision/hash changed.");
        var result=Record<CompilationControlResult>(binding.ResultId,"compilation_control_result");
        var plan=Record<CompilationControlPlan>(result.PlanId,"compilation_control_plan"); var root=Record<MutationRoot>(result.TransactionId,"mutation");
        var outcome=Record<MutationOutcome>(root.Id+"-outcome","mutation_outcome"); var operation=Catalog.GetOperation(root.OperationId);
        var candidate=Record<CilCandidateRecord>(plan.Candidate.Id,"cil_candidate"); var candidateRecord=Catalog.Get(candidate.Id);
        if(operation.State!=OperationState.Completed||operation.ResultId!=outcome.Id||outcome.CompilationControlResultId!=result.Id||!outcome.Restored||outcome.Tainted||
            !result.ProtocolVerified||!result.RestorationVerified||result.PlanHash!=Wire.Hash(plan)||root.CompilationControlPlanId!=plan.Id||result.CandidateHash!=candidate.ContentHash||
            candidateRecord.Revision!=plan.Candidate.Revision||candidateRecord.Hash!=plan.Candidate.Hash||candidate.BodyHash!=candidate.OriginalIlHash||
            result.MeasuredRuns!=2*plan.Blocks||result.IndependentSessions!=1||result.RestoredEpoch!=outcome.Epoch)
            throw new LabException("control_unverified","Control lacks complete protocol, exact candidate, and verified terminal restoration evidence.");
        VerifyMutationJournal(root,candidate);
        var artifact=Record<Artifact>(binding.ArtifactId,"artifact");
        if(artifact.Hash!=binding.ArtifactHash||artifact.Length>67108864) throw new LabException("artifact_corrupt","Control manifest hash or size differs.");
        using var manifest=JsonDocument.Parse(await File.ReadAllTextAsync(await Artifacts.VerifiedPathAsync(artifact,cancellation),cancellation));
        if(Wire.Hash(manifest.RootElement.GetProperty("plan").Deserialize<CompilationControlPlan>(Wire.Json))!=Wire.Hash(plan)||
            Wire.Hash(manifest.RootElement.GetProperty("candidate").Deserialize<CilCandidateRecord>(Wire.Json))!=Wire.Hash(candidate)||
            Wire.Hash(manifest.RootElement.GetProperty("root").Deserialize<MutationRoot>(Wire.Json))!=Wire.Hash(root))
            throw new LabException("artifact_corrupt","Control manifest identity differs from frozen records.");
        var rows=manifest.RootElement.GetProperty("measurements").Deserialize<CompilationControlMeasurement[]>(Wire.Json)!;
        if(rows.Length!=result.MeasuredRuns||rows.Select(r=>r.Id).Distinct().Count()!=rows.Length||rows.Any(r=>r.PlanId!=plan.Id||r.PlanHash!=result.PlanHash||
            r.Measurement.InputHash!=plan.InputHash||r.Measurement.ScenarioVersion!=plan.ScenarioVersion||r.Measurement.CompletedOperations!=plan.OperationsPerRun||
            !r.Measurement.Correct||!r.Measurement.Quiescent)||rows.Select(r=>Wire.Hash(r.Runtime)).Distinct().Count()!=1||
            rows.Count(r=>r.Regime=="original_normal_compile"&&r.Epoch==0&&r.RejitRequestFlags==0)!=plan.Blocks||
            rows.Count(r=>r.Regime=="original_body_rejit"&&r.RejitRequestFlags==plan.RejitRequestFlags)!=plan.Blocks||
            rows.GroupBy(r=>r.Regime).Any(group=>!group.Select(r=>r.BlockOrdinal).Order().SequenceEqual(Enumerable.Range(0,plan.Blocks))))
            throw new LabException("control_unverified","Control measurements do not complete both frozen regimes and observer identities.");
        var verified=new HashSet<string>();
        foreach(var row in rows)
        {
            var measurement=Record<Artifact>(row.ArtifactId,"artifact");
            if(Wire.Hash(Wire.Deserialize<Measurement>(await File.ReadAllTextAsync(await Artifacts.VerifiedPathAsync(measurement,cancellation),cancellation)))!=Wire.Hash(row.Measurement))
                throw new LabException("artifact_corrupt","Control measurement differs from sealed evidence.");
            foreach(string id in new[]{row.CodeBeforeArtifactId,row.CodeAfterArtifactId})
                if(verified.Add(id)) await Artifacts.VerifiedPathAsync(Record<Artifact>(id,"artifact"),cancellation);
        }
        return new(result,plan,candidate,Wire.Hash(rows[0].Runtime));
    }
    private async Task<CompilationRegimeEvidence?> CompilationRegimeAsync(TargetIdentity target,RuntimeFootprint? runtime,ExperimentPlan plan,
        ScenarioDescriptor scenario,VerifiedCompilationControl? control,CancellationToken cancellation)
    {
        if(runtime?.NativeProfilerPresent!=true)
        {
            if(control is not null) throw new LabException("control_mismatch","A target-scoped native control cannot authorize another compilation environment.");
            return null;
        }
        var state=await profiler.SendAsync(target,new ProfilerRequest(Wire.Version,"mutation_status",target.Id,target.ProcessId,target.StartTimeUtcTicks,target.Epoch),cancellation);
        if(state.GetProperty("pending").GetBoolean()||state.GetProperty("tainted").GetBoolean()||state.GetProperty("epoch").GetInt64FromString()!=target.Epoch)
            throw new LabException("mutation_unverified","Native execution epoch or mutation health is unresolved.");
        int mutations=state.GetProperty("mutation_count").GetInt32(); string regime=mutations==0?"original_normal_compile":state.GetProperty("mode").GetString()!;
        string methodScope=state.GetProperty("method_scope").GetString()!,bodyHash=state.GetProperty("current_body_hash").GetString()!;
        int flags=mutations==0?0:state.GetProperty("request_flags").GetInt32();
        var callers=state.GetProperty("affected_callers").EnumerateArray().Select(c=>c.GetProperty("binding").Deserialize<Tedd.DotNetLab.Analysis.Native.NativeInlinerBinding>(Wire.Json)!).ToArray();
        if(regime is "candidate_rejit" or "original_body_rejit")
        {
            if(control is null) throw new LabException("compilation_control_required","Live ReJIT performance trials require a frozen completed original-body control.");
            var source=control.Plan.Target;
            if(source.Id!=target.Id||source.ProcessId!=target.ProcessId||source.StartTimeUtcTicks!=target.StartTimeUtcTicks||source.RuntimeVersion!=target.RuntimeVersion||
                source.Architecture!=target.Architecture||control.RuntimeHash!=Wire.Hash(runtime)||control.Candidate.Scope!=methodScope||
                Wire.Hash(control.Candidate.Inliners??[])!=Wire.Hash(callers)||flags!=control.Plan.RejitRequestFlags||!state.GetProperty("compilation_observed").GetBoolean()||
                !state.GetProperty("correctness_passed").GetBoolean()||!(scenario.MutationScopes?.Contains(methodScope)??false)||
                control.Plan.ScenarioId!=plan.ScenarioId||control.Plan.InputHash!=plan.InputHash||control.Plan.ScenarioVersion!=plan.ScenarioVersion||
                control.Plan.OperationsPerRun!=plan.OperationsPerRun||control.Plan.WarmupBatches!=plan.WarmupBatches||target.Epoch<=control.Result.RestoredEpoch)
                throw new LabException("control_mismatch","Live method/caller, flags, session, observer, or workload differs from the frozen compilation control.");
        }
        else if(regime is not ("original_normal_compile" or "restored_original_version" or "selected_probe"))
            throw new LabException("mutation_unverified","Unknown native compilation regime.");
        return new(regime,target.Epoch,methodScope,bodyHash,state.GetProperty("rejit_id").GetString()!,flags,Wire.Hash(callers),
            state.GetProperty("inliner_coverage").GetString()!,plan.CompilationControl?.ResultId);
    }
}
