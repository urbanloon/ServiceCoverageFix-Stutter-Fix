# Park-heavy service coverage audit, November 19

**Release decision.** Service Coverage Stutter Fix 0.3.6 is the verified build for this pass. Shared-road Apply has measurable room for improvement. The strongest prototype ran the full Apply worker 1.09–1.16× as fast on several overlapping workloads, then lost performance on finite-budget workloads. The available recordings do not identify the job responsible for a hitch in a city running 0.3.6.

## Which build ran

Two supplied session reports, dated November 17 and 18, both loaded **0.3.3.0**. The November 18 launch at 09:13 local preceded the 0.3.6 source commit at 13:16 that day. Version 0.3.3 patched simulation Apply only. Opening the Park coverage infoview could still run the game's original preview Apply. In the historical Park-infoview capture, vanilla Apply readied the waiting game entry thread in **90 of 91** heavy episodes. Its median sampled span was 77.99 ms. Version 0.3.6 covers simulation and preview separately. The two startup logs do not record which view the player had open when a hitch occurred. [Session verification](handoff/session-provenance.csv) · [Preview trace](../trace-profile/TRACE_PROFILE.md)

## What can hold up a park-heavy city

Four optimized historical captures contain 41 heavy simulation Apply episodes. The main thread spent 439.69 ms waiting during 673.23 ms of sampled Apply spans. Of 328 overlapping waits, the Apply thread directly readied the main thread three times, each more than 3 ms after the final Apply sample. A long Apply sample alone is insufficient to assign the frame stall to Apply.

A separate transition was repeatable. Car navigation first appeared a median of **0.589 ms** after the last Apply sample in those 41 episodes. In 29 episodes the Apply worker directly readied 51 waiting workers whose first sample after resuming was car navigation. Navigation schedules its own ECS and spatial-data dependencies. The trace cannot identify which Unity handle released the work. Those captures lack matched frame presentation and use older mod builds.

A separate published 0.3.2 capture recorded the game entry thread running ordinary route search for **60 ms** in one uninterrupted stretch. A city can therefore have substantial main-thread work outside service coverage. None of these observations attributes a 0.3.6 hitch in the user's current city to car navigation, route search or Apply. [Scheduler replay](handoff/REPORT.md) · [Published-build route measurements](../release-critical-path/REPORT.md)

## Apply with shared road targets

The park-heavy Loon Lake save contains **533 park candidates**, **6,128 road edges with coverage buffers**, and **4,380 edges with a nonzero saved Park value**. The City 6 P11 save has 2,279 eligible road edges, zero Park candidates and zero nonzero Park slots. The game saves a float pair per coverage slot. Neither save preserves the live path list needed to replay an update exactly. [Read-only measurements](saved-roads.csv) · [Save inspector](inspect_road_buffers.py)

The benchmark combines the 6,128-road scale with a separate recorded count of 526 participating providers. Each generated provider has 2,114 distinct destination roads. Providers can share those roads. The independent stock oracle counted 1,111,964 visits and 18,926 stores in one local workload. Its tight-budget case visited just 1,576 elements from the same available count. The result depends on which roads the providers share, in what order they reach them and when their budgets run out. [Fixture and replay](../../tests/park_overlap/README.md) · [Native verification](native/NATIVE-VERIFICATION.md)

The most promising prototype kept the two leading providers available while the worker applied coverage. It used the same serial writes and arithmetic. Measured from 36–48 paired rounds, the table gives baseline CPU time divided by prototype CPU time. Above 1 favors the prototype. Each timing covers the full worker with diagnostics enabled.

| Generated workload | Packed AVX2 | Scattered AVX2 | Scattered SSE4 |
|---|---:|---:|---:|
| Locally overlapping roads | 1.127× | 1.113× | 1.094× |
| Citywide overlap | 1.135× | 1.128× | 1.155× |
| Half the providers on tight budgets | 1.084× | 1.114× | 1.101× |
| Equal priorities | 1.036× | 1.072× | 1.056× |
| All providers on tight budgets | 1.087× | 1.030× | 1.001× |

An independent fresh seed also favored the prototype on the local and citywide workloads, by 1.155× and 1.133×. The earlier finite-budget fixture routes 510,220 visited elements to distinct destinations. On that fixture the same prototype returned **0.910× on AVX2 and 0.934× on SSE4** across 32 balanced pairs each. At 0.910× the prototype takes about **10% more time**. Allowing the two providers to bypass queue maintenance explained fewer than 0.2% of winner switches on the shared-road fixture. The measured improvement cannot be assigned entirely to fewer queue operations. Worker timings measure CPU on generated inputs. They do not measure frame time. [Paired road results](overlap/summary.csv) · [Selector alternatives and finite-budget holdouts](../pass3-large-apply/README.md)

