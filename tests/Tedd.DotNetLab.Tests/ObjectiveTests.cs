using System.Text.Json;
using Tedd.DotNetLab.Analysis.Statistics;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class ObjectiveTests
{
    private static readonly double[] Logs=[-.042,-.041,-.040,-.039,-.038,-.0415,-.0405,-.0395,-.0385,-.040];
    public static MetricPolicy Cost()=>new("scenario.ns_per_operation","ns/operation","batch_normalized_mean",MetricDirection.LowerIsBetter,EffectScale.PositiveRatio,.99,1.01,.99,1.01);
    public static MetricPolicy Allocation(bool descriptive=false)=>new("scenario.process_allocation_bytes_per_operation","bytes/operation","batch_normalized_allocation_delta",MetricDirection.LowerIsBetter,EffectScale.AbsoluteDifference,-2,2,-2,2,DescriptiveOnly:descriptive);
    public static MetricPolicy Gc(bool descriptive=false)=>new("gc.suspension_overlap_ns","ns","execution_suspension_overlap",MetricDirection.LowerIsBetter,EffectScale.AbsoluteDifference,-1000000,1000000,-1000000,1000000,DescriptiveOnly:descriptive);
    private static MetricPolicy Throughput()=>new("scenario.operations_per_second","operations/second","batch_rate",MetricDirection.HigherIsBetter,EffectScale.PositiveRatio,.99,1.01,1.01,.99);
    private static MetricPair[] Pairs(MetricPolicy policy,double[] baseline,double[] candidate,Availability status=Availability.Available)
    {
        var definition=Metrics.Define(policy.Metric);
        MetricValue Value(double value)=>new(policy.Metric,status,status==Availability.Available?value:null,policy.Unit,policy.ObservationKind,definition.Scope);
        return baseline.Select((value,index)=>new MetricPair(index.ToString(),index.ToString(),Value(value),Value(candidate[index]),"b-"+index,"c-"+index)).ToArray();
    }
    private static MetricEffect Benefit(AnalysisPolicy? analysis=null)=>ObjectiveEngine.Evaluate(Cost(),analysis??new(),Pairs(Cost(),Enumerable.Repeat(100d,10).ToArray(),Logs.Select(d=>100*Math.Exp(d)).ToArray()),true);

    [Theory,InlineData("cost"),InlineData("throughput"),InlineData("allocation"),Case("UT-STAT-018","FR-024,FR-033,FR-034","M6","Unit")]
    public void Objective_Inference_MatchesIndependentSciPyOracle(string name)
    {
        using var oracle=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","statistics","golden.json")));
        var fixture=oracle.RootElement.GetProperty("objectives").GetProperty(name);
        var policy=name switch { "cost"=>Cost(),"throughput"=>Throughput(),_=>Allocation() };
        var baseline=fixture.GetProperty("baseline").EnumerateArray().Select(value=>value.GetDouble()).ToArray();
        var candidate=fixture.GetProperty("candidate").EnumerateArray().Select(value=>value.GetDouble()).ToArray();
        var effect=ObjectiveEngine.Evaluate(policy,new(FamilySize:3),Pairs(policy,baseline,candidate),true);
        Assert.Equal("usable",effect.Quality);
        Assert.Equal(fixture.GetProperty("effect").GetDouble(),effect.Effect!.Value,11);
        Assert.Equal(fixture.GetProperty("standard_error").GetDouble(),effect.StandardError!.Value,11);
        Assert.Equal(fixture.GetProperty("alpha_local").GetDouble(),effect.AlphaLocal,12);
        Assert.Equal(fixture.GetProperty("difference_interval")[0].GetDouble(),effect.DifferenceInterval!.Lower,10);
        Assert.Equal(fixture.GetProperty("difference_interval")[1].GetDouble(),effect.DifferenceInterval.Upper,10);
        Assert.Equal(fixture.GetProperty("equivalence_interval")[0].GetDouble(),effect.EquivalenceInterval!.Lower,10);
        Assert.Equal(fixture.GetProperty("equivalence_interval")[1].GetDouble(),effect.EquivalenceInterval.Upper,10);
        Assert.InRange(Math.Abs(Math.Log(fixture.GetProperty("difference_p").GetDouble())-Math.Log(effect.DifferenceP!.Value)),0,1e-8);
        Assert.InRange(Math.Abs(Math.Log(fixture.GetProperty("tost_p").GetDouble())-Math.Log(effect.TostP!.Value)),0,1e-8);
        Assert.True(effect.NonInferiorityPassed);
        Assert.Equal(name=="allocation"?EffectClass.EquivalentWithinMargin:EffectClass.Improved,effect.EffectClass);
    }
    [Fact,Case("UT-STAT-027","FR-036,FR-055","M6","Unit")]
    public void MandatoryGuardrail_RegressionOrUnknown_BlocksUsefulPrimaryBenefit()
    {
        var primary=Benefit(); Assert.Equal(EffectClass.Improved,primary.EffectClass);
        var policy=Allocation(); var baseline=Enumerable.Repeat(100d,10).ToArray();
        var regression=ObjectiveEngine.Evaluate(policy,new(),Pairs(policy,baseline,baseline.Select((b,i)=>b+10+(i-4.5)*.01).ToArray()),true);
        Assert.Equal(EffectClass.Regressed,regression.EffectClass);
        Assert.False(ObjectiveEngine.Promotion([primary,regression],true).Eligible);
        Assert.Contains("guardrail_regressed:"+policy.Metric,ObjectiveEngine.Promotion([primary,regression],true).Reasons);
        var unknown=ObjectiveEngine.Evaluate(Gc(),new(),Pairs(Gc(),baseline,baseline,Availability.NotCollected),true);
        Assert.Equal(Availability.NotCollected,unknown.Availability); Assert.Null(unknown.Effect);
        Assert.False(ObjectiveEngine.Promotion([primary,unknown],true).Eligible);
        Assert.Equal(ParetoRelation.Unresolved,ObjectiveEngine.Pareto([primary,unknown]));
        var descriptive=ObjectiveEngine.Evaluate(Gc(true),new(),Pairs(Gc(true),baseline,baseline,Availability.NotCollected),true);
        Assert.True(ObjectiveEngine.Promotion([primary,descriptive],true).Eligible);
        Assert.False(ObjectiveEngine.Promotion([primary,descriptive],false).Eligible);
    }
    [Fact,Case("UT-STAT-028","FR-036","M6","Unit")]
    public void OppositeMetricDirections_PreserveRawRatiosAndConsistentBenefit()
    {
        var primary=Benefit(new(FamilySize:2)); var policy=Throughput();
        var throughput=ObjectiveEngine.Evaluate(policy,new(FamilySize:2),Pairs(policy,Enumerable.Repeat(100d,10).ToArray(),Logs.Select(d=>100*Math.Exp(-d)).ToArray()),true);
        Assert.True(primary.Effect<1); Assert.True(throughput.Effect>1);
        Assert.Equal(EffectClass.Improved,throughput.EffectClass);
        Assert.Equal(ParetoRelation.CandidateDominates,ObjectiveEngine.Pareto([primary,throughput]));
        Assert.True(ObjectiveEngine.Promotion([primary,throughput],true).Eligible);
    }
    [Fact,Case("UT-STAT-029","FR-036","M6","Unit")]
    public void WideObjectiveUncertainty_DoesNotEstablishParetoDominanceOrSafety()
    {
        var policy=Allocation(); double[] change=[-20,-15,-10,-5,0,5,10,15,20,0];
        var effect=ObjectiveEngine.Evaluate(policy,new(),Pairs(policy,Enumerable.Repeat(100d,10).ToArray(),change.Select(d=>100+d).ToArray()),true);
        Assert.Equal(EffectClass.Inconclusive,effect.EffectClass); Assert.False(effect.NonInferiorityPassed);
        Assert.Equal(ParetoRelation.Unresolved,ObjectiveEngine.Pareto([Benefit(),effect]));
    }
    [Fact,Case("UT-STAT-008","FR-033,FR-034","M6","Unit")]
    public void ZeroAllocationVariance_RemainsDescriptiveWithoutEpsilonOrConfidenceGuarantee()
    {
        var policy=Allocation(); var zeros=new double[10];
        var effect=ObjectiveEngine.Evaluate(policy,new(),Pairs(policy,zeros,zeros),true);
        Assert.Equal(0,effect.Effect); Assert.Equal("bytes/operation",effect.EffectUnit);
        Assert.Null(effect.DifferenceInterval); Assert.Null(effect.TostP); Assert.False(effect.NonInferiorityPassed);
        Assert.Contains("degenerate_variance",effect.ReasonCodes);
        Assert.False(ObjectiveEngine.Promotion([Benefit(),effect],true).Eligible);
    }
    [Fact,Case("UT-STAT-014","FR-034,FR-055","M6","Unit")]
    public void GuardrailConfidenceEndpoint_ExactlyAtHarmMargin_DoesNotPass()
    {
        var policy=Allocation(); var baseline=Enumerable.Repeat(10d,10).ToArray();
        var pairs=Pairs(policy,baseline,baseline.Select((b,i)=>b+1+(i-4.5)*.01).ToArray());
        var first=ObjectiveEngine.Evaluate(policy,new(),pairs,true); double margin=first.DifferenceInterval!.Upper;
        var strict=ObjectiveEngine.Evaluate(policy with { EquivalenceLower=-margin/2,EquivalenceUpper=margin/2,UsefulImprovement=-margin,MeaningfulRegression=margin },new(),pairs,true);
        Assert.Equal(margin,strict.DifferenceInterval!.Upper); Assert.False(strict.NonInferiorityPassed);
    }
    [Fact,Case("UT-EXP-014","FR-032","M6","Unit")]
    public void MissingOrMismatchedGuardrail_DoesNotInvalidateIndependentPrimaryMetric()
    {
        var primary=Benefit(); var policy=Allocation(); var pairs=Pairs(policy,new double[10],Enumerable.Repeat(1d,10).ToArray());
        var malformed=pairs.Select(pair=>pair with { Candidate=pair.Candidate with { Unit="ns" } }).ToArray();
        var blocked=ObjectiveEngine.Evaluate(policy,new(),malformed,true);
        Assert.Equal(EffectClass.NotComparable,blocked.EffectClass); Assert.Contains("metric_unit_scope_or_estimand_mismatch",blocked.ReasonCodes);
        Assert.Equal(EffectClass.Improved,primary.EffectClass); Assert.NotNull(primary.DifferenceInterval);
        var descriptive=ObjectiveEngine.Evaluate(policy,new(),pairs,true,descriptiveReasons:["observer_mismatch_descriptive_allocation_only"]);
        var timing=ObjectiveEngine.Evaluate(Cost(),new(),Pairs(Cost(),Enumerable.Repeat(100d,10).ToArray(),Logs.Select(d=>100*Math.Exp(d)).ToArray()),true,
            blockers:["timing_profile_or_observer_mismatch"]);
        Assert.Equal("descriptive_only",descriptive.Quality); Assert.Equal(1,descriptive.Effect); Assert.Null(descriptive.DifferenceInterval);
        Assert.Equal(EffectClass.NotComparable,timing.EffectClass); Assert.Null(timing.Effect);
    }
    [Fact,Case("UT-STAT-016","FR-035","M6","Unit")]
    public void ObjectiveFamilyAndMetadata_AreValidatedBeforeMeasurement()
    {
        var allocation=Allocation();
        var plan=new ExperimentPlan("","s","1","input",["b","c"],10,100,1,42,"quiet-validation",true,"Declared independent blocks.",true,60,new(),
            GuardrailMetrics:[allocation.Metric],GuardrailPolicies:[allocation]);
        Assert.Equal("invalid_plan",Assert.Throws<LabException>(()=>Policies.Validate(plan)).Code);
        Policies.Validate(plan with { Analysis=new(FamilySize:2) });
        Policies.Validate(plan with { GuardrailPolicies=[allocation with { DescriptiveOnly=true }] });
        Assert.Equal("invalid_plan",Assert.Throws<LabException>(()=>Policies.Validate(allocation with { Scale=EffectScale.PositiveRatio })).Code);
        Assert.Equal("invalid_plan",Assert.Throws<LabException>(()=>Policies.Validate(allocation with { Direction=MetricDirection.HigherIsBetter })).Code);
        var unadjusted=ObjectiveEngine.Evaluate(Throughput(),new(),Pairs(Throughput(),Enumerable.Repeat(100d,10).ToArray(),Logs.Select(d=>100*Math.Exp(-d)).ToArray()),true);
        Assert.Contains("inconsistent_or_incomplete_objective_family",ObjectiveEngine.Promotion([Benefit(),unadjusted],true).Reasons);
        Assert.Equal(ParetoRelation.Unresolved,ObjectiveEngine.Pareto([Benefit(),unadjusted]));
    }
    [Fact,Case("UT-HAR-014","FR-024","M6","Unit")]
    public void ThreadAllocationEligibilityAndProcessAllocation_HaveDistinctScopes()
    {
        var value=new Measurement("e","r","b","candidate",100,200,10000000,10,100,50,1,2,true,null,"input","1");
        var process=Metrics.Read("scenario.process_allocation_bytes_per_operation",value);
        var thread=Metrics.Read("scenario.thread_allocation_bytes_per_operation",value);
        Assert.Equal(10,process.Value); Assert.Equal("process",process.Scope); Assert.Equal(Availability.Available,process.Status);
        Assert.Null(thread.Value); Assert.Equal("current_thread",thread.Scope); Assert.Equal(Availability.NotApplicable,thread.Status);
    }
    [Fact,Case("UT-STAT-008","FR-033,FR-034","M6","Unit")]
    public void RoundedConfidenceIntervals_DoNotCreateZeroWidthGuarantees()
    {
        var policy=Allocation(); var candidate=Enumerable.Repeat(1e18,10).ToArray(); candidate[0]=Math.BitIncrement(candidate[0]);
        var additive=ObjectiveEngine.Evaluate(policy,new(),Pairs(policy,new double[10],candidate),true);
        Assert.True(additive.StandardError>0); Assert.Null(additive.DifferenceInterval);
        Assert.Contains("measurement_resolution_floor",additive.ReasonCodes);
        var costs=Enumerable.Repeat(Math.Exp(.04),10).ToArray(); costs[0]=Math.BitIncrement(costs[0]);
        var ratio=StatisticalEngine.Paired(costs.Select((cost,i)=>new CostPair(i.ToString(),i.ToString(),1,cost)).ToArray(),new());
        Assert.True(ratio.StandardError>0); Assert.Null(ratio.DifferenceIntervalRatio);
        Assert.Contains("measurement_resolution_floor",ratio.ReasonCodes); Assert.False(ratio.PromotionEligible);
    }
    [Fact,Case("UT-STAT-017","FR-033,FR-035","M6","Unit")]
    public void UnrepresentableLocalAlpha_IsRejectedBeforeExecution()
    {
        Assert.Equal("invalid_plan",Assert.Throws<LabException>(()=>Policies.Validate(new AnalysisPolicy(AlphaFamily:1e-300))).Code);
    }
    [Fact,Case("UT-EXP-004","FR-033","M6","Unit")]
    public void RepeatedRowsFromOneCluster_DoNotIncreaseIndependentUnitCount()
    {
        var pairs=Pairs(Cost(),Enumerable.Repeat(100d,10).ToArray(),Logs.Select(value=>100*Math.Exp(value)).ToArray());
        var effect=ObjectiveEngine.Evaluate(Cost(),new(),pairs.Select(pair=>pair with { ClusterId="one-session" }).ToArray(),true);
        Assert.Equal(1,effect.IndependentBlocks); Assert.Equal(EffectClass.NotComparable,effect.EffectClass);
        Assert.Null(effect.DifferenceInterval);
        var legacy=StatisticalEngine.Paired(pairs.Select(pair=>new CostPair(pair.BlockId,"one-session",pair.Baseline.Value!.Value,pair.Candidate.Value!.Value)).ToArray(),new());
        Assert.Equal(1,legacy.IndependentBlocks); Assert.Contains("dependent_cluster_rows",legacy.ReasonCodes);
    }
}
