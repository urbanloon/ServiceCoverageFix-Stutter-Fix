#!/usr/bin/env bash
set -euo pipefail
# Arguments: game Managed, SDK dotnet, Unity Burst package directory, .NET 10 dotnet, output directory.
src=$(cd "$(dirname "$0")" && pwd)
managed=$(realpath "$1")
sdk=$(realpath "$2")
burst=$(realpath "$3")/.Runtime
runtime=$(realpath "$4")
output=$(realpath -m "$5")
mkdir -p "$output/raw" "$output/bound" "$output/aot"
compiler=$(find "$(dirname "$sdk")/sdk" -path '*/Roslyn/bincore/csc.dll' | sort -V | tail -1)
refs=()
for name in mscorlib System netstandard Unity.Collections Unity.Burst UnityEngine.CoreModule Unity.Mathematics; do refs+=("-r:$managed/$name.dll"); done
"$sdk" "$compiler" -nologo -nostdlib+ -optimize+ -unsafe -target:library -out:"$output/raw/ProcessNative.dll" "${refs[@]}" "$src/BurstSort.cs"
"$sdk" run --project "$src/bind/BindGame.csproj" -c Release -p:GameManagedPath="$managed" -- "$managed/Game.dll" "$output/raw/ProcessNative.dll" "$output/bound/ProcessNative.dll"
printf '%s\n' '{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' > "$output/runtime.json"
"$runtime" exec --runtimeconfig "$output/runtime.json" "$burst/bcl.exe" --platform=Linux --target=AVX2 \
  --root-assembly="$output/bound/ProcessNative.dll" --assembly-folder="$managed" \
  --output="$output/aot/process" --temp-folder="$output/aot/tmp" --verbose --nolink --keep-intermediate-files > "$output/aot/compiler.log" 2>&1
"$burst/burst-lld-19-hostlin" -flavor gnu --shared --soname=process.so --no-undefined --gc-sections --build-id -o "$output/process.so" "$burst/libs/burstRTL_l64.a" "$output"/aot/lib_burst_generated_part_0*.o --threads=1
nm -D "$output/process.so" | awk '$2 == "T" && $3 ~ /^[[:xdigit:]]+$/ {print $3}' > "$output/export.txt"
test "$(wc -l < "$output/export.txt")" -eq 1
