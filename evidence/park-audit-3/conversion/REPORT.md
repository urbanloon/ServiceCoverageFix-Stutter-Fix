# Coverage graph, preview and road-result conversion

Keep the current preview and pathfinding schedules. Both tested conversion replacements lost to the game's own worker on multiple input shapes. Four earlier simulation recordings contain zero result-conversion CPU samples inside the 41 heavy Apply spans. They contain concurrent graph work in 30 of the 41 spans, all on other threads. Work at another point in a simulation update can still affect its deadline.

## Where the recorded conversion time went

The historical game Burst image has SHA-256 `cb38360072a9a7fed7b06b0b745b0a0300dee60f4fd284117a16a4fd8dd2190d`. The conversion producer's exported entry is RVA 0x35c1500. Each count below is a physical CPU sample from a matching game stack and game instruction leaf. Capture IDs and hash-verified decoder inputs are listed in the repository's trace profile. Source code in `map_samples.py` gives the native instruction ranges and caller addresses.

| Capture | Conversion samples | EdgeLane presence | EdgeLane value | Lane owner value | Improving duplicate map edits | Final emit loop |
|---|---:|---:|---:|---:|---:|---:|
| C03 | 4,446 | 2,763 | 682 | 175 | 64 | 38 |
| C04 | 6,140 | 3,659 | 948 | 278 | 103 | 91 |
| C06 | 4,027 | 2,488 | 634 | 169 | 60 | 39 |
| C07 | 7,307 | 4,434 | 1,015 | 241 | 141 | 111 |

The three identified ECS access sites accounted for 77.9–81.7% of samples. The presence test calls RVA 0xabd0 from RVA 0xa6d92b. Two following calls to RVA 0xa5d0 read the lane and its owner. The map-edit count includes direct instructions and immediate callees at the remove and insert sites. The emit count includes direct instructions in the final loop. These are site attributions. Shared callees reached through additional stack levels, other hash costs, allocation and cache effects can fall outside those ranges. They do not give a precise ceiling for an algorithm change.

In the Park infoview recording C05, the entire conversion job produced 327 samples across 45.689 s. The coverage graph produced 2,317, split into 1,655 for graph search and 662 for emitting the raw results. The game’s old Apply worker readied the waiting main thread in 90 of 91 heavy Apply episodes in that recording. In published 0.3.2 capture C09, 142,913 samples came from graph search and 57,513 from emitting graph results. Searching, traversing connections, reading full node identities and curve positions, and preserving directional output dominate coverage graph work. Graph initialization had 206 samples. In C09's separate scheduler analysis for waits of at least 5 ms, seven coverage-graph matches spanned at least half a blocked wait. No conversion match met that threshold. Sample totals cover all workers and cannot be interpreted as frame time.

## Full conversion worker differential

On Linux x64, Unity Burst 1.8.23 compiled the current game's **actual** `CoverageJobs.ProcessResultsJob.Execute(int)` and two alternatives. All three execute real ECS presence/value lookups, version checks, change-version writes, Unity hash containers and writes into a provider's dynamic buffer. The harness builds synthetic entities, archetypes and chunks and supplies a bounded thread-local-style temporary arena. One test thread runs a single provider per call. The fixture supplies a preallocated output buffer and excludes Unity scheduling and contention. A small, identical one-item input-list allocation and disposal are included in each timed call. No cities were opened.

The stock implementation records the first occurrence of every road in a list, stores costs in a map and then looks up each road again to populate the output buffer. An improving duplicate causes removal and reinsertion. Both alternatives use a road-to-output-index map and preserve the first-seen order. The combined version also reads `EdgeLane` via `TryGetComponent`; stock uses `HasComponent` followed by the indexer. All versions apply the same lane orientation and update the two cost components only after stock's strict-less-than condition.

| Native target | Shapes | Indexed slower | Indexed median ratio range | Combined slower | Combined median ratio range |
|---|---:|---:|---:|---:|---:|
| AVX2 | 36 | 14 | 0.876–1.150× | 10 | 0.898–1.313× |
| x64 SSE4 | 36 | 11 | 0.905–1.292× | 13 | 0.844–1.375× |

