"""Prepare isolated sources for the overlap-gating control."""
import argparse
import shutil
import subprocess
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("output", type=Path)
args = parser.parse_args()
here = Path(__file__).resolve().parent
root = here.parents[2]
out = args.output.resolve()
out.mkdir(parents=True, exist_ok=False)
for mode in ("gated", "noop"):
    src = out / mode / "src"
    src.mkdir(parents=True)
    for path in (root / "src").glob("*.cs"):
        original = subprocess.check_output(["git", "-C", root, "show", "6c4c7f4:src/" + path.name])
        (src / path.name).write_bytes(original)
    subprocess.run(["patch", "--batch", "-p1", "-i", here / (mode + ".patch")], cwd=src.parent, check=True)
fixture = out / "fixture"
fixture.mkdir()
shutil.copy(root / "tests/park_overlap/Program.cs", fixture)
project = (root / "tests/park_overlap/ParkOverlap.csproj").read_text()
project = "\n".join(line for line in project.splitlines() if "<Compile Include=" not in line)
(fixture / "ParkOverlap.csproj").write_text(project + "\n")
for path in ("game_compare/Inputs.cs", "burst_compare/VerifiedWorkers.cs", "burst_compare/NativeWorker.cs"):
    shutil.copy(root / "tests" / path, fixture)
subprocess.run(["patch", "--batch", "-p1", "-i", here / "fixture.patch"], cwd=out, check=True)
