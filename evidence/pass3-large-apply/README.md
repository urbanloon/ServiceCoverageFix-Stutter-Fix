# Large Apply selector experiments against 0.3.6

Fourteen unshipped implementations were compiled as actual Unity Burst workers against the current f076df4b Game.dll. The production baseline is 0.3.6 at `6c4c7f4`. The game's own Apply worker supplied the output and visit-count oracles. The 0.3.5 worker did not participate in this series.

The closest candidate retained the first two competing providers outside the radix queue. It passed the initial 526-provider screen but lost to 0.3.6 on the same finite-budget workload in both balanced holds. At 526 providers it did show repeatable benefits on the eight-level tied fixture. Other changes traded gains on particular key distributions for larger losses on finite budgets, ties or other full-overlap inputs. This experiment supports keeping the production selector in 0.3.6.

## The front pair

Each losing provider normally re-enters the radix queue. The `pair` candidate can swap the first two positions directly when the former winner still precedes the third provider. Equality puts the newly inserted loser ahead of its ties, reproducing vanilla's insertion rule. An unordered initial head uses the existing ordered fallback. `ApplyElement`, record sizes, scratch directories and the surrounding game pipeline keep their 0.3.6 behavior.

The entries below are medians of the within-round elapsed-time ratio, 0.3.6 divided by candidate. A ratio above 1 means the candidate ran faster. The two-way holds reversed execution order within every adjacent pair of rounds. Other shapes, raw timings, sample counts, per-worker medians and thread CPU ratios are available in [summary.csv](summary.csv) and [observations](observations).

| 526-provider generated input | AVX2 screen, 15 rounds | AVX2 hold, 32 pairs | SSE4 hold, 32 pairs |
|---|---:|---:|---:|
| Interleaved | 1.092 | 1.014 | 1.025 |
| Finite budgets, distinct targets | 1.041 | 0.910 | 0.934 |
| Eight-level ties | 1.067 | 1.023 | 1.056 |
| Alternative full overlap | 1.102 | 0.993 | 0.982 |
| Mixed priority ranges | 1.083 | 1.034 | 0.955 |
| Narrow, separated ranges | 1.168 | 1.061 | 1.047 |

Both holds used the original 526 × 2,114 finite-budget input, which processes 510,220 of 1,111,964 available elements. The candidate won 10 of 32 finite-budget pairs on AVX2 and 7 of 32 on SSE4. A 16-pair AVX2 holdout spanning the four saved candidate counts of 49, 524, 533 and 1,154 measured ratios from 0.966 to 1.201 across four generated distributions. These counts determine fixture size. The priorities and visited roads for a real loaded city remain unknown.

A separate instrumented build counted actual bypasses. The normal interleaved and finite-budget fixtures skipped 916 of 508,722 and 1,022 of 508,557 losing switches. Narrow ranges skipped 67,852 of 441,342. On 526 providers with 2,114 elements each sharing 6,128 roads, bypasses accounted for 2,108 of 1,109,222 citywide switches and 2,123 of 1,109,221 local switches. Reading the third provider earlier also changes memory access order. The experiment does not isolate which effect caused any particular timing difference. The probe never ran in the timing series. [Switch counts](observations/switch-counts.csv)

## Other candidates

| Candidate | Change | Outcome from 526-provider tests |
|---|---|---|
| `inverse`, `asint`, `cache` | Reconstruct the runner cutoff from the popped radix priority using two equivalent integer encodings, or retain the runner's raw float bits in its node. | The 24-pair inverse hold measured 0.964× on interleaved, 0.903× on budget and 0.953× on ties. The raw-cache screen reached 0.802× on separated ranges. |
| `tree`, `packed`, `select` | Replay a loser's path in a bounded tournament. The first version fetches each opponent's rank from a provider slot. The other two retain ranks and packed leaf/provider IDs along the replay path using branching or conditional selection. | Budget measured 0.814×, 0.574× and 0.800× respectively. At 1,154 providers the `tree` and `select` variants measured 0.863× and 0.805× on the full-overlap holdout. |
| `cal0`, `cal3`, `cal6`, `cal9` | Keep 1,024 near bins, a binary radix for farther windows, and respectively 0, 3, 6 or 9 low-order key bits sorted inside each bin. Window transitions preserve list tie order. | All four regressed on finite budgets, from 0.357× to 0.839×. The 3-bit version achieved 1.805× on narrow separated ranges and 0.759× on the budget fixture. |
| `pairkey` | Use the two competitors and reconstruct their cutoff from the radix priority. | Its first screen measured 0.965× on finite budgets and 0.957× on mixed ranges. |
| `cursor`, `carry` | Retain the existing next runner's cursor or its complete 16-byte provider state across a switch in the 0.3.6 selector. | The screen found 0.963× and 0.984× on finite budgets and 0.997× and 0.936× on ties. |

