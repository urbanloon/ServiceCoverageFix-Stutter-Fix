# Cities: Skylines II periodic-stutter investigation

## Version-history note

The published source progression is **0.1.8 to 0.3.2**. The complete original
0.1.8 source is preserved in commit `95121ee` and on the `archive-v0.1.8`
branch. Labels such as 0.1.9, 0.2.0, and 0.3.1 below identify unpublished
development experiments, not public releases. In particular, there was no
published or tested 0.3.1 build.

## Result

The sampled native work is the AVX2 Burst implementation of:

`Game.Simulation.ServiceCoverageSystem.ApplyCoverageJob.Execute()`

The pathological cost is its repeated linear reinsertion of 24-byte
`BuildingData` records. After a service provider consumes one sorted coverage
element, the job scans forward and shifts every higher-priority provider by one
slot before inserting the updated record. With `B` active providers and `E`
coverage elements, the ordering work can approach `O(E * B)` record moves.

This attribution does not rely on native strings or physical proximity between
a readable job name and the hotspot.

## Native address classification

All addresses below are PE RVAs in the supplied
`lib_burst_generated(1).dll` (SHA-256
`cb38360072a9a7fed7b06b0b745b0a0300dee60f4fd284117a16a4fd8dd2190d`).

| Observed RVA | Containing function from `.pdata` | Role |
|---|---:|---|
| `0xA7C491` | `0xA7C160–0xA7C55B` | Actual AVX2 work body; store of current `BuildingData.m_ElementIndex` during a record-shift loop |
| `0xA7B3F6` | `0xA7B3E0–0xA7B3FD` | Return site immediately after the wrapper's direct call to `0xA7C160` |
| `0x35C0FB7` | `0x35C0FA0–0x35C0FBE` | Return site immediately after the exported stub's indirect runtime-dispatch call |

`0x35C0FA0` is export-table entry 799 (ordinal base 800) with the exact
exported name `7c07d6bb6794d303cc764c088932d9ca`. Its ISA initialization exports
select one of four wrappers:

| ISA | Wrapper | Body |
|---|---:|---:|
| AVX | `0x17B2480` | `0x17B3200` |
| AVX2 | `0xA7B3E0` | `0xA7C160` |
| SSE2 | `0x32C4110` | `0x32C4E70` |
| SSE4 | `0x250FE40` | `0x2510B90` |

The ETW frame at `0xA7B3F6` therefore identifies AVX2 as the selected variant.
The only direct call to `0xA7C160` in the DLL's executable sections is the
wrapper call at `0xA7B3F1`.

## ETW relationship

The module image base recorded in the trace is `0x7FF9783F0000`. Manual parsing
of the variable-length ETW StackWalk payloads recovered 834 sampled stacks
containing this function. Every one has the same native chain, in this order:

1. an instruction in `0xA7C160–0xA7C55B`;
2. wrapper return address `0xA7B3F6`;
3. export-stub return address `0x35C0FB7`.

The first seven long episodes begin 0.000, 4.413, 8.816, 13.210, 17.672,
22.051, and 26.458 seconds relative to the first. Their worker thread IDs are
30840, 30440, 18088, 31696, 30632, 20944, and 31380 respectively. The full ETL
contains an eighth later episode on thread 5612.

Of the 834 body samples, 740 (88.7%) land in the reinsertion/record-shift
region `0xA7C440–0xA7C4F5`. The single most sampled instruction is
`0xA7C491` with 368 samples.

## Post-fix ETW validation

The second supplied trace is 56.461 seconds long and contains 8,423,005 stack
samples for `Cities2.exe` PID 8072. It records both the game Burst module and
the deployed v0.1.3 module:

| Module | Image base | Image size |
|---|---:|---:|
| `lib_burst_generated.dll` | `0x7FF980460000` | `0x3916000` |
| `ServiceCoverageFix_win_x86_64.dll` | `0x7FFA45E40000` | `0x5000` |

There are **zero** stacks in the original Apply body, wrapper, or export-stub
regions. This proves that the schedule replacement was active and that the
catastrophic implementation was not executing.

