using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.StatisticalQualification;
using Xunit;
using System.Security.Cryptography;

namespace Tedd.DotNetLab.Tests;

public sealed class SimulationQualificationTests
{
    private static SimulationPlan Plan()=>Wire.Deserialize<SimulationPlan>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","statistics","simulation-plan.json")));
    [Fact,Case("UT-STAT-QUALIFICATION-NATIVE-SESSIONS","FR-032,FR-033,FR-034","M6","Reference")]
    public void NativeSessionQualificationRetainsTenThousandTrialsAndExactIndependentSource()
    {
        string root=Path.Combine(AppContext.BaseDirectory,"Fixtures","statistics");
        string Hash(string file)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root,file))));
        var plan=Plan(); var report=Wire.Deserialize<QualificationReport>(File.ReadAllText(Path.Combine(root,"native-series-simulation-results.json")));
        var oracle=Wire.Deserialize<SimulationOracle>(File.ReadAllText(Path.Combine(root,"native-series-simulation-oracle.json")));
        Assert.False(report.Passed); Assert.False(report.Complete); Assert.Equal(10000,report.RequiredExperiments);
        Assert.Equal(Hash("simulation-plan.json"),report.PlanHash); Assert.Equal(report.PlanHash,oracle.PlanHash);
        Assert.Equal(Hash("native-series-simulation-oracle.json"),report.OracleHash); Assert.Equal(Hash("simulation_reference.py"),oracle.GeneratorHash);
        var outcome=Assert.Single(report.Outcomes); var design=plan.Designs.Single(d=>d.Id=="session_cluster_means");
        Assert.Equal(Wire.Hash(design),Wire.Hash(outcome.Design)); Assert.Equal(10000,outcome.Experiments); Assert.True(outcome.Passed);
        Assert.True(Simulation.Assess(plan,design,Assert.Single(oracle.Designs),outcome.Counters,outcome.First).Passed);
    }
    [Fact,Case("UT-STAT-QUALIFICATION-EVIDENCE","FR-033,FR-034,FR-035","M2","Reference")]
    public void RetainedEnsemblesAreCompleteAndBoundToFrozenCriteriaAndIndependentSource()
    {
        string root=Path.Combine(AppContext.BaseDirectory,"Fixtures","statistics");
        string Hash(string file)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root,file))));
        var plan=Plan(); var oracle=Wire.Deserialize<SimulationOracle>(File.ReadAllText(Path.Combine(root,"simulation-oracle.json")));
        var report=Wire.Deserialize<QualificationReport>(File.ReadAllText(Path.Combine(root,"simulation-results.json")));
        Assert.Equal(Hash("simulation-plan.json"),report.PlanHash); Assert.Equal(report.PlanHash,oracle.PlanHash);
        Assert.Equal(Hash("simulation-oracle.json"),report.OracleHash); Assert.Equal(Hash("simulation_reference.py"),oracle.GeneratorHash);
        Assert.True(report.Complete&&report.Passed); Assert.Equal(plan.Designs.Count,report.Outcomes.Count);
        Assert.Equal(plan.Designs.Select(d=>d.Id),report.Outcomes.Select(o=>o.Design.Id));
        foreach(var outcome in report.Outcomes)
        {
            var design=plan.Designs.Single(d=>d.Id==outcome.Design.Id); Assert.Equal(Wire.Hash(design),Wire.Hash(outcome.Design));
            Assert.Equal(10000,outcome.Experiments); Assert.True(outcome.Passed);
            var checkedOutcome=Simulation.Assess(plan,design,oracle.Designs.Single(d=>d.Id==design.Id),outcome.Counters,outcome.First);
            Assert.True(checkedOutcome.Passed); Assert.Equal(outcome.Assessment,checkedOutcome.Assessment);
        }
        var dependent=report.Outcomes.Single(o=>o.Design.Kind=="autocorrelation");
        Assert.Equal("diagnostic_only",dependent.Assessment); Assert.Equal(0,dependent.Counters["usable"]);
        var clustered=report.Outcomes.Single(o=>o.Design.Kind=="quantile_clusters");
        Assert.True(clustered.Counters["coverage"]>clustered.Counters["naive_quantile_coverage"]);
    }
    private static Dictionary<string,int> Counts()=>new()
    {
        ["coverage"]=9500,["difference_rejected"]=500,["equivalent"]=0,["improved"]=0,["promotion"]=0,["usable"]=10000,["blocked"]=0,["descriptive"]=0,
        ["naive_coverage"]=0,["naive_difference_rejected"]=0,["naive_promotion"]=0,["naive_quantile_coverage"]=0
    };
    [Fact,Case("UT-STAT-QUALIFICATION-PLAN","FR-033,FR-034,FR-035","M2","Unit")]
    public void QualificationRequiresFrozenTenThousandExperimentDesignsAndPrespecifiedEnvelopes()
    {
        var plan=Plan(); Simulation.Validate(plan); Assert.Equal(16,plan.Designs.Count);
        Assert.Throws<InvalidDataException>(()=>Simulation.Validate(plan with { Experiments=9999 }));
        Assert.Throws<InvalidDataException>(()=>Simulation.Validate(plan with { Designs=[plan.Designs[0] with { Criteria=[] }] }));
        Assert.Throws<InvalidDataException>(()=>Simulation.Validate(plan with { SampleAlgorithm="System.Random" }));
    }
    [Fact,Case("UT-STAT-QUALIFICATION-GATE","FR-033,FR-034","M2","Unit")]
    public void NumericalOracleAgreementCannotOverrideFailedCoverage()
    {
        var plan=Plan(); var design=plan.Designs.Single(d=>d.Id=="normal_null"); var counters=Counts(); counters["coverage"]=9000;
        var first=new Dictionary<string,double> { ["estimate"]=1,["lower"]=.99,["upper"]=1.01 };
        var outcome=Simulation.Assess(plan,design,new(design.Id,counters,first),counters,first);
        Assert.True(outcome.OracleCountsAgree&&outcome.OracleFirstAgrees); Assert.False(outcome.Passed); Assert.Equal("qualification_failed",outcome.Assessment);
        Assert.Contains(outcome.Checks,c=>c.Counter=="coverage"&&!c.Passed);
    }
    [Fact,Case("UT-STAT-QUALIFICATION-ASSUMPTIONS","FR-032,FR-033,FR-035","M2","Unit")]
    public void ViolatedAssumptionsCannotBecomeQualifiedAfterFavorableNaiveCoverage()
    {
        var plan=Plan(); var design=plan.Designs.Single(d=>d.Id=="ar1_unresolved_dependence"); var counters=Counts();
        counters["coverage"]=0; counters["difference_rejected"]=0; counters["naive_coverage"]=9500; counters["usable"]=0; counters["descriptive"]=10000;
        var first=new Dictionary<string,double> { ["estimate"]=1,["lower"]=.99,["upper"]=1.01 };
        var diagnostic=Simulation.Assess(plan,design,new(design.Id,counters,first),counters,first);
        Assert.True(diagnostic.Passed); Assert.Equal("diagnostic_only",diagnostic.Assessment); Assert.Empty(diagnostic.Checks);
        counters["usable"]=10000;
        var falseInference=Simulation.Assess(plan,design,new(design.Id,counters,first),counters,first); Assert.False(falseInference.Passed);
    }
    [Fact,Case("UT-STAT-QUALIFICATION-ORACLE","FR-033,FR-074","M2","Unit")]
    public void PassingEnvelopeDoesNotExcuseMissingOrMismatchedIndependentEvidence()
    {
        var plan=Plan(); var design=plan.Designs.Single(d=>d.Id=="normal_null"); var counts=Counts();
        var first=new Dictionary<string,double> { ["estimate"]=1,["lower"]=.99,["upper"]=1.01 };
        var independent=Counts(); independent["difference_rejected"]=501;
        var outcome=Simulation.Assess(plan,design,new(design.Id,independent,first),counts,first);
        Assert.True(outcome.Checks.All(c=>c.Passed)); Assert.False(outcome.OracleCountsAgree); Assert.False(outcome.Passed);
        counts.Remove("promotion"); Assert.Throws<InvalidDataException>(()=>Simulation.Assess(plan,design,new(design.Id,independent,first),counts,first));
    }
}
