# Apply worker workload matrix

These measurements compare commit 88ed617 with the runner-up selector in 0.3.5. Both are the compiled Linux x64 SSE4 job producers, with diagnostics counters attached. The vanilla producer supplies the reference output for every generated fixture. Timings cover the complete Apply worker, including filtering, initial NativeSort and coverage arithmetic.

The final candidate includes the denormals-are-zero priority correction. The managed assembly SHA-256 was `ec0f1babf3bda04a71bb81808858112c25816111605ebb48a9628b2b5a2b48ce`. Its Linux SSE4 worker SHA-256 was `cf6f0eaeda3918763269af86d7d47a9445458fbbfd69525042ddb1b0b9763b23`.

The inputs are generated. The two largest sizes match the recorded active provider and processed-element counts. All providers in this matrix have enough budget to finish. The separate budget-exhaustion fixture in `tests/burst_compare` measures providers that retire partway through their inputs.

| Fixture | Paired median speedup, seed 371 | Seed 1601 |
|---|---:|---:|
| 421 providers, interleaved | 1.0820× | 1.0546× |
| 421, dense ties | 1.1081× | 1.0905× |
| 421, mixed overlap | 1.0978× | 1.0997× |
| 421, long runs | 1.1175× | 1.1140× |
| 526 providers, interleaved | 1.0640× | 1.0852× |
| 526, dense ties | 1.0982× | 1.1090× |
| 526, mixed overlap | 1.1014× | 1.0820× |
| 526, long runs | 1.0932× | 1.0948× |
| 8 providers / 256 elements, dense ties | 0.9895× | 0.9875× |
| 16 providers / 1,024 elements, dense ties | 0.9684× | 0.9732× |

Each trial resets the provider records and coverage targets outside the timer. Three warmups per worker precede 23 randomized paired rounds. Small passes run in batches of up to 256 independently initialized copies. Every fixture checks all target bits against the original game producer, all coverage elements for mutation and every buffer's end guards. Each seed includes 36 fixtures. The host was an AMD EPYC 9V74 virtual CPU, pinned to logical CPU 4.

The individual trial observations are in `raw.csv`. Candidate ID 0 is 0.3.4 and ID 1 is the runner-up selector. `summary.csv` gives median times in microseconds, the median of the 23 paired old/new time ratios, and their empirical 10th/90th-percentile order statistics. A speedup above 1 means the candidate took less time. The worker measurements exclude game scheduling and frame time.

## Reproduce

Build the three workers using the Linux commands in `tests/burst_compare/README.md`. With the three worker directories available, run from the repository root.

```sh
export CS2_BURST_VANILLA="$PWD/out/vanilla"
for seed in 371 1601; do
  CS2_SHAPE_SEED="$seed" dotnet run -c Release --project tests/burst_compare -- \
    "$GAME_MANAGED" out/candidate out/baseline --shapes --require-nan > "shapes-$seed.log"
done
```

The CSV rows begin with `SHAPE,`. The `interleaved` streams span a common range. The `ties` streams sample 64 quantized levels before computing true coverage averages. The `mixed` streams overlap in bands with random offsets. The `runs` streams occupy narrower bands staggered by provider index. Targets are shared random addresses with about seven elements per target, and provider records are shuffled before native sorting.
