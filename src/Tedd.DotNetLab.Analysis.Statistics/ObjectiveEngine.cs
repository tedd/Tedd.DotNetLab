using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;

namespace Tedd.DotNetLab.Analysis.Statistics;

public static class ObjectiveEngine
{
    public const string Version="paired-objective-vector/v1";
    public static MetricEffect Evaluate(MetricPolicy policy,AnalysisPolicy analysis,IReadOnlyList<MetricPair> pairs,
        bool independent,IReadOnlyList<string>? blockers=null,IReadOnlyList<string>? descriptiveReasons=null)
    {
        Policies.Validate(policy); Policies.Validate(analysis);
        var definition=Metrics.Define(policy.Metric);
        bool ratio=policy.Scale==EffectScale.PositiveRatio;
        string estimator=ratio?StatisticalEngine.Version:"paired-additive/v1";
        double alpha=analysis.AlphaFamily/analysis.FamilySize;
        var ids=pairs.SelectMany(pair=>new[]{pair.BaselineRunId,pair.CandidateRunId}).ToArray();
        string[] assumptions=[independent?"independent_blocks_declared_by_plan":"independence_not_established","fixed_family_and_sample_budget","no_posthoc_exclusions",
            ratio?"t_model_for_paired_log_costs":"t_model_for_paired_absolute_differences",
            "intervals_describe_the_declared_estimand_not_distributional_equivalence"];
        MetricEffect Result(Availability availability,EffectClass effect,string quality,IReadOnlyList<string> reasons,
            double? estimate=null,double? se=null,double? df=null,Interval? difference=null,Interval? equivalence=null,
            double? p=null,double? tost=null,bool equivalent=false,bool noninferior=false)=>
            new(policy.Metric,policy.Unit,policy.ObservationKind,definition.Scope,policy,estimator,availability,effect,quality,
                independent?pairs.Select(pair=>pair.ClusterId).Distinct().Count():0,estimate,ratio?"ratio":policy.Unit,se,ratio?"log_ratio":"absolute_difference",df,
                difference,equivalence,p,tost,equivalent,noninferior,analysis.AlphaFamily,alpha,analysis.FamilySize,reasons,assumptions,ids);
        if(blockers is {Count:>0}) return Result(Availability.Invalid,EffectClass.NotComparable,"blocked",blockers.Distinct().ToArray());
        if(pairs.Count==0||pairs.Count>100000) return Result(Availability.Invalid,EffectClass.Invalid,"invalid",["empty_or_oversized_metric_input"]);
        if(pairs.Select(pair=>pair.BlockId).Distinct().Count()!=pairs.Count)
            return Result(Availability.Invalid,EffectClass.Invalid,"invalid",["duplicate_block"]);
        if(pairs.Select(pair=>pair.ClusterId).Distinct().Count()!=pairs.Count)
            return Result(Availability.Partial,EffectClass.NotComparable,"blocked",["dependent_cluster_rows"]);
        var values=pairs.SelectMany(pair=>new[]{pair.Baseline,pair.Candidate}).ToArray();
        if(values.Any(value=>value.Metric!=policy.Metric||value.Unit!=policy.Unit||value.ObservationKind!=policy.ObservationKind||value.Scope!=definition.Scope))
            return Result(Availability.Invalid,EffectClass.NotComparable,"blocked",["metric_unit_scope_or_estimand_mismatch"]);
        if(values.Any(value=>value.Status!=Availability.Available))
        {
            var states=values.Select(value=>value.Status).Distinct().ToArray();
            Availability status=states.Length==1?states[0]:Availability.Partial;
            return Result(status,EffectClass.NotComparable,policy.DescriptiveOnly?"descriptive_only":"blocked",
                values.Where(value=>value.Status!=Availability.Available).Select(value=>"metric_unavailable:"+value.Status.ToString()).Distinct().ToArray());
        }
        if(values.Any(value=>value.Value is null||!double.IsFinite(value.Value.Value)||value.Value<0||ratio&&value.Value<=0))
            return Result(Availability.Invalid,EffectClass.Invalid,"invalid",[ratio?"nonpositive_or_nonfinite_cost":"invalid_absolute_cost"]);
        var contrasts=pairs.Select(pair=>ratio?Math.Log(pair.Candidate.Value!.Value)-Math.Log(pair.Baseline.Value!.Value):
            pair.Candidate.Value!.Value-pair.Baseline.Value!.Value).ToArray();
        if(contrasts.Any(value=>!double.IsFinite(value))) return Result(Availability.Invalid,EffectClass.Invalid,"invalid",["contrast_outside_finite_range"]);
        var (mean,se)=StatisticalEngine.Moments(contrasts);
        double estimate=ratio?Math.Exp(mean):mean;
        if(!double.IsFinite(estimate)||ratio&&estimate==0||!double.IsFinite(se))
            return Result(Availability.Invalid,EffectClass.Invalid,"invalid",["effect_outside_finite_range"]);
        if(descriptiveReasons is {Count:>0}) return Result(Availability.Available,EffectClass.Inconclusive,"descriptive_only",descriptiveReasons,estimate,se);
        if(policy.DescriptiveOnly) return Result(Availability.Available,EffectClass.Inconclusive,"descriptive_only",["predeclared_descriptive_objective"],estimate,se);
        if(!independent||pairs.Count<policy.MinimumBlocks)
            return Result(Availability.Available,EffectClass.Inconclusive,"descriptive_only",["insufficient_independent_evidence"],estimate,se);
        if(se==0) return Result(Availability.Available,EffectClass.Inconclusive,"descriptive_only",["degenerate_variance"],estimate,se);
        double df=pairs.Count-1;
        double differenceCritical=StudentT.Quantile(1-alpha/2,df),equivalenceCritical=StudentT.Quantile(1-alpha,df);
        double Transform(double value)=>ratio?Math.Exp(value):value;
        var difference=new Interval(Transform(mean-differenceCritical*se),Transform(mean+differenceCritical*se),1-alpha);
        var equivalence=new Interval(Transform(mean-equivalenceCritical*se),Transform(mean+equivalenceCritical*se),1-2*alpha);
        if(new[]{difference.Lower,difference.Upper,equivalence.Lower,equivalence.Upper}.Any(value=>!double.IsFinite(value))||ratio&&difference.Lower==0)
            return Result(Availability.Invalid,EffectClass.Invalid,"invalid",["interval_outside_finite_range"],estimate,se,df);
        if(difference.Lower==difference.Upper||equivalence.Lower==equivalence.Upper)
            return Result(Availability.Available,EffectClass.Inconclusive,"descriptive_only",["measurement_resolution_floor"],estimate,se,df);
        double lower=ratio?Math.Log(policy.EquivalenceLower):policy.EquivalenceLower;
        double upper=ratio?Math.Log(policy.EquivalenceUpper):policy.EquivalenceUpper;
        double p=2*StudentT.Survival(Math.Abs(mean/se),df);
        double tost=Math.Max(StudentT.Survival((mean-lower)/se,df),StudentT.Cdf((mean-upper)/se,df));
        bool equivalent=tost<alpha&&equivalence.Lower>policy.EquivalenceLower&&equivalence.Upper<policy.EquivalenceUpper;
        bool improved=policy.Direction==MetricDirection.LowerIsBetter?difference.Upper<policy.UsefulImprovement:difference.Lower>policy.UsefulImprovement;
        bool regressed=policy.Direction==MetricDirection.LowerIsBetter?difference.Lower>policy.MeaningfulRegression:difference.Upper<policy.MeaningfulRegression;
        bool noninferior=policy.Direction==MetricDirection.LowerIsBetter?difference.Upper<policy.MeaningfulRegression:difference.Lower>policy.MeaningfulRegression;
        EffectClass effect=improved?EffectClass.Improved:regressed?EffectClass.Regressed:equivalent?EffectClass.EquivalentWithinMargin:EffectClass.Inconclusive;
        return Result(Availability.Available,effect,"usable",effect==EffectClass.Inconclusive?["effect_not_resolved_at_declared_margin"]:[],
            estimate,se,df,difference,equivalence,p,tost,equivalent,noninferior);
    }

