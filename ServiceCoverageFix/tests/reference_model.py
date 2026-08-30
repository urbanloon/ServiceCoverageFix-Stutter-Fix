#!/usr/bin/env python3
"""Property tests for the linear-reinsertion, heap, and radix schedulers.

The input to both models is the already-filtered, already-NativeSorted active
list. Ties retain that concrete initial rank. This isolates the transformation
made by the mod from Unity's NativeSort implementation.
"""

from __future__ import annotations

import heapq
import random
import struct
from dataclasses import dataclass


@dataclass
class Provider:
    provider_id: int
    priorities: list[float]
    index: int = 0

    @property
    def priority(self) -> float:
        return self.priorities[self.index]


def original(initial: list[Provider]) -> tuple[list[tuple[int, float]], int]:
    active = [Provider(x.provider_id, x.priorities.copy()) for x in initial]
    output: list[tuple[int, float]] = []
    shifts = 0
    first = 0

    while first < len(active):
        current = active[first]
        output.append((current.provider_id, current.priority))
        current.index += 1
        if current.index == len(current.priorities):
            first += 1
            continue

        insertion = first + 1
        while insertion < len(active) and current.priority < active[insertion].priority:
            active[insertion - 1] = active[insertion]
            active[insertion] = current
            insertion += 1
            shifts += 1

    return output, shifts


def optimized(initial: list[Provider]) -> list[tuple[int, float]]:
    # On an exact tie the linear insertion loop places the just-updated record
    # before all existing equal records. A monotonically increasing reinsertion
    # epoch reproduces that rule without scanning the list.
    heap: list[tuple[float, int, Provider]] = []
    next_epoch = len(initial)
    for rank, item in enumerate(initial):
        provider = Provider(item.provider_id, item.priorities.copy())
        initial_epoch = len(initial) - rank
        heapq.heappush(heap, (-provider.priority, -initial_epoch, provider))

    output: list[tuple[int, float]] = []
    while heap:
        _, _, current = heapq.heappop(heap)
        output.append((current.provider_id, current.priority))
        current.index += 1
        if current.index < len(current.priorities):
            next_epoch += 1
            heapq.heappush(heap, (-current.priority, -next_epoch, current))

    return output


def float_priority(value: float) -> int:
    # Match the Burst implementation, including CompareTo's -0 == +0 rule.
    if value == 0.0:
        value = 0.0
    bits = struct.unpack("<I", struct.pack("<f", value))[0]
    ascending = (~bits & 0xFFFFFFFF) if bits & 0x80000000 else bits ^ 0x80000000
    return ~ascending & 0xFFFFFFFF


def bucket_index(priority: int, last_priority: int) -> int:
    return (priority ^ last_priority).bit_length()


def radix_optimized(initial: list[Provider]) -> list[tuple[int, float]]:
    providers = [Provider(item.provider_id, item.priorities.copy()) for item in initial]
    next_node = [-1] * len(providers)
    heads = [-1] * 33
    tails = [-1] * 33
    last_priority = 0

    def prepend(node: int, bucket: int) -> None:
        next_node[node] = heads[bucket]
        heads[bucket] = node
        if tails[bucket] == -1:
            tails[bucket] = node

    def append(node: int, bucket: int) -> None:
        next_node[node] = -1
        if tails[bucket] == -1:
            heads[bucket] = node
        else:
            next_node[tails[bucket]] = node
        tails[bucket] = node

    for node in range(len(providers) - 1, -1, -1):
        priority = float_priority(providers[node].priority)
        prepend(node, bucket_index(priority, last_priority))

    output: list[tuple[int, float]] = []
    active_count = len(providers)
    while active_count:
        if heads[0] == -1:
            source = 1
            while heads[source] == -1:
                source += 1
            node = heads[source]
            next_priority = 0xFFFFFFFF
            while node != -1:
                next_priority = min(next_priority, float_priority(providers[node].priority))
                node = next_node[node]
            last_priority = next_priority

            node = heads[source]
            heads[source] = -1
            tails[source] = -1
            while node != -1:
                following = next_node[node]
                priority = float_priority(providers[node].priority)
                append(node, bucket_index(priority, last_priority))
                node = following

        node = heads[0]
        heads[0] = next_node[node]
        if heads[0] == -1:
            tails[0] = -1

        current = providers[node]
        output.append((current.provider_id, current.priority))
        current.index += 1
        if current.index < len(current.priorities):
            priority = float_priority(current.priority)
            assert priority >= last_priority
            prepend(node, bucket_index(priority, last_priority))
        else:
            active_count -= 1

    return output


def randomized_equivalence() -> None:
    rng = random.Random(0xA7C491)
    for provider_count in (1, 2, 5, 20, 100):
        for _ in range(500):
            providers = []
            for provider_id in range(provider_count):
                # The small integer range intentionally creates many exact ties.
                priorities = sorted(
                    (float(rng.randrange(12)) for _ in range(rng.randrange(1, 30))),
                    reverse=True,
                )
                providers.append(Provider(provider_id, priorities))

            # This represents the concrete order produced by the game's initial
            # NativeSort; shuffling exercises arbitrary concrete tie orders and
            # Python stability is not otherwise relied upon.
            rng.shuffle(providers)
            providers.sort(key=lambda item: item.priority, reverse=True)
            expected, _ = original(providers)
            actual = optimized(providers)
            assert actual == expected
            assert radix_optimized(providers) == expected


def randomized_float_equivalence() -> None:
    rng = random.Random(0x35C0FB7)
    values = [0.0, -0.0, float("inf"), -float("inf")]
    for _ in range(500):
        # Finite float32 values exercise the sortable-bit transform across signs,
        # exponents, subnormals, exact ties, and both zero representations.
        bits = rng.randrange(0x100000000)
        value = struct.unpack("<f", struct.pack("<I", bits))[0]
        if value == value:
            values.append(value)

    for provider_count in (1, 2, 5, 20, 100):
        for _ in range(200):
            providers = []
            for provider_id in range(provider_count):
                priorities = sorted(
                    (rng.choice(values) for _ in range(rng.randrange(1, 30))),
                    reverse=True,
                )
                providers.append(Provider(provider_id, priorities))
            rng.shuffle(providers)
            providers.sort(key=lambda item: item.priority, reverse=True)
            expected, _ = original(providers)
            assert radix_optimized(providers) == expected


def adversarial_shift_counts() -> None:
    for provider_count in (100, 1_000, 5_000):
        # Every provider's second key is lower than every first key, forcing the
        # original current record across the remaining active list.
        providers = [
            Provider(provider_id, [float(provider_count - provider_id), -float(provider_id)])
            for provider_id in range(provider_count)
        ]
        expected, shifts = original(providers)
        assert optimized(providers) == expected
        assert radix_optimized(providers) == expected
        assert shifts == provider_count * (provider_count - 1)
        print(
            f"providers={provider_count:5d} shifts={shifts:10d} "
            f"bytes_moved={shifts * 24:12d}"
        )


if __name__ == "__main__":
    randomized_equivalence()
    randomized_float_equivalence()
    adversarial_shift_counts()
    print("equivalence tests passed")
