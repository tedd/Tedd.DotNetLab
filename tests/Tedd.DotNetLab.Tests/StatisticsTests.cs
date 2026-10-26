using System.Text.Json;
using Tedd.DotNetLab.Analysis.Statistics;
using Tedd.DotNetLab.Contracts;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class StatisticsTests
{
    private static JsonDocument Oracle() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","statistics","golden.json")));
    private static CostPair[] Pairs(double[] logs) => logs.Select((d,i)=>new CostPair(i.ToString(),i.ToString(),100,100*Math.Exp(d))).ToArray();
    [Fact, Case("UT-STAT-004", "FR-033,FR-034", "M2", "Unit")]
    public void Paired_PublicEstimator_MatchesIndependentOracle()
    {
        using var oracle=Oracle();
        foreach(var fixture in oracle.RootElement.GetProperty("fixtures").EnumerateObject())
        {
            var expected=fixture.Value;
            double[] logs=expected.GetProperty("log_ratios").EnumerateArray().Select(x=>x.GetDouble()).ToArray();
            var result=StatisticalEngine.Paired(Pairs(logs),new());
            Assert.InRange(Math.Abs(result.Ratio!.Value-expected.GetProperty("ratio").GetDouble()),0,1e-10);
            Assert.InRange(Math.Abs(result.StandardError!.Value-expected.GetProperty("standard_error").GetDouble()),0,1e-12);
            Assert.InRange(Math.Abs(result.DifferenceIntervalRatio!.Lower-expected.GetProperty("difference_interval")[0].GetDouble()),0,1e-10);
            Assert.InRange(Math.Abs(result.DifferenceIntervalRatio.Upper-expected.GetProperty("difference_interval")[1].GetDouble()),0,1e-10);
            Assert.InRange(Math.Abs(result.EquivalenceIntervalRatio!.Lower-expected.GetProperty("equivalence_interval")[0].GetDouble()),0,1e-10);
            Assert.InRange(Math.Abs(result.EquivalenceIntervalRatio.Upper-expected.GetProperty("equivalence_interval")[1].GetDouble()),0,1e-10);
            Assert.InRange(Math.Abs(Math.Log(result.DifferenceP!.Value)-Math.Log(expected.GetProperty("difference_p").GetDouble())),0,1e-8);
            Assert.InRange(Math.Abs(Math.Log(result.TostP!.Value)-Math.Log(expected.GetProperty("tost_p").GetDouble())),0,1e-8);
            Assert.Equal(fixture.Name switch { "B"=>EffectClass.Improved,"I"=>EffectClass.Inconclusive,_=>EffectClass.EquivalentWithinMargin },result.EffectClass);
            Assert.False(result.PromotionEligible);
        }
    }
    [Fact, Case("UT-STAT-010", "FR-034", "M2", "Unit")]
    public void Nonsignificance_WideInterval_RemainsInconclusive()
    {
        var result=StatisticalEngine.Paired(Pairs([-.08,-.06,-.04,-.02,0,.02,.04,.06,.08,0]),new());
        Assert.Equal(EffectClass.Inconclusive,result.EffectClass);
        Assert.False(result.EquivalencePassed);
        Assert.True(result.DifferenceP>.9);
    }
    [Fact, Case("UT-STAT-008", "FR-033", "M2", "Unit")]
    public void DegenerateOrInsufficientEvidence_DoesNotInventInference()
    {
        var degenerate=StatisticalEngine.Paired(Pairs(new double[10]),new());
        Assert.Equal("descriptive_only",degenerate.Quality);
        Assert.Null(degenerate.DifferenceIntervalRatio);
        Assert.Contains("degenerate_variance",degenerate.ReasonCodes);
        var few=StatisticalEngine.Paired(Pairs([-.01,-.02,-.03]),new());
        Assert.Contains("insufficient_independent_evidence",few.ReasonCodes);
        Assert.Equal(EffectClass.Invalid,StatisticalEngine.Paired([new("1","1",0,1)],new()).EffectClass);
        Assert.Equal(EffectClass.Invalid,StatisticalEngine.Paired([new("1","1",1,double.NaN)],new()).EffectClass);
    }
    [Fact, Case("PROP-STAT-002", "FR-033", "M2", "Property")]
    public void Paired_ScalingAndArmSwap_PreserveMathematicalRelations()
    {
        var pairs=Pairs([-.042,-.041,-.040,-.039,-.038,-.0415,-.0405,-.0395,-.0385,-.040]);
        var original=StatisticalEngine.Paired(pairs,new());
        foreach(double scale in new[]{1e-200,1e-10,1e10,1e200})
        {
            var scaled=StatisticalEngine.Paired(pairs.Select(p=>p with { Baseline=p.Baseline*scale,Candidate=p.Candidate*scale }).ToArray(),new());
            Assert.InRange(Math.Abs(original.Ratio!.Value-scaled.Ratio!.Value),0,1e-12);
        }
        var swapped=StatisticalEngine.Paired(pairs.Select(p=>p with { Baseline=p.Candidate,Candidate=p.Baseline }).ToArray(),new());
        Assert.Equal(EffectClass.Regressed,swapped.EffectClass);
        Assert.InRange(Math.Abs(swapped.Ratio!.Value-1/original.Ratio!.Value),0,1e-12);
        Assert.InRange(Math.Abs(swapped.DifferenceIntervalRatio!.Lower-1/original.DifferenceIntervalRatio!.Upper),0,1e-12);
    }
    [Fact, Case("UT-STAT-016", "FR-035", "M2", "Unit")]
    public void Multiplicity_DeclaredFamily_AllocatesLocalAlpha()
    {
        var pairs=Pairs([-.08,-.06,-.04,-.02,0,.02,.04,.06,.08,0]);
        var ordinary=StatisticalEngine.Paired(pairs,new());
        var family=StatisticalEngine.Paired(pairs,new(FamilySize:5));
        Assert.Equal(.01,family.AlphaLocal,12);
        Assert.Equal(.99,family.DifferenceIntervalRatio!.Coverage);
        Assert.Equal(.98,family.EquivalenceIntervalRatio!.Coverage);
        Assert.True(family.DifferenceIntervalRatio.Lower<ordinary.DifferenceIntervalRatio!.Lower);
        Assert.Throws<LabException>(()=>StatisticalEngine.Paired(pairs,new(FamilySize:0)));
    }
    [Fact, Case("UT-EXP-004", "FR-033", "M2", "Unit")]
    public void RepeatedClusterRows_DoNotIncreaseIndependentEvidence()
    {
        var pairs=Pairs([-.01,-.02,-.03,-.04,-.05,-.06,-.07,-.08,-.09,-.1]);
        var result=StatisticalEngine.Paired(pairs.Select(p=>p with { ClusterId="same-session" }).ToArray(),new());
        Assert.Equal(EffectClass.NotComparable,result.EffectClass);
    }
    [Fact, Case("UT-STAT-019", "FR-033,FR-036", "M2", "Unit")]
    public void QuantilesAndFactorialContrasts_UseDeclaredScale()
    {
        Assert.Equal(2.5,StatisticalEngine.Quantile([1,2,3,4],.5));
        Assert.Equal(3.7,StatisticalEngine.Quantile([1,2,3,4],.9),12);
        Assert.Equal(-11,StatisticalEngine.Interaction(100,104,97,90));
        Assert.Equal(Math.Log(90)-Math.Log(104)-Math.Log(97)+Math.Log(100),StatisticalEngine.Interaction(100,104,97,90,true),12);
    }
    [Fact, Case("UT-STAT-022", "FR-033", "M2", "Unit")]
    public void Bootstrap_WholePairedClusters_IsReproducible()
    {
        var clusters=Enumerable.Range(0,10).Select(i=>new PairedCluster(i.ToString(),new[]{1.0+i,2.0+i},new[]{2.0+i,3.0+i})).ToArray();
        var a=StatisticalEngine.ClusterQuantile(clusters,.9,42,1000);
        var b=StatisticalEngine.ClusterQuantile(clusters,.9,42,1000);
        Assert.Equal(a,b);
        Assert.Equal(1,a.Difference,10);
        Assert.False(a.EquivalenceInferenceSupported);
        Assert.Throws<LabException>(()=>StatisticalEngine.ClusterQuantile(clusters,.9,42,1000,independent:false));
    }
    [Fact, Case("UT-STAT-018", "FR-024,FR-033", "M2", "Unit")]
    public void Additive_ZeroBaseline_DoesNotUseLogOrPercentage()
    {
        var result=StatisticalEngine.AdditivePaired([0,0,0,0],[1,2,3,4]);
        Assert.Equal(2.5,result.Mean);
        Assert.Equal("paired-additive/v1",result.Estimator);
        var welch=StatisticalEngine.WelchLog([100,120,90,110],[80,95,85,90,140,70]);
        Assert.Equal("welch-log-cost/v1",welch.Estimator);
        Assert.True(welch.DegreesOfFreedom>0);
    }
}
