using System.Globalization;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Analysis.Runtime;

public static class EventCounterAdapter
{
    public static CounterPoint Parse(IDictionary<string,object> payload,double timestamp)
    {
        string name=Convert.ToString(payload["Name"],CultureInfo.InvariantCulture)??"";
        bool increment=payload.ContainsKey("Increment");
        double value=Convert.ToDouble(payload[increment?"Increment":"Mean"],CultureInfo.InvariantCulture);
        double interval=Convert.ToDouble(payload["IntervalSec"],CultureInfo.InvariantCulture);
        string unit=payload.TryGetValue("DisplayUnits",out var supplied)?Convert.ToString(supplied,CultureInfo.InvariantCulture)??"unknown":"unknown";
        if(name.Length is <1 or >256||!double.IsFinite(timestamp)||!double.IsFinite(value)||!double.IsFinite(interval)||interval<=0||increment&&value<0)
            throw new LabException("invalid_counter_payload","EventCounter name, value, or observed interval is invalid.");
        return new(name,increment?"interval_increment":"gauge",unit,timestamp,interval,value,
            Observation:increment?"interval_increment":"interval_mean");
    }
}

public sealed class ModernMetricsAdapter
{
    private sealed record Collection(double EndMilliseconds,double Seconds);
    private sealed record Instrument(string Meter,string Name,string Unit,string Type,string Version,string Scope);
    private readonly Dictionary<string,Collection> collections=new();
    private readonly Dictionary<(string Session,int Id),Instrument> instruments=new();
    private readonly HashSet<string> warnings=new();
    private readonly HashSet<string> openCollections=new();
    public IReadOnlyCollection<string> Warnings=>openCollections.Count>0?warnings.Append("modern_collection_missing_stop").ToArray():warnings;
    public IReadOnlyList<CounterPoint> Parse(string eventName,int version,double timestamp,IDictionary<string,object> payload)
    {
        eventName=eventName switch { "Collection/Start"=>"CollectionStart","Collection/Stop"=>"CollectionStop",_=>eventName };
        string Text(string name)=>Convert.ToString(payload[name],CultureInfo.InvariantCulture)??"";
        double Number(string name)
        {
            if(!double.TryParse(Text(name),NumberStyles.Float,CultureInfo.InvariantCulture,out double value)||!double.IsFinite(value))
                throw new LabException("invalid_counter_payload","Modern metric has no finite numeric observation.");
            return value;
        }
        if(eventName.EndsWith("Error",StringComparison.Ordinal)) { warnings.Add("modern_metrics_provider_error"); return []; }
        if(eventName=="CollectionStart")
        {
            if(version!=0) throw new LabException("capability_unsupported","Unsupported metric collection schema.");
            var start=Date(payload["intervalStartTime"]); var end=Date(payload["intervalEndTime"]); double seconds=(end-start).TotalSeconds;
            if(!double.IsFinite(timestamp)||!double.IsFinite(seconds)||seconds<=0) throw new LabException("invalid_counter_payload","Invalid modern metric collection period.");
            string session=Text("sessionId"); if(collections.Count>=32&&!collections.ContainsKey(session)) throw new LabException("resource_limit","Modern metric session cap exceeded.");
            if(!openCollections.Add(session)) warnings.Add("modern_collection_missing_stop");
            collections[session]=new(timestamp,seconds); return [];
        }
        if(eventName=="CollectionStop")
        {
            if(version!=0) throw new LabException("capability_unsupported","Unsupported metric collection schema.");
            if(!openCollections.Remove(Text("sessionId"))) warnings.Add("modern_collection_missing_start");
            return [];
        }
        if(eventName=="BeginInstrumentReporting")
        {
            if(version!=3) throw new LabException("capability_unsupported","Modern instrument metadata requires the qualified version-three schema.");
            if(instruments.Count>=10000) throw new LabException("resource_limit","Modern metric instrument cap exceeded.");
            instruments[(Text("sessionId"),Convert.ToInt32(payload["instrumentId"],CultureInfo.InvariantCulture))]=
                new(Text("meterName"),Text("instrumentName"),Text("unit"),Text("instrumentType"),Text("meterVersion"),Text("meterScopeHash"));
            return [];
        }
        if(eventName=="HistogramValuePublished") { warnings.Add("modern_histogram_quantiles_not_individual_observations"); return []; }
        if(eventName is not ("CounterRateValuePublished" or "GaugeValuePublished" or "UpDownCounterRateValuePublished")) return [];
        if(version!=2) throw new LabException("capability_unsupported","Modern values require the qualified version-two schema.");
        string sessionId=Text("sessionId"); int id=Convert.ToInt32(payload["instrumentId"],CultureInfo.InvariantCulture);
        if(!instruments.TryGetValue((sessionId,id),out var instrument)||!collections.TryGetValue(sessionId,out var collection)||!openCollections.Contains(sessionId))
            throw new LabException("not_collected","Modern metric metadata or collection interval is missing.");
        if(instrument.Meter!=Text("meterName")||instrument.Name!=Text("instrumentName")||instrument.Unit!=Text("unit")||instrument.Version!=Text("meterVersion"))
            throw new LabException("invalid_counter_payload","Modern metric identity differs from its instrument metadata.");
        string tags=Text("tags"),source="System.Diagnostics.Metrics/"+instrument.Meter;
        warnings.Add("modern_collection_trace_anchor_is_contextual");
        string series=Wire.Hash(new { sessionId,id,instrument.Version,instrument.Scope,tags });
        CounterPoint Point(string kind,double value,string observation)=>new(instrument.Name,kind,instrument.Unit,collection.EndMilliseconds,collection.Seconds,value,
            source,"process_contextual",series,tags,instrument.Type,observation);
        if(eventName=="GaugeValuePublished")
        {
            if(Text("lastValue")=="") { warnings.Add("modern_gauge_no_observation"); return []; }
            return [Point("gauge",Number("lastValue"),"instantaneous")];
        }
        if(eventName=="UpDownCounterRateValuePublished") return [Point("gauge",Number("value"),"instantaneous")];
        double total=Number("value"); if(total<0) throw new LabException("invalid_counter_payload","A monotonic total must be nonnegative.");
        var points=new List<CounterPoint> { Point("cumulative_counter",total,"cumulative_observation") };
        // In this EventSource schema, 'rate' is an interval delta, not a per-second rate.
        if(Text("rate")!="")
        {
            double delta=Number("rate");
            if(delta<0) warnings.Add("modern_counter_reset_or_negative_delta");
            else points.Add(Point("interval_increment",delta,"interval_increment"));
        }
        return points;
    }
    private static DateTime Date(object value)=>value switch
    {
        DateTime date=>date.ToUniversalTime(),DateTimeOffset date=>date.UtcDateTime,
        long ticks=>DateTime.FromFileTimeUtc(ticks),
        _=>throw new LabException("invalid_counter_payload","Unsupported modern collection-clock representation.")
    };
}
