using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Application;

public sealed partial class LabService
{
    private async Task<Measurement> ExecuteMeasuredAsync(Tedd.DotNetLab.Contracts.RunRequest request,CancellationToken cancellation,
        ParserMeasurementGate.Lease? existingPermit=null)
    {
        using var owned=existingPermit is null?await activityScheduler.EnterMeasurementAsync(cancellation):null;
        var permit=existingPermit??owned!;
        try
        {
            var measured=await harness.ExecuteAsync(request,cancellation);
            if(!measured.Quiescent) permit.RetainForUncertainTarget(request.Target);
            return measured;
        }
        catch
        {
            // Missing acknowledgement cannot establish that target-side timed work has stopped.
            permit.RetainForUncertainTarget(request.Target); throw;
        }
    }
}
