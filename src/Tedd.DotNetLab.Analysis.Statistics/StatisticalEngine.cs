using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;

namespace Tedd.DotNetLab.Analysis.Statistics;

public record CostPair(string BlockId,string ClusterId,double Baseline,double Candidate,string BaselineRunId="",string CandidateRunId="");
public record Estimate(double Mean,double StandardError,double DegreesOfFreedom,Interval Interval,string Estimator,int IndependentUnits);
public record PairedCluster(string Id,IReadOnlyList<double> Baseline,IReadOnlyList<double> Candidate);
public record BootstrapResult(double Difference,Interval Interval,string Estimator,int Clusters,int Resamples,
    string RandomAlgorithm,ulong Seed,bool EquivalenceInferenceSupported = false);

public static class StatisticalEngine
{
    public const string Version="paired-log-ratio/v2";
    public static double Sum(IEnumerable<double> values)
    {
        double total=0,compensation=0;
        foreach(double value in values)
        {
            double y=value-compensation, next=total+y;
            compensation=(next-total)-y; total=next;
        }
        return total;
    }
    public static double Mean(IReadOnlyList<double> values)
    {
        Validate(values);
        return Sum(values.Select(x=>x/values.Count));
    }
    internal static (double Mean,double Se) Moments(IReadOnlyList<double> values)
    {
        double mean=Mean(values);
        if(values.Count<2) return(mean,0);
        double scale=values.Max(x=>Math.Abs(x-mean));
        if(scale==0) return(mean,0);
        double variance=Sum(values.Select(x=>Math.Pow((x-mean)/scale,2)))/(values.Count-1);
        return(mean,scale*Math.Sqrt(variance/values.Count));
    }
    private static void Validate(IReadOnlyList<double> values)
    {
        if(values.Count==0||values.Count>1000000||values.Any(x=>!double.IsFinite(x)))
            throw new LabException("invalid_statistical_input","Expected bounded finite observations.");
    }
    public static double Quantile(IReadOnlyList<double> values,double p)
    {
        Validate(values);
        if(!double.IsFinite(p)||p<0||p>1) throw new LabException("invalid_statistical_input","Invalid quantile probability.");
        var sorted=values.Order().ToArray();
        double h=(sorted.Length-1)*p;
        int lower=(int)Math.Floor(h), upper=(int)Math.Ceiling(h);
        double fraction=h-lower;
        return sorted[lower]*(1-fraction)+sorted[upper]*fraction;
    }
    public static Comparison Paired(IReadOnlyList<CostPair> pairs,AnalysisPolicy policy,bool independent=true,
        bool freshConfirmation=false,IReadOnlyList<string>? blockers=null)
    {
        Policies.Validate(policy);
        double alpha=policy.AlphaFamily/policy.FamilySize;
        var runIds=pairs.SelectMany(p=>new[]{p.BaselineRunId,p.CandidateRunId}).Where(s=>s.Length>0).ToArray();
        Comparison Result(EffectClass effect,string quality,string[] reasons,double? ratio=null,double? change=null,
            double? se=null,double? df=null,Interval? difference=null,Interval? equivalence=null,
            double? differenceP=null,double? tostP=null,bool passed=false,bool promotion=false) =>
            new(Wire.Id("cmp"),Version,effect,quality,independent?pairs.Select(pair=>pair.ClusterId).Distinct().Count():0,ratio,change,se,df,difference,equivalence,
                differenceP,tostP,passed,alpha,promotion,reasons,runIds);
        if(blockers is {Count:>0}) return Result(EffectClass.NotComparable,"blocked",blockers.ToArray());
        if(pairs.Count==0||pairs.Count>100000||
            pairs.Any(p=>!double.IsFinite(p.Baseline)||!double.IsFinite(p.Candidate)||p.Baseline<=0||p.Candidate<=0))
            return Result(EffectClass.Invalid,"invalid",["nonpositive_or_nonfinite_cost"]);
        if(pairs.Select(p=>p.BlockId).Distinct().Count()!=pairs.Count)
            return Result(EffectClass.Invalid,"invalid",["duplicate_block"]);
        if(pairs.Select(p=>p.ClusterId).Distinct().Count()!=pairs.Count)
            return Result(EffectClass.NotComparable,"blocked",["dependent_cluster_rows"]);
        var logs=pairs.Select(p=>Math.Log(p.Candidate)-Math.Log(p.Baseline)).ToArray();
        var (mean,se)=Moments(logs);
        double ratio=Math.Exp(mean),change=100*(ratio-1);
        if(!double.IsFinite(ratio)||ratio==0) return Result(EffectClass.Invalid,"invalid",["effect_outside_finite_range"]);
        if(!independent||pairs.Count<policy.MinimumBlocks)
            return Result(EffectClass.Inconclusive,"descriptive_only",["insufficient_independent_evidence"],ratio,change,se);
        if(se==0) return Result(EffectClass.Inconclusive,"descriptive_only",["degenerate_variance"],ratio,change,se);
        double df=pairs.Count-1;
        double criticalDifference=StudentT.Quantile(1-alpha/2,df),criticalEquivalence=StudentT.Quantile(1-alpha,df);
        var difference=new Interval(Math.Exp(mean-criticalDifference*se),Math.Exp(mean+criticalDifference*se),1-alpha);
        var equivalence=new Interval(Math.Exp(mean-criticalEquivalence*se),Math.Exp(mean+criticalEquivalence*se),1-2*alpha);
        if(!double.IsFinite(difference.Upper)||difference.Lower==0)
            return Result(EffectClass.Invalid,"invalid",["interval_outside_finite_range"],ratio,change,se,df);
        if(difference.Lower==difference.Upper||equivalence.Lower==equivalence.Upper)
            return Result(EffectClass.Inconclusive,"descriptive_only",["measurement_resolution_floor"],ratio,change,se,df);
        double p=2*StudentT.Survival(Math.Abs(mean/se),df);
        double tost=Math.Max(StudentT.Survival((mean-Math.Log(policy.EquivalenceLowerRatio))/se,df),
            StudentT.Cdf((mean-Math.Log(policy.EquivalenceUpperRatio))/se,df));
        bool passed=tost<alpha && equivalence.Lower>policy.EquivalenceLowerRatio && equivalence.Upper<policy.EquivalenceUpperRatio;
        var effect=difference.Upper<policy.UsefulImprovementRatio ? EffectClass.Improved :
            difference.Lower>policy.MeaningfulRegressionRatio ? EffectClass.Regressed :
            passed ? EffectClass.EquivalentWithinMargin : EffectClass.Inconclusive;
        return Result(effect,"usable",effect==EffectClass.Inconclusive?["effect_not_resolved_at_declared_margin"]:[],
            ratio,change,se,df,difference,equivalence,p,tost,passed,
            freshConfirmation && effect==EffectClass.Improved);
    }
    public static Estimate AdditivePaired(IReadOnlyList<double> baseline,IReadOnlyList<double> candidate,double alpha=.05)
    {
        Validate(baseline); Validate(candidate);
        if(baseline.Count!=candidate.Count||baseline.Count<2||alpha<=0||alpha>=.5)
            throw new LabException("invalid_statistical_input","Paired additive observations or alpha are invalid.");
        var differences=baseline.Zip(candidate,(b,c)=>c-b).ToArray();
        var (mean,se)=Moments(differences);
        if(se==0) throw new LabException("degenerate_variance","Additive inference requires nonzero variance.");
        double df=baseline.Count-1,half=StudentT.Quantile(1-alpha/2,df)*se;
        return new(mean,se,df,new(mean-half,mean+half,1-alpha),"paired-additive/v1",baseline.Count);
    }
    public static Estimate WelchLog(IReadOnlyList<double> baseline,IReadOnlyList<double> candidate,double alpha=.05)
    {
        Validate(baseline); Validate(candidate);
        if(baseline.Count<2||candidate.Count<2||baseline.Concat(candidate).Any(v=>v<=0)||alpha<=0||alpha>=.5)
            throw new LabException("invalid_statistical_input","Positive unpaired samples and valid alpha are required.");
        var b=Moments(baseline.Select(x=>Math.Log(x)).ToArray());
        var c=Moments(candidate.Select(x=>Math.Log(x)).ToArray());
        double vb=b.Se*b.Se,vc=c.Se*c.Se,se=Math.Sqrt(vb+vc);
        if(se==0) throw new LabException("degenerate_variance","Welch inference requires nonzero variance.");
        double df=(vb+vc)*(vb+vc)/(vb*vb/(baseline.Count-1)+vc*vc/(candidate.Count-1));
        double mean=c.Mean-b.Mean,half=StudentT.Quantile(1-alpha/2,df)*se;
        return new(mean,se,df,new(Math.Exp(mean-half),Math.Exp(mean+half),1-alpha),"welch-log-cost/v1",baseline.Count+candidate.Count);
    }
    public static BootstrapResult ClusterQuantile(IReadOnlyList<PairedCluster> clusters,double p,ulong seed,
        int resamples=10000,double alpha=.05,bool independent=true,CancellationToken cancellation=default)
    {
        if(!independent||clusters.Count<10||clusters.Select(c=>c.Id).Distinct().Count()!=clusters.Count)
            throw new LabException("insufficient_independent_evidence","At least ten independent paired clusters are required.");
        if(resamples is <1000 or >100000||!double.IsFinite(alpha)||!double.IsFinite(p)||alpha<=0||alpha>=.5||p<0||p>1)
            throw new LabException("invalid_plan","Invalid bootstrap configuration.");
        foreach(var cluster in clusters) { Validate(cluster.Baseline); Validate(cluster.Candidate); }
        if(clusters.Sum(c=>c.Baseline.Count+c.Candidate.Count)>100000)
            throw new LabException("resource_limit","Bootstrap observations exceed policy.");
        double Statistic(IEnumerable<PairedCluster> data)
        {
            var selected=data.ToArray();
            return Quantile(selected.SelectMany(c=>c.Candidate).ToArray(),p)-Quantile(selected.SelectMany(c=>c.Baseline).ToArray(),p);
        }
        var random=new DeterministicRandom(seed);
        var estimates=new double[resamples];
        for(int sample=0;sample<resamples;sample++)
        {
            cancellation.ThrowIfCancellationRequested();
            var selected=new PairedCluster[clusters.Count];
            for(int j=0;j<clusters.Count;j++) selected[j]=clusters[random.Index(clusters.Count)];
            estimates[sample]=Statistic(selected);
        }
        return new(Statistic(clusters),new(Quantile(estimates,alpha/2),Quantile(estimates,1-alpha/2),1-alpha),
            "paired-cluster-quantile-percentile/v1",clusters.Count,resamples,"splitmix64/rejection-v1",seed);
    }
    public static double Interaction(double cost00,double cost10,double cost01,double cost11,bool logScale=false)
    {
        double[] values=[cost00,cost10,cost01,cost11]; Validate(values);
        if(logScale && values.Any(v=>v<=0)) throw new LabException("invalid_statistical_input","Log interactions require positive costs.");
        if(logScale) values=values.Select(x=>Math.Log(x)).ToArray();
        return Sum([values[3],-values[1],-values[2],values[0]]);
    }
}
