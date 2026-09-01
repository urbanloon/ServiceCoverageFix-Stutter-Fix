# Service Coverage Stutter Fix

Service Coverage Stutter Fix is a Cities: Skylines II code mod that removes a
verified periodic CPU bottleneck from
`Game.Simulation.ServiceCoverageSystem.ApplyCoverageJob`.

The original game repeatedly moves 24-byte provider records through a sorted
array while combining service-coverage paths. In cities with hundreds of
providers and hundreds of thousands of processed coverage elements, that
ordering work can approach `O(E * B)` record moves and create a severe rhythmic
hitch.

The mod replaces the provider-selection and queue-maintenance mechanism while
retaining the coverage calculation and surrounding job schedule. It does not
remove, approximate, delay, parallelize, or reduce service-coverage
calculations.

## Technical index

| Resource | Contents |
|---|---|
| This README | End-to-end problem model, investigation path, algorithm, invariants, patch boundary, and evidence limits |
| [`ANALYSIS.md`](ANALYSIS.md) | Native addresses, ETW stack reconstruction, disassembly attribution, development experiments, and confidence classification |
| [`BENCHMARKS.md`](BENCHMARKS.md) | Native selector-only benchmark methodology, results, and rejected candidates |
| [`src/OptimizedApplyCoverageJob.cs`](src/OptimizedApplyCoverageJob.cs) | Published 0.3.2 record-local byte-radix implementation |
| [`src/ServiceCoverageSystemPatch.cs`](src/ServiceCoverageSystemPatch.cs) | Harmony schedule-call replacement and managed fallback bridge |
| [`src/Mod.cs`](src/Mod.cs) | Load, unload, hash warning, and structural compatibility gate |
| [`tests/reference_model.py`](tests/reference_model.py) | Executable reference models, ordering properties, layout proof, and stress tests |
| [`archive-v0.1.8`](https://github.com/urbanloon/ServiceCoverageFix-Stutter-Fix/tree/archive-v0.1.8/ServiceCoverageFix) | Exact original 0.1.8 source snapshot |

The repository contains the source, analysis record, benchmark results, and
reference-model tests. It does not include the supplied `.cok` test saves,
Colossal Order's `Game.dll`, the game's generated Burst binary, the original ETL
captures, raw frame-time captures, native benchmark raw timings, or the native
development benchmark harness. Measurements derived from those artifacts are
reported here and in the analysis documents, but the underlying artifacts are
not archived in this repository.

## Public version history

The public source progression is **0.1.8 to 0.3.2**.

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

No build bearing version 0.3.1 was published or tested in game. Constituent
design ideas were evaluated in unpublished model and benchmark prototypes before
the record-local 0.3.2 build was assembled and tested. Earlier numbered builds
and labels in `ANALYSIS.md`, including 0.1.3 through 0.1.7, 0.1.9, 0.2.0, and
0.3.1, identify local investigation or design stages unless explicitly marked
otherwise. They are not additional public releases.

## Problem model: an exact k-way merge

The preceding Process stage gives each service provider an individually
ordered range of `CoverageElement` records. Apply must merge the current heads
of those ranges into one serial processing sequence.

For provider `i`, let its transformed priority stream be:

```text
k[i, 0] <= k[i, 1] <= ... <= k[i, n]
```

Smaller transformed keys represent stronger `AverageCoverage`. Apply repeatedly
selects the globally smallest current key, applies that coverage element, and
advances only the selected provider. This is a k-way merge over `B` monotone
streams, not a shortest-path calculation and not Dijkstra's algorithm.

The important shared property with monotone priority-queue applications is that
once a key has been extracted, a later valid key cannot be smaller. That is the
property used by the radix queue.

### Exact float-to-key mapping

The game orders provider heads by descending `AverageCoverage`. Version 0.3.2
maps the raw IEEE-754 binary32 representation to an ascending unsigned key with
the following code:

```csharp
uint bits = math.asuint(coverage);
if ((bits & 0x80000000u) == 0)
{
    return bits ^ 0x7FFFFFFFu;
}

return (bits & 0x7FFFFFFFu) == 0
    ? 0x7FFFFFFFu
    : bits;
```

This is the bitwise inverse of the standard ascending float-flip transform,
followed by normalization of negative zero to the same key as positive zero.
For reference, `+infinity` maps to `0x007FFFFF`, both zero representations map
to `0x7FFFFFFF`, and `-infinity` maps to `0xFF800000`.

For every finite float, numerically greater coverage maps to a smaller key.
Positive and negative zero both map to `0x7FFFFFFF`, preserving their equality.
Positive and negative infinity remain ordered. No float arithmetic is performed
by this transform; it operates only on the original bits.

An `AverageCoverage` NaN is a deliberately stated domain exclusion. Vanilla's comparer is itself
non-antisymmetric for NaN because both `Compare(NaN, x)` and `Compare(x, NaN)`
can return `-1`. There is no reproducible total integer order that can promise
the same undefined `NativeSort` permutation for hostile NaN prefab data without
adding a full validation and fallback pass. The float-to-key ordering guarantee
therefore excludes only NaN `AverageCoverage`; it covers every non-NaN binary32
value, including signed zero and both infinities. Valid base-game coverage
magnitudes are expected to be finite.

### Equivalence domain

The behavioral-equivalence claims apply to the verified game's normal upstream
invariants: every provider range is in bounds and ordered, every processed
element has a non-null `CoveragePtr` that is valid as a `float2*` and targets
storage outside both temporary list allocations, and the temporary records have
the verified field layout. The published job trusts those invariants and does not
retain the generated native null-pointer branch that valid
`ProcessCoverageJob` output cannot take. Corrupt temporary data, malformed
modded coverage inputs that violate these invariants, or an unknown build with
changed internal field offsets are outside the guarantee.

## The vanilla algorithm and its scaling failure

The vanilla Apply job effectively performs:

```text
filter inactive providers
sort provider records by their current head element

while providers remain:
    take the first provider
    apply one coverage element
    advance that provider
    if its range or budget is exhausted:
        remove its first record
    otherwise:
        scan forward to find its new position
        shift every intervening 24-byte provider record left by one slot
        insert the advanced provider
```

With `B` active providers and `E` processed coverage elements, the ordering
maintenance can approach `O(E * B)` 24-byte record moves. The `O(E)` coverage
calculation is necessary. The repeated movement of up to `B` provider records
after nearly every element is not.

Observed Park workload data during this investigation was:

| Save | Providers | Available elements | Processed elements per update |
|---|---:|---:|---:|
| Providence Bay | 523 | 1,467,894 | approximately 457,000 to 478,000 |
| Loon Lake | 526 | approximately 1.11 million | approximately 507,000 to 512,000 |
| Dens-City | 49 saved Park query candidates; live `B` not captured | live buffer not persisted or captured | live processed count not captured |

This is why population alone did not predict the failure. The live provider
count and the rebuilt lengths of their coverage paths determine available `E`;
coverage writes and provider budgets then determine how much of `E` is actually
processed. Dens-City's saved candidate count is an indicator, not a captured
live Apply count.

## How the bottleneck was established

The diagnosis followed the runtime evidence back to the managed job rather than
starting from a guessed system name.

1. **Establish the cadence.** Stationary 1x captures showed 74 to 97 ms stalls
   approximately every 4.4 seconds. At 3x simulation speed, the corresponding
   cadence contracted to approximately 1.47 seconds, tying the event to
   simulation work rather than camera or rendering movement.
2. **Recover native addresses from ETW.** StackWalk payloads were parsed against
   the trace's module image base to produce PE-relative virtual addresses in the
   analyzed vanilla `lib_burst_generated.dll`, SHA-256
   `cb38360072a9a7fed7b06b0b745b0a0300dee60f4fd284117a16a4fd8dd2190d`.
   These RVAs are specific to that binary.
3. **Classify the native call chain.** PE `.pdata`, export dispatch, wrapper
   calls, and disassembly connected the sampled AVX2 body at the half-open range
   `[0xA7C160, 0xA7C55B)` to its wrapper return at `0xA7B3F6` and export-stub
   return at `0x35C0FB7`.
4. **Map native work to managed semantics.** The corresponding managed
   `Game.dll` job established the 24-byte `BuildingData` layout, 32-byte
   `CoverageElement` layout, comparator, budget relation, and coverage formula.
5. **Identify the exact loop.** Of 834 sampled stacks inside the AVX2 body, 740,
   or 88.7%, landed in `[0xA7C440, 0xA7C4F5)`, the provider reinsertion and
   24-byte record-shifting region. The most sampled instruction was the shifted
   record's `m_ElementIndex` store at `0xA7C491`, with 368 samples.
6. **Prove the replacement was active.** The local v0.1.3 binary-heap ETW trace
   contained zero stacks in the original Apply body, wrapper, or export-stub
   regions. The remaining periodic samples resolved inside the replacement DLL
   itself.
7. **Measure before redesigning again.** Across nine heavy v0.1.3 replacement
   invocations, the binary heap removed the quadratic shifts but still had a
   mean sampled execution span of 26.428 ms. Two local v0.1.4 33-bucket radix
   traces, each containing eight heavy invocations, reduced the Loon Lake and
   Providence Bay mean sampled spans to 20.92 ms and 20.45 ms respectively.
8. **Capture the real input at the correct dependency point.** An initial
   managed observation ran before Prepare completed and reported zero-length
   lists. Instrumentation was moved to the beginning of the Burst job, after
   the dependency, which recovered the actual provider and element counts.
9. **Reclassify the residual cost.** Across 416 samples from the two v0.1.4
   radix traces, 304 samples, or 73.1%, remained in queue, refill, pop, and
   reinsert work. Coverage pointer/current-value tests accounted for 84 samples,
   or 20.2%; deep coverage arithmetic for 10, or 2.4%; and entry/top-level work
   for 17, or 4.1%. Those four published region counts account for 415 samples;
   one sample is not represented in the summarized regions. The result justified
   another selector redesign and argued against changing the coverage formula.

The full address derivation, episode timing, module hashes, and development
trace tables are recorded in [`ANALYSIS.md`](ANALYSIS.md).

## Why a monotone radix queue

A binary heap changes the avoidable ordering work from linear record shifts to
`O(log B)` comparisons and 24-byte heap movements for almost every processed
element. That was a large improvement, but the post-fix trace showed that it was
still too expensive at approximately half a million selections per Park pass.

A monotone radix queue fits this workload more closely:

- provider streams are already ordered;
- extracted transformed keys never decrease;
- priorities are fixed-width 32-bit values;
- only the current head of each provider is live in the queue;
- exact tie order matters;
- the merge must remain serial because different elements can point to the same
  coverage storage and provider budgets depend on write order.

For fixed 32-bit keys, each queued key can cross at most four byte bands before
extraction. Queue-management work is therefore bounded by a constant number of
bucket moves per processed key instead of as many as `B` record moves per key.
This does not remove the necessary linear pass over the `E` coverage elements.

Version 0.1.8 used a conventional 33-bucket monotone radix queue. Version 0.3.2
keeps the same monotone merge but groups key differences by byte rather than by
individual bit.

## The 0.1.8 33-bucket queue

The 0.1.8 queue classified a key by the highest bit that differed from the last
extracted key:

```csharp
uint difference = priority ^ lastPriority;
return difference == 0 ? 0 : 32 - math.lzcnt(difference);
```

Bucket zero held keys equal to `lastPriority`; the other 32 buckets represented
the highest differing bit. This removed the vanilla shift loop, but a queued key
could still be redistributed through as many as 32 bit levels. The
implementation also searched bucket headers for the next source and walked a
source bucket once to discover its minimum before walking it again to
redistribute its nodes.

## The 0.3.2 four-level byte-radix queue

### Bucket mapping

Version 0.3.2 uses one exact bucket plus four 256-bucket byte bands, for 1,025
logical buckets in total:

```csharp
uint difference = priority ^ lastPriority;
if (difference == 0)
{
    return 0;
}

int shift = (31 - math.lzcnt(difference)) & ~7;
return 1 + (shift << 5) +
    (int)((priority >> shift) & 0xFFu);
```

`shift` is `0`, `8`, `16`, or `24`, identifying the most significant byte in
which the key differs from `lastPriority`. `(shift << 5)` selects that byte's
256-bucket band and the selected priority byte chooses the bucket within it.
After a source refill, each surviving key moves to a lower differing-byte band.
A queued key can therefore be redistributed at most four times before
extraction rather than up to 32 times.

### Occupancy and cached minima

Three 1,025-entry tables store bucket heads, tails, and cached minimum keys.
Seventeen 64-bit occupancy words cover all 1,025 buckets. A `uint` directory
uses its low 17 bits to record which occupancy words are nonempty.

Two trailing-zero counts locate the lowest live bucket without a 1,025-bucket
scan: one selects the first live occupancy word and the other selects the first
live bit inside that word. A cached minimum supplies the new `lastPriority`
without a separate minimum-discovery traversal.

When bucket zero is empty, the queue:

```text
locates the lowest occupied source bucket
sets lastPriority to that bucket's cached minimum
removes the source chain
redistributes each node against the new lastPriority
resumes extraction from bucket zero
```

Redistribution appends nodes, preserving the existing concrete order among
nodes that land in the same target chain.

### Main merge loop

The published job performs:

```text
filter inactive providers using vanilla's conditions
run NativeList.Sort with the equivalent vanilla comparator expression
overlay each disposable 24-byte record with queue node plus provider state
enqueue providers in reverse sorted order by prepending

while the queue is not empty:
    refill bucket zero when necessary
    pop the first provider from bucket zero
    apply its current element with vanilla's arithmetic and write order
    stop that provider if its range or budget is exhausted
    continue directly if no competitor remains
    continue directly if its next key equals lastPriority
    optionally prove that vanilla would still select it before the minimum competitor
    otherwise prepend it into the byte-radix bucket for its next key
```

The queue changes how the next provider is found. `ApplyElement` retains the
game's original coverage comparison, factor calculation, clamp, pointer write,
ratio calculation, remaining-budget update, and floating-point operation order.

### Correctness invariants and complexity

For non-NaN `AverageCoverage`, the queue maintains these invariants:

1. `lastPriority` is a monotone lower bound installed by the most recent
   bucket-zero refill. Every queued key, and the temporarily selected provider's
   next key, is greater than or equal to that lower bound. During direct winner
   batching, the stored variable may lag the latest processed key.
2. Relative to the same `lastPriority`, a key in a lower differing-byte band
   precedes every key in a higher band. Within one band, a lower byte digit
   precedes a higher digit.
3. The lowest occupied bucket therefore contains the global minimum live key.
   Its cached minimum becomes the next `lastPriority`; redistribution against
   that value moves every exact match into bucket zero.
4. Reverse-prepend initialization, append redistribution, and prepend
   reinsertion preserve the concrete tie rules described below.
5. If the selected key is `x`, every untouched provider head is at least `x`
   and the selected provider's successor is also at least `x`. The next global
   selection therefore cannot be smaller than `x`, even when batching leaves
   the stored `lastPriority` at an earlier lower bound.
6. The selected coverage element is applied before that provider's next key is
   reintroduced, so the serial observable operation sequence remains unchanged.

The inactive-record filter remains `O(B)`. The initial comparison sort remains
`O(B log B)`. After sorting, each queued 32-bit head key can be redistributed at
most four times, so byte-radix selector work is amortized `O(4E)`, or `O(E)` for
fixed-width keys, plus `O(B)` initialization. This replaces vanilla's worst-case
`O(E * B)` record shifting; it does not remove Apply's necessary `O(E)` coverage
work.

### Exact tie behavior

Matching only the final average coverage is insufficient because equal-priority
provider order can change pointer writes and budgets. Version 0.3.2 preserves
the post-sort merge sequence as follows:

- The replacement calls `NativeList.Sort` over the same aliased 24-byte records
  with a reimplemented comparer containing the same equality and descending
  `AverageCoverage` expression as vanilla. It does not assume sort stability.
- Initial providers are traversed in reverse and prepended, reconstructing the
  concrete order produced by that sort inside queue chains.
- Bucket redistribution appends, retaining the source chain's order.
- An advanced provider is prepended on reinsertion, matching vanilla's placement
  before existing equal-priority providers.
- When bucket zero still contains a competitor at `lastPriority`, a temporarily
  removed provider with a different next priority cannot remain the winner.
  When bucket zero is empty, `priority <= competitorMinimum` is sufficient for
  it to remain the exact next winner because equality also places that advanced
  provider first under vanilla's reinsertion rule.

The reference suite compares every selected `(provider, element)` pair after a
concrete initial order has been supplied, not only the final numeric result. It
proves that the radix merge preserves vanilla reinsertion and tie behavior from
that point forward. It does not execute Unity's compiled sort or independently
prove the concrete permutation that `NativeList.Sort` chooses among initial
equal keys.

### Exact adaptive winner batching

Sending a provider through the queue only to select it again at its next winner
opportunity is algebraically redundant when it remains the winner. Version
0.3.2 always takes the constant-time `priority == lastPriority` path. For
non-equal advances, a live bucket-zero competitor rejects the broad winner check
immediately. Only when bucket zero is empty does the check compare the
provider's next key with the cached minimum of the lowest nonzero bucket.

The first 4,096 non-equal advances for which at least one competitor remains
form a fixed probe window within each Apply invocation. The broader check starts
enabled. If a full window is reached, it remains enabled when at least 5% of the
probes succeed and is disabled otherwise. With integer counts, retaining it
requires at least 205 hits. If the invocation has fewer than 4,096 qualifying
advances, no policy decision is reached and the check stays enabled throughout
that invocation. Disabling it changes only whether the redundant queue round
trip before that provider's next selection is elided. It cannot change which
provider wins or the output sequence.

### Other hot-loop decisions

- A one-provider input bypasses the radix tables and consumes that provider's
  range directly. During a multi-provider merge, the selected provider also
  continues directly whenever its removal leaves no competitor in the queue.
- `ElementCount` is converted once to an absolute `EndIndex`, replacing a
  per-element remaining-count decrement with an index comparison.
- The next element's immutable `AverageCoverage` is loaded before following the
  current element's random `CoveragePtr`. This is intended to expose independent
  memory work to the CPU without relying on a Burst prefetch intrinsic.
- `ApplyElement` copies provider and element inputs to locals before dereferencing
  the opaque coverage target. The coverage target can alias another element's
  target, but it cannot alias either temporary list allocation. This is intended
  to let the compiler avoid conservative reloads across the observable pointer
  write without claiming that coverage targets are mutually non-aliasing.
- The common path where the target already has equal or stronger coverage does
  not modify `Remaining`; only the arithmetic path performs the subsequent
  budget test.

### Record-local memory layout

The original temporary `BuildingData` record is 24 bytes:

| Original bytes | Original field | 0.3.2 use |
|---:|---|---|
| `0..7` | `Entity` | `RadixNode { Next, Priority }` |
| `8..11` | `ElementIndex` | `ProviderState.NextIndex` |
| `12..15` | `ElementCount` | `ProviderState.EndIndex` after adding `NextIndex` |
| `16..19` | `Total` | `ProviderState.Total` |
| `20..23` | `Remaining` | `ProviderState.Remaining` |

The Entity value is no longer needed at this stage. Both temporary lists are
disposed after Apply and no downstream job reads the mutated provider records.
Every node and state write remains inside its own original record, so no write
crosses a provider boundary and there is no provider-count-dependent scratch
array.

The queue performs no allocator-backed or heap allocation. When the byte-radix
path runs, its explicit `stackalloc` array payload is exactly:

```text
3 * 1,025 * 4 bytes + 17 * 8 bytes = 12,436 bytes
```

That is approximately 12.1 KiB of explicit temporary worker-stack payload,
independent of provider count. The complete compiled Burst stack frame may be
larger because alignment, spills, and other locals are compiler-dependent.
Unlike the unpublished 0.2.0 stack-node experiment, the published record-local
layout has no 8,192-provider cap. It has been stress-modeled through 65,537
providers.

## Patch boundary and runtime data flow

The mod replaces a scheduling call rather than patching arbitrary native code:

1. `CompatibilityGate.Validate()` resolves `ServiceCoverageSystem` and its
   nested `ApplyCoverageJob`, records the loaded `Game.dll` hash, and checks the
   two required `NativeList` fields.
2. A Harmony transpiler targets `ServiceCoverageSystem.OnUpdate` and replaces
   exactly one generic `IJobExtensions.Schedule<ApplyCoverageJob>` call. Zero or
   multiple matches abort loading and remove the patch.
3. The generic bridge caches `DynamicMethod` accessors that emit `ldflda` for
   the two named job fields. This locates the `NativeList` fields without
   hard-coding their offsets inside `ApplyCoverageJob`. `MemCpy` then copies only
   each `NativeList` header into a typed, non-owning view. It does not copy the
   provider or element buffers; both views refer to the game's existing
   temporary allocations, and the game remains their owner and disposer.
4. The optimized job is scheduled with the original dependency `JobHandle`.
   Prepare, Clear, Process, disposal, service cadence, and surrounding dependency
   topology remain the game's own.
5. If the bridge throws before returning the optimized `JobHandle`, including a
   synchronous exception from `Schedule(optimizedJob, dependency)`, it logs once
   and invokes `Schedule(originalJob, dependency)`. This fallback cannot catch
   a failure during later Burst execution after the optimized handle has been
   returned.
6. Unloading the mod removes its Harmony patches. Because no save data is added,
   the next update uses the original schedule path.

## Behavior preserved

On the verified `Game.dll` and for non-NaN `AverageCoverage`, the published
replacement preserves:

- the same active-provider filter: `ElementCount == 0 || Remaining <= 0f`;
- the same provider ranges and coverage elements;
- an equivalent reimplementation of the initial comparator expression and the
  same highest-coverage-first selection;
- the same post-sort equal-priority reinsertion and processing sequence for a
  given concrete initial sorted order;
- the same coverage-pointer write order;
- the same floating-point operations and their order;
- the same `!(Remaining <= 0f)` post-update liveness relation;
- the same provider budgets and stopping conditions;
- the same update frequency and job dependency;
- the same serial merge semantics.

At source and Burst-configuration level, it does not intentionally introduce
approximate reciprocals, expression reassociation, explicit FMA or `math.mad`
rewrites, or `FloatMode.Fast`. Final machine-instruction selection remains a
Burst compiler decision. It also does not introduce parallel coverage writes,
deferred elements, update skipping, or a reduced simulation frequency.

## Alternatives evaluated and rejected

| Candidate | Reason rejected |
|---|---|
| Binary heap | Removed `O(E * B)` shifts but retained a comparison and 24-byte heap-movement cost for nearly every event; the heavy replacement invocation still averaged 26.428 ms |
| Winner tree | Measured 0.699x geometric mean against the same unpublished development baseline used in `BENCHMARKS.md` |
| Binary insertion with 4-byte moves | Retained the poor scaling case and measured as low as 0.543x |
| 33-bucket source-minimum rescans | Required an extra linked-list traversal and measured up to 9% worse |
| Always-on broad winner peek | Regressed on highly interleaved streams, which motivated the exact adaptive policy |
| Node metadata as structure-of-arrays | Measured 0.986x geometric mean against the same unpublished development baseline; the selected candidate used 8-byte AoS nodes |
| Approximate reciprocal, reassociation, FMA, or fast math | Changes floating-point results or operation order |
| Parallel provider processing | Invalid because `CoveragePtr` targets can alias and earlier writes affect later provider budgets |
| Lower update frequency, deferred work, or dropped elements | Changes simulation behavior rather than optimizing the merge |
| Replacing the Process-stage sort | The stage is already parallel, was not isolated as the residual critical path, and a separate 1,111,964-element proxy measured index sorting 0.9% to 29.6% slower |
| Cross-record compaction prototype | Used in the design harness but replaced before publication by the easier-to-audit record-local 8+16-byte overlay |

Arithmetic experiments were also rejected on evidence. In a separate
510,220-event strict-float Apply proxy, a factor cache measured 0.880x
geometrically and a density-one branch measured 0.943x. Deep coverage arithmetic
represented only 2.4% of the classified post-fix ETW samples, so adding
workload-sensitive branches there was not justified.

## Validation methodology

### Executable reference model

`tests/reference_model.py` independently models the vanilla linear reinsertion
sequence and the four-level byte-radix scheduler. It includes:

- 4,374 exhaustive small-domain tie and ordering cases;
- 1,038 randomized scheduler trials using 1, 2, 5, 20, 100, and 526 providers;
- all four byte boundaries and every possible byte digit;
- 10,000 randomized occupancy-directory cases;
- exact priority-transform comparison over sign/exponent/mantissa edges and
  1,000,000 randomized binary32 patterns;
- 250,000 randomized finite-float ordering pairs;
- dense ties, arbitrary 32-bit priorities, signed zero, infinity, and vanilla
  NaN `Remaining` liveness behavior;
- adaptive-probe enable and disable paths;
- byte-level proof of the record-local 8+16-byte overlay;
- adversarial record-shift counts;
- provider stress cases at 8,193 and 65,537 providers.

These tests validate the mathematical scheduler, priority transform, and memory
layout model. They do not execute the compiled C# Burst binary. ETW resolved the
original game body and the local v0.1.3 and v0.1.4 replacement binaries. The
published 0.3.2 evidence consists of a successful template build, extended
in-game simulation, the reference suite, and the reported frame-time capture;
the repository does not claim a final-binary 0.3.2 ETW equivalence trace.

### Selector-only native development benchmark

The native harness modeled 421 providers with 427,319 processed elements and
526 providers with 510,220 processed elements. At each size it tested
high-interleave, dense-tie, mixed-overlap, and long-run streams with physical
provider-range shuffling, the real 32-byte key stride, full sequence validation,
and 41 randomized-order measurement rounds.

The selected byte-radix design measured 1.165x to 2.013x faster than the
unpublished compact 0.2.0 33-bucket development selector across eight workloads,
with a 1.404x geometric mean. That baseline retained the 0.1.8 selection
algorithm but included later compact-layout experiments, so it is not the public
0.1.8 binary. `BENCHMARKS.md` reports that every paired confidence interval
remained above 1.0, but the repository does not include the confidence level,
interval construction, timer and warmup details, machine/compiler description,
raw samples, or harness needed to reproduce that statistical statement.

Those figures isolate selector time. The harness used a compact prototype layout
rather than the final record-local layout, so they are not presented as a direct
benchmark of either published binary or as total frame-time speedups. Full
methodology and workload results are in [`BENCHMARKS.md`](BENCHMARKS.md).

### In-game result

The following values were recorded in the same Loon Lake 1x test scene:

| Build | Average FPS | 1% low | 0.1% low |
|---|---:|---:|---:|
| Without the mod | 43.8 | 24.5 | 9.1 |
| Version 0.3.2 | 52.8 | 46.8 | 43.5 |

The largest observed improvement was frame-time consistency. Results depend on
the city, service-coverage workload, hardware, and other active mods. Cities
without unusually large service-coverage workloads may receive only a small
improvement.

## Compatibility and failure behavior

Version 0.3.2 was verified against `Game.dll` SHA-256:

`721e7e17bf74299aa2b988c1bd07e90874bb8bc72d263229500c4bf639e7e4ee`

For a different hash, the current startup path verifies that:

- `Game.Simulation.ServiceCoverageSystem` exists;
- its nested `ApplyCoverageJob` exists with the expected full name;
- `m_BuildingData` and `m_Elements` are `NativeList<T>` fields;
- their element type names remain `BuildingData` and `CoverageElement`;
- their element types remain 24 and 32 bytes respectively;
- exactly one matching `Schedule<ApplyCoverageJob>` call is replaced.

These are defensive structural checks, not proof that an unknown future build
is semantically compatible. The current gate does not reflect over every
internal element field type or offset. A different game hash therefore produces
a visible compatibility warning even when the listed checks pass. If a listed
check fails, the mod fails closed during loading and removes its patch.

If the managed bridge cannot construct the typed views for one update, it logs
once and schedules the original game job for that update. Removing the mod
restores the original implementation because the mod adds no save data.

Lib.Harmony 2.2.2 is declared with `PrivateAssets="all"`, making it a
non-transitive NuGet dependency, and the build copies `0Harmony.dll` beside the
mod. Its notice is retained in
[`THIRD_PARTY_NOTICES.txt`](THIRD_PARTY_NOTICES.txt).

## Build and test

1. Open `ServiceCoverageFix.sln` in the Visual Studio installation where the
   official Cities: Skylines II mod template builds.
2. Select **Release** and choose **Build > Rebuild Solution**.
3. Confirm `1 succeeded, 0 failed` and that the load message begins with:

   `Enabled ServiceCoverageFix 0.3.2.0 record-local byte-radix hardened build`

4. Run:

   ```text
   python tests/reference_model.py
   ```

5. Complete an extended in-game simulation test before publishing a changed
   build.

For performance changes, repeat the same-scene capture and resolve samples to
the exact replacement binary before changing another hot path. The next change
should be justified by a new trace, not by the presence of an optimization that
is theoretically possible.
