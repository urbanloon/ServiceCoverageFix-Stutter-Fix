# Service Coverage Stutter Fix

Service Coverage Stutter Fix reduces a recurring simulation hitch in Cities: Skylines II. The game combines coverage paths from parks, post offices, healthcare buildings and other service providers. In cities with hundreds of providers, keeping them in priority order can take long enough to stall a frame. The mod speeds up that final merge while preserving coverage values, provider budgets and update timing.

[Service Coverage Stutter Fix on Paradox Mods](https://mods.paradoxplaza.com/mods/157236/Windows)

## Version 0.4.2

This hotfix restores vanilla housing and resource-seller searches after reports of simulation errors. The fix applies even if you enabled those options in 0.4.0 or 0.4.1. Restart the game after updating.

Service coverage runs by default. **Options → Service Coverage Stutter Fix → Diagnostics** shows coverage status and the latest workload for each service, including participating providers and available and processed coverage elements.

| Beta option | What it speeds up |
|---|---|
| Water flow | Water and sewage calculations on branching pipe networks. |
| Route search | Selecting the next step in large route searches. |
| Company property search | Checking occupancy while businesses compare properties. |

All three options start off. These beta options may be incompatible with Big City Performance.

## Build

Open `ServiceCoverageFix.sln` in Visual Studio with the Cities: Skylines II modding toolchain and .NET 8 SDK installed. Choose **Release → Build Solution**. NuGet restores the dependencies on the first build. The build generates the route and company property workers from the installed game assemblies.

## Verification and technical details

- Run `python tests/reference_model.py` to check coverage ordering, including equal priorities and large provider counts.
- The [release build checks](tests/release_build/README.md) verify the solution, scheduling patches and Windows Burst workers against local game assemblies.
- The [original stutter analysis](ANALYSIS.md), [coverage benchmarks](BENCHMARKS.md) and [0.3.3 algorithm write-up](https://github.com/urbanloon/ServiceCoverageFix-Stutter-Fix/blob/77dbcd3af472dec6ff74abcdeb3961ffe0c1e6e0/README.md) document the earlier service-coverage work.

[MIT license](LICENSE) · [Third-party notices](THIRD_PARTY_NOTICES.txt)
