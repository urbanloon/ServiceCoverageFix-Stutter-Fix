"""Verify each manifest hash resolves to the listed RVA in the archived PE."""
import csv
import hashlib
from pathlib import Path
import re
import subprocess
import sys

image=Path(sys.argv[1])
with image.open("rb") as stream:
    if hashlib.file_digest(stream,"sha256").hexdigest()!="cb38360072a9a7fed7b06b0b745b0a0300dee60f4fd284117a16a4fd8dd2190d":
        raise SystemExit("Game Burst image hash mismatch")
dump=subprocess.check_output(["objdump","-p",str(image)],text=True)
rvas=dict(re.findall(r"\[ *(\d+)\] \+base\[ *\d+\]\s+([a-f0-9]+) Export RVA",dump))
exports={name:int(rvas[index],16) for index,name in re.findall(
    r"\[ *(\d+)\] \+base\[ *\d+\]\s+[a-f0-9]+ ([a-f0-9]{32})\n",dump)}
with Path(__file__).resolve().parents[1].joinpath("job-roots.csv").open() as stream:
    for row in csv.DictReader(stream):
        if exports[row["burst_export"]]!=int(row["export_rva"],16):
            raise SystemExit(row["managed_job"]+" RVA mismatch")
        print(row["label"],row["export_rva"])