Each ratio is the median of 24 stock/candidate pairs for one shape. All six execution orders occurred four times after two warmups. The two native targets used independent random seeds on the same CPU. The workload used 8,456 or 24,512 raw results, 2,114 or 6,128 possible road IDs, one or nine archetypes, three cost-ordering patterns, and local or shuffled lane IDs. Each target also passed 24 additional fixtures covering empty results, stale providers, stale lanes, missing components, NaNs, signed zeros, tiny numbers and infinities. The runner checks count, first-seen order, changed output bytes, untouched tail/canaries, and the expected provider chunk change-version. Published timings use the existing output capacity. They cannot validate growth, concurrent scheduler behavior or transient buffer visibility for a shipping patch.

## Preview and worker ownership

- An active entity with `InfomodeActive` and `InfoviewCoverageData` enables preview. Preview selects the first entity of the first matching chunk. Assuming exactly one result would change the multi-match case.
- A service switch or an entry in the game's modified query can scan all preview-visible service providers. The updated-provider path scans its narrower query. Each matching provider gets its own target setup, random seed advance and action enqueue. The owner can differ from the temporary tool entity's original provider. The explicit read dependency for `Temp` precedes the main-thread empty-buffer scan. That scan populates the pending set before coverage work is scheduled. Ordinary and tool graph requests can differ in source, graph state and timing.
- The pending set consumes `CoverageUpdated` events. While providers are pending on a service switch, the game initializes the preview slot from the selected live slot. Event-only updates can reach the preview calculation after the pending set empties. Skipping an apparently unrelated event can delay observation of changed efficiency, district, geometry or other current ECS input until a later publication.
- The result system already folds completed actions by provider before scheduling conversion. The last eligible item replaces the earlier item at that provider's first list position. Statistics and coverage events are handled per eligible action. Conversion is a separate public parallel-for job with batch size one in the coverage overload of `PathfindResultSystem.ProcessResults`. Its returned handle is combined into the system dependency. The raw graph worker sets completed state after a memory barrier. Queue consumption tests the setup dependency's readiness before calling `Complete`.
- The stock pathfinding pool handles coverage, ordinary routes and availability in the same native worker. Its base thread count uses half the configured Unity job-worker count, with a minimum of one. Batch dispatch also accounts for action count and high-priority requests. A C# Harmony patch on the private graph routine cannot replace calls already compiled into the game's Burst dispatcher. No benefit was established for swapping that shared producer, changing priorities or suppressing searches.

## Reproduce

The `reproduce` directory needs Python with NumPy, a C compiler, the .NET 8 SDK and .NET 10 runtime, Unity Burst 1.8.23's Linux compiler, and the matching game Managed directory. Set absolute `DOTNET`, `CSC`, `BURST_RUNTIME` and `GAME_MANAGED` paths. `CSC` is the SDK's Roslyn `csc.dll`; `BURST_RUNTIME` is the package's `.Runtime` directory. The fixture builder uses a generated, accessibility-only Roslyn reference to initialize ECS internals. Burst resolves the original game and Unity assemblies. The input assemblies are opened read-only.

```sh
cd reproduce
bash build.sh AVX2
taskset -c 4 python run.py AVX2 20265 24
bash build.sh X64_SSE4
taskset -c 4 python run.py X64_SSE4 98731 24
```

The timing run used an Intel Xeon Platinum 8573C and CPU 4. Game.dll SHA-256 was `f076df4b70aa0f21a80345e03a052ca2273bc6754f41dab7e63d837837a43745`. Unity.Entities.dll was `7e6d9ed7df5295652fccdb609d3d092cabb5a3fdb91336702d77381a1d42ed86`. The checked-in raw samples feed `summarize.py`. To repeat the trace attribution, run `map_samples.py PATH_TO_DECODED_SAMPLES` from a scratch directory containing the C03, C04, C06 and C07 inputs decoded with the repository’s release-critical-path decoder. The script writes `conversion-sites.csv` there.
