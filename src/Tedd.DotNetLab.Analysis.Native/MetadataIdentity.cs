using System.Collections.Immutable;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Analysis.Native;

internal static class MetadataIdentity
{
    [ThreadStatic] private static int depth;
    private sealed class Recursion : IDisposable
    {
        public Recursion() { if(depth>=32) throw new LabException("resource_limit","Metadata reference nesting exceeds 32."); depth++; }
        public void Dispose()=>depth--;
    }
    private static string Bound(string value)=>value.Length<=8192?value:throw new LabException("resource_limit","Metadata identity exceeds 8 KiB.");
    internal static readonly Types Provider=new();
    internal sealed class Types : ISignatureTypeProvider<string,object?>
    {
        public string GetArrayType(string element,ArrayShape shape)=>element+"["+shape.Rank+":"+string.Join(',',shape.Sizes)+":"+string.Join(',',shape.LowerBounds)+"]";
        public string GetByReferenceType(string element)=>element+"&";
        public string GetFunctionPointerType(MethodSignature<string> signature)=>throw new LabException("capability_unsupported","Function pointer signatures are unsupported.");
        public string GetGenericInstantiation(string type,ImmutableArray<string> arguments)=>Bound(type+"<"+string.Join(',',arguments)+">");
        public string GetGenericMethodParameter(object? context,int index)=>"!!"+index;
        public string GetGenericTypeParameter(object? context,int index)=>"!"+index;
        public string GetModifiedType(string modifier,string type,bool required)=>(required?"modreq(":"modopt(")+modifier+")"+type;
        public string GetPinnedType(string type)=>throw new LabException("capability_unsupported","Pinned locals are unsupported.");
        public string GetPointerType(string element)=>throw new LabException("capability_unsupported","Unmanaged pointer signatures are unsupported.");
        public string GetPrimitiveType(PrimitiveTypeCode type)=>type.ToString();
        public string GetSZArrayType(string element)=>element+"[]";
        public string GetTypeFromDefinition(MetadataReader reader,TypeDefinitionHandle handle,byte rawTypeKind)
        {
            using var recursion=new Recursion();
            var type=reader.GetTypeDefinition(handle); var parent=type.GetDeclaringType();
            return parent.IsNil?"["+Assembly(reader)+"]"+reader.GetString(type.Namespace)+"."+reader.GetString(type.Name):
                GetTypeFromDefinition(reader,parent,rawTypeKind)+"+"+reader.GetString(type.Name);
        }
        public string GetTypeFromReference(MetadataReader reader,TypeReferenceHandle handle,byte rawTypeKind)
        {
            using var recursion=new Recursion();
            var type=reader.GetTypeReference(handle);
            string scope=type.ResolutionScope.Kind switch
            {
                HandleKind.AssemblyReference=>AssemblyRef(reader,(AssemblyReferenceHandle)type.ResolutionScope),
                HandleKind.ModuleDefinition=>Assembly(reader),
                HandleKind.TypeReference=>GetTypeFromReference(reader,(TypeReferenceHandle)type.ResolutionScope,rawTypeKind),
                _=>throw new LabException("capability_unsupported","Unsupported type resolution scope.")
            };
            return type.ResolutionScope.Kind==HandleKind.TypeReference?scope+"+"+reader.GetString(type.Name):
                "["+scope+"]"+reader.GetString(type.Namespace)+"."+reader.GetString(type.Name);
        }
        public string GetTypeFromSpecification(MetadataReader reader,object? context,TypeSpecificationHandle handle,byte rawTypeKind)
        { using var recursion=new Recursion(); var type=reader.GetTypeSpecification(handle); SignatureBounds.Check(reader,type.Signature,0); return Bound(type.DecodeSignature(this,context)); }
    }
    private static string Assembly(MetadataReader reader)
    { var a=reader.GetAssemblyDefinition(); return reader.GetString(a.Name)+","+a.Version+","+reader.GetString(a.Culture)+","+Convert.ToHexString(reader.GetBlobBytes(a.PublicKey)); }
    private static string AssemblyRef(MetadataReader reader,AssemblyReferenceHandle handle)
    { var a=reader.GetAssemblyReference(handle); return reader.GetString(a.Name)+","+a.Version+","+reader.GetString(a.Culture)+","+Convert.ToHexString(reader.GetBlobBytes(a.PublicKeyOrToken))+","+(int)a.Flags; }
    private static string Signature(MethodSignature<string> signature)=>
        signature.Header.RawValue+":"+signature.GenericParameterCount+":"+signature.ReturnType+"("+string.Join(',',signature.ParameterTypes)+")";
    internal static MethodSignature<string> CallSignature(MetadataReader reader,EntityHandle handle)
    {
        using var recursion=new Recursion();
        if(handle.Kind==HandleKind.MethodSpecification) return CallSignature(reader,reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method);
        if(handle.Kind==HandleKind.MethodDefinition)
        { var method=reader.GetMethodDefinition((MethodDefinitionHandle)handle); SignatureBounds.Check(reader,method.Signature,1); return method.DecodeSignature(Provider,null); }
        if(handle.Kind==HandleKind.MemberReference)
        { var member=reader.GetMemberReference((MemberReferenceHandle)handle); SignatureBounds.Check(reader,member.Signature,1); return member.DecodeMethodSignature(Provider,null); }
        throw new LabException("invalid_cil","Expected a method reference.");
    }
    internal static string Key(MetadataReader reader,EntityHandle handle)
    {
        using var recursion=new Recursion(); return Bound(handle.Kind switch
        {
        HandleKind.TypeDefinition=>Provider.GetTypeFromDefinition(reader,(TypeDefinitionHandle)handle,0),
        HandleKind.TypeReference=>Provider.GetTypeFromReference(reader,(TypeReferenceHandle)handle,0),
        HandleKind.TypeSpecification=>Provider.GetTypeFromSpecification(reader,null,(TypeSpecificationHandle)handle,0),
        HandleKind.MethodDefinition=>Method(reader,(MethodDefinitionHandle)handle),
        HandleKind.FieldDefinition=>Field(reader,(FieldDefinitionHandle)handle),
        HandleKind.MemberReference=>Member(reader,(MemberReferenceHandle)handle),
        HandleKind.MethodSpecification=>Specification(reader,(MethodSpecificationHandle)handle),
        HandleKind.StandaloneSignature=>Locals(reader,(StandaloneSignatureHandle)handle),
        _=>throw new LabException("capability_unsupported","Unsupported metadata reference: "+handle.Kind)
        });
    }
    private static string Locals(MetadataReader reader,StandaloneSignatureHandle handle)
    { var local=reader.GetStandaloneSignature(handle); SignatureBounds.Check(reader,local.Signature,7); return "locals:"+string.Join(',',local.DecodeLocalSignature(Provider,null)); }
    private static string Method(MetadataReader reader,MethodDefinitionHandle handle)
    { var m=reader.GetMethodDefinition(handle); SignatureBounds.Check(reader,m.Signature,1); return Key(reader,m.GetDeclaringType())+"::"+reader.GetString(m.Name)+":"+Signature(m.DecodeSignature(Provider,null)); }
    private static string Field(MetadataReader reader,FieldDefinitionHandle handle)
    { var f=reader.GetFieldDefinition(handle); SignatureBounds.Check(reader,f.Signature,6); return Key(reader,f.GetDeclaringType())+"::"+reader.GetString(f.Name)+":"+f.DecodeSignature(Provider,null); }
    private static string Member(MetadataReader reader,MemberReferenceHandle handle)
    { var m=reader.GetMemberReference(handle); SignatureBounds.Check(reader,m.Signature,m.GetKind()==MemberReferenceKind.Field?6:1); return Key(reader,m.Parent)+"::"+reader.GetString(m.Name)+":"+
        (m.GetKind()==MemberReferenceKind.Field?m.DecodeFieldSignature(Provider,null):Signature(m.DecodeMethodSignature(Provider,null))); }
    private static string Specification(MetadataReader reader,MethodSpecificationHandle handle)
    { var m=reader.GetMethodSpecification(handle); SignatureBounds.Check(reader,m.Signature,10); return Key(reader,m.Method)+"<"+string.Join(',',m.DecodeSignature(Provider,null))+">"; }
    internal static IEnumerable<EntityHandle> Handles(MetadataReader reader)
    {
        foreach(var table in new[]{TableIndex.TypeDef,TableIndex.TypeRef,TableIndex.TypeSpec,TableIndex.MethodDef,TableIndex.MemberRef,TableIndex.MethodSpec,TableIndex.Field,TableIndex.StandAloneSig})
        {
            int count=reader.GetTableRowCount(table); if(count>100000) throw new LabException("resource_limit","Metadata table exceeds import budget.");
            for(int row=1;row<=count;row++) yield return MetadataTokens.EntityHandle(table,row);
        }
    }
    internal static void ValidateOperand(MetadataReader reader,uint token,OperandType operand)
    {
        if(operand==OperandType.InlineString)
        { if((token>>24)!=0x70) throw new LabException("invalid_cil","Expected a user string token."); _=reader.GetUserString(MetadataTokens.UserStringHandle((int)(token&0xffffff))); return; }
        var handle=MetadataTokens.EntityHandle((int)token);
        bool method=handle.Kind is HandleKind.MethodDefinition or HandleKind.MethodSpecification||handle.Kind==HandleKind.MemberReference&&reader.GetMemberReference((MemberReferenceHandle)handle).GetKind()==MemberReferenceKind.Method;
        bool field=handle.Kind==HandleKind.FieldDefinition||handle.Kind==HandleKind.MemberReference&&reader.GetMemberReference((MemberReferenceHandle)handle).GetKind()==MemberReferenceKind.Field;
        bool type=handle.Kind is HandleKind.TypeDefinition or HandleKind.TypeReference or HandleKind.TypeSpecification;
        if(!(operand switch { OperandType.InlineMethod=>method,OperandType.InlineField=>field,OperandType.InlineType=>type,OperandType.InlineTok=>method||field||type,_=>false }))
            throw new LabException("invalid_cil","Metadata operand has the wrong token kind.");
        _=Key(reader,handle);
    }
    internal static string Layout(MetadataReader reader,TypeDefinitionHandle handle)
    {
        var type=reader.GetTypeDefinition(handle); var layout=type.GetLayout();
        return (int)type.Attributes+":"+layout.Size+":"+layout.PackingSize+":"+(type.BaseType.IsNil?"":Key(reader,type.BaseType))+":"+
            string.Join(';',type.GetFields().Select(h=> { var f=reader.GetFieldDefinition(h); return Key(reader,h)+":"+(int)f.Attributes+":"+f.GetOffset(); }));
    }
}
