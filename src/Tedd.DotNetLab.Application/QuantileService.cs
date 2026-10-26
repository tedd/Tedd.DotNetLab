using Tedd.DotNetLab.Analysis.Statistics;
using Tedd.DotNetLab.Contracts;
using System.Text.Json.Serialization;

namespace Tedd.DotNetLab.Application;

public record QuantileCluster(string Id,IReadOnlyList<string> Blocks);
public record QuantilePlanRequest(string ObservationType,string WorkloadIdentity,string ObserverIdentity,IReadOnlyList<QuantileCluster> Clusters,
    int ObservationsPerArmPerBlock,double Quantile=.99,ulong Seed=42,int Resamples=10000,double AlphaFamily=.05,int FamilySize=1,
    bool IndependentClusters=false,string IndependenceJustification="",double UsefulImprovementNs=0,double MeaningfulRegressionNs=0,int MaximumDurationSeconds=300,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] QuantileHarnessPlanSelection? HarnessPlans=null);
public record QuantilePlan(string Id,QuantilePlanRequest Design,DateTimeOffset FrozenAt,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] QuantileHarnessBinding? HarnessBinding=null)
{
    public string ContentHash=>HarnessBinding is null?Wire.Hash(new { Id,Design,FrozenAt }):Wire.Hash(new { Id,Design,FrozenAt,HarnessBinding });
}
public record PairedObservationBlock(string Id,IReadOnlyList<double> Baseline,IReadOnlyList<double> Candidate);
public record QuantileObservations(string PlanId,string PlanHash,string ObservationType,string Unit,string WorkloadIdentity,string ObserverIdentity,
    IReadOnlyList<PairedObservationBlock> Blocks,string Provenance="supplied_individual_observations_not_capture_certified");
public record QuantileDataset(string Id,string PlanId,string PlanHash,string ArtifactId,string ArtifactHash,int Blocks,int Clusters,int RawObservations,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] QuantileHarnessManifestReference? HarnessEvidence=null);
public record QuantileComparison(string Id,string PlanId,string PlanHash,string DatasetId,string SourceArtifactId,string SourceArtifactHash,
    string ObservationType,string Unit,int RawObservations,int DeclaredClusters,int IndependentClusters,double BaselineQuantile,double CandidateQuantile,
    double Difference,BootstrapResult? Bootstrap,string Assessment,IReadOnlyList<string> Reasons,
    string ObservationProvenance="supplied_individual_observations_not_capture_certified",bool PromotionEligible=false,
    bool EquivalenceInferenceSupported=false,double? DifferencePValue=null,double? EquivalencePValue=null,string QuantileConvention="linear_h=(N-1)*p/v1");

