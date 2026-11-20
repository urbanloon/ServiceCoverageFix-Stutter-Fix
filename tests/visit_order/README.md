# Complete Apply visit and write order

For each supplied Game.dll, the instrumented vanilla and candidate workers matched on **1,551 deterministic cases, 2,039,931 selected elements and 253,466 float2 stores**. The comparison checks every interleaved event. A visit identifies the original element array index. A store identifies the destination index and both incoming float bit patterns. Each case also checks the final destination bytes, unmodified elements and native buffer guards. The historical and current games ran the same fixtures independently. Both logs identify the final candidate by SHA-256 `ec0f1babf3bda04a71bb81808858112c25816111605ebb48a9628b2b5a2b48ce`, including the correction for denormals-are-zero handling.

The .NET 8 harness builds private, temporary instrumented copies of the supplied assemblies. In vanilla it records the index consumed by the NativeList get_Item instruction at IL_0096. The pattern matches the record subsequently dereferenced through m_CoveragePtr and excludes sorting and lookahead reads. In the candidate every selected element passes through ApplyElement. Its pointer is mapped back to the same original 32-byte element index. Before each worker's unique float2 store, the harness records the address and the incoming 64-bit representation, restores the original stack operands, and executes the original store. It compares full event lists in memory.

The fixtures exercise 0–127 providers, exact ties, a single aliased target, precovered targets, NaN at initial and later heads, and malformed priority ordering. Modes 0 and 3 repeat the same ordinary-input generator. Additional inputs explicitly overlap provider slices, start fully occluded, or contain 526 providers × 970 items. The heavy single-target case performs 510,220 visits with only 14 stores. The two traces distinguish visits that leave the destination unchanged from executed writes even when a write repeats the previous value.

Run from the repository root on Linux x86-64 with a .NET 8 SDK. Set GAME_MANAGED to the absolute path to a supplied Managed directory and CANDIDATE_DLL to the compiled mod. Inputs.cs and Jobs.cs are shared from tests/game_compare.

```bash
dotnet build tests/visit_order/VisitOrder.csproj -c Release -p:GameManagedPath="$GAME_MANAGED"
dotnet tests/visit_order/bin/Release/net8.0/VisitOrder.dll "$GAME_MANAGED" "$CANDIDATE_DLL"
```

current.log and historical.log include the original input hashes and exact counts per fixture group. The probes execute as managed IL on synthetic inputs. No game executable or native timing measurement participates. The temporary game and mod copies are removed at exit. The original assemblies are opened read-only.
