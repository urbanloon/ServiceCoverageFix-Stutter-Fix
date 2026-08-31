# Service Coverage Fix 0.3.2 — Hardened Performance Test

This Cities: Skylines II code mod replaces the pathological final merge in
`Game.Simulation.ServiceCoverageSystem.ApplyCoverageJob`. The original game
repeatedly shifts 24-byte provider records one position at a time while merging
hundreds of sorted service-coverage paths. In cities with hundreds of coverage
providers and hundreds of thousands of processed elements, that work approaches
`O(E * B)` and causes the deterministic simulation-clock hitch.

The game still runs its original Prepare, Clear, and Process jobs. This mod
changes only the data structure used to select the next coverage element. It
retains the game's ordering, equal-priority behavior, serial coverage writes,
budget arithmetic, and stop conditions.

## What 0.3.2 changes

Version 0.3.2 retains the exact high-performance selector introduced in 0.3.1
and hardens its temporary-memory representation after an unattributed native
crash was observed during extended testing. The available crash report contained
no faulting module, address, managed stack, Windows event, or dump, so it does
not establish that the mod caused the crash. This revision nevertheless removes
the one unusually aggressive memory transformation as a precaution.

The selector redesign is based on the two post-fix ETW traces.
Those traces attribute 73.1% of the v0.1.4 replacement samples to queue/refill/
reinsert work, 20.2% to coverage-pointer tests, and only 2.4% to deep coverage
math. The new implementation therefore attacks the selector rather than using
result-changing fast math:

- a four-level byte-radix queue gives every provider at most four
  redistributions instead of the 33-bucket queue's theoretical 32;
- a two-level occupancy bitmap finds the next nonempty bucket in constant time;
- cached bucket minima avoid a separate source-list scan;
- equal-priority winner runs always remove redundant reinsert/pop pairs, while
  broader winner peeks are retained only when a 4,096-advance sample reaches a
  5% hit rate, avoiding their overhead on highly interleaved workloads;
- a sole remaining provider is consumed directly without further priority work;
- each disposable 24-byte game record is reinterpreted independently as its own
  eight-byte queue node plus 16-byte provider state; no write crosses from one
  provider record into another;
- `EndIndex` removes the per-element count decrement;
- the next immutable stream key is loaded before the current random coverage
  pointer, exposing independent memory work to the CPU;
- occluded elements skip a redundant budget comparison;
- the proven-dead null-pointer check is removed;
- diagnostics and the destructive park-removal hotkey are removed from this
  production-performance flavor, including their managed scheduling overhead.

There is no stack-size threshold, allocation, or binary-heap fallback. The
8+16-byte record-local state and queue reuse exactly the temporary allocation
that the game disposes immediately after Apply.

No approximate reciprocal, float reassociation, parallel coverage write, update
skipping, or reduced simulation frequency is used.

## What changed from 0.1.8

Version 0.1.8 already removed the catastrophic vanilla record-shifting loop,
but its selector still used a 33-bucket, one-bit-at-a-time radix queue. It could
redistribute a provider up to 32 times, scanned for the next nonempty bucket,
walked a source bucket to discover its minimum, and continued mutating full
24-byte temporary records on every processed element.

Version 0.3.2 uses four byte levels and cached minima. A
provider is redistributed at most four times, while a two-level occupancy
bitmap locates the next bucket directly. It also:

- overlays every disposable 24-byte provider record with its own eight-byte
  radix node and 16-byte hot state, without cross-record compaction;
- removes per-element record copies, count decrements, diagnostic counters, and
  writes to temporary fields that no later system reads;
- processes equal-key runs and the final provider without redundant queue work;
- adaptively retains broader winner batching only when it is actually useful;
- localizes Apply inputs before the random coverage-pointer write and loads the
  mandatory next key early;
- removes the impossible generated null-pointer branch while preserving the
  original coverage calculations and serial write order;
- removes the logging-control and destructive park-removal systems from the
  gameplay build.

In the exact implemented-configuration native benchmark, the new selector was
1.165x to 2.013x faster than the 33-bucket selector across eight traced-size
workloads, with a 1.404x geometric mean. These are selector-only results, not a
claim of the same percentage increase in total FPS or complete-job time.

## Compatibility and failure behavior

This build was verified against `Game.dll` SHA-256:

`721e7e17bf74299aa2b988c1bd07e90874bb8bc72d263229500c4bf639e7e4ee`

It validates the private job name, both `NativeList` layouts, their exact
element sizes, and the unique schedule call. A different hash is allowed only
when those structural checks still pass, and produces a visible compatibility
warning at startup. If the relevant game structures or scheduling point change,
the mod fails closed without replacing the game's job. If the managed bridge
ever fails for one update, that update schedules the original game job.

The radix ordering assumes finite `AverageCoverage`, which is what valid base
game coverage assets produce. A modded or corrupt prefab with a NaN coverage
magnitude is outside this guarantee; vanilla's own comparator is inconsistent
for NaN and does not define a reproducible total order for that input.

Harmony 2.2.2 is packaged privately. The old DependencyPack subscription is not
required.

## Build and test

1. Open `ServiceCoverageFix.sln` in the Visual Studio installation where the
   official Cities: Skylines II mod template builds.
2. Select **Release** and choose **Build → Rebuild Solution**.
3. Confirm `1 succeeded, 0 failed` and this load message:

   `Enabled ServiceCoverageFix 0.3.2.0 record-local byte-radix hardened build`

4. Test Loon Lake or Providence Bay stationary at 1× for at least 45 seconds.
5. Do not publish this over a stable build until its Release/Burst build and an
   extended live test have been verified.

`ANALYSIS.md` records the native attribution and trace evidence.
`tests/reference_model.py` validates the exact selector sequence and structural
invariants.
