"""Compile just one private vanilla or mod IJob producer with the Unity Burst AOT compiler."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile

def run(arguments, log=None):
    result = subprocess.run(arguments, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    if log is not None:
        log.write_text(result.stdout)
    if result.returncode:
        print(result.stdout, file=sys.stderr)
        result.check_returncode()
    return result.stdout

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("kind", choices=["vanilla", "mod"])
    parser.add_argument("managed", type=Path, help="Cities2_Data/Managed directory")
    parser.add_argument("assembly", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--burst-runtime", type=Path, required=True, help="com.unity.burst@1.8.23/.Runtime")
    parser.add_argument("--harmony-dir", type=Path)
    parser.add_argument("--dotnet10", default="dotnet", help="dotnet host with .NET 10 installed")
    parser.add_argument("--platform", choices=["Linux", "Windows"], default="Linux")
    parser.add_argument("--target", choices=["X64_SSE4", "AVX2"], default="X64_SSE4")
    args = parser.parse_args()
    burst, managed, assembly, output = [p.resolve() for p in (args.burst_runtime, args.managed, args.assembly, args.output)]
    output.mkdir(parents=True, exist_ok=True)
    if any((output / f).exists() for f in ("coverage.so","coverage.dll","worker.json")):
        parser.error("Choose a fresh output directory to keep the manifest and producer together.")
    if args.kind == "vanilla" and assembly != (managed / "Game.dll").resolve():
        parser.error("For vanilla use the Game.dll from the selected Managed directory.")
    runtimeconfig = {"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}
    with tempfile.TemporaryDirectory() as temp:
        config_path = Path(temp) / "burst.runtimeconfig.json"
        config_path.write_text(json.dumps(runtimeconfig))
        command = [args.dotnet10, "exec", "--runtimeconfig", config_path, burst / "bcl.exe",
                   "--platform=" + args.platform, "--target=" + args.target, "--assembly-folder=" + str(managed),
                   "--assembly-folder=" + str(assembly.parent)]
        if args.harmony_dir:
            command.append("--assembly-folder=" + str(args.harmony_dir.resolve()))
        if args.kind == "vanilla":
            method = Path(__file__).with_name("vanilla-method.txt").read_text().strip()
            command.append("--method=" + method)
        else:
            command.append("--root-assembly=" + str(assembly))
        command += ["--output=" + str(output / "coverage"), "--temp-folder=" + temp,
                    "--threads=2", "--verbose", "--nolink", "--keep-intermediate-files"]
        report = run(command, output / "compiler.log")
    method, = [line for line in report.splitlines() if line.startswith("Method: ")]
    symbol = method.rsplit("--", 1)[-1].strip()
    if args.kind == "vanilla" and "ServiceCoverageSystem/ApplyCoverageJob>::Execute(" not in method:
        raise ValueError("Unexpected vanilla producer: " + method)
    if args.kind == "mod" and "OptimizedApplyCoverageJob>::Execute(" not in method:
        raise ValueError("Unexpected mod producer: " + method)
    linker = burst / "burst-lld-19-hostlin"
    if args.platform == "Linux":
        binary = output / "coverage.so"
        run([linker,"-flavor","gnu","--shared","--soname=coverage.so","--no-undefined","--gc-sections",
             "--build-id","-o",binary,burst / "libs/burstRTL_l64.a",*sorted(output.glob("*.o")),"--threads=1"])
    else:
        binary = output / "coverage.dll"
        run([linker,"-flavor","link","/dll","/noentry","/nodefaultlib","/out:" + str(binary),
             *sorted(output.glob("*.obj")),burst / "libs/burstRTL_w64.lib"])
    game = managed / "Game.dll"
    digest = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()
    manifest = {"assembly":str(assembly),"sourceSha256":digest(assembly),"kind":args.kind,"gameSha256":digest(game),
                "symbol":symbol,"binarySha256":digest(binary),"platform":args.platform,"target":args.target}
    (output / "worker.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print(json.dumps(manifest))

if __name__ == "__main__":
    main()
