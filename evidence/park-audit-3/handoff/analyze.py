"""Summarize historical heavy replacement-Apply episodes after decoding matched ETLs.

Input files C03/C04/C06/C07.etl are verified against the repository's capture table.
Pass repository, ETL directory, empty private scratch directory and output directory.
"""
import argparse
import csv
import hashlib
import subprocess
import sys
from collections import defaultdict
from pathlib import Path
import numpy as np

CAPTURES = ("C03", "C04", "C06", "C07")
TICKS = 10_000

def read_rows(path):
    with path.open() as f:
        return list(csv.DictReader(f))

def group_samples(rows):
    return np.split(rows, np.flatnonzero(np.diff(rows["t"]) > 50_000) + 1)

def analyze(prefix, reference, roots, impl):
    rows = np.memmap(prefix.with_suffix(".samples"), dtype=impl.SAMPLE, mode="r")
    labels, ids = impl.identify(rows, roots)
    tids, starts = np.unique(rows["tid"], return_index=True)
    spans = {int(t): (a,b) for t,a,b in zip(tids, starts, [*starts[1:],len(rows)])}
    main = next(int(line.split()[0]) for line in prefix.with_suffix(".threads").read_text().splitlines()
                if line.split()[1] == "entry")
    main_lo, main_hi = spans[main]
    workers = {t for t,(lo,hi) in spans.items() if t != main and np.count_nonzero(ids[lo:hi])>=3}
    pairs = impl.extract_waits(prefix, rows, labels, ids, spans, workers)
    ready_stacks = defaultdict(set)
    for row in prefix.with_suffix(".stacks").read_text().splitlines():
        tick,event,unity,rva,*_ = row.split()
        if event == "R" and int(unity):
            ready_stacks[int(tick)].add(int(rva))
    clusters = sorted((c for lo,hi in spans.values()
                       for c in group_samples(rows[lo:hi][ids[lo:hi] == labels.index(impl.MOD)])
                       if len(c) >= 8),key=lambda c:c["t"][0])
    assert len(reference) == len(clusters)
    episodes, waits, windows = [], [], []
    time_order = np.argsort(rows["t"])
    timeline = rows["t"][time_order]
    def named_cpu(lo, hi):
        ix = time_order[np.searchsorted(timeline,lo):np.searchsorted(timeline,hi,side="right")]
        return np.bincount(ids[ix], minlength=len(labels)),np.bincount(
            ids[ix[(ix>=main_lo)&(ix<main_hi)]],minlength=len(labels))
    for seq, (c,ref) in enumerate(zip(clusters,reference),1):
        lo,hi = int(c["t"][0]),int(c["t"][-1])
        worker = int(c["tid"][0])
        assert abs((lo-int(clusters[0]["t"][0]))/TICKS - float(ref["apply_start_ms_from_first"])) < 0.00011
        assert int(ref["apply_samples"])==len(c)
        assert abs((hi-lo)/TICKS-float(ref["apply_span_ms"])) < 0.00011
        # Include all distinct blocked waits intersecting the sampled Apply interval.
        # Intersection with the blocked, not just off-CPU, part is required.
        overlapping = [w for w in pairs if w.state==5 and w.ready_count==1 and w.start<hi and w.ready>lo]
        direct = [w for w in overlapping if w.source==worker]
        same_last = [w for w in direct if w.label==impl.MOD]
        episodes.append(dict(capture=prefix.name,episode=seq,apply_samples=len(c),
            apply_span_ms=(hi-lo)/TICKS,entry_blocked_overlap_ms=round(sum(
                (min(hi,w.ready)-max(lo,w.start))/TICKS for w in overlapping),4),
            overlapping_waits=len(overlapping),apply_thread_ready=len(direct),
            apply_near_ready=len(same_last),apply_sustained_near_ready=sum(w.observations>=3 for w in same_last)))
        for w in overlapping:
            waits.append(dict(capture=prefix.name,episode=seq,start_minus_apply_end_ms=(w.start-hi)/TICKS,
                ready_minus_apply_end_ms=(w.ready-hi)/TICKS,blocked_ms=round(w.blocked_ms(),4),
                overlap_ms=(min(hi,w.ready)-max(lo,w.start))/TICKS,
                same_apply_thread=int(w.source==worker),reason=w.reason,recent_readier_activity=w.label,
                recent_sample_age_ms=w.age if w.label!=impl.MISSING else "",
                consecutive_named_samples=w.observations,sampled_suffix_ms=w.suffix,
                ready_unity_rvas=" ".join(hex(x) for x in sorted(ready_stacks[w.ready]))))
        for window, start, end in (("apply",lo,hi),("prior_25ms",lo-25*TICKS,lo-1),
                                   ("last_3ms",hi-3*TICKS+1,hi),
                                   ("first_3ms_after",hi+1,hi+3*TICKS),
                                   ("next_3ms_after",hi+3*TICKS+1,hi+6*TICKS),
                                   ("following_25ms",hi+1,hi+25*TICKS),
                                   ("control_minus250ms",lo-250*TICKS,hi-250*TICKS),
                                   ("control_plus250ms",lo+250*TICKS,hi+250*TICKS)):
            counts,main_counts=named_cpu(start,end)
            for label,count,main_count in zip(labels,counts,main_counts):
                windows.append(dict(capture=prefix.name,episode=seq,window=window,activity=label,
                                    samples=int(count),entry_samples=int(main_count)))
    import phase
    return episodes,waits,windows,phase.analyze(prefix,rows,ids,labels,clusters)

