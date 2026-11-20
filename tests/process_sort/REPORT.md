# Upstream service coverage experiments

Keep the stock Prepare, Clear and per-provider Process jobs. Four exact ordering candidates were tested. None demonstrated a consistent sort benefit across the input orders the game can generate. The archived ETLs do not record the original keys inside live provider buffers.

## What the game does

Prepare walks provider chunks, checks each chunk’s shared service, emits one 24-byte building record for each nonempty path buffer, accumulates prefix offsets, and resizes the single element list once with ResizeUninitialized. A million reserved elements do not imply a million iterations of Prepare. Clear is a parallel chunk job that writes one default ServiceCoverage value to the active service slot of every queried edge. Skipping previously covered edges would leave old coverage unless replacement code could independently prove which edges need clearing.

Process works in parallel by provider. It resolves coverage parameters, owners, efficiency, districts, network buffers, density and curve length. It emits accepted 32-byte records, sorts each accepted subrange by descending average coverage, and sets the provider capacity and accepted count. Apply consumes the resulting order, including the identity order inside equal keys. Replacing the sort changes the native Process job boundary. The private Burst job is already AOT-compiled in the game.

The resolved type, field and method-instruction listings are identical across the original verified Game.dll (721e7e17…) and the supplied later build (f076df4b…). The corresponding Unity.Collections assemblies differ in file hash but give identical NativeSortExtension listings. The inspection commands below reproduce both comparisons.

In the archived ETW review, the stock simulation and Park infoview captures had 0 and 3 matched Prepare leaves, and 5 and 12 Clear leaves, across the full respective recordings. Parallel Process spans had run medians of 3.19–5.25 ms in the seven heavy-episode captures. The identified sorting routines contributed 22–29% of Process samples. The historical infoview capture’s median Apply span was 77.99 ms. CPU sample shares cannot be multiplied by elapsed time to obtain a frame-time saving.

## Historical pointer-overload sorting results

The following earlier timing table used the pointer overload of Unity's sort. The game's Process job calls the NativeArray overload, which enters a distinct native introsort implementation. Updated comparisons with the exact stock overload are in [the Process audit](array-baseline/README.md).

One CPU core processed arrays of synthetic provider slices. The two scales combine a 421-provider workload with the prior 1,111,964-record Process benchmark size, and the logged Providence Bay input capacity of 523 providers and 1,467,894 records. This benchmark sorts all generated records. Actual Process can filter records before sorting.

The table gives paired median speedups at the two scales, in that order. Values above 1 favor the candidate. Both the baseline and candidates ran as Linux AVX2 machine code produced by Unity Burst 1.8.23. The historical baseline instantiates the actual private game CoverageElement and its CompareTo in Unity's pointer-based NativeSort. Each timed output was compared byte-for-byte, including identity and 32-byte stride, with an independent .NET execution of that pointer overload.

| Input within each provider | Strict descending guard | Compact descriptors + gather | Compact descriptors + cycles | Four-pass radix, unique only |
|---|---:|---:|---:|---:|
| Shuffled distinct | 1.005 / 1.001 | 0.965 / 0.960 | 0.922 / 0.921 | 5.625 / 5.550 |
| Shuffled tied | 0.999 / 0.998 | 0.961 / 0.965 | 0.917 / 0.927 | 0.999 / 0.997 |
| Descending distinct | 6.912 / 6.060 | 0.885 / 0.879 | 1.110 / 1.080 | 0.725 / 0.749 |
| Descending tied | 1.002 / 0.985 | 0.973 / 0.952 | 0.902 / 0.905 | 0.991 / 0.996 |
| Descending with about 1% random swaps | 1.001 / 1.002 | 0.867 / 0.866 | 1.054 / 1.041 | 0.798 / 0.835 |
| Ascending distinct | 1.001 / 0.998 | 1.091 / 1.089 | 1.148 / 1.146 | 1.197 / 1.268 |
| All equal | 0.992 / 1.001 | 1.140 / 1.133 | 1.084 / 1.076 | 0.996 / 0.998 |
| Shuffled with one duplicate at the last position | — | — | — | 0.873 / 0.884 |
| Shuffled keys computed as 1 − normalized rank squared | — | — | — | 5.382 / 5.316 |

The strict guard can skip the sort only when every neighboring priority strictly decreases. For 17 equal keys with input IDs 1 through 17, stock NativeSort emitted IDs 1, 15, 14, 13, 12, 11, 10, 16, 9, 7, 6, 5, 4, 3, 2, 8, 17. A non-increasing guard or a stable radix sort would preserve the input IDs and change the result. Unity’s comparator also gives special behavior to NaNs and treats both zero signs as equal.

