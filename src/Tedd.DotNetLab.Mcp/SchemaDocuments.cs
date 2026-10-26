using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Mcp;

public static class SchemaDocuments
{
    public const string GeneratorVersion="dotnetlab-wire-schema/v1";
    public static IReadOnlyList<string> Names { get; }=Array.AsReadOnly(new[] {"plan","measurement","capability","comparison","error"});
    public static JsonObject Create(string name)
    {
        Type type=name switch
        {
            "plan"=>typeof(ExperimentPlan),"measurement"=>typeof(Measurement),"capability"=>typeof(TargetInfo),
            "comparison"=>typeof(Comparison),"error"=>typeof(LabResponse),
            _=>throw new LabException("invalid_request","Unknown published schema name.")
        };
        var schema=(JsonObject)Wire.Json.GetJsonSchemaAsNode(type,new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable=true,
            TransformSchemaNode=(context,node)=>
            {
                if(Scalar(context.TypeInfo.Type) is { } scalar) return scalar;
                if(node is JsonObject collection&&context.TypeInfo.ElementType is { } element&&Scalar(element) is { } item)
                {
                    // Custom-converter element contracts can otherwise export as unconstrained true/omitted schemas.
                    if(context.TypeInfo.Kind==JsonTypeInfoKind.Enumerable) collection["items"]=item;
                    if(context.TypeInfo.Kind==JsonTypeInfoKind.Dictionary) collection["additionalProperties"]=item;
                }
                if(node is JsonObject document&&document["properties"] is JsonObject properties)
                {
                    var required=new JsonArray();
                    foreach(var property in context.TypeInfo.Properties)
                    {
                        if(!properties.ContainsKey(property.Name)) continue;
                        // Optional custom-converter scalars with a parameter default can export as an untyped {"default":null}.
                        if(properties[property.Name] is JsonObject exported&&!exported.ContainsKey("type")&&!exported.ContainsKey("enum")
                            &&Scalar(property.PropertyType) is { } repaired)
                        {
                            if(exported.TryGetPropertyValue("default",out var defaultValue)) repaired["default"]=defaultValue?.DeepClone();
                            properties[property.Name]=repaired;
                        }
                        var ignore=property.AttributeProvider?.GetCustomAttributes(typeof(JsonIgnoreAttribute),true).OfType<JsonIgnoreAttribute>().SingleOrDefault();
                        if(ignore?.Condition is not (JsonIgnoreCondition.WhenWritingNull or JsonIgnoreCondition.WhenWritingDefault)) required.Add(property.Name);
                    }
                    document["required"]=required;
                    if(context.TypeInfo.Type==typeof(IndividualLatencyVector)&&properties["elapsed_ticks"] is JsonObject ticks)
                    { ticks["minItems"]=1; ticks["maxItems"]=10000; }
                }
                return node;
            }
        });
        schema["$schema"]="https://json-schema.org/draft/2020-12/schema";
        schema["$id"]="dotnetlab://schemas/v1/"+name;
        schema["$comment"]=GeneratorVersion+"; output shape for Wire.Version="+Wire.Version+". Authorization, provenance, finite arithmetic, lifecycle, and cross-field domain validation remain required.";
        if(name=="error")
        {
            var properties=(JsonObject)schema["properties"]!;
            properties["schema_version"]=new JsonObject { ["const"]=Wire.Version };
            properties["status"]=new JsonObject { ["const"]="error" };
            properties["data"]=new JsonObject { ["type"]="null" };
            properties["code"]=new JsonObject { ["type"]="string",["minLength"]=1 };
            properties["message"]=new JsonObject { ["type"]="string",["maxLength"]=4096 };
            properties["recovery"]=new JsonObject { ["type"]=new JsonArray("string","null"),["maxLength"]=RecoveryHints.MaximumLength };
        }
        return schema;
    }
    private static JsonObject? Scalar(Type type)
    {
        Type concrete=Nullable.GetUnderlyingType(type)??type; bool nullable=Nullable.GetUnderlyingType(type) is not null;
        if(concrete==typeof(long)||concrete==typeof(ulong))
            return new JsonObject { ["type"]=nullable?new JsonArray("string","null"):JsonValue.Create("string"),
                ["pattern"]=concrete==typeof(long)?SignedPattern:UnsignedPattern,
                ["maxLength"]=20,["description"]="Canonical decimal exact integer; serialized output uses strings." };
        if(!concrete.IsEnum) return null;
        var values=new JsonArray();
        foreach(string value in Enum.GetNames(concrete)) values.Add(JsonSerializer.SerializeToNode(Enum.Parse(concrete,value),concrete,Wire.Json));
        if(nullable) values.Add((JsonNode?)null);
        return new JsonObject { ["enum"]=values };
    }
    // Enumerate lexicographic prefix ranges, rather than accepting every 19/20-digit string.
    private static string PositiveRange(string maximum)
    {
        var alternatives=new List<string> { "[1-9][0-9]{0,"+(maximum.Length-2)+"}" };
        for(int index=0;index<maximum.Length;index++)
        {
            int first=index==0?1:0,last=maximum[index]-'0'-1;
            if(last<first) continue;
            string digit=first==last?first.ToString():"["+first+"-"+last+"]";
            int remaining=maximum.Length-index-1;
            alternatives.Add(maximum[..index]+digit+(remaining==0?"":"[0-9]{"+remaining+"}"));
        }
        alternatives.Add(maximum); return "(?:"+string.Join('|',alternatives)+")";
    }
    private static string SignedPattern { get; }="^(?:0|"+PositiveRange("9223372036854775807")+"|-"+PositiveRange("9223372036854775808")+")(?![\\s\\S])";
    private static string UnsignedPattern { get; }="^(?:0|"+PositiveRange("18446744073709551615")+")(?![\\s\\S])";
}
