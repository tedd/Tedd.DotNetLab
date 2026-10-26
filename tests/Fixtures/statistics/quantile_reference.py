"""Independent NumPy oracle for complete paired session-cluster quantiles."""
import json
import sys
from pathlib import Path
import numpy as np

MASK = (1 << 64) - 1

class SplitMix64:
    def __init__(self, seed):
        self.state = seed

    def index(self, count):
        threshold = ((-count) & MASK) % count
        while True:
            self.state = (self.state + 0x9E3779B97F4A7C15) & MASK
            value = self.state
            value = ((value ^ (value >> 30)) * 0xBF58476D1CE4E5B9) & MASK
            value = ((value ^ (value >> 27)) * 0x94D049BB133111EB) & MASK
            value ^= value >> 31
            if value >= threshold:
                return value % count

clusters, blocks = [], []
paired = []
for i in range(12):
    ids, baseline, candidate = [], [], []
    for b in range(1 + i % 3):
        block_id = f"block-{i}-{b}"
        a = [100 + i * 3 + b * 2 + j * j * 0.7 for j in range(4)]
        c = [85 + i * 2.1 + b * 1.6 + j * j * 0.9 + i % 3 for j in range(4)]
        blocks.append({"id": block_id, "baseline": a, "candidate": c})
        ids.append(block_id)
        baseline.extend(a)
        candidate.extend(c)
    clusters.append({"id": f"session-{i}", "blocks": ids})
    paired.append((baseline, candidate))

p, seed, resamples, alpha = 0.99, 71337, 10000, 0.05
def statistic(indices):
    a = np.concatenate([paired[i][0] for i in indices])
    c = np.concatenate([paired[i][1] for i in indices])
    return float(np.quantile(c, p, method="linear") - np.quantile(a, p, method="linear"))

random = SplitMix64(seed)
estimates = [statistic([random.index(len(paired)) for _ in paired]) for _ in range(resamples)]
expected = {
    "difference": statistic(range(len(paired))),
    "lower": float(np.quantile(estimates, alpha / 2, method="linear")),
    "upper": float(np.quantile(estimates, 1 - alpha / 2, method="linear")),
    "confidence": 1 - alpha,
}
output = {"generator": f"Python/NumPy {np.__version__}", "quantile": p, "seed": str(seed),
          "resamples": resamples, "alpha": alpha, "clusters": clusters, "blocks": blocks, "expected": expected}
Path(sys.argv[1]).write_text(json.dumps(output, indent=2, allow_nan=False) + "\n", encoding="utf-8")
