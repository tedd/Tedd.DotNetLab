using System.Text.Json;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class McpTests
{
    [Fact,Case("CT-MCP-ORDER","FR-060,FR-074","M7","Integration")]
    public async Task StdioDiscriminatorOrderDoesNotChangeStructuredDomainErrors()
    {
        using var workspace=new TestWorkspace(); using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client=await Connect(workspace.Root,timeout.Token);
        foreach(string payload in new[]{"{\"id\":\"missing-order-test\",\"action\":\"record\"}","{\"action\":\"record\",\"id\":\"missing-order-test\"}"})
        {
            using var request=JsonDocument.Parse(payload);
            var result=await client.CallToolAsync("lab.inspect",new Dictionary<string,object?> { ["request"]=request.RootElement },cancellationToken:timeout.Token);
            Assert.True(result.IsError);
            var response=Wire.Deserialize<LabResponse>(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
            Assert.Equal("error",response.Status); Assert.Equal("record_not_found",response.Code);
        }
    }
    [Fact,Case("UT-MCP-ORDER","FR-060,FR-074","M7","Contract")]
    public void DiscriminatorLookaheadPreservesUnknownAndDuplicatePropertyRejection()
    {
        Assert.IsType<InspectRecord>(Wire.Deserialize<InspectRequest>("{\"id\":\"missing\",\"action\":\"record\"}"));
        Assert.Throws<JsonException>(()=>Wire.Deserialize<InspectRequest>("{\"id\":\"missing\",\"extra\":true,\"action\":\"record\"}"));
        Assert.Throws<JsonException>(()=>Wire.Deserialize<InspectRequest>("{\"action\":\"record\",\"id\":\"missing\",\"action\":\"record\"}"));
    }
    private static async Task<McpClient> Connect(string workspace,CancellationToken cancellation,bool nativeExtension=false,bool shared=false)
    {
        string host=Path.Combine(Path.Combine(TestWorkspace.TaskRoot,"bin","Tedd.DotNetLab.Host","Release","net10.0"),"Tedd.DotNetLab.Host.dll");
        var transport=new StdioClientTransport(new StdioClientTransportOptions
        { Name="Tedd.DotNetLab test",Command=RuntimeSupport.StartInfo(RuntimeSupport.Current).FileName,Arguments=nativeExtension?["exec","--fx-version",RuntimeSupport.Current,host,shared?"connect":"serve",workspace,"--enable-native-extension"]:["exec","--fx-version",RuntimeSupport.Current,host,shared?"connect":"serve",workspace],ShutdownTimeout=TimeSpan.FromSeconds(10) });
        return await McpClient.CreateAsync(transport,cancellationToken:cancellation);
    }
    [Fact,Case("IT-MCP-SHARED-001","FR-060,FR-061,FR-070","M7","Integration")]
    public async Task ConcurrentClientsShareRecordsAndKeepIndependentToolCatalogs()
    {
        using var workspace=new TestWorkspace(); using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(40));
        McpClient? first=null,second=null,native=null;
        try
        {
            var connections=await Task.WhenAll(Connect(workspace.Root,timeout.Token,shared:true),Connect(workspace.Root,timeout.Token,shared:true));
            first=connections[0]; second=connections[1];
            var record=await Call(first,"lab.experiment",new { action="create_hypothesis",mechanism="shared-catalog",prediction="visible-to-both",evidence_ids=Array.Empty<string>() },timeout.Token);
            string id=record.GetProperty("id").GetString()!;
            var inspected=await Call(second,"lab.inspect",new { action="record",id },timeout.Token);
            Assert.Contains("shared-catalog",inspected.ToString());
            native=await Connect(workspace.Root,timeout.Token,nativeExtension:true,shared:true);
            Assert.Equal(7,(await native.ListToolsAsync(cancellationToken:timeout.Token)).Count);
            Assert.Equal(6,(await second.ListToolsAsync(cancellationToken:timeout.Token)).Count);
            await first.DisposeAsync(); first=null;
            Assert.Equal(6,(await second.ListToolsAsync(cancellationToken:timeout.Token)).Count);
        }
        finally
        {
            if(first is not null) await first.DisposeAsync();
            if(second is not null) await second.DisposeAsync();
            if(native is not null) await native.DisposeAsync();
            using var shutdown=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while(File.Exists(Path.Combine(workspace.Root,"controller.lock")))
            {
                try { using var unlocked=new FileStream(Path.Combine(workspace.Root,"controller.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None); break; }
                catch(IOException) { await Task.Delay(50,shutdown.Token); }
            }
        }
    }
    private static async Task<JsonElement> Call(McpClient client,string tool,object request,CancellationToken cancellation)
    {
        var arguments=new Dictionary<string,object?> { ["request"]=JsonSerializer.SerializeToElement(request,Wire.Json) };
        var result=await client.CallToolAsync(tool,arguments,cancellationToken:cancellation);
        Assert.False(result.IsError == true, string.Join("\n",result.Content.OfType<TextContentBlock>().Select(t=>t.Text)));
        string text=Assert.Single(result.Content.OfType<TextContentBlock>()).Text;
        var envelope=Wire.Deserialize<LabResponse>(text);
        Assert.True(envelope.Status=="ok",envelope.Code+": "+envelope.Message);
        Assert.Equal(Wire.Version,envelope.SchemaVersion);
        return envelope.Data!.Value;
    }
    [Fact,Case("CT-COMPAT-007","FR-001,FR-060,FR-070","M7","Integration")]
    public async Task StdioCompatibilityReportUsesDiscoveryActionWithoutAddingAnExecutorOrGrant()
    {
        using var workspace=new TestWorkspace(); using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using(var client=await Connect(workspace.Root,timeout.Token))
        {
            var tools=await client.ListToolsAsync(cancellationToken:timeout.Token);
            Assert.Equal(6,tools.Count); Assert.Contains("compatibility",tools.Single(item=>item.Name=="lab.discover").JsonSchema.ToString());
            Assert.Contains("target_runtime_version",tools.Single(item=>item.Name=="lab.experiment").JsonSchema.ToString());
            var response=await Call(client,"lab.discover",new { action="compatibility" },timeout.Token);
            var report=response.Deserialize<CompatibilityReport>(Wire.Json)!; Assert.False(report.PermissionsGranted); Assert.False(report.FullPlatformCertification);
            Assert.Null(report.Target); Assert.True(RuntimeSupport.SupportsController(report.BuildPins.CoreClrVersion,"windows"));
            Assert.Equal("target_runtime_version",report.ManagedRuntimeSelection!.PlanField);
        }
        await using var service=new LabService(workspace.Root);
        Assert.Empty(service.Catalog.List("grant").Rows); Assert.Empty(service.Catalog.List("mutation").Rows);
    }
    [Fact, Case("CT-MCP-001", "FR-060,FR-061", "M3", "Contract")]
    public async Task Stdio_DefaultCatalogContainsExactlySixBaseTools()
    {
        using var workspace=new TestWorkspace();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client=await Connect(workspace.Root,timeout.Token);
        var tools=await client.ListToolsAsync(cancellationToken:timeout.Token);
        Assert.Equal(new[]{"lab.compare","lab.discover","lab.experiment","lab.inspect","lab.query","lab.run"},tools.Select(t=>t.Name).Order());
        string schema=tools.Single(t=>t.Name=="lab.run").JsonSchema.ToString();
        Assert.Contains("start_capture",schema);
        Assert.Contains("idempotency_key",schema);
        Assert.DoesNotContain("shell",schema);
        var profiles=await Call(client,"lab.discover",new { action="profiles" },timeout.Token);
        Assert.Equal(Tedd.DotNetLab.Domain.Profiles.Names.Count,profiles.GetArrayLength());
    }
    [Fact,Case("CT-MCP-002","FR-060,FR-061,FR-070","M6","Contract")]
    public async Task Stdio_ExplicitNativeExtensionAddsOnlyPatchAndDoesNotAuthorizeMutation()
    {
        using var workspace=new TestWorkspace(); using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using(var client=await Connect(workspace.Root,timeout.Token,nativeExtension:true))
        {
            var tools=await client.ListToolsAsync(cancellationToken:timeout.Token);
            Assert.Equal(new[]{"lab.compare","lab.discover","lab.experiment","lab.inspect","lab.patch","lab.query","lab.run"},tools.Select(item=>item.Name).Order());
            var patch=tools.Single(item=>item.Name=="lab.patch"); Assert.Contains("recover_interrupted",patch.JsonSchema.ToString()); Assert.Contains("track_exceptions",patch.JsonSchema.ToString());
            var result=await client.CallToolAsync("lab.patch",new Dictionary<string,object?> { ["request"]=JsonSerializer.SerializeToElement<PatchRequest>(new PreflightCil("not-a-target","0","not-a-candidate","not-a-grant"),Wire.Json) },cancellationToken:timeout.Token);
            Assert.True(result.IsError); var response=Wire.Deserialize<LabResponse>(Assert.Single(result.Content.OfType<TextContentBlock>()).Text);
            Assert.Equal("record_not_found",response.Code);
        }
        await using var service=new LabService(workspace.Root); Assert.Empty(service.Catalog.List("grant").Rows); Assert.Empty(service.Catalog.List("mutation").Rows);
    }
    [Fact,Case("CT-MCP-BASE-PATCH-DENIED","FR-060,FR-070","M3","Contract")]
    public async Task Stdio_UnregisteredPatchCannotBeCalledInBaseMode()
    {
        using var workspace=new TestWorkspace(); using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using(var client=await Connect(workspace.Root,timeout.Token))
        {
            try
            {
                var result=await client.CallToolAsync("lab.patch",new Dictionary<string,object?> { ["request"]=JsonSerializer.SerializeToElement<PatchRequest>(new PreflightCil("target","0","candidate","grant"),Wire.Json) },cancellationToken:timeout.Token);
                Assert.True(result.IsError);
            }
            catch(ModelContextProtocol.McpException rejected) { Assert.Contains("lab.patch",rejected.Message); }
            await Call(client,"lab.discover",new { action="profiles" },timeout.Token);
        }
        await using var service=new LabService(workspace.Root); Assert.Empty(service.Catalog.List("grant").Rows); Assert.Empty(service.Catalog.List("mutation").Rows);
    }
    [Fact,Case("CT-MCP-NATIVE-OPTION","FR-060,FR-070","M6","Integration")]
    public async Task UnknownServerExtensionOptionRejectsBeforeCreatingWorkspace()
    {
        using var workspace=new TestWorkspace(); string target=Path.Combine(workspace.Root,"uncreated");
        var start=new System.Diagnostics.ProcessStartInfo("dotnet") { UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true };
        start.ArgumentList.Add(Path.Combine(TestWorkspace.TaskRoot,"bin","Tedd.DotNetLab.Host","Release","net10.0","Tedd.DotNetLab.Host.dll"));
        start.ArgumentList.Add("serve"); start.ArgumentList.Add(target); start.ArgumentList.Add("--arbitrary-extension");
        using var process=System.Diagnostics.Process.Start(TestWorkspace.PinRuntime(start))!; var output=process.StandardOutput.ReadToEndAsync(); var errors=process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); Assert.NotEqual(0,process.ExitCode); Assert.Empty(await output);
        Assert.Equal("invalid_request",Wire.Deserialize<LabResponse>(await errors).Code); Assert.False(Directory.Exists(target));
    }
    [Fact,Case("CT-MCP-NATIVE-WORKFLOW","FR-052,FR-053,FR-060,FR-062,FR-070","M6","Integration")]
    public async Task Stdio_EnabledPatchExecutesOnlyGrantedCandidateAndVerifiedOriginalRestore()
    {
        using var workspace=new TestWorkspace(); using var target=await ProfilerTests.StartNativeTarget(workspace.Root);
        try
        {
            string targetId,candidateId,mutationGrantId,executeGrantId;
            await using(var prepared=new LabService(workspace.Root))
            {
                var source=await CompilationControlTests.Prepare(prepared,target.Id);
                var candidate=await CompilationAdmissionTests.Candidate(prepared,source.InspectionId,source.Candidate.OriginalIlHash);
                targetId=source.Target.Id; candidateId=candidate.Id;
                mutationGrantId=prepared.IssueMutationGrant(targetId,candidate.Id,120,8).Id;
                executeGrantId=prepared.IssueGrant(targetId,Permission.ExecuteRegisteredScenario,120,8).Id;
            }
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await using var client=await Connect(workspace.Root,timeout.Token,nativeExtension:true);
            async Task<JsonElement> Finish(JsonElement operation)
            {
                string id=operation.GetProperty("id").GetString()!;
                while(operation.GetProperty("state").GetString() is "running" or "accepted" or "cancellation_requested")
                { await Task.Delay(20,timeout.Token); operation=await Call(client,"lab.run",new { action="status",operation_id=id },timeout.Token); }
                Assert.Equal("completed",operation.GetProperty("state").GetString()); return operation;
            }
            async Task<MutationOutcome> Outcome(JsonElement operation)
            {
                var record=await Call(client,"lab.inspect",new { action="record",id=operation.GetProperty("result_id").GetString() },timeout.Token);
                return Wire.Deserialize<MutationOutcome>(record.GetProperty("json").GetString()!);
            }
            var applied=await Finish(await Call(client,"lab.patch",new { action="apply",target_id=targetId,expected_target_epoch="0",candidate_id=candidateId,
                grant_id=mutationGrantId,execution_grant_id=executeGrantId,scenario_id="native-value",idempotency_key="stdio-native-apply",setup_operations=10 },timeout.Token));
            var candidateOutcome=await Outcome(applied); Assert.True(candidateOutcome.Measurable&&!candidateOutcome.Tainted); Assert.False(candidateOutcome.PromotionEligible);
            var restored=await Finish(await Call(client,"lab.patch",new { action="restore_original",transaction_id=candidateOutcome.Id[..^8],
                expected_target_epoch=candidateOutcome.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture),grant_id=mutationGrantId,
                execution_grant_id=executeGrantId,idempotency_key="stdio-native-restore" },timeout.Token));
            var original=await Outcome(restored); Assert.True(original.Restored&&!original.Tainted&&!original.Measurable); Assert.False(original.PromotionEligible);
            Assert.True(original.Epoch>candidateOutcome.Epoch);
        }
        finally { if(!target.HasExited) target.Kill(true); await target.WaitForExitAsync(); }
    }
    [Fact, Case("CT-MCP-014", "FR-060,FR-062", "M3", "Integration")]
    public async Task Stdio_PublicWorkflow_PersistsEvidenceThroughRestart()
    {
        using var workspace=new TestWorkspace();
        using var target=await CaptureTests.StartTarget(workspace.Root);
        try
        {
            string targetId,grantId,observeId;
            await using(var init=new LabService(workspace.Root))
            {
                var identity=init.Discover().Single(t=>t.Identity.ProcessId==target.Id).Identity;
                targetId=identity.Id;
                grantId=init.IssueGrant(targetId,Permission.ExecuteRegisteredScenario,120,20).Id;
                observeId=init.IssueGrant(targetId,Permission.Observe,120,1).Id;
            }
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
            string hypothesisId,comparisonId;
            await using(var client=await Connect(workspace.Root,timeout.Token))
            {
                await Call(client,"lab.discover",new { action="targets" },timeout.Token);
                var handshake=await Call(client,"lab.discover",new { action="scenarios",target_id=targetId },timeout.Token);
                var scenario=handshake.GetProperty("scenarios").EnumerateArray().Single(s=>s.GetProperty("id").GetString()=="cpu-sum");
                var hypothesis=await Call(client,"lab.experiment",new { action="create_hypothesis",
                    mechanism="Avoid the enumerator allocation.",prediction="Lower allocation and elapsed cost.",evidence_ids=Array.Empty<string>() },timeout.Token);
                hypothesisId=hypothesis.GetProperty("id").GetString()!;
                var plan=await Call(client,"lab.experiment",new { action="create_plan",scenario_id="cpu-sum",scenario_version="1",
                    input_hash=scenario.GetProperty("input_hash").GetString(),variants=new[]{"baseline","candidate"},
                    blocks=10,operations_per_run=100,warmup_batches=1,independent_blocks=true,
                    independence_justification="Synthetic blocks in a fixed laboratory scenario.",maximum_duration_seconds=30 },timeout.Token);
                string planId=plan.GetProperty("id").GetString()!;
                var accepted=await Call(client,"lab.run",new { action="start",target_id=targetId,expected_target_epoch="0",
                    plan_id=planId,grant_id=grantId,idempotency_key="mcp-exec" },timeout.Token);
                string operationId=accepted.GetProperty("id").GetString()!;
                JsonElement status;
                do
                {
                    await Task.Delay(20,timeout.Token);
                    status=await Call(client,"lab.run",new { action="status",operation_id=operationId },timeout.Token);
                } while(status.GetProperty("state").GetString() is "running" or "accepted");
                Assert.Equal("completed",status.GetProperty("state").GetString());
                var comparison=await Call(client,"lab.compare",new { execution_id=status.GetProperty("result_id").GetString() },timeout.Token);
                comparisonId=comparison[0].GetProperty("id").GetString()!;
                Assert.False(comparison[0].GetProperty("promotion_eligible").GetBoolean());
                var capture=await Call(client,"lab.run",new { action="start_capture",target_id=targetId,expected_target_epoch="0",
                    profile="baseline-context",duration_seconds=1,grant_id=observeId,idempotency_key="mcp-cap" },timeout.Token);
                operationId=capture.GetProperty("id").GetString()!;
                do
                {
                    await Task.Delay(25,timeout.Token);
                    status=await Call(client,"lab.run",new { action="status",operation_id=operationId },timeout.Token);
                } while(status.GetProperty("state").GetString() is "running" or "accepted");
                Assert.Equal("completed",status.GetProperty("state").GetString());
                var analyses=await Call(client,"lab.query",new { view="history",kind="analysis" },timeout.Token);
                string analysisId=analyses.GetProperty("rows")[0].GetProperty("id").GetString()!;
                await Call(client,"lab.query",new { view="gc_timeline",analysis_id=analysisId },timeout.Token);
                var missing=await Call(client,"lab.query",new { view="metric",analysis_id=analysisId,metric="method.average_ns" },timeout.Token);
                Assert.Equal("not_collected",missing.GetProperty("status").GetString());
                await Call(client,"lab.experiment",new { action="create_work_item",question="Confirm in fresh builds.",priority=1,
                    dependencies=Array.Empty<string>(),evidence_ids=new[]{comparisonId} },timeout.Token);
            }
            await using var restarted=await Connect(workspace.Root,timeout.Token);
            var history=await Call(restarted,"lab.query",new { view="history",kind="hypothesis" },timeout.Token);
            Assert.Equal(hypothesisId,history.GetProperty("rows")[0].GetProperty("id").GetString());
            var saved=await Call(restarted,"lab.inspect",new { action="record",id=comparisonId },timeout.Token);
            Assert.Equal(comparisonId,saved.GetProperty("id").GetString());
        }
        finally { if(!target.HasExited) { target.Kill(true); await target.WaitForExitAsync(); } }
    }
    [Fact,Case("CT-MCP-005,CT-MCP-010","FR-060,FR-063,FR-064","M3","Integration")]
    public async Task Stdio_LargeRecordUsesVerifiedToolSlicesAndResourceReference()
    {
        using var workspace=new TestWorkspace(); string content="λ\"\\\n"+new string('z',80000);
        await using(var prepared=new LabService(workspace.Root)) prepared.Catalog.Put("large","hypothesis",new { content });
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using(var client=await Connect(workspace.Root,timeout.Token))
        {
            var reference=await Call(client,"lab.inspect",new { action="record",id="large" },timeout.Token);
            Assert.Equal("artifact",reference.GetProperty("representation").GetString()); Assert.Equal("lab_response",reference.GetProperty("result_kind").GetString());
            string id=reference.GetProperty("artifact_id").GetString()!; using var bytes=new MemoryStream(); string offset="0";
            int count=reference.GetProperty("read").GetProperty("request").GetProperty("count").GetInt32();
            while(true)
            {
                var slice=await Call(client,"lab.query",new { view="artifact_slice",artifact_id=id,offset,count },timeout.Token);
                await bytes.WriteAsync(Convert.FromBase64String(slice.GetProperty("data").GetString()!),timeout.Token);
                if(!slice.GetProperty("truncated").GetBoolean()) break;
                offset=slice.GetProperty("next_offset").GetString()!;
            }
            Assert.Equal(reference.GetProperty("artifact_hash").GetString(),Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes.ToArray())));
            var original=Wire.Deserialize<LabResponse>(System.Text.Encoding.UTF8.GetString(bytes.ToArray()));
            using var body=JsonDocument.Parse(original.Data!.Value.GetProperty("json").GetString()!); Assert.Equal(content,body.RootElement.GetProperty("content").GetString());
            var resource=await client.ReadResourceAsync("dotnetlab://records/large",cancellationToken:timeout.Token);
            var resourceReference=Wire.Deserialize<JsonElement>(Assert.Single(resource.Contents.OfType<TextResourceContents>()).Text);
            Assert.Equal("notebook_record",resourceReference.GetProperty("result_kind").GetString()); Assert.True(resourceReference.GetProperty("truncated").GetBoolean());
        }
        await using var retained=new LabService(workspace.Root); Assert.Empty(retained.Catalog.List("grant").Rows);
        Assert.Empty(retained.Catalog.List("mutation").Rows); Assert.Equal(2,retained.Catalog.List("artifact").Rows.Count);
    }
    [Fact, Case("CT-MCP-003", "FR-061", "M3", "Contract")]
    public async Task Tool_InvalidDiscriminantFields_DoNotExecute()
    {
        using var workspace=new TestWorkspace();
        await using var service=new LabService(workspace.Root);
        var tools=new LabTools(service);
        var response=await tools.InvokeAsync("lab.run","{\"action\":\"status\",\"operation_id\":\"x\",\"shell\":\"arbitrary\"}");
        Assert.Equal("invalid_request",response.Code);
        var oversized=await tools.InvokeAsync("lab.query","{\"view\":\"history\",\"kind\":\"hypothesis\",\"limit\":1001}");
        Assert.Equal("resource_limit",oversized.Code);
        var missing=await tools.Inspect(new InspectNative("x","y","0","g"));
        Assert.Equal("record_not_found",missing.Code);
    }
}
