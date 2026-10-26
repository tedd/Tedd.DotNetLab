using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Analysis.Native;

public static class NativeSnapshotAcquirer
{
    public static async Task<NativeSnapshotReply> AcquireAsync(Func<CancellationToken,Task<NativeSnapshotReply>> read,
        CancellationToken cancellation=default,Action<int,string>? rejected=null)
    {
        for(int attempt=1;;attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            try { return await read(cancellation); }
            catch(LabException error) when(error.Code=="version_changed"&&attempt<3)
            {
                rejected?.Invoke(attempt,error.Code);
                await Task.Delay(25,cancellation);
            }
        }
    }
}