A second prototype applied the new selector only when a bounded sample of path entries contained repeated road addresses. It improved a local-overlap test by 1.100× on AVX2 and 1.157× on SSE4. With early budget exhaustion it returned 0.936× and 0.905×. The safety reviewer could switch its decision in either direction by changing only addresses the stock worker never visited. Its output still matched, but this selector added work and could choose the slower implementation. Neither prototype enters the release. [Independent branch-control experiment](safety/gate-control/README.md) · [Conditional selector timings](native/gated-overlap-summary.csv) · [Classifier implementation and controls](overlap-gate/README.md)

For a direct example against stock, an independent 18-round test of local overlap measured 673.93 ms for vanilla, 68.93 ms for 0.3.6 and 59.73 ms for the prototype on AVX2. These are separate median worker times on the same machine and input. [Three-worker measurements](native/pair-overlap-summary.csv)

## Changes rejected elsewhere in the pipeline

| Stage | Evidence and decision |
|---|---|
| Prepare and Clear | Prior captures recorded 0 and 3 Prepare samples, and 5 and 12 Clear samples, in the stock-simulation and Park-preview recordings respectively. Prepare sizes the list once. Clear must reset edges that lost coverage. Keep both original jobs and their scheduling. |
| Process | The older sort benchmark used the pointer overload. This pass bound the exact NativeArray overload called by the stock Process worker. Unique-key radix reached 4.005× on one shuffled fixture, fell to 0.802× with one late duplicate and 0.533× on descending input. The measured 533 parks had no assigned service districts. A shortcut for district lookup would select none. An ideal precomputed length factor including the stock sort gave 0.989–1.043× before cache construction. Keep the original worker. |
| Coverage pathfinding and result conversion | The actual native result-conversion worker was timed on 36 shapes per ISA. Both alternatives slowed 10–14 shapes on AVX2 and 11–13 on SSE4. Conversion had zero samples during the 41 historical heavy Apply spans. Coverage graph work appeared in 30. Keep the original search, conversion and preview dependencies. |
| Diagnostics | The existing panel reports worker starts/completions, Burst or managed execution and separate per-service provider, available, processed and reserved counts for simulation and preview. The same empty native worker spent an additional 11.4 ns on SSE4 and 13.7 ns on AVX2 publishing diagnostic counters. The nonempty paired timing distributions spanned 1.0. Opening Options uses the existing completion publication. The offline tools add no city scan or runtime profiler. |

[Corrected sort benchmark](../../tests/process_sort/array-baseline/README.md) · [Range construction](../../tests/process_sort/range/RESULTS.md) · [Conversion timings](conversion/REPORT.md) · [Counter cost](native/counter-cost.csv)

## Save safety, verification and build

The independent safety reviewer verified that road coverage values persist in vanilla saves. A wrong value could survive removing the mod. The offline verifier compares each observable store's destination, ordering and both float bit patterns. The retained worker matched 2,669,403 visits and 262,133 stores in 2,750 cases on each of three real game assembly builds. Tests included shared targets, equal priorities, exhausted budgets, unordered inputs, the known NaN behavior, signed zero and subnormals. Additional checks covered protected pages, pending jobs, failed scheduling, unload and city changes. No user save was modified. A live save round trip and current-city frame delivery remain unmeasured. [Independent safety verdict](safety/README.md) · [Native checks](native/native-checks.csv)

Open `ServiceCoverageFix.sln` in Visual Studio and build **Release** with the Cities: Skylines II mod toolchain. The production project compiles only its six source files. The compile-only Visual Studio archive contains the project, the full Properties directory, the production source and licenses. Rebuilding the managed code from that extracted archive against the supplied game references produced SHA-256 `b8c8a7d6494839bda0445af05098d94c1d2841c3fa107dd45368993284748a6d`. Windows x64 SSE4 and AVX2 Burst workers compiled and linked in isolation. The official Windows postprocessor and a live game launch were outside this run. No dedicated in-game test is requested. The audit brief executed by the six high-effort Astra reviewers is included. [Audit prompt](AUDIT-PROMPT.md)
