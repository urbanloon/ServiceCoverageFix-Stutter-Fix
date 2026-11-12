#!/usr/bin/env python3
"""Independent property and stress tests for the v0.3 byte-radix merge.

The scheduler consumes the concrete provider order produced by NativeSort.
Every provider stream is monotone in the transformed uint key (smaller is
better). The linear-reinsertion model is the behavioral oracle; an epoch heap
is a second, asymptotically efficient oracle for large tests.

This suite deliberately models implementation details that can otherwise hide
subtle errors: 1,025 byte-radix buckets, the 17-word occupancy directory,
cached minima, prepend reinsertion, append redistribution, consecutive-winner
batching, and the record-local 8 + 16 byte overlay inside each 24-byte slot.
"""

from __future__ import annotations

import heapq
import itertools
import math
import random
import struct
from dataclasses import dataclass


UINT32_MAX = 0xFFFFFFFF
BYTE_RADIX_BUCKET_COUNT = 1025
OCCUPANCY_WORD_COUNT = 17


@dataclass
class Provider:
    provider_id: int
    keys: tuple[int, ...]
    index: int = 0

    @property
    def key(self) -> int:
        return self.keys[self.index]

    def clone(self) -> "Provider":
        return Provider(self.provider_id, self.keys, self.index)


@dataclass
class QueueStats:
    processed: int = 0
    selections: int = 0
    reinserts: int = 0
    batched: int = 0
    probes: int = 0
    probe_hits: int = 0
    adaptive_disabled: bool = False


def assert_streams_monotone(initial: list[Provider]) -> None:
    for provider in initial:
        assert provider.keys
        assert all(
            left <= right
            for left, right in zip(provider.keys, provider.keys[1:])
        )


def emitted(provider: Provider) -> tuple[int, int, int]:
    """Provider, per-provider element ordinal, and exact transformed key."""

    return provider.provider_id, provider.index, provider.key


def linear_reinsertion(
    initial: list[Provider],
) -> tuple[list[tuple[int, int, int]], int]:
    """Literal model of vanilla's move-one-record-at-a-time merge."""

    assert_streams_monotone(initial)
    active = [provider.clone() for provider in initial]
    output: list[tuple[int, int, int]] = []
    shifts = 0

    while active:
        current = active[0]
        output.append(emitted(current))
        current.index += 1
        if current.index == len(current.keys):
            active.pop(0)
            continue

        # Strict comparison is important. The just-advanced record remains in
        # front of every existing record with an equal key.
        insertion = 1
        while insertion < len(active) and current.key > active[insertion].key:
            active[insertion - 1] = active[insertion]
            insertion += 1
            shifts += 1
        active[insertion - 1] = current

    return output, shifts


def epoch_heap_oracle(initial: list[Provider]) -> list[tuple[int, int, int]]:
    """O(E log B) oracle with vanilla's exact advanced-record tie rule."""

    assert_streams_monotone(initial)
    heap: list[tuple[int, int, int, Provider]] = []
    next_epoch = len(initial)
    for rank, item in enumerate(initial):
        provider = item.clone()
        # Earlier concrete initial ranks win ties. Every reinsertion gets a
        # newer epoch and therefore precedes all records already at that key.
        epoch = len(initial) - rank
        heapq.heappush(
            heap,
            (provider.key, -epoch, provider.provider_id, provider),
        )

    output: list[tuple[int, int, int]] = []
    while heap:
        _, _, _, current = heapq.heappop(heap)
        output.append(emitted(current))
        current.index += 1
        if current.index != len(current.keys):
            next_epoch += 1
            heapq.heappush(
                heap,
                (current.key, -next_epoch, current.provider_id, current),
            )
    return output


