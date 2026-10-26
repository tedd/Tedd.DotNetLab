using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Analysis.Native;

public record StoredMethod(string ModuleHash,string ModuleMvid,string Token,string DeclaringType,string Name,
    string SignatureHex,bool Generic,bool Constructor,bool Static,bool HasBody)
{
    public string Id => "method-"+Wire.Hash(new { ModuleHash,ModuleMvid,Token,SignatureHex })[..24];
}
public record StoredIl(StoredMethod Method,string IlHash,byte[] Bytes,int MaxStack,string LocalSignature,
    bool InitLocals,IReadOnlyList<StoredExceptionRegion> Exceptions,string Qualification="retained_build_not_live_body");
public record StoredExceptionRegion(string Kind,int TryOffset,int TryLength,int HandlerOffset,int HandlerLength,
    int FilterOffset,string CatchType);
public record ProbeHelperReferences(uint EnterToken,uint ExitToken,uint ExceptionalExitToken=0);
public record ProbeLocalSignature(uint Token,int Count);
public static class PeInspector
{
    public static ProbeHelperReferences ProbeReferences(string path)
    {
        Bound(path); using var stream=File.OpenRead(path); using var pe=new PEReader(stream);
        var reader=pe.GetMetadataReader(); uint enter=0,exit=0,exceptional=0;
        foreach(var handle in reader.MemberReferences)
        {
            var member=reader.GetMemberReference(handle);
            if(member.Parent.Kind!=HandleKind.TypeReference) continue;
            var type=reader.GetTypeReference((TypeReferenceHandle)member.Parent);
            if(reader.GetString(type.Namespace)!="Tedd.DotNetLab.Probes"||reader.GetString(type.Name)!="ProbeRuntime"||
                type.ResolutionScope.Kind!=HandleKind.AssemblyReference) continue;
            var assembly=reader.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope);
            if(reader.GetString(assembly.Name)!="Tedd.DotNetLab.Probes"||Convert.ToHexStringLower(reader.GetBlobBytes(member.Signature))!="00010108") continue;
            string name=reader.GetString(member.Name); uint token=(uint)MetadataTokens.GetToken(handle);
            if(name=="Enter") { if(enter!=0) throw new LabException("ambiguous_helper","Multiple Enter references."); enter=token; }
            if(name=="Exit") { if(exit!=0) throw new LabException("ambiguous_helper","Multiple Exit references."); exit=token; }
            if(name=="ExitExceptional") { if(exceptional!=0) throw new LabException("ambiguous_helper","Multiple exceptional exit references."); exceptional=token; }
        }
        if(enter==0||exit==0) throw new LabException("capability_unsupported","Existing approved helper references are absent from the selected module.");
        return new(enter,exit,exceptional);
    }
    public static ProbeLocalSignature ProbeReturnSignature(string path,int originalLocals)
    {
        Bound(path);
        if(originalLocals is <0 or >=64) throw new LabException("capability_unsupported","No return-local capacity remains.");
        using var stream=File.OpenRead(path); using var pe=new PEReader(stream); var reader=pe.GetMetadataReader();
        if(reader.GetTableRowCount(TableIndex.StandAloneSig)>100000)
            throw new LabException("resource_limit","Local-signature table exceeds the metadata scan budget.");
        var choices=new List<ProbeLocalSignature>();
        for(int row=1;row<=reader.GetTableRowCount(TableIndex.StandAloneSig);row++)
        {
            var handle=MetadataTokens.StandaloneSignatureHandle(row);
            byte[] signature=reader.GetBlobBytes(reader.GetStandaloneSignature(handle).Signature);
            if(signature.Length<2||signature[0]!=7||signature[1]<=originalLocals||signature[1]>64||
                signature.Length!=signature[1]+2||signature.Skip(2).Any(t=>t!=8)) continue;
            choices.Add(new((uint)MetadataTokens.GetToken(handle),signature[1]));
        }
        return choices.OrderBy(s=>s.Count).ThenBy(s=>s.Token).FirstOrDefault()
            ??throw new LabException("capability_unsupported","An existing target-module Int32 local signature with return capacity is required.");
    }
    private static void Bound(string path)
    {
        if(new FileInfo(path).Length>67108864) throw new LabException("resource_limit","PE image exceeds 64 MiB.");
    }
    private static string TypeName(MetadataReader reader,TypeDefinitionHandle handle,int depth=0)
    {
        if(depth>64) throw new LabException("invalid_metadata","Nested type depth exceeds policy.");
        var type=reader.GetTypeDefinition(handle);
        string name=reader.GetString(type.Name);
        var parent=type.GetDeclaringType();
        if(!parent.IsNil) return TypeName(reader,parent,depth+1)+"+"+name;
        string ns=reader.GetString(type.Namespace);
        return ns.Length==0?name:ns+"."+name;
    }
    private static StoredMethod Describe(MetadataReader reader,MethodDefinitionHandle handle,string hash)
    {
        var method=reader.GetMethodDefinition(handle);
        string name=reader.GetString(method.Name);
        return new(hash,reader.GetGuid(reader.GetModuleDefinition().Mvid).ToString("D"),
            "0x"+MetadataTokens.GetToken(handle).ToString("x8"),TypeName(reader,method.GetDeclaringType()),name,
            Convert.ToHexStringLower(reader.GetBlobBytes(method.Signature)),method.GetGenericParameters().Count>0,
            name is ".ctor" or ".cctor",(method.Attributes&MethodAttributes.Static)!=0,method.RelativeVirtualAddress!=0);
    }
    public static Page<StoredMethod> Methods(string path,string hash,string filter="",int limit=100,string? cursor=null)
    {
        Bound(path);
        if(limit is <1 or >1000||filter.Length>256) throw new LabException("resource_limit","Invalid metadata query bounds.");
        int after=0;
        if(cursor is not null&&!int.TryParse(cursor,out after)) throw new LabException("invalid_cursor","Invalid metadata cursor.");
        try
        {
            using var stream=File.OpenRead(path);
            using var pe=new PEReader(stream);
            var reader=pe.GetMetadataReader();
            if(reader.MethodDefinitions.Count>100000) throw new LabException("resource_limit","Method table exceeds policy.");
            if(after<0||after>=reader.MethodDefinitions.Count&&after!=0) throw new LabException("invalid_cursor","Metadata cursor exceeds table.");
            var methods=new List<StoredMethod>();
            foreach(var handle in reader.MethodDefinitions)
            {
                int row=MetadataTokens.GetRowNumber(handle);
                if(row<=after) continue;
                var method=Describe(reader,handle,hash);
                if(!(method.DeclaringType+"."+method.Name).Contains(filter,StringComparison.Ordinal)) continue;
                if(methods.Count==limit) return new(methods,true,MetadataTokens.GetRowNumber(MetadataTokens.MethodDefinitionHandle(
                    Convert.ToInt32(methods[^1].Token[2..],16)&0xffffff)).ToString());
                methods.Add(method);
            }
            return new(methods,false,null);
        }
        catch(BadImageFormatException) { throw new LabException("invalid_metadata","PE image has invalid managed metadata."); }
    }
    public static StoredIl Il(string path,string hash,string token)
    {
        Bound(path);
        try
        {
            int value=Convert.ToInt32(token.StartsWith("0x",StringComparison.OrdinalIgnoreCase)?token[2..]:token,16);
            if((value&unchecked((int)0xff000000))!=0x06000000||(value&0xffffff)==0)
                throw new LabException("invalid_metadata","Expected a MethodDef token.");
            using var stream=File.OpenRead(path);
            using var pe=new PEReader(stream);
            var reader=pe.GetMetadataReader();
            var handle=MetadataTokens.MethodDefinitionHandle(value&0xffffff);
            var descriptor=Describe(reader,handle,hash);
            var method=reader.GetMethodDefinition(handle);
            if(!descriptor.HasBody) throw new LabException("not_collected","Method has no retained CIL body.");
            var body=pe.GetMethodBody(method.RelativeVirtualAddress);
            byte[] bytes=body.GetILBytes()??[];
            var exceptions=body.ExceptionRegions.Select(e=>new StoredExceptionRegion(e.Kind.ToString(),e.TryOffset,e.TryLength,
                e.HandlerOffset,e.HandlerLength,e.FilterOffset,"0x"+MetadataTokens.GetToken(e.CatchType).ToString("x8"))).ToArray();
            return new(descriptor,Convert.ToHexStringLower(SHA256.HashData(bytes)),bytes,body.MaxStack,
                "0x"+MetadataTokens.GetToken(body.LocalSignature).ToString("x8"),body.LocalVariablesInitialized,exceptions);
        }
        catch(Exception e) when(e is BadImageFormatException or FormatException or ArgumentException or OverflowException)
        { throw new LabException("invalid_metadata","Invalid method token or managed image."); }
    }
}
