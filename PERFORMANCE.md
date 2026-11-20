# Service Coverage Stutter Fix 0.3.5

**Historical 0.3.5 report.** The subsequent pass found that the game’s Options writer omitted the diagnostic values described below. Version 0.3.6 corrects the binding. [Updated verification](PERFORMANCE-0.3.6.md)

The reference build is 0.3.4 at `88ed617d84ccf0eab656292bf0a2b96712a6af67`. That build intercepted Apply in the simulation. A second caller in the tool and infoview preview continued to schedule the game's original Apply worker. Both callers run the replacement in 0.3.5 on the two reviewed game builds. Each retains its original job dependency, service slot, update cadence and disposal chain. The mod adds no save fields or serialized components.

## What the final worker costs

These are median elapsed milliseconds for the complete Apply worker on generated inputs. All three workers were compiled with Unity Burst 1.8.23 and executed on the same Linux x64 host. Each of 24 timed rounds runs all three workers, balancing all six possible orders. The last column reports the median of the paired 0.3.4 / 0.3.5 ratios. Ratios above one favor 0.3.5. On preview, 0.3.4 used the vanilla column.

| Target and workload | Providers | Vanilla | 0.3.4 simulation | 0.3.5 | Paired speedup over 0.3.4 simulation |
|---|---:|---:|---:|---:|---:|
| AVX2, budget-limited parks | 526 | 380.289 | 26.422 | 23.467 | 1.144× |
| SSE4, same budget-limited parks | 526 | 392.855 | 28.796 | 26.105 | 1.123× |
| AVX2, dense ties | 526 | 6.304 | 2.835 | 2.557 | 1.108× |
| AVX2, fully interleaved streams | 526 | 378.419 | 15.163 | 15.947 | 0.942× |
| SSE4, twice the park providers | 1,052 | 1,543.734 | 44.807 | 40.082 | 1.142× |
| SSE4, light service | 12 | 0.02298 | 0.01986 | 0.01696 | 1.174× |
| SSE4, single provider | 1 | 0.000648 | 0.000600 | 0.000624 | 0.962× |

The budget-limited workload reproduces the scale of a recorded park update. Of 1,111,964 usable elements, 510,220 were visited before budgets ran out. The candidate's four workload counts matched the expected values. Unique destinations on this fixture allow an independent count from the vanilla output. Other fixtures deliberately share destinations. The reconstructed paths and priorities are synthetic.

The new merge keeps the next provider outside the queue while one provider stays ahead. This reduces queue operations when one provider can visit several elements in a row. A separate SSE4 matrix varied overlap, ties, provider count and stream length across 72 cases. All 16 park-sized cases improved, with paired median speedups of 1.055–1.118×. Several small tie-heavy cases and the separately generated fully interleaved case above favored 0.3.4. The implementation keeps vanilla's initial sort, concrete ordering of ties and coverage arithmetic. The available evidence supports the tradeoff for heavy park workloads. Frame-time improvement in an individual city remains unmeasured for 0.3.5.

[Three-worker summaries](evidence/native-summary.csv) · [Individual samples](evidence/native-samples.csv) · [72-case matrix, methods and raw results](evidence/apply-shapes/README.md)

## Where the simulation waits

Nine historical ETW captures were available. Two contain the published 0.3.2 worker, whose coverage body underlies 0.3.4. The longest sampled same-worker Apply spans there were 2.00 and 2.67 ms. Both showed replacement execution and zero sampled vanilla Apply calls. No recording identified an exact 0.3.4 runtime on the later reported city. For an exact baseline on common inputs, the table above runs the native worker built directly from 0.3.4.

The park infoview capture contained 91 heavy episodes of the original Apply worker. It readied the waiting main thread in 90 of them. Median Apply spanned 77.99 ms, Process spanned 3.31 ms, and the main thread resumed a median 0.57 ms after the last Apply sample. The capture lacks caller and frame-presentation markers. The two game assemblies independently establish the missed preview call. The evidence identifies a way the old mod could leave a visible hitch and does not assign a cause to the other player's report.

| Stage | Observation and decision |
|---|---|
| Pathfinding and result conversion | On one mod-enabled capture, graph actions had 37,812 CPU samples and conversion had 6,232. Much of this work can run ahead of Apply. The capture does not tie it to the observed main-thread wait. |
| Prepare | Builds provider ranges and reserves the combined list. It had zero matched leaves in the stock simulation capture and three in the park infoview capture. Kept vanilla. |
| Clear | Resets the service slot on queried edges, including edges that lost coverage. The same captures had five and twelve matched leaves. Kept vanilla. |
| Process | Waits on Prepare and Clear, builds and sorts each provider's accepted range in parallel. Median spans across heavy captures were 3.19–5.25 ms. Four sort experiments produced input-dependent regressions. One fast path lost 13–15% with a single late duplicate. Kept vanilla. |
| Apply | Serially merges provider ranges into shared coverage targets. Changed provider selection and covered the second scheduling site. Scheduling, temporary-list disposal and downstream consumers remain under game control. |