Tournament scratch is bounded at 1,536 leaves. Larger inputs use the original radix path. The prototype's finite insertion ticket also assumes at most `int.MaxValue` losing insertions. The game's prepared, nonoverlapping provider ranges bound visits by the element list length. Artificially overlapping ranges can violate that bound. The prototypes are included only as test data.

## Verification and scope

Every timed fixture compared candidate target bytes to the real vanilla native worker before timing and checked all input elements, buffer guards and candidate counters. In a separate run, each of the 14 AVX2 candidates passed 2,750 native cases with an independently instrumented vanilla total of 2,669,403 visits and matching workload counters. Each also passed 3,072 native randomized cases including shared targets, initial and later NaNs, negative or unordered priorities, and 8,193- and 65,537-provider tie fixtures. The latter two sizes exceed the tournament cutoff and exercise its original radix fallback. These checks compare the final output and counters. Full ordered traces require the repository's separate `tests/visit_order` verifier. [Worker and assembly hashes](workers.csv) · [Verifier inventory](checks.csv) · [Shared verifier output](native-differential.log)

The measurements cover complete native Apply execution on Linux x64, including filtering, the original concrete sort, selection, arithmetic and diagnostics. The 526-provider workloads are generated; timing excludes input reset, scheduling and frame presentation. This Intel Xeon Platinum 8573C host used logical CPU 5. Other work ran concurrently. Multi-worker screens shuffled worker order. Two-worker holds alternated AB/BA within each pair of rounds. Raw observations include two clocks after the first screen and inverse hold. Thread CPU time reduces descheduling artifacts and still reflects cache and bandwidth contention. Gains on one generated distribution do not establish a player-visible frame-time change.

## Reproduction

Run from the repository root. The exact variant patches apply to copies of the baseline. The materializer accepts an empty destination and copies all six source files before applying the necessary patch chain. Keep the working copy and compiled workers separate from the shipping mod.

```bash
python3 evidence/pass3-large-apply/reproduce/materialize.py pair /path/to/empty/pair
python3 tests/compile_managed.py "$GAME_MANAGED" "$HARMONY_DIR/0Harmony.dll" \
    /path/to/empty/pair/managed/ServiceCoverageFix.dll \
    --src /path/to/empty/pair/src --csc "$CSC"
python3 tests/burst_compare/compile_worker.py mod "$GAME_MANAGED" \
    /path/to/empty/pair/managed/ServiceCoverageFix.dll "$SELECTOR_WORKERS/pair/AVX2" \
    --burst-runtime "$BURST_RUNTIME" --harmony-dir "$HARMONY_DIR" --target AVX2
dotnet build -c Release evidence/pass3-large-apply/reproduce/Bench.csproj
```

Place matching native producer directories at `$SELECTOR_WORKERS/{vanilla,base,variant}/{AVX2,X64_SSE4}`, with `base` built from the unmodified 0.3.6 sources. Compile stock from its own Game.dll as described in [the native harness instructions](../../tests/burst_compare/README.md). The runner arguments are the seed, measured round count, architecture and worker labels. Two warm-up rounds precede measurements.

| Observation file | Seed | Rounds | Architecture | Worker labels after `base` | Workload option |
|---|---:|---:|---|---|---|
| screen | 351 | 7 | AVX2 | inverse asint cache tree | standard |
| hold-inverse | 4217 | 24 | AVX2 | inverse | standard |
| screen-cal | 4731 | 11 | AVX2 | cal0 cal3 cal6 cal9 | minimum 4,096 elements |
| screen-tree2 | 8931 | 15 | AVX2 | packed select | minimum 4,096 elements |
| save-screen | 13577 | 11 | AVX2 | tree select cal3 | saved counts |
| screen-pair | 12347 | 15 | AVX2 | pair pairkey | minimum 4,096 elements |
| hold-pair-avx | 13171 | 32 | AVX2 | pair | minimum 4,096 elements |
| hold-pair-sse | 5171 | 32 | X64_SSE4 | pair | minimum 4,096 elements |
| hold-pair-save | 81517 | 16 | AVX2 | pair | saved counts |
| screen-carry | 29171 | 15 | AVX2 | cursor carry | minimum 4,096 elements |

```bash
bench=evidence/pass3-large-apply/reproduce/bin/Release/net8.0/Bench.dll
SCREEN_MIN_N=4096 taskset -c 5 dotnet "$bench" 13171 32 AVX2 base pair
SCREEN_SAVE=1 taskset -c 5 dotnet "$bench" 81517 16 AVX2 base pair
COUNT_SWITCHES=1 taskset -c 5 dotnet "$bench" 13171 1 AVX2 pairprobe
COUNT_SWITCHES=1 SCREEN_ROAD=1 taskset -c 5 dotnet "$bench" 682731 1 AVX2 pairprobe
```

The two earliest series used the [existing elapsed-time driver](../large-apply/reproduce) with the same normal fixture generator. Their CSVs contain the elapsed column only. For the switch probe the emitted fields are seed, providers, shape, processed, losing switches and bypasses. The native producer hashes in this directory identify the actual timed or checked products.
