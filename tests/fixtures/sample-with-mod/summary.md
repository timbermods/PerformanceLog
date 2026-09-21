# Performance log summary

Session `sample-with-mod`, status **finished**. This file is rewritten about once a minute while the game runs and once more when the session ends, so it is complete after a clean exit and at most a minute stale after a crash. Read `README.md` in this folder for what every file and column means.

## Session

| | |
|---|---|
| Started | 2026-01-01 12:00:00 |
| Recorded | 300 s (5.0 min), 17893 frames, 2440 simulation ticks |
| Game / mod | Timberborn 1.1.2.4 (sample) / Performance Log 0.1.0 |
| Profile level | standard (slow frame = 50 ms or more; summary rows every 10 s) |
| Colony at the last sample | 240 beavers, 12 bots, 9000 entities, day 20 |
| Mods enabled | 3 (listed at the end) |

**Read first:**

- This is made-up sample data written by the tests, not a recording of a game.

## Frame rate

- Mean frame time **16.8 ms** (60 fps on average); median about 15.8 ms, 90th percentile about 17.2 ms, 99th about 17.5 ms, slowest 436.6 ms. (Percentiles are read off the frame-time histogram, so they are only as fine as its buckets.)
- **9** frames took 50 ms or more (0.1% of frames, 0.4% of all frame time). Among them: 6 contained a garbage collection, 1 a save, 0 were with the window in the background, 0 were with the game paused.
- The very first frame after the log started took 17 ms (start-up cost, not gameplay).

| Game speed | Frames | Share | Mean frame time |
|---|---|---|---|
| paused | 898 | 5.0% | 16.7 ms |
| 1 | 3569 | 19.9% | 16.8 ms |
| 3 | 13426 | 75.0% | 16.8 ms |

Frame times are only comparable at the same game speed: at speed 3 the game runs three simulation ticks' worth of work per second.

## Where an average frame goes

Times are exclusive (a part inside another is taken out of the outer one), so the rows add up to the frame time.

| Part | ms per frame | Share of frame | ms per tick | KB per frame |
|---|---|---|---|---|
| `tickMs` | 0.02 | 0.1% | 0.14 | 0.0 |
| `singMs` | 0.25 | 1.5% | 1.80 | 0.6 |
| `entMs` | 1.61 | 9.6% | 11.84 | 31.0 |
| `parWaitMs` | 0.08 | 0.4% | 0.55 | 0.0 |
| `parStartMs` | 0.06 | 0.4% | 0.45 | 0.2 |
| `updMs` | 1.69 | 10.1% |  | 16.1 |
| `lateMs` | 0.05 | 0.3% |  | 1.5 |
| `saveMs` | 0.02 | 0.1% |  | 0.3 |
| `otherMs` (drawing, other scripts, other mods, the system) | 12.99 | 77.5% | | 0.0 |

Unity's phases of a frame (the wait for vertical sync is in one of them, usually `plPost`):

| Phase | ms per frame | Share |
|---|---|---|
| `plTime` | 0.20 | 1.2% |
| `plUpdate` | 3.75 | 22.3% |
| `plPost` | 12.75 | 76.0% |

## Simulation ticks

