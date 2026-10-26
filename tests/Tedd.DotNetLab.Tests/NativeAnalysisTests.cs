using Tedd.DotNetLab.Analysis.Native;
using Tedd.DotNetLab.Contracts;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class NativeAnalysisTests
{
    private static NativeSnapshot Snapshot(byte[] bytes,string address="0x1000") =>
        new(Wire.Version,"fixture","10.0.12","x64","build","mvid","module","0x06000001","int32()","0","1","unknown",
            [new(address,bytes,"hot")],[new(-2,0,0,1,"prolog")],"offline_fixture","il","il");
    [Fact, Case("UT-NAT-010", "FR-042", "M4", "Unit")]
    public void Decode_OriginalIp_PreservesRelativeCallTargets()
    {
        var a=NativeDecoder.Decode(Snapshot([0xe8,0x01,0,0,0,0xc3,0xc3]));
        var b=NativeDecoder.Decode(Snapshot([0xe8,0x01,0,0,0,0xc3,0xc3],"0x2000"));
        Assert.Equal("0x1006",a.Instructions[0].BranchTarget);
        Assert.Equal("0x2006",b.Instructions[0].BranchTarget);
        Assert.Equal(a.Instructions[0].Bytes,b.Instructions[0].Bytes);
        Assert.Equal("imported_snapshot_unverified",a.SourceQualification);
        Assert.Equal(-2,a.Maps[0].IlOffset);
    }
    [Fact, Case("UT-NAT-009", "FR-042", "M4", "Unit")]
    public void Decode_TruncatedInstruction_IsReportedWithRetainedBytes()
    {
        var snapshot=Snapshot([0xe8,0x01]);
        var listing=NativeDecoder.Decode(snapshot);
        Assert.Empty(listing.Instructions);
        Assert.Contains(listing.Warnings,w=>w.StartsWith("invalid_instruction",StringComparison.Ordinal));
        Assert.Equal(2,snapshot.Regions[0].Bytes.Length);
    }
    [Fact, Case("UT-NAT-002", "FR-041", "M4", "Unit")]
    public void Decode_SplitRegions_AreKeptSeparate()
    {
        var original=Snapshot([0xc3]);
        var snapshot=original with { Regions=[new("0x1000",[0xc3],"hot"),new("0x2000",[0xc3],"cold")] };
        var listing=NativeDecoder.Decode(snapshot);
        Assert.Equal(new[]{0,1},listing.Instructions.Select(i=>i.Region));
        Assert.Throws<LabException>(()=>NativeDecoder.Validate(original with { Regions=[new("0xffffffffffffffff",[0xc3],"hot")] }));
    }
    [Fact, Case("UT-NAT-013", "FR-042", "M4", "Unit")]
    public void Diff_ChangedImmediate_RemainsVisible()
    {
        var a=Snapshot([0xb8,0x2a,0,0,0,0xc3]);
        var b=Snapshot([0xb8,0x2b,0,0,0,0xc3]);
        var diff=NativeDecoder.Diff(a,b);
        var change=Assert.Single(diff.Changes);
        Assert.Equal(1,change.Offset);
        Assert.Equal(42,change.Original);
        Assert.Equal(43,change.Candidate);
        Assert.False(diff.ProvesSemanticEquivalence);
    }
    [Fact, Case("UT-ID-004", "FR-005,FR-040", "M4", "Integration")]
    public void StoredMetadata_MethodIdentity_IsTokenAndModuleBound()
    {
        string path=typeof(Tedd.DotNetLab.Harness.ScenarioRunner).Assembly.Location;
        var methods=PeInspector.Methods(path,"fixture-build","RunSync");
        var method=Assert.Single(methods.Rows);
        Assert.StartsWith("0x06",method.Token);
        Assert.True(Guid.TryParse(method.ModuleMvid,out _));
        var il=PeInspector.Il(path,"fixture-build",method.Token);
        Assert.NotEmpty(il.Bytes);
        Assert.Equal("retained_build_not_live_body",il.Qualification);
        Assert.Equal(method.ModuleMvid,il.Method.ModuleMvid);
    }

    [Fact, Case("IT-NAT-OFFLINE", "FR-040,FR-042", "M4", "Integration")]
    public async Task StoredCode_ImporterAndService_KeepLiveIdentityUnverified()
    {
        using var workspace=new TestWorkspace();
        await using var service=new Tedd.DotNetLab.Application.LabService(workspace.Root);
        var pe=await service.ImportAsync(typeof(Tedd.DotNetLab.Harness.ScenarioRunner).Assembly.Location,"application/vnd.microsoft.portable-executable");
        var methods=await service.MethodsAsync(pe.Id,"RunSync",100,null);
        var method=Assert.Single(methods.Rows);
        var inspected=await service.StoredIlAsync(pe.Id,method.Token);
        var json=System.Text.Json.JsonSerializer.SerializeToElement(inspected,Wire.Json);
        Assert.Equal("retained_build_not_live_body",json.GetProperty("qualification").GetString());
        Assert.NotNull(json.GetProperty("artifact_id").GetString());
        string input=Path.Combine(workspace.Root,"snapshot.json");
        await File.WriteAllTextAsync(input,Wire.Serialize(Snapshot([0xb8,42,0,0,0,0xc3])));
        var raw=await service.ImportAsync(input,"application/vnd.dotnetlab.native-snapshot+json");
        var listing=await service.DecodeAsync(raw.Id);
        json=System.Text.Json.JsonSerializer.SerializeToElement(listing,Wire.Json);
        Assert.Equal("imported_snapshot_unverified",json.GetProperty("qualification").GetString());
        Assert.Equal(2,json.GetProperty("instruction_count").GetInt32());
    }
}
