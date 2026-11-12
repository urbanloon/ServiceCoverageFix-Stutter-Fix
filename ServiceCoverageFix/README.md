# Service Coverage Fix

This is a Cities: Skylines II code-mod project created from the official Visual
Studio template. It replaces the pathological final merge in
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

The radix path validates the per-provider monotone ordering and rejects NaNs
before making any coverage writes. If either invariant is ever absent, that
invocation uses the proven v0.1.3 binary heap instead.

The C# sources have been compiled successfully against the exact supplied game,
Unity, Colossal, and Harmony 2.2.2 assemblies. Harmony is copied into the local
mod output by this project; the old CS2 DependencyPack subscription is not
required. Version 2.2.2 is used because CS2's Burst 1.8.23 build step cannot
parse Harmony 2.4.2's newer merged assembly format. Building this solution performs that step and deploys it to the game's local
`Mods` folder.

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

The successful load message is:

`Enabled ServiceCoverageFix 0.1.4.0 monotone-radix scheduling.`

Do not remove the hash gate to make this run on a later game build. Re-analyze
the updated managed and Burst bodies first.

`ANALYSIS.md` contains the binary proof, trace evidence, save comparison, and
confidence classification. `tests/reference_model.py` is the deterministic
algorithm-equivalence test. The Python script under `tools` reproduces the
targeted save query comparison and is not part of the mod.
