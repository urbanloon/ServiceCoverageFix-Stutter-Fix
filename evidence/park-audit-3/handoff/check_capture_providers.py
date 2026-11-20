"""Check for presentation-provider GUIDs anywhere in the four hash-verified ETLs."""
import argparse
import csv
import hashlib
import mmap
from pathlib import Path
import sys
from uuid import UUID

from analyze import CAPTURES, read_rows

PROVIDERS = {
    "DXGI": "ca11c036-0102-4a2d-a6ad-f03cfed5d3c9",
    "DxgKrnl": "802ec45a-1e99-4b83-9920-87c98277ba9d",
    "D3D9": "783aca0a-790e-4d7f-8451-aa850511c6b9",
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", required=True, type=Path)
    parser.add_argument("--inputs", required=True, type=Path)
    args = parser.parse_args()
    reference = read_rows(args.repo / "evidence/trace-profile/captures.csv")
    hashes = {row["capture_id"]: row["trace_sha256"] for row in reference}
    writer = csv.writer(sys.stdout)
    writer.writerow(["capture", "provider", "guid_bytes_present"])
    for capture in CAPTURES:
        path = args.inputs / (capture + ".etl")
        with path.open("rb") as source:
            if hashlib.file_digest(source, "sha256").hexdigest() != hashes[capture]:
                raise ValueError(capture + " hash mismatch")
            with mmap.mmap(source.fileno(), 0, access=mmap.ACCESS_READ) as trace:
                for provider, guid in PROVIDERS.items():
                    writer.writerow([capture, provider, int(trace.find(UUID(guid).bytes_le) >= 0)])


if __name__ == "__main__":
    main()
