using System.Reflection.Metadata;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Analysis.Native;

// Bound counts before SignatureDecoder allocates arrays from metadata counts.
internal static class SignatureBounds
{
    public static void Check(MetadataReader reader,BlobHandle handle,int kind)
    {
        var blob=reader.GetBlobReader(handle);
        if(blob.RemainingBytes is <1 or >4096) throw new LabException("resource_limit","Metadata signature exceeds 4 KiB.");
        if(kind==0) Type(ref blob,0);
        else
        {
            byte flags=blob.ReadByte();
            if(kind==6) { if(flags!=6) throw new BadImageFormatException(); Type(ref blob,0); }
            else if(kind is 7 or 10)
            { if(flags!=kind) throw new BadImageFormatException(); int count=Count(ref blob); for(int i=0;i<count;i++) Type(ref blob,0); }
            else
            {
                if((flags&0xcf)!=0) throw new LabException("capability_unsupported","Only default managed signatures are supported.");
                if((flags&0x10)!=0) _=Count(ref blob);
                int count=Count(ref blob); Type(ref blob,0); for(int i=0;i<count;i++) Type(ref blob,0);
            }
        }
        if(blob.RemainingBytes!=0) throw new BadImageFormatException();
    }
    private static int Count(ref BlobReader blob)
    { int count=blob.ReadCompressedInteger(); if(count is <0 or >64) throw new LabException("resource_limit","Metadata signature count exceeds 64."); return count; }
    private static void Type(ref BlobReader blob,int depth)
    {
        if(depth>32) throw new LabException("resource_limit","Type nesting exceeds 32.");
        byte type=blob.ReadByte();
        if(type is >=1 and <=0x0e or 0x18 or 0x19 or 0x1c) return;
        if(type is 0x11 or 0x12 or 0x13 or 0x1e) { _=blob.ReadCompressedInteger(); return; }
        if(type is 0x10 or 0x1d) { Type(ref blob,depth+1); return; }
        if(type is 0x1f or 0x20) { _=blob.ReadCompressedInteger(); Type(ref blob,depth+1); return; }
        if(type==0x15)
        {
            if(blob.ReadByte() is not (0x11 or 0x12)) throw new BadImageFormatException();
            _=blob.ReadCompressedInteger(); int count=Count(ref blob); for(int i=0;i<count;i++) Type(ref blob,depth+1); return;
        }
        if(type==0x14)
        {
            Type(ref blob,depth+1); int rank=Count(ref blob),sizes=Count(ref blob);
            if(rank>32||sizes>rank) throw new BadImageFormatException();
            for(int i=0;i<sizes;i++) _=blob.ReadCompressedInteger();
            int bounds=Count(ref blob); if(bounds>rank) throw new BadImageFormatException();
            for(int i=0;i<bounds;i++) _=blob.ReadCompressedSignedInteger(); return;
        }
        throw new LabException("capability_unsupported","Unmanaged pointer, pinned, function-pointer, or unsupported type signature.");
    }
}