The replacement DLL has 326 leaf samples. Nine invocations are materially
long, with sampled spans of 24.895–27.998 ms (mean 26.428 ms). Their starts,
relative to the first game stack, are 1.348, 5.651, 9.930, 14.219, 18.488,
22.783, 27.050, 31.316, and 35.611 seconds. The mean start-to-start interval is
4.283 seconds, and each invocation runs on one Unity worker thread. Other
service slots usually contribute zero to five samples and occur at roughly
one-eighth of that interval.

Therefore the remaining small rhythmic frame-time spike is not an unrelated
rendering or simulation system. It is the v0.1.3 binary-heap Apply replacement
for the same heavy service slot. The first fix removed the quadratic record
shifts, but `O(E log B)` comparisons and 24-byte heap movements are still large
enough to consume about 26 ms on the pathological input.

## v0.1.4 radix trace and exact native resolution

Two subsequent stationary 1x traces captured v0.1.4 Release in the two bad
saves. The original game's Apply body, wrapper, and export stub have zero
samples in both captures. The replacement has eight heavy executions in each:

| Capture | v0.1.4 sampled execution span | Mean | Start cadence |
|---|---:|---:|---:|
| Loon Lake | 18.91–23.31 ms | 20.92 ms | 4.291 s |
| Providence Bay | 18.26–24.98 ms | 20.45 ms | 4.280 s |

The traced native module has SHA-256
`5adf019883b29d4a1104f5b189b7bd2521f31808456702a8aaf8d82e6a150ce7`.
Its matching native PDB has SHA-256
`34c8959336042a75df3cb2914536883e5a72b432470c0eb0b2000febaec5820e`.
The PE exception directory, native PDB symbols, managed IL, and disassembly
resolve the relevant ranges as follows:

| RVA | Native role |
|---:|---|
| `0x1030–0x104C` | Exported Burst job wrapper; `0x1046` is the post-call return site |
| `0x1050–0x11E0` | Top-level Execute: filtering, NativeSort, monotonic validation, and radix/fallback dispatch |
| `0x11F0–0x1554` | `ExecuteBinaryHeap` fallback |
| `0x1560–0x1A91` | `ExecuteRadixMerge` fast path |
| `0x18CF` | Inlined `ApplyElement`: the current-vs-target `float2` coverage test and coverage-update block |
| `0x19BB` | Inlined `GetPriority` for the just-advanced provider's next `CoverageElement` before radix reinsertion |

This falsifies the suspected fallback explanation: both reported hot offsets
are inside `ExecuteRadixMerge`, and neither is in the binary-heap range. The
remaining invocation still performs the original per-element coverage math
serially. v0.1.4 removed most selection overhead, but it cannot remove the
linear `O(E)` element pass itself. The full monotonic validation is another
linear read-only pass, but the reported `0x18CF`/`0x19BB` samples are not in it.

Version 0.1.5 attempted to report `(B, E)` from the managed scheduling bridge,
but that observation point precedes the Prepare dependency and consequently
reported zero-length lists. Version 0.1.6 captures the lengths at the beginning
of the Burst job, after Prepare has completed, then reports each result from the
managed bridge after its handle completes. This distinguishes a genuinely
enormous coverage-element workload from residual queue overhead before another
behavioral optimization is made.

The corrected Providence Bay capture reports a stable Park pass of 523
providers and 1,467,894 `CoverageElement` records. At the native 32-byte stride,
the defensive monotonicity gate touches roughly 47 MB before the merge performs
any coverage writes. The next largest pass is PostService at 193 providers and
139,958 elements. Version 0.1.7 removes this redundant full validation pass for
the exact hash-locked game build and reports how many elements the radix merge
actually processes before provider budgets are exhausted.

## Unpublished 0.1.9 cached-radix experiment

The v0.1.8 native fast path still performs two linked-list traversals whenever
radix bucket zero is empty: the first finds the minimum priority in the next
nonempty bucket, and the second redistributes that bucket around the new radix
base. It also searches bucket headers linearly to locate that source bucket.

The unpublished 0.1.9 experiment maintains the minimum priority of each bucket
as records are inserted and tracks nonempty buckets in a 33-bit mask. A refill obtains the
source bucket with a trailing-zero count and reads its cached minimum, then
performs only the required redistribution traversal. The queue representation,
priority transform, prepend-on-reinsertion rule, append-on-redistribution rule,
and per-element coverage calculation are unchanged.

