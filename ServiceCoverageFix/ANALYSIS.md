# Cities: Skylines II periodic-stutter investigation

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
