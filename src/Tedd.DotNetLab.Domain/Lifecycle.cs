using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Domain;

public static class Lifecycle
{
    public static bool Terminal(OperationState state) => state is OperationState.Completed or OperationState.Failed
        or OperationState.Interrupted or OperationState.Tainted;
    public static void Transition(OperationState from, OperationState to)
    {
        var allowed = from switch
        {
            OperationState.Accepted => to is OperationState.Running or OperationState.CancellationRequested or OperationState.Failed or OperationState.Interrupted,
            OperationState.Running => to is OperationState.Completed or OperationState.Failed or OperationState.CancellationRequested or OperationState.Interrupted or OperationState.Tainted,
            OperationState.CancellationRequested => to is OperationState.Completed or OperationState.Failed or OperationState.Interrupted or OperationState.Tainted,
            _ => false
        };
        if (!allowed) throw new LabException("invalid_transition", $"Illegal operation transition {from} -> {to}.");
    }
    public static void Transition(CaptureState from, CaptureState to)
    {
        bool allowed = from switch
        {
            CaptureState.Created => to is CaptureState.Opening or CaptureState.Failed,
            CaptureState.Opening => to is CaptureState.Collecting or CaptureState.Failed,
            CaptureState.Collecting => to is CaptureState.Stopping or CaptureState.Incomplete or CaptureState.Failed,
            CaptureState.Stopping => to is CaptureState.Draining or CaptureState.Incomplete or CaptureState.Failed,
            CaptureState.Draining => to is CaptureState.Sealed or CaptureState.Incomplete or CaptureState.Failed,
            _ => false
        };
        if (!allowed) throw new LabException("invalid_transition", $"Illegal capture transition {from} -> {to}.");
    }
}

// Algorithm identity is independent of System.Random and runtime version.
public sealed class DeterministicRandom(ulong seed)
{
    private ulong state = seed;
    public ulong Next()
    {
        state += 0x9e3779b97f4a7c15UL;
        ulong value = state;
        value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
        value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
        return value ^ (value >> 31);
    }
    public int Index(int count)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        ulong bound = (ulong)count;
        ulong threshold = unchecked(0UL - bound) % bound;
        ulong value;
        do value = Next(); while (value < threshold);
        return (int)(value % bound);
    }
    public T[] Shuffle<T>(IEnumerable<T> input)
    {
        var values = input.ToArray();
        for (int i = values.Length - 1; i > 0; i--)
        {
            int j = Index(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
        return values;
    }
}