The extended reference model compares the cached implementation against the
original linear-reinsertion sequence across the existing randomized integer,
tie-heavy, arbitrary-initial-order, float32, infinity, subnormal, signed-zero,
and adversarial cases. All selected provider/priority sequences remain exactly
equal. In synthetic workloads sized like the observed 421-provider/427,000-
element and 526-provider/510,000-element Park passes, caching removes one of
the two source-list walks, approximately 1.8 to 2.2 million linked-node visits.
This is a bounded micro-optimization; the irreducible serial coverage math is
still performed once for every processed element.

## Unpublished 0.2.0 compact hot-loop experiment

The unpublished 0.1.9 experiment stored each radix link and transformed
priority in the otherwise unused eight-byte `Entity` field at the front of the
24-byte provider record.
That avoided an allocation, but queue traversal still touched the wide provider
array and every selected element copied and rewrote all 24 bytes even though
only `ElementIndex`, `ElementCount`, and sometimes `Remaining` change.

The unpublished 0.2.0 experiment moves each link and priority into one dense
eight-byte stack node. At the observed 526-provider scale the nodes occupy about
4.2 KB. Provider records are now updated in place through pointers, and the
unchanged `Entity` and `Total` fields are no longer moved through the
half-million-iteration loop.
The diagnostic processed count is computed from provider-sized sums before and
after the merge instead of incrementing a loop-carried counter once per
element. Bucket selection and the sortable float-key conversion were also
simplified without changing their bit results. Burst is given valid non-alias
information for the disjoint provider and element arrays, and stack-local
zeroing is skipped because every metadata entry is explicitly initialized
before use. Dynamic queue metadata is capped at 8,192 providers (64 KiB); the
equivalent heap implementation is retained as a safety path above that bound.

The exact key rewrite was checked against the prior transform for 1,000,000
random 32-bit patterns plus signed zeros, infinities, subnormals, and signed
NaNs. The full provider-selection model still matches the original linear
reinsertion algorithm across dense ties, arbitrary initial tie order, random
float32 values, and 421-, 526-, and 1,024-provider stress cases.

A more aggressive consecutive-winner batching prototype was also tested. It is
sequence-equivalent, but on a 526-provider synthetic interleaved workload it
removed only 76 of 510,220 queue selections, and its timings were inconsistent
or worse at other observed service sizes. It is therefore deliberately not in
the runtime job. The compact-layout change showed a directional queue-only gain
without relying on provider paths having favorable runs; even that benchmark
does not predict the whole in-game job because the unchanged coverage math and
pointer writes remain once per processed element.

## Managed/native proof

The actual body consumes two list headers from job offsets 0 and 8. The first
list uses a 24-byte stride and the second a 32-byte stride. The matching private
managed job has exactly two fields:

- `NativeList<BuildingData> m_BuildingData`
- `NativeList<CoverageElement> m_Elements`

Their layouts match every native access:

| Structure | Managed fields and native offsets | Size |
|---|---|---:|
| `BuildingData` | `Entity` +0, `m_ElementIndex` +8, `m_ElementCount` +12, `m_Total` +16, `m_Remaining` +20 | 24 |
| `CoverageElement` | `m_CoveragePtr` +0, `m_Coverage` +8, `m_AverageCoverage` +16, `m_DensityFactor` +20, `m_LengthFactor` +24 | 32 |

The managed `Execute` IL (method RVA `0x432A9C`) and native body perform the
same complete sequence:

1. remove records with no elements or no remaining amount;
2. sort providers by the `CoverageElement` at each record's element index;
3. take the first provider and advance its element index;
4. update the pointed `float2` coverage using the same constants (`0.99`, `1`,
   `0.5`), three repeated squarings, clamp, saturate, and remaining-amount math;
5. decrement the element count;
6. linearly reinsert an active record by shifting 24-byte records.

Across `Game.dll`, the two private job-field tokens occur together only in
`ServiceCoverageSystem.OnUpdate` (where the job is constructed/scheduled) and
this `Execute` body. No second managed job with the same two-list relationship
was found.

At native `0xA7C491`, the surrounding stores are:

