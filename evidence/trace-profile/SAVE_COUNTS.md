# Saved service-coverage candidates

Two historical cities that exhibited periodic stalls had **533 and 524 Park candidates**. A comparison city with more residents had **49**. A separate stress-test city had **1,154**. In all four saves every Park candidate also carried the game's Park building component.

| Save ID | Saved population | All coverage candidates | Park candidates |
|---|---:|---:|---:|
| S-1016f0a5203b | 164,419 | 781 | 533 |
| S-53b5a1a5487f | 250,227 | 818 | 524 |
| S-b1d087f62b4a | 395,380 | 130 | 49 |
| S-4fb561444240 | 52,511 | 1,420 | 1,154 |

The parser applies the game's provider query to each serialized archetype. It requires `Game.Net.CoverageServiceType`, `Game.Pathfind.CoverageElement` and `Game.Prefabs.PrefabRef`, and excludes `Game.Common.Deleted` and `Game.Tools.Temp`. It reads each matching archetype's run-length encoded service values and checks that their run lengths add up to its entity count.

All four files register the path buffer with the empty serializer. The pathfinder rebuilds each provider's buffer after loading. Prepare emits a provider record only if the rebuilt buffer is nonempty. The save therefore establishes the pool of candidate providers. Live path lengths, processed element counts and job durations require runtime evidence. None of these four files was identified as the save from the later port-stutter report.

`save-counts.csv` gives all eight service counts and the complete SHA-256 of each original save. The ID is the first 12 hexadecimal digits of that hash. To reproduce counts for your own inputs, use Python 3 with the `zstandard` package and run `python count_save_providers.py city.cok`. Multiple input files are supported. The parser reads the inputs and writes CSV to standard output.
