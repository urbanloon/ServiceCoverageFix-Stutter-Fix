"""Read-only inspection of saved road coverage, using the existing save decoder."""
import argparse
import csv
from pathlib import Path
import struct
import sys
import zipfile

import zstandard

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'trace-profile'))
from count_save_providers import (EXCLUDED, find_member, inspect_save, read_block,
                                  read_exact, require, table)


def road_buffers(path):
    metadata = inspect_save(path)
    total = road_tag = park_nonzero = preview_nonzero = nine_slots = 0
    with zipfile.ZipFile(path) as archive, archive.open(find_member(archive, '.SaveGameData')) as stream:
        decoder = zstandard.ZstdDecompressor()
        size, = struct.unpack('<i', read_exact(stream, 4))
        require(size >= 0, 'Invalid context size')
        read_exact(stream, size)
        types = []
        for record in table(read_block(stream, decoder, decode=True)):
            kind = record.take(1)[0]
            name = bytes(record.take(record.integer())).decode().split(',', 1)[0]
            types.append((name, kind))
        read_block(stream, decoder, decode=False)
        archetypes = read_block(stream, decoder, decode=True)
        for archetype in table(archetypes):
            count, type_count = archetype.integer(), archetype.integer()
            require(min(count, type_count) >= 0, 'Invalid archetype size')
            indices = [archetype.integer() for _ in range(type_count)]
            require(all(0 <= i < len(types) for i in indices), 'Invalid type')
            fields = [types[i] for i in indices]
            names = {name for name, _ in fields}
            keep = {'Game.Net.Edge', 'Game.Net.ServiceCoverage'} <= names and EXCLUDED.isdisjoint(names)
            block = read_block(stream, decoder, decode=keep)
            if not keep:
                continue
            total += count
            if 'Game.Net.Road' in names:
                road_tag += count
            for name, kind in fields:
                if kind == 1:
                    continue
                record = block.record()
                if name != 'Game.Net.ServiceCoverage':
                    continue
                for _ in range(count):
                    length = record.integer()
                    require(length >= 0, 'Invalid buffer length')
                    values = record.take(8 * length)
                    nine_slots += int(length == 9)
                    if length > 3:
                        park_nonzero += int(any(struct.unpack_from('<ff', values, 3 * 8)))
                    if length > 8:
                        preview_nonzero += int(any(struct.unpack_from('<ff', values, 8 * 8)))
                record.done()
            block.done()
    return (metadata['save_id'], metadata['saved_population'], metadata['park'], total,
            road_tag, nine_slots, park_nonzero, preview_nonzero)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('saves', nargs='+', type=Path)
    writer = csv.writer(sys.stdout, lineterminator='\n')
    writer.writerow(('save_id', 'population', 'park_candidates', 'coverage_edges', 'with_road_tag',
                     'with_nine_slots', 'park_slot_nonzero', 'preview_slot_nonzero'))
    for path in parser.parse_args().saves:
        writer.writerow(road_buffers(path))
