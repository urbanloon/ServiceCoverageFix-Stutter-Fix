# Shared-road Apply benchmark

The historical park-heavy save has 533 park candidates and 6,128 road edges with coverage buffers. A separate runtime observation recorded 526 participating providers. This benchmark supplies 526 synthetic ranges with 2,114 distinct roads per provider, for 1,111,964 available elements. The actual per-provider paths and order are unavailable in the save. The fixtures test how real native workers respond when many paths share destinations.

Local draws each provider's roads from a wrapping window of 2,560 road IDs. Citywide draws from all 6,128. Ties quantizes the endpoints to 16 levels. Tight uses very small budgets. Mixed gives alternate providers a small budget. Elements are sorted by computed average before the worker runs. Park slot 3 is cleared on each road. The eight other slots and any padding have sentinel values and are included in each comparison.

The default allocation packs nine float2 slots per synthetic road. Setting PARK_OVERLAP_LAYOUT=scattered permutes physical road order and inserts 3–125 extra float2 cells between roads. Both layouts preserve the road identity and collision pattern. The actual game's buffers are individually addressable and can occupy separate allocations.

Build with .NET 8 on Linux x64. Use the repository's Burst compiler harness to generate worker directories and set GAME_MANAGED to the matching game reference directory. The loader checks game, managed source and native worker hashes, ABI and ISA. Supply worker directories with the same ISA. Input creation, resets and comparisons occur outside the timer. One stock execution produces the comparison bytes. Timed stock runs are enabled by default. With PARK_TIME_STOCK=0, the timer rotates among just the mod workers. Raw samples contain both elapsed time and thread CPU milliseconds. The summary reports median thread time and within-round baseline/worker ratios. Each job includes its normal diagnostic counter updates.

```sh
dotnet build -c Release tests/park_overlap/ParkOverlap.csproj
PARK_OVERLAP_LAYOUT=scattered PARK_TIME_STOCK=0 taskset -c 2 dotnet \
  tests/park_overlap/bin/Release/net8.0/ParkOverlap.dll 48 22129 local,citywide,mixed,ties,tight \
  vanilla=/path/to/sse4/stock v036=/path/to/sse4/baseline pair=/path/to/sse4/pair > trial.csv
python3 tests/park_overlap/summarize.py trial.csv
```

Seed 83951 on the packed layout also has an independent stock visit/store trace, including its four workload counters. The exact-sequence replay and signed-zero/NaN tests live alongside the audit evidence.