```text
0xA7C48B  current.Entity.Index
0xA7C48D  current.Entity.Version
0xA7C491  current.m_ElementIndex
0xA7C495  current.m_ElementCount
0xA7C499  current.m_Total
0xA7C49E  current.m_Remaining
```

This corresponds to managed IL `0x0235–0x0250`, which swaps the updated current
record forward one slot while moving the encountered record backward one slot.

## Periodicity

`ServiceCoverageSystem.COVERAGE_UPDATE_INTERVAL` is 256. The managed
`GetFrameService(uint frame)` implementation is equivalent to:

```text
(byte)(((frame % 256) * 8) / 256)
```

The service changes every 32 simulation frames and a given service repeats
every 256 frames. The eight enum values are Healthcare, FireRescue, Police,
Park, PostService, Education, EmergencyShelter, and Welfare. One service's
Apply job is pathological; the other seven interval slots are too short to
produce a visible hitch. The trace does not expose the service enum value, so
the native samples alone do not expose the affected enum member.

## Targeted save comparison

The supplied `.cok` files are ZIP containers. Their `SaveGameData` stream uses
an uncompressed context header followed by records containing an uncompressed
size, a compressed size, and a Zstandard frame. Zero-size records are valid
buffers; they are not segment separators.

The first three compressed records are the component-type table, system-type
table, and archetype table. Each following record corresponds to one serialized
archetype. This was verified against the supplied `Colossal.Core.dll`
`EntityDeserializer<T>`, `DeserializeArchetypeJob<T>`, and component serializer
implementations, and by consuming every record boundary exactly.

`ServiceCoverageSystem.m_BuildingQuery` requires:

- `Game.Net.CoverageServiceType` (read only);
- `Game.Pathfind.CoverageElement` (read only buffer);
- `Game.Prefabs.PrefabRef` (read only);
- and excludes `Game.Common.Deleted` and `Game.Tools.Temp`.

Applying that exact component query to the saved archetypes and decoding the
run-length encoded `CoverageServiceType` shared-component payload gives:

| Saved query candidates | Loon Lake (bad) | Providence Bay (bad) | Dens-City (good) |
|---|---:|---:|---:|
| Healthcare | 21 | 25 | 8 |
| FireRescue | 18 | 18 | 4 |
| Police | 15 | 20 | 10 |
| **Park** | **533** | **524** | **49** |
| PostService | 169 | 193 | 52 |
| Education | 24 | 37 | 7 |
| EmergencyShelter | 0 | 0 | 0 |
| Welfare | 1 | 1 | 0 |
| **Total** | **781** | **818** | **130** |

The Park input population is 10.88 times the good save in Loon Lake and 10.69
times in Providence Bay. It is not an artifact of counting arbitrary city
entities: every row in the table is a member of the exact coverage-job query,
and every one of the 533/524 Park candidates has the
`Game.Buildings.Park` component. Loon Lake's Park candidates are 524 ordinary
Park buildings, five with `InstalledUpgrade`, and four unique Park buildings.
Providence Bay also includes 132 Park extensions. The PostService candidates
all have `Game.Routes.MailBox` and are also elevated, but only by 3.25 and 3.71
times relative to Dens-City.

There is one important serialization limit. `Game.Pathfind.CoverageElement` is
registered as serializer type `Empty` (value 1), so its dynamic-buffer contents
and lengths are deliberately not present in the save. The pathfinder rebuilds
them after load. `PrepareCoverageJob` creates one 24-byte `BuildingData` record
for each query entity whose rebuilt buffer is nonempty and sets its element
count to that buffer's length. Thus the save proves a roughly 11x Park-provider
input population, but cannot prove the live per-provider path lengths.

Because the broken Apply ordering can approach `O(E * B)`, and `E` is itself
the sum of the `B` providers' rebuilt coverage-buffer lengths, the Park counts
are a strong, narrowly grounded explanation for why both bad saves trigger the
pathology. The ETW trace still cannot distinguish Park from PostService by
enum value. A one-run live counter of `(service, B, E)` would conclusively
separate them; it is not necessary for the algorithmic fix because the
replacement covers all eight services.

## Fix design and equivalence

