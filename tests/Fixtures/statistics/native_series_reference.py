"""Independent NumPy/SciPy oracle for equal-weight native session log costs."""
import json
import hashlib
from pathlib import Path
import numpy as np
import scipy
from scipy import stats

session_logs = np.array([-.070, -.043, -.061, -.033, -.051, -.048, -.065, -.036, -.058, -.045])
sessions = []
for index, shift in enumerate(session_logs):
    baseline = np.array([80., 100., 130.]) * (1 + .07 * index)
    candidate = baseline * np.exp(shift + np.array([.014, -.021, .007]))
    sessions.append({"id": f"session-{index}", "baseline": baseline.tolist(), "candidate": candidate.tolist(),
                     "geometric_baseline": float(stats.gmean(baseline)), "geometric_candidate": float(stats.gmean(candidate))})
logs = np.array([np.mean(np.log(s["candidate"])) - np.mean(np.log(s["baseline"])) for s in sessions])
mean = float(np.mean(logs)); se = float(stats.sem(logs)); df = len(logs) - 1; alpha = .05
difference = np.exp(mean + np.array([-1, 1]) * stats.t.ppf(1-alpha/2, df) * se)
equivalence = np.exp(mean + np.array([-1, 1]) * stats.t.ppf(1-alpha, df) * se)
result = {"oracle": {"numpy": np.__version__, "scipy": scipy.__version__, "script": "native_series_reference.py",
                     "script_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest()},
          "estimand": "equal_weight_session_mean_matched_log_batch_cost_ratios", "sessions": sessions,
          "expected": {"ratio": float(np.exp(mean)), "se": se, "df": df, "independent_units": len(logs),
                       "difference_interval": difference.tolist(), "equivalence_interval": equivalence.tolist(),
                       "difference_p": float(2*stats.t.sf(abs(mean/se), df)),
                       "tost_p": float(max(stats.t.sf((mean-np.log(.99))/se, df), stats.t.cdf((mean-np.log(1.01))/se, df)))}}
Path(__file__).with_name("native-series-golden.json").write_text(json.dumps(result, indent=2)+"\n", encoding="utf-8")
