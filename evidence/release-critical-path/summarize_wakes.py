"""Summarize scratch records from read_release_trace.cpp, without emitting raw OS identifiers."""
import argparse
import bisect
import csv
from collections import Counter, defaultdict
from dataclasses import dataclass
from pathlib import Path

import numpy as np

TICKS_PER_MS = 10_000
FIELDS = ["tid", "cpu", "leaf", "root", "branch", "joined", "first_caller"]
SAMPLE = [("t", "<u8")] + [(name, "<u4") for name in FIELDS]
MISSING = "No recent readier sample"
OTHER = "Other or unresolved activity"
DISPATCH = ["Other pathfinding dispatcher work", "Coverage graph", "Ordinary route", "Availability"]
MOD = "Replacement Apply"


def write_rows(path, rows):
    with path.open("w", newline="") as stream:
        w = csv.DictWriter(stream, fieldnames=rows[0])
        w.writeheader()
        w.writerows(rows)


def identify(samples, symbols):
    names = [OTHER, MOD] + DISPATCH + [key for key in symbols if key != "Pathfinding dispatcher"]
    ids = np.zeros(len(samples), dtype=np.uint8)
    for name, rva in symbols.items():
        matched = (samples["root"] >= rva) & (samples["root"] < rva + 30)
        if name == "Pathfinding dispatcher":
            ids[matched] = names.index(DISPATCH[0]) + samples["branch"][matched]
        else:
            ids[matched] = names.index(name)
    ids[(samples["leaf"] >= 0x80000000) & (samples["leaf"] != 0xffffffff)
        & (samples["joined"] != 0)] = names.index(MOD)
    return names, ids


@dataclass
class Wait:
    start: int
    end: int
    reason: int
    state: int
    ready_count: int
    ready: int = 0
    source: int = 0
    worker: bool = False
    label: str = MISSING
    age: float = 0
    suffix: float = 0
    observations: int = 0
    cpu_since: int = 0
    has_same_run_sample: bool = False

    def blocked_ms(self):
        return (self.ready - self.start) / TICKS_PER_MS


def extract_waits(prefix, samples, labels, ids, thread_ranges, workers):
    switches = sorted((int(t), direction, int(reason), int(state))
                      for t, direction, reason, state in
                      (line.split() for line in prefix.with_suffix(".changes").read_text().splitlines()))
    readies = sorted(tuple(map(int, line.split())) for line in prefix.with_suffix(".ready").read_text().splitlines())
    ready_times = [r[0] for r in readies]
    times = {tid:np.asarray(samples["t"][lo:hi]).copy() for tid,(lo,hi) in thread_ranges.items()}
    pairs, off = [], None
    for t, direction, reason, state in switches:
        if direction == "out":
            off = t, reason, state
            continue
        if off is None:
            continue
        lo, reason, state = off
        off = None
        first, last = bisect.bisect_left(ready_times, lo), bisect.bisect_right(ready_times, t)
        wait = Wait(lo, t, reason, state, last-first)
        pairs.append(wait)
        if wait.ready_count != 1:
            continue
        ready, cpu, tid, since = readies[first]
        wait.ready, wait.source, wait.worker, wait.cpu_since = ready, tid, tid in workers, since
        if tid not in thread_ranges or not since:
            continue
        l, h = thread_ranges[tid]
        k = np.searchsorted(times[tid], ready, side="right")-1
        if k < 0:
            continue
        idx = l+k
        st = int(samples["t"][idx])
        if st < max(lo,since) or int(samples["cpu"][idx]) != cpu:
            continue
        wait.has_same_run_sample = True
        wait.age = (ready-st) / TICKS_PER_MS
        if wait.age > 1:
            continue
        wait.label = labels[ids[idx]]
        if wait.label == OTHER:
            continue
        j = idx
        while j > l and ids[j-1] == ids[idx]:
            prev = samples[j-1]
            if int(prev["cpu"]) != cpu or int(prev["t"]) < max(lo,since):
                break
            if int(samples["t"][j]) - int(prev["t"]) > 13_000:
                break
            j -= 1
        wait.suffix = (st-int(samples["t"][j])) / TICKS_PER_MS
        wait.observations = idx-j+1
    return pairs


