"""Extract fixture geometry and park magnitudes from the archived Loon Lake save."""
import argparse
import struct
import sys
import zipfile
from pathlib import Path

import numpy as np
import zstandard

sys.path.insert(0, str(Path(__file__).resolve().parents[3] / "evidence/trace-profile"))
from count_save_providers import EXCLUDED, REQUIRED, SERVICE, find_member, read_block, read_exact, table


def extract(save, output):
    with zipfile.ZipFile(save) as archive, archive.open(find_member(archive, ".SaveGameData")) as stream:
        decoder = zstandard.ZstdDecompressor()
        context_size, = struct.unpack("<i", read_exact(stream, 4))
        read_exact(stream, context_size)
        types = []
        for record in table(read_block(stream, decoder, decode=True)):
            kind = record.take(1)[0]
            name = bytes(record.take(record.integer())).decode().split(",", 1)[0]
            types.append((name, kind))
        read_block(stream, decoder, decode=False)
        archetypes = read_block(stream, decoder, decode=True)
        geometry = []
        modifiers = []
        for record in table(archetypes):
            count = record.integer()
            fields = [types[record.integer()] for _ in range(record.integer())]
            names = {name for name, _ in fields}
            road = "Game.Net.ServiceCoverage" in names
            provider = REQUIRED <= names and EXCLUDED.isdisjoint(names)
            block = read_block(stream, decoder, decode=road or provider)
            if block is None:
                continue
            data = {name: block.record().data for name, kind in fields if kind != 1}
            block.done()

            if road:
                # Curve stores four float3 control points using stride 12. Undo
                # its filtered byte deltas and deinterleave the 12 byte planes.
                encoded = np.frombuffer(data["Game.Net.Curve"], dtype=np.uint8)
                decoded = np.cumsum(encoded, dtype=np.uint8).reshape(12, -1).T.copy()
                curves = decoded.view("<f4").reshape(count, 12)
                density = np.frombuffer(data["Game.Net.Density"], dtype="<f4")
                assert np.all(np.isfinite(curves)) and density.shape == (count,)
                geometry.append(np.column_stack((density, curves)))

            if provider:
                services = [service for service, length in struct.iter_unpack("<Bi", data[SERVICE])
                            for _ in range(length)]
                assert len(services) == count
                park = np.asarray(services) == 3
                if np.any(park):
                    assert {"Game.Areas.ServiceDistrict", "Game.Common.Owner"}.isdisjoint(names)
                    coverage = np.frombuffer(data["Game.Buildings.ModifiedServiceCoverage"], dtype="<f4")
                    modifiers.append(coverage.reshape(count, 3)[park])

    geometry = np.concatenate(geometry)
    modifiers = np.concatenate(modifiers)
    assert geometry.shape == (6128, 13) and modifiers.shape == (533, 3)
    assert np.all(modifiers[:, 0] > 0) and np.all(modifiers[:, 2] > 0)
    assert np.count_nonzero(geometry[:, 0] <= np.float32(0.01)) == 3713
    with output.open("wb") as target:
        target.write(struct.pack("<ii", len(geometry), len(modifiers)))
        target.write(geometry.astype("<f4").tobytes())
        target.write(modifiers[:, 2].astype("<f4").tobytes())


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("save", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    extract(args.save, args.output)
