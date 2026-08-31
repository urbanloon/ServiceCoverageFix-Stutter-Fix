# Service Coverage Stutter Fix

Service Coverage Stutter Fix is a Cities: Skylines II code mod that removes a
verified periodic CPU bottleneck from
`Game.Simulation.ServiceCoverageSystem.ApplyCoverageJob`.

The original game repeatedly moves 24-byte provider records through a sorted
array while combining service-coverage paths. In cities with hundreds of
providers and hundreds of thousands of processed coverage elements, that
ordering work can approach `O(E * B)` and create a severe rhythmic hitch.

The mod changes only the data structure used to select the next provider. It
does not remove, approximate, delay, or reduce service-coverage calculations.

## Public version history

The public release history is **0.1.8 to 0.3.2**.

- **0.1.8** was the first published source build. It replaced the vanilla
  sorted-array reinsertion loop with an exact 33-bucket monotone radix queue and
  included temporary diagnostic controls. The original source is preserved on
  the [`archive-v0.1.8`](https://github.com/urbanloon/ServiceCoverageFix-Stutter-Fix/tree/archive-v0.1.8/ServiceCoverageFix)
  branch and in commit
  [`95121ee`](https://github.com/urbanloon/ServiceCoverageFix-Stutter-Fix/tree/95121ee/ServiceCoverageFix).
  The older `v0.1.8` tag predates the source upload and is not the authoritative
  source snapshot.
- **0.3.2** is the current published build. It replaces the 33-bucket selector
  with a four-level byte-radix selector, removes the diagnostic controls, and
  uses a record-local temporary-memory layout.

There was no published or tested 0.3.1 build. Earlier repository documents used
0.3.1 as an internal development label for a design draft. Those references did
not represent a public release and have been corrected.

## The original bottleneck

Each service provider produces an individually ordered path of coverage
elements. The vanilla Apply job combines those paths using this effective loop:

```text
take the first provider
process one coverage element
find where the provider belongs now
shift every intervening 24-byte provider record
insert the provider
repeat
```

With `B` active providers and `E` processed elements, the ordering work can
approach `O(E * B)` record moves.

The Providence Bay Park pass contained:

```text
523 Park providers
1,467,894 available CoverageElements
approximately 457,000 to 478,000 processed elements per update
```

The Loon Lake Park pass contained 526 providers, approximately 1.11 million
available elements, and approximately 507,000 to 512,000 processed elements per
update.

ETW profiling identified the exact native Burst job. Of 834 sampled stacks
inside its AVX2 body, 740, or 88.7%, landed specifically in the provider
reinsertion and 24-byte record-shifting region.

## What 0.1.8 changed

Version 0.1.8 left the surrounding ServiceCoverageSystem jobs intact and
replaced only the sorted-array merge with a 33-bucket monotone radix queue.

Each provider's coverage path is already ordered by the preceding Process job.
As a provider advances through its path, its next sortable priority moves in
only one direction. The queue uses that monotone property to reproduce the
vanilla selection sequence without continuously maintaining one shifted array.

The 0.1.8 queue selected a bucket from the highest bit that differed from the
last processed priority:

```csharp
uint difference = priority ^ lastPriority;
return difference == 0 ? 0 : 32 - math.lzcnt(difference);
```

This removed the catastrophic vanilla record shifting. However, a provider
could still be redistributed up to 32 times, bucket headers were searched for
the next source, and a source bucket was walked to discover its minimum key.

## What 0.3.2 changes

Version 0.3.2 retains the same exact monotone merge and makes the selector
itself more efficient:

- Four byte levels process the complete 32-bit priority one byte at a time. A
  provider can be redistributed at most four times instead of up to 32.
- A two-level occupancy bitmap jumps directly to the next populated bucket.
- Cached bucket minima remove a separate source-bucket scan.
- Equal-priority winner runs are processed without redundant queue operations.
- Broader repeated-winner batching is retained only when a 4,096-advance sample
  shows that it is beneficial.
- A sole remaining provider is consumed directly.
- `EndIndex` removes the per-element count decrement.
- The next immutable priority is loaded before the current random coverage
  pointer, exposing independent memory work to the CPU.
- Each original 24-byte temporary record independently stores its own 8-byte
  queue node and 16-byte provider state. No write crosses into another provider
  record and no additional native allocation is required.
- Runtime coverage logging and the destructive park-removal test are removed
  from the published gameplay build.

The 0.3.2 merge effectively performs:

```text
take the next provider from the radix queue
process its next coverage element
continue directly while that provider remains the exact winner
otherwise reinsert it using the four priority bytes
jump directly to the next occupied bucket
repeat
```

## Behavior preserved

The replacement processes the same providers and coverage elements while
preserving highest-coverage-first processing, equal-priority behavior, the
original floating-point calculations, serial write order, update frequency,
budgets, and stopping conditions.

The game still runs its original Prepare, Clear, and Process jobs. The mod does
not add anything to the save file, and removing it restores the original
vanilla implementation.

No approximate reciprocal, float reassociation, parallel coverage write,
update skipping, or reduced simulation frequency is used.

## Validation and measured results

The reference suite validates the complete provider-selection sequence, not
only the final average result. It covers 4,374 ordering cases, 1,038 randomized
scheduler trials, finite floating-point edge cases, dense ties, byte boundaries,
and stress inputs containing up to 65,537 providers.

The native development benchmark in `BENCHMARKS.md` measures the selector in
isolation. Its four-level byte-radix design measured 1.165x to 2.013x faster
than an unpublished compact 33-bucket development baseline across eight
traced-size workloads, with a 1.404x geometric mean. It is not a direct
benchmark of the published 0.1.8 binary or total in-game frame time.

In the same Loon Lake 1x test scene, the measured in-game result was:

| Build | Average FPS | 1% low | 0.1% low |
|---|---:|---:|---:|
| Without the mod | 43.8 | 24.5 | 9.1 |
| Version 0.3.2 | 52.8 | 46.8 | 43.5 |

The largest observed improvement was frame-time consistency. Results depend on
the city, workload, hardware, and other active mods. Cities without unusually
large service-coverage workloads may experience only a small improvement.

## Compatibility and failure behavior

Version 0.3.2 was verified against `Game.dll` SHA-256:

`721e7e17bf74299aa2b988c1bd07e90874bb8bc72d263229500c4bf639e7e4ee`

The mod validates the private job name, both `NativeList` fields, their exact
element layouts, and the unique schedule call. An unverified game hash produces
a visible compatibility warning and continues only when those structural checks
still pass. A structurally incompatible build fails closed without replacing
the vanilla job. If the managed bridge fails for an individual update, that
update schedules the original game job.

The radix ordering assumes finite `AverageCoverage`, which is what valid base
game coverage assets produce. A modded or corrupt prefab with a NaN coverage
magnitude is outside this guarantee because vanilla's own comparator does not
define a reproducible total order for that input.

Harmony 2.2.2 is packaged privately.

## Build and test

1. Open `ServiceCoverageFix.sln` in the Visual Studio installation where the
   official Cities: Skylines II mod template builds.
2. Select **Release** and choose **Build > Rebuild Solution**.
3. Confirm `1 succeeded, 0 failed` and this load message:

   `Enabled ServiceCoverageFix 0.3.2.0 record-local byte-radix hardened build`

4. Run `python tests/reference_model.py`.
5. Complete an extended in-game simulation test before publishing a changed
   build.

`ANALYSIS.md` contains the native attribution, trace evidence, algorithm
development record, and confidence classification. `BENCHMARKS.md` records the
native selector development benchmark.
