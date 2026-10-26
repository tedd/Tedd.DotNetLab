using System.Buffers.Binary;
using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Analysis.Native;

public static class NativeBodyPolicy
{
    public static CilPolicy Resolve(byte[] body,StoredMethod method,string imagePath)
    {
        byte[] signature=Convert.FromHexString(method.SignatureHex);
        if(signature.Length<3||signature[0]!=0||signature[1]>4||signature[2]!=8||signature.Length!=3+signature[1]||signature.Skip(3).Any(t=>t!=8))
            throw new LabException("capability_unsupported","Native body signatures require static Int32 returns and bounded Int32 arguments.");
        uint localToken=body.Length>=12&&(body[0]&3)==3?BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8)):0;
        int count=0;
        if(localToken!=0)
        {
            if((localToken>>24)!=0x11||new FileInfo(imagePath).Length>67108864)
                throw new LabException("invalid_cil","Expected a bounded existing target-module local signature.");
            using var stream=File.OpenRead(imagePath); using var pe=new PEReader(stream); var reader=pe.GetMetadataReader();
            int row=(int)(localToken&0xffffff);
            if(row<1||row>reader.GetTableRowCount(System.Reflection.Metadata.Ecma335.TableIndex.StandAloneSig))
                throw new LabException("invalid_cil","Local signature token is absent from the selected module.");
            var local=reader.GetStandaloneSignature(MetadataTokens.StandaloneSignatureHandle(row));
            byte[] types=reader.GetBlobBytes(local.Signature);
            if(types.Length<2||types[0]!=7||types[1]>64||types.Length!=types[1]+2||types.Skip(2).Any(t=>t!=8))
                throw new LabException("capability_unsupported","Only bounded existing Int32 local signatures are supported.");
            count=types[1];
        }
        uint self=uint.Parse(method.Token.AsSpan(2),NumberStyles.HexNumber,CultureInfo.InvariantCulture);
        return new(method.ModuleMvid,method.Token,method.SignatureHex,method.Static,method.Generic,method.Constructor,signature[1],count,localToken,
            [new(self,signature[1],true,true)]);
    }
    public static VerifiedCil Verify(byte[] body,CilPolicy policy)
    {
        if(body.Length>4096) throw new LabException("resource_limit","Native CIL body exceeds 4 KiB policy.");
        return CilVerifier.Verify(body,policy);
    }
}