    public static (bool Eligible,IReadOnlyList<string> Reasons) Promotion(IReadOnlyList<MetricEffect> effects,bool freshConfirmation)
    {
        if(effects.Count==0) throw new LabException("invalid_request","An objective vector requires its primary effect.");
        var reasons=new List<string>(); var primary=effects[0];
        if(effects.Any(effect=>effect.AlphaFamily!=primary.AlphaFamily||effect.FamilySize!=primary.FamilySize||effect.AlphaLocal!=primary.AlphaLocal)||
            primary.FamilySize<effects.Count(effect=>!effect.Policy.DescriptiveOnly))
            reasons.Add("inconsistent_or_incomplete_objective_family");
        if(!freshConfirmation) reasons.Add("fresh_normal_build_confirmation_required");
        if(primary.EffectClass!=EffectClass.Improved||primary.Quality!="usable"||primary.Availability!=Availability.Available)
            reasons.Add("primary_benefit_not_established");
        foreach(var guardrail in effects.Skip(1).Where(effect=>!effect.Policy.DescriptiveOnly))
            if(!guardrail.NonInferiorityPassed)
                reasons.Add((guardrail.EffectClass==EffectClass.Regressed?"guardrail_regressed:":"guardrail_unknown_or_unresolved:")+guardrail.Metric);
        return(reasons.Count==0,reasons);
    }
    public static ParetoRelation Pareto(IReadOnlyList<MetricEffect> effects)
    {
        if(effects.Count==0||effects.Any(effect=>effect.DifferenceInterval is null||effect.Quality!="usable"||effect.Availability!=Availability.Available)||
            effects.Select(effect=>(effect.AlphaFamily,effect.AlphaLocal,effect.FamilySize)).Distinct().Count()!=1||effects[0].FamilySize<effects.Count)
            return ParetoRelation.Unresolved;
        bool candidate=true,baseline=true,candidateStrict=false,baselineStrict=false;
        foreach(var effect in effects)
        {
            double center=effect.Policy.Scale==EffectScale.PositiveRatio?1:0;
            var interval=effect.DifferenceInterval!;
            bool lower=effect.Policy.Direction==MetricDirection.LowerIsBetter;
            bool candidateNoWorse=lower?interval.Upper<=center:interval.Lower>=center;
            bool baselineNoWorse=lower?interval.Lower>=center:interval.Upper<=center;
            candidate&=candidateNoWorse; baseline&=baselineNoWorse;
            candidateStrict|=lower?interval.Upper<center:interval.Lower>center;
            baselineStrict|=lower?interval.Lower>center:interval.Upper<center;
        }
        return candidate&&candidateStrict?ParetoRelation.CandidateDominates:baseline&&baselineStrict?ParetoRelation.BaselineDominates:ParetoRelation.Unresolved;
    }
}
