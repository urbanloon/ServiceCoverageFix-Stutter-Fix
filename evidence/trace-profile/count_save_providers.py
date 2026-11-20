"""Count saved service-coverage candidates. Requires Python 3 and zstandard."""
import argparse
import csv
import hashlib
import json
from pathlib import Path
import struct
import sys
import zipfile

import zstandard


SERVICES = ('healthcare', 'fire_rescue', 'police', 'park', 'postal', 'education',
            'emergency_shelter', 'welfare')
SERVICE = 'Game.Net.CoverageServiceType'
PATH_BUFFER = 'Game.Pathfind.CoverageElement'
REQUIRED = {SERVICE, PATH_BUFFER, 'Game.Prefabs.PrefabRef'}
EXCLUDED = {'Game.Common.Deleted', 'Game.Tools.Temp'}


def require(condition, message):
    if not condition:
        raise ValueError(message)


class Reader:
    def __init__(self, data):
        self.data = memoryview(data)
        self.pos = 0

    def take(self, count):
        require(0 <= count <= len(self.data) - self.pos, 'Invalid record length')
        start = self.pos
        self.pos += count
        return self.data[start:self.pos]

    def integer(self):
        return struct.unpack('<i', self.take(4))[0]

    def record(self):
        return Reader(self.take(self.integer()))

    def done(self):
        require(self.pos == len(self.data), 'Unexpected trailing record data')


def read_exact(stream, size):
    data = stream.read(size)
    require(len(data) == size, 'Truncated save stream')
    return data


def read_block(stream, decoder, *, decode):
    raw_size, compressed_size = struct.unpack('<ii', read_exact(stream, 8))
    require(raw_size >= 0 and compressed_size >= 0, 'Invalid native block size')
    compressed = read_exact(stream, compressed_size)
    if not decode:
        return None
    if raw_size == 0:
        if compressed_size:
            require(decoder.decompress(compressed) == b'', 'Invalid empty block')
        return Reader(b'')
    data = decoder.decompress(compressed, max_output_size=raw_size)
    require(len(data) == raw_size, 'Native size mismatch')
    return Reader(data)


def table(reader):
    count = reader.integer()
    require(count >= 0, 'Invalid table size')
    for _ in range(count):
        record = reader.record()
        yield record
        record.done()
    reader.done()


def count_native(stream):
    decoder = zstandard.ZstdDecompressor()
    context_size = struct.unpack('<i', read_exact(stream, 4))[0]
    require(context_size >= 0, 'Invalid context size')
    read_exact(stream, context_size)

    types = []
    for record in table(read_block(stream, decoder, decode=True)):
        kind = record.take(1)[0]
        name = bytes(record.take(record.integer())).decode('utf-8').split(',', 1)[0]
        types.append((name, kind))
    by_name = dict(types)
    require(by_name.get(PATH_BUFFER) == 1, 'Expected an empty path-buffer serializer')
    read_block(stream, decoder, decode=False)  # System-type table.
    archetypes = read_block(stream, decoder, decode=True)
    counts = [0] * len(SERVICES)
    tagged_parks = 0
    for archetype in table(archetypes):
        entity_count, type_count = archetype.integer(), archetype.integer()
        require(entity_count >= 0 and type_count >= 0, 'Invalid archetype')
        indices = [archetype.integer() for _ in range(type_count)]
        require(all(0 <= i < len(types) for i in indices), 'Invalid type index')
        fields = [types[i] for i in indices]
        names = {name for name, _ in fields}
        keep = REQUIRED <= names and EXCLUDED.isdisjoint(names)
        block = read_block(stream, decoder, decode=keep)
        if not keep:
            continue
        grouped_entities = 0
        for name, kind in fields:
            if kind == 1:
                continue
            record = block.record()
            if name != SERVICE:
                continue
            require(len(record.data) % 5 == 0, 'Invalid coverage service run')
            for service, length in struct.iter_unpack('<Bi', record.data):
                require(service < len(SERVICES) and length > 0, 'Invalid coverage service')
                counts[service] += length
                grouped_entities += length
                if service == 3 and 'Game.Buildings.Park' in names:
                    tagged_parks += length
        require(grouped_entities == entity_count, 'Service runs do not cover the archetype')
        block.done()
    return counts, tagged_parks


def find_member(archive, suffix):
    matches = [n for n in archive.namelist() if n.endswith(suffix)]
    require(len(matches) == 1, 'Expected one ' + suffix + ' member')
    return matches[0]


def inspect_save(path):
    digest = hashlib.sha256()
    with path.open('rb') as src:
        for chunk in iter(lambda: src.read(1 << 20), b''):
            digest.update(chunk)
    sha = digest.hexdigest()
    with zipfile.ZipFile(path) as archive:
        metadata = json.loads(archive.read(find_member(archive, '.SaveGameMetadata')))
        with archive.open(find_member(archive, '.SaveGameData')) as stream:
            counts, tagged_parks = count_native(stream)
    return {'save_id': 'S-' + sha[:12], 'save_sha256': sha,
            'saved_population': metadata['population'], 'query_candidates': sum(counts),
            **dict(zip(SERVICES, counts)), 'park_candidates_with_park_component': tagged_parks}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('save', type=Path, nargs='+', help='CS2 .cok file')
    samples = sorted((inspect_save(p) for p in parser.parse_args().save),
                     key=lambda row: row['save_id'])
    writer = csv.DictWriter(sys.stdout, fieldnames=samples[0], lineterminator='\n')
    writer.writeheader()
    writer.writerows(samples)
