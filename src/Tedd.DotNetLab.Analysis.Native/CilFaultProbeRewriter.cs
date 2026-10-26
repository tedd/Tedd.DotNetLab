using System.Buffers.Binary;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Analysis.Native;

public record FaultProbePolicy(uint ExceptionalExitToken,uint LocalSignatureToken,int LocalCount,int ReturnLocal);

public static class CilFaultProbeRewriter
{
    public static ProbeRewrite Rewrite(byte[] original,CilPolicy policy,int probeId,uint enterToken,uint exitToken,FaultProbePolicy fault)
    {
        var source=CilVerifier.Verify(original,policy);
        if(probeId is <0 or >255||fault.ReturnLocal!=policy.LocalCount||fault.LocalCount<=fault.ReturnLocal||fault.LocalCount>64||
            (fault.LocalSignatureToken>>24)!=0x11)
            throw new LabException("invalid_cil","Fault probes require a resolved existing Int32 signature with a distinct return local.");
        foreach(uint token in new[]{enterToken,exitToken,fault.ExceptionalExitToken})
            if(!(policy.Calls?.Any(c=>c.Token==token&&c.Arguments==1&&!c.ReturnsInt32&&c.ProbeSafe)??false))
                throw new LabException("invalid_cil","All fault probe helpers require approved Int32-to-void references.");
        if(source.Instructions.Any(i=>i.CallToken is uint token&&!(policy.Calls?.Any(c=>c.Token==token&&c.ProbeSafe)??false)))
            throw new LabException("capability_unsupported","Fault probes cannot call unqualified references.");
        var offsets=new Dictionary<int,int>(); int handler=10;
        foreach(var row in source.Instructions) { offsets[row.Offset]=handler; handler+=row.Return?7:row.BranchTarget is not null?5:row.Length; }
        int epilogue=handler+11,codeSize=epilogue+13,section=(12+codeSize+3)&~3;
        if(section+28>4096) throw new LabException("resource_limit","Fault probe body exceeds the native byte budget.");
        byte[] body=new byte[section+28];
        BinaryPrimitives.WriteUInt16LittleEndian(body,0x301b);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(2),(ushort)Math.Max(2,source.MaxStack+1));
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4),codeSize);
        BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(8),fault.LocalSignatureToken);
        int cursor=12;
        void Helper(uint token)
        {
            body[cursor++]=0x20; BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(cursor),probeId); cursor+=4;
            body[cursor++]=0x28; BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(cursor),token); cursor+=4;
        }
        void Branch(byte op,int target)
        { body[cursor++]=op; BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(cursor),target-(cursor-12+4)); cursor+=4; }
        Helper(enterToken);
        foreach(var row in source.Instructions)
        {
            if(row.Return) { body[cursor++]=0x13; body[cursor++]=(byte)fault.ReturnLocal; Branch(0xdd,epilogue); }
            else if(row.BranchTarget is int target) Branch(row.Opcode<=0x37?(byte)(row.Opcode+0x0d):row.Opcode,offsets[target]);
            else { original.AsSpan(source.CodeOffset+row.Offset,row.Length).CopyTo(body.AsSpan(cursor)); cursor+=row.Length; }
        }
        Helper(fault.ExceptionalExitToken); body[cursor++]=0xdc;
        Helper(exitToken); body[cursor++]=0x11; body[cursor++]=(byte)fault.ReturnLocal; body[cursor++]=0x2a;
        body[section]=0x41; body[section+1]=28;
        var region=new CilFaultRegion(10,handler-10,handler,11);
        int[] values=[4,region.TryOffset,region.TryLength,region.HandlerOffset,region.HandlerLength,0];
        for(int i=0;i<values.Length;i++) BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(section+4+i*4),values[i]);
        var expanded=policy with {LocalCount=fault.LocalCount,LocalSignatureToken=fault.LocalSignatureToken};
        return new(body,CilVerifier.Verify(body,expanded,region),offsets,"synchronous-int32/fault-unwind/inclusive-wall-time",
            "normal-exit-and-managed-fault-unwind");
    }
}
