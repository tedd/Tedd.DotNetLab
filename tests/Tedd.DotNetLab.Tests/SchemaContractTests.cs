using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Mcp;
using Xunit;
using Xunit.Abstractions;

namespace Tedd.DotNetLab.Tests;

public sealed class SchemaContractTests(ITestOutputHelper testOutput)
{
    [Theory,InlineData("plan"),InlineData("measurement"),InlineData("capability"),InlineData("comparison"),InlineData("error")]
    [Case("CT-SCHEMA-001","FR-060,FR-074","M0","Contract")]
    public void PublishedSchemasMatchPinnedSerializerMetadata(string name)
    {
        var published=JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Schemas",name+".schema.json")))!;
        Assert.True(JsonNode.DeepEquals(published,SchemaDocuments.Create(name)),name+" schema drift");
        Assert.Equal("dotnetlab://schemas/v1/"+name,published["$id"]!.GetValue<string>());
        Assert.False(published["additionalProperties"]!.GetValue<bool>());
    }

    [Fact,Case("CT-SCHEMA-002,CT-MCP-007","FR-021,FR-060,FR-074","M0","Reference")]
    public async Task IndependentValidatorChecksOutputShapesNullableEvidenceAndExactIntegerBounds()
    {
        using var workspace=new TestWorkspace(); var cases=new JsonArray();
        JsonObject Payload(object value)=>JsonSerializer.SerializeToNode(value,value.GetType(),Wire.Json)!.AsObject();
        void Add(string schema,string name,JsonObject payload,bool valid)=>cases.Add(new JsonObject
            { ["schema"]=schema,["name"]=name,["valid"]=valid,["payload"]=payload.DeepClone() });
        var plan=Payload(new ExperimentPlan("plan","scenario","1","input",["baseline","candidate"],10,20,1,ulong.MaxValue,
            "quiet-validation",true,"independent process sessions",false,30,new(),DiagnosticWindows:new(),
            CompilationControl:new("control",long.MaxValue,"record-hash","artifact","artifact-hash"),TargetRuntimeVersion:"12.0.0-dev"));
        var measurement=Payload(new Measurement("execution","run","block","baseline",1,long.MaxValue,10000000,1,long.MaxValue,null,1,2,true,null,"input","1",
            IndividualLatencies:new("observer","individual_operation_latency",10000000,[1,long.MaxValue])));
        var capability=Payload(new TargetInfo(new("target",1,long.MaxValue,"runtime","10.0.12","x64"),"fixture",
            Enum.GetValues<CapabilityState>().ToDictionary(s=>s.ToString(),s=>new Capability(s,"reason"))));
        var comparison=Payload(new Comparison("comparison","paired-log-ratio/v2",EffectClass.Inconclusive,"insufficient_independent_evidence",1,
            null,null,null,null,null,null,null,null,false,.05,false,["insufficient_independent_evidence"],["run"]));
        var error=Payload(new LabResponse(Wire.Version,"error",null,"capability_unsupported","outside qualification",RecoveryHints.ForCode("capability_unsupported")));
        var payloads=new Dictionary<string,JsonObject> { ["plan"]=plan,["measurement"]=measurement,["capability"]=capability,["comparison"]=comparison,["error"]=error };
        foreach(var (schema,payload) in payloads)
        {
            Add(schema,schema+"-valid",payload,true);
            var extra=payload.DeepClone().AsObject(); extra["arbitrary_executor"]="no"; Add(schema,schema+"-extra-field",extra,false);
            var missing=payload.DeepClone().AsObject(); missing.Remove(schema=="error"?"code":schema=="capability"?"identity":schema=="measurement"?"frequency":"id");
            Add(schema,schema+"-missing-required",missing,false);
        }
        foreach(string integer in new[] {"0","1","-1","9223372036854775807","-9223372036854775808"})
        {
            var copy=measurement.DeepClone().AsObject(); copy["start_ticks"]=integer; Add("measurement","signed-valid-"+integer,copy,true);
        }
        foreach(string integer in new[] {"9223372036854775808","-9223372036854775809","18446744073709551615","-0","01","+1","1\n"," 1","1.0"})
        {
            var copy=measurement.DeepClone().AsObject(); copy["start_ticks"]=integer; Add("measurement","signed-invalid-"+integer,copy,false);
        }
        foreach(string seed in new[] {"0","9223372036854775808","18446744073709551615"})
        {
            var copy=plan.DeepClone().AsObject(); copy["seed"]=seed; Add("plan","seed-valid-"+seed,copy,true);
        }
        foreach(string seed in new[] {"-1","18446744073709551616","00","1\n"})
        {
            var copy=plan.DeepClone().AsObject(); copy["seed"]=seed; Add("plan","seed-invalid-"+seed,copy,false);
        }
        var number=measurement.DeepClone().AsObject(); number["frequency"]=10000000; Add("measurement","numeric-ticks-are-not-output-strings",number,false);
        var unknown=capability.DeepClone().AsObject(); unknown["capabilities"]!["Supported"]!["state"]="future_supported"; Add("capability","unknown-capability-enum",unknown,false);
        unknown=capability.DeepClone().AsObject(); unknown["capabilities"]!["Supported"]!["state"]=1; Add("capability","numeric-capability-enum",unknown,false);
        unknown=comparison.DeepClone().AsObject(); unknown["effect_class"]="identical"; Add("comparison","unknown-effect-enum",unknown,false);
        var badVector=measurement.DeepClone().AsObject(); badVector["individual_latencies"]!["elapsed_ticks"]!.AsArray().Add("9223372036854775808");
        Add("measurement","nested-vector-out-of-range",badVector,false);
        badVector=measurement.DeepClone().AsObject(); badVector["individual_latencies"]!["elapsed_ticks"]!.AsArray().Add(2);
        Add("measurement","nested-vector-numeric-tick",badVector,false);
        var nullable=measurement.DeepClone().AsObject(); nullable.Remove("individual_latencies"); Add("measurement","omitted-optional-vector",nullable,true);
        var shapeOnly=measurement.DeepClone().AsObject(); shapeOnly["frequency"]="0"; Add("measurement","shape-does-not-prove-domain-validity",shapeOnly,true);
        Assert.Throws<LabException>(()=>Policies.Validate(new Measurement("e","r","b","v",1,2,0,1,0,null,1,1,true,null,"i","1")));
        var wrongError=error.DeepClone().AsObject(); wrongError["schema_version"]="99"; Add("error","wrong-error-version",wrongError,false);
        wrongError=error.DeepClone().AsObject(); wrongError["message"]=new string('m',4097); Add("error","oversized-error-message",wrongError,false);
        wrongError=error.DeepClone().AsObject(); wrongError["recovery"]=new string('r',2049); Add("error","oversized-error-guidance",wrongError,false);
        string path=Path.Combine(workspace.Root,"schema-cases.json"); await File.WriteAllTextAsync(path,cases.ToJsonString());
        var start=new ProcessStartInfo(@"C:\Python314\python.exe") { UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=workspace.Root };
        start.Environment["PYTHONDONTWRITEBYTECODE"]="1"; start.Environment["TEMP"]=workspace.Root; start.Environment["TMP"]=workspace.Root;
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"schema_contract_reference.py"));
        start.ArgumentList.Add("--schemas"); start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"Schemas"));
        start.ArgumentList.Add("--cases"); start.ArgumentList.Add(path);
        using var process=Process.Start(TestWorkspace.PinRuntime(start))!; var output=process.StandardOutput.ReadToEndAsync(); var errors=process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(process.ExitCode==0,await errors+await output);
        string validationReport=await output; testOutput.WriteLine(validationReport);
        using var report=JsonDocument.Parse(validationReport); Assert.Equal(cases.Count,report.RootElement.GetProperty("passed").GetInt32());
        Assert.Equal("jsonschema",report.RootElement.GetProperty("validator").GetString());
        Assert.True(report.RootElement.GetProperty("integer_cases").GetInt32()>=1000);
    }

    [Fact,Case("CT-SCHEMA-003","FR-060,FR-070","M0","Contract")]
    public async Task CliSchemaCommandDoesNotCreateWorkspaceOrAcceptArbitraryTypes()
    {
        using var workspace=new TestWorkspace();
        async Task<(int Exit,string Output)> Run(string name)
        {
            var start=new ProcessStartInfo("dotnet") { UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=workspace.Root };
            start.ArgumentList.Add(Path.Combine(TestWorkspace.TaskRoot,"bin","Tedd.DotNetLab.Host","Release","net10.0","Tedd.DotNetLab.Host.dll"));
            start.ArgumentList.Add("schema"); start.ArgumentList.Add(name);
            using var process=Process.Start(TestWorkspace.PinRuntime(start))!; var output=process.StandardOutput.ReadToEndAsync(); var errors=process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); await errors; return (process.ExitCode,await output);
        }
        var valid=await Run("measurement"); Assert.Equal(0,valid.Exit); Assert.True(JsonNode.DeepEquals(JsonNode.Parse(valid.Output),SchemaDocuments.Create("measurement")));
        var invalid=await Run("System.Diagnostics.Process"); Assert.NotEqual(0,invalid.Exit);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Root));
    }
}
