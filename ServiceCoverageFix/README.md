# Service Coverage Fix

This is a Cities: Skylines II code-mod project created from the official Visual
Studio template. Version 0.1.8 is a performance/diagnostic build of the v0.1.4
fix. It replaces the pathological final merge in
`Game.Simulation.ServiceCoverageSystem.ApplyCoverageJob` with an equivalent
monotone radix-queue merge.

The game still runs its original Prepare, Clear, and Process jobs. The patch
changes only the ordering data structure in the Apply stage:

- original: sorted array plus repeated linear record shifts, approaching
  `O(E * B)`;
- v0.1.3 replacement: the same k-way merge driven by a max heap,
  `O(E log B)`;
- v0.1.4 replacement: the same merge driven by the already-sorted float keys
  through a 33-bucket radix queue, avoiding the remaining `log B` comparison
  tree and 24-byte heap record moves.

v0.1.4 validated every provider range and used the v0.1.3 heap on unexpected
input. v0.1.7 removes that redundant full scan for the exact hash-locked game
build: its immediately preceding Process job is the code that establishes the
ordering. An incompatible game update is still rejected before patching.

The mod has no settings page. Version 0.1.8 adds temporary keyboard controls for
repeatable diagnostics without reloading the game.

## v0.1.8 runtime controls

### Coverage logging: Ctrl+Shift+F9

Press `Ctrl+Shift+F9` once to begin a new logging session. Press the same hotkey
again to stop it and flush the last pending result. Logging is disabled at mod
load and there is no 24-pass limit.

Each Apply schedule in the active session logs the exact live input shape as:

`Coverage input pass NNNN (session SS): providers=B, elements=E, processed=P`

The Prepare dependency fills these lists after Apply is scheduled. The
diagnostic captures the two lengths at the beginning of the Burst job and the
processed-element count at its end, then reports the completed result from the
managed bridge on a later pass.

### Destructive park test: hold Ctrl+Shift+F10 for 3 seconds

This hotkey marks all currently loaded entities carrying any of these exact
components as deleted:

- `Game.Buildings.Park` (ordinary parks and park extensions);
- `Game.Buildings.ParkMaintenance` (maintenance facilities);
- `Game.Vehicles.ParkMaintenanceVehicle` (active maintenance vehicles).

The game performs its normal `Game.Common.Deleted` cleanup; the mod does not
call `EntityManager.DestroyEntity` directly. The hold requirement prevents an
accidental tap from changing the city. **Use a disposable copy of the save and
do not save over the original city after running this test.**

The intended test sequence is: start logging, collect at least one complete
eight-service rotation, execute the deletion hotkey, collect another complete
rotation, then stop logging. This provides a within-session before/after result.

## v0.1.7 fast path retained

Providence Bay's Park pass contains 523 providers and 1,467,894 elements. The
v0.1.4 safety gate redundantly scanned every element to reconfirm the descending
ordering already established by the immediately preceding ProcessCoverageJob.
Because this mod is locked to the exact analyzed Game.dll and both bad-save
traces proved that invariant, v0.1.7 removes that duplicate full scan. The radix
merge, tie ordering, and coverage calculations are unchanged.

The v0.1.4 C# sources compiled successfully against the exact supplied game,
Unity, Colossal, and Harmony 2.2.2 assemblies. The v0.1.8 source retains the
fast path and passes the algorithm-equivalence suite. It requires the normal
local Visual Studio build/Burst post-processing before testing. Harmony is
copied into the local mod output by this project; the old CS2 DependencyPack
subscription is not required.
Version 2.2.2 is used because CS2's Burst 1.8.23 build step cannot parse Harmony
2.4.2's newer merged assembly format.

## Compatibility and failure behavior

This build is deliberately locked to the analyzed `Game.dll` SHA-256:

`721e7e17bf74299aa2b988c1bd07e90874bb8bc72d263229500c4bf639e7e4ee`

It also validates the private job name, both `NativeList` fields, their element
types and sizes, and the unique schedule call. If any check fails, the patch is
not installed. If the runtime bridge ever fails for an individual update, that
update schedules the game's original job.

## Build and local test

1. Open `ServiceCoverageFix.sln` in the same Visual Studio installation where
   the Cities: Skylines II mod template works.
2. Select **Release** and choose **Rebuild Solution**. Visual Studio will restore
   Harmony 2.2.2 from NuGet and package it beside the mod DLL. If that copy does
   not happen, the build deliberately stops with a clear error.
3. Use the template's normal local mod deployment/debug command.
4. Load Providence Bay or Loon Lake, leave the camera stationary at 1x, and
   observe for at least 45 seconds.

The successful load message begins:

`Enabled ServiceCoverageFix 0.1.8.0 radix diagnostic-control build.`

Do not remove the hash gate to make this run on a later game build. Re-analyze
the updated managed and Burst bodies first.

`ANALYSIS.md` contains the binary proof, trace evidence, save comparison, and
confidence classification. `tests/reference_model.py` is the deterministic
algorithm-equivalence test. The Python script under `tools` reproduces the
targeted save query comparison and is not part of the mod.
