# Worker handoff after heavy service coverage updates

Four archived simulation captures contain 41 heavy replacement Apply episodes. Car navigation first appeared a median **0.589 ms** after the last Apply sample, with a range of **0.096–1.233 ms**. Human navigation followed at a median **2.135 ms**, with a range of **0.759–3.527 ms**. Both jobs were sampled after all 41 episodes. Neither appeared inside any of the 41 sampled Apply spans.

| Capture | Historical build | Episodes | Median Apply span | Median time from last Apply sample to first car navigation sample |
|---|---|---:|---:|---:|
| C03 | 0.1.4 radix selector | 8 | 20.44 ms | 0.446 ms |
| C04 | 0.1.4 radix selector | 8 | 19.86 ms | 0.659 ms |
| C06 | Historical pre-release | 8 | 12.17 ms | 0.868 ms |
| C07 | Same pre-release binary | 17 | 14.11 ms | 0.654 ms |

In 29 episodes the worker that supplied the final Apply sample then readied at least one waiting thread subsequently sampled in car navigation. The replay found 51 such dispatches. In each one the final Apply sample was on the readying CPU in the same uninterrupted dispatch, within 1 ms of the ready event, with no intervening physical sample from that thread. The destination had exactly one ready event between its waiting switch-out and return to that CPU dispatch. In all 51 dispatches the destination’s first physical sample after switch-in was already in car navigation, within 3 ms of the last Apply sample. The source sample preceded readiness by 0.045–1.000 ms. In 16 episodes the destination provided the first observed car-navigation sample. In seven episodes car navigation ran on the Apply thread itself without an intervening switch. At least one of the two forms appeared in 31 episodes.

The entry thread spent 439.69 ms blocked inside 673.23 ms of sampled Apply intervals. Of 328 overlapping waits, the Apply thread readied the entry thread three times, 3.08–3.96 ms after its final Apply sample. None of those signals had a recent Apply sample within 1 ms. The cadence is visible in the worker handoff even though a direct Apply-to-entry signal was rare.

| Job | Worker and entry samples during all Apply spans | Episodes with samples during Apply |
|---|---:|---:|
| Replacement Apply | 716 | 41 |
| Ordinary graph route search | 2,658 | 39 |
| Coverage graph search and related actions | 660 | 30 |
| Render batch culling | 568 | 39 |
| Water pipe flow | 314 | 37 |
| Coverage Process | 0 | 0 |
| Coverage result conversion | 0 | 0 |
| Car navigation | 0 | 0 |
| Human navigation | 0 | 0 |

Coverage Process had 2,594 samples in the 25 ms preceding Apply. During the following 25 ms, car navigation had 504 samples and human navigation had 749. These are physical samples aggregated over concurrent threads. The job list in the table is selective. The full window data cover the named roots and classify all remaining samples as other or unresolved.

## Interpretation

The recorded worker-to-worker ready events support a handoff around heavy historical coverage updates. The historical managed assembly schedules navigation with ECS and spatial-data dependencies. Neither navigation scheduling method explicitly consumes the ServiceCoverage Apply handle. The ETL supplies thread and CPU identities, with gaps between profiler samples. It cannot name the Unity handle behind an individual signal or determine what code ran in a sub-millisecond gap.

The captures do not identify the active service enum for each episode. Historical save data and capture provenance support investigating a park-heavy workload. None of the four recordings used 0.3.6. None contains the raw GUID bytes for the DXGI, DxgKrnl or D3D9 providers. Frame impact in the current city remains unmeasured.

## Replay

Use Python 3.11 or newer, NumPy, and a C++17 compiler on little-endian x86-64 Linux. Supply the four expanded ETLs as C03.etl, C04.etl, C06.etl and C07.etl. The script checks each SHA-256 against the original repository's capture table and matches every episode against its published onset, duration and sample count. Choose separate private and output directories. Only the summary CSVs contain results suitable for distribution.

```sh
python3 analyze.py --repo CHECKOUT --inputs ETLS --scratch EMPTY_PRIVATE_DIRECTORY --out SUMMARY_DIRECTORY
python3 check_capture_providers.py --repo CHECKOUT --inputs ETLS > presentation-providers.csv
```

The script uses the decoder and original job labels in commit 6c4c7f4. The 13 additional job identities in extra-job-roots.csv were independently matched using the archived managed assembly, Burst's closed producer encoder and the archived game's export table. The two assembly digests are documented in the repository's release critical-path reproduction notes. The repository's managed VerifyJobs program also accepts extra-job-roots.csv as its third argument.
