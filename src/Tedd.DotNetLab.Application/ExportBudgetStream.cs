using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Application;

internal sealed class ExportBudgetStream(Stream inner,long maximum) : Stream
{
    private void Check(long count)
    {
        if(count<0||inner.Position>maximum-count) throw new LabException("resource_limit","Export write would exceed its byte budget.");
    }
    public override bool CanRead=>inner.CanRead;
    public override bool CanSeek=>inner.CanSeek;
    public override bool CanWrite=>inner.CanWrite;
    public override long Length=>inner.Length;
    public override long Position { get=>inner.Position; set { if(value<0||value>maximum) throw new LabException("resource_limit","Export position exceeds its budget."); inner.Position=value; } }
    public override void Flush()=>inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken)=>inner.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer,int offset,int count)=>inner.Read(buffer,offset,count);
    public override long Seek(long offset,SeekOrigin origin)
    {
        long target=origin switch { SeekOrigin.Begin=>offset,SeekOrigin.Current=>checked(inner.Position+offset),_=>checked(inner.Length+offset) };
        Position=target; return target;
    }
    public override void SetLength(long value) { if(value<0||value>maximum) throw new LabException("resource_limit","Export length exceeds its budget."); inner.SetLength(value); }
    public override void Write(byte[] buffer,int offset,int count) { Check(count); inner.Write(buffer,offset,count); }
    public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); inner.Write(buffer); }
    public override Task WriteAsync(byte[] buffer,int offset,int count,CancellationToken cancellationToken) { Check(count); return inner.WriteAsync(buffer,offset,count,cancellationToken); }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellationToken=default) { Check(buffer.Length); return inner.WriteAsync(buffer,cancellationToken); }
}
