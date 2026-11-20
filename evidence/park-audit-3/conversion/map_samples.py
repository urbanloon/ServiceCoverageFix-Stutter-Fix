import csv, sys
from pathlib import Path
import numpy as np
sample = [('t','<u8')] + [(k,'<u4') for k in ('tid','cpu','leaf','root','branch','joined','first_caller')]
def within(x,start,end): return (x>=start)&(x<end)
for_output = []
for file in sorted(Path(sys.argv[1]).glob('*.samples')):
    all_samples=np.fromfile(file,dtype=sample)
    hits=all_samples[within(all_samples['root'],0x35c1500,0x35c151e)&(all_samples['leaf']<0x80000000)&(all_samples['joined']!=0)]
    leaf=hits['leaf']; caller=hits['first_caller']
    has=within(leaf,0xabd0,0xad65)&(caller==0xa6d930)
    lane=(caller==0xa6daa6)|within(leaf,0xa6daa6,0xa6daaf)
    owner=(caller==0xa6dae0)|within(leaf,0xa6dae0,0xa6dae6)
    updates=(caller==0xa6db28)|(caller==0xa6db4d)|within(leaf,0xa6db17,0xa6db52)
    output=within(leaf,0xa6dbde,0xa6dd50)
    assert not np.any((has.astype(int)+lane+owner+updates+output)>1)
    row=dict(capture=file.stem,total=len(hits),lane_presence=int(sum(has)),lane_read=int(sum(lane)),owner_read=int(sum(owner)),improved_duplicate_map_change=int(sum(updates)),final_emit_loop_direct=int(sum(output)))
    print(row); for_output.append(row)
with open('conversion-sites.csv','w') as f:
 w=csv.DictWriter(f,fieldnames=for_output[0]);w.writeheader();w.writerows(for_output)