def main():
    arg=argparse.ArgumentParser(description=__doc__)
    for name in ["repo","inputs","scratch","out"]:arg.add_argument("--"+name,required=True,type=Path)
    args=arg.parse_args()
    repo=args.repo.resolve()
    path=repo/"evidence/release-critical-path"
    sys.path.insert(0,str(path))
    import summarize_wakes as impl
    args.scratch.mkdir(parents=True,exist_ok=True)
    if any(args.scratch.iterdir()):raise ValueError("Use an empty private scratch directory")
    args.out.mkdir(parents=True,exist_ok=True)
    binary=args.scratch/"decode"
    subprocess.run(["c++","-O2","-std=c++17",path/"read_release_trace.cpp","-o",binary],check=True)
    dispatch=args.scratch/"read_dispatch"
    subprocess.run(["c++","-O2","-std=c++17","-I",str(path),Path(__file__).with_name("read_dispatch.cpp"),"-o",dispatch],check=True)
    hashes={r["capture_id"]:r["trace_sha256"] for r in read_rows(repo/"evidence/trace-profile/captures.csv")}
    roots={r["label"]:int(r["export_rva"],16) for r in read_rows(path/"job-roots.csv")}
    roots.update({r["label"]:int(r["export_rva"],16) for r in read_rows(Path(__file__).with_name("extra-job-roots.csv"))})
    reference=read_rows(repo/"evidence/trace-profile/episodes.csv")
    combined=[[],[],[],[]]
    for c in CAPTURES:
        etl=args.inputs/(c+".etl")
        with etl.open("rb") as f:
            if hashlib.file_digest(f,"sha256").hexdigest()!=hashes[c]:raise ValueError(c+" hash mismatch")
        prefix=args.scratch/c
        subprocess.run([binary,etl,prefix],check=True)
        subprocess.run([dispatch,etl,prefix.with_suffix(".threads"),prefix],check=True)
        for dest,records in zip(combined,analyze(prefix,[r for r in reference if r["capture_id"]==c],roots,impl)):
            dest.extend(records)
    for name,records in zip(("historical-episodes","historical-waits","historical-windows","historical-phase"),combined):
        impl.write_rows(args.out/(name+".csv"),records)

if __name__=="__main__":
    main()
