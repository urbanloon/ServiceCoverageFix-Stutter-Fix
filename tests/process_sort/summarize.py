"""Summarize pre-randomized paired trials. Numbers above 1 favor the candidate."""
import csv
import random
import statistics
import sys
from collections import defaultdict

print("providers,shape,method,baseline_median_ms,candidate_median_ms,median_paired_speedup,bootstrap_low,bootstrap_high")
for file in sys.argv[1:]:
    groups = defaultdict(dict)
    with open(file, newline="") as stream:
        for row in csv.DictReader(stream):
            key = row["providers"], row["shape"], row["method"]
            groups[key][int(row["round"])] = float(row["ms"])
    for (providers, shape, method), trials in groups.items():
        if method == "Vanilla":
            continue
        baseline = groups[(providers, shape, "Vanilla")]
        if baseline.keys() != trials.keys():
            raise ValueError("unpaired timings")
        speedups = [baseline[r] / trials[r] for r in sorted(trials)]
        rng = random.Random(7722)
        draws = sorted(statistics.median(rng.choices(speedups, k=len(speedups))) for _ in range(5000))
        print(f"{providers},{shape},{method},{statistics.median(baseline.values()):.3f},"
              f"{statistics.median(trials.values()):.3f},{statistics.median(speedups):.3f},"
              f"{draws[125]:.3f},{draws[4874]:.3f}")
