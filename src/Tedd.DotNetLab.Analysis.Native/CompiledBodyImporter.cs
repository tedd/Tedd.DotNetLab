using System.Buffers.Binary;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Analysis.Native;

public static class CompiledBodyImporter
{
    public static byte[] Import(string candidatePath,string candidateMethodToken,string targetPath,StoredMethod target)
    {
        if(new FileInfo(candidatePath).Length>67108864||new FileInfo(targetPath).Length>67108864)
            throw new LabException("resource_limit","Compiled images must not exceed 64 MiB.");
        try
        {
            using var candidateStream=File.OpenRead(candidatePath); using var candidatePe=new PEReader(candidateStream);
            using var targetStream=File.OpenRead(targetPath); using var targetPe=new PEReader(targetStream);
            var source=candidatePe.GetMetadataReader(); var destination=targetPe.GetMetadataReader();
            int candidateToken=Convert.ToInt32(candidateMethodToken.StartsWith("0x",StringComparison.Ordinal)?candidateMethodToken[2..]:candidateMethodToken,16);
            if((candidateToken>>24)!=6) throw new LabException("invalid_metadata","Expected a candidate MethodDef token.");
            var sourceHandle=MetadataTokens.MethodDefinitionHandle(candidateToken&0xffffff);
            var targetHandle=MetadataTokens.MethodDefinitionHandle(Convert.ToInt32(target.Token[2..],16)&0xffffff);
            if(MetadataIdentity.Key(source,sourceHandle)!=MetadataIdentity.Key(destination,targetHandle))
                throw new LabException("method_identity_changed","Compiled method owner, name, calling convention, or signature differs from the live target.");
            var targetTypes=destination.TypeDefinitions.ToDictionary(h=>MetadataIdentity.Key(destination,h));
            foreach(var handle in source.TypeDefinitions)
                if(targetTypes.TryGetValue(MetadataIdentity.Key(source,handle),out var existing)&&MetadataIdentity.Layout(source,handle)!=MetadataIdentity.Layout(destination,existing))
                    throw new LabException("type_layout_changed","Compiled candidate changes an existing type layout or field contract.");
            var map=new Dictionary<string,int>(StringComparer.Ordinal);
            foreach(var handle in MetadataIdentity.Handles(destination))
            {
                try { map.TryAdd(MetadataIdentity.Key(destination,handle),MetadataTokens.GetToken(handle)); }
                catch(LabException error) when(error.Code=="capability_unsupported") { }
                catch(BadImageFormatException) { } // StandAloneSig can also contain calli signatures.
            }
            int Map(uint token)
            {
                var key=MetadataIdentity.Key(source,MetadataTokens.EntityHandle((int)token));
                return map.TryGetValue(key,out int mapped)?mapped:throw new LabException("metadata_reference_missing","Candidate requires metadata absent from the loaded module: "+key);
            }
            var method=source.GetMethodDefinition(sourceHandle);
            if(method.RelativeVirtualAddress==0||method.GetGenericParameters().Count!=0)
                throw new LabException("capability_unsupported","Candidate must have a concrete nongeneric managed body.");
            var body=candidatePe.GetMethodBody(method.RelativeVirtualAddress);
            if(body.ExceptionRegions.Length!=0) throw new LabException("capability_unsupported","Compiled replacement exception regions require separate qualification.");
            byte[] code=body.GetILBytes()??[];
            // User strings are heap offsets, not metadata table rows. Index only strings
            // actually referenced by retained destination bodies, without loading them.
            Dictionary<string,int>? strings=null;
            for(int offset=0;offset<code.Length;)
            {
                ushort opcode=code[offset++]; if(opcode==0xfe) opcode=(ushort)(0xfe00|code[offset++]);
                if(!ManagedBodyPolicy.Allowed(opcode)) throw new LabException("capability_unsupported","Compiled method uses an unsupported opcode.");
                var op=ManagedBodyPolicy.Opcodes[opcode]; int length=ManagedBodyPolicy.OperandSize(op.OperandType,code.AsSpan(offset));
                if(length>code.Length-offset) throw new BadImageFormatException();
                if(op.OperandType is OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineType or OperandType.InlineTok)
                    BinaryPrimitives.WriteInt32LittleEndian(code.AsSpan(offset),Map(BinaryPrimitives.ReadUInt32LittleEndian(code.AsSpan(offset))));
                else if(op.OperandType==OperandType.InlineString)
                {
                    strings??=StringTokens(destination,targetPe);
                    uint token=BinaryPrimitives.ReadUInt32LittleEndian(code.AsSpan(offset));
                    if((token>>24)!=0x70) throw new BadImageFormatException();
                    string text=source.GetUserString(MetadataTokens.UserStringHandle((int)(token&0xffffff)));
                    if(!strings.TryGetValue(text,out int mapped)) throw new LabException("metadata_reference_missing","Candidate requires a string absent from the loaded module.");
                    BinaryPrimitives.WriteInt32LittleEndian(code.AsSpan(offset),mapped);
                }
                offset+=length;
            }
            byte[] complete=new byte[12+code.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(complete,(ushort)(body.LocalVariablesInitialized?0x3013:0x3003));
            BinaryPrimitives.WriteUInt16LittleEndian(complete.AsSpan(2),(ushort)Math.Max(1,body.MaxStack));
            BinaryPrimitives.WriteInt32LittleEndian(complete.AsSpan(4),code.Length);
            if(!body.LocalSignature.IsNil) BinaryPrimitives.WriteInt32LittleEndian(complete.AsSpan(8),Map((uint)MetadataTokens.GetToken(body.LocalSignature)));
            code.CopyTo(complete,12); ManagedBodyPolicy.Verify(complete,target,targetPath); return complete;
        }
        catch(Exception error) when(error is BadImageFormatException or ArgumentException or OverflowException or IndexOutOfRangeException or FormatException)
        { throw new LabException("invalid_metadata","Malformed compiled method or metadata."); }
    }
    private static Dictionary<string,int> StringTokens(MetadataReader reader,PEReader pe)
    {
        var result=new Dictionary<string,int>(StringComparer.Ordinal); long scanned=0;
        foreach(var handle in reader.MethodDefinitions)
        {
            var method=reader.GetMethodDefinition(handle); if(method.RelativeVirtualAddress==0) continue;
            var code=pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()??[]; scanned+=code.Length;
            if(scanned>67108864) throw new LabException("resource_limit","String reference scan exceeds 64 MiB.");
            for(int offset=0;offset<code.Length;)
            {
                ushort opcode=code[offset++]; if(opcode==0xfe) opcode=(ushort)(0xfe00|code[offset++]);
                if(!ManagedBodyPolicy.Opcodes.TryGetValue(opcode,out var op)) throw new BadImageFormatException();
                int length=ManagedBodyPolicy.OperandSize(op.OperandType,code.AsSpan(offset)); if(length>code.Length-offset) throw new BadImageFormatException();
                if(op.OperandType==OperandType.InlineString)
                { int token=BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(offset)); result.TryAdd(reader.GetUserString(MetadataTokens.UserStringHandle(token&0xffffff)),token); }
                offset+=length;
            }
        }
        return result;
    }
}
