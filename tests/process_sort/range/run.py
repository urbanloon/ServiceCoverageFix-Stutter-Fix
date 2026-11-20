"""Compare exact range arithmetic and Unity ordering on an extracted fixture."""
import argparse
import csv
import ctypes
import hashlib
import random
import struct
import sys
import time
from pathlib import Path

import numpy as np


def load_worker(directory, target):
    path = directory / "aot" / target
    symbol = (path / "symbol").read_text().strip()
    worker = getattr(ctypes.CDLL(str(path / "process.so")), symbol)
    worker.argtypes = [ctypes.c_void_p] * 6 + [ctypes.c_int] * 3
    worker.restype = None
    return worker


def fixture(path, shape):
    raw = path.read_bytes()
    roads, providers = struct.unpack_from("<ii", raw)
    entries = np.frombuffer(raw, offset=8, count=roads * 13, dtype="<f4").reshape(roads, 13)
    density = entries[:, 0].copy()
    curves = np.ascontiguousarray(entries[:, 1:])
    magnitudes = np.frombuffer(raw, offset=8 + roads * 52, dtype="<f4").copy()
    assert len(magnitudes) == providers
    efficiency = np.ones(providers, dtype=np.float32)
    rng = np.random.default_rng(50123)
    geometry = np.zeros((roads, 3), dtype=np.float32)
    geometry[:, 0] = density
    if shape == "fuzz":
        providers, roads = 51, 4096
        geometry = np.zeros((roads, 3), dtype=np.float32)
        geometry[:, 0] = rng.integers(0, 2**32, roads, dtype=np.uint32).view("<f4")
        special = np.asarray([0, 0x80000000, 0x7f800000, 0xff800000, 0x7fc01234, 0x7fa00000,
                              0xff801000, 0x3c23d709, 0x3c23d70a, 0x3c23d70b, 1, 0x80000001],
                             dtype=np.uint32).view("<f4")
        geometry[:len(special), 0] = special
        curves = np.resize(curves, (roads, 12))
        magnitudes = np.resize(special, providers)
        efficiency = np.resize(special[::-1], providers)
        starts = np.arange(providers + 1, dtype=np.int32) * roads
    else:
        starts = np.linspace(0, 1111964, providers + 1).astype(np.int32)

    paths = np.zeros(starts[-1], dtype=[("edge", "<i4"), ("cost", "<f4", 2)])
    centers = (curves[:, :3] + curves[:, 9:]) / 2
    for start, end in zip(starts, starts[1:]):
        size = end - start
        if shape == "fuzz":
            edges = rng.permutation(roads)
            costs = rng.integers(0, 2**32, (size, 2), dtype=np.uint32).view("<f4")
        elif shape == "local":
            distances = np.linalg.norm(centers - centers[rng.integers(roads)], axis=1)
            edges = np.argpartition(distances, size)[:size]
            rng.shuffle(edges)
            normalized = distances[edges, None] / np.maximum(distances[edges].max(), 1)
            costs = np.minimum(normalized + rng.random((size, 2)) * 0.025, 1.2)
        else:
            edges = rng.choice(roads, size, replace=False)
            costs = rng.random((size, 2))
        paths["edge"][start:end] = edges
        paths["cost"][start:end] = costs
    return geometry, curves, magnitudes, efficiency, starts, paths


def measure(directory, input_path, target, shape, rounds, writer):
    worker = load_worker(directory, target)
    geometry, curves, mags, efficiency, starts, paths = fixture(input_path, shape)
    output = np.zeros((starts[-1], 32), dtype=np.uint8)
    args = [array.ctypes.data_as(ctypes.c_void_p) for array in (output, paths, geometry, starts, mags, efficiency)]
    setup = args.copy()
    setup[1] = curves.ctypes.data_as(ctypes.c_void_p)
    worker(*setup, len(geometry), 4, 0)
    worker(*args, len(mags), 3, 0)

    for sort in (0, 1):
        worker(*args, len(mags), 0, sort)
        expected = hashlib.sha256(output).digest()
        methods = [0, 1, 2] + ([3] if sort else [])
        for method in methods[1:]:
            worker(*args, len(mags), method if method < 3 else 0, sort if method < 3 else 2)
            if hashlib.sha256(output).digest() != expected:
                raise ValueError((target, shape, sort, method))
        if shape == "fuzz":
            continue
        rng = random.Random(421 + sort)
        for pair in range(rounds):
            order = methods.copy()
            rng.shuffle(order)
            for method in order:
                start_wall = time.perf_counter_ns()
                start_cpu = time.thread_time_ns()
                worker(*args, len(mags), method if method < 3 else 0, sort if method < 3 else 2)
                cpu = time.thread_time_ns() - start_cpu
                wall = time.perf_counter_ns() - start_wall
                writer.writerow([target, shape, sort, pair, method, f"{cpu/1e6:.6f}", f"{wall/1e6:.6f}"])
    print("verified", target, shape, file=sys.stderr)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("workers", type=Path)
    parser.add_argument("fixture", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--rounds", type=int, default=18)
    args = parser.parse_args()
    with args.output.open("w", newline="") as output:
        writer = csv.writer(output)
        writer.writerow(["target", "shape", "sort", "pair", "mode", "cpu_ms", "wall_ms"])
        for target in ("AVX2", "X64_SSE4"):
            for shape in ("fuzz", "local", "random"):
                measure(args.workers.resolve(), args.fixture, target, shape, args.rounds, writer)
