"""Compile the mod against installed game assemblies for the isolated offline tests."""
import argparse
import subprocess
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("managed", type=Path, help="Cities2_Data/Managed directory")
parser.add_argument("harmony", type=Path, help="Lib.Harmony 2.2.2 net48/0Harmony.dll")
parser.add_argument("output", type=Path, help="Destination ServiceCoverageFix.dll")
parser.add_argument("--dotnet", default="dotnet")
parser.add_argument("--csc", type=Path, required=True, help="SDK Roslyn/bincore/csc.dll")
parser.add_argument("--src", type=Path, default=Path(__file__).resolve().parents[1] / "src")
args = parser.parse_args()

references = ["mscorlib", "System", "System.Core", "netstandard", "Game", "Colossal.Core",
              "Colossal.Logging", "UnityEngine.CoreModule", "Unity.Collections", "Unity.Mathematics",
              "Unity.Burst", "Unity.Entities", "UnityEngine", "Colossal.IO.AssetDatabase",
              "Colossal.Localization", "Colossal.UI.Binding", "Colossal.UI", "Colossal.Mono.Cecil"]
args.output.parent.mkdir(parents=True, exist_ok=True)
command = [args.dotnet, str(args.csc), "-nologo", "-target:library", "-optimize+", "-deterministic+",
           "-unsafe", "-nullable:enable", "-langversion:latest", "-nostdlib+",
           "-out:" + str(args.output.resolve())]
command.extend("-r:" + str((args.managed / (name + ".dll")).resolve()) for name in references)
command.append("-r:" + str(args.harmony.resolve()))
command.extend(str(path) for path in sorted(args.src.resolve().glob("*.cs")))
subprocess.run(command,check=True)
