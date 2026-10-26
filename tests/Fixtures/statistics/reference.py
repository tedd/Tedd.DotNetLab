"""Independent SciPy oracle; never imports production Tedd.DotNetLab calculations."""
import argparse
import json
import math
import platform
import numpy as np
import scipy
from scipy.stats import t

vectors = {
    "B": [-.042,-.041,-.040,-.039,-.038,-.0415,-.0405,-.0395,-.0385,-.040],
    "E": [-.002,-.0015,-.001,-.0005,0,.0005,.001,.0015,.002,0],
    "I": [-.08,-.06,-.04,-.02,0,.02,.04,.06,.08,0],
    "T": [-.0032,-.00315,-.0031,-.00305,-.003,-.00295,-.0029,-.00285,-.0028,-.003]
}
def oracle(values):
    d=np.array(values,dtype=np.float64)
    mean=float(d.mean())
    se=float(d.std(ddof=1)/math.sqrt(len(d)))
    df=len(d)-1
    ordinary=np.exp(mean+np.array([-1,1])*t.ppf(.975,df)*se).tolist()
    equivalent=np.exp(mean+np.array([-1,1])*t.ppf(.95,df)*se).tolist()
    lower=float(t.sf((mean-math.log(.99))/se,df))
    upper=float(t.cdf((mean-math.log(1.01))/se,df))
    return dict(log_ratios=values,ratio=math.exp(mean),standard_error=se,
        difference_interval=ordinary,equivalence_interval=equivalent,
        difference_p=float(2*t.sf(abs(mean/se),df)),tost_p=max(lower,upper))

def objective_oracle(baseline,candidate,scale,lower,upper,family_size=3):
    b=np.array(baseline,dtype=np.float64)
    c=np.array(candidate,dtype=np.float64)
    d=np.log(c)-np.log(b) if scale=="positive_ratio" else c-b
    mean=float(d.mean())
    se=float(d.std(ddof=1)/math.sqrt(len(d)))
    df=len(d)-1
    alpha=.05/family_size
    ordinary=mean+np.array([-1,1])*t.ppf(1-alpha/2,df)*se
    equivalent=mean+np.array([-1,1])*t.ppf(1-alpha,df)*se
    lo,hi=(math.log(lower),math.log(upper)) if scale=="positive_ratio" else (lower,upper)
    transform=np.exp if scale=="positive_ratio" else lambda x:x
    return dict(baseline=baseline,candidate=candidate,scale=scale,equivalence_lower=lower,equivalence_upper=upper,
        family_size=family_size,alpha_local=alpha,effect=math.exp(mean) if scale=="positive_ratio" else mean,
        standard_error=se,degrees_of_freedom=df,difference_interval=transform(ordinary).tolist(),
        equivalence_interval=transform(equivalent).tolist(),difference_p=float(2*t.sf(abs(mean/se),df)),
        tost_p=float(max(t.sf((mean-lo)/se,df),t.cdf((mean-hi)/se,df))))

def factorial_oracle(scale):
    noise=(np.arange(10,dtype=np.float64)-4.5)/10
    arms=np.array([100+.5*noise,104+.8*noise,97+.3*noise,90+.9*noise])
    transformed=np.log(arms) if scale=="log" else arms
    d=transformed[3]-transformed[1]-transformed[2]+transformed[0]
    mean=float(d.mean())
    se=float(d.std(ddof=1)/math.sqrt(len(d)))
    alpha=.05/6
    return dict(scale=scale,arms=arms.tolist(),contrast=mean,standard_error=se,degrees_of_freedom=9,
        interval=(mean+np.array([-1,1])*t.ppf(1-alpha/2,9)*se).tolist(),
        difference_p=float(2*t.sf(abs(mean/se),9)),alpha_local=alpha)
if __name__=="__main__":
    parser=argparse.ArgumentParser()
    parser.add_argument("--output",required=True)
    args=parser.parse_args()
    result={"environment":{"python":platform.python_version(),"numpy":np.__version__,"scipy":scipy.__version__},
        "fixtures":{name:oracle(values) for name,values in vectors.items()},
        "objectives":{
            "cost":objective_oracle([100.]*10,[100.*math.exp(d) for d in vectors["B"]],"positive_ratio",.99,1.01),
            "throughput":objective_oracle([100.]*10,[100.*math.exp(-d) for d in vectors["B"]],"positive_ratio",.99,1.01),
            "allocation":objective_oracle(list(range(10)),[i+1+(i-4.5)*.01 for i in range(10)],"absolute_difference",-2,2)
        },"factorial":{scale:factorial_oracle(scale) for scale in ["absolute","log"]}}
    with open(args.output,"w",encoding="utf-8") as file:
        json.dump(result,file,indent=2,allow_nan=False)