def control(samples, ids, names, pairs, stack_rows):
    mask = ((ids == names.index("Stock Apply")) & (samples["leaf"] >= 0xa7c160)
            & (samples["leaf"] < 0xa7c55b))
    rows = np.sort(np.array(samples[mask]), order="t")
    clusters = np.split(rows,np.flatnonzero((np.diff(rows["t"])>50_000)
                                            |(rows["tid"][1:] != rows["tid"][:-1]))+1)
    heavy = [c for c in clusters if len(c)>=8]
    unity_frames = defaultdict(set)
    for tick,event,unity,rva,*_ in stack_rows:
        if event=="R" and int(unity):
            unity_frames[int(tick)].add(int(rva))
    direct, exact, strict = 0, Counter(), 0
    for cluster in heavy:
        lo, hi = int(cluster["t"][0]), int(cluster["t"][-1])
        overlaps = [(min(w.end,hi)-max(w.start,lo),w) for w in pairs if w.start<hi and w.end>lo]
        if not overlaps:
            continue
        w = max(overlaps,key=lambda x:x[0])[1]
        if w.state==5 and w.ready_count==1 and w.source==int(cluster["tid"][0]):
            direct += 1
            exact.update(unity_frames[w.ready])
            strict += w.label=="Stock Apply" and w.observations>=3
    return dict(capture="C05",heavy_apply_episodes=len(heavy),same_worker_readied_entry=direct,
                exact_unity_ready_stacks=sum(exact.values()),unique_first_unity_frames=len(exact),
                first_unity_rva=hex(exact.most_common(1)[0][0]),strict_three_sample_matches=strict)


