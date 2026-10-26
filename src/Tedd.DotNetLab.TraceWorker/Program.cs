using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Tedd.DotNetLab.Analysis.Runtime;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Transport.Local;

try
{
    using var parent=ParserParentGuard.Start();
    if(args.Length!=0) throw new LabException("invalid_request","Trace worker accepts only the bounded controller protocol.");
    var request=await Frames.ReadAsync<TraceWorkerRequest>(Console.OpenStandardInput(),CancellationToken.None);
    request.Policy.Validate();
    if(request.Kind is not ("runtime" or "stacks")||request.Kind=="runtime"&&request.StackPolicy is not null||request.Kind=="stacks"&&request.StackPolicy is null)
        throw new LabException("invalid_request","Parser request kind and policy do not agree.");
    if(request.SchemaVersion!=Wire.Version||request.Capture.ArtifactId!=request.SourceArtifact.Id)
        throw new LabException("invalid_request","Parser request schema or source identity differs.");
    string root=Path.GetFullPath(request.Root);
    string Verify(string path,string requiredPrefix)
    {
        string full=Path.GetFullPath(path);
        if(!full.StartsWith(Path.Combine(root,requiredPrefix)+Path.DirectorySeparatorChar,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))
            throw new LabException("invalid_path","Parser path escaped its artifact/input namespace.");
        for(string? current=full;current is not null;current=Path.GetDirectoryName(current))
            if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                throw new LabException("invalid_path","Parser paths cannot traverse reparse points.");
        return full;
    }
    string source=Verify(request.SourcePath,Path.Combine("artifacts","sha256")); string output=Verify(request.OutputPath,"incoming");
    if(Path.GetFileName(source)!=request.SourceArtifact.Hash||!output.EndsWith(".partial",StringComparison.Ordinal))
        throw new LabException("invalid_path","Parser paths are not controller-owned artifact paths.");
    using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(request.Policy.MaximumDurationSeconds));
    // Hold a read-only sharing handle across hash verification and decoding on the qualified Windows path.
    await using var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read);
    if(input.Length!=request.SourceArtifact.Length||input.Length>request.Policy.MaximumInputBytes||
        Convert.ToHexStringLower(await SHA256.HashDataAsync(input,deadline.Token))!=request.SourceArtifact.Hash)
        throw new LabException("artifact_corrupt","Parser input integrity or byte budget differs.");
    object summary;
    if(request.Kind=="stacks")
    {
        request.StackPolicy!.Validate();
        if(request.StackPolicy.MaximumEvents>request.Policy.MaximumEvents||request.StackPolicy.MaximumRawBytes>request.Policy.MaximumInputBytes||!request.Capture.Profile.DataClasses.Contains("samples"))
            throw new LabException("invalid_plan","Stack parser request exceeds its resource or provider qualification.");
        string scratch=Path.Combine(Path.GetDirectoryName(output)!,"stack-conversion-"+Path.GetFileNameWithoutExtension(output));
        Verify(scratch,"incoming");
        summary=StackParser.Analyze(source,scratch,request.Capture,request.SourceArtifact,request.StackPolicy,deadline.Token);
    }
    else summary=new TraceAnalyzer().Analyze(source,request.Capture,request.Policy.MaximumEvents);
    deadline.Token.ThrowIfCancellationRequested();
    await using(var file=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.None))
    {
        await using var bounded=new BoundedOutput(file,request.Policy.MaximumOutputBytes);
        await JsonSerializer.SerializeAsync(bounded,summary,summary.GetType(),Wire.Json,deadline.Token); await bounded.FlushAsync(deadline.Token); file.Flush(true);
    }
    await using var retained=new FileStream(output,FileMode.Open,FileAccess.Read,FileShare.Read);
    string hash=Convert.ToHexStringLower(await SHA256.HashDataAsync(retained,deadline.Token));
    using var process=Process.GetCurrentProcess();
    await Frames.WriteAsync(Console.OpenStandardOutput(),new TraceWorkerReply(Wire.Version,"ok",null,hash,retained.Length,
        process.Id,ProcessIdentity.StartTimeUtcTicks(process),process.TotalProcessorTime.Ticks,process.PeakWorkingSet64),deadline.Token);
}
catch(Exception error)
{
    string code=error is LabException lab?lab.Code:error is OutOfMemoryException?"parser_memory_limit":error is OperationCanceledException?"parser_timeout":"parser_failed";
    try { await Frames.WriteAsync(Console.OpenStandardOutput(),new TraceWorkerReply(Wire.Version,"error",code,null,0,Environment.ProcessId,0,0,0),CancellationToken.None); }
    catch(Exception) { }
    Environment.ExitCode=1;
}

internal sealed class BoundedOutput(Stream target,long maximum) : Stream
{
    private long length;
    public override bool CanRead=>false; public override bool CanSeek=>false; public override bool CanWrite=>true;
    public override long Length=>length; public override long Position { get=>length; set=>throw new NotSupportedException(); }
    private void Reserve(int count) { if(count>maximum-length) throw new LabException("resource_limit","Parser output exceeds its byte budget."); length+=count; }
    public override void Write(byte[] buffer,int offset,int count) { Reserve(count); target.Write(buffer,offset,count); }
    public override void Write(ReadOnlySpan<byte> buffer) { Reserve(buffer.Length); target.Write(buffer); }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken cancellation=default) { Reserve(buffer.Length); return target.WriteAsync(buffer,cancellation); }
    public override void Flush()=>target.Flush(); public override Task FlushAsync(CancellationToken cancellation)=>target.FlushAsync(cancellation);
    public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException(); public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
    public override void SetLength(long value)=>throw new NotSupportedException();
}
