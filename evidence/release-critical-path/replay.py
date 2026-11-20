"""Replay matched, decompressed ETL inputs into an anonymized output directory."""
import argparse
import hashlib
import subprocess
from pathlib import Path
import sys

INPUTS = {
    "C08": "1ff6431f55a159748044917b618e3f601cc3eb60ae8d194e961968eb3d444b79",
    "C09": "4248a3cb70349cfa6dd6813e418c52a95ab4cf27a9d8e75230ae7761139dac40",
    "C05": "48292c0fe2212556f50d98f23e1375c27e30a70b3fefa6fe7e7389fac0d23cb6",
}


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    for name in INPUTS:
        parser.add_argument("--"+name.lower(),type=Path,required=name!="C05")
    parser.add_argument("--scratch",required=True,type=Path,help="Separate, empty directory for private intermediate files")
    parser.add_argument("--out",required=True,type=Path,help="Directory for public CSVs")
    args=parser.parse_args()
    code=Path(__file__).resolve().parent
    scratch=args.scratch.resolve()
    output=args.out.resolve()
    if scratch==output or output in scratch.parents or scratch in output.parents:
        parser.error("Use separate non-overlapping private and public directories")
    scratch.mkdir(parents=True,exist_ok=True)
    if any(scratch.iterdir()):
        parser.error("Choose an empty scratch directory for this replay")
    binary=scratch/"read_release_trace"
    subprocess.run(["c++","-std=c++17","-O2",str(code/"read_release_trace.cpp"),"-o",str(binary)],check=True)
    for name,expected in INPUTS.items():
        path=getattr(args,name.lower())
        if path is None:
            continue
        with path.open("rb") as stream:
            actual=hashlib.file_digest(stream,"sha256").hexdigest()
        if actual!=expected:
            raise SystemExit(f"{name} input hash mismatch")
        subprocess.run([str(binary),str(path),str(scratch/name)],check=True)
    subprocess.run([sys.executable,str(code/"summarize_wakes.py"),str(scratch),str(output)],check=True)


if __name__=="__main__":
    main()
