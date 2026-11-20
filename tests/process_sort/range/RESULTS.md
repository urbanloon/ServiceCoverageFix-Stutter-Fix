# Park range construction on two Burst targets

The floor shortcut slowed all four shapes. Precomputing the entire length factor in a separate edge table improved the isolated arithmetic, but with the stock sort included the paired medians ranged from 0.989× to 1.043×. This experiment gives no indication of a savings large enough to justify a new cache and upstream worker for this expression alone.

Each cell is a median paired baseline/candidate CPU time ratio. Above one favors the candidate. The local and random shapes use the same archived density values, curves, park magnitudes and total path length. Paths and costs were generated. Each execution ran on one pinned thread. Both native builds ran twice, with 18 randomized rounds per run. The output CSV includes both thread CPU and wall-clock times. Native sample spans and frames in the game have different denominators.

| Machine code | Edge selection | Floor shortcut, build | Precomputed, build | Floor shortcut, build + sort | Precomputed, build + sort | Pointer control, build + sort |
|---|---|---:|---:|---:|---:|---:|
| AVX2 | Local | 0.366× | 1.188× | 0.926× | 1.043× | 1.022× |
| AVX2 | Random | 0.365× | 1.190× | 0.936× | 1.000× | 1.001× |
| SSE4 | Local | 0.386× | 1.177× | 0.937× | 1.023× | 1.025× |
| SSE4 | Random | 0.379× | 1.117× | 0.928× | 0.989× | 0.993× |

The native worker SHA-256 hashes matched across both builds. AVX2 was 565bd9fa6b291bbaa64bac45736b42a91e48bc3333e8024ba841feb6ba2fca55. SSE4 was 7f0efdb664aefe50819b127c23fd48306fa3f4cf27e8938b55a8ed448fe485fe. The corresponding [fixture design and reproduction commands](../array-baseline/README.md) describe what the arithmetic benchmark includes.
