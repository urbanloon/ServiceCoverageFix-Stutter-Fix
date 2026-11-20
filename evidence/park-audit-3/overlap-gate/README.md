# Can road-pointer reuse select the faster merge?

Keep the existing 0.3.6 selector. Exact repeated pointers in the reserved ranges predict some shared-road shapes. The signal loses information about which roads a provider can actually afford to visit. It also costs work on short executions. This experimental gate was verified in offline native workers.

## Gate and controls

For at least 384 active providers and 262,144 prepared elements, sample one pointer from each of 384 distinct providers near one eighth of its range. Repeat near five eighths. The offset varies by provider. Check the list bounds and minimum range length before each load. In each band a fixed 256-entry table must find two different road pointers that each occurred twice. A table collision can drop a match. Only exact, nonzero pointer equality counts. A positive result chooses the two-competitor merge. Neither sampling nor hashing reads the pointed-to road data. The gate uses 2 KiB of bounded stack scratch and at most 768 element-pointer reads per job.

The initial sample counted actual target addresses in representative generated ranges after sorting providers by their first priority. A 256-provider pilot found only one repeated pointer in the first local band. With 384 samples, the summed full-band set results in the [initial observations](initial-probe.log) were as follows. The columns are distinct repeated addresses in the two bands as recognized by the direct-mapped table in that run. The underlying number of distinct roads is exact.

| Shape | Available elements | Distinct targets | Detected duplicate addresses, first / second bands |
|---|---:|---:|---:|
| Local road selection | 1,111,964 | 6,128 | 5 / 5 |
| Citywide and early-retirement profiles | 1,111,964 | 6,128 | 4 / 9 for both |
| Disjoint | 1,111,964 | 1,111,964 | 0 / 0 |
| First 24 per provider shared, all tails disjoint | 1,111,964 | 1,099,364 | 0 / 0 |
| Sparse city | 1,111,964 | 250,003 | 0 / 0 |
| Shared arterial hubs and disjoint local streets | 1,111,964 | 889,915 | 4 / 3 |

The direct-mapped table's collision count can change with the address at which the fixture is allocated. The same hub input selected different branches across process allocations in the isolated sampler runs. Counts from the full-band scan describe representative samples, independently of the classifier's early exit.

## Measured effect

Paired baseline/candidate thread-CPU ratios above one favor the gate. Each holdout fixture used 24 rounds per target, rotated and reversed worker order, pinned to CPU 6. Every native run compared target bytes with the original Game.dll worker and all four workload counters with 0.3.6. The compressed CSVs include wall-clock and thread-CPU times. Inputs are generated after applying the saved road count. No ECS scheduler or frame timings are included.

| Holdout (seed 1878) | Visits | AVX2 | SSE4 |
|---|---:|---:|---:|
| Local, 6,128 roads | 1,111,964 | 1.100× | 1.157× |
| Finite budget, disjoint roads | 510,220 | 1.049× | 1.004× |
| Sparse city | 1,111,964 | 0.980× | 1.010× |
| Shared arterial hubs | 637,580 | 1.040× | 0.931× |
| Shared tails outside the reachable budget | 67,854 | 0.993× | 1.046× |
| Early retirement, shared roads | 1,556 | 0.936× | 0.905× |
| 12 providers below the size threshold | 1,152 | 0.996× | 1.040× |

All 526 providers in the shared-tail fixture have 240 unique roads at the front of the range, with shared roads afterward. The gate chose the pair path on both native targets. The stock worker visited 67,854 elements and changed zero shared roads. Every visited road was unique. Positive coverage on initially clear targets makes the first possible shared visit observable. An independent exact-order harness also flipped the classification by changing unvisited tails while holding the visited targets constant.

A separate seed (682731) included a no-op worker that evaluates the same classifier and runs the baseline merge. Its otherwise unreachable branch uses a volatile read. AVX2 disassembly confirmed an emitted call to the classifier. The no-op/control ratio was 0.931× on the 1,528-visit early-retirement case, and the full gate was 0.886×. The unconditional pair alone was 0.997×. Across the 24-round holdout the unconditional pair on early retirement was 1.002× AVX2 and 1.004× SSE4.

Separate measurements called the emitted classifier directly on sorted provider states. The 12 trial medians, each based on 4,000 consecutive warm calls, ranged from 0.83 to 1.57 microseconds for the large shapes. The size-threshold return was approximately 4 ns. Warm repeated-call timings exclude the first-time cost of touching distant element records and cannot be subtracted from the full worker medians. Both full native workers remain sensitive to cache state, code layout and host scheduling. [Paired ratios and full-worker medians](summary.csv)

## Reproduction

Run from the repository root with the matching managed game, .NET, Harmony and Burst 1.8.23 inputs as described in [the worker instructions](../../../tests/burst_compare/README.md). Use a fresh output directory. The materializer reads the production source at 6c4c7f4, applies the experimental or no-op change into that directory and extends the verified park-overlap fixture.

```bash
python3 evidence/park-audit-3/overlap-gate/materialize.py "$out"
for mode in gated noop; do
    python3 tests/compile_managed.py "$GAME_MANAGED" "$HARMONY" "$out/$mode/managed/ServiceCoverageFix.dll" --src "$out/$mode/src" --csc "$CSC"
    for arch in AVX2 X64_SSE4; do
        python3 tests/burst_compare/compile_worker.py mod "$GAME_MANAGED" "$out/$mode/managed/ServiceCoverageFix.dll" "$out/$mode/$arch" --burst-runtime "$BURST_RUNTIME" --harmony-dir "$HARMONY_DIR" --target "$arch"
    done
done
dotnet build "$out/fixture/ParkOverlap.csproj" -c Release
runner="$out/fixture/bin/Release/net8.0/ParkOverlap.dll"
export PARK_OVERLAP_LAYOUT=scattered PARK_TIME_STOCK=0
for arch in AVX2 X64_SSE4; do
    taskset -c 6 dotnet "$runner" 24 1878 falsehot,disjoint,hubs,sparse,tight,local,small \
        "vanilla=$SELECTOR_WORKERS/vanilla/$arch" "base=$SELECTOR_WORKERS/base/$arch" \
        "pair=$SELECTOR_WORKERS/pair/$arch" "gated=$out/gated/$arch"
done
```

The [pair build](../../pass3-large-apply/README.md) uses the same 0.3.6 source and exact Burst toolchain. For the 20-round AVX2 screen, use seed 682731, shapes local,disjoint,bait,hubs,tight,sparse,sharedinter and worker order vanilla, base, pair, noop, gated. For the standalone classifier run, set GATE_MICRO to the absolute gated coverage.so path and write the full dynamic symbol name containing .HasSharedTargets( to a sibling file ending in coverage.so.symbol. Run the same fixture with zero timed rounds. This optional path directly calls the x64 Burst symbol and reports its decision and thread time. The two holdouts also preserve each sample's CPU timing and both worker identities.
