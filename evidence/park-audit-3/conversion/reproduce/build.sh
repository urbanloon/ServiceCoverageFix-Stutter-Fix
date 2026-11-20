#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
target="${1:?Expected X64_SSE4 or AVX2}"
: "${GAME_MANAGED:?}" "${DOTNET:?}" "${CSC:?}" "${BURST_RUNTIME:?}"
out="$PWD/$target"
mkdir -p "$out" public temp
"$DOTNET" run --project publicize/Publicize.csproj -c Release -p:GameManagedPath="$GAME_MANAGED" -- "$GAME_MANAGED" "$PWD/public/Unity.Entities.dll"
refs=()
for asm in mscorlib System Colossal.Core netstandard Unity.Collections Unity.Burst UnityEngine.CoreModule Unity.Mathematics Game; do refs+=("-r:$GAME_MANAGED/$asm.dll"); done
# Publicized ECS is a Roslyn-only compile reference for the fixture builder.
# Burst must resolve the original assemblies from the game's Managed directory.
"$DOTNET" "$CSC" -nologo -nostdlib+ -optimize+ -unsafe -target:library -out:"$out/Kernel.dll" "${refs[@]}" -r:"$PWD/public/Unity.Entities.dll" Probe.cs
echo '{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}' > "$out/runtime.json"
"$DOTNET" exec --runtimeconfig "$out/runtime.json" "$BURST_RUNTIME/bcl.exe" --platform=Linux --target="$target" --root-assembly="$out/Kernel.dll" --assembly-folder="$GAME_MANAGED" --output="$out/kernel" --temp-folder="$PWD/temp/$target" --threads=1 --verbose --nolink --keep-intermediate-files > "$out/compile.log" 2>&1
"$BURST_RUNTIME/burst-lld-19-hostlin" -flavor gnu --shared --soname=kernel.so --no-undefined --gc-sections --build-id -o "$out/kernel.so" "$BURST_RUNTIME/libs/burstRTL_l64.a" "$out"/*.o --threads=1
cc -O2 -std=gnu11 -shared -fPIC callbacks.c -o callbacks.so
