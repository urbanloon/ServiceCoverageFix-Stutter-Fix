import ctypes as C, sys, re, csv,itertools
from time import perf_counter_ns
from pathlib import Path
import numpy as np
target=sys.argv[1]
rng=np.random.default_rng(int(sys.argv[2]) if len(sys.argv)>2 else 20265)
rounds=int(sys.argv[3]) if len(sys.argv)>3 else 24
lib=C.CDLL(str(Path(target,'kernel.so').resolve()));cb=C.CDLL(str(Path('callbacks.so').resolve()))
init=getattr(lib,'burst.initialize');init.argtypes=[C.c_void_p,C.c_void_p];init(cb.resolve_external,None)
fn=getattr(lib,re.search(r'--([a-f0-9]{32})',Path(target,'compile.log').read_text()).group(1));fn.argtypes=[C.c_void_p,C.c_int];fn.restype=C.c_int
class Descriptor(C.Structure):
 _fields_=[(k,C.c_void_p) for k in ['memory','inputs','outputs']]+[(k,C.c_int) for k in ['size','entities','count','arches','mask','roads','capacity','start','version','status']]
arena=np.zeros(64*1024*1024+64,dtype='B');ptr=arena.ctypes.data+(-arena.ctypes.data)%64
modes=list(itertools.permutations(range(3)));canary=np.uint32(0x7efa4321)
def fixture(ne,n,roads,mask,archetypes,order,pattern,version,writer=None):
    arena.fill(0)
    idx=np.arange(n)%ne if order=='adjacent' else rng.integers(0,ne,size=n)
    vals=np.ones((n,4),dtype='I');vals[:,0]=idx;vals[::61,1]=3
    costs=rng.normal(size=(n,2)).astype('f')
    if pattern=='special':
        vals[:,2:]=rng.choice([0,0x80000000,0x3f800000,0xbf800000,0x7fc00001,0xffc00053,0x7f800000,0xff800000,1,0x80000001],size=(n,2))
    else:
        if pattern=='rising':costs=np.repeat(np.arange(n)[:,None],2,axis=1).astype('f')
        if pattern=='falling':costs=np.repeat((n-np.arange(n))[:,None],2,axis=1).astype('f')
        vals[:,2:]=costs.view('I')
    valid=(vals[:,1]==1)&(((((idx//256)+1)%archetypes)&mask)==0)
    expected_edges=list(dict.fromkeys((idx[valid]%roads+19).tolist()))
    capacity=max(16,roads);out=np.full((3,capacity+2,4),canary,dtype='I')
    p=Descriptor(ptr,vals.ctypes.data,out[0,1:].ctypes.data,arena.nbytes-64,ne,n,archetypes,mask,roads,capacity,5,version,0)
    size=fn(C.byref(p),0);assert 0<size<=p.size
    times=[]
    for j in range((rounds if writer else 0)+2):
        dt=[0]*3
        for m in modes[j%6]:
            p.outputs=out[m,1:].ctypes.data
            out[m].fill(canary)
            cb.reset_arena()
            start=perf_counter_ns();result=fn(C.byref(p),m+1);dt[m]=perf_counter_ns()-start
            if version==1:
                assert result==len(expected_edges) and p.status==561,(result,len(expected_edges),p.status,ne,n,roads,mask,archetypes,m)
                assert np.array_equal(out[m,1:1+result,0],expected_edges)
                assert np.all(out[m,1:1+result,1]==3)
            else: assert result==5 and p.status==123 and np.all(out[m]==canary)
            assert np.all(out[m,0]==canary) and np.all(out[m,1+result:]==canary)
        assert np.array_equal(out[0],out[1]) and np.array_equal(out[0],out[2]),(ne,n,roads,mask,archetypes,pattern,'value mismatch')
        if j>=2 and writer:
            writer.writerow([ne,n,roads,archetypes,mask,order,pattern,j-2,*dt]);times.append(dt)
    if writer:
        a=np.asarray(times);print(ne,n,roads,archetypes,mask,order,pattern,'median ratios',round(np.median(a[:,0]/a[:,1]),3),round(np.median(a[:,0]/a[:,2]),3),flush=True)
for n in [0,1,99,101,517,4097]:
 for ar,mask in [(1,0),(5,7)]:
    for v in [1,3]:fixture(3072,n,257,mask,ar,'shuffled','special',v)
print('Checked 24 stock job differentials with alternative workers, versioned owner/lane, special floats, ordering and untouched tail',flush=True)
with open(target+'/samples.csv','w') as f:
 w=csv.writer(f);w.writerow(['entities','results','roads','archetypes','missing_mask','order','shape','round','stock_ns','indexed_ns','combined_ns'])
 for ne,n,roads in [(32768,8456,2114),(240000,24512,6128)]:
  for ar,mask in [(1,0),(9,1),(9,7)]:
   for order in ['adjacent','shuffled']:
    for pattern in ['rising','falling','random']:
     fixture(ne,n,roads,mask,ar,order,pattern,1,w);f.flush()
