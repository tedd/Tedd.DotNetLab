using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;

namespace Tedd.DotNetLab.Analysis.Statistics;

public static class FactorialEngine
{
    public const string Version="matched-four-arm-interaction/v1";
    public static InteractionEstimate Interaction(MetricPolicy policy,ContrastScale scale,AnalysisPolicy analysis,
        IReadOnlyList<FactorialMetricBlock> blocks,bool independent,IReadOnlyList<string>? blockers=null)
    {
        Policies.Validate(policy); Policies.Validate(analysis);
        if(!Enum.IsDefined(scale)||scale==ContrastScale.Log&&policy.Scale!=EffectScale.PositiveRatio)
            throw new LabException("invalid_plan","Log interaction requires a qualified positive-cost metric.");
        if(analysis.FamilySize<6&&!policy.DescriptiveOnly) throw new LabException("invalid_plan","The factorial family must cover five pair contrasts and interaction.");
        var definition=Metrics.Define(policy.Metric); var ids=blocks.SelectMany(block=>block.RunIds).ToArray();
        double alpha=analysis.AlphaFamily/analysis.FamilySize;
        InteractionEstimate Result(Availability availability,string quality,IReadOnlyList<string> reasons,double? mean=null,double? se=null,
            double? df=null,Interval? interval=null,double? p=null)=>new(policy.Metric,scale==ContrastScale.Log?"log_ratio_contrast":policy.Unit,
                scale,Version,availability,quality,blocks.Count,independent?blocks.Select(block=>block.ClusterId).Distinct().Count():0,mean,se,df,
                interval,p,analysis.AlphaFamily,alpha,analysis.FamilySize,reasons,ids);
        if(blockers is {Count:>0}) return Result(Availability.Invalid,"blocked",blockers.Distinct().ToArray());
        if(blocks.Count is <1 or >10000||blocks.Any(block=>block.RunIds.Count!=4)||ids.Distinct().Count()!=ids.Length||
            blocks.Select(block=>block.BlockId).Distinct().Count()!=blocks.Count)
            return Result(Availability.Invalid,"invalid",["incomplete_or_duplicate_four_arm_blocks"]);
        if(blocks.Select(block=>block.ClusterId).Distinct().Count()!=blocks.Count)
            return Result(Availability.Partial,"blocked",["dependent_cluster_rows"]);
        var values=blocks.SelectMany(block=>new[]{block.Value00,block.Value10,block.Value01,block.Value11}).ToArray();
        if(values.Any(value=>value.Metric!=policy.Metric||value.Unit!=policy.Unit||value.ObservationKind!=policy.ObservationKind||value.Scope!=definition.Scope))
            return Result(Availability.Invalid,"blocked",["metric_unit_scope_or_estimand_mismatch"]);
        if(values.Any(value=>value.Status!=Availability.Available))
        {
            var states=values.Select(value=>value.Status).Distinct().ToArray();
            return Result(states.Length==1?states[0]:Availability.Partial,policy.DescriptiveOnly?"descriptive_only":"blocked",["metric_unavailable"]);
        }
        if(values.Any(value=>value.Value is null||!double.IsFinite(value.Value.Value)||value.Value<0||scale==ContrastScale.Log&&value.Value<=0))
            return Result(Availability.Invalid,"invalid",["invalid_interaction_cost"]);
        var contrasts=blocks.Select(block=>StatisticalEngine.Interaction(block.Value00.Value!.Value,block.Value10.Value!.Value,
            block.Value01.Value!.Value,block.Value11.Value!.Value,scale==ContrastScale.Log)).ToArray();
        if(contrasts.Any(value=>!double.IsFinite(value))) return Result(Availability.Invalid,"invalid",["contrast_outside_finite_range"]);
        var (mean,se)=StatisticalEngine.Moments(contrasts);
        if(!double.IsFinite(mean)||!double.IsFinite(se)) return Result(Availability.Invalid,"invalid",["effect_outside_finite_range"]);
        if(policy.DescriptiveOnly) return Result(Availability.Available,"descriptive_only",["predeclared_descriptive_objective"],mean,se);
        if(!independent||blocks.Count<policy.MinimumBlocks) return Result(Availability.Available,"descriptive_only",["insufficient_independent_evidence"],mean,se);
        if(se==0) return Result(Availability.Available,"descriptive_only",["degenerate_variance"],mean,se);
        double df=blocks.Count-1,half=StudentT.Quantile(1-alpha/2,df)*se;
        var interval=new Interval(mean-half,mean+half,1-alpha);
        if(!double.IsFinite(interval.Lower)||!double.IsFinite(interval.Upper)) return Result(Availability.Invalid,"invalid",["interval_outside_finite_range"],mean,se,df);
        if(interval.Lower==interval.Upper) return Result(Availability.Available,"descriptive_only",["measurement_resolution_floor"],mean,se,df);
        return Result(Availability.Available,"usable",[],mean,se,df,interval,2*StudentT.Survival(Math.Abs(mean/se),df));
    }
}