- 2440 ticks in 300 s, so 8.1 ticks per second on average. The game's tick is 0.30 s of game time, so at speed 1 it runs 3.3 ticks per second, proportionally more at higher speeds.
- One tick costs **14.78 ms** on the game thread (tick loop, singletons, entities and the parallel tick's start and wait). Entity ticks per simulation tick: 896.
- The game reports its parallel tick (work on the worker threads) at 2.10 ms per tick; the game thread waited 0.55 ms per tick for it.
- Ticks run per frame: 0: 86.4%, 1: 13.6%, 2: 0.0%, 3-4: 0.0%, 5-9: 0.0%, 10+: 0.0%.

## How the session changed over time

The session cut into 8 equal stretches of time. A frame or tick that gets more expensive as the game goes on, or memory that only climbs, shows here. Compare stretches at the same average speed: a stretch at a higher speed has longer frames.

| Stretch | Frames | Mean frame ms | Slowest frame ms | Avg speed | Ticks/s | ms per tick | Collections | Heap MB | Entities | Beavers |
|---|---|---|---|---|---|---|---|---|---|---|
| 0.0-0.6 min | 1786 | 16.8 | 125 | 1.0 | 3.3 | 15.04 | 2 | 4 | 10060 | 293 |
| 0.6-1.2 min | 2380 | 16.8 | 124 | 2.0 | 5.0 | 14.87 | 2 | 4 | 10140 | 297 |
| 1.2-1.9 min | 2384 | 16.8 | 132 | 3.0 | 9.9 | 14.73 | 1 | 4 | 10220 | 301 |
| 1.9-2.5 min | 1788 | 17.0 | 437 | 3.0 | 9.8 | 14.73 | 1 | 4 | 10281 | 304 |
| 2.5-3.1 min | 2396 | 16.7 | 17 | 3.0 | 10.0 | 14.76 | 0 | 4 | 10361 | 308 |
| 3.1-3.7 min | 2396 | 16.7 | 17 | 3.0 | 10.0 | 14.76 | 0 | 4 | 10441 | 312 |
| 3.7-4.4 min | 2396 | 16.7 | 17 | 1.5 | 6.2 | 14.77 | 0 | 4 | 10521 | 316 |
| 4.4-5.0 min | 2367 | 16.7 | 17 | 3.0 | 10.0 | 14.78 | 0 | 4 | 10581 | 319 |

## Garbage collection and memory

- 6 garbage collections (1.2 per minute). The game allocated about 2 KB per second (0 KB per tick).
- Managed heap ranged 4 to 4 MB; at the last sample Unity's managed heap was 300 MB reserved, 217 MB in use, 1400 MB in all with native memory, and the process held 3100 MB in RAM.
- Allocation by part (KB per tick, from the timed slots; coarse if the allocation source is the heap size): entKB 228, updKB 118, lateKB 11, singKB 4, saveKB 2, parStartKB 1.

## Is the game thread working or waiting?

Processor time was not available on this computer.

## Where the time goes, by part of the game or mod

`ms/s` is milliseconds of the game thread's time per second of play, so 10 ms/s is one hundredth of a core. Singletons are timed on every call; entity kinds, components and watched methods are sampled and scaled up (see `profile.csv`).

**Singletons ticked once per simulation tick** (together 14.6 ms/s)

| Name | Mod | ms/s | Share | calls/s | us per call | KB/s | slowest call ms |
|---|---|---|---|---|---|---|---|
| `Timberborn.WaterSystem.WaterSimulator` | game | 10.15 | 69.4% | 8 | 1248.0 | 11.9 | 1.40 |
| `LateGamePerformance.HaulCache` | kyler.lategameperformance | 2.85 | 19.5% | 8 | 349.7 | 11.9 | 0.40 |
| `Timberborn.Population.PopulationService` | game | 1.63 | 11.1% | 8 | 200.0 | 11.9 | 0.20 |

**Singletons updated once per frame** (together 100.9 ms/s)

| Name | Mod | ms/s | Share | calls/s | us per call | KB/s | slowest call ms |
|---|---|---|---|---|---|---|---|
| `Timberborn.CoreUI.PanelStack` | game | 58.51 | 58.0% | 60 | 980.9 | 87.4 | 1.70 |
| `Timberborn.CameraSystem.CameraService` | game | 23.84 | 23.6% | 60 | 399.7 | 87.4 | 0.45 |
| `LateGamePerformance.RouteMapsBackground` | kyler.lategameperformance | 18.53 | 18.4% | 60 | 310.6 | 87.4 | 95.00 |

**Singletons late-updated once per frame** (together 3.0 ms/s)

| Name | Mod | ms/s | Share | calls/s | us per call | KB/s | slowest call ms |
|---|---|---|---|---|---|---|---|
| `Timberborn.TimeSystem.SpeedManager` | game | 2.98 | 100.0% | 60 | 50.0 | 87.4 | 0.05 |

**Parallel singletons: the game thread starting them** (together 3.7 ms/s)

| Name | Mod | ms/s | Share | calls/s | us per call | KB/s | slowest call ms |
|---|---|---|---|---|---|---|---|
| `Timberborn.Navigation.NavigationSynchronizer` | game | 3.66 | 100.0% | 8 | 450.0 | 11.9 | 0.45 |

**Entity kinds (sampled)** (together 96.1 ms/s)

| Name | Mod | ms/s | Share | calls/s | us per call | KB/s | slowest call ms |
|---|---|---|---|---|---|---|---|
| `Bot.Worker` |  | 29.06 | 30.3% | 1211 | 24.0 | 307.4 | 0.02 |
| `Beaver.Adult` |  | 26.77 | 27.9% | 1217 | 22.0 | 308.9 | 0.02 |
| `Beaver.Child` |  | 19.32 | 20.1% | 1208 | 16.0 | 306.6 | 0.02 |
| `Lodge` |  | 10.73 | 11.2% | 1219 | 8.8 | 309.5 | 0.01 |
| `FarmHouse` |  | 6.76 | 7.0% | 1208 | 5.6 | 306.7 | 0.01 |
| `Pine` |  | 3.41 | 3.6% | 1219 | 2.8 | 309.6 | 0.00 |

**Singleton time by mod** (tick, update and late-update singletons together; 'game' is Timberborn itself)

| Mod | ms/s | KB/s |
|---|---|---|
| game | 97.11 | 286.0 |
| kyler.lategameperformance | 21.37 | 99.3 |

## The slowest frames

`frames.csv` has a row for every slow frame and `spikes.csv` the biggest contributors to each; these are the worst 9. `Biggest parts` are the timed slots of the frame; `Blame` are the singletons that spent the most time in it.

| Frame | Tick | Frame ms | Speed | Ticks | GC | Save | Biggest parts | Blame |
|---|---|---|---|---|---|---|---|---|
| 7140 | 795 | 437 | 3 | 1 |  | yes | saveMs 430, entMs 2, updMs 2 | Timberborn.WaterSystem.WaterSimulator 1, Timberborn.CoreUI.PanelStack 1, Timberborn.Navigation.NavigationSynchronizer 0 |
| 5099 | 454 | 132 | 3 | 0 | yes |  | otherMs 128, entMs 2, updMs 2 | Timberborn.CoreUI.PanelStack 1, Timberborn.CameraSystem.CameraService 0, LateGamePerformance.RouteMapsBackground 0 |
| 1499 | 83 | 125 | 1 | 0 | yes |  | otherMs 122, updMs 2, entMs 1 | Timberborn.CoreUI.PanelStack 1, Timberborn.CameraSystem.CameraService 0, LateGamePerformance.RouteMapsBackground 0 |
| 2599 | 144 | 124 | 1 | 0 | yes |  | otherMs 121, updMs 2, entMs 1 | Timberborn.CoreUI.PanelStack 1, Timberborn.CameraSystem.CameraService 0, LateGamePerformance.RouteMapsBackground 0 |
| 3899 | 253 | 112 | 3 | 0 | yes |  | otherMs 108, entMs 2, updMs 2 | Timberborn.CoreUI.PanelStack 1, Timberborn.CameraSystem.CameraService 0, LateGamePerformance.RouteMapsBackground 0 |
| 6799 | 738 | 107 | 3 | 1 | yes |  | otherMs 100, entMs 2, singMs 2 | Timberborn.WaterSystem.WaterSimulator 1, Timberborn.CoreUI.PanelStack 1, Timberborn.Navigation.NavigationSynchronizer 0 |
| 699 | 38 | 105 | 1 | 0 | yes |  | otherMs 102, updMs 2, entMs 1 | Timberborn.CoreUI.PanelStack 1, Timberborn.CameraSystem.CameraService 0, LateGamePerformance.RouteMapsBackground 0 |
| 4699 | 387 | 99 | 3 | 0 |  |  | updMs 97, entMs 2, otherMs 0 | LateGamePerformance.RouteMapsBackground 95, Timberborn.CoreUI.PanelStack 1, Timberborn.CameraSystem.CameraService 0 |
| 3299 | 183 | 97 | 1 | 0 |  |  | updMs 96, entMs 1, otherMs 0 | LateGamePerformance.RouteMapsBackground 95, Timberborn.CoreUI.PanelStack 1, Timberborn.CameraSystem.CameraService 0 |

## What each measurement source could do

- allocation source: scripted counter
- processor times: unavailable in this sample

A source that says it produced nothing leaves its columns at 0; do not read a 0 in them as a measurement.

## Computer and game settings

- **Computer:** Sample CPU x16, Sample GPU 8192 MB, 32768 MB RAM
- **Display:** vSync on, 60 Hz, 2560x1440
- **Garbage collector:** mode=Enabled incremental=False

## Mods enabled

| Id | Name | Version |
|---|---|---|
| Harmony | Harmony | v2.4.1 |
| kyler.lategameperformance | Late Game Performance | v0.4.9 |
| kyler.performancelog | Performance Log | v0.1.0 |

Which mod patches which hot method of the game is in the header of `frames.csv` (`# patch|` lines).

## Files

- `README.md`: what the files and columns mean.
- `summary.md`: this file.
- `frames.csv`: one row per slow frame (`F`) and per summary window (`S`).
- `profile.csv`: per window, the time and allocation of each singleton, entity kind, component and watched method.
- `spikes.csv`: for each slow frame, the singletons that took most of it.
- `events.csv`: saves, loading and other one-off events.

Folder: `sample`
