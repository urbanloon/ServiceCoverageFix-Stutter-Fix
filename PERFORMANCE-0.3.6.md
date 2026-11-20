# Service Coverage Stutter Fix 0.3.6

This pass starts at 0.3.5, commit `0920e76e1329e792db1dc0c114893cd52a7e4f67`. Both the simulation and preview were already optimized in that build. The three-worker timings below compare 0.3.5, 0.3.6 and vanilla on the same input. The candidate changes how Apply selects the next provider and how Options presents diagnostics. The game still decides when each service updates, computes the same coverage values and owns its save data.

## Apply worker results

Median elapsed milliseconds on the current game's Linux AVX2 worker. Each shape has 24 rounds containing all three workers, with all six execution orders balanced. The speedup column is the median of the paired 0.3.5 / 0.3.6 time ratios, which can differ slightly from dividing the separate medians.

| Generated workload | Vanilla | 0.3.5 | 0.3.6 | Paired speedup |
|---|---:|---:|---:|---:|
| 526 park providers, budgets exhausted | 407.148 | 24.765 | 22.548 | 1.087× |
| 526 park providers, fully interleaved priorities | 379.839 | 16.100 | 14.588 | 1.092× |
| 526 park providers, dense ties | 6.280 | 2.618 | 2.671 | 1.008× |
| 1,052 providers, 1,020,440 elements | 1,573.356 | 40.108 | 36.897 | 1.098× |
| 12 providers, 768 elements | 0.02023 | 0.01405 | 0.01032 | 1.375× |

The budget fixture had 1,111,964 usable elements. Each of the 526 providers exhausted its budget after 970 visits, for 510,220 processed elements. On SSE4, the paired speedups were 1.101× for this workload and 1.056× for the fully interleaved case. Dense ties at this scale showed no consistent gain. The inputs use measured city-scale counts and generated paths. Timings exclude scheduling, rendering and frame presentation.

Two production changes earned their place.

- **Small merges.** For one provider the worker keeps its position in a local variable. For 2–16 providers it keeps the competing priorities in a fixed 128-byte queue, preserving vanilla's ordering of equally ranked providers. On the 8-provider, 256-element tie fixture the paired speedup was 1.38× on SSE4 and 1.77× on AVX2. A longer 16-provider, 8,192-element interleaved case regressed to 0.88× on SSE4 and improved to 1.10× on AVX2. Larger cutoffs performed poorly in the threshold sweep, so the small queue ends at 16. [Small-merge trials](evidence/small-apply/README.md)
- **Large merges.** Burst had retained an out-of-line call in the loop that chooses the next provider. Inlining that lookup removed the call on both native targets. Across three separate comparisons, the 526-provider interleaved workload improved 1.082–1.101× over 0.3.5. Thirty-three alternatives were screened. Several widened buckets or reorganized the queue but slowed other heavy workloads. The shipping change keeps the existing large-queue algorithm. [Screening and confirmation](evidence/large-apply/README.md)

An independently generated holdout used the four saved Park candidate counts of 49, 524, 533 and 1,154. Across four priority distributions at each size the final paired medians ranged from 0.967× to 1.110×. A saved candidate might have an empty live path after loading. The count of participating providers and the amount visited can differ on each update. [Integrated native results](evidence/final-native/three-worker-summary.csv) · [Holdout results](evidence/final-native/holdout-shape-summary.csv) · [Saved candidate counts](evidence/trace-profile/SAVE_COUNTS.md)

## Diagnostics and lifecycle

0.3.5 collected the counts correctly, but the earlier test stopped before the game's Options widget serialized a value. The game's multiline widget reads the display name, so those fields could send just the static label. We reproduced the failure with 0.3.5. Five corrected fields now use the read-only value accessor. The two workload sections use the multiline widget's dynamic display-name callback. The actual widget factory, update and JSON writer returned all seven values and refreshed existing widgets on three game builds.

**Options → Service Coverage Stutter Fix → Diagnostics** shows separate simulation and tool/infoview preview execution states. A live completed Burst job is distinguishable from a managed execution or a vanilla fallback. The Park row's **providers** entered Apply with a usable range and budget. **Available** is the total usable length of those ranges. **Processed** includes every visited element, even if its target was already covered. **Reserved** is the size of the original prepared element list. Rows show the last completed pass for that service and reset after a city load.

A read uses existing counters, without a city scan or a forced completion. Formatting changed rows takes place outside the scheduler lock. A test paused formatting while scheduling another pass and completing a load, then checked that the old row could not overwrite the new history. A recognized incompatible game now keeps an inactive panel with its reason. A missing optional load-event accessor no longer prevents teardown and a later reload. Exact game job and Unity container types are checked before substituting a worker.

