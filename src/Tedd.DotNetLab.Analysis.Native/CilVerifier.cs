using System.Buffers.Binary;
using System.Security.Cryptography;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Analysis.Native;

public record ApprovedCilCall(uint Token,int Arguments,bool ReturnsInt32,bool ProbeSafe=false);
public record CilPolicy(string ModuleMvid,string MethodToken,string SignatureHex,bool Static,bool Generic,bool Constructor,
    int ArgumentCount,int LocalCount=0,uint LocalSignatureToken=0,IReadOnlyList<ApprovedCilCall>? Calls=null);
public record CilInstruction(int Offset,byte Opcode,int Length,int Pop,int Push,int? BranchTarget,bool Conditional,bool Return,
    uint? CallToken=null,bool Leave=false,bool HandlerEnd=false);
public record CilFaultRegion(int TryOffset,int TryLength,int HandlerOffset,int HandlerLength);
public record VerifiedCil(string Hash,int CodeOffset,int CodeSize,int MaxStack,int RequiredStack,uint LocalSignature,
    bool InitLocals,IReadOnlyList<CilInstruction> Instructions,string Qualification="restricted-int32-cil/1");

public static class CilVerifier
{
    private static LabException Invalid(string message)=>new("invalid_cil",message);
    public static VerifiedCil Verify(byte[] body,CilPolicy policy,CilFaultRegion? fault=null)
    {
        if(body.Length is <2 or >65536) throw Invalid("Complete CIL body exceeds limits.");
        if(!policy.Static||policy.Generic||policy.Constructor||policy.ArgumentCount is <0 or >4||policy.LocalCount is <0 or >64)
            throw new LabException("capability_unsupported","Only static nongeneric Int32 methods with bounded Int32 parameters/locals are supported.");
        if(policy.Calls is {Count:>256}||policy.Calls?.Select(c=>c.Token).Distinct().Count()!=policy.Calls?.Count)
            throw Invalid("Approved call references must be bounded and unique.");
        byte[] expectedSignature=[0,(byte)policy.ArgumentCount,8,..Enumerable.Repeat((byte)8,policy.ArgumentCount)];
        if(!policy.SignatureHex.Equals(Convert.ToHexStringLower(expectedSignature),StringComparison.Ordinal))
            throw new LabException("capability_unsupported","Signature is outside the Int32 body subset.");
        if(!Guid.TryParseExact(policy.ModuleMvid,"D",out _)||policy.MethodToken.Length!=10||!policy.MethodToken.StartsWith("0x06",StringComparison.Ordinal))
            throw Invalid("Explicit target-module and MethodDef identities are required.");
        int codeOffset,codeSize,maxStack; uint locals=0; bool init=false;
        if((body[0]&3)==2) { codeOffset=1; codeSize=body[0]>>2; maxStack=8; }
        else if((body[0]&3)==3)
        {
            if(body.Length<12) throw Invalid("Truncated fat header.");
            ushort flags=BinaryPrimitives.ReadUInt16LittleEndian(body);
            if((flags>>12)!=3||(flags&0x0fff&~(fault is null?0x13:0x1b))!=0||((flags&8)!=0)!=(fault is not null))
                throw Invalid("Unsupported header flags or exception sections.");
            codeOffset=12; maxStack=BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2));
            uint length=BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(4));
            if(length>65524) throw Invalid("Code length exceeds policy.");
            codeSize=(int)length; locals=BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)); init=(flags&0x10)!=0;
        }
        else throw Invalid("Invalid method-header format.");
        int sectionOffset=(codeOffset+codeSize+3)&~3;
        if(codeSize<1||codeSize>body.Length-codeOffset||fault is null&&codeSize!=body.Length-codeOffset||maxStack is <1 or >1024||locals!=policy.LocalSignatureToken||
            (locals==0&&policy.LocalCount!=0)||(locals!=0&&(locals>>24)!=0x11)) throw Invalid("Header length, stack, or local signature differs from policy.");
        if(policy.LocalCount>0&&!init) throw Invalid("Resolved locals require initlocals in the initial subset.");
        if(fault is not null)
        {
            if(codeOffset!=12||body.Length!=sectionOffset+28||fault.TryOffset!=10||fault.TryLength<=0||
                fault.HandlerOffset!=fault.TryOffset+fault.TryLength||fault.HandlerLength!=11||fault.HandlerOffset+fault.HandlerLength>=codeSize||
                body.AsSpan(codeOffset+codeSize,sectionOffset-codeOffset-codeSize).IndexOfAnyExcept((byte)0)>=0)
                throw Invalid("Fault region, body length, or alignment differs from the trusted layout.");
            var section=body.AsSpan(sectionOffset);
            if(section[0]!=0x41||section[1]!=28||section[2]!=0||section[3]!=0||
                BinaryPrimitives.ReadUInt32LittleEndian(section[4..])!=4||
                BinaryPrimitives.ReadUInt32LittleEndian(section[8..])!=fault.TryOffset||
                BinaryPrimitives.ReadUInt32LittleEndian(section[12..])!=fault.TryLength||
                BinaryPrimitives.ReadUInt32LittleEndian(section[16..])!=fault.HandlerOffset||
                BinaryPrimitives.ReadUInt32LittleEndian(section[20..])!=fault.HandlerLength||
                BinaryPrimitives.ReadUInt32LittleEndian(section[24..])!=0)
                throw Invalid("Expected one exact fat fault clause without a catch/filter token.");
        }
        var code=body.AsSpan(codeOffset,codeSize);
        var rows=new List<CilInstruction>();
        int offset=0;
        while(offset<code.Length)
        {
            if(rows.Count==10000) throw Invalid("Instruction count exceeds policy.");
            int start=offset; byte op=code[offset++]; int pop=0,push=0,operand=0; int? branch=null; uint? call=null;
            bool conditional=false,returns=false,leave=false,handlerEnd=false;
            if(op==0x00) { }
            else if(op is >=0x02 and <=0x05) { if(op-2>=policy.ArgumentCount) throw Invalid("Argument index is outside the signature."); push=1; }
            else if(op is >=0x06 and <=0x09) { if(op-6>=policy.LocalCount) throw Invalid("Local index is outside the resolved signature."); push=1; }
            else if(op is >=0x0a and <=0x0d) { if(op-10>=policy.LocalCount) throw Invalid("Local index is outside the resolved signature."); pop=1; }
            else if(op is 0x0e or 0x11 or 0x13)
            { operand=1; if(op==0x13) pop=1; else push=1; }
            else if(op is >=0x15 and <=0x1e) push=1;
            else if(op==0x1f) { operand=1; push=1; }
            else if(op==0x20) { operand=4; push=1; }
            else if(op==0x25) { pop=1; push=2; }
            else if(op==0x26) pop=1;
            else if(op==0x28) operand=4;
            else if(op==0x2a) { pop=1; returns=true; }
            else if(op is >=0x2b and <=0x37 or >=0x38 and <=0x44)
            {
                operand=op<=0x37?1:4;
                int kind=op<=0x37?op-0x2b:op-0x38;
                conditional=kind!=0; pop=kind is 1 or 2?1:kind==0?0:2;
            }
            else if(op is >=0x58 and <=0x64) { pop=2; push=1; }
            else if(op is 0x65 or 0x66) { pop=1; push=1; }
            else if(fault is not null&&op==0xdd) { operand=4; leave=true; }
            else if(fault is not null&&op==0xdc) handlerEnd=true;
            else throw new LabException("capability_unsupported","Opcode or prefix is outside the restricted CIL subset: 0x"+op.ToString("x2"));
            if(operand>code.Length-offset) throw Invalid("Truncated instruction operand.");
            if(op is 0x0e or 0x11 or 0x13)
            {
                int maximum=op==0x0e?policy.ArgumentCount:policy.LocalCount;
                if(code[offset]>=maximum) throw Invalid("Argument/local index exceeds its resolved definition.");
            }
            if(op==0x28)
            {
                call=BinaryPrimitives.ReadUInt32LittleEndian(code[offset..]);
                var approved=policy.Calls?.SingleOrDefault(c=>c.Token==call);
                if(approved is null||approved.Arguments is <0 or >4||(call>>24) is not (0x06 or 0x0a))
                    throw Invalid("Call token is not an approved existing target-module definition/reference.");
                pop=approved.Arguments; push=approved.ReturnsInt32?1:0;
            }
            if(op is >=0x2b and <=0x44||leave)
            {
                int displacement=operand==1?unchecked((sbyte)code[offset]):BinaryPrimitives.ReadInt32LittleEndian(code[offset..]);
                long destination=(long)offset+operand+displacement;
                if(destination<0||destination>=code.Length) throw Invalid("Branch is outside the method body.");
                branch=(int)destination;
            }
            offset+=operand; rows.Add(new(start,op,offset-start,pop,push,branch,conditional,returns,call,leave,handlerEnd));
        }
        var instructions=rows.ToDictionary(r=>r.Offset);
        foreach(var row in rows) if(row.BranchTarget is int target&&!instructions.ContainsKey(target)) throw Invalid("Branch targets an operand.");
        var depths=new Dictionary<int,int>(); var pending=new Queue<(int Offset,int Depth)>(); pending.Enqueue((0,0));
        if(fault is not null)
        {
            if(!instructions.ContainsKey(fault.TryOffset)||!instructions.ContainsKey(fault.HandlerOffset)||
                !instructions.ContainsKey(fault.HandlerOffset+fault.HandlerLength)) throw Invalid("Fault ranges must align with instructions.");
            pending.Enqueue((fault.HandlerOffset,0));
        }
        int Region(int position)=>fault is null?0:position>=fault.TryOffset&&position<fault.HandlerOffset?1:
            position>=fault.HandlerOffset&&position<fault.HandlerOffset+fault.HandlerLength?2:0;
        int required=0; bool sawReturn=false;
        while(pending.Count>0)
        {
            var (position,depth)=pending.Dequeue();
            if(depths.TryGetValue(position,out int previous)) { if(previous!=depth) throw Invalid("Control-flow merge has inconsistent stack depth."); continue; }
            depths.Add(position,depth); var row=instructions[position];
            if(depth<row.Pop) throw Invalid("Evaluation-stack underflow.");
            int next=depth-row.Pop+row.Push; required=Math.Max(required,next);
            if(required>maxStack) throw Invalid("Declared maxstack is insufficient.");
            if(row.Return) { if(next!=0||Region(position)!=0) throw Invalid("Return stack or protected-region exit is invalid."); sawReturn=true; continue; }
            if(row.HandlerEnd)
            {
                if(Region(position)!=2||next!=0||position+row.Length!=fault!.HandlerOffset+fault.HandlerLength)
                    throw Invalid("Fault handler must terminate with an empty-stack endfinally.");
                continue;
            }
            if(row.BranchTarget is int target)
            {
                if(row.Leave)
                {
                    if(Region(position)!=1||target!=fault!.HandlerOffset+fault.HandlerLength||next!=0)
                        throw Invalid("Trusted leave must carry an empty stack to the normal epilogue.");
                }
                else if(Region(position)!=Region(target)) throw Invalid("A branch crosses a protected-region boundary.");
                pending.Enqueue((target,next));
            }
            if(row.BranchTarget is null||row.Conditional)
            {
                int fallthrough=row.Offset+row.Length;
                if(fallthrough>=code.Length) throw Invalid("Control flow falls through the method end.");
                if(Region(position)!=Region(fallthrough)&&!(Region(position)==0&&fallthrough==fault!.TryOffset&&next==0))
                    throw Invalid("Illegal protected-region fallthrough.");
                pending.Enqueue((fallthrough,next));
            }
        }
        if(!sawReturn||depths.Count!=rows.Count) throw Invalid("Body has no reachable return or contains unreachable instructions.");
        return new(Convert.ToHexStringLower(SHA256.HashData(body)),codeOffset,codeSize,maxStack,required,locals,init,rows);
    }
}
