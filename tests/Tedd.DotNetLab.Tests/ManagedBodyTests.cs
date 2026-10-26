using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Tedd.DotNetLab.Analysis.Native;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class ManagedBodyTests
{
    private static string Original=>Path.Combine(TestWorkspace.TaskRoot,"bin","LabWorkload","Release","net10.0","LabWorkload.dll");
    private static string Compiled=>Path.Combine(TestWorkspace.TaskRoot,"bin","Tedd.DotNetLab.LiveCandidate","Release","net10.0","LabWorkload.dll");
    private static StoredMethod Method(string path,string name)=>Assert.Single(PeInspector.Methods(path,
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),"ManagedBodyFixture."+name).Rows);
    [Fact,Case("UT-IL-MANAGED-METADATA-REJECTION","FR-052,FR-053","M6","Unit")]
    public void MissingMetadataAndExceptionRegionsAreRejectedBeforeNativeSubmission()
    {
        Assert.Equal("metadata_reference_missing",Assert.Throws<LabException>(()=>CompiledBodyImporter.Import(
            Compiled,Method(Compiled,"MissingReference").Token,Original,Method(Original,"MissingReference"))).Code);
        Assert.Equal("capability_unsupported",Assert.Throws<LabException>(()=>CompiledBodyImporter.Import(
            Compiled,Method(Compiled,"ExceptionRegion").Token,Original,Method(Original,"ExceptionRegion"))).Code);
    }
    [Fact,Case("UT-IL-MANAGED-LAYOUT-REJECTION","FR-052,FR-053","M6","Unit")]
    public void RecompiledTypeLayoutChangesAreRejected()
    {
        using var workspace=new TestWorkspace(); byte[] bytes=File.ReadAllBytes(Compiled);
        using(var stream=new MemoryStream(bytes,false))
        using(var pe=new PEReader(stream))
        {
            var reader=pe.GetMetadataReader(); var method=reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(Convert.ToInt32(Method(Compiled,"Sum").Token[2..],16)&0xffffff));
            var field=reader.GetTypeDefinition(method.GetDeclaringType()).GetFields().First();
            int offset=pe.PEHeaders.MetadataStartOffset+reader.GetTableMetadataOffset(TableIndex.Field)+(MetadataTokens.GetRowNumber(field)-1)*reader.GetTableRowSize(TableIndex.Field);
            bytes[offset]^=0x20; // Change the existing field's initonly contract.
        }
        string changed=Path.Combine(workspace.Root,"changed-layout.dll"); File.WriteAllBytes(changed,bytes);
        Assert.Equal("type_layout_changed",Assert.Throws<LabException>(()=>CompiledBodyImporter.Import(changed,Method(Compiled,"Sum").Token,Original,Method(Original,"Sum"))).Code);
    }
    [Theory,InlineData("Sum"),InlineData("Add"),InlineData("Increment"),InlineData("Transform"),InlineData("IntegerCall"),InlineData("SpanSum")]
    [Case("UT-IL-MANAGED-IMPORT","FR-052,FR-053","M6","Unit")]
    public void CompiledBodiesResolveInstanceFieldsArraysSpansRefsStructsAndSimd(string name)
    {
        var target=Method(Original,name); var source=Method(Compiled,name);
        byte[] body=CompiledBodyImporter.Import(Compiled,source.Token,Original,target);
        var verified=ManagedBodyPolicy.Verify(body,target,Original);
        Assert.Equal(ManagedBodyPolicy.Qualification,verified.Qualification);
        Assert.True(verified.RequiredStack>0);
        Assert.Throws<LabException>(()=>NativeBodyPolicy.Verify(body,NativeBodyPolicy.Resolve(body,target,Original)));
        Assert.Throws<LabException>(()=>CompiledBodyImporter.Import(Compiled,Method(Compiled,name=="Sum"?"Increment":"Sum").Token,Original,target));
        byte[] invalid=(byte[])body.Clone(); invalid[12]=0xff;
        Assert.Throws<LabException>(()=>ManagedBodyPolicy.Verify(invalid,target,Original));
        BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(8),0x110fffff);
        Assert.Throws<LabException>(()=>ManagedBodyPolicy.Verify(invalid,target,Original));
    }
    [Theory,InlineData("Sum",true,false,false),InlineData("Sum",true,true,false),InlineData("Sum",true,false,true),InlineData("Add",false,false,false),InlineData("Increment",false,false,false),InlineData("Transform",false,false,false),InlineData("Incorrect",true,false,false),InlineData("IntegerCall",false,false,false),InlineData("SpanSum",false,false,false)]
    [Case("IT-REJIT-MANAGED-BODY","FR-052,FR-053,FR-054,FR-037","M6","Integration")]
    public async Task CompiledReplacementAndRestoreRetainTheSameRunningProcess(string name,bool applicationThread,bool attach,bool releaseFailure)
    {
        if(attach&&!OperatingSystem.IsWindows()) return;
        using var workspace=new TestWorkspace(); using var process=await ProfilerTests.StartNativeTarget(workspace.Root,applicationThread:applicationThread,startupProfiler:!attach);
        try
        {
            await using var service=new LabService(workspace.Root,mutationJournalCommitted:item=>
            { if(releaseFailure&&item.Phase=="application_boundary_releasing"&&!process.HasExited) process.Kill(true); });
            var target=service.Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity;
            if(attach)
            {
                byte[] metadata=Encoding.UTF8.GetBytes(Wire.Serialize(new { schema_version=Wire.Version,target_id=target.Id,
                    process_id=target.ProcessId,start_time_utc_ticks=target.StartTimeUtcTicks,workspace=workspace.Root }));
                new Microsoft.Diagnostics.NETCore.Client.DiagnosticsClient(process.Id).AttachProfiler(TimeSpan.FromSeconds(10),Guid.Parse(ProfilerTests.ProfilerId),ProfilerTests.ProfilerPath,metadata);
            }
            await service.ProfilerAsync(target.Id,CancellationToken.None);
            var inspectGrant=service.IssueGrant(target.Id,Permission.InspectLiveCode,120,10);
            var build=await service.ImportAsync(Original,"application/vnd.microsoft.portable-executable");
            var method=Assert.Single((await service.MethodsAsync(build.Id,"ManagedBodyFixture."+name,100,null)).Rows);
            var inspection=await ServiceTests.Wait(service,(await service.InspectLiveAsync(target.Id,method.Id,0,inspectGrant.Id,null,CancellationToken.None)).Id);
            Assert.True(inspection.State==OperationState.Completed,Wire.Serialize(inspection));
            var row=service.Catalog.Get<JsonElement>(inspection.ResultId!);
            var manifest=service.Catalog.Get<Artifact>(row.GetProperty("manifest_artifact_id").GetString()!);
            var snapshot=Wire.Deserialize<NativeSnapshotReply>(await File.ReadAllTextAsync(await service.Artifacts.VerifiedPathAsync(manifest)));
            var originalControl=await service.PrepareOriginalBodyControlAsync(inspection.ResultId!);
            Assert.Equal(ManagedBodyPolicy.Qualification,originalControl.BodyPolicy);
            var compiled=await service.ImportAsync(Compiled,"application/vnd.microsoft.portable-executable");
            var candidate=await service.RegisterCompiledCilAsync(compiled.Id,Method(Compiled,name).Token,inspection.ResultId!,snapshot.OriginalBodyHash,[]);
            Assert.Equal(ManagedBodyPolicy.Qualification,candidate.BodyPolicy);
            var mutation=service.IssueMutationGrant(target.Id,candidate.Id,120,10);
            var execution=service.IssueGrant(target.Id,Permission.ExecuteRegisteredScenario,120,20);
            await service.PreflightCilAsync(target.Id,0,candidate.Id,mutation.Id);
            var applied=await ServiceTests.Wait(service,(await service.ApplyCilAsync(target.Id,0,candidate.Id,mutation.Id,execution.Id,"managed-"+name,"managed-apply")).Id);
            var outcome=service.Catalog.Get<MutationOutcome>(applied.ResultId!);
            if(releaseFailure)
            {
                Assert.Equal(OperationState.Tainted,applied.State); Assert.True(outcome.Tainted); Assert.False(outcome.Measurable);
                Assert.Equal("application_boundary_release_unknown",outcome.Failure); return;
            }
            if(name=="Incorrect")
            {
                Assert.Equal(OperationState.Failed,applied.State); Assert.Equal("correctness_failed",outcome.Failure);
                Assert.True(outcome.Restored); Assert.False(outcome.Tainted); Assert.False(outcome.Measurable); Assert.False(process.HasExited); return;
            }
            Assert.True(applied.State==OperationState.Completed,Wire.Serialize(outcome));
            Assert.True(outcome.Measurable); Assert.False(outcome.Restored); Assert.False(process.HasExited);
            var root=service.Catalog.List("mutation").Rows.Select(r=>Wire.Deserialize<MutationRoot>(r.Json)).Single(r=>r.OperationId==applied.Id);
            if(applicationThread)
            {
                var phases=service.Catalog.RecordsByPrefix("mutation_event",root.Id+"-event-").Select(r=>Wire.Deserialize<MutationEvent>(r.Json).Phase).ToArray();
                Assert.Contains("application_boundary_held",phases); Assert.Contains("application_boundary_released",phases);
                Assert.True(Array.IndexOf(phases,"application_boundary_held")<Array.IndexOf(phases,"request_submitted"));
            }
            var restored=await ServiceTests.Wait(service,(await service.RestoreCilAsync(root.Id,outcome.Epoch,mutation.Id,execution.Id,"managed-restore")).Id);
            Assert.True(restored.State==OperationState.Completed,Wire.Serialize(restored));
            var original=service.Catalog.Get<MutationOutcome>(restored.ResultId!);
            Assert.True(original.Restored); Assert.False(original.Tainted); Assert.False(process.HasExited);
            Assert.Equal(target.StartTimeUtcTicks,service.Target(target.Id).Identity.StartTimeUtcTicks);
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }
}
