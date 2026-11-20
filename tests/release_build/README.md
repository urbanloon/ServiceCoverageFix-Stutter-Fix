# Release build checks

Build the solution normally on Windows with the Cities: Skylines II toolchain configured. The .NET 8 helper restores ILSpy 9.1.0.7988, reads the local game reference files and emits route and company property source into the SDK intermediate directory. A SHA-256 check covers all ten reviewed input assemblies. The generated stock-derived source stays in the build directory. Only the authored generator is present in the source archive.

For independent verification on Linux, extract the source ZIP, change into the project directory, and set the following paths. `MANAGED` is Cities2_Data/Managed from the November game build, `HARMONY` is the Lib.Harmony 2.2.2 net48 DLL, `OUT` is a new absolute work directory outside the extracted project, and `BURST` is Unity Burst 1.8.23’s .Runtime directory. The SDK commands use .NET 8. The native compiler uses a .NET 10 runtime host, shown as `$DOTNET10`.

```sh
python3 tests/release_build/verify_project.py "$MANAGED" "$OUT/project"
dotnet build tests/release_build -c Release --artifacts-path "$OUT/validation" -p:HarmonyPath="$HARMONY"
MOD="$OUT/project/bin/ServiceCoverageFix/release/ServiceCoverageFix.dll"
dotnet "$OUT/validation/bin/ReleaseBuild/release/ReleaseBuild.dll" "$MANAGED" "$MOD"
python3 tests/release_build/verify_burst.py "$MANAGED" "$MOD" "$OUT/native" --harmony-dir "$(dirname "$HARMONY")" --burst-runtime "$BURST" --dotnet "$DOTNET10"
```

The project check builds the actual ServiceCoverageFix.sln and its net48 project using stand-in game imports for assembly lookup, then verifies private Harmony deployment and unchanged incremental output. The contract check loads the released mod and pristine game, validates its compatibility manifest, checks the layout of each worker and both Unity chunk wrappers, and exercises Harmony’s production transpiler/emitter on the six original scheduling methods. It checks wrong-site cases and JIT-compiles the closed adapters. The native command automatically discovers and links the six Windows workers on SSE4 and AVX2. These checks run outside a game player and do not measure frame pacing.

For a fast standalone Roslyn build, `tests/compile_managed.py` accepts the same Managed directory, private Harmony DLL, output DLL, and the SDK Roslyn compiler through `--csc`. An optional `--decompiler-dir` points to an existing, verified ILSpyCmd 9.1.0.7988 tools directory for an offline generator build.
