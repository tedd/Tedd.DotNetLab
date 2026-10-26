using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Analysis.Native;

// Structural admission for compiler-produced managed bodies. The CLR remains the
// type verifier; this is not a sandbox for untrusted managed code.
public static class ManagedBodyPolicy
{
    public const string Qualification="managed-body-cil/1";
    public static readonly IReadOnlyDictionary<ushort,OpCode> Opcodes=typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static)
        .Where(f=>f.FieldType==typeof(OpCode)).Select(f=>(OpCode)f.GetValue(null)!)
        .Where(o=>o.OpCodeType!=OpCodeType.Nternal).ToDictionary(o=>unchecked((ushort)o.Value));
    private static LabException Invalid(string message)=>new("invalid_cil",message);
    public static bool Allowed(ushort opcode)=>Opcodes.ContainsKey(opcode)&&opcode is not
        (0x27 or 0x29 or 0xc2 or 0xc3 or 0xc6 or 0xdc or 0xdd or 0xde or
         0xfe00 or 0xfe0f or 0xfe11 or 0xfe14 or 0xfe17 or 0xfe18 or 0xfe19 or 0xfe1a or 0xfe1d);
    public static int StackCount(StackBehaviour value)=>value switch
    {
        StackBehaviour.Pop0 or StackBehaviour.Push0=>0,
        StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref or StackBehaviour.Push1 or
            StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4 or StackBehaviour.Pushr8 or StackBehaviour.Pushref=>1,
        StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi or StackBehaviour.Popi_popi8 or
            StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8 or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi or StackBehaviour.Push1_push1=>2,
        StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_pop1 or StackBehaviour.Popref_popi_popi or
            StackBehaviour.Popref_popi_popi8 or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8 or StackBehaviour.Popref_popi_popref=>3,
        _=>-1
    };
    public static VerifiedCil Verify(byte[] body,StoredMethod method,string imagePath)
    {
        try
        {
            using var stream=File.OpenRead(imagePath); using var pe=new PEReader(stream); var reader=pe.GetMetadataReader();
            var definition=reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(Convert.ToInt32(method.Token[2..],16)&0xffffff));
            SignatureBounds.Check(reader,definition.Signature,1);
            var signature=definition.DecodeSignature(MetadataIdentity.Provider,null);
            if(method.Generic||method.Constructor||reader.GetTypeDefinition(definition.GetDeclaringType()).GetGenericParameters().Count!=0||
                signature.Header.CallingConvention!=SignatureCallingConvention.Default)
                throw new LabException("capability_unsupported","Managed replacement requires a nongeneric, nonconstructor method with a default calling convention.");
            var layout=ReadHeader(body); int argumentCount=signature.ParameterTypes.Length+(signature.Header.IsInstance?1:0);
            if(layout.Locals!=0) SignatureBounds.Check(reader,reader.GetStandaloneSignature(MetadataTokens.StandaloneSignatureHandle((int)(layout.Locals&0xffffff))).Signature,7);
            int localCount=layout.Locals==0?0:reader.GetStandaloneSignature(MetadataTokens.StandaloneSignatureHandle((int)(layout.Locals&0xffffff)))
                .DecodeLocalSignature(MetadataIdentity.Provider,null).Length;
            if(localCount>64||argumentCount>64) throw Invalid("Argument or local count exceeds 64.");
            var rows=new List<(int Offset,int Length,int Pop,int Push,int[] Targets,bool Fallthrough,bool Terminal)>();
            var code=body.AsSpan(layout.Offset,layout.Size); int offset=0; var operandOffsets=new HashSet<int>();
            while(offset<code.Length)
            {
                int start=offset; ushort value=code[offset++];
                if(value==0xfe) { if(offset==code.Length) throw Invalid("Truncated opcode."); value=(ushort)(0xfe00|code[offset++]); }
                if(!Allowed(value)) throw new LabException("capability_unsupported","Unsupported managed opcode: 0x"+value.ToString("x4"));
                var op=Opcodes[value]; int length=OperandSize(op.OperandType,code[offset..]);
                if(length>code.Length-offset) throw Invalid("Truncated operand.");
                int operand=offset; offset+=length; int pop=StackCount(op.StackBehaviourPop),push=StackCount(op.StackBehaviourPush);
                int index=value is >=2 and <=5?value-2:value is >=6 and <=9?value-6:value is >=10 and <=13?value-10:
                    op.OperandType==OperandType.ShortInlineVar?code[operand]:op.OperandType==OperandType.InlineVar?BinaryPrimitives.ReadUInt16LittleEndian(code[operand..]):-1;
                if(index>=0)
                {
                    bool argument=value is >=2 and <=5 or >=0x0e and <=0x10 or >=0xfe09 and <=0xfe0b;
                    if(index>=(argument?argumentCount:localCount)) throw Invalid("Argument or local index exceeds its signature.");
                }
                if(op.OperandType is OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineType or OperandType.InlineTok or OperandType.InlineString)
                {
                    uint token=BinaryPrimitives.ReadUInt32LittleEndian(code[operand..]);
                    MetadataIdentity.ValidateOperand(reader,token,op.OperandType);
                    if(value is 0x28 or 0x6f or 0x73)
                    {
                        var call=MetadataIdentity.CallSignature(reader,MetadataTokens.EntityHandle((int)token));
                        if(call.Header.CallingConvention!=SignatureCallingConvention.Default) throw Invalid("Vararg calls are unsupported.");
                        pop=call.ParameterTypes.Length+(value!=0x73&&call.Header.IsInstance?1:0);
                        push=value==0x73||call.ReturnType!="Void"?1:0;
                    }
                }
                if(value==0x2a) { pop=signature.ReturnType=="Void"?0:1; push=0; }
                if(pop<0||push<0) throw Invalid("Unsupported variable stack behavior.");
                int[] targets=op.OperandType switch
                {
                    OperandType.ShortInlineBrTarget=>[offset+unchecked((sbyte)code[operand])],
                    OperandType.InlineBrTarget=>[checked(offset+BinaryPrimitives.ReadInt32LittleEndian(code[operand..]))],
                    OperandType.InlineSwitch=>SwitchTargets(code.Slice(operand,length),offset),_=>[]
                };
                bool terminal=op.FlowControl is FlowControl.Return or FlowControl.Throw;
                rows.Add((start,offset-start,pop,push,targets,op.FlowControl!=FlowControl.Branch&&!terminal,terminal));
                // A prefix and its following instruction form one branch target.
                if(rows.Count>1&&Opcodes[ReadOpcode(code,rows[^2].Offset)].OpCodeType==OpCodeType.Prefix) operandOffsets.Add(start);
                if(op.OpCodeType==OpCodeType.Prefix&&offset==code.Length) throw Invalid("Dangling prefix.");
                if(rows.Count>10000) throw Invalid("Instruction count exceeds policy.");
            }
            var positions=rows.Select((r,i)=>(r.Offset,i)).ToDictionary(x=>x.Offset,x=>x.i);
            foreach(var row in rows) foreach(int target in row.Targets)
                if(!positions.ContainsKey(target)||operandOffsets.Contains(target)) throw Invalid("Branch targets an operand or the interior of a prefix sequence.");
            var depths=new Dictionary<int,int>(); var pending=new Queue<(int Offset,int Depth)>(); pending.Enqueue((0,0)); int maximum=0;
            while(pending.TryDequeue(out var next))
            {
                if(depths.TryGetValue(next.Offset,out int previous)) { if(previous!=next.Depth) throw Invalid("Inconsistent stack merge."); continue; }
                depths.Add(next.Offset,next.Depth); var row=rows[positions[next.Offset]];
                if(next.Depth<row.Pop) throw Invalid("Evaluation stack underflow.");
                int depth=next.Depth-row.Pop+row.Push; maximum=Math.Max(maximum,depth);
                if(maximum>layout.Stack||row.Terminal&&depth!=0) throw Invalid("Invalid terminal stack or maxstack.");
                foreach(int target in row.Targets) pending.Enqueue((target,depth));
                if(row.Fallthrough) { int target=row.Offset+row.Length; if(target>=code.Length) throw Invalid("Control flow falls through the method end."); pending.Enqueue((target,depth)); }
            }
            if(depths.Count!=rows.Count) throw Invalid("Unreachable instructions are unsupported.");
            return new(Convert.ToHexStringLower(SHA256.HashData(body)),layout.Offset,layout.Size,layout.Stack,maximum,layout.Locals,layout.Init,[],Qualification);
        }
        catch(Exception error) when(error is BadImageFormatException or ArgumentException or OverflowException or IndexOutOfRangeException)
        { throw Invalid("Malformed managed body or metadata reference."); }
    }
    private static ushort ReadOpcode(ReadOnlySpan<byte> code,int offset)=>code[offset]==0xfe?(ushort)(0xfe00|code[offset+1]):code[offset];
    public static (int Offset,int Size,int Stack,uint Locals,bool Init) ReadHeader(byte[] body)
    {
        if(body.Length is <2 or >4096) throw Invalid("Managed body must contain 2..4096 bytes.");
        if((body[0]&3)==2)
        { if((body[0]>>2)!=body.Length-1) throw Invalid("Invalid tiny body length."); return(1,body.Length-1,8,0,false); }
        if(body.Length<13||BinaryPrimitives.ReadUInt16LittleEndian(body) is not (0x3003 or 0x3013))
            throw new LabException("capability_unsupported","Managed replacement requires a tiny/fat body without exception sections.");
        int size=BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4)); int stack=BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2));
        uint locals=BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8));
        if(size!=body.Length-12||stack is <1 or >1024||locals!=0&&(locals>>24)!=0x11) throw Invalid("Invalid managed body header.");
        return(12,size,stack,locals,(body[0]&0x10)!=0);
    }
    public static int OperandSize(OperandType type,ReadOnlySpan<byte> remaining)=>type switch
    {
        OperandType.InlineNone=>0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar=>1,
        OperandType.InlineVar=>2,
        OperandType.InlineI8 or OperandType.InlineR=>8,
        OperandType.InlineSwitch=>remaining.Length<4?throw Invalid("Truncated switch."):
            BinaryPrimitives.ReadUInt32LittleEndian(remaining)>10000?throw Invalid("Switch exceeds policy."):
            checked(4+4*(int)BinaryPrimitives.ReadUInt32LittleEndian(remaining)),
        _=>4
    };
    private static int[] SwitchTargets(ReadOnlySpan<byte> operand,int end)
    { var result=new int[(operand.Length-4)/4]; for(int i=0;i<result.Length;i++) result[i]=checked(end+BinaryPrimitives.ReadInt32LittleEndian(operand[(4+4*i)..])); return result; }
}
