using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;

public sealed class ManagedBodyFixture
{
    public static readonly ManagedBodyFixture Shared=new();
    public float Scale=2;
    public Vector2 Bias=new(0,0);
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static float SpanSum(ReadOnlySpan<float> values)
    {
        float sum=0;
        for(int i=0;i<values.Length;i++)
#if LIVE_REPLACEMENT_CANDIDATE
            sum=values[i]+sum;
#else
            sum+=values[i];
#endif
        return sum;
    }
    public static Vector2 MetadataAnchor(Vector2 value)=>2f*value;
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int IntegerCall(int value)
    {
#if LIVE_REPLACEMENT_CANDIDATE
        return Math.Abs(value)*1;
#else
        return Math.Abs(value);
#endif
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static float Incorrect()
    {
#if LIVE_REPLACEMENT_CANDIDATE
        return 13;
#else
        return 12;
#endif
    }
    public static int MissingReference(byte[] values)
    {
#if LIVE_REPLACEMENT_CANDIDATE
        return System.Security.Cryptography.SHA256.HashData(values).Length;
#else
        return values.Length;
#endif
    }
    public static int ExceptionRegion(int value)
    { try { return 42/value; } catch(DivideByZeroException) { return 0; } }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public float Sum(float[] values)
    {
        Span<float> span=values.AsSpan(); float sum=0;
        for(int i=0;i<span.Length;i++)
#if LIVE_REPLACEMENT_CANDIDATE
            sum+=Scale*span[i]+Bias.X;
#else
            sum+=span[i]*Scale+Bias.X;
#endif
        return sum;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Vector256<float> Add(Vector256<float> value)
    {
#if LIVE_REPLACEMENT_CANDIDATE
        return Vector256.Add(Vector256.Create(1f),value);
#else
        return Vector256.Add(value,Vector256.Create(1f));
#endif
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Increment(ref int value)
    {
#if LIVE_REPLACEMENT_CANDIDATE
        value=1+value;
#else
        value+=1;
#endif
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static Vector2 Transform(Vector2 value)
    {
#if LIVE_REPLACEMENT_CANDIDATE
        return 2f*value;
#else
        return value*2f;
#endif
    }
}

public sealed class ManagedReplacementScenario(string method,int? owningThread=null) : ISynchronousScenario
{
    private float result;
    private int count;
    private readonly float[] values=[1,2,3];
    private readonly string scope=MethodBinding.Scope(typeof(ManagedBodyFixture).GetMethod(method)!);
    public ScenarioDescriptor Describe()=>new("managed-"+method,"1",Wire.Hash("managed-body-"+method),ResetLevel.LogicalState,
        owningThread is null?ThreadAffinity.Any:ThreadAffinity.ApplicationThread,false,"One managed body invocation",["baseline","candidate"],[scope]);
    public void Prepare(ScenarioContext context)=>CheckThread();
    public void Reset(ResetContext context) { CheckThread(); result=0; count=0; }
    public long ExecuteBatch(int operations)
    {
        CheckThread();
        for(int i=0;i<operations;i++)
        {
            switch(method)
            {
                case nameof(ManagedBodyFixture.Sum): result+=ManagedBodyFixture.Shared.Sum(values); break;
                case nameof(ManagedBodyFixture.Add): result+=ManagedBodyFixture.Add(Vector256.Create(2f)).GetElement(0); break;
                case nameof(ManagedBodyFixture.Increment): int value=2; ManagedBodyFixture.Increment(ref value); result+=value; break;
                case nameof(ManagedBodyFixture.Transform): result+=ManagedBodyFixture.Transform(new Vector2(2,3)).X; break;
                case nameof(ManagedBodyFixture.Incorrect): result+=ManagedBodyFixture.Incorrect(); break;
                case nameof(ManagedBodyFixture.IntegerCall): result+=ManagedBodyFixture.IntegerCall(-3); break;
                case nameof(ManagedBodyFixture.SpanSum): result+=ManagedBodyFixture.SpanSum(values); break;
                default: throw new InvalidOperationException("Unknown managed fixture method.");
            }
            count++;
        }
        return operations;
    }
    public ValidationResult Validate()
    { CheckThread(); return new(result==count*(method is nameof(ManagedBodyFixture.Sum) or nameof(ManagedBodyFixture.Incorrect)?12:
        method==nameof(ManagedBodyFixture.Transform)?4:method==nameof(ManagedBodyFixture.SpanSum)?6:3)); }
    public void Cleanup()=>CheckThread();
    private void CheckThread()
    { if(owningThread is int thread&&Environment.CurrentManagedThreadId!=thread) throw new InvalidOperationException("Mutation oracle ran outside the application thread."); }
}
