# Final worker verification and timings

The 0.3.6 shipping-source build produces managed SHA-256 `b8c8a7d6494839bda0445af05098d94c1d2841c3fa107dd45368993284748a6d`. The 0.3.5 comparison uses commit `0920e76` and managed SHA-256 `ec0f1babf3bda04a71bb81808858112c25816111605ebb48a9628b2b5a2b48ce`. Unity Burst 1.8.23 produced every measured worker. These elapsed times cover an isolated Apply worker on synthetic inputs, measured on an AMD EPYC 9V74 Linux x64 host. No game was launched.

## Three-worker measurements

Each target and workload has 24 measured triplets with all six orders balanced. The table reports milliseconds and the median within-round 0.3.5/0.3.6 ratio. Independent medians and a median paired ratio can differ in direction around 1.0. Sample order statistics in the full summary describe run-to-run spread; they are not confidence intervals.

| Target | Workload | Stock ms | 0.3.5 ms | 0.3.6 ms | Paired ratio |
|---|---|---:|---:|---:|---:|
| AVX2 | Budget-limited park | 407.1483 | 24.7651 | 22.5483 | 1.087× |
| AVX2 | Light | 0.0202 | 0.0140 | 0.0103 | 1.375× |
| AVX2 | Park interleaved | 379.8392 | 16.1000 | 14.5883 | 1.092× |
| AVX2 | Park dense ties | 6.2796 | 2.6175 | 2.6715 | 1.008× |
| AVX2 | Twice the park providers | 1573.3557 | 40.1083 | 36.8971 | 1.098× |
| SSE4 | Budget-limited park | 535.0626 | 30.9523 | 25.2753 | 1.101× |
| SSE4 | Light | 0.0233 | 0.0170 | 0.0153 | 1.111× |
| SSE4 | Park interleaved | 400.2192 | 15.7437 | 14.5884 | 1.056× |
| SSE4 | Park dense ties | 6.5154 | 2.9341 | 2.9121 | 1.000× |
| SSE4 | Twice the park providers | 1648.3460 | 40.4503 | 37.6230 | 1.060× |

The 526-provider budget fixture started with 1,111,964 usable elements and independently verified 510,220 processed elements. Schedule noise affected individual samples, particularly on SSE4. For example, the third and 22nd ordered ratios for its 24 budget rounds were 0.783 and 1.543. Complete triplet samples and all 18 summary rows accompany this report.

## Additional independent workloads

Seed 3179 tested 36 provider-count/stream-length/priority-shape combinations on each instruction set, using 23 randomized-order pairs per row. The AVX2 paired medians range from 0.985× to 2.156×, and SSE4 from 0.990× to 1.713×. Seed 8819 tested 49, 524, 533 and 1,154 providers with 970 generated elements per provider on AVX2. Each of the 16 size/shape combinations ran 17 randomized-order pairs. The interleaved cases at 524, 533 and 1,154 providers measured 1.106×, 1.110× and 1.093×. The full save-scale range was 0.967–1.110×. Actual saves supplied the nominal provider counts only. All fixture priorities, ranges, coverage targets and budgets here were generated.

The four-field workload payload was also compared with an otherwise identical worker compiled after omitting only `Diagnostics->Workload = workload;`. Three AVX2 budget runs with balanced stock/control/shipping triplets gave 72 matched observations. Median worker time was 22.392 ms with publication off and 22.134 ms with publication on. The median paired on/off ratio was 0.991, with 10th and 90th percentiles of 0.832 and 1.183. This run did not resolve a consistent timing cost.

## Correctness and build identity

- On each of the two original game assembly hashes, an exact interleaved trace compared all 2,669,403 selected elements and 262,133 destination stores across 2,750 fixtures. Each store carries its destination and both float bit patterns.
- For each game and each Linux target, all four workload fields matched the independently instrumented stock visits and input ranges across the same 2,750 cases. Full native destination bytes, elements and buffer guards matched. Each target also passed 3,072 generated differential cases plus single-element tie stresses with 8,193 and 65,537 providers.
- The complete 3,072-case differential and both tie stresses passed under each of the four DAZ/FTZ combinations on both targets on both game builds.
- Both Windows x64 shipping workers linked and exported the expected job producer and `burst.initialize`. Windows artifacts were inspected offline.
- The shipping import fix qualifies the intended MethodImpl types for the .NET 8 harness. Its managed assembly SHA differs from the timing build. All 154 methods matched after resolving token operands, including bytecode instructions, locals, method flags and exception tables. The canonical method listing digest is `f2b57a3f4c7553797afc34a126755e532b2ebdd5c419673bde53a5bb0010ca8f`. The Linux shared libraries are byte-identical in full on all four game/target combinations. The Windows libraries match after zeroing only the COFF linker timestamp. The shipping publication-control worker also matches its timed Linux binary in full. See `timed-shipping-identity.csv` for exact hashes.
- `checks.csv` contains the final-source runs, and `worker-hashes.csv` and `reference-hashes.csv` identify native producers and source images.

## Reproduction

Use the compiler and native runner described in [the native harness guide](../../tests/burst_compare/README.md). Build a matching stock and 0.3.5 worker for each target. For each target, set `CS2_BURST_VANILLA` to its stock worker directory, and supply the matching 0.3.6 and 0.3.5 directories in that order. Run the following from the repository root.

```bash
dotnet run -c Release --project tests/burst_compare -- "$GAME_MANAGED" "$FINAL_WORKER" "$V035_WORKER" --require-nan --budget --three-way
CS2_SHAPE_SEED=3179 CS2_SHAPE_ROUNDS=23 dotnet run -c Release --project tests/burst_compare -- "$GAME_MANAGED" "$FINAL_WORKER" "$V035_WORKER" --shapes --require-nan
CS2_SHAPE_SAVE_SCALE=1 CS2_SHAPE_SEED=8819 CS2_SHAPE_ROUNDS=17 dotnet run -c Release --project tests/burst_compare -- "$GAME_MANAGED" "$FINAL_WORKER" "$V035_WORKER" --shapes --require-nan
```

Use AVX2 for the save-scale run. For the publication control, replace the 0.3.5 worker directory with the one-line-omission control and run the budget fixture three times. Use the visit-order oracle sequence and DAZ/FTZ loop in the native harness guide to reproduce the differential checks on each game. To compare managed IL identities, run the project in `il-identity` with `-p:GameManagedPath="$GAME_MANAGED"` and supply the two DLL paths after `--`.
