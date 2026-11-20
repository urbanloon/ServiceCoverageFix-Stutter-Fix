"""Exercise the unmodified release solution, generator and copy-out with game references outside a Windows game install."""
import argparse
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("managed", type=Path)
parser.add_argument("output", type=Path, help="New directory for the .NET SDK artifacts")
parser.add_argument("--dotnet", default="dotnet", help="Path to the .NET 8+ SDK host")
parser.add_argument("--decompiler-dir", type=Path, help="Optional offline ILSpyCmd 9.1.0.7988 tool directory")
args = parser.parse_args()
repo = Path(__file__).resolve().parents[2]
out = args.output.resolve()
managed = args.managed.resolve()
if out.is_relative_to(managed) or out.is_relative_to(repo):
    parser.error("Select an output outside the game references and the extracted source tree.")
out.mkdir(parents=True, exist_ok=False)
dotnet = shutil.which(args.dotnet)
if not dotnet:
    parser.error("Could not find the .NET SDK host.")
env = dict(os.environ)
env["PATH"] = str(Path(dotnet).resolve().parent) + os.pathsep + env.get("PATH", "")
env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
with tempfile.TemporaryDirectory(prefix="scf-template-") as tmp:
    # Substitute only the toolchain-owned imports. The project, source, generators,
    # restored package dependencies and SDK build/copy targets are the release ones.
    props = Path(tmp) / "Mod.props"
    props.write_text("""<Project>
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <AssemblySearchPaths>$(GameManagedPath);$(AssemblySearchPaths)</AssemblySearchPaths>
    <ModPropsFile>$(MSBuildThisFileDirectory)Mod.props</ModPropsFile>
    <ModTargetsFile>$(MSBuildThisFileDirectory)Mod.targets</ModTargetsFile>
    <DebugType>portable</DebugType>
  </PropertyGroup>
  <ItemGroup><Reference Include="netstandard" HintPath="$(GameManagedPath)/netstandard.dll" Private="false" /></ItemGroup>
</Project>""")
    (Path(tmp) / "Mod.targets").write_text("<Project />")
    command = [dotnet, "build", str(repo / "ServiceCoverageFix.sln"), "-c", "Release",
               "--artifacts-path", str(out), "-p:CSII_TOOLPATH=" + tmp, "-p:GameManagedPath=" + str(managed),
               "--nologo"]
    if args.decompiler_dir:
        command.append("-p:DecompilerDir=" + str(args.decompiler_dir.resolve()))
    target = out / "bin" / "ServiceCoverageFix" / "release"
    for stage in ["clean", "incremental"]:
        with (out / (stage + ".log")).open("w") as log:
            subprocess.run(command, env=env, stdout=log, stderr=subprocess.STDOUT, check=True)
        required = ["ServiceCoverageFix.dll", "ServiceCoverageFix.pdb", "0Harmony.dll", "LICENSE", "THIRD_PARTY_NOTICES.txt"]
        missing = [name for name in required if not (target / name).is_file()]
        if missing or (target / "Game.dll").exists() or (target / "ICSharpCode.Decompiler.dll").exists():
            raise RuntimeError(f"Output verification failed; missing {missing!r}.")
        evidence = [(name, (target / name).stat().st_mtime_ns, hashlib.sha256((target / name).read_bytes()).hexdigest())
                    for name in required]
        if stage == "clean":
            first = evidence
        elif evidence != first:
            raise RuntimeError("An unchanged incremental build rewrote release output.")
    print("Clean build and unchanged incremental output:", target)
