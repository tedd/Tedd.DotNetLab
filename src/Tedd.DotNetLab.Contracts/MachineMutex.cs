namespace Tedd.DotNetLab.Contracts;

// A named Mutex is thread-affine. Its dedicated owner thread spans async callers
// and releases ownership only after the lease signals completion.
public sealed class MachineMutex : IDisposable
{
    public const string MeasurementName="Tedd.DotNetLab.Measurement.v1";
    private readonly ManualResetEventSlim release=new(false);
    private int disposed;
    private MachineMutex() { }

    public static Task<MachineMutex> AcquireAsync(string name,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        var completion=new TaskCompletionSource<MachineMutex>(TaskCreationOptions.RunContinuationsAsynchronously);
        new Thread(()=>
        {
            var lease=new MachineMutex(); bool owned=false;
            try
            {
                using var mutex=new Mutex(false,OperatingSystem.IsWindows()?"Global\\"+name:name);
                try { owned=WaitHandle.WaitAny([cancellation.WaitHandle,mutex])==1; }
                catch(AbandonedMutexException) { owned=true; }
                try
                {
                    if(!owned||cancellation.IsCancellationRequested) { completion.TrySetCanceled(cancellation); return; }
                    completion.SetResult(lease);
                    lease.release.Wait();
                }
                finally { if(owned) mutex.ReleaseMutex(); }
            }
            catch(Exception error) { completion.TrySetException(error); }
            finally { lease.release.Dispose(); }
        }) { IsBackground=true,Name="DotNetLab mutex owner" }.Start();
        return completion.Task;
    }

    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)==0) release.Set();
    }
}
