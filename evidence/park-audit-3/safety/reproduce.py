"""Run the independent Apply holdout against a repository checkout and matching native workers."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

here = Path(__file__).resolve().parent
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("managed", type=Path, help="Original game's Managed folder")
parser.add_argument("candidate_dll", type=Path)
parser.add_argument("vanilla_worker", type=Path)
parser.add_argument("baseline_worker", type=Path, help="0.3.6 native worker directory")
parser.add_argument("candidate_worker", type=Path)
parser.add_argument("--repo", type=Path, default=here.parents[2])
parser.add_argument("--dotnet", default="dotnet")
parser.add_argument("--frontier", action="store_true", help="Run the 24,576 targeted front-three fixtures")
args = parser.parse_args()
managed = args.managed.resolve()
candidate_dll = args.candidate_dll.resolve()
vanilla, baseline, candidate = [p.resolve() for p in (args.vanilla_worker, args.baseline_worker, args.candidate_worker)]
dotnet = args.dotnet
if "/" in dotnet:
    dotnet = str(Path(dotnet).resolve())
with tempfile.TemporaryDirectory(prefix="park-holdout-") as temp:
    root = Path(temp)
    paths = root / "tests"
    for name in ["visit_order", "game_compare", "burst_compare"]:
        shutil.copytree(args.repo / "tests" / name, paths / name, ignore=shutil.ignore_patterns("bin", "obj"))
    fixture = "ThreeFrontier.cs" if args.frontier else "AllCases.cs"
    shutil.copy2(here / fixture, paths / "visit_order/AllCases.cs")
    for name in ["ReadBoundary.cs", "Triad.cs"]:
        shutil.copy2(here / name, paths / "burst_compare")
    program = paths / "game_compare" / "Program.cs"
    text = program.read_text()
    marker = "var jobs = new Jobs(args[0], args[1], baselinePath);"
    assert text.count(marker) == 1
    program.write_text(text.replace(marker, marker + """
if (args.Contains("--guard-stock")) { ReadBoundary.Run(jobs.Vanilla); return 0; }
if (args.Contains("--guard-mod")) { ReadBoundary.Run(jobs.Candidate); return 0; }
if (args.Contains("--triad")) { Triad.Verify(jobs); return 0; }
"""))
    def run(*command, environment=None):
        subprocess.run(command, check=True, env=environment)
    for name in ["visit_order", "burst_compare"]:
        run(dotnet, "build", paths / name, "-c", "Release", f"-p:GameManagedPath={managed}")
    visit = paths / "visit_order/bin/Release/net8.0/VisitOrder.dll"
    native = paths / "burst_compare/bin/Release/net8.0/BurstCompare.dll"
    oracle = root / "visits.csv"
    run(dotnet, visit, managed, candidate_dll, oracle)
    helper = root / "libscf_test_mxcsr.so"
    run("cc", "-O2", "-fPIC", "-shared", paths / "burst_compare/set_denormal_flags.c", "-o", helper)
    env = os.environ.copy()
    for key in ["CS2_BURST_DENORMAL_MODE", "CS2_BURST_WORKLOAD_ORACLE"]:
        env.pop(key, None)
    env["LD_LIBRARY_PATH"] = os.pathsep.join([str(root), env.get("LD_LIBRARY_PATH", "")])
    env["CS2_BURST_VANILLA"] = str(vanilla)
    run(dotnet, native, managed, candidate, "--guard-stock", environment=env)
    run(dotnet, native, managed, baseline, "--guard-mod", environment=env)
    run(dotnet, native, managed, candidate, "--guard-mod", environment=env)
    for mode in ["preserve", "daz", "ftz", "daz-ftz"]:
        mode_env = dict(env, CS2_BURST_DENORMAL_MODE=mode)
        if mode == "preserve":
            mode_env["CS2_BURST_WORKLOAD_ORACLE"] = str(oracle)
        run(dotnet, native, managed, candidate, baseline, "--triad", environment=mode_env)
