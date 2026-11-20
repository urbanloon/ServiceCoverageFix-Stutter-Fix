"""Compile and link every discovered release job on Windows x64 using the Unity Burst Linux-hosted tools."""
import argparse
import hashlib
import json
import re
import subprocess
import tempfile
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("managed", type=Path)
parser.add_argument("assembly", type=Path)
parser.add_argument("output", type=Path)
parser.add_argument("--harmony-dir", type=Path, required=True)
parser.add_argument("--burst-runtime", type=Path, required=True, help="com.unity.burst 1.8.23/.Runtime")
parser.add_argument("--dotnet", default="dotnet", help=".NET 10 runtime host")
parser.add_argument("--target", choices=["X64_SSE4", "AVX2"], action="append")
args = parser.parse_args()
runtime = args.burst_runtime.resolve()
required = {"OptimizedApplyCoverageJob", "SellerJob", "HousingJob", "RouteJob", "OptimizedWaterFlowJob"}
for target in (args.target or ["X64_SSE4", "AVX2"]):
    output = args.output.resolve() / target
    output.mkdir(parents=True, exist_ok=False)
    with tempfile.TemporaryDirectory(prefix="scf-burst-") as temp:
        rt = Path(temp) / "burst.runtimeconfig.json"
        rt.write_text(json.dumps({"runtimeOptions": {"tfm": "net10.0", "framework":
                      {"name": "Microsoft.NETCore.App", "version": "10.0.0"}}}))
        command = [args.dotnet, "exec", "--runtimeconfig", str(rt), str(runtime / "bcl.exe"),
                   "--platform=Windows", "--target=" + target, "--root-assembly=" + str(args.assembly.resolve()),
                   "--assembly-folder=" + str(args.managed.resolve()), "--assembly-folder=" + str(args.harmony_dir.resolve()),
                   "--assembly-folder=" + str(args.assembly.resolve().parent), "--output=" + str(output / "workers"),
                   "--temp-folder=" + temp, "--threads=2", "--verbose", "--nolink", "--keep-intermediate-files"]
        with (output / "compiler.log").open("w") as log:
            subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, check=True)
    lines = (output / "compiler.log").read_text().splitlines()
    methods = [re.search(r"\bServiceCoverageFix\.(\w+)>::Execute\(", line).group(1)
               for line in lines if line.startswith("Method: ")]
    if len(methods) != len(required) or set(methods) != required:
        raise RuntimeError(f"Unexpected discovered worker inventory: {methods}")
    dll = output / "workers.dll"
    subprocess.run([str(runtime / "burst-lld-19-hostlin"), "-flavor", "link", "/dll", "/noentry",
                    "/nodefaultlib", "/out:" + str(dll), *sorted(output.glob("*.obj")),
                    str(runtime / "libs/burstRTL_w64.lib")], check=True)
    print(target, dll.stat().st_size, hashlib.sha256(dll.read_bytes()).hexdigest(), ",".join(sorted(methods)), flush=True)
