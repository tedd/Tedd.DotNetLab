using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Tedd.DotNetLab.Contracts;
using Iced.Intel;

namespace Tedd.DotNetLab.Analysis.Native;

public record NativeRegion(string StartAddress,byte[] Bytes,string Kind);
public record IlNativeMap(int IlOffset,int RegionIndex,int NativeStartOffset,int NativeEndOffset,string Category);
public record RawIlNativeMap(int IlOffset,string NativeStartOffset,string NativeEndOffset);
public record SnapshotPerturbation(bool RuntimeSuspended,bool WorkerRegisteredWithRuntime,bool InspectionOutsideMeasurementRequired);
public record NativeSnapshotReply(IReadOnlyList<NativeSnapshot> Snapshots,string MethodName,string DeclaringType,byte[] OriginalBody,
    string OriginalBodyHash,SnapshotPerturbation Perturbation,bool CodeRewrittenForInspection,IReadOnlyList<NativeInlinerBinding>? Inliners=null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? InlinerCoverage=null);
public record NativeInlinerBinding(string ModuleMvid,string ModuleHash,string ModuleLoadIdentity,string MethodToken,string Signature,string OriginalIlHash)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string Scope=>ModuleMvid+"/"+MethodToken+"/"+ModuleHash;
}
public record NativeSnapshot(string SchemaVersion,string TargetId,string RuntimeVersion,string Architecture,
    string ModuleHash,string ModuleMvid,string ModuleLoadIdentity,string MethodToken,string Signature,
    string RejitId,string Epoch,string Tier,IReadOnlyList<NativeRegion> Regions,IReadOnlyList<IlNativeMap> Maps,
    string Acquisition,string OriginalIlHash,string CurrentIlHash,IReadOnlyList<RawIlNativeMap>? RawMaps=null,string? ObservationEpoch=null,
    byte[]? CurrentIlBody=null);
public record DecodedInstruction(int Region,int Offset,string Address,string Bytes,string Text,string FlowControl,
    string? BranchTarget);
public record CodeAnnotation(string Rule,string Confidence,int Region,int StartOffset,int EndOffset,
    string Description,IReadOnlyList<string> AlternativeExplanations);
public record NativeListing(string DecoderVersion,string SourceQualification,IReadOnlyList<DecodedInstruction> Instructions,
    IReadOnlyList<CodeAnnotation> Annotations,IReadOnlyList<string> Warnings,IReadOnlyList<IlNativeMap> Maps);
public record ByteDifference(int Region,int Offset,int? Original,int? Candidate);
public record NativeDiff(IReadOnlyList<ByteDifference> Changes,bool Truncated,bool ProvesSemanticEquivalence=false);

