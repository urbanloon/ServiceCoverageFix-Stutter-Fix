"""Compare two Release builds of DiagnosticCost.dll, alternating process order."""
import argparse
import csv
import os
from pathlib import Path
import statistics
import subprocess

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("baseline", type=Path)
parser.add_argument("candidate", type=Path)
parser.add_argument("output", type=Path)
parser.add_argument("--dotnet", default="dotnet")
parser.add_argument("--pairs", type=int, default=15)
args = parser.parse_args()
environment = {**os.environ, "DOTNET_TieredCompilation": "0"}
rows = []
for pair in range(args.pairs):
    builds = [("0.3.5", args.baseline), ("candidate", args.candidate)]
    for build, path in builds[::1 if pair % 2 == 0 else -1]:
        output = subprocess.check_output([args.dotnet, str(path.resolve())], text=True, env=environment)
        for case, slots, iterations, ns, allocated in csv.reader(output.splitlines()):
            rows.append((pair + 1, build, case, int(slots), int(iterations), float(ns), float(allocated)))
with args.output.open("w", newline="") as destination:
    writer = csv.writer(destination, lineterminator="\n")
    writer.writerow(("pair", "build", "case", "slots", "iterations", "ns_per_call", "bytes_per_call"))
    writer.writerows(rows)
print("case,slots,baseline_ns,candidate_ns,paired_speedup,baseline_bytes,candidate_bytes")
for case, slots in dict.fromkeys((r[2], r[3]) for r in rows):
    old, new = ([r for r in rows if r[1:4] == (build, case, slots)] for build in ("0.3.5", "candidate"))
    median = statistics.median
    print(f"{case},{slots},{median(r[5] for r in old):.2f},{median(r[5] for r in new):.2f},"
          f"{median(a[5]/b[5] for a,b in zip(old,new)):.3f},{median(r[6] for r in old):.0f},{median(r[6] for r in new):.0f}")