def capture(prefix, symbols):
    rows = np.memmap(prefix.with_suffix(".samples"), mode="r", dtype=SAMPLE)
    names, ids = identify(rows,symbols)
    threads = dict(line.split("\t",1) for line in prefix.with_suffix(".threads").read_text().splitlines())
    main = next(int(k) for k,v in threads.items() if v=="entry")
    tids, starts = np.unique(rows["tid"],return_index=True)
    ranges = {int(t): (lo,hi) for t,lo,hi in zip(tids,starts,[*starts[1:],len(rows)])}
    workers = {t for t,(lo,hi) in ranges.items() if t!=main and np.count_nonzero(ids[lo:hi])>=3}
    pairs = extract_waits(prefix,rows,names,ids,ranges,workers)
    stack_rows = [line.split() for line in prefix.with_suffix(".stacks").read_text().splitlines()]
    if prefix.name=="C05":
        return control(rows,ids,names,pairs,stack_rows)
    cpu, wait_rows, activities, examples = [],[],[],[]
    lo,hi = ranges[main]
    for i,name in enumerate(names):
        hits = ids==i
        cpu.append(dict(capture=prefix.name,activity=name,
                        cpu_samples=int(np.count_nonzero(hits)),
                        mapped_game_or_mod_leaf=int(np.count_nonzero(hits & (rows["leaf"]!=0xffffffff)
                                                                     & (rows["joined"]!=0))),
                        entry_thread_samples=int(np.count_nonzero(hits[lo:hi]))))
    waiting = [w for w in pairs if w.state==5]
    good = [w for w in waiting if w.ready_count==1]
    for threshold in [1,5,16.7,33.3]:
        subset = [w for w in good if w.blocked_ms()>=threshold]
        unmatched = [w for w in waiting if w.ready_count!=1 and w.end-w.start>=threshold*TICKS_PER_MS]
        workers_woke = sum(w.worker for w in subset)
        wait_rows.append(dict(capture=prefix.name,min_blocked_ms=threshold,matched_blocked_waits=len(subset),
                              blocked_seconds=round(sum(w.blocked_ms() for w in subset)/1000,5),
                              ready_to_switchin_seconds=round(sum(w.end-w.ready for w in subset)/1e7,5),
                              worker_context_ready=workers_woke,worker_contexts_in_capture=len(workers),
                              waits_with_reason37=sum(w.reason==37 for w in subset),
                              one_ms_readier_samples=sum(w.label!=MISSING for w in subset),
                              selected_named_activity=sum(w.label not in (MISSING,OTHER) for w in subset),
                              unmatched_waiting_intervals_offcpu_at_least_threshold=len(unmatched),
                              exact_game_source_ready_stacks_in_capture=len({int(t) for t,e,*_ in stack_rows if e=="R"})))
        for label in [*names,MISSING]:
            group = [w for w in subset if w.label==label]
            sustained = [w for w in group if w.observations>=3]
            full = [w for w in sustained if w.suffix>=w.blocked_ms()/2]
            activities.append(dict(capture=prefix.name,min_blocked_ms=threshold,readier_activity=label,
                                   near_wakes=len(group),near_within_half_ms=sum(w.age<=0.5 for w in group) if label!=MISSING else "",
                                   sustained_wakes=len(sustained),sustained_covering_half_wait=len(full),
                                   sustained_sampled_span_sum_ms=round(sum(w.suffix for w in sustained),4),
                                   longest_sustained_sampled_span_ms=round(max((w.suffix for w in sustained),default=0),4)))
            if threshold==5 and sustained:
                w=max(sustained,key=lambda x:x.suffix)
                # Time relative to the first sampled game-thread instruction.
                origin=int(rows["t"].min())
                examples.append(dict(capture=prefix.name,activity=label,wait_start_seconds=round((w.start-origin)/1e7,5),
                                     blocked_ms=round(w.blocked_ms(),4),readier_consecutive_samples=w.observations,
                                     first_to_last_sample_ms=round(w.suffix,4),last_sample_to_ready_ms=round(w.age,4),
                                     ready_to_switchin_ms=round((w.end-w.ready)/TICKS_PER_MS,4),old_wait_reason=w.reason))
        assert sum(row["near_wakes"] for row in activities if row["min_blocked_ms"]==threshold)==len(subset)
    seller = ids==names.index("Resource seller setup")
    helper = (rows["leaf"]>=0xabD0)&(rows["leaf"]<0xad65)
    seven_calls = [0x15a853, 0x15a88d, 0x15a8c4, 0x15a8f8, 0x15a932, 0x15a9a4, 0x15aa91]
    seller_helpers = [dict(capture=prefix.name, seller_mapped_leaves=np.count_nonzero(seller & (rows["leaf"]!=0xffffffff)),
                          in_helper=np.count_nonzero(seller & helper),
                          in_helper_from_seven_call_sites=np.count_nonzero(seller & helper & np.isin(rows["first_caller"],seven_calls)))]
    entry_runs = []
    switches = sorted(int(line.split()[0]) for line in prefix.with_suffix(".changes").read_text().splitlines())
    entry = rows[lo:hi]
    run = np.searchsorted(switches,entry["t"])
    for name in ("Ordinary route","Coverage graph","Resource seller setup","Replacement Apply"):
        pick = np.flatnonzero(ids[lo:hi]==names.index(name))
        if len(pick):
            groups = np.split(pick,np.flatnonzero((np.diff(pick)!=1) | (np.diff(entry["t"][pick])>13000)
                                                  | (run[pick[1:]]!=run[pick[:-1]]))+1)
            durations = [(int(entry["t"][g[-1]])-int(entry["t"][g[0]]))/TICKS_PER_MS for g in groups]
            idx = int(np.argmax(durations))
            longest, count = durations[idx], len(groups[idx])
        else:
            longest, count = 0,0
        entry_runs.append(dict(capture=prefix.name,activity=name,entry_samples=len(pick),
                               longest_uninterrupted_sample_span_ms=round(longest,4),samples_in_that_span=count))
    return cpu,wait_rows,activities,examples,seller_helpers,entry_runs


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("private_records",type=Path)
    parser.add_argument("output_directory",type=Path)
    args=parser.parse_args()
    args.output_directory.mkdir(parents=True,exist_ok=True)
    with Path(__file__).with_name("job-roots.csv").open() as f:
        symbols={r["label"]:int(r["export_rva"],16) for r in csv.DictReader(f)}
    groups=[[],[],[],[],[],[]]
    for name in ("C08","C09"):
        for group,rows in zip(groups,capture(args.private_records/name,symbols)):
            group.extend(rows)
    for name,rows in zip(("release-cpu","release-waits","wake-activity","wake-examples","seller-lookup","entry-runs"),groups):
        write_rows(args.output_directory/(name+".csv"),rows)
    if (args.private_records/"C05.samples").is_file():
        write_rows(args.output_directory/"infoview-control.csv",[capture(args.private_records/"C05",symbols)])


if __name__=="__main__":
    main()
