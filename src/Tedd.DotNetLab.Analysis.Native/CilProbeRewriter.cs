using System.Buffers.Binary;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Analysis.Native;

public record ProbeRewrite(byte[] Body,VerifiedCil Verification,IReadOnlyDictionary<int,int> OffsetMap,string Scope,
    string ExceptionCoverage="unsupported");
public static class CilProbeRewriter
{
    public static ProbeRewrite Rewrite(byte[] original,CilPolicy policy,int probeId,uint enterToken,uint exitToken)
    {
        if(probeId is <0 or >255) throw new LabException("resource_limit","Probe identifier exceeds the slot budget.");
        var verified=CilVerifier.Verify(original,policy);
        if(verified.Instructions.Any(i=>i.Opcode is >=0x5b and <=0x5e||i.CallToken is uint token&&
            !(policy.Calls?.Any(c=>c.Token==token&&c.ProbeSafe)??false)))
            throw new LabException("capability_unsupported","Throwing arithmetic and unqualified calls require an exception-capable rewriter.");
        foreach(uint token in new[]{enterToken,exitToken})
            if(!(policy.Calls?.Any(c=>c.Token==token&&c.Arguments==1&&!c.ReturnsInt32&&c.ProbeSafe)??false))
                throw new LabException("invalid_cil","Probe helper references must be resolved approved Int32-to-void calls.");
        var offsets=new Dictionary<int,int>(); int size=10;
        foreach(var row in verified.Instructions) { offsets[row.Offset]=size; size+=(row.Return?10:0)+(row.BranchTarget is not null?5:row.Length); }
        if(size>65524) throw new LabException("resource_limit","Instrumented body exceeds its size budget.");
        byte[] body=new byte[12+size];
        BinaryPrimitives.WriteUInt16LittleEndian(body,(ushort)(0x3003|(verified.InitLocals?0x10:0)));
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2),(ushort)Math.Max(2,verified.MaxStack+1));
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4),(uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),verified.LocalSignature);
        int cursor=12;
        void Helper(uint token)
        { body[cursor++]=0x20; BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(cursor),probeId); cursor+=4;
            body[cursor++]=0x28; BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(cursor),token); cursor+=4; }
        Helper(enterToken);
        foreach(var row in verified.Instructions)
        {
            if(row.Return) Helper(exitToken);
            if(row.BranchTarget is int target)
            {
                body[cursor++]=row.Opcode<=0x37?(byte)(row.Opcode+0x0d):row.Opcode;
                BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(cursor),offsets[target]-(cursor-12+4)); cursor+=4;
            }
            else { original.AsSpan(verified.CodeOffset+row.Offset,row.Length).CopyTo(body.AsSpan(cursor)); cursor+=row.Length; }
        }
        return new(body,CilVerifier.Verify(body,policy),offsets,"synchronous-int32/nonthrowing/normal-completions");
    }
}