The replacement retains the original filtering, NativeSort, coverage math,
dependency chain, and disposal chain. Version 0.1.3 changed only the
active-provider ordering data structure from a linearly shifted sorted array to
a binary max heap.

Each provider's source coverage slice is already sorted by the preceding
Process job, so selecting the next first record is a k-way merge. The heap gives
the same selected record sequence in `O(E log B)` ordering work. Exact equal
priorities require a dynamic reinsertion epoch: the original loop places a
just-advanced provider before existing equal providers. The replacement uses
that epoch as its tie key.

Randomized reference-model tests cover 1, 2, 5, 20, and 100 providers, 500
trials each, deliberately generate many exact priority ties, and randomize the
initial concrete tie order. The selected provider/priority sequence is exactly
equal between the original and replacement models.

The post-fix trace justifies a second ordering-only optimization. The preceding
`ProcessCoverageJob` calls `NativeSort` on every individual provider's
`CoverageElement` slice. Its managed IL at `0x0352–0x0375` proves that every
stream consumed by Apply is already descending by `m_AverageCoverage`.
Version 0.1.4 converts the float priority into a sortable unsigned key and uses
a 33-bucket monotone radix queue. The queue stores only record indices in its
links, eliminating the heap's comparison tree and repeated 24-byte record
moves.

The concrete initial tie order still comes from the game's NativeSort. A
reinserted provider is prepended to its priority bucket, exactly reproducing
the original loop's "before existing equals" behavior. Redistribution appends
in current list order so it cannot reverse equal keys. Both signed zero forms
are normalized because the game's comparator considers them equal.

Before any coverage write, v0.1.4 verifies every active provider range is in
descending order and contains no NaN key. An unexpected range uses the proven
v0.1.3 heap for that invocation. Extended property tests compare the original,
heap, and radix record sequences across the prior tied-integer cases plus
random float32 values spanning signs, infinities, subnormals, and both zero
representations. All sequences are exactly equal.

An adversarial two-element case produces:

| Providers | Original shifts | Bytes of 24-byte records moved |
|---:|---:|---:|
| 100 | 9,900 | 237,600 |
| 1,000 | 999,000 | 23,976,000 |
| 5,000 | 24,995,000 | 599,880,000 |

## Confidence classification

### Proven

- The roles and function boundaries of all three requested native RVAs.
- The export-dispatch → AVX2 wrapper → AVX2 body call chain.
- The exact mapping to `ServiceCoverageSystem.ApplyCoverageJob.Execute`.
- The record layouts, hotspot field, managed IL region, math, and shift loop.
- The 256-frame per-service recurrence and eight-service rotation.
- The fact that 88.7% of sampled body PCs are in the reinsertion region.
- The post-fix trace contains zero executions of the original Apply function.
- The remaining ~4.28-second event is the v0.1.3 replacement itself, averaging
  26.428 ms across nine heavy invocations.

### Strong inference

- The visible main-thread hitch is the main thread waiting for this worker job
  or its dependency chain to complete. The timing and repeated worker episode
  align exactly with the disruptions, but no private Unity job name is present
  in the ETW scheduler metadata.
- The v0.1.3 heap removed the observed 74–97 ms disruptions but retained a
  smaller periodic cost, as independently measured in the second trace.
- Park is the pathological service in the supplied saves. Both bad saves have
  524–533 exact Park query candidates versus 49 in the good save; PostService
  is the only material alternative and is much less elevated.

### Live service inputs from v0.1.6

The stable Providence Bay rotation reports:

| Service | Providers | Rebuilt elements |
|---|---:|---:|
| Park | 523 | 1,467,894 |
| PostService | 193 | 139,958 |
| Education | 36 | 134,568 |
| EmergencyShelter | 0 | 0 |
| Welfare | 1 | 9,689 |
| Healthcare | 25 | 94,906 |
| FireRescue | 18 | 119,293 |
| Police | 18 | 112,130 |

This conclusively identifies Park as the heavy service slot. Its provider count
matches the targeted save decode, and its rebuilt element population is more
than ten times every other service.

### Remaining unknowns after v0.1.6

- How many of Park's 1,467,894 elements the Apply merge actually reaches before
  each provider's remaining budget is exhausted.
- The wall time after removing the redundant 1.47-million-element validation
  pass.

