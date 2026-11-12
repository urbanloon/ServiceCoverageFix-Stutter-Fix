# ServiceCoverageFix 0.3.1 selector benchmark

## Scope

This benchmark isolates the provider selector that dominated the post-fix ETW
samples. It is not a prediction of total in-game frame time: the coverage
pointer reads, writes, and floating-point budget math are deliberately excluded.

The native harness models both measured heavy passes:

- 421 providers and 427,319 processed elements;
- 526 providers and 510,220 processed elements.

For each size it tests highly interleaved, dense-tie, mixed-overlap, and
long-run priority streams. Provider ranges are physically shuffled and keys use
the real 32-byte element stride. Every candidate's complete provider-selection
sequence is compared with the reference before timing. Measurements use 41
randomized-order rounds.

## Selected design

The selected four-level byte-radix queue with a 4,096-event/5% adaptive winner
probe measured the following speedups against the v0.2 33-bucket selector:

| Workload | Selector speedup | Queue selections |
|---|---:|---:|
| 421, high interleave | 1.173x | 427,167 |
| 421, dense ties | 1.957x | 53,913 |
| 421, mixed overlap | 1.312x | 179,916 |
| 421, long runs | 1.355x | 163,028 |
| 526, high interleave | 1.165x | 510,050 |
| 526, dense ties | 2.013x | 64,553 |
| 526, mixed overlap | 1.300x | 241,052 |
| 526, long runs | 1.215x | 200,561 |

Geometric mean: **1.404x**. Range: **1.165x to 2.013x**. Every
workload's paired confidence interval remained above 1.0.

The original game's extreme linear shifting is evidence that the problematic
Park stream is highly interleaved. Byte radix remained faster in both modeled
high-interleave cases without relying on a prefetch intrinsic. This remains a
selector-only native result until a Release/Burst build is captured in game.

## Adaptive policy

The broad winner check is exact, but checking it on every element is wasteful
when almost every provider immediately loses to a competitor. The first 4,096
non-equal advances classify the input:

| Shape | 421-provider hits | 526-provider hits | Decision |
|---|---:|---:|---|
| High interleave | 59 / 4,096 | 49 / 4,096 | Disable broad peek |
| Mixed overlap | 2,882 / 4,096 | 2,800 / 4,096 | Retain broad peek |
| Long runs | 1,961 / 4,096 | 4,096 / 4,096 | Retain broad peek |

Equal-to-current-minimum runs always take a separate constant-time exact path.
The adaptive choice only controls whether a redundant reinsert followed by an
immediate pop is algebraically removed; it cannot alter output order.

## Rejected alternatives

| Candidate | Result / reason rejected |
|---|---|
| Winner tree | 0.699x geometric mean; fixed tree update per element loses |
| Binary insertion + 4-byte moves | 0.543x worst case; retains scaling hazard |
| Node metadata SoA | 0.986x geometric mean; AoS8 is better |
| Source-bucket minimum rescan | Slower and up to 9% worse |
| Always-on full winner peek | Regresses on the high-interleave case |
| Fast math / reciprocal / FMA | Changes floating-point results |
| Parallel provider writes | Coverage pointers alias; order affects budgets |
| Work skipping or lower update frequency | Changes simulation behavior |

Strict-float Apply microbenchmarks also rejected a factor cache (0.880x
geometric mean) and a density-one branch (0.943x). Other arithmetic shortcuts
were workload-sensitive and target only 2.4% of the ETW samples. A separate
traced-size Process-sort prototype found 4-byte index sorting slower by 0.9% to
29.6%; 24-byte packing saved only 1.4% to 8.2% inside that already-parallel
stage while greatly expanding the patch boundary.

## Structural verification

- Four byte levels bound a node to at most four redistributions rather than 32.
- A 17-word bitmap plus word directory finds the next source without scanning
  1,025 buckets.
- Reverse prepend, append redistribution, and prepend reinsertion retain the
  exact concrete tie order.
- Forward compaction transforms each disposable 24-byte provider record into a
  16-byte state and uses its reclaimed 8 bytes for one node. The regions exactly
  fit the original allocation and remove the prior provider cap/fallback.
- Exhaustive small-domain, byte-boundary, random 32-bit, dense-tie, and
  large-provider property tests compare the entire output sequence.
