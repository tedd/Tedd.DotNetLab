using System.Buffers.Binary;
using System.Reflection.Emit;
using Tedd.DotNetLab.Analysis.Native;
using Tedd.DotNetLab.Contracts;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class CilTests
{
    private static CilPolicy Policy(int args=0)=>new("00000000-0000-0000-0000-000000000001","0x06000001",
        args==0?"000008":"00010808",true,false,false,args);
    private static byte[] Tiny(params byte[] code)=>[(byte)((code.Length<<2)|2),..code];
    [Fact, Case("UT-IL-001", "FR-052", "M6", "Unit")]
    public void CompleteBody_ValidatesHeaderIdentityAndStack()
    {
        var body=Tiny(0x1f,42,0x2a); var result=CilVerifier.Verify(body,Policy());
        Assert.Equal(3,result.CodeSize); Assert.Equal(1,result.RequiredStack);
        Assert.Equal(64,result.Hash.Length);
        Assert.Throws<LabException>(()=>CilVerifier.Verify(body,Policy() with { Generic=true }));
        Assert.Throws<LabException>(()=>CilVerifier.Verify(body,Policy() with { Constructor=true }));
        Assert.Throws<LabException>(()=>CilVerifier.Verify(body,Policy() with { SignatureHex="00001008" }));
    }
    [Fact, Case("UT-IL-BOUNDARIES", "FR-052", "M6", "Unit")]
    public void InvalidOperandsBranchesStacksAndPrefixes_AreRejected()
    {
        byte[][] invalid=[Tiny(0x20,1),Tiny(0x2a),Tiny(0x17,0x18,0x2a),Tiny(0x2b,0xff,0x17,0x2a),
            Tiny(0x2b,0x02,0x1f,0,0x2a),Tiny(0xfe,0x14,0x17,0x2a),Tiny(0x0e,0,0x2a),Tiny(0x06,0x2a),
            Tiny(0x28,1,0,0,0x0a,0x2a),Tiny(0x17),Tiny(0x2b,0xfe),Tiny(0x17,0x2a,0x18,0x2a)];
        foreach(var body in invalid) Assert.Throws<LabException>(()=>CilVerifier.Verify(body,Policy()));
        var badLength=Tiny(0x17,0x2a); badLength[0]+=4;
        Assert.Throws<LabException>(()=>CilVerifier.Verify(badLength,Policy()));
        byte[] fat=new byte[15]; BinaryPrimitives.WriteUInt16LittleEndian(fat,0x300b);
        BinaryPrimitives.WriteUInt16LittleEndian(fat.AsSpan(2),8); BinaryPrimitives.WriteUInt32LittleEndian(fat.AsSpan(4),3);
        fat[12]=0x17; fat[13]=0x25; fat[14]=0x2a;
        Assert.Throws<LabException>(()=>CilVerifier.Verify(fat,Policy()));
        BinaryPrimitives.WriteUInt16LittleEndian(fat,0x3003); BinaryPrimitives.WriteUInt16LittleEndian(fat.AsSpan(2),1);
        Assert.Throws<LabException>(()=>CilVerifier.Verify(fat,Policy()));
    }
    [Fact, Case("UT-IL-006", "FR-052", "M6", "Unit")]
    public void MergeWithDifferentEvaluationDepth_IsRejected()
    {
        // True path brings two values to the common return; false path brings one.
        var body=Tiny(0x02,0x2c,0x04,0x17,0x18,0x2b,0x01,0x19,0x2a);
        Assert.Throws<LabException>(()=>CilVerifier.Verify(body,Policy(1)));
    }
    private static int entered,completed,exceptional;
    private static void Enter(int id)=>entered++;
    private static void Exit(int id)=>completed++;
    private static void ExitExceptional(int id)=>exceptional++;
    [Fact, Case("UT-IL-013", "FR-051", "M5", "Integration")]
    public void ProbeRewrite_RuntimeExecutionPreservesBothReturnPaths()
    {
        var dynamic=new DynamicMethod("probe-fixture",typeof(int),[typeof(int)],typeof(CilTests).Module,true);
        var runtime=dynamic.GetDynamicILInfo();
        uint enter=(uint)runtime.GetTokenFor(typeof(CilTests).GetMethod(nameof(Enter),System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!.MethodHandle);
        uint exit=(uint)runtime.GetTokenFor(typeof(CilTests).GetMethod(nameof(Exit),System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!.MethodHandle);
        var policy=Policy(1) with { Calls=[new(enter,1,false,true),new(exit,1,false,true)] };
        var original=Tiny(0x02,0x2c,0x02,0x17,0x2a,0x18,0x2a);
        var rewritten=CilProbeRewriter.Rewrite(original,policy,7,enter,exit);
        runtime.SetLocalSignature([0x07,0]);
        runtime.SetCode(rewritten.Body[rewritten.Verification.CodeOffset..],rewritten.Verification.MaxStack);
        var execute=dynamic.CreateDelegate<Func<int,int>>();
        entered=completed=0;
        Assert.Equal(2,execute(0)); Assert.Equal(1,execute(1));
        Assert.Equal(2,entered); Assert.Equal(2,completed);
        Assert.Equal(6,rewritten.OffsetMap.Count);
        Assert.Equal("unsupported",rewritten.ExceptionCoverage);
    }
    [Fact, Case("UT-IL-EXCEPTIONS", "FR-051", "M5", "Unit")]
    public void NormalCompletionProbe_RejectsThrowingArithmeticAndUnknownHelpers()
    {
        var policy=Policy(1) with { Calls=[new(0x0a000001,1,false,true),new(0x0a000002,1,false,true)] };
        Assert.Throws<LabException>(()=>CilProbeRewriter.Rewrite(Tiny(0x02,0x16,0x5b,0x2a),policy,0,0x0a000001,0x0a000002));
        Assert.Throws<LabException>(()=>CilProbeRewriter.Rewrite(Tiny(0x02,0x2a),policy,0,0x0a000003,0x0a000002));
    }
    [Fact, Case("UT-IL-FAULT-RUNTIME", "FR-051,FR-054", "M5", "Integration")]
    public void FaultProbe_RuntimePreservesReturnsAndPropagatesException()
    {
        var dynamic=new DynamicMethod("fault-probe-fixture",typeof(int),[typeof(int)],typeof(CilTests).Module,true);
        var runtime=dynamic.GetDynamicILInfo();
        uint Reference(string name)=>(uint)runtime.GetTokenFor(typeof(CilTests).GetMethod(name,
            System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!.MethodHandle);
        uint enter=Reference(nameof(Enter)),exit=Reference(nameof(Exit)),fault=Reference(nameof(ExitExceptional));
        var policy=Policy(1) with { Calls=[new(enter,1,false,true),new(exit,1,false,true),new(fault,1,false,true)] };
        var rewrite=CilFaultProbeRewriter.Rewrite(Tiny(0x1f,42,0x02,0x5b,0x2a),policy,0,enter,exit,new(fault,0x11000001,1,0));
        runtime.SetLocalSignature([0x07,1,0x08]);
        runtime.SetCode(rewrite.Body.AsSpan(12,rewrite.Verification.CodeSize).ToArray(),rewrite.Verification.MaxStack);
        int section=(12+rewrite.Verification.CodeSize+3)&~3; runtime.SetExceptions(rewrite.Body[section..]);
        var execute=dynamic.CreateDelegate<Func<int,int>>(); entered=completed=exceptional=0;
        Assert.Equal(21,execute(2)); Assert.Throws<DivideByZeroException>(()=>execute(0)); Assert.Equal(-21,execute(-2));
        Assert.Equal(3,entered); Assert.Equal(2,completed); Assert.Equal(1,exceptional);
        Assert.Equal("normal-exit-and-managed-fault-unwind",rewrite.ExceptionCoverage);
    }
    [Fact, Case("UT-IL-011", "FR-051,FR-054", "M5", "Unit")]
    public void FaultProbe_RejectsMalformedRangesCrossRegionBranchesAndForgedLocals()
    {
        const uint enter=0x0a000001,exit=0x0a000002,fault=0x0a000003,locals=0x11000001;
        var policy=Policy(1) with { Calls=[new(enter,1,false,true),new(exit,1,false,true),new(fault,1,false,true)] };
        var source=Tiny(0x02,0x2c,0x02,0x17,0x2a,0x18,0x2a);
        var rewritten=CilFaultProbeRewriter.Rewrite(source,policy,0,enter,exit,new(fault,locals,1,0));
        var expanded=policy with { LocalCount=1,LocalSignatureToken=locals };
        int section=(12+rewritten.Verification.CodeSize+3)&~3;
        var region=new CilFaultRegion(10,BinaryPrimitives.ReadInt32LittleEndian(rewritten.Body.AsSpan(section+12)),
            BinaryPrimitives.ReadInt32LittleEndian(rewritten.Body.AsSpan(section+16)),11);
        Assert.Throws<LabException>(()=>CilVerifier.Verify(rewritten.Body,expanded));
        foreach(int index in new[]{section,section+1,section+4,section+8,section+12,section+16,section+20,section+24})
        {
            byte[] malformed=(byte[])rewritten.Body.Clone(); malformed[index]^=1;
            Assert.Throws<LabException>(()=>CilVerifier.Verify(malformed,expanded,region));
        }
        byte[] cross=(byte[])rewritten.Body.Clone();
        var branch=rewritten.Verification.Instructions.First(i=>i.BranchTarget is not null&&!i.Leave);
        BinaryPrimitives.WriteInt32LittleEndian(cross.AsSpan(12+branch.Offset+1),region.HandlerOffset-(branch.Offset+branch.Length));
        Assert.Throws<LabException>(()=>CilVerifier.Verify(cross,expanded,region));
        Assert.Throws<LabException>(()=>CilFaultProbeRewriter.Rewrite(source,policy,0,enter,exit,new(fault,locals,1,1)));
        Assert.Throws<LabException>(()=>CilFaultProbeRewriter.Rewrite(source,policy,0,enter,exit,new(0x0a000004,locals,1,0)));
    }
}