The next test is one complete before/after park-deletion diagnostic session plus
a stationary 1x frame-time capture with v0.1.8. Another save comparison is not
needed.

## v0.1.8 repeatable diagnostic controls

Version 0.1.8 removes the startup-only 24-pass diagnostic limit. Coverage
capture is explicitly toggled with `Ctrl+Shift+F9`; every start creates a fresh
numbered session and every stop completes and logs the final pending job. This
permits sequential cities and controlled state changes without restarting the
game.

For the destructive hypothesis test, holding `Ctrl+Shift+F10` for three real
seconds adds the game's `Game.Common.Deleted` marker to all live, non-temporary
entities carrying `Game.Buildings.Park`, `Game.Buildings.ParkMaintenance`, or
`Game.Vehicles.ParkMaintenanceVehicle`. This is intentionally broader than
removing maintenance depots alone: the save analysis proved the 524–533 heavy
providers are the Park entities themselves. The test must be performed on a
disposable copy and not saved over the original city.

## Unpublished four-level byte-radix design study

The two v0.1.4 post-fix traces were reclassified by native instruction region,
using the exact replacement DLL rather than neighboring strings. Across 416
Apply samples, the remaining cost divides as follows:

| Native region | Samples | Share |
|---|---:|---:|
| Queue/refill/pop/reinsert | 304 | 73.1% |
| Coverage pointer/current-value tests | 84 | 20.2% |
| Deep coverage arithmetic | 10 | 2.4% |
| Entry/top-level work | 17 | 4.1% |

This makes another selector redesign substantially higher value than changing
the coverage formula. The latter would also risk changing observable float
results for only a small sampled block.

The unpublished design uses an exact four-level byte-radix queue. A provider
can be redistributed at most once per differing key byte rather than once per bit.
There are 1,025 logical buckets: bucket zero plus 256 buckets for each of four
byte positions. A 17-word occupancy bitmap and 17-bit word directory locate the
next live source in constant time. Cached minima avoid a preliminary linked-list
scan when a bucket is redistributed.

The temporary `BuildingData` and `Elements` lists are disposed immediately
after Apply and no downstream job observes their mutated cursor fields. After
retaining the exact game `NativeSort`, the implementation therefore compacts
each 24-byte building record in place into a 16-byte
`{ NextIndex, EndIndex, Total, Remaining }` state. The reclaimed tail holds one
8-byte `{ Next, Priority }` radix node per provider:

```text
16 * providerCount + 8 * providerCount == 24 * providerCount
```

The forward copy is overlap-safe because the complete 24-byte source record is
loaded before its narrower destination is written. The queue is initialized
only after every state is compacted. This eliminates per-element writes to dead
records, the prior worker-stack provider limit, extra allocation, and the
large-provider heap fallback.

The exact selector microbenchmark uses the two measured heavy sizes
(421/427,319 and 526/510,220), four monotone stream shapes, physical provider
range shuffling, the real 32-byte key stride, full sequence validation, and 41
randomized-order measurement rounds. The tested configuration uses byte radix,
adaptive 4,096/5% batching, 16-byte AoS provider state, and no unavailable
prefetch intrinsic. It is 1.404x faster geometrically than the unpublished 0.2.0 selector,
ranging from 1.165x to 2.013x. Every workload's paired confidence interval
remains above 1.0. The high-interleave cases measure 1.173x and 1.165x; the
vanilla shift-loop pathology is direct evidence that the real Park merge is
strongly interleaved. These figures isolate selector time and are not claimed
as total job or frame-time speedups.

Full same-provider batching is exact but can itself be too expensive on an
interleaved stream. The four-level design always takes the constant-time
equal-key path, then samples the first 4,096 non-equal advances. The broader competitor-minimum
peek stays enabled only at a hit rate of at least 5%. In the benchmark that
disabled it for the two high-interleave streams (59 and 49 hits), while retaining
it for mixed and run-heavy streams (1,961–4,096 hits). This decision changes only
whether a redundant reinsert/immediate-pop pair is executed; selection remains
identical.

Additional exact changes remove the impossible generated null pointer check,
use `EndIndex` rather than decrementing a count, process the sole provider as a
direct serial tail, preserve vanilla's NaN-retaining `!(Remaining <= 0f)` budget
relation, and hoist the next immutable key load before the current random
coverage-pointer work. The original coverage calculation, float operation
order, pointer-write order, service cadence, and dependency chain are retained.

