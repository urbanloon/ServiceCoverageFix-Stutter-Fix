"""Identify navigation samples following each historical heavy Apply episode."""
from bisect import bisect_left,bisect_right
from collections import defaultdict
import csv
import numpy as np

def analyze(prefix, rows, ids, labels, clusters):
    edges=defaultdict(lambda:defaultdict(list))
    # Keep events only in broad neighborhoods of these episodes. A missing
    # switch-out outside a neighborhood makes that possible join unresolved.
    neighborhoods=[(int(c["t"][0])-5_000_000,int(c["t"][-1])+5_000_000) for c in clusters]
    starts=[item[0] for item in neighborhoods]
    for line in prefix.with_suffix(".dispatch").open():
        event,t,tid,cpu,arg=line.split()
        t=int(t)
        n=bisect_right(starts,t)-1
        if n>=0 and t<=neighborhoods[n][1]:
            edges[int(tid)][event].append((t,int(cpu),int(arg)))
    for ev in edges.values():
        for records in ev.values():records.sort()
    def last(tid,event,t):
        records=edges[tid][event]
        i=bisect_right(records,(t,2**32,2**32))
        return records[i-1] if i else (0,0,0)
    def slice_rows(tid,event,lo,hi):
        records=edges[tid][event]
        return records[bisect_left(records,(lo,)):bisect_right(records,(hi,2**32,2**32))]
    order=np.argsort(rows["t"])
    sorted_times=rows["t"][order]
    report=[]
    for seq,c in enumerate(clusters,1):
        lo,hi=int(c["t"][0]),int(c["t"][-1])
        source=int(c["tid"][0])
        ix=order[np.searchsorted(sorted_times,lo):np.searchsorted(sorted_times,hi+250_000,side="right")]
        for name in ("Car navigation","Human navigation"):
            nav=ix[ids[ix]==labels.index(name)]
            during=nav[rows["t"][nav]<=hi]
            after=nav[rows["t"][nav]>hi]
            if not len(after):raise RuntimeError("Missing following sample")
            hits=after[rows["t"][after]<=hi+30_000]
            # Only count a single observation per destination thread.
            first=dict()
            for k in hits:
                first.setdefault(int(rows[k]["tid"]),k)
            same_observed,strict_worker,strict_first,wake_seen=set(),set(),False,set()
            nav_first_observed=0
            ages=[]
            for target,k in first.items():
                ts,cpu=int(rows[k]["t"]),int(rows[k]["cpu"])
                if target==source:
                    if int(c["cpu"][-1])==cpu and last(source,"I",ts)[0]<=hi:
                        same_observed.add(target)
                    continue
                start,in_cpu,_=last(target,"I",ts)
                old,_,state=last(target,"O",start-1)
                if in_cpu!=cpu or not old or state!=5:continue
                if start < hi-5_000_000 or old < hi-5_000_000:continue
                wakes=slice_rows(target,"R",old,start)
                if len(wakes)!=1:continue
                ready,ready_cpu,readier=wakes[0]
                if readier!=source or ready<hi or ready-hi>10_000:continue
                wake_seen.add(target)
                if (int(c["cpu"][-1])!=ready_cpu or last(source,"I",ready)[0]>hi or
                    slice_rows(source,"O",hi,ready)):continue
                # Enforce same last physical sample on the source.
                intervening=ix[(rows["tid"][ix]==source)&(rows["t"][ix]>hi)&(rows["t"][ix]<=ready)]
                if len(intervening):continue
                strict_worker.add(target)
                ages.append((ready-hi)/10_000)
                earlier=ix[(rows["tid"][ix]==target)&(rows["t"][ix]>=start)&(rows["t"][ix]<ts)]
                if not len(earlier):nav_first_observed+=1
                if k==after[0]:strict_first=True
            report.append(dict(capture=prefix.name,episode=seq,activity=name,
                during_apply_samples=len(during),following_25ms_samples=len(after),
                first_after_apply_ms=(int(rows["t"][after[0]])-hi)/10_000,
                sampled_threads_within_3ms=len(first),
                first_3ms_on_uninterrupted_apply_thread=len(same_observed),
                directly_readied_threads_near_last_apply_sample=len(wake_seen),
                directly_readied_threads_strict_final_apply_sample=len(strict_worker),
                first_nav_sample_on_strictly_readied_thread=int(strict_first),
                first_destination_sample_already_navigation=nav_first_observed,
                min_strict_final_apply_to_ready_ms=min(ages,default=""),
                max_strict_final_apply_to_ready_ms=max(ages,default="")))
    return report
