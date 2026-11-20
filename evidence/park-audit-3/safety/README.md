# Independent lifecycle and memory review

The reviewed production baseline is 0.3.6 at `6c4c7f4`. Its offline rebuilt DLL has SHA-256 `b8c8a7d6494839bda0445af05098d94c1d2841c3fa107dd45368993284748a6d`. No reviewed change adds a persistent component or save-format migration. The game’s ordinary road coverage can persist.

## Storage and removal

The following metadata and IL findings were independently identical on game SHA-256 prefixes `721e7e17`, `f076df4b`, and `aaee15c4`.

| Observation | Evidence and consequence |
|---|---|
| An edge's coverage buffer contains service values | `Game.Net.ServiceCoverage` implements `IBufferElementData` and `ISerializable`. Its sole field is a float2. The generic Serialize/Deserialize bodies directly write/read it. |
| Stock edge initialization sizes the buffer to nine | `Game.Net.InitializeSystem.InitializeEdgesJob` calls ResizeUninitialized(9). The type has InternalBufferCapacity(0). Distinct roads can point at distinct allocations. |
| Older or differently sized buffers require a length check | `Game.Serialization.ServiceCoverageSystem.ServiceCoverageJob` appends default elements when length is below nine. Equal or longer lengths pass through. |
| Park simulation and preview have separate target slots | Simulation uses the selected service, Park = 3. Preview Clear and Process set index 8. Its initialization path copies the selected service into slot 8 while awaiting searches. |
| Producer pointers refer to particular target elements | Process takes AddressOf(buffer.ElementAt(index)). Different work items can resolve to the same target. Cached target values, reordered stores and assumed physical adjacency would need separate proof. |
| Unloading removes the patch and diagnostic allocations | The mod contains no save serializer. The game can retain an existing coverage value across save/load and recalculate it later. This review included no game launch or live save round trip. |

The original dependency enters the replacement Apply. Both callers schedule their native-list disposal after it. The bridge copies list headers and borrows their storage. The worker reuses each private 24-byte building record and never assumes ownership of the backing lists. All target writes occur inside Apply. Mod load, release and city-switch handling affect diagnostics. A completed city load advances the diagnostic submission cutoff so a late result from the prior city cannot reappear in the UI.

The managed scheduling bridge retries the original job with its original dependency when replacement submission fails synchronously. After successful submission, a worker fault cannot be replayed safely through that bridge. Teardown disables additional optimized submissions and completes outstanding handles before releasing counters. A completion failure retains the allocations for cleanup retry.

## Independent checks

- Reran the production-linked lifecycle harness, including duplicate load, pending jobs, city switches, failed scheduling, preview fallback and all 16 injected teardown-failure combinations. The fixture uses a simulated scheduler. The real Harmony call-emission harness also passed for the simulation and preview methods on the current assembly.
- Generated 396 fresh cases with seed families across 12 provider counts through 1,154. Inputs include overlapping ranges, repeated destinations, empty/short budgets, signed zeros, NaN payloads, infinities, subnormals and nonmonotone priorities. The injected managed observers recorded the original stock element index, destination and both stored float bit patterns on every visit/store. Each tested run matched all 504,234 visits and 46,744 stores, final bytes and buffer guards. The three baseline fixture hashes and individually tested candidate hashes are in [exact-order.csv](exact-order.csv).
- The current 0.3.6 native worker matched stock destination bytes, input bytes and guards on both Linux x64 targets. All four workload counters matched the independent managed stock oracle. In the inverse-cutoff and two-competitor comparisons, native stock, 0.3.6 and candidate output matched in each floating-point mode. Actual visit counts vary by mode on these deliberately exceptional inputs. [Native check results](native-checks.csv)
- A separate Linux protected-page test put the last byte of each array and simulated container header immediately before an inaccessible page. Exhausted tails held a target address on an inaccessible page. Stock, 0.3.6 and both the inverse and two-competitor candidates completed all 40 cases on both targets. This checks memory reads and writes that would cross those boundaries. It does not observe hardware prefetch or speculative microarchitectural effects.
- Replacing a strict priority test in the private inverse fixture with an inclusive test caused the holdout to fail at event 22 in `alias-edge-18`. Stock visited element 92 and the mutation visited element 27. This sensitivity control never entered the production source.
- The inverse change reconstructs the runner’s cutoff from the popped key. Zero encodings that compare equal can reconstruct to positive zero. The comparison uses the same floating-point mode throughout a job. It removes an element read and introduces no additional target read. The mechanical checks passed. Shipping also requires repeatable benefit on representative overlapping-road inputs.
- Offline findings cover the reviewed code and valid fixture lifetimes. The fingerprint verifies the managed image on disk. Other mods can change the same execution paths after loading. Directly mutated game methods, invalid pointers, concurrent writes or persistent references to the temporary provider list were outside these tests.

## Two-competitor candidate

The reviewed managed candidate has SHA-256 `9181b7e563c576e53d328320780772ccc93bd70b61eec4b7f84461946c924eb5`. The only source difference is the large merge loop in `OptimizedApplyCoverageJob.cs`, containing source SHA-256 `bd5a8d618e73297c661bfa45c87b4950b4d3cb55a672f2455b6285e3326f56f4`.

The new loop holds the second and third provider outside the remaining queue. While the old winner loses to the second but beats or ties the third, it swaps the front two and retains the third. A tie must promote the most recently advanced provider ahead of the waiting third. A head strictly below the third re-enters the queue at or beyond its popped key. A last pair returns to the existing single-provider tail after one provider exhausts its entries or its budget. A NaN initial head selects the original ordered merge before any coverage visit. A later NaN head keeps the current provider under the original unordered comparison. The new early read is the queued third provider's in-range `AverageCoverage`. The code does not follow that provider's target pointer until its turn.

All 396 independent managed fixtures matched stock on each of the three game assemblies. For a second targeted suite 17 live providers forced the large path. Three leading providers had six interior positions, each independently chosen from −1, 0, +1 and NaN. Six head-order and budget configurations produced 24,576 cases. All 540,672 visits and 379,904 interleaved target stores matched the real stock managed worker, including destination and both float bit patterns. Both native targets also matched stock output bytes, input elements and guards on this corpus. All four native counters matched the independent default-mode oracle. A private mutation changing the test against the third provider from strict to inclusive failed at event 10 of the first fixture group, where stock selected element 4 and the mutation selected element 1.

The independent safety review clears these worker mechanics on the supplied builds and tested inputs. The shipping decision also depends on representative worker timings. The candidate would preserve the 0.3.6 simulation/preview job sites, dependencies, counter ownership, city-switch cutoff and save schema.

## Replay

The script needs Python, a C compiler, .NET 8, a game Managed directory, a compiled mod and matching native worker directories. Build the workers using [the native harness instructions](../../../tests/burst_compare/README.md). Run once per CPU target. For a baseline-only check, pass the 0.3.6 managed DLL and repeat its worker directory for both mod arguments.

```sh
python3 evidence/park-audit-3/safety/reproduce.py \
  "$GAME_MANAGED" "$CANDIDATE_DLL" "$STOCK_WORKER" "$V036_WORKER" "$CANDIDATE_WORKER"
```

Add `--frontier` to select the 24,576-case pair-specific corpus.

The script creates private temporary copies of the repository tests and runs stock and mod workers offline. Each protected-page test runs in a separate process. All test sources stay outside the production compile include.