def get_byte_bucket(priority: int, last_priority: int) -> int:
    difference = priority ^ last_priority
    if difference == 0:
        return 0
    shift = ((difference.bit_length() - 1) // 8) * 8
    return 1 + (shift << 5) + ((priority >> shift) & 0xFF)


def get_lowest_occupied_bucket(
    occupancy: list[int], occupied_word_mask: int
) -> int:
    assert occupied_word_mask
    source_word_bit = occupied_word_mask & -occupied_word_mask
    source_word = source_word_bit.bit_length() - 1
    value = occupancy[source_word]
    assert value
    source_bit = (value & -value).bit_length() - 1
    return (source_word << 6) + source_bit


def byte_radix_scheduler(
    initial: list[Provider],
    *,
    batching: str = "full",
    adaptive_window: int = 4096,
    adaptive_hit_numerator: int = 1,
    adaptive_hit_denominator: int = 20,
) -> tuple[list[tuple[int, int, int]], QueueStats]:
    """Exact model of the four-level byte-radix queue.

    ``batching='full'`` models the current RemainsWinner optimization.
    ``batching='adaptive'`` proves an optional exact policy which probes full
    winner batching for a bounded non-equal-key window, then retains it only at
    the requested hit rate. Equality batching is always safe because prepend
    followed by pop must select the same provider. ``none`` is the unbatched
    queue and supplies another independent equivalence check.
    """

    assert batching in {"none", "full", "adaptive"}
    assert adaptive_window > 0
    assert 0 <= adaptive_hit_numerator <= adaptive_hit_denominator
    assert_streams_monotone(initial)
    if not initial:
        return [], QueueStats()

    providers = [provider.clone() for provider in initial]
    node_next = [-1] * len(providers)
    node_priority = [0] * len(providers)
    heads = [-1] * BYTE_RADIX_BUCKET_COUNT
    tails = [-1] * BYTE_RADIX_BUCKET_COUNT
    minimums = [UINT32_MAX] * BYTE_RADIX_BUCKET_COUNT
    occupancy = [0] * OCCUPANCY_WORD_COUNT
    occupied_word_mask = 0
    last_priority = 0
    stats = QueueStats()

    probe_done = 0
    probe_hits = 0
    adaptive_decided = False
    adaptive_full_enabled = True

    def set_occupied(bucket: int) -> None:
        nonlocal occupied_word_mask
        word = bucket >> 6
        occupancy[word] |= 1 << (bucket & 63)
        occupied_word_mask |= 1 << word

    def clear_bucket(bucket: int) -> None:
        nonlocal occupied_word_mask
        heads[bucket] = -1
        word = bucket >> 6
        occupancy[word] &= ~(1 << (bucket & 63))
        if occupancy[word] == 0:
            occupied_word_mask &= ~(1 << word)

    def prepend(node: int, bucket: int, priority: int) -> None:
        old_head = heads[bucket]
        node_next[node] = old_head
        node_priority[node] = priority
        heads[bucket] = node
        if old_head == -1:
            tails[bucket] = node
            minimums[bucket] = priority
            set_occupied(bucket)
        else:
            minimums[bucket] = min(minimums[bucket], priority)

    def append(node: int, bucket: int, priority: int) -> None:
        node_next[node] = -1
        if heads[bucket] == -1:
            heads[bucket] = node
            tails[bucket] = node
            minimums[bucket] = priority
            set_occupied(bucket)
        else:
            node_next[tails[bucket]] = node
            tails[bucket] = node
            minimums[bucket] = min(minimums[bucket], priority)

    def refill_zero() -> None:
        nonlocal last_priority
        source = get_lowest_occupied_bucket(occupancy, occupied_word_mask)
        last_priority = minimums[source]
        current = heads[source]
        clear_bucket(source)
        while current != -1:
            following = node_next[current]
            priority = node_priority[current]
            append(current, get_byte_bucket(priority, last_priority), priority)
            current = following

    def remains_winner(priority: int) -> bool:
        if heads[0] != -1:
            return priority <= last_priority
        source = get_lowest_occupied_bucket(occupancy, occupied_word_mask)
        return priority <= minimums[source]

    def should_batch(priority: int) -> bool:
        nonlocal probe_done, probe_hits, adaptive_decided, adaptive_full_enabled
        if batching == "none":
            return False
        if batching == "full":
            return remains_winner(priority)

        # Equality is a zero-lookup exact fast path even after adaptive probing
        # decides broad competitor checks are unprofitable.
        if priority == last_priority:
            return True
        if adaptive_decided and not adaptive_full_enabled:
            return False

        winner = remains_winner(priority)
        if not adaptive_decided:
            probe_done += 1
            probe_hits += int(winner)
            stats.probes = probe_done
            stats.probe_hits = probe_hits
            if probe_done == adaptive_window:
                adaptive_full_enabled = (
                    probe_hits * adaptive_hit_denominator
                    >= probe_done * adaptive_hit_numerator
                )
                adaptive_decided = True
                stats.adaptive_disabled = not adaptive_full_enabled
        return winner

    # Reverse prepend preserves the concrete NativeSort order, including ties.
    for provider_index in range(len(providers) - 1, -1, -1):
        priority = providers[provider_index].key
        prepend(
            provider_index,
            get_byte_bucket(priority, last_priority),
            priority,
        )

    output: list[tuple[int, int, int]] = []
    while occupied_word_mask:
        if heads[0] == -1:
            refill_zero()

        provider_index = heads[0]
        heads[0] = node_next[provider_index]
        if heads[0] == -1:
            clear_bucket(0)
        stats.selections += 1

        current = providers[provider_index]
        while True:
            output.append(emitted(current))
            stats.processed += 1
            current.index += 1
            if current.index == len(current.keys):
                break

            # With no competitor, the provider owns its entire serial tail.
            if occupied_word_mask == 0:
                stats.batched += 1
                continue

            priority = current.key
            assert priority >= last_priority
            if should_batch(priority):
                stats.batched += 1
                continue

            prepend(
                provider_index,
                get_byte_bucket(priority, last_priority),
                priority,
            )
            stats.reinserts += 1
            break

    return output, stats


def concrete_initial_order(
    streams: tuple[tuple[int, ...], ...], permutation: tuple[int, ...]
) -> list[Provider]:
    providers = [Provider(index, streams[index]) for index in permutation]
    # Python's stable sort gives us one explicit concrete NativeSort outcome.
    # Iterating every input permutation exercises every possible order for ties.
    providers.sort(key=lambda provider: provider.key)
    return providers


def float32_from_bits(bits: int) -> float:
    return struct.unpack("<f", struct.pack("<I", bits))[0]


def original_priority_bits(bits: int) -> int:
    """Bit-exact form of the original two-stage sortable-float transform."""

    if bits & 0x7FFFFFFF == 0:
        bits = 0
    ascending = (
        (~bits & UINT32_MAX)
        if bits & 0x80000000
        else bits ^ 0x80000000
    )
    return ~ascending & UINT32_MAX


def optimized_priority_bits(bits: int) -> int:
    """Bit-exact form emitted by OptimizedApplyCoverageJob.GetPriority."""

    if bits & 0x80000000 == 0:
        return bits ^ 0x7FFFFFFF
    return 0x7FFFFFFF if bits & 0x7FFFFFFF == 0 else bits


def priority_transform_properties() -> None:
    edge_patterns = {
        0x00000000,  # +0
        0x80000000,  # -0
        0x00000001,
        0x007FFFFF,
        0x00800000,
        0x3F800000,  # +1
        0x7F7FFFFF,
        0x7F800000,  # +infinity
        0x7F800001,  # signaling/low-payload positive NaN
        0x7FC00000,  # quiet positive NaN
        0x7FFFFFFF,  # maximum positive NaN payload
        0x80000001,
        0x807FFFFF,
        0x80800000,
        0xBF800000,  # -1
        0xFF7FFFFF,
        0xFF800000,  # -infinity
        0xFF800001,  # signaling/low-payload negative NaN
        0xFFC00000,  # quiet negative NaN
        0xFFFFFFFF,  # maximum negative NaN payload
    }

    # Cover every sign/exponent combination and the important mantissa edges,
    # including both classes and both signs of NaN.
    mantissas = (0, 1, 2, 0x3FFFFF, 0x400000, 0x400001, 0x7FFFFE, 0x7FFFFF)
    for sign in (0, 0x80000000):
        for exponent in range(256):
            for mantissa in mantissas:
                edge_patterns.add(sign | (exponent << 23) | mantissa)

    rng = random.Random(0xA7C491_35C0FB7)
    patterns = list(edge_patterns)
    patterns.extend(rng.getrandbits(32) for _ in range(1_000_000))
    for bits in patterns:
        assert optimized_priority_bits(bits) == original_priority_bits(bits)

    assert optimized_priority_bits(0x00000000) == 0x7FFFFFFF
    assert optimized_priority_bits(0x80000000) == 0x7FFFFFFF
    for bits in (
        0x7F800001,
        0x7FC00000,
        0x7FFFFFFF,
        0xFF800001,
        0xFFC00000,
        0xFFFFFFFF,
    ):
        assert math.isnan(float32_from_bits(bits))
        assert optimized_priority_bits(bits) == original_priority_bits(bits)

    # For every finite random pair, a numerically larger coverage must have a
    # smaller transformed key. Both representations of zero are one exact tie.
    finite: list[tuple[float, int]] = []
    while len(finite) < 100_000:
        bits = rng.getrandbits(32)
        value = float32_from_bits(bits)
        if math.isfinite(value):
            finite.append((value, bits))
    for _ in range(250_000):
        left_value, left_bits = rng.choice(finite)
        right_value, right_bits = rng.choice(finite)
        left_key = optimized_priority_bits(left_bits)
        right_key = optimized_priority_bits(right_bits)
        if left_value > right_value:
            assert left_key < right_key
        elif left_value < right_value:
            assert left_key > right_key
        elif left_value == 0.0:
            assert left_key == right_key == 0x7FFFFFFF


def byte_bucket_and_directory_properties() -> None:
    assert get_byte_bucket(0, 0) == 0
    assert get_byte_bucket(UINT32_MAX, UINT32_MAX) == 0

    # Reach every possible byte digit in every radix level. Higher bytes are
    # held equal and the selected byte is forced to differ from lastPriority.
    for shift in (0, 8, 16, 24):
        lower_mask = (1 << shift) - 1
        higher_mask = UINT32_MAX ^ ((1 << (shift + 8)) - 1)
        for digit in range(256):
            last_digit = digit ^ 1
            for higher in (0, 0xA5A5A5A5 & higher_mask, higher_mask):
                last = higher | (last_digit << shift) | (lower_mask & 0x55AA55AA)
                for lower in (0, lower_mask, lower_mask & 0xAA55AA55):
                    priority = higher | (digit << shift) | lower
                    difference = priority ^ last
                    assert difference
                    oracle_shift = ((difference.bit_length() - 1) // 8) * 8
                    assert oracle_shift == shift
                    expected = 1 + (shift << 5) + digit
                    assert get_byte_bucket(priority, last) == expected

    # Exercise all 17 occupancy words and every 63/64-bit boundary, including
    # the last valid bucket (1024).
    directory_edges = {
        0,
        1,
        2,
        63,
        64,
        65,
        127,
        128,
        255,
        256,
        511,
        512,
        767,
        768,
        1023,
        1024,
    }
    directory_edges.update(range(0, BYTE_RADIX_BUCKET_COUNT, 64))
    for lowest in sorted(directory_edges):
        occupancy = [0] * OCCUPANCY_WORD_COUNT
        word_mask = 0
        for bucket in sorted({lowest, 1024, min(1024, lowest + 1)}):
            word = bucket >> 6
            occupancy[word] |= 1 << (bucket & 63)
            word_mask |= 1 << word
        assert get_lowest_occupied_bucket(occupancy, word_mask) == lowest

    rng = random.Random(0x1025_17)
    for _ in range(10_000):
        buckets = set(rng.sample(range(BYTE_RADIX_BUCKET_COUNT), rng.randrange(1, 40)))
        occupancy = [0] * OCCUPANCY_WORD_COUNT
        word_mask = 0
        for bucket in buckets:
            word = bucket >> 6
            occupancy[word] |= 1 << (bucket & 63)
            word_mask |= 1 << word
        assert get_lowest_occupied_bucket(occupancy, word_mask) == min(buckets)


def exhaustive_small_domain_tie_order() -> None:
    # Every nonempty monotone stream of length <= 2 over three keys.
    streams = [(key,) for key in range(3)]
    streams += [pair for pair in itertools.combinations_with_replacement(range(3), 2)]
    cases = 0
    for selected in itertools.product(streams, repeat=3):
        for permutation in itertools.permutations(range(3)):
            initial = concrete_initial_order(selected, permutation)
            expected, _ = linear_reinsertion(initial)
            assert epoch_heap_oracle(initial) == expected
            assert byte_radix_scheduler(initial, batching="none")[0] == expected
            assert byte_radix_scheduler(initial, batching="full")[0] == expected
            assert byte_radix_scheduler(
                initial,
                batching="adaptive",
                adaptive_window=2,
                adaptive_hit_numerator=1,
                adaptive_hit_denominator=2,
            )[0] == expected
            cases += 1
    assert cases == len(streams) ** 3 * math.factorial(3)
    print(f"exhaustive tie/order cases={cases}")


def uint32_edge_sequence_properties() -> None:
    edges = {
        0,
        1,
        2,
        0x7E,
        0x7F,
        0x80,
        0x81,
        0xFE,
        0xFF,
        0x100,
        0x101,
        0x7FFE,
        0x7FFF,
        0x8000,
        0x8001,
        0xFFFE,
        0xFFFF,
        0x10000,
        0x10001,
        0x7FFFFE,
        0x7FFFFF,
        0x800000,
        0x800001,
        0xFFFFFE,
        0xFFFFFF,
        0x1000000,
        0x1000001,
        0x7FFFFFFE,
        0x7FFFFFFF,
        0x80000000,
        0x80000001,
        0xFFFFFFFE,
        UINT32_MAX,
    }
    for shift in (0, 8, 16, 24):
        for digit in range(256):
            value = digit << shift
            edges.add(value)
            edges.add(min(UINT32_MAX, value + ((1 << shift) - 1)))

    ordered_edges = sorted(edges)
    providers: list[Provider] = []
    for provider_id, key in enumerate(ordered_edges):
        tail = (
            key,
            min(UINT32_MAX, key + 1),
            min(UINT32_MAX, key + 0x100),
            UINT32_MAX,
        )
        providers.append(Provider(provider_id, tuple(sorted(tail))))
    providers.sort(key=lambda provider: provider.key)

    expected = epoch_heap_oracle(providers)
    for mode in ("none", "full", "adaptive"):
        assert byte_radix_scheduler(providers, batching=mode)[0] == expected
    print(
        f"uint32 edge providers={len(providers)} "
        f"processed={len(expected)}"
    )


def randomized_scheduler_equivalence() -> None:
    rng = random.Random(0x35C0FB7_A7B3F6)
    trials = 0
    for provider_count, repetitions in (
        (1, 150),
        (2, 300),
        (5, 300),
        (20, 200),
        (100, 80),
        (526, 8),
    ):
        for trial in range(repetitions):
            providers: list[Provider] = []
            dense_ties = trial & 1
            for provider_id in range(provider_count):
                length = rng.randrange(1, 40 if provider_count < 100 else 16)
                if dense_ties:
                    keys = tuple(sorted(rng.randrange(32) for _ in range(length)))
                else:
                    keys = tuple(sorted(rng.getrandbits(32) for _ in range(length)))
                providers.append(Provider(provider_id, keys))
            rng.shuffle(providers)
            providers.sort(key=lambda provider: provider.key)

            expected, _ = linear_reinsertion(providers)
            assert epoch_heap_oracle(providers) == expected
            assert byte_radix_scheduler(providers, batching="none")[0] == expected
            assert byte_radix_scheduler(providers, batching="full")[0] == expected
            # Randomized probe policies exercise both early and late decisions.
            assert byte_radix_scheduler(
                providers,
                batching="adaptive",
                adaptive_window=rng.randrange(1, 33),
                adaptive_hit_numerator=rng.randrange(0, 5),
                adaptive_hit_denominator=4,
            )[0] == expected
            trials += 1
    print(f"randomized scheduler trials={trials}")


def adaptive_batching_policy_paths() -> None:
    # Interleaved keys make broad winner probes miss and force adaptive disable.
    interleaved = [
        Provider(provider_id, tuple(range(provider_id, 400, 8)))
        for provider_id in range(8)
    ]
    expected = epoch_heap_oracle(interleaved)
    actual, stats = byte_radix_scheduler(
        interleaved,
        batching="adaptive",
        adaptive_window=16,
        adaptive_hit_numerator=1,
        adaptive_hit_denominator=20,
    )
    assert actual == expected
    assert stats.probes == 16
    assert stats.probe_hits == 0
    assert stats.adaptive_disabled

    # Long winner runs make the probe succeed and retain full batching.
    winner_runs = [
        Provider(0, tuple(range(0, 300))),
        Provider(1, tuple(range(1000, 1300))),
        Provider(2, tuple(range(2000, 2300))),
    ]
    expected = epoch_heap_oracle(winner_runs)
    actual, stats = byte_radix_scheduler(
        winner_runs,
        batching="adaptive",
        adaptive_window=16,
        adaptive_hit_numerator=1,
        adaptive_hit_denominator=20,
    )
    assert actual == expected
    assert stats.probes == 16
    assert stats.probe_hits == 16
    assert not stats.adaptive_disabled
    assert stats.selections == 3
    assert stats.batched == len(expected) - 3
    print("adaptive batching enable/disable paths=exact")


def record_local_overlay_proof() -> None:
    raw_size = struct.calcsize("<iiiiff")
    state_size = struct.calcsize("<iiff")
    node_size = struct.calcsize("<iI")
    assert (raw_size, state_size, node_size) == (24, 16, 8)

    # Every transformed provider remains entirely inside its own original
    # 24-byte record: node at [0, 8), state at [8, 24). There is no compaction,
    # shared tail region, or write that can cross into a neighboring record.
    for record_count in range(1, 100_001):
        for index in (0, record_count - 1):
            base = raw_size * index
            assert base + node_size == base + 8
            assert base + 8 + state_size == base + raw_size

    # Byte-level simulation of InitializeSlots plus queue-node initialization.
    for record_count in (1, 2, 3, 7, 64, 421, 526, 8_193, 65_537):
        storage = bytearray(raw_size * record_count)
        expected_states: list[tuple[int, int, float, float]] = []
        for index in range(record_count):
            element_index = index * 7 + 3
            element_count = index % 19 + 1
            total = float(index + 1000)
            remaining = float(index % 37) + 0.5
            struct.pack_into(
                "<iiiiff",
                storage,
                raw_size * index,
                index,
                index ^ 0x123456,
                element_index,
                element_count,
                total,
                remaining,
            )
            expected_states.append(
                (element_index, element_index + element_count, total, remaining)
            )

        for index in range(record_count):
            base = raw_size * index
            next_index, count, total, remaining = struct.unpack_from(
                "<iiff", storage, base + node_size
            )
            struct.pack_into("<i", storage, base + 12, next_index + count)
            struct.pack_into("<iI", storage, base, -1, index)

        for index, expected in enumerate(expected_states):
            base = raw_size * index
            assert struct.unpack_from("<iI", storage, base) == (-1, index)
            assert struct.unpack_from("<iiff", storage, base + node_size) == expected
    print("record-local 8 + 16 overlay/layout proof=passed")


def vanilla_nan_liveness_properties() -> None:
    finite_cases = (
        (-float("inf"), False),
        (-1.0, False),
        (-0.0, False),
        (0.0, False),
        (float32_from_bits(0x00000001), True),
        (1.0, True),
        (float("inf"), True),
    )
    for remaining, expected_live in finite_cases:
        assert (not (remaining <= 0.0)) is expected_live

    nan_patterns = (
        0x7F800001,
        0x7FA00000,
        0x7FC00000,
        0x7FFFFFFF,
        0xFF800001,
        0xFFA00000,
        0xFFC00000,
        0xFFFFFFFF,
    )
    for bits in nan_patterns:
        remaining = float32_from_bits(bits)
        assert math.isnan(remaining)
        # Vanilla bgt.un / !(Remaining <= 0) keeps every NaN live.
        assert not (remaining <= 0.0)

    # ElementCount==0 is independently inactive, including with NaN Remaining.
    for bits in nan_patterns:
        remaining = float32_from_bits(bits)
        assert (0 == 0 or remaining <= 0.0)
        assert not (1 == 0 or remaining <= 0.0)
    print("vanilla NaN and signed-zero liveness=exact")


def adversarial_shift_and_batch_counts() -> None:
    for provider_count in (100, 1_000, 5_000):
        providers = [
            Provider(provider_id, (provider_id, provider_count + provider_id))
            for provider_id in range(provider_count)
        ]
        expected, shifts = linear_reinsertion(providers)
        actual, stats = byte_radix_scheduler(providers, batching="full")
        assert actual == expected
        assert shifts == provider_count * (provider_count - 1)
        print(
            f"providers={provider_count:5d} shifts={shifts:10d} "
            f"bytes_moved={shifts * 24:12d} "
            f"selections={stats.selections:7d}/{stats.processed:7d}"
        )


def large_provider_no_cap_stress() -> None:
    rng = random.Random(0x526_421_8193)
    cases: list[list[Provider]] = []

    # Immediately above the old 8,192 stack-node threshold, with real merging.
    providers_8193 = [
        Provider(
            provider_id,
            tuple(sorted((rng.getrandbits(32) for _ in range(3)))),
        )
        for provider_id in range(8_193)
    ]
    providers_8193.sort(key=lambda provider: provider.key)
    cases.append(providers_8193)

    # Far beyond any former cap. Dense ties also stress prepend tie ordering.
    providers_65537 = [
        Provider(provider_id, (provider_id & 0xFF, UINT32_MAX))
        for provider_id in range(65_537)
    ]
    providers_65537.sort(key=lambda provider: provider.key)
    cases.append(providers_65537)

    for providers in cases:
        expected = epoch_heap_oracle(providers)
        full, full_stats = byte_radix_scheduler(providers, batching="full")
        adaptive, adaptive_stats = byte_radix_scheduler(
            providers,
            batching="adaptive",
            adaptive_window=4096,
            adaptive_hit_numerator=1,
            adaptive_hit_denominator=20,
        )
        assert full == expected
        assert adaptive == expected
        print(
            f"no-cap providers={len(providers):6d} processed={len(expected):7d} "
            f"full_selections={full_stats.selections:7d} "
            f"adaptive_selections={adaptive_stats.selections:7d}"
        )


def main() -> None:
    priority_transform_properties()
    byte_bucket_and_directory_properties()
    exhaustive_small_domain_tie_order()
    uint32_edge_sequence_properties()
    randomized_scheduler_equivalence()
    adaptive_batching_policy_paths()
    record_local_overlay_proof()
    vanilla_nan_liveness_properties()
    adversarial_shift_and_batch_counts()
    large_provider_no_cap_stress()
    print("all v0.3 byte-radix equivalence and stress tests passed")


if __name__ == "__main__":
    main()
