# Isolated Burst worker verification

This Linux x64 fixture compiles the supplied game's private ApplyCoverageJob producer, the candidate mod and an optional previous mod into separate native libraries. It passes independently allocated NativeList headers and sentinel-protected buffers to the actual generated producer exports. Each mod receives its own live diagnostics counters. The inputs, full output comparisons, guard checks and paired timing rounds come from the adjacent game_compare project. This measures worker time without starting Unity, scheduling jobs or applying Harmony patches.

Use Python 3, the .NET 8 SDK for the harness, a .NET 10 host for the Burst compiler, Unity Burst 1.8.23, the game's Managed folder, Harmony 2.2.2 net48 and compiled managed mod assemblies. The package's Linux host linker creates each native library from the compiler output. Each compilation writes the assembly path, file hashes and producer name to worker.json. The runner validates these and the actual job, list and counter layouts. It accepts self-contained Linux x64 SSE4 and AVX2 workers whose generated static and external initialization callbacks are empty.

From the repository root, set `GAME_MANAGED`, `BURST_RUNTIME` (the package's .Runtime directory), `HARMONY_DIR`, `CANDIDATE_DLL`, and `BASELINE_DLL` to absolute paths. The .NET 10 runtime can be selected with `--dotnet10 /path/to/dotnet` on each compiler command.

```bash
python3 tests/burst_compare/compile_worker.py vanilla "$GAME_MANAGED" "$GAME_MANAGED/Game.dll" out/vanilla --burst-runtime "$BURST_RUNTIME"
python3 tests/burst_compare/compile_worker.py mod "$GAME_MANAGED" "$CANDIDATE_DLL" out/candidate --burst-runtime "$BURST_RUNTIME" --harmony-dir "$HARMONY_DIR"
python3 tests/burst_compare/compile_worker.py mod "$GAME_MANAGED" "$BASELINE_DLL" out/baseline --burst-runtime "$BURST_RUNTIME" --harmony-dir "$HARMONY_DIR"
export CS2_BURST_VANILLA="$PWD/out/vanilla"
dotnet run -c Release --project tests/burst_compare -- "$GAME_MANAGED" out/candidate out/baseline --require-nan --timing
```

To measure the original game worker against the candidate, pass `out/vanilla` in the baseline position. To check Windows x64 code generation on Linux, pass `--platform Windows` on a separate compile into a fresh directory. Windows results attest compilation and linking. The native runner executes Linux libraries. The published mod still uses the official game toolchain for postprocessing and packaging.

Pass `--require-nan --budget --three-way` in place of `--timing` for the balanced vanilla / previous mod / candidate measurements. Each shape runs 24 measured triplets rotating all six execution orders. `SAMPLE` rows give rounds and milliseconds for vanilla, previous and candidate. `THREE_WAY` rows give shape, providers, available elements, three median times, the previous/candidate ratio (median and 3rd and 22nd sorted values), then the three corresponding vanilla/candidate statistics. Each fixture compares its output bytes and checks allocation guards before timing. Times exclude input reset and guard verification.

The budget fixture uses 526 sorted streams of 2,114 elements each. A budget of 746.25 and unit density and length naturally exhaust every provider after 970 elements. The permuted targets are all distinct and initialized to zero, so the count of changed vanilla target pairs independently certifies the consumed count reported by the candidate. Other benchmark shapes exercise shared targets and small priority ranges.

For the AVX2 comparison, compile *all three* workers using `--target AVX2` into separate directories and point `CS2_BURST_VANILLA` at the matching vanilla directory. The optional `--park-only` limits the three-way sweep to the two 526-provider interleaved/tied fixtures. The independent budget workload is selected separately with `--budget`.

### Workload counter overhead

To reproduce the worker-only control, copy the final candidate sources into a temporary directory, remove only the line `Diagnostics->Workload = workload;` from that copy, and compile it with the identical compiler options. Keep the normal completion, start and managed-execution counters. Compile that control and the candidate for AVX2. Use the control as the baseline argument to the budget fixture above with `--budget` and a matching AVX2 vanilla worker. Repeat three times for 72 balanced pairs. The directory arguments in order are the game Managed directory, reporting-enabled candidate and temporary control.

### Denormal input and output modes

This optional fixture sets the Linux x64 test thread's MXCSR before running both native workers. The production mod uses the game's existing floating-point environment. With the worker directories set as above, run the same differential under each combination of denormals-are-zero (DAZ) and flush-to-zero (FTZ).

```bash
fp_helper=$(mktemp -d)
cc -O2 -fPIC -shared tests/burst_compare/set_denormal_flags.c -o "$fp_helper/libscf_test_mxcsr.so"
for mode in preserve daz ftz daz-ftz; do
  CS2_BURST_DENORMAL_MODE="$mode" LD_LIBRARY_PATH="$fp_helper${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" \
    dotnet run -c Release --project tests/burst_compare -- "$GAME_MANAGED" out/candidate --require-nan
done
```
