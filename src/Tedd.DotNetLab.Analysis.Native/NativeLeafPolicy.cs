using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Analysis.Native;

public static class NativeLeafPolicy
{
    public static VerifiedCil Verify(byte[] body,StoredMethod method)
    {
        var policy=new CilPolicy(method.ModuleMvid,method.Token,method.SignatureHex,method.Static,method.Generic,method.Constructor,0);
        var result=CilVerifier.Verify(body,policy);
        if(body.Length>64||result.CodeOffset!=1||result.Instructions.Any(i=>
            !(i.Opcode is >=0x15 and <=0x20 or 0x2a or 0x58 or 0x59 or 0x5a or >=0x5f and <=0x66)))
            throw new LabException("capability_unsupported","Native leaf qualification excludes calls, branches, locals, arguments, and throwing arithmetic.");
        return result;
    }
}