| Isolated managed diagnostics, one slot | 0.3.5 | 0.3.6 |
|---|---:|---:|
| Read all seven unchanged getters | 373 ns, 792 bytes allocated | 133 ns, 0 bytes |
| Complete a changed pass and read the panel | 5.07 μs | 1.10 μs |
| Check steady Burst state for logging | 30 ns, 72 bytes | 14 ns, 0 bytes |

Native counter overhead was measured separately on the budget-limited AVX2 job. Across 72 pairs the worker medians were 22.39 ms with publication omitted and 22.13 ms with it enabled. The median paired on/off ratio was 0.991. These repetitions resolved no consistent worker penalty. Expensive profiling is invoked through the offline tools. [Widget tests, managed measurement design and raw observations](evidence/diagnostics/README.md) · [Native evidence](evidence/final-native)

## Pipeline and timing

The source-level chain on the reviewed builds is Prepare and Clear, followed by parallel Process, then serial Apply. The game disposes the temporary lists after their respective consumers. The second pass ran the actual Harmony 2.2.2 reader, transpiler dispatcher and emitter on both real scheduling methods on all three game assemblies. A separate metadata reader found exactly the expected bridge call in the emitted method. The surrounding-call and dependency tests also passed. The normal service rotation and tool/infoview preview still schedule the same number of jobs at the original sites.

- **Prepare and Clear.** The stock and park-infoview captures had zero and three matched Prepare samples, and five and twelve Clear samples. Prepare already resizes the combined list once. Clear must reset edges that lost coverage. Both remain vanilla.
- **Process.** Existing heavy captures show median spans of 3.19–5.25 ms. The previous experiments with the game's native comparator produced regressions on input orders the game can generate. The new scheduler analysis supplied no justification for replacing it. [Pipeline measurements](evidence/trace-profile/TRACE_PROFILE.md) · [Process alternatives](tests/process_sort/REPORT.md)
- **Apply.** In the historical park-infoview recording the vanilla Apply worker readied the waiting main thread in 90 of 91 heavy episodes. A second replay found the same event-time UnityPlayer caller in all 90. Two other archived captures show the published 0.3.2 replacement executing. Neither sampled replacement Apply directly on the main thread. Those captures supply no individual 0.3.5 frame times.
- **Other jobs.** In C09 the main thread itself spent 61 consecutive samples spanning 60.00 ms executing ordinary route search, without a context switch. Its longest corresponding coverage-graph span was 7.01 ms. Resource-seller setup was associated with many worker-to-main wakeups, but release captures lack the event-time stack needed to attribute any particular wake to a precise dependency. These findings explain why the same visible symptom can survive a coverage speedup. The later reported city's own cause remains unverified. [Scheduler findings and raw summaries](evidence/release-critical-path/REPORT.md)

## Verification and build

Independent comparisons against each of the three real game assemblies matched the full interleaved order of **2,669,403 visits and 262,133 destination stores across 2,750 cases per assembly**, including the two float bit patterns written each time. Fixtures include shared targets, equal priorities, exhausted budgets, initial and later NaNs, subnormals, unordered streams, and the 4/5 and 16/17 provider boundaries. The native workload oracle independently derives visits from the stock worker, then checks all four diagnostic counters. A separate native differential passed 3,072 generated inputs plus tied workloads with 8,193 and 65,537 active providers. The current and original game builds ran on Linux SSE4 and AVX2. The additional September build ran on SSE4. The x64 floating-point tests include all four DAZ/FTZ modes. [Native checks](evidence/final-native/checks.csv) · [Third-build exact order](evidence/september-build/visit-order.log) · [Third-build native oracle](evidence/september-build/native-sse4.log)

The managed verifier produced SHA-256 `b8c8a7d6494839bda0445af05098d94c1d2841c3fa107dd45368993284748a6d`. Windows x64 SSE4 and AVX2 Burst workers compiled and linked. The official Windows mod postprocessor and live rendering were outside this offline run. Open `ServiceCoverageFix.sln` with the Cities: Skylines II mod toolchain and rebuild **Release**. The project includes only the six production C# files in its assembly. [Reproduction commands](tests/burst_compare/README.md) · [Build and worker identities](evidence/final-native/worker-hashes.csv)

No dedicated playtest is requested. During normal play, opening the Park coverage infoview and checking the preview's completed counter and Park row is enough to confirm execution on that installation. The offline data cannot establish a personal park-count ceiling or identify the source of a future frame hitch.