public sealed partial class LabService
{
    public QuantilePlan CreateQuantilePlan(QuantilePlanRequest design)
    {
        if(design is null||design.Clusters is null||design.Clusters.Any(c=>c is null)) throw new LabException("invalid_plan","Cluster layout is required.");
        var ids=design.Clusters?.SelectMany(c=>c.Blocks??[]).ToArray()??[];
        if(design.ObservationType is not ("frame_time" or "individual_operation_latency")||string.IsNullOrWhiteSpace(design.WorkloadIdentity)||design.WorkloadIdentity.Length>512||
            string.IsNullOrWhiteSpace(design.ObserverIdentity)||design.ObserverIdentity.Length>512||design.Clusters is null||design.Clusters.Count is <1 or >100||
            design.Clusters.Any(c=>string.IsNullOrWhiteSpace(c.Id)||c.Id.Length>128||c.Blocks is null||c.Blocks.Count is <1 or >100)||
            design.Clusters.Select(c=>c.Id).Distinct().Count()!=design.Clusters.Count||ids.Length is <1 or >1000||ids.Distinct().Count()!=ids.Length||
            ids.Any(id=>string.IsNullOrWhiteSpace(id)||id.Length>128)||design.ObservationsPerArmPerBlock is <1 or >10000||
            !double.IsFinite(design.Quantile)||design.Quantile is <0 or >1||!double.IsFinite(design.AlphaFamily)||design.AlphaFamily is <=0 or >=.5||design.FamilySize is <1 or >10000||
            design.Resamples is <1000 or >100000||design.Resamples*(design.AlphaFamily/design.FamilySize)/2<5||
            !double.IsFinite(design.UsefulImprovementNs)||!double.IsFinite(design.MeaningfulRegressionNs)||design.UsefulImprovementNs<0||design.MeaningfulRegressionNs<0||
            design.MaximumDurationSeconds is <1 or >600||design.MaximumDurationSeconds>Limits.MaximumDurationSeconds||
            design.IndependentClusters&&(string.IsNullOrWhiteSpace(design.IndependenceJustification)||design.IndependenceJustification.Length>4096))
            throw new LabException("invalid_plan","Invalid individual-observation, cluster, inference, or resource policy.");
        long observations=checked(2L*ids.Length*design.ObservationsPerArmPerBlock);
        if(observations>100000||observations*design.Resamples>50000000)
            throw new LabException("resource_limit","Quantile plan exceeds observation or bootstrap work budget.");
        var binding=FreezeQuantileHarnessBinding(design);
        var plan=new QuantilePlan(Wire.Id("quantile-plan"),design,DateTimeOffset.UtcNow,binding); Catalog.Put(plan.Id,"quantile_plan",plan); return plan;
    }
    private static PairedCluster[] ValidateObservations(QuantilePlan plan,QuantileObservations observations,bool harnessVerified=false)
    {
        var design=plan.Design;
        if(observations.PlanId!=plan.Id||observations.PlanHash!=plan.ContentHash||observations.ObservationType!=design.ObservationType||observations.Unit!="ns"||
            observations.WorkloadIdentity!=design.WorkloadIdentity||observations.ObserverIdentity!=design.ObserverIdentity||
            observations.Provenance!=(harnessVerified?QuantileHarnessProvenance:"supplied_individual_observations_not_capture_certified")||observations.Blocks is null||observations.Blocks.Any(b=>b is null)||
            observations.Blocks.Count!=design.Clusters.Sum(c=>c.Blocks.Count)||observations.Blocks.Select(b=>b.Id).Distinct().Count()!=observations.Blocks.Count)
            throw new LabException("not_comparable","Individual observations differ from the exact frozen plan and source semantics.");
        var blocks=observations.Blocks.ToDictionary(b=>b.Id,StringComparer.Ordinal); var result=new List<PairedCluster>();
        foreach(var cluster in design.Clusters)
        {
            var baseline=new List<double>(); var candidate=new List<double>();
            foreach(string id in cluster.Blocks)
            {
                if(!blocks.TryGetValue(id,out var block)||block.Baseline is null||block.Candidate is null||
                    block.Baseline.Count!=design.ObservationsPerArmPerBlock||block.Candidate.Count!=design.ObservationsPerArmPerBlock||
                    block.Baseline.Concat(block.Candidate).Any(value=>!double.IsFinite(value)||value<0))
                    throw new LabException("not_comparable","Every declared paired block must retain its complete ordered individual-observation budget.");
                baseline.AddRange(block.Baseline); candidate.AddRange(block.Candidate);
            }
            result.Add(new(cluster.Id,baseline,candidate));
        }
        return result.ToArray();
    }
    public async Task<QuantileDataset> RegisterQuantileObservationsAsync(string planId,string artifactId)
    {
        var plan=Record<QuantilePlan>(planId,"quantile_plan"); var artifact=Record<Artifact>(artifactId,"artifact");
        if(plan.HarnessBinding is not null) throw new LabException("invalid_plan","Harness-bound plans require verified run bindings, rather than supplied observation arrays.");
        if(!artifact.Complete||artifact.Length>16777216||artifact.MediaType!="application/json")
            throw new LabException("invalid_request","Quantile observations require a complete bounded JSON artifact.");
        if(Catalog.Get(artifactId).CreatedAt<plan.FrozenAt) throw new LabException("invalid_plan","Observation artifact must be registered after the plan was frozen.");
        var observations=Wire.Deserialize<QuantileObservations>(await File.ReadAllTextAsync(await Artifacts.VerifiedPathAsync(artifact)));
        var clusters=ValidateObservations(plan,observations);
        string id="quantile-data-"+Wire.Hash(new { planId,artifact.Hash });
        var dataset=new QuantileDataset(id,plan.Id,plan.ContentHash,artifact.Id,artifact.Hash,observations.Blocks.Count,clusters.Length,
            clusters.Sum(c=>c.Baseline.Count+c.Candidate.Count));
        lock(gate) { Upsert(id,"quantile_dataset",dataset); Catalog.Pin(artifact.Id,"quantile_individual_observations"); }
        return dataset;
    }
    public Operation StartQuantileComparison(string datasetId,string key)
    {
        var dataset=Record<QuantileDataset>(datasetId,"quantile_dataset"); var plan=Record<QuantilePlan>(dataset.PlanId,"quantile_plan");
        if(dataset.PlanHash!=plan.ContentHash) throw new LabException("not_comparable","Dataset plan hash differs from its frozen plan.");
        string scope="quantile-comparison-"+datasetId;
        return Start("quantile_comparison",new(scope,0,0,scope,"workspace","local"),key,Wire.Hash(new { datasetId,plan.ContentHash }),async(operation,cancellation)=>
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(plan.Design.MaximumDurationSeconds));
            var source=Record<Artifact>(dataset.ArtifactId,"artifact");
            if(source.Hash!=dataset.ArtifactHash) throw new LabException("artifact_corrupt","Source observations differ from the dataset hash.");
            var data=Wire.Deserialize<QuantileObservations>(await File.ReadAllTextAsync(await Artifacts.VerifiedPathAsync(source,deadline.Token),deadline.Token));
            if(dataset.HarnessEvidence is not null) await VerifyQuantileHarnessEvidenceAsync(plan,dataset,data,deadline.Token);
            else if(plan.HarnessBinding is not null) throw new LabException("not_comparable","Harness-bound dataset lacks source-run evidence.");
            var clusters=ValidateObservations(plan,data,dataset.HarnessEvidence is not null); var design=plan.Design;
            double baseline=StatisticalEngine.Quantile(clusters.SelectMany(c=>c.Baseline).ToArray(),design.Quantile);
            double candidate=StatisticalEngine.Quantile(clusters.SelectMany(c=>c.Candidate).ToArray(),design.Quantile);
            BootstrapResult? bootstrap=null; string assessment="descriptive_only"; var reasons=new List<string> { "conditional_on_declared_cluster_independence" };
            reasons.Add(dataset.HarnessEvidence is null?"supplied_observations_not_capture_certified":"verified_harness_collection_does_not_prove_session_independence");
            if(!design.IndependentClusters||clusters.Length<10) reasons.Add("insufficient_independent_evidence");
            else
            {
                bootstrap=StatisticalEngine.ClusterQuantile(clusters,design.Quantile,design.Seed,design.Resamples,design.AlphaFamily/design.FamilySize,true,deadline.Token);
                assessment=bootstrap.Interval.Upper < -design.UsefulImprovementNs?"bootstrap_interval_supports_useful_improvement":
                    bootstrap.Interval.Lower > design.MeaningfulRegressionNs?"bootstrap_interval_supports_meaningful_regression":"bootstrap_threshold_inconclusive";
                if(bootstrap.Interval.Lower==bootstrap.Interval.Upper)
                { assessment="descriptive_only"; reasons.Add("degenerate_bootstrap_distribution"); }
                reasons.Add("percentile_interval_approximation_no_p_value_or_equivalence");
            }
            var comparison=new QuantileComparison(operation.Id+"-quantile",plan.Id,plan.ContentHash,dataset.Id,source.Id,source.Hash,design.ObservationType,"ns",
                dataset.RawObservations,clusters.Length,design.IndependentClusters?clusters.Length:0,baseline,candidate,candidate-baseline,bootstrap,assessment,reasons,
                ObservationProvenance:data.Provenance);
            if(!double.IsFinite(comparison.Difference)) throw new LabException("invalid_statistical_input","Quantile difference is nonfinite.");
            var artifact=await Artifacts.WriteJsonAsync(operation.Id,comparison,"paired-cluster-quantile-percentile/v1",cancellation:deadline.Token);
            Upsert(artifact.Id,"artifact",artifact); Catalog.Pin(artifact.Id,"quantile_comparison");
            Catalog.Put(comparison.Id,"quantile_comparison",comparison);
            Catalog.Put(comparison.Id+"-manifest","quantile_comparison_manifest",new { artifact_id=artifact.Id,source_artifact_id=source.Id });
            Catalog.Transition(operation.Id,OperationState.Completed,comparison.Id); return true;
        });
    }
}
