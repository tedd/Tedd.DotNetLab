using System.Diagnostics.Metrics;
using System.Diagnostics.Tracing;
using System.Reflection;
using Tedd.DotNetLab.Analysis.Runtime;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class CounterTests
{
    private static CounterPoint Point(string kind,double time,double value,double seconds=1,string observation="interval_mean")=>
        new("metric",kind,"items",time,seconds,value,Observation:observation);
    [Fact,Case("UT-MET-013","FR-025","M1","Unit")]
    public void IdenticalNumbers_HaveDifferentGaugeIncrementRateAndCumulativeAggregations()
    {
        var gauge=CounterAnalyzer.Analyze([Point("gauge",1000,10),Point("gauge",3000,20,2)],"metric",0,3000);
        Assert.Equal(50d/3,gauge.Value!.Value,10); Assert.Equal(Availability.Available,gauge.Status);
        var increment=CounterAnalyzer.Analyze([Point("interval_increment",1000,10),Point("interval_increment",3000,20,2)],"metric",0,3000);
        Assert.Equal(30,increment.Value);
        var rate=CounterAnalyzer.Analyze([Point("rate",1000,10,observation:"interval_rate_per_second"),Point("rate",3000,20,2,"interval_rate_per_second")],"metric",0,3000);
        Assert.Equal(50d/3,rate.Value!.Value,10); Assert.NotEqual(rate.Aggregation,gauge.Aggregation);
        var cumulative=CounterAnalyzer.Analyze([Point("cumulative_counter",0,10),Point("cumulative_counter",1000,20),Point("cumulative_counter",3000,30,2)],"metric",0,3000);
        Assert.Equal(20,cumulative.Value); Assert.Equal(3000,cumulative.CoveredMilliseconds);
    }
    [Theory,InlineData("gauge"),InlineData("interval_increment"),InlineData("cumulative_counter"),Case("UT-MET-011","FR-025","M1","Unit")]
    public void CoarseCounter_CannotSupplyAnExactTwoMillisecondRunValue(string kind)
    {
        var result=CounterAnalyzer.Analyze([Point(kind,1000,50),Point(kind,2000,75)],"metric",1499,1501);
        Assert.Equal(Availability.Partial,result.Status); Assert.Null(result.Value); Assert.Contains("insufficient_temporal_resolution",result.Warnings);
    }
    [Fact,Case("UT-MET-012","FR-025","M1","Unit")]
    public void Reset_ExcludesNegativeIntervalAndPreservesValidObservedChange()
    {
        var result=CounterAnalyzer.Analyze([Point("cumulative_counter",0,10),Point("cumulative_counter",1000,20),Point("cumulative_counter",2000,2),Point("cumulative_counter",3000,7)],"metric",0,3000);
        Assert.Equal(15,result.Value); Assert.Equal(2000,result.CoveredMilliseconds); Assert.Equal(1,result.ResetIntervals); Assert.Equal(Availability.Partial,result.Status);
        Assert.Contains("counter_reset_interval_excluded",result.Warnings);
    }
    [Fact,Case("UT-MET-014","FR-021,FR-025","M1","Unit")]
    public void MissingCounter_DiffersFromACompleteObservedZero()
    {
        var missing=CounterAnalyzer.Analyze([],"metric",0,1000); Assert.Equal(Availability.NotCollected,missing.Status); Assert.Null(missing.Value);
        var zero=CounterAnalyzer.Analyze([Point("interval_increment",1000,0)],"metric",0,1000); Assert.Equal(Availability.Available,zero.Status); Assert.Equal(0,zero.Value);
    }
    [Fact,Case("UT-MET-013","FR-013,FR-025","M1","Unit")]
    public void MixedSeriesDuplicatesOverlapAndInvalidValues_AreRejected()
    {
        var a=Point("gauge",1000,10); var b=Point("gauge",2000,20) with { Source="other" };
        Assert.Equal(Availability.Invalid,CounterAnalyzer.Analyze([a,b],"metric",0,2000).Status);
        Assert.Equal(Availability.Available,CounterAnalyzer.Analyze([a,b],"metric",0,1000,source:a.Source).Status);
        Assert.Equal(Availability.Invalid,CounterAnalyzer.Analyze([a,a],"metric",0,2000).Status);
        Assert.Equal(Availability.Invalid,CounterAnalyzer.Analyze([a,Point("gauge",1500,20)],"metric",0,2000).Status);
        Assert.Equal(Availability.Invalid,CounterAnalyzer.Analyze([a with { Value=double.NaN }],"metric",0,2000).Status);
    }
    [Fact,Case("UT-MET-011","FR-025","M1","Unit")]
    public void PartialBoundary_IsNotProratedAndInstantaneousGaugeIsLabeled()
    {
        var partial=CounterAnalyzer.Analyze([Point("interval_increment",1000,10),Point("interval_increment",2000,20)],"metric",500,2000);
        Assert.Equal(20,partial.Value); Assert.Equal(1000,partial.CoveredMilliseconds); Assert.Equal(Availability.Partial,partial.Status);
        var gauge=CounterAnalyzer.Analyze([Point("gauge",1000,10,observation:"instantaneous"),Point("gauge",2000,20,observation:"instantaneous")],"metric",0,3000);
        Assert.Equal(15,gauge.Value); Assert.Equal(Availability.Partial,gauge.Status); Assert.Equal("reported_gauge_sample_mean/v1",gauge.Aggregation);
        Assert.Equal(0,gauge.CoveredMilliseconds);
    }
    [Fact,Case("UT-MET-013","FR-025","M1","Unit")]
    public void LegacyAdapter_RetainsIncrementInsteadOfTreatingDisplayRateAsPerSecond()
    {
        var point=EventCounterAdapter.Parse(new Dictionary<string,object> { ["Name"]="rate-display",["Increment"]=30d,["IntervalSec"]=3d,["DisplayUnits"]="events" },3000);
        Assert.Equal("interval_increment",point.Kind); Assert.Equal(30,point.Value); Assert.Equal(3,point.IntervalSeconds);
    }
    private static Dictionary<string,object> Metadata()=>new()
    {
        ["sessionId"]="session",["meterName"]="System.Runtime",["meterVersion"]="10",["instrumentName"]="metric",
        ["instrumentType"]="ObservableCounter`1",["unit"]="items",["meterScopeHash"]="scope",["instrumentId"]=1
    };
    [Fact,Case("UT-MET-013,UT-MET-017","FR-025,FR-032","M1","Unit")]
    public void ModernAdapter_PreservesTotalsDeltasSeriesTagsAndObservedPeriod()
    {
        var adapter=new ModernMetricsAdapter(); adapter.Parse("BeginInstrumentReporting",3,0,Metadata());
        var collection=new Dictionary<string,object> { ["sessionId"]="session",["intervalStartTime"]=DateTime.UnixEpoch,["intervalEndTime"]=DateTime.UnixEpoch.AddSeconds(2) };
        adapter.Parse("Collection/Start",0,2000,collection);
        var values=Metadata(); values["tags"]="generation=gen0"; values["rate"]="6"; values["value"]="100";
        var points=adapter.Parse("CounterRateValuePublished",2,2001,values);
        Assert.Equal(100,points.Single(p=>p.Kind=="cumulative_counter").Value); Assert.Equal(6,points.Single(p=>p.Kind=="interval_increment").Value);
        Assert.All(points,p=> { Assert.Equal(2,p.IntervalSeconds); Assert.Equal(2000,p.TimeMilliseconds); Assert.Equal("System.Diagnostics.Metrics/System.Runtime",p.Source); });
        values["tags"]="generation=gen1"; Assert.NotEqual(points[0].Series,adapter.Parse("CounterRateValuePublished",2,2002,values)[0].Series);
        adapter.Parse("Collection/Stop",0,2003,collection); Assert.DoesNotContain("modern_collection_missing_stop",adapter.Warnings);
        Assert.Throws<LabException>(()=>adapter.Parse("CounterRateValuePublished",3,2004,values));
    }
    [Fact,Case("IT-PLAT-001","FR-025,FR-074","M1","Integration")]
    public void InstalledRuntime_MetricsEventSchemasMatchTheQualifiedAdapter()
    {
        var type=typeof(Meter).Assembly.GetType("System.Diagnostics.Metrics.MetricsEventSource",true)!;
        foreach(var (name,version) in new[]{("BeginInstrumentReporting",3),("CounterRateValuePublished",2),("GaugeValuePublished",2),("UpDownCounterRateValuePublished",2)})
        { var method=type.GetMethod(name,BindingFlags.Public|BindingFlags.Instance)!; Assert.Equal(version,method.GetCustomAttribute<EventAttribute>()!.Version); }
    }
    [Fact,Case("IT-CAP-001","FR-011,FR-025","M1","Integration")]
    public async Task ModernRuntimeCapture_RetainsSeriesAndSupportsBoundedRetrospectiveQuery()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        using var target=await CaptureTests.StartTarget();
        try
        {
            var identity=service.Discover().Single(t=>t.Identity.ProcessId==target.Id).Identity;
            var grant=service.IssueGrant(identity.Id,Permission.Observe,120,1);
            var operation=service.StartCapture(identity.Id,identity.Epoch,"runtime-metrics-context",4,grant.Id,"modern-capture");
            var completed=await ServiceTests.Wait(service,operation.Id); Assert.Equal(OperationState.Completed,completed.State);
            var analysis=service.Catalog.List("analysis").Rows.Single(); var summary=await service.AnalysisAsync(analysis.Id);
            var failures=new List<string>();
            var capture=service.Catalog.Get<Capture>(summary.CaptureId);
            if(summary.Quality.Warnings.Contains("unsupported_modern_metrics_schema"))
            new TraceAnalyzer().Analyze(await service.Artifacts.VerifiedPathAsync(service.Catalog.Get<Artifact>(summary.ArtifactId)),capture,
                counterParseFailure:(data,error)=>
                {
                    string fields=string.Join(",",data.PayloadNames.Select(name=>name+"="+(data.PayloadByName(name)?.GetType().Name??"null")));
                    failures.Add(data.EventName+"/v"+data.Version+" "+(error is LabException lab?lab.Code+" "+lab.Message:error.GetType().Name)+": "+fields);
                });
            Assert.True(!summary.Quality.Warnings.Contains("unsupported_modern_metrics_schema"),
                string.Join(Environment.NewLine,summary.EventCounts.Keys.Where(k=>k.StartsWith("System.Diagnostics.Metrics/",StringComparison.Ordinal)))+Environment.NewLine+
                string.Join(Environment.NewLine,failures.Take(3)));
            var points=summary.Counters.Where(p=>p.Source=="System.Diagnostics.Metrics/System.Runtime").ToArray(); Assert.NotEmpty(points);
            var group=points.Where(p=>p.Kind=="cumulative_counter").GroupBy(p=>new { p.Name,p.Series }).First(g=>g.Count()>=2);
            var rows=group.OrderBy(p=>p.TimeMilliseconds).ToArray();
            var window=await service.CounterWindowAsync(summary.Id,group.Key.Name,rows[0].TimeMilliseconds,rows[^1].TimeMilliseconds,"cumulative_counter",rows[0].Source,group.Key.Series);
            Assert.NotNull(window.Value); Assert.True(window.Value>=0); Assert.Equal("process_contextual",window.Scope);
            service.Artifacts.Delete(service.Catalog.Get<Artifact>(summary.ArtifactId));
            Assert.Equal(Availability.ArtifactExpired,(await service.CounterWindowAsync(summary.Id,group.Key.Name,0,4000,"cumulative_counter",rows[0].Source,group.Key.Series)).Status);
        }
        finally { if(!target.HasExited) target.Kill(true); await target.WaitForExitAsync(); }
    }
}