The following options were explicitly rejected:

- winner trees and binary insertion lost native benchmarks or retained a bad
  scaling case;
- node SoA and source-minimum rescans regressed;
- approximate reciprocals, reassociation, FMA, and fast-math change results;
- parallel provider processing is invalid because coverage pointers alias and
  order changes provider budgets;
- reducing frequency, deferring work, or dropping elements changes simulation;
- replacing Process/index sorting is a separate, more invasive patch boundary
  without evidence that it owns the remaining periodic critical path.

The rejected arithmetic paths were also timed in a 510,220-event strict-float
native proxy. A padding-based factor cache regressed to 0.880x geometrically;
the density-one clamp specialization regressed to 0.943x. A scaled-target no-op
won only in deliberately no-op-heavy data and fell to 0.925x when hits were
scarce. Removing saturate for proven-positive lanes projected to only about
0.12% of the whole job under the trace's 2.4% deep-math share. None was robust
enough to add branches and code size to the hot path.

A separate Process-stage prototype was also rejected. Process is already an
`IJobParallelFor`, the ETW data does not isolate it as the residual serial
critical path, and a traced-size 1,111,964-element proxy showed that sorting
4-byte indices was 0.9% to 29.6% slower than direct 32-byte records because of
indirect key and consume reads. Packing records to 24 bytes saved only 1.4% to
8.2% in that isolated proxy while requiring a new Process ABI, pointer tagging,
and a much wider behavioral patch. It remains a separate research boundary,
not an evidence-backed v0.3 optimization.

`BENCHMARKS.md` records the native selector methodology and workload results.
`tests/reference_model.py` covers byte boundaries, arbitrary 32-bit priorities,
dense ties, adaptive batching, in-place overlap, vanilla liveness, and provider
counts beyond the removed cap.

One domain qualification remains. `Game.dll` does not finally clamp authored or
modded coverage magnitude to finite values. A NaN `AverageCoverage` makes the
vanilla comparer itself non-antisymmetric: both `Compare(NaN, x)` and
`Compare(x, NaN)` can return `-1`. Consequently no total integer key can promise
the same undefined NativeSort permutation for hostile NaN prefab data without a
full validation/fallback scan. The four-level design deliberately avoids that
million-element scan and guarantees ordering equivalence for valid finite coverage
inputs. Infinity remains ordered; NaN authored data is the excluded case.

### Guarded cross-version behavior

The exact analyzed `Game.dll` hash remains recorded as the verified baseline,
but a different hash is no longer rejected by itself. The mod continues only
after resolving `ServiceCoverageSystem.ApplyCoverageJob`, validating both
`NativeList` fields and their 24/32-byte element layouts, and replacing exactly
one matching `Schedule<ApplyCoverageJob>` call. A structurally incompatible
build still fails closed and leaves the original job untouched. A structurally
compatible but unverified build displays a visible compatibility warning that
includes its `Game.dll` hash, then enables the replacement normally.

## v0.3.2 published record-local implementation

The cross-record compaction layout described in the unpublished design study
was not published or tested as a 0.3.1 build. The published 0.3.2 implementation
uses the four-level byte-radix selector while deliberately rejecting that
cross-record layout. Instead of compacting all 24-byte records into a contiguous
16-byte state array and placing a separate node array in the reclaimed tail,
every original record is independently overlaid as:

```text
byte  0..7   RadixNode { Next, Priority }
byte  8..23  ProviderState { NextIndex, EndIndex, Total, Remaining }
```

Only `ElementCount` is converted in place to an absolute `EndIndex`; the other
three state fields already occupy their final offsets. Every write remains
inside the same original record. The queue is still allocation-free, uncapped,
and uses the same four-level byte-radix selection, cached minima, occupancy
directory, exact tie behavior, and adaptive winner batching. Coverage writes,
floating-point arithmetic, and serial ordering remain unchanged.

This is the record-local layout shipped in the published 0.3.2 build. The
reference suite verifies its structural bounds and exact provider-selection
sequence, and the release was subjected to extended in-game testing before
publication.
