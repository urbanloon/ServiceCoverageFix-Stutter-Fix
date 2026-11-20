# Options binding and diagnostics overhead

## What the game sends to the Options panel

In 0.3.5 all seven fields used `SettingsUIMultilineText`. The game's multiline widget reads its display name. With a static label, the writer serialized only the localization key. The first 0.3.5 execution-state widget produced `Options.OPTION[test.DiagnosticSettings.ExecutionStatus]` and omitted its value.

The candidate binds the five single-value fields to the game's read-only string accessor. Each of the two multiline workload widgets binds its display-name callback. The game runs these callbacks as part of its existing widget update. The mod adds no updater, timer or world query.

The [widget test](../../tests/options_widgets/Program.cs) creates all seven widgets using the supplied game's property classifier and widget factories, calls the actual widget updates and captures the strings the game's JSON writer sends to the frontend. The same widget objects show a checked-inactive reason, subsequently active fields, a changed load boundary, the four counts in a seeded Park row, preview status, and distinct fallback errors. The candidate passed on these three Game.dll SHA-256 prefixes: `721e7e17`, `f076df4b`, and `aaee15c4`. The test runs outside the game and covers the binding and serialized values. Visual layout in the frontend was not measured.

## Managed adapter costs against 0.3.5

Median nanoseconds and managed bytes per operation, 15 paired processes in alternating order, .NET 8.0.25 Release on AMD EPYC 9V74, pinned to one logical CPU with tiered compilation disabled. One or sixteen simultaneous simulation submissions establish the pool size, and all finish before each scenario. The empty managed pass includes the deterministic scheduler adapter's own allocations. The logging-only scenario seeds completed native-style counters before measurement. The menu scenarios call the seven property getters directly to compare equivalent reads. The broken 0.3.5 widget bindings did not issue all seven reads through the UI. These measurements exclude Unity scheduling, actual Burst workers, frontend rendering and frame time.

| Operation | Pool | 0.3.5 ns | Candidate ns | 0.3.5 B | Candidate B |
|---|---:|---:|---:|---:|---:|
| Empty managed pass | 1 | 92 | 86 | 224 | 152 |
| Read unchanged panel values | 1 | 373 | 133 | 792 | 0 |
| Empty pass and read changed panel values | 1 | 5,069 | 1,095 | 6,899 | 2,577 |
| Check steady Burst log state | 1 | 30 | 14 | 72 | 0 |
| Empty managed pass | 16 | 223 | 77 | 224 | 152 |
| Read unchanged panel values | 16 | 788 | 309 | 792 | 0 |
| Empty pass and read changed panel values | 16 | 5,796 | 1,273 | 6,899 | 2,577 |
| Check steady Burst log state | 16 | 141 | 35 | 72 | 0 |

The scheduler stops at the first reusable slot for the requesting caller. It completes only handles already reported finished. Options caches the published row for each service, copies changed data under the lock, formats outside it and reuses unchanged status strings. A deterministic stress fixture pauses numeric formatting and verifies that scheduling and a load reset can finish during that pause. A late UI read cannot restore old rows after the load. Execution totals survive partial native-buffer disposal and an unload retry.

## Reproduction

The [raw samples](samples.csv) contain every measurement and the [summary](summary.csv) includes median paired speedups. Use a source checkout of commit `0920e76` for the 0.3.5 comparator. Supply an installed game's Managed directory and a compiled candidate DLL.

```sh
dotnet run -c Release --project tests/options_widgets -- "$GAME_MANAGED" "$CANDIDATE_DLL"
dotnet run -c Release --project tests/runtime_harness -p:GameManagedPath="$GAME_MANAGED"
dotnet build -t:Rebuild -c Release tests/diagnostic_cost -p:GameManagedPath="$GAME_MANAGED" -p:SourceRoot="$BASELINE_SRC" -o /tmp/diagnostics-before
dotnet build -t:Rebuild -c Release tests/diagnostic_cost -p:GameManagedPath="$GAME_MANAGED" -o /tmp/diagnostics-after
python3 tests/diagnostic_cost/compare.py /tmp/diagnostics-before/DiagnosticCost.dll /tmp/diagnostics-after/DiagnosticCost.dll /tmp/diagnostics-samples.csv
```
