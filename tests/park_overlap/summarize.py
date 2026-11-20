"""Summarize matched worker observations. Each CSV must contain a complete run."""
import argparse
import csv
from collections import defaultdict
from pathlib import Path
from statistics import median
import sys


def summarize(file):
    by_shape = defaultdict(lambda: defaultdict(dict))
    counts = {}
    with file.open() as stream:
        for row in csv.DictReader(stream):
            key = row['seed'], row['shape']
            counts[key] = int(row['processed'])
            workers = by_shape[key][int(row['round'])]
            name = row['worker']
            if name in workers:
                raise ValueError('Duplicate worker row')
            workers[name] = float(row['thread_ms'])
    for (seed, shape), rounds in by_shape.items():
        names = set(next(iter(rounds.values())))
        if any(set(observation) != names for observation in rounds.values()):
            raise ValueError('Incomplete round in ' + str(file))
        for name in sorted(names):
            times = [r[name] for r in rounds.values()]
            paired = sorted(r['v036'] / r[name] for r in rounds.values())
            yield (file.stem, seed, shape, counts[seed, shape], name, len(times),
                   f'{median(times):.5f}', f'{median(paired):.4f}',
                   f'{paired[(len(paired)-1)//4]:.4f}', f'{paired[3*(len(paired)-1)//4]:.4f}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('files', nargs='+', type=Path)
    writer = csv.writer(sys.stdout, lineterminator='\n')
    writer.writerow(('run', 'seed', 'shape', 'visited', 'worker', 'rounds', 'median_thread_ms',
                     'paired_v036_over_worker', 'lower_quartile_ratio', 'upper_quartile_ratio'))
    for path in parser.parse_args().files:
        writer.writerows(summarize(path))
