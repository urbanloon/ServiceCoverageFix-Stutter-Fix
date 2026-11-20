#!/usr/bin/env bash
set -euo pipefail
# Usage: build.sh GAME_MANAGED SDK_DOTNET BURST_PACKAGE DOTNET10 OUTPUT
src=$(cd "$(dirname "$0")" && pwd)
managed=$(realpath "$1")
sdk=$(realpath "$2")
burst=$(realpath "$3")/.Runtime
runtime=$(realpath "$4")
output=$(realpath -m "$5")
mkdir -p "$output/raw" "$output/bound"
compiler=$(find "$(dirname "$sdk")/sdk" -path '*/Roslyn/bincore/csc.dll' | sort -V | tail -1)
refs=()
for name in mscorlib System netstandard Unity.Collections Unity.Burst UnityEngine.CoreModule Unity.Mathematics Colossal.Mathematics; do
    refs+=("-r:$managed/$name.dll")
done
"$sdk" "$compiler" -nologo -nostdlib+ -optimize+ -unsafe -target:library \
    -out:"$output/raw/RangeKernel.dll" "${refs[@]}" "$src/RangeKernel.cs"
"$sdk" run --project "$src/../native/bind/BindGame.csproj" -c Release -p:GameManagedPath="$managed" \
    -- "$managed/Game.dll" "$output/raw/RangeKernel.dll" "$output/bound/RangeKernel.dll"
printf '%s\n' '{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' > "$output/runtime.json"
for target in AVX2 X64_SSE4; do
    aot="$output/aot/$target"
    mkdir -p "$aot"
    "$runtime" exec --runtimeconfig "$output/runtime.json" "$burst/bcl.exe" --platform=Linux --target="$target" \
        --root-assembly="$output/bound/RangeKernel.dll" --assembly-folder="$managed" --output="$aot/process" \
        --temp-folder="$aot/tmp" --threads=1 --verbose --nolink --keep-intermediate-files > "$aot/compiler.log" 2>&1
    "$burst/burst-lld-19-hostlin" -flavor gnu --shared --soname=process.so --no-undefined --gc-sections --build-id \
        -o "$aot/process.so" "$burst/libs/burstRTL_l64.a" "$aot"/lib_burst_generated_part_0*.o --threads=1
    nm -D "$aot/process.so" | awk '$2 == "T" && $3 ~ /^[[:xdigit:]]+$/ {print $3}' > "$aot/symbol"
    test "$(wc -l < "$aot/symbol")" -eq 1
done