public static class NativeDecoder
{
    public const string Version="iced-x64/1.21.0";
    private sealed class TextOutput : FormatterOutput
    {
        public StringBuilder Text { get; }=new();
        public override void Write(string text,FormatterTextKind kind)=>Text.Append(text);
    }
    private static ulong Address(string address)
    {
        string text=address.StartsWith("0x",StringComparison.OrdinalIgnoreCase)?address[2..]:address;
        if(!ulong.TryParse(text,NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture,out ulong value))
            throw new LabException("invalid_snapshot","Native addresses must be hexadecimal strings.");
        return value;
    }
    public static void Validate(NativeSnapshot snapshot)
    {
        if(snapshot.CurrentIlBody is { } body&&(body.Length>65536||Convert.ToHexStringLower(SHA256.HashData(body))!=snapshot.CurrentIlHash))
            throw new LabException("invalid_snapshot","Retained replacement CIL differs from its version hash.");
        if(snapshot.SchemaVersion!=Wire.Version||!snapshot.Architecture.Equals("x64",StringComparison.OrdinalIgnoreCase))
            throw new LabException("capability_unsupported","Only schema 1.0 x64 offline snapshots are supported.");
        if(snapshot.Regions.Count is <1 or >16||snapshot.Maps.Count>100000||
            snapshot.Regions.Sum(r=>(long)r.Bytes.Length)>1048576||snapshot.Regions.Any(r=>r.Bytes.Length==0))
            throw new LabException("resource_limit","Snapshot exceeds region, byte, or mapping limits.");
        var ranges=new List<(ulong Start,ulong End)>();
        foreach(var region in snapshot.Regions)
        {
            ulong start=Address(region.StartAddress);
            if(start>ulong.MaxValue-(ulong)region.Bytes.Length) throw new LabException("invalid_snapshot","Native region address overflow.");
            ulong end=start+(ulong)region.Bytes.Length;
            if(ranges.Any(r=>start<r.End&&end>r.Start)) throw new LabException("invalid_snapshot","Native regions overlap.");
            ranges.Add((start,end));
        }
        foreach(var map in snapshot.Maps)
            if(map.RegionIndex<0||map.RegionIndex>=snapshot.Regions.Count||map.NativeStartOffset<0||
                map.NativeEndOffset<map.NativeStartOffset||map.NativeEndOffset>snapshot.Regions[map.RegionIndex].Bytes.Length)
                throw new LabException("invalid_snapshot","Native map lies outside its declared region.");
    }
    public static NativeListing Decode(NativeSnapshot snapshot)
    {
        Validate(snapshot);
        var instructions=new List<DecodedInstruction>();
        var annotations=new List<CodeAnnotation>();
        var warnings=new List<string> { "imported_identity_not_live_verified","snapshot_is_not_proof_of_earlier_run_code" };
        var formatter=new IntelFormatter();
        var output=new TextOutput();
        for(int regionIndex=0;regionIndex<snapshot.Regions.Count;regionIndex++)
        {
            var region=snapshot.Regions[regionIndex];
            ulong start=Address(region.StartAddress);
            var decoder=Iced.Intel.Decoder.Create(64,new ByteArrayCodeReader(region.Bytes)); decoder.IP=start;
            while(decoder.IP-start<(ulong)region.Bytes.Length)
            {
                if(instructions.Count>=10000) { warnings.Add("decode_instruction_cap"); break; }
                int offset=(int)(decoder.IP-start);
                decoder.Decode(out Instruction instruction);
                if(instruction.Code==Code.INVALID)
                { warnings.Add("invalid_instruction_region_"+regionIndex+"_offset_"+offset); break; }
                output.Text.Clear(); formatter.Format(instruction,output);
                string? target=instruction.FlowControl is FlowControl.Call or FlowControl.UnconditionalBranch or FlowControl.ConditionalBranch
                    ? "0x"+instruction.NearBranchTarget.ToString("x",CultureInfo.InvariantCulture):null;
                instructions.Add(new(regionIndex,offset,"0x"+instruction.IP.ToString("x",CultureInfo.InvariantCulture),
                    Convert.ToHexStringLower(region.Bytes.AsSpan(offset,instruction.Length)),output.Text.ToString(),
                    instruction.FlowControl.ToString(),target));
                if(instruction.FlowControl is FlowControl.Call or FlowControl.IndirectCall)
                    annotations.Add(new("call-site/v1","high",regionIndex,offset,offset+instruction.Length,
                        "A call instruction is present.",["Inlining intent and cause are not established.","Target may be a runtime helper."]));
                if(instruction.FlowControl==FlowControl.ConditionalBranch&&instructions.Count>=2&&
                    instructions[^2].Region==regionIndex&&instructions[^2].Text.StartsWith("cmp ",StringComparison.Ordinal))
                    annotations.Add(new("comparison-branch/v1","low",regionIndex,instructions[^2].Offset,offset+instruction.Length,
                        "Comparison followed by a conditional branch; bounds-check interpretation is a hypothesis.",
                        ["Ordinary application control flow.","Type, null, or argument validation."]));
            }
        }
        return new(Version,"imported_snapshot_unverified",instructions,annotations,warnings,snapshot.Maps);
    }
    public static NativeDiff Diff(NativeSnapshot original,NativeSnapshot candidate,int limit=1000)
    {
        Validate(original); Validate(candidate);
        if(limit is <1 or >10000) throw new LabException("resource_limit","Diff limit must be 1..10000.");
        var changes=new List<ByteDifference>();
        for(int region=0;region<Math.Max(original.Regions.Count,candidate.Regions.Count);region++)
        {
            byte[] a=region<original.Regions.Count?original.Regions[region].Bytes:[];
            byte[] b=region<candidate.Regions.Count?candidate.Regions[region].Bytes:[];
            for(int offset=0;offset<Math.Max(a.Length,b.Length);offset++)
            {
                int? left=offset<a.Length?a[offset]:null,right=offset<b.Length?b[offset]:null;
                if(left==right) continue;
                if(changes.Count==limit) return new(changes,true);
                changes.Add(new(region,offset,left,right));
            }
        }
        return new(changes,false);
    }
}
