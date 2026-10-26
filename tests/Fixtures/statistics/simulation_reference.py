"""Frozen simulation protocol and independent NumPy/SciPy ensemble oracle.

No production calculations are imported. --freeze computes acceptance criteria,
without generating or inspecting experiments; --plan runs that frozen protocol.
"""
import argparse
import hashlib
import json
import math
import platform
from pathlib import Path
import numpy as np
import scipy
from scipy.integrate import quad
from scipy.stats import binom, chi2, nct, norm, t

MASK = (1 << 64) - 1
COUNTERS = ["coverage", "difference_rejected", "equivalent", "improved", "promotion", "usable", "blocked", "descriptive",
            "naive_coverage", "naive_difference_rejected", "naive_promotion", "naive_quantile_coverage"]

class Random:
    def __init__(self, seed):
        self.state = seed

    def values(self, count):
        step = 0x9E3779B97F4A7C15
        with np.errstate(over="ignore"):
            states = np.uint64(self.state) + np.arange(1, count + 1, dtype=np.uint64) * np.uint64(step)
            value = (states ^ (states >> 30)) * np.uint64(0xBF58476D1CE4E5B9)
            value = (value ^ (value >> 27)) * np.uint64(0x94D049BB133111EB)
            value ^= value >> 31
        self.state = (self.state + count * step) & MASK
        return value

    def normal(self, count):
        value = ((self.values(2 * count) >> 12).astype(np.float64) + 0.5) / 4503599627370496.0
        value = value.reshape(count, 2)
        return np.sqrt(-2 * np.log(value[:, 0])) * np.cos(2 * math.pi * value[:, 1])

    def uniform(self):
        return (int(self.values(1)[0] >> np.uint64(12)) + 0.5) / 4503599627370496.0

    def scalar_normal(self):
        return math.sqrt(-2 * math.log(self.uniform())) * math.cos(2 * math.pi * self.uniform())

    def indices(self, count, total):
        before = self.state
        values = self.values(total)
        threshold = ((-count) & MASK) % count
        if np.any(values < threshold):
            self.state = before
            selected = []
            while len(selected) < total:
                value = int(self.values(1)[0])
                if value >= threshold:
                    selected.append(value % count)
            return np.array(selected, dtype=np.int64)
        return (values % np.uint64(count)).astype(np.int64)

def equivalence_power(mean, sigma, size):
    df = size - 1
    critical = t.ppf(.95, df)
    lo, hi = math.log(.99), math.log(1.01)
    # Condition on independent sample variance, then integrate the normal mean.
    maximum = df * (((hi - lo) / (2 * critical)) * math.sqrt(size) / sigma) ** 2
    def integrand(x):
        se = sigma * math.sqrt(x / df / size)
        lower = (lo + critical * se - mean) * math.sqrt(size) / sigma
        upper = (hi - critical * se - mean) * math.sqrt(size) / sigma
        return max(0., float(norm.cdf(upper) - norm.cdf(lower))) * float(chi2.pdf(x, df))
    return min(1., max(0., quad(integrand, 0, maximum, epsabs=1e-11)[0]))

