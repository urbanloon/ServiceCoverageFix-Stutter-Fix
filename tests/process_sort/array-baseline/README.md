# Process and range audit against 0.3.6

The upstream simulation and preview jobs in 0.3.6 are the stock game jobs. This audit found no upstream change that merits another scheduler substitution. The earlier sort experiment used a different overload. Correcting it changes which sorts win on shuffled inputs, but each candidate still loses on plausible input orders.

## Exact stock sort binding

On the current Game.dll (SHA-256 starting f076df4b), ProcessCoverageJob.Execute at IL_0369 calls Sort<T>(NativeArray<T>). The historical benchmark bound Sort<T>(T*, int) in both its native worker and managed oracle. Unity.Collections sends those overloads into IntroSortStruct and IntroSort respectively. Compiling the actual private Process job with Burst 1.8.23 also produced IntroSortStruct, PartitionStruct and HeapSortStruct symbols. The shipping Apply worker has its own independent native differential and visit-order oracle.

The corrected harness borrows the existing rows as a NativeArray and directly instantiates both operations with the game's private CoverageElement. The descriptor candidates use the same NativeArray overload for their descriptors. Native AVX2 output matched the independent managed execution of the corrected stock method in 161 edge-size cases, 200 additional mixed-bit/equality cases, and every full-size warmup and timed repetition. Seventeen equal priorities reproduced the concrete stock ID permutation reported previously. Eleven paired rounds ran for each method and shape at each scale. Timing uses one thread pinned to CPU 6, generated keys and elapsed time. Other activity on the shared host can affect the dispersion. The range tests below separately recorded thread CPU time.

Median paired speedup, at 421 providers / 1,111,964 elements and 523 / 1,467,894 respectively.

| Input per provider | Strict descending guard | Descriptor gather | Descriptor cycles | Unique-only radix |
|---|---:|---:|---:|---:|
| Shuffled distinct | 0.987 / 0.995 | 1.097 / 1.046 | 1.045 / 1.055 | 4.005 / 3.675 |
| Shuffled with ties | 1.022 / 1.007 | 1.081 / 1.060 | 1.059 / 1.035 | 0.993 / 0.990 |
| Descending distinct | 3.056 / 6.830 | 0.828 / 0.780 | 0.946 / 1.013 | 0.533 / 0.489 |
| Descending with ties | 0.856 / 1.023 | 0.831 / 0.971 | 0.868 / 0.991 | 1.026 / 0.983 |
| About 1% random swaps | 0.958 / 0.931 | 0.820 / 0.818 | 1.002 / 1.074 | 0.590 / 0.617 |
| Ascending distinct | 0.994 / 0.977 | 0.994 / 0.945 | 1.027 / 1.036 | 0.841 / 1.084 |
| All equal | 1.017 / 1.016 | 1.209 / 1.222 | 1.254 / 1.167 | 0.965 / 1.025 |
| One duplicate at end | — | — | — | 0.802 / 0.806 |
| Shuffled fractional keys | — | — | — | 3.744 / 4.374 |

The CSVs here preserve per-round times. The summary includes within-run percentile bootstrap intervals. Run the commands in the [sort report](../REPORT.md) with 11 native rounds to repeat these trials.

## Park inputs and ECS operations

The archetypes in Loon Lake Episode 10, Prep 4 contain 533 Park query candidates. Every one has ModifiedServiceCoverage, positive capacity (200–15,000) and positive magnitude. None has an owner or assigned service-district buffer. A one-district fast path and an early nonpositive-capacity exit would therefore select zero park providers in that save. All 6,128 saved coverage edges have Road, Curve, Density and BorderDistrict. Density is at or below the 0.01 floor for 3,713 edges.

Prepare builds one record and advances its prefix offset per nonempty provider. It reserves the element list once. Clear resets each edge's current service slot, including edges a previously covering park can no longer reach. Process resolves the provider's parameters and traverses its path. Each path edge performs the writable ServiceCoverage buffer lookup first. An assigned district can filter the edge. Accepted edges resolve density and curve and store a 32-byte record. The AVX2 stock producer broadcasts magnitude and efficiency per provider, uses packed x/y math, and emits one scalar square root for the length factor. Combining magnitude and efficiency into one scalar changes the two rounding steps.

Vanilla path result conversion emits each road edge once per provider. A provider-local geometry memo therefore has no repeated road on the stock path. A cache shared across providers needs additional lifetime, concurrency and invalidation handling. A successful writable buffer lookup also stamps the component chunk's change version. Suppressing that lookup before district rejection or in a budget fast path would need a separate proof that all of its marks are already made, including in the preview. Keeping the normal lookups preserves this behavior.

## Coverage arithmetic experiment

The [range fixture](../range) rebuilds curve length with the real Colossal MathUtils.Length function and uses the saved road densities and park magnitudes. It generates two sets of 1,111,964 path entries over the 533 providers. Each provider visits a distinct subset of roads in shuffled order. The local shape takes its nearest roads around an independently sampled center and uses generated endpoint costs based on distance. The random shape samples the road indices and costs independently. Efficiencies are one. No live path-buffer data are serialized in the save.

The baseline evaluates the stock floating-point expressions and emits all fields. Targets use unique surrogate road identities. District filtering and ECS storage are excluded. Every arithmetic variant matched the 32 bytes and order of every output against the baseline before timing, before and after sorting. Additional fixtures passed with arbitrary float bits and exceptional values in density, magnitude, efficiency and costs. The reference for the arithmetic is a transcription of stock IL with the real Unity math methods. Both sort paths use the private game element and actual comparator.

The density shortcut branches on the floor to avoid a square root. The precomputed mode reads a length factor calculated with the baseline expression and excludes the fill cost. The separate pointer control uses the historically measured overload. These are single-thread fixture costs. The city's parallel Process stage, job scheduling and rendering are outside the fixture. See [range results](../range/RESULTS.md).

From tests/process_sort, with the same native prerequisites as the earlier sort report and Python with numpy and zstandard:

```bash
range/build.sh "$GAME_MANAGED" "$DOTNET8" "$BURST_PACKAGE" "$DOTNET10" "$out/range"
python3 range/extract.py "$LOON_LAKE_PREP4" "$out/fixture.bin"
OPENBLAS_NUM_THREADS=1 taskset -c 6 python3 range/run.py "$out/range" "$out/fixture.bin" "$out/range-samples.csv"
```

This reconstruction requires the same archived save. The repository contains the generated timing observations and no city data.
