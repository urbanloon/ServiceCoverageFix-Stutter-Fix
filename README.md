# Service Coverage Stutter Fix

Service Coverage Stutter Fix reduces a recurring simulation hitch in Cities: Skylines II. The game combines coverage paths from parks, post offices, healthcare buildings and other service providers. In cities with hundreds of providers, keeping them in priority order can take long enough to stall a frame. The mod speeds up that final merge while preserving coverage values, provider budgets and update timing.

[Service Coverage Stutter Fix on Paradox Mods](https://mods.paradoxplaza.com/mods/157236/Windows)

## Version 0.4.0

The service-coverage fix runs by default and carries over from 0.3.6. Four other optimizations are available under **Options → Service Coverage Stutter Fix → Beta options**. Each starts off and can be enabled independently.

| Beta option | What it speeds up |
|---|---|
| Water flow | Picks the next pipe junction to check more efficiently while calculating water and sewage flow on branching networks. |
| Resource-seller search | Reuses a resource's market price while a buyer compares eligible sellers in the same group. |
| Route search | Speeds up selecting the next step when thousands of candidates are queued in a large route search. PathfindLocalityFix takes priority when loaded. |
| Housing search | Reuses household income and support information while checking a group of available homes. |

**Beta worker status** shows which optional workers are available. The separate **Diagnostics** tab shows whether optimized coverage has run and reports the last completed workload by service. For parks, it shows how many providers participated, how many coverage elements were available, and how many the worker processed.

The four betas matched vanilla outputs in offline comparisons on the reviewed game build. Gains depend on the workload. The 0.4.0 betas have no measured city-wide FPS result. The mod adds no data to city saves.

## Build

Open `ServiceCoverageFix.sln` in Visual Studio with the Cities: Skylines II modding toolchain and .NET 8 SDK installed. Choose **Release → Build Solution**. NuGet restores the dependencies on the first build. The route worker generator reads the installed game assemblies during compilation.

## Verification and technical details

- Run `python tests/reference_model.py` to check coverage ordering, including equal priorities and large provider counts.
- The [release build checks](tests/release_build/README.md) verify the solution, scheduling patches and Windows Burst workers against local game assemblies.
- The [original stutter analysis](ANALYSIS.md), [coverage benchmarks](BENCHMARKS.md) and [0.3.3 algorithm write-up](https://github.com/urbanloon/ServiceCoverageFix-Stutter-Fix/blob/77dbcd3af472dec6ff74abcdeb3961ffe0c1e6e0/README.md) document the earlier service-coverage work.

[MIT license](LICENSE) · [Third-party notices](THIRD_PARTY_NOTICES.txt)
