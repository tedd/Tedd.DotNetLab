using Tedd.DotNetLab.Analysis.Statistics;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;

namespace Tedd.DotNetLab.StatisticalQualification;

// Every Normal consumes two uniforms; the second Box-Muller variate is discarded.
// The independent Python oracle reproduces sampling, without importing engine calculations.
public sealed class ModelRandom(ulong seed)
{
    private readonly DeterministicRandom random=new(seed);
    public double Uniform()=>((random.Next()>>12)+.5)/4503599627370496d;
    public double Normal()=>Math.Sqrt(-2*Math.Log(Uniform()))*Math.Cos(2*Math.PI*Uniform());
    public double Student3()=>Normal()/Math.Sqrt((Math.Pow(Normal(),2)+Math.Pow(Normal(),2)+Math.Pow(Normal(),2))/3);
}

public static class Simulation
{
    public static void Validate(SimulationPlan plan)
    {
        if(plan.Version!="statistical-simulation/v1"||plan.Experiments is <10000 or >100000||
            plan.SampleAlgorithm!="splitmix64-open52-boxmuller-discard/v1"||plan.Alpha!=.05||plan.Resamples is <1000 or >100000||
            plan.Quantile!=.5||plan.FramesPerCluster is <2 or >16||plan.AcceptanceMethod!="scipy-binom-equal-tail-bonferroni/v1"||
            plan.AcceptanceFamilyError is <=0 or >=.05||plan.Designs.Count is <1 or >32||plan.Designs.Select(d=>d.Id).Distinct().Count()!=plan.Designs.Count)
            throw new InvalidDataException("Unsupported or incomplete frozen simulation protocol.");
        foreach(var design in plan.Designs)
            if(design.Size is <10 or >100||design.CandidateSize is <10 or >100||design.FamilySize is <1 or >10||
                !new[]{"paired","heavy_tail","outliers","autocorrelation","session_clusters","welch","additive","family","winner","quantile_clusters"}.Contains(design.Kind)||
                !double.IsFinite(design.Mean)||!double.IsFinite(design.Sigma)||design.Sigma<=0||!double.IsFinite(design.CandidateSigma)||design.CandidateSigma<=0||
                design.Criteria.Any(c=>!double.IsFinite(c.Probability)||c.Probability is <0 or >1||c.Minimum<0||c.Maximum<c.Minimum||c.Maximum>plan.Experiments)||
                design.QualifiedAssumptions&&design.Criteria.Count==0||!design.QualifiedAssumptions&&design.Criteria.Count!=0||
                design.Kind=="session_clusters"&&plan.FramesPerCluster>15)
                throw new InvalidDataException("Invalid design or prespecified acceptance envelope.");
    }
    private static readonly string[] CounterNames=["coverage","difference_rejected","equivalent","improved","promotion","usable","blocked","descriptive",
        "naive_coverage","naive_difference_rejected","naive_promotion","naive_quantile_coverage"];
    public static SimulationOutcome Execute(SimulationPlan plan,SimulationDesign design,OracleDesign oracle,Action<int>? progress=null,CancellationToken cancellation=default)
    {
        Validate(plan); var random=new ModelRandom(design.Seed);
        var counters=CounterNames.ToDictionary(name=>name,_=>0,StringComparer.Ordinal); var first=new Dictionary<string,double>();
        var policy=new AnalysisPolicy(FamilySize:design.FamilySize);
        void Snapshot(double estimate,double lower,double upper,double? p=null,double? tost=null)
        {
            if(first.Count!=0) return;
            first.Add("estimate",estimate); first.Add("lower",lower); first.Add("upper",upper);
            if(p.HasValue) first.Add("difference_p",p.Value); if(tost.HasValue) first.Add("tost_p",tost.Value);
        }
        void Count(Comparison result,double truth,bool naive=false)
        {
            string prefix=naive?"naive_":"";
            if(result.DifferenceIntervalRatio is { } interval&&interval.Lower<=truth&&truth<=interval.Upper) counters[prefix+"coverage"]++;
            if(result.DifferenceP<result.AlphaLocal) counters[prefix+"difference_rejected"]++;
            if(result.PromotionEligible) counters[prefix+"promotion"]++;
            if(naive) return;
            if(result.EquivalencePassed) counters["equivalent"]++;
            if(result.EffectClass==EffectClass.Improved) counters["improved"]++;
            if(result.Quality=="usable") counters["usable"]++;
            if(result.Quality=="blocked") counters["blocked"]++;
            if(result.Quality=="descriptive_only") counters["descriptive"]++;
        }
        CostPair[] Pairs(double[] logs)=>logs.Select((d,i)=>new CostPair(i.ToString(),i.ToString(),100,100*Math.Exp(d))).ToArray();
        for(int experiment=0;experiment<plan.Experiments;experiment++)
        {
            cancellation.ThrowIfCancellationRequested();
            if(design.Kind=="welch")
            {
                var baseline=Enumerable.Range(0,design.Size).Select(_=>100*Math.Exp(design.Sigma*random.Normal())).ToArray();
                var candidate=Enumerable.Range(0,design.CandidateSize).Select(_=>100*Math.Exp(design.Mean+design.CandidateSigma*random.Normal())).ToArray();
                var result=StatisticalEngine.WelchLog(baseline,candidate,plan.Alpha); double truth=Math.Exp(design.Mean);
                if(result.Interval.Lower<=truth&&truth<=result.Interval.Upper) counters["coverage"]++;
                if(result.Interval.Lower>1||result.Interval.Upper<1) counters["difference_rejected"]++;
                counters["usable"]++; Snapshot(Math.Exp(result.Mean),result.Interval.Lower,result.Interval.Upper);
            }
            else if(design.Kind=="additive")
            {
                var baseline=Enumerable.Repeat(100d,design.Size).ToArray();
                var candidate=baseline.Select(b=>b+design.Mean+design.Sigma*random.Normal()).ToArray();
                var result=StatisticalEngine.AdditivePaired(baseline,candidate,plan.Alpha);
                if(result.Interval.Lower<=design.Mean&&design.Mean<=result.Interval.Upper) counters["coverage"]++;
                if(result.Interval.Lower>0||result.Interval.Upper<0) counters["difference_rejected"]++;
                counters["usable"]++; Snapshot(result.Mean,result.Interval.Lower,result.Interval.Upper);
            }
            else if(design.Kind is "family" or "winner")
            {
                var arms=Enumerable.Range(0,design.FamilySize).Select(_=>Enumerable.Range(0,design.Size).Select(_=>design.Mean+design.Sigma*random.Normal()).ToArray()).ToArray();
                if(design.Kind=="family")
                {
                    var results=arms.Select(logs=>StatisticalEngine.Paired(Pairs(logs),policy)).ToArray();
                    if(results.All(r=>r.DifferenceIntervalRatio!.Lower<=Math.Exp(design.Mean)&&Math.Exp(design.Mean)<=r.DifferenceIntervalRatio.Upper)) counters["coverage"]++;
                    if(results.Any(r=>r.DifferenceP<r.AlphaLocal)) counters["difference_rejected"]++;
                    counters["usable"]++; var value=results[0]; Snapshot(value.Ratio!.Value,value.DifferenceIntervalRatio!.Lower,value.DifferenceIntervalRatio.Upper,value.DifferenceP,value.TostP);
                }
                else
                {
                    int winner=Enumerable.Range(0,arms.Length).MinBy(i=>arms[i].Average());
                    var naive=StatisticalEngine.Paired(Pairs(arms[winner]),new(),freshConfirmation:true); Count(naive,Math.Exp(design.Mean),true);
                    var exploration=StatisticalEngine.Paired(Pairs(arms[winner]),new(),blockers:["post_selection_exploration"]);
                    if(exploration.Quality=="blocked") counters["blocked"]++;
                    var fresh=Enumerable.Range(0,design.Size).Select(_=>design.Mean+design.Sigma*random.Normal()).ToArray();
                    var result=StatisticalEngine.Paired(Pairs(fresh),new(),freshConfirmation:true); Count(result,Math.Exp(design.Mean));
                    Snapshot(result.Ratio!.Value,result.DifferenceIntervalRatio!.Lower,result.DifferenceIntervalRatio.Upper,result.DifferenceP,result.TostP);
                }
            }
            else if(design.Kind=="quantile_clusters")
            {
                var clusters=Enumerable.Range(0,design.Size).Select(i=>new PairedCluster(i.ToString(),Enumerable.Repeat(100d,plan.FramesPerCluster).ToArray(),
                    Enumerable.Repeat(100+design.Mean+design.Sigma*random.Normal(),plan.FramesPerCluster).ToArray())).ToArray();
                ulong seed=design.Seed+(ulong)experiment;
                var result=StatisticalEngine.ClusterQuantile(clusters,plan.Quantile,seed,plan.Resamples,plan.Alpha,cancellation:cancellation);
                var naiveRows=clusters.SelectMany((cluster,i)=>cluster.Candidate.Select((value,j)=>new PairedCluster($"{i}/{j}",new[]{100d},new[]{value}))).ToArray();
                var naive=StatisticalEngine.ClusterQuantile(naiveRows,plan.Quantile,seed,plan.Resamples,plan.Alpha,cancellation:cancellation);
                if(result.Interval.Lower<=design.Mean&&design.Mean<=result.Interval.Upper) counters["coverage"]++;
                if(naive.Interval.Lower<=design.Mean&&design.Mean<=naive.Interval.Upper) counters["naive_quantile_coverage"]++;
                if(result.Interval.Lower==result.Interval.Upper) counters["descriptive"]++; else counters["usable"]++;
                Snapshot(result.Difference,result.Interval.Lower,result.Interval.Upper);
            }
            else
            {
                double[] logs=new double[design.Size];
                if(design.Kind=="session_clusters")
                {
                    for(int i=0;i<logs.Length;i++)
                    {
                        double session=design.Sigma*random.Normal(); string sessionId=i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        var blocks=new CostPair[plan.FramesPerCluster];
                        for(int j=0;j<blocks.Length;j++) blocks[j]=new(j.ToString(System.Globalization.CultureInfo.InvariantCulture),sessionId,100,
                            100*Math.Exp(design.Mean+session+design.CandidateSigma*random.Normal()));
                        var aggregate=NativeSessionEngine.Aggregate(sessionId,blocks);
                        logs[i]=Math.Log(aggregate.Candidate)-Math.Log(aggregate.Baseline);
                    }
                }
                else if(design.Kind=="autocorrelation")
                {
                    double previous=random.Normal();
                    for(int i=0;i<logs.Length;i++) { previous=.8*previous+.6*random.Normal(); logs[i]=design.Mean+design.Sigma*previous; }
                }
                else for(int i=0;i<logs.Length;i++)
                {
                    double value=design.Kind=="heavy_tail"?random.Student3():random.Normal();
                    if(design.Kind=="outliers"&&random.Uniform()<.01) value+=random.Uniform()<.5?-50:50;
                    logs[i]=design.Mean+design.Sigma*value;
                }
                var pairs=Pairs(logs);
                if(!design.QualifiedAssumptions)
                {
                    var naive=StatisticalEngine.Paired(pairs,policy); Count(naive,Math.Exp(design.Mean),true);
                    Snapshot(naive.Ratio!.Value,naive.DifferenceIntervalRatio!.Lower,naive.DifferenceIntervalRatio.Upper,naive.DifferenceP,naive.TostP);
                    var guarded=StatisticalEngine.Paired(pairs,policy,independent:design.Kind!="autocorrelation",
                        blockers:design.Kind=="autocorrelation"?null:["modeled_distribution_assumption_not_qualified"]);
                    Count(guarded,Math.Exp(design.Mean));
                }
                else
                {
                    var result=StatisticalEngine.Paired(pairs,policy); Count(result,Math.Exp(design.Mean));
                    Snapshot(result.Ratio!.Value,result.DifferenceIntervalRatio!.Lower,result.DifferenceIntervalRatio.Upper,result.DifferenceP,result.TostP);
                }
            }
            if((experiment+1)%1000==0) progress?.Invoke(experiment+1);
        }
        return Assess(plan,design,oracle,counters,first);
    }
    public static SimulationOutcome Assess(SimulationPlan plan,SimulationDesign design,OracleDesign oracle,IReadOnlyDictionary<string,int> counters,IReadOnlyDictionary<string,double> first)
    {
        Validate(plan);
        if(CounterNames.Any(name=>!counters.TryGetValue(name,out int count)||count<0||count>plan.Experiments)||counters.Count!=CounterNames.Length||
            first.Values.Any(value=>!double.IsFinite(value))) throw new InvalidDataException("Simulation counts or numerical outputs are incomplete or invalid.");
        var checks=design.Criteria.Select(c=>new CriterionCheck(c.Counter,counters[c.Counter],c.Probability,c.Minimum,c.Maximum,
            counters[c.Counter]>=c.Minimum&&counters[c.Counter]<=c.Maximum)).ToArray();
        bool countsAgree=oracle.Id==design.Id&&oracle.Counters.Count==counters.Count&&oracle.Counters.All(c=>counters.TryGetValue(c.Key,out int actual)&&actual==c.Value);
        bool firstAgrees=oracle.First.Count==first.Count&&oracle.First.All(value=>first.TryGetValue(value.Key,out double actual)&&
            Math.Abs(actual-value.Value)<=1e-10*Math.Max(1,Math.Abs(value.Value)));
        bool assumptionGate=design.QualifiedAssumptions||counters["usable"]==0&&counters["promotion"]==0;
        bool passed=countsAgree&&firstAgrees&&checks.All(c=>c.Passed)&&assumptionGate;
        return new(design,plan.Experiments,design.QualifiedAssumptions?passed?"qualified_for_frozen_model":"qualification_failed":"diagnostic_only",
            counters,first,checks,countsAgree,firstAgrees,passed);
    }
}