def freeze():
    definitions = [
        ("normal_null", "paired", 0., .02, .02, 32, 32, 1, True),
        ("normal_useful", "paired", math.log(.96), .02, .02, 32, 32, 1, True),
        ("equivalence_lower_boundary", "paired", math.log(.99), .01, .01, 32, 32, 1, True),
        ("equivalence_upper_boundary", "paired", math.log(1.01), .01, .01, 32, 32, 1, True),
        ("equivalence_inside", "paired", 0., .01, .01, 32, 32, 1, True),
        ("equivalence_outside_lower", "paired", math.log(.985), .01, .01, 32, 32, 1, True),
        ("equivalence_outside_upper", "paired", math.log(1.015), .01, .01, 32, 32, 1, True),
        ("unequal_variance_welch", "welch", 0., .01, .08, 32, 48, 1, True),
        ("normal_additive", "additive", 0., 2., 2., 32, 32, 1, True),
        ("student3_heavy_tail", "heavy_tail", 0., .02, .02, 32, 32, 1, False),
        ("symmetric_outliers", "outliers", 0., .02, .02, 32, 32, 1, False),
        ("ar1_unresolved_dependence", "autocorrelation", 0., .02, .02, 32, 32, 1, False),
        ("session_cluster_means", "session_clusters", 0., .02, .01, 32, 32, 1, True),
        ("fixed_family_five", "family", 0., .02, .02, 32, 32, 5, True),
        ("adaptive_winner_fresh_confirmation", "winner", 0., .04, .04, 32, 32, 5, True),
        ("paired_cluster_median", "quantile_clusters", 0., 5., 5., 100, 100, 1, True),
    ]
    experiments, family_error = 10000, .001
    targets = []
    for name, kind, mean, sigma, candidate_sigma, size, candidate_size, family, qualified in definitions:
        criteria = []
        if qualified:
            criteria = [("coverage", .95), ("usable", 1.)]
            if kind == "family":
                criteria[0] = ("coverage", (1 - .05 / family) ** family)
                criteria.append(("difference_rejected", 1 - (1 - .05 / family) ** family))
            elif kind == "winner":
                criteria.extend([("difference_rejected", .05), ("blocked", 1.)])
            elif kind in ("welch", "additive", "session_clusters") or name == "normal_null":
                criteria.append(("difference_rejected", .05))
            if kind == "paired" and name.startswith("equivalence_"):
                criteria.append(("equivalent", equivalence_power(mean, sigma, size)))
            if name == "normal_useful":
                probability = float(nct.cdf(-t.ppf(.975, size - 1), size - 1, (mean - math.log(.99)) * math.sqrt(size) / sigma))
                criteria.append(("improved", probability))
        targets.append((definitions[len(targets)], criteria))
    tests = sum(len(criteria) for _, criteria in targets)
    tail = family_error / (2 * tests)
    designs = []
    for index, (definition, criteria) in enumerate(targets):
        name, kind, mean, sigma, candidate_sigma, size, candidate_size, family, qualified = definition
        designs.append(dict(id=name, kind=kind, seed=str(700001 + index * 104729), size=size, candidate_size=candidate_size,
                            mean=mean, sigma=sigma, candidate_sigma=candidate_sigma, family_size=family, qualified_assumptions=qualified,
                            criteria=[dict(counter=counter, probability=probability, minimum=int(binom.ppf(tail, experiments, probability)),
                                           maximum=int(binom.ppf(1-tail, experiments, probability))) for counter, probability in criteria]))
    return dict(version="statistical-simulation/v1", experiments=experiments, sample_algorithm="splitmix64-open52-boxmuller-discard/v1",
                alpha=.05, resamples=1000, quantile=.5, frames_per_cluster=2, acceptance_method="scipy-binom-equal-tail-bonferroni/v1",
                acceptance_family_error=family_error, designs=designs)

def paired(logs, alpha):
    # Actual positive costs and their binary64 log differences are the input.
    values = np.log(100 * np.exp(logs)) - math.log(100.)
    size = values.shape[-1]
    mean = values.mean(axis=-1)
    se = values.std(axis=-1, ddof=1) / math.sqrt(size)
    df = size - 1
    lower = np.exp(mean - t.ppf(1-alpha/2, df) * se)
    upper = np.exp(mean + t.ppf(1-alpha/2, df) * se)
    eq_lower = np.exp(mean - t.ppf(1-alpha, df) * se)
    eq_upper = np.exp(mean + t.ppf(1-alpha, df) * se)
    p = 2 * t.sf(np.abs(mean/se), df)
    tost = np.maximum(t.sf((mean - math.log(.99))/se, df), t.cdf((mean - math.log(1.01))/se, df))
    return dict(estimate=np.exp(mean), lower=lower, upper=upper, difference_p=p, tost_p=tost,
                equivalent=(tost < alpha) & (eq_lower > .99) & (eq_upper < 1.01), improved=upper < .99)

def first_values(result):
    return {key:float(result[key].flat[0]) for key in ["estimate", "lower", "upper", "difference_p", "tost_p"] if key in result}

