"""Compile project source and generated worker against local Cities II references for offline verification."""
import argparse
import subprocess
import tempfile
import xml.etree.ElementTree as ET
from pathlib import Path

project = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("managed", type=Path, help="Cities2_Data/Managed directory")
parser.add_argument("harmony", type=Path, help="Lib.Harmony 2.2.2 net48/0Harmony.dll")
parser.add_argument("output", type=Path)
parser.add_argument("--dotnet", default="dotnet")
parser.add_argument("--csc", type=Path, required=True, help="SDK Roslyn/bincore/csc.dll")
parser.add_argument("--src", type=Path, default=project / "src")
parser.add_argument("--decompiler-dir", type=Path, help="Optional offline copy of ILSpyCmd 9.1.0.7988 tools directory")
args = parser.parse_args()
managed, output, source = (p.resolve() for p in (args.managed, args.output, args.src))
if output.is_relative_to(managed):
    parser.error("Choose a test output outside the installed game references.")
references = ["mscorlib", "System", "System.Core", "netstandard", "Game", "Colossal.Core", "Colossal.Collections",
              "Colossal.Mathematics", "Colossal.Logging", "UnityEngine.CoreModule", "Unity.Collections", "Unity.Mathematics",
              "Unity.Burst", "Unity.Entities", "UnityEngine", "Colossal.IO.AssetDatabase", "Colossal.Localization",
              "Colossal.UI.Binding", "Colossal.UI", "Colossal.Mono.Cecil"]
output.parent.mkdir(parents=True, exist_ok=True)
command = [args.dotnet, str(args.csc.resolve()), "-nologo", "-target:library", "-optimize+", "-deterministic+",
           "-unsafe", "-nullable:enable", "-langversion:latest", "-nostdlib+", "-out:" + str(output)]
command += ["-r:" + str(managed / (name + ".dll")) for name in references]
command.append("-r:" + str(args.harmony.resolve()))
command += [str(path) for path in sorted(source.rglob("*.cs"))]
with tempfile.TemporaryDirectory(prefix="scf-managed-") as temp:
    work = Path(temp)
    # Original 0.3 fixtures use alternate source roots without beta workers.
    if (source / "Beta" / "RouteJob.cs").is_file():
        config = ET.parse(project / "ServiceCoverageFix.csproj")
        constants = config.findtext(".//DefineConstants", default="").split(";")
        if "SCF_BETA" not in constants:
            raise ValueError("Project build symbol SCF_BETA is missing.")
        command.append("-define:SCF_BETA")
        tool_dir = work / "tool"
        build = [args.dotnet, "build", str(project / "tools" / "WorkerGenerator" / "WorkerGenerator.csproj"),
                 "-c", "Release", "--artifacts-path", str(tool_dir), "--nologo"]
        if args.decompiler_dir:
            build.append("-p:DecompilerDir=" + str(args.decompiler_dir.resolve()))
        subprocess.run(build, check=True)
        generated = work / "generated"
        subprocess.run([args.dotnet, str(tool_dir / "bin/WorkerGenerator/release/WorkerGenerator.dll"),
                        str(managed), str(generated)], check=True)
        command += [str(f) for f in sorted(generated.glob("*.g.cs"))]
        version = config.findtext(".//Version")
        version_source = work / "Version.cs"
        version_source.write_text(f'[assembly: System.Reflection.AssemblyVersion("{version}.0")]\n', encoding="utf8")
        command.append(str(version_source))
    subprocess.run(command, check=True)
