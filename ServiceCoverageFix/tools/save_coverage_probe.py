#!/usr/bin/env python3
"""Count saved entities that match ServiceCoverageSystem's building query.

Requires the third-party ``zstandard`` Python package. This is an analysis
tool, not part of the mod build.
"""

from __future__ import annotations

import argparse
import collections
import struct
from pathlib import Path

import zstandard


SERVICES = (
    "Healthcare",
    "FireRescue",
    "Police",
    "Park",
    "PostService",
    "Education",
    "EmergencyShelter",
    "Welfare",
)


def read_records(path: Path) -> list[bytes]:
    data = path.read_bytes()
    first_frame = data.find(b"\x28\xb5\x2f\xfd")
    if first_frame < 8:
        raise ValueError(f"No Zstandard buffer record found in {path}")

    offset = first_frame - 8
    records: list[bytes] = []
    decompressor = zstandard.ZstdDecompressor()
    while offset < len(data):
        uncompressed_size, compressed_size = struct.unpack_from("<II", data, offset)
        offset += 8
        if uncompressed_size == 0 and compressed_size == 0:
            records.append(b"")
            continue
        end = offset + compressed_size
        if end > len(data):
            raise ValueError("Truncated compressed buffer record")
        record = decompressor.decompress(
            data[offset:end], max_output_size=uncompressed_size
        )
        if len(record) != uncompressed_size:
            raise ValueError("Unexpected decompressed buffer size")
        records.append(record)
        offset = end
    return records


def read_component_types(record: bytes) -> list[tuple[int, str]]:
    count = struct.unpack_from("<I", record)[0]
    offset = 4
    result: list[tuple[int, str]] = []
    for _ in range(count):
        record_size = struct.unpack_from("<I", record, offset)[0]
        payload = offset + 4
        serializer_type = record[payload]
        name_size = struct.unpack_from("<I", record, payload + 1)[0]
        name = record[payload + 5 : payload + 5 + name_size].decode("utf-8")
        if record_size != 5 + name_size:
            raise ValueError("Unexpected component-type record size")
        result.append((serializer_type, name.split(",", 1)[0]))
        offset = payload + record_size
    if offset != len(record):
        raise ValueError("Trailing bytes in component-type table")
    return result


def read_archetypes(record: bytes) -> list[tuple[int, list[int]]]:
    count = struct.unpack_from("<I", record)[0]
    offset = 4
    result: list[tuple[int, list[int]]] = []
    for _ in range(count):
        record_size = struct.unpack_from("<I", record, offset)[0]
        payload = offset + 4
        entity_count, serializer_count = struct.unpack_from("<II", record, payload)
        serializer_indices = list(
            struct.unpack_from(f"<{serializer_count}I", record, payload + 8)
        )
        if record_size != 8 + serializer_count * 4:
            raise ValueError("Unexpected archetype record size")
        result.append((entity_count, serializer_indices))
        offset = payload + record_size
    if offset != len(record):
        raise ValueError("Trailing bytes in archetype table")
    return result


def find_component_payload(
    record: bytes,
    serializer_indices: list[int],
    component_types: list[tuple[int, str]],
    target_index: int,
) -> bytes:
    offset = 0
    for serializer_index in serializer_indices:
        # ComponentSerializerType.Empty is the value 1 and consumes no buffer.
        if component_types[serializer_index][0] == 1:
            continue
        size = struct.unpack_from("<I", record, offset)[0]
        offset += 4
        payload = record[offset : offset + size]
        offset += size
        if serializer_index == target_index:
            return payload
    raise ValueError("Target serializer has no component payload")


def analyze(path: Path) -> tuple[int, collections.Counter[int]]:
    records = read_records(path)
    component_types = read_component_types(records[0])
    archetypes = read_archetypes(records[2])
    indices = {name: i for i, (_, name) in enumerate(component_types)}

    service_type = indices["Game.Net.CoverageServiceType"]
    coverage_element = indices["Game.Pathfind.CoverageElement"]
    prefab_ref = indices["Game.Prefabs.PrefabRef"]
    excluded = {
        indices.get("Game.Common.Deleted"),
        indices.get("Game.Tools.Temp"),
    }

    candidate_count = 0
    service_counts: collections.Counter[int] = collections.Counter()
    for archetype_index, (entity_count, serializer_indices) in enumerate(archetypes):
        if not all(
            item in serializer_indices
            for item in (service_type, coverage_element, prefab_ref)
        ):
            continue
        if any(item in serializer_indices for item in excluded if item is not None):
            continue

        candidate_count += entity_count
        payload = find_component_payload(
            records[3 + archetype_index],
            serializer_indices,
            component_types,
            service_type,
        )
        if len(payload) % 5 != 0:
            raise ValueError("Unexpected CoverageServiceType shared-component payload")

        payload_total = 0
        for offset in range(0, len(payload), 5):
            service = payload[offset]
            count = struct.unpack_from("<I", payload, offset + 1)[0]
            service_counts[service] += count
            payload_total += count
        if payload_total != entity_count:
            raise ValueError("Shared-component run counts do not match archetype size")

    return candidate_count, service_counts


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("saves", nargs="+", type=Path)
    args = parser.parse_args()
    for save in args.saves:
        candidate_count, counts = analyze(save)
        print(f"{save.name}: {candidate_count} query candidates")
        for service, name in enumerate(SERVICES):
            print(f"  {name:18} {counts[service]:6}")


if __name__ == "__main__":
    main()
