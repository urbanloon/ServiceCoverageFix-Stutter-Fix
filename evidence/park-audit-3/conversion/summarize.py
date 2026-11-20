import csv
from pathlib import Path
from collections import defaultdict
from statistics import median
rows=[]
for p in sorted(Path(__file__).parent.glob('full-*.csv')):
    groups=defaultdict(list)
    with p.open() as f:
        for row in csv.DictReader(f):
            key=tuple(row[k] for k in ('entities','results','roads','archetypes','missing_mask','order','shape'))
            groups[key].append([int(row[k]) for k in ('stock_ns','indexed_ns','combined_ns')])
    for key,vs in groups.items():
        ratios=[[v[0]/v[i] for v in vs] for i in (1,2)]
        rows.append([p.stem[5:],*key,len(vs),*[round(median([v[i] for v in vs])/1000,3) for i in range(3)],*[round(median(r),4) for r in ratios]])
with (Path(__file__).parent/'full-summary.csv').open('w') as f:
    w=csv.writer(f);w.writerow('target entities results roads archetypes missing_mask order shape rounds stock_us indexed_us combined_us indexed_paired_ratio combined_paired_ratio'.split());w.writerows(rows)
for target in sorted({r[0] for r in rows}):
    for i in [-2,-1]:
        c=[r[i] for r in rows if r[0]==target]
        print(target,i,len(c),min(c),max(c),sum(v<1 for v in c))