Across four real saves the park candidate counts were 533, 524, 49 and 1,154. The city with 49 had the largest population. Saved candidates establish scale only. The game rebuilds path buffers after loading, and each update can visit a different fraction before budgets run out. None of these saves was identified as the later port-stutter report.

[Trace attribution and sample definitions](evidence/trace-profile/TRACE_PROFILE.md) · [Offline save counts](evidence/trace-profile/SAVE_COUNTS.md) · [Process experiments](tests/process_sort/REPORT.md)

## Correctness and lifecycle

- A direct comparison against the game's private stock worker passed 3,072 generated cases per tested configuration, plus tied-priority cases with 8,193 and 65,537 providers. It compares destination bytes, immutable elements and buffer guards. Current Linux SSE4 and AVX2 and historical Linux SSE4 ran native. Both supplied game assemblies also passed managed execution. The native regression for subnormal values passed all four combinations of DAZ and FTZ on both instruction sets.
- On each game build, an independent managed check compared the complete interleaved history of 2,039,931 element visits and 253,466 actual destination stores across 1,551 cases. Every visited index, destination and pair of float bits matched vanilla, including the known NaN case, equal priorities, aliased targets, overlapping ranges, occluded elements and exhausted budgets. One heavy case visited 510,220 elements sharing a single destination and executed just 14 stores.
- Both actual scheduling methods passed direct instruction checks. The original Prepare service ID, Apply input and output handles, other calls and disposal order were preserved. A failure during replacement submission schedules vanilla once, even if reporting also throws. No partial worker is replayed.
- Two exact game hashes have a fast startup path. Other builds undergo normalized checks of job methods, ordering, arithmetic and layouts. The preview caller has its own compatibility check. Isolated lifecycle tests exercised duplicate load, partial startup, all 16 teardown fault combinations, pending jobs, retryable cleanup, city-load history reset and stale completions. They also distinguished a completed empty update from a service that has never run.

The final independently compiled managed test assembly has SHA-256 `ec0f1babf3bda04a71bb81808858112c25816111605ebb48a9628b2b5a2b48ce`. Both Windows x64 Burst targets compiled and linked with their expected exports. The official Windows mod postprocessor and a running Unity player were unavailable. A candidate FPS number, a successful deployed load and a safe park-count ceiling cannot be inferred from these checks.

[Native reproduction](tests/burst_compare/README.md) · [Visit/write proof and game hashes](tests/visit_order/README.md) · [Actual scheduling-site checks](tests/game_contract) · [Compatibility checks](tests/coverage_contract) · [Lifecycle checks](tests/runtime_harness)

## Reading Diagnostics

Open **Options → Service Coverage Stutter Fix → Diagnostics**. The normal simulation and the tool/infoview preview have independent execution states and per-service workload rows. **Burst execution observed** with an increasing completed count confirms a live optimized job ran. Startup probes are excluded. Managed executions and fallbacks have separate counters. Service rows show the most recent completed replacement pass since the last completed game load. Reopen the panel to refresh a cached view.

| Field | Meaning for the Park row |
|---|---|
| Providers | Park providers that entered Apply with a usable range and a live budget |
| Available | Sum of the usable element counts for those providers |
| Processed | Elements actually visited, including visits that found the target already covered |
| Reserved | Total entries in the prepared input list before Apply filtered providers |

Counts reuse the game's ranges and each provider's retirement point. Reading the panel neither queries city entities nor waits for pending jobs. There are no sampling timers in the live mod. The expensive traces and benchmarks are separate, explicitly invoked tools.

With 72 paired measurements on the budget-limited AVX2 fixture, the median worker duration was **22.61 ms** with the workload write omitted and **22.69 ms** with it enabled. The median paired on/off ratio was 0.998. This comparison resolved no consistent worker overhead. The control removed only publication of the four workload fields, allowing Burst to eliminate unused count work while preserving execution counters. A separate managed adapter measurement against the earlier execution-only diagnostics found a median incremental cost of **17.2 ns** per empty submission, worker and completed snapshot combined. It excludes Unity scheduling and menu rendering.

The optional live observation requires no special city changes. During normal play, open the Park coverage infoview briefly and reopen Diagnostics. A preview completion and a populated Park row verify execution and attribution in that installation. Establishing the source of a remaining frame hitch would require a matched trace from that session.

[Paired overhead samples](evidence/counter-ab-samples.csv) · [Summary](evidence/counter-ab-summary.csv) · [Managed adapter measurements](evidence/managed-counter-overhead.txt)