def ensemble(plan, design):
    trials, size, frames = plan["experiments"], design["size"], plan["frames_per_cluster"]
    mean, sigma, candidate_sigma, kind = design["mean"], design["sigma"], design["candidate_sigma"], design["kind"]
    family, alpha = design["family_size"], plan["alpha"] / design["family_size"]
    random = Random(int(design["seed"])); counters = {name:0 for name in COUNTERS}
    truth = math.exp(mean)
    def count(result, naive=False, fresh=False):
        prefix = "naive_" if naive else ""
        counters[prefix+"coverage"] = int(np.count_nonzero((result["lower"] <= truth) & (truth <= result["upper"])))
        counters[prefix+"difference_rejected"] = int(np.count_nonzero(result["difference_p"] < (plan["alpha"] if kind == "winner" else alpha)))
        if fresh:
            counters[prefix+"promotion"] = int(np.count_nonzero(result["improved"]))
        if not naive:
            counters["equivalent"] = int(np.count_nonzero(result["equivalent"]))
            counters["improved"] = int(np.count_nonzero(result["improved"]))
            counters["usable"] = trials
    if kind == "quantile_clusters":
        values = 100 + mean + sigma * random.normal(trials * size).reshape(trials, size)
        first = None
        for experiment in range(trials):
            row = values[experiment]
            seed = int(design["seed"]) + experiment
            indices = Random(seed).indices(size, plan["resamples"] * size).reshape(plan["resamples"], size)
            estimates = np.quantile(row[indices], .5, axis=1, method="linear") - 100
            lower, upper = np.quantile(estimates, [plan["alpha"]/2, 1-plan["alpha"]/2], method="linear")
            naive_rows = np.repeat(row, frames)
            indices = Random(seed).indices(len(naive_rows), plan["resamples"] * len(naive_rows)).reshape(plan["resamples"], len(naive_rows))
            naive = np.quantile(naive_rows[indices], .5, axis=1, method="linear") - 100
            naive_lower, naive_upper = np.quantile(naive, [plan["alpha"]/2, 1-plan["alpha"]/2], method="linear")
            counters["coverage"] += int(lower <= mean <= upper)
            counters["naive_quantile_coverage"] += int(naive_lower <= mean <= naive_upper)
            counters["descriptive" if lower == upper else "usable"] += 1
            if first is None:
                first = dict(estimate=float(np.quantile(row, .5, method="linear")-100), lower=float(lower), upper=float(upper))
            if (experiment + 1) % 1000 == 0:
                print(f'{design["id"]}: {experiment+1}/{trials}', flush=True)
        return dict(id=design["id"], counters=counters, first=first)
    if kind == "welch":
        raw = random.normal(trials * (size + design["candidate_size"])).reshape(trials, size + design["candidate_size"])
        b = np.log(100 * np.exp(sigma * raw[:, :size])); c = np.log(100 * np.exp(mean + candidate_sigma * raw[:, size:]))
        vb = b.var(axis=1, ddof=1)/size; vc = c.var(axis=1, ddof=1)/design["candidate_size"]
        se = np.sqrt(vb+vc); df=(vb+vc)**2/(vb**2/(size-1)+vc**2/(design["candidate_size"]-1))
        effect = c.mean(axis=1)-b.mean(axis=1); half=t.ppf(.975, df)*se
        result=dict(estimate=np.exp(effect), lower=np.exp(effect-half), upper=np.exp(effect+half))
    elif kind == "additive":
        raw = 100 + mean + sigma * random.normal(trials * size).reshape(trials, size)
        d = raw - 100; effect = d.mean(axis=1); se = d.std(axis=1, ddof=1)/math.sqrt(size)
        result=dict(estimate=effect, lower=effect-t.ppf(.975,size-1)*se, upper=effect+t.ppf(.975,size-1)*se)
        truth = mean
    elif kind in ("family", "winner"):
        count_arms = family + (kind == "winner")
        raw=mean + sigma*random.normal(trials*count_arms*size).reshape(trials,count_arms,size)
        if kind == "family":
            result=paired(raw, alpha)
            counters["coverage"]=int(np.count_nonzero(np.all((result["lower"]<=truth)&(truth<=result["upper"]),axis=1)))
            counters["difference_rejected"]=int(np.count_nonzero(np.any(result["difference_p"]<alpha,axis=1)))
            counters["usable"]=trials
            return dict(id=design["id"],counters=counters,first=first_values(result))
        winners=np.argmin(raw[:,:family].mean(axis=2),axis=1)
        naive=paired(raw[np.arange(trials),winners],plan["alpha"]); count(naive,True,True)
        result=paired(raw[:,family],plan["alpha"]); count(result,False,True); counters["blocked"]=trials
        return dict(id=design["id"],counters=counters,first=first_values(result))
    else:
        if kind == "heavy_tail":
            raw=random.normal(trials*size*4).reshape(trials,size,4)
            logs=mean+sigma*raw[:,:,0]/np.sqrt(np.sum(raw[:,:,1:]**2,axis=2)/3)
        elif kind == "outliers":
            samples=[]
            for _ in range(trials*size):
                value=random.scalar_normal()
                if random.uniform()<.01:
                    value += -50 if random.uniform()<.5 else 50
                samples.append(mean+sigma*value)
            logs=np.array(samples).reshape(trials,size)
        elif kind == "autocorrelation":
            raw=random.normal(trials*(size+1)).reshape(trials,size+1); previous=raw[:,0]
            logs=np.empty((trials,size))
            for i in range(size):
                previous=.8*previous+.6*raw[:,i+1]; logs[:,i]=mean+sigma*previous
        elif kind == "session_clusters":
            raw=random.normal(trials*size*(frames+1)).reshape(trials,size,frames+1)
            within=np.sum(candidate_sigma*raw[:,:,1:]/frames,axis=2)
            logs=mean+sigma*raw[:,:,0]+within
        else:
            logs=mean+sigma*random.normal(trials*size).reshape(trials,size)
        result=paired(logs,alpha)
        if not design["qualified_assumptions"]:
            count(result,True); counters["descriptive" if kind=="autocorrelation" else "blocked"]=trials
            return dict(id=design["id"],counters=counters,first=first_values(result))
        count(result)
        return dict(id=design["id"],counters=counters,first=first_values(result))
    counters["coverage"]=int(np.count_nonzero((result["lower"]<=truth)&(truth<=result["upper"])))
    center=0 if kind=="additive" else 1
    counters["difference_rejected"]=int(np.count_nonzero((result["lower"]>center)|(result["upper"]<center)))
    counters["usable"]=trials
    return dict(id=design["id"],counters=counters,first=first_values(result))

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument("--freeze"); parser.add_argument("--plan"); parser.add_argument("--output"); parser.add_argument("--design")
    args=parser.parse_args()
    if args.freeze:
        if args.plan or args.output or args.design:
            parser.error("Freeze and execution are distinct operations.")
        Path(args.freeze).write_text(json.dumps(freeze(),indent=2,allow_nan=False)+"\n",encoding="utf-8")
        return
    if not args.plan or not args.output:
        parser.error("Use --plan and --output.")
    path=Path(args.plan); plan=json.loads(path.read_text(encoding="utf-8"))
    # Recompute the prespecified template without observing generated outcomes.
    if plan != freeze():
        raise ValueError("Frozen plan differs from its independently specified protocol/envelopes.")
    designs=[d for d in plan["designs"] if not args.design or d["id"]==args.design]
    if not designs:
        raise ValueError("No selected design.")
    outcomes=[]
    result=dict(plan_hash=hashlib.sha256(path.read_bytes()).hexdigest(),generator=f"Python {platform.python_version()}; NumPy {np.__version__}; SciPy {scipy.__version__}",
                generator_hash=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),designs=outcomes)
    output=Path(args.output)
    if output.exists():
        raise ValueError("Preserve the preceding oracle; choose a new output path.")
    for design in designs:
        outcomes.append(ensemble(plan,design))
        partial=output.with_suffix(output.suffix+".partial")
        partial.write_text(json.dumps(result,indent=2,allow_nan=False)+"\n",encoding="utf-8")
        partial.replace(output)
        print(design["id"]+": independent oracle complete",flush=True)

if __name__=="__main__":
    main()
