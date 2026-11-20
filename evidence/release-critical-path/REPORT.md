# Main-thread work and worker wakeups in the published 0.3.2 traces

The clearest remaining main-thread stall candidate is ordinary route search in C09. The game entry thread produced **61 consecutive route-search samples over 60.00 ms** on one logical CPU, with no intervening thread switch. Resource-seller target setup also repeatedly ran on the worker that readied the main thread in both captures. These are two distinct parts of pathfinding. The replacement Apply worker recorded 111 and 599 samples across the two captures, all on other threads. Neither recording contains 0.3.5, the current candidate, or presentation timings.

## Which work runs where

C08 and C09 are historical recordings of the same published 0.3.2 native module. Counts below use physical CPU samples with a matching game stack and a game or mod Burst instruction at the leaf. They sum across threads.

| Activity | C08 samples | C09 samples | C09 samples on entry thread¹ | Longest C09 uninterrupted entry-thread sample span |
|---|---:|---:|---:|---:|
| Ordinary graph route search | 706,290 | 2,332,991 | 168 | 60.00 ms |
| Resource-seller target setup | 185,698 | 760,308 | 127 | 9.98 ms |
| Coverage graph action | 37,812 | 201,164 | 11 | 7.01 ms |
| Coverage result conversion | 6,232 | 31,851 | 3 | — |
| Coverage Process | 763 | 4,457 | 0 | — |
| Replacement Apply | 111 | 599 | 0 | — |

¹ Entry-thread counts also admit an external instruction leaf if its physical sample and game stack match and the stack contains the verified job root. C08 had zero entry-thread samples in these six activities. The 60.00 ms run began 20.73 s after C09's first sampled game-thread instruction. Every sample had the same UnityPlayer caller sequence beneath the Burst dispatcher. A worker can process several actions within one invocation, so the number of distinct routes in that span is unknown.

## What wakes the main thread

The scheduler recorded 2,178 waits of at least 5 ms in C08 and 9,113 in C09. A thread observed executing named game jobs issued the ready transition in the running CPU context for **2,126 (97.6%)** and **8,899 (97.7%)**, respectively. These counts use blocked time from switch-out to ReadyThread. The sum of blocked time was 21.73 s and 99.29 s. Additional time from ready to actual switch-in totaled 0.056 s and 0.294 s across those same waits.

To associate a job with a wake, the final CPU sample must fall during the wait, on the readying thread and CPU, in the same uninterrupted scheduling interval, within 1 ms before ReadyThread. A *sustained* match requires at least three consecutive samples in that activity, with no sampling gap over 1.3 ms. The *half-wait* test requires the observed first-to-last sample span to cover at least half the blocked interval.

| Final readier activity during waits ≥5 ms | C08 near / sustained / half-wait | C09 near / sustained / half-wait |
|---|---:|---:|
| Resource-seller target setup | 269 / 246 / 229 | 1,274 / 1,113 / 988 |
| Ordinary graph route search | 11 / 7 / 5 | 72 / 62 / 49 |
| Coverage graph action | 0 / 0 / 0 | 12 / 9 / 7 |
| Coverage result conversion | 2 / 0 / 0 | 8 / 3 / 0 |
| Coverage Process | 0 / 0 / 0 | 0 / 0 / 0 |
| Replacement Apply | 0 / 0 / 0 | 0 / 0 / 0 |
| Procedural rendering preparation | 235 / 0 / 0 | 738 / 0 / 0 |
| Citizen happiness | 50 / 37 / 23 | 155 / 115 / 88 |
| All waits with a recent sample, including other activities | 821 | 3,421 |

In C09, one worker produced 23 consecutive seller-setup samples over 22.05 ms during a 22.80 ms main-thread wait. The final sample preceded the ready event by 0.54 ms. Other workers produced 15 consecutive ordinary-route samples over 14.01 ms during a 15.50 ms wait, and 13 coverage-graph samples over 11.98 ms during a 13.91 ms wait. Each of those waits used OS reason 37, WrAlertByThreadId. The table includes procedural rendering because a single nearby sample and a sustained series give materially different results for that job.

Release ETW identifies the running thread at ReadyThread but supplies no matched game-source user stack at the ready event. A worker can move to another task between its final sample and the signal. CPU context can also include an interrupt. These associations cannot assign a particular JobHandle or dependency to an individual wake. Roughly 62% of qualifying waits in each capture lacked a recent same-dispatch sample altogether. The 60 ms sampled run directly on the entry thread is independent of that attribution problem.

## A specific target outside service coverage

In the inspected historical game assembly, `ResourcePathfindSetup.SetupResourceSeller` schedules a parallel chunk job to find candidate sellers for path requests. `PathfindSetupSystem.FindTargets` schedules a dequeue job dependent on that search and combines the resulting handle into `m_SetupDependencies`. `CompleteSetup` completes this handle before publishing the targets. The pathfinding queue's update also calls `CompleteSetup`. This establishes a source-level path by which seller work can hold up a simulation update.

The seller loop checks entity and prefab components for each candidate and seeker. **47.3% and 47.4%** of the two runs' mapped seller samples were in one native lookup helper, reached from seven call sites in the seller loop. The PE unwind range is RVA 0xabd0 to 0xad65, exclusive. The hottest part includes dependent loads and a comparison. Instruction samples alone cannot distinguish cache stalls or predict the benefit of reusing lookups. Any optimization here would change a separate game system and would need its own target-set, order and random-state comparisons. This evidence supports leaving it outside the ServiceCoverageFix patch.

## Positive control and measurement boundaries

C05's heavy vanilla Apply episodes provide an independent check. The Apply worker readied the main thread in 90 of 91 episodes. All 90 ready events had an event-time user stack with first UnityPlayer frame at RVA 0x57618f. The stricter recent-work rule also found three consecutive Apply samples for 89 of those 90. Event-time source stacks were available in C05.

The operating-system entry thread was identified by the start address inside Cities2.exe. The 31 other job-executing thread identities in each release capture each produced at least three samples in the selected named jobs. There were no unmatched Waiting-state intervals with at least 5 ms of off-CPU time. CPU samples, blocked wall time, and ready-to-running scheduler latency have separate denominators. Intervals between samples remain unresolved, and simultaneous work across workers overlaps in real time. No game launch was used.
