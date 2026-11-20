"""Summarize paired --shapes --small-shapes output for the two published seeds."""
import csv
import gzip
import statistics
from collections import defaultdict
from pathlib import Path

directory = Path(__file__).resolve().parent
rounds = defaultdict(dict)
with gzip.open(directory / "samples.csv.gz", "rt") as source:
    for row in csv.DictReader(source):
        key = (row["target"], int(row["providers"]), int(row["elements"]), row["shape"])
        sample = rounds[key].setdefault((row["seed"], row["round"]), {})
        sample[int(row["worker"])] = float(row["ns"])
with (directory / "summary.csv").open("w", newline="") as output:
    writer = csv.writer(output)
    writer.writerow(("target", "providers", "elements", "shape", "rounds", "vanilla_ns",
                     "v035_ns", "candidate_ns", "v035_over_candidate", "vanilla_over_candidate"))
    for key, samples in sorted(rounds.items()):
        values = samples.values()
        times = [statistics.median(x[i] for x in values) for i in (2, 0, 1)]
        ratios = [statistics.median(x[i] / x[1] for x in values) for i in (0, 2)]
        writer.writerow((*key, len(values), *(round(v, 2) for v in times), *(round(v, 4) for v in ratios)))