The 8-byte descriptor candidates store the float key alongside the original index. They run the same Unity introsort on the same sequence of comparisons, preserving the concrete permutation even for equal keys. Gather uses a temporary row buffer and copies back. Cycle decomposition permutes the existing buffer. Earlier diagnostic JIT timings favored descriptors on random input by about 35% at the smaller scale. The native runs at both scales instead favor full records on shuffled inputs. Detailed JIT CSVs are retained to show that distinction.

The radix candidate first checks for NaNs and obvious duplicates. It normalizes signed zero, sorts four bytes of the integer key, then checks adjacent keys for any duplicate missed by the slot cache. The original rows remain available to stock NativeSort until all keys pass. One late duplicate per provider costs roughly 13–15% more elapsed sort time because most radix work has already run. Perfectly descending streams also favor stock or the strict guard. The scratch arrays here are preallocated and reused, providing optimistic conditions for scratch-heavy candidates.

All four candidates passed 161 edge-size cases and 200 additional seeded trials covering arbitrary mixed-sign float bits, distinct values, one duplicate, paired zero signs and infinities. The full benchmark also checks every warmup and timed permutation. No ECS world, city, gameplay frame or game session ran in this experiment.

## Separate worker-throughput lead

CoverageJobs.ProcessResultsJob consolidates lane costs into first-seen road-edge order. The stock code hashes an edge again for each output and removes then reinserts an edge on an improving duplicate. An edge-to-output-index table could update the two cost lanes directly while retaining their first-seen order. The archived stock capture contains 4,000 matched ProcessResults leaves, compared with 327 in the Park infoview capture. Full-path correctness, duplicate/update rates and any effect on the simulation deadline remain unmeasured. This candidate was not implemented or timed.

## Reproduction

Prerequisites are Linux x86-64 with AVX2, an available CPU core, .NET SDK 8, .NET 10 runtime, the Unity Burst 1.8.23 package, and the matching game Managed directory including Colossal.Mono.Cecil. The historical pointer-overload runs used AMD EPYC 9V74, core 6, .NET SDK 8.0.419, one warmup, fixed seeds and randomized method order within each round. There are 15 paired timed rounds per native case and 13 per managed case. Allocation, input resets and byte verification are outside the timed interval. Scratch arrays are shared successively by simulated providers on the same thread. The runs exclude ECS lookups, job scheduling and cross-worker memory pressure.

Set GAME_MANAGED, DOTNET8, BURST_PACKAGE and DOTNET10 to absolute local paths. From this folder run:

```bash
out=/tmp/process-sort-test
native/build.sh "$GAME_MANAGED" "$DOTNET8" "$BURST_PACKAGE" "$DOTNET10" "$out"
"$DOTNET8" build ProcessSort.csproj -c Release -p:GameManagedPath="$GAME_MANAGED"
runner=bin/Release/net8.0/ProcessSort.dll
taskset -c 6 "$DOTNET8" "$runner" "$GAME_MANAGED" 421 1111964 0 "$out/process.so" verify
for providers in 421 523; do
    elements=1111964
    if test "$providers" = 523; then elements=1467894; fi
    taskset -c 6 "$DOTNET8" "$runner" "$GAME_MANAGED" "$providers" "$elements" 15 "$out/process.so" > "$out/burst-$providers.csv"
    taskset -c 6 "$DOTNET8" "$runner" "$GAME_MANAGED" "$providers" "$elements" 15 "$out/process.so" radix > "$out/radix-$providers.csv"
    taskset -c 6 "$DOTNET8" "$runner" "$GAME_MANAGED" "$providers" "$elements" 13 > "$out/managed-$providers.csv"
done
python3 summarize.py "$out"/burst-*.csv "$out"/radix-*.csv
```

The corrected binder instantiates both the borrowed NativeArray view and its sort with the private game element type, then writes a separate benchmark DLL consumed by Burst. The historical and current original game assemblies are opened read-only. To reproduce the structural comparison on each Managed directory:

```bash
inspector=native/bind/bin/Release/net8.0/BindGame.dll
"$DOTNET8" "$inspector" --inspect "$GAME_MANAGED/Game.dll" Game.Simulation.ServiceCoverageSystem | sha256sum
"$DOTNET8" "$inspector" --inspect "$GAME_MANAGED/Unity.Collections.dll" Unity.Collections.NativeSortExtension | sha256sum
```

Expected listing hashes are 6144776fef93c4175dca08e7b0efb6cb62fbe66c322cbef86fd0e8d82d1f4932 and 77a82fb823468a79c161da0f639f6344817d60527aecc2a170c7b922a129aa70 respectively. The CSV summaries include within-run bootstrap intervals for median paired speedup. They describe these synthetic repetitions on one machine.
