# Sampled overlap and visited overlap

The conditional prototype with managed SHA-256 `5ef6bb3c185cde1a4ce040f84fa0711f5396b7d7591109ca82d37870c7541228` reads target addresses in two bands of each sampled provider's path. This experiment measures the branch actually chosen after the game's initial sort, while an independent observer records every stock visit and store.

Each input has 384 providers and 704 valid prepared elements per provider. A short budget exhausts on the first visit. The first elements all reference one target or 384 different targets. Separately, the unused portions contain two repeated addresses or a different address per provider. [Observed choices and visits](results.csv)

Changing only unvisited addresses flipped the choice in both directions. A fully shared visited workload could select the original queue. A fully distinct visited workload could select the two-competitor loop. All 1,536 visits and ordered target stores still matched, including both float bit patterns, final bytes, immutable elements and guards. This establishes a classification limitation. The sampler can recognize repeated addresses in its inspected bands. Those bands can be entirely outside the visited prefix.

To reproduce on Linux, set `REPO`, `GAME_MANAGED` and `GATED_DLL` to absolute paths. Use a private copy for the observer patch.

```sh
tmp=$(mktemp -d)
cp -r "$REPO/tests/visit_order" "$tmp/visit_order"
cp "$REPO/evidence/park-audit-3/safety/gate-control/AllCases.cs" "$tmp/visit_order/"
patch -d "$tmp/visit_order" -p1 < "$REPO/evidence/park-audit-3/safety/gate-control/observe-gate.patch"
dotnet run --project "$tmp/visit_order" -c Release \
  -p:CompareSource="$REPO/tests/game_compare" -p:GameManagedPath="$GAME_MANAGED" \
  -- "$GAME_MANAGED" "$GATED_DLL"
```
