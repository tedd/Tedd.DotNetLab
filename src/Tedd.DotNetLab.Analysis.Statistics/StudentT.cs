namespace Tedd.DotNetLab.Analysis.Statistics;

// Student tails via regularized incomplete beta. Validated independently
// against SciPy; survival probability is evaluated directly, avoiding 1-CDF.
public static class StudentT
{
    private static readonly double[] Lanczos = [676.5203681218851,-1259.1392167224028,771.32342877765313,
        -176.61502916214059,12.507343278686905,-.13857109526572012,9.9843695780195716e-6,1.5056327351493116e-7];
    private static double LogGamma(double z)
    {
        if (z<.5) return Math.Log(Math.PI)-Math.Log(Math.Sin(Math.PI*z))-LogGamma(1-z);
        z--;
        double sum=.99999999999980993;
        for(int i=0;i<Lanczos.Length;i++) sum += Lanczos[i]/(z+i+1);
        double t=z+7.5;
        return .5*Math.Log(2*Math.PI)+(z+.5)*Math.Log(t)-t+Math.Log(sum);
    }
    private static double Fraction(double a,double b,double x)
    {
        const double tiny=1e-300;
        double qab=a+b, qap=a+1, qam=a-1;
        double c=1, d=1-qab*x/qap;
        if(Math.Abs(d)<tiny) d=tiny;
        d=1/d;
        double h=d;
        for(int m=1;m<=400;m++)
        {
            double m2=2*m;
            double aa=m*(b-m)*x/((qam+m2)*(a+m2));
            d=1+aa*d; if(Math.Abs(d)<tiny) d=tiny;
            c=1+aa/c; if(Math.Abs(c)<tiny) c=tiny;
            d=1/d; h*=d*c;
            aa=-(a+m)*(qab+m)*x/((a+m2)*(qap+m2));
            d=1+aa*d; if(Math.Abs(d)<tiny) d=tiny;
            c=1+aa/c; if(Math.Abs(c)<tiny) c=tiny;
            d=1/d;
            double delta=d*c; h*=delta;
            if(Math.Abs(delta-1)<2e-14) return h;
        }
        throw new ArithmeticException("Incomplete beta did not converge.");
    }
    private static double Beta(double x,double a,double b)
    {
        if(x<=0) return 0;
        if(x>=1) return 1;
        double factor=Math.Exp(LogGamma(a+b)-LogGamma(a)-LogGamma(b)+a*Math.Log(x)+b*Math.Log(1-x));
        return x<(a+1)/(a+b+2) ? factor*Fraction(a,b,x)/a : 1-factor*Fraction(b,a,1-x)/b;
    }
    public static double Survival(double t,double degreesOfFreedom)
    {
        if(!double.IsFinite(degreesOfFreedom)||degreesOfFreedom<=0||double.IsNaN(t))
            throw new ArgumentOutOfRangeException(nameof(degreesOfFreedom));
        if(t==0) return .5;
        double magnitude=Math.Abs(t);
        double x=magnitude>1e150 ? degreesOfFreedom/magnitude/magnitude : degreesOfFreedom/(degreesOfFreedom+magnitude*magnitude);
        double tail=.5*Beta(x,degreesOfFreedom/2,.5);
        return t>0 ? tail : 1-tail;
    }
    public static double Cdf(double t,double df) => Survival(-t,df);
    public static double Quantile(double probability,double df)
    {
        if(probability<=0||probability>=1||!double.IsFinite(probability)) throw new ArgumentOutOfRangeException(nameof(probability));
        if(probability==.5) return 0;
        if(probability<.5) return -Quantile(1-probability,df);
        double tail=1-probability, lower=0, upper=1;
        while(Survival(upper,df)>tail) upper*=2;
        for(int i=0;i<100;i++)
        {
            double middle=lower+(upper-lower)/2;
            if(Survival(middle,df)>tail) lower=middle; else upper=middle;
        }
        return lower+(upper-lower)/2;
    }
}
