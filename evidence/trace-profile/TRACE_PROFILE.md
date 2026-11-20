# Service coverage on the main-thread critical path

In Park infoview capture **C05**, the vanilla Apply worker woke the waiting game main thread in **90 of 91** substantial Apply episodes. The median sampled Apply span was **77.99 ms**. The main thread resumed a median **0.57 ms** after the final Apply sample among the 90 overlapping waits. The preceding parallel Process stage took a median **3.31 ms**.

Both inspected game assemblies contain a second scheduling site for the same vanilla job in `Game.Tools.CoveragePreviewSystem.OnUpdate`. Mod commit `88ed617` patched only `Game.Simulation.ServiceCoverageSystem.OnUpdate`. The preview call can therefore bypass that patch. ETW records execution of the shared job. It does not identify which managed caller scheduled a particular instance.

## Sampled worker spans

| Capture | Workload | Heavy episodes | Median Process | Median Apply | Median first Process to last Apply |
|---|---|---:|---:|---:|---:|
| C01 | Stock simulation | 8 | 4.37 ms | 89.14 ms | 96.47 ms |
| C02 | Experimental heap selector | 9 | 3.83 ms | 26.56 ms | 31.07 ms |
| C03 | 0.1.4 radix selector | 8 | 4.36 ms | 20.44 ms | 25.88 ms |
| C04 | 0.1.4 radix selector | 8 | 5.25 ms | 19.86 ms | 25.36 ms |
| C05 | Park infoview, vanilla Apply observed | 91 | 3.31 ms | 77.99 ms | 81.87 ms |
| C06 | Historical pre-release | 8 | 4.13 ms | 12.17 ms | 16.62 ms |
| C07 | Same pre-release binary | 17 | 3.19 ms | 14.11 ms | 19.13 ms |

C08 and C09 used published 0.3.2 in a different city. Their loaded native modules matched the release image and PDB identity. They recorded 111 and 599 replacement Apply samples and zero vanilla Apply samples. The longest observed same-worker Apply spans were 2.00 and 2.67 ms. Comparisons across cities and builds do not establish a release speedup. These recordings contain CPU and scheduler timings. Presented frame times and candidate-build performance were not measured.

## Other pipeline stages

The pathfinding worker searches the graph and fills coverage results. A parallel conversion job writes the resulting edge lists back to each provider. At the service boundary, Prepare builds the provider ranges and Clear resets the active service slot on edges. Process waits for both, builds and sorts the per-provider ranges in parallel, and feeds the serial Apply merge.

On C08, coverage graph actions accounted for **37,812** CPU samples, compared with **6,232** for result conversion, **763** for Process and **111** for Apply. The same shared pathfinding worker recorded **706,290** samples executing ordinary route searches. Graph searches run ahead of the service slot. Their aggregate worker CPU cost and the main-thread wait during C05 answer different performance questions.

## Evidence and measurement

- `captures.csv` identifies all nine inputs by SHA-256 of both the compressed archive and the expanded ETL and gives CPU sample counts per stage. All nine report zero lost events and zero lost buffers. The six columns from `coverage_graph_init_samples` through `coverage_dispatch_samples` partition `coverage_actions_samples`.
- `episodes.csv` contains the 149 heavy episodes and the paired Process and scheduler measurements. Its timeline starts at each capture's first heavy Apply sample. An empty readying field means no match was observed. `apply_worker_readied_main=1` means the Apply worker was running on the CPU that emitted the ReadyThread event for the waiting main thread.
- `job-symbols.csv` identifies the native roots by managed job, compiler-generated export hash and executable-relative address. The archived game Burst binary's SHA-256 is `cb38360072a9a7fed7b06b0b745b0a0300dee60f4fd284117a16a4fd8dd2190d`. The published 0.3.2 native binary's SHA-256 is `96f84354b502f1a2df4f42af86c37f4be4119e0714d329ad93d11bf89e146007`.

The parser joined ETW SampledProfile and StackWalk by sample timestamp, physical instruction address and thread within the game process. Stage counts include matching game or mod Burst leaves. A heavy Apply episode requires eight or more samples from one worker with gaps of at most 5 ms. The paired Process burst is the last matching cluster within 30 ms before to 1.3 ms after the first Apply sample, with gaps of at most 2 ms, at least three samples and a final sample within 4 ms before Apply or later. Sampled spans run from first to last observation. Work between profiler ticks remains unresolved.

The main thread was identified by its process-entry start address. CSwitch events give off-CPU intervals. ReadyThread and per-CPU scheduling history identify the thread that readied it. All 90 matched C05 waits had old-thread state Waiting and wait reason 37 (WrAlertByThreadId).
