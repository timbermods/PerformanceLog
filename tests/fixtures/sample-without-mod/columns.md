# Columns of the Performance Log files (version 0.1.3)

Generated from the mod's own column definitions. `README.md` explains how to read the numbers; this file only says what each column is.

## frames.csv

One row per slow frame (`F`) and per summary window (`S`). Every row has every column. In `S` rows times and Unity's figures are averages per frame, and allocation, counts and the two histograms are totals over the window's frames (the third column below says which). `fh0`..`fh16` count frames per frame-time bucket; the upper edges in milliseconds are 4, 6, 8.5, 11.5, 14, 17.5, 21, 25, 30, 35, 42, 50, 75, 100, 200, 400 (the last bucket is everything above). `th0`..`th5` count frames that ran 0, 1, 2, 3-4, 5-9 and 10 or more ticks.

| Column | Unit | In S rows | Meaning |
|---|---|---|---|
| `type` |  | last | F: one frame that took at least the slow-frame threshold. S: a summary of every frame in one window. |
| `frame` | count | last | Frame number since the log started. |
| `tick` | count | last | Simulation ticks since the log started (not the save's own tick count). |
| `utcMs` | ms | last | Wall clock, milliseconds since 1970 UTC. |
| `frames` | count | total | How many frames the row covers: 1 in F rows, the count in S rows. |
| `frameMs` | ms | average per frame | Frame time: from the start of one frame to the start of the next, so it includes drawing and the wait for vertical sync. |
| `maxFrameMs` | ms | largest | The slowest frame in the row. |
| `ticks` | count | total | Simulation ticks run in the row's frames. More than 1 per frame means the game is catching up or running at a high speed. |
| `buckets` | count | total | Tick buckets run. The game splits a tick into 129 buckets (one for the singletons, 128 for entities) and runs as many per frame as time allows. |
| `speed` | x | last | The game speed: 0 paused, 1, 2, 3 and so on. |
| `paused` | frames | total | 1 if the game was paused (speed 0) in the frame. |
| `saving` | frames | total | 1 if a save ran in the frame. |
| `unfocused` | frames | total | 1 if the game window was in the background. The system throttles a background window, so such frames say little. |
| `tickMs` | ms | average per frame | The tick loop itself: Ticker.Update minus the parts below. The game's own tick bookkeeping, and other mods' patches on the tick loop. |
| `singMs` | ms | average per frame | The once-per-tick singletons (the game's and mods'), which run once at the start of every tick. |
| `entMs` | ms | average per frame | Every entity's tick: beavers, buildings, plants and the rest, across all of the tick's entity buckets. |
| `parWaitMs` | ms | average per frame | The game thread waiting for the parallel part of the tick (pathfinding, water and so on) to finish on the worker threads. |
| `parStartMs` | ms | average per frame | The game thread starting the parallel part of the tick: scheduling work for the worker threads. |
| `updMs` | ms | average per frame | Per-frame singleton updates (IUpdatableSingleton): the user interface, camera, input and many mods. |
| `lateMs` | ms | average per frame | Per-frame late singleton updates (ILateUpdatableSingleton). |
| `saveMs` | ms | average per frame | Saving the game (an autosave or a manual save). |
| `otherMs` | ms | average per frame | The rest of the frame: drawing, animation, other scripts, other mods, the system. Every column above adds up with this one to frameMs. |
| `tickKB` | KB | total | Kilobytes allocated inside the slot of the same name. (Coarse: see the allocation source in the capability lines.) |
| `singKB` | KB | total | Kilobytes allocated inside the slot of the same name. (Coarse: see the allocation source in the capability lines.) |
| `entKB` | KB | total | Kilobytes allocated inside the slot of the same name. (Coarse: see the allocation source in the capability lines.) |
| `parWaitKB` | KB | total | Kilobytes allocated inside the slot of the same name. (Coarse: see the allocation source in the capability lines.) |
| `parStartKB` | KB | total | Kilobytes allocated inside the slot of the same name. (Coarse: see the allocation source in the capability lines.) |
| `updKB` | KB | total | Kilobytes allocated inside the slot of the same name. (Coarse: see the allocation source in the capability lines.) |
| `lateKB` | KB | total | Kilobytes allocated inside the slot of the same name. (Coarse: see the allocation source in the capability lines.) |
| `saveKB` | KB | total | Kilobytes allocated inside the slot of the same name. (Coarse: see the allocation source in the capability lines.) |
| `otherKB` | KB | total | Kilobytes allocated outside every timed slot. |
| `gcDelta` | count | total | Garbage collections during the row's frames. A frame with one is usually one long frame. |
| `heapMB` | MB | last | Managed memory in use. |
| `allocKB` | KB | total of the increases | How much heapMB grew since the previous frame. Negative (not counted in S rows) means a collection freed memory. |
| `probeUs` | us | average per frame | What closing this frame cost this mod. |
| `overheadUs` | us | average per frame | Estimated cost of this mod's timers, samples and patches in the frame. Add probeUs for the whole cost of measuring. |
| `dropped` | rows | last | Rows lost because the file writer fell behind. Should be 0. |
| `entities` | count | total | Entity ticks that ran: each tickable entity counts once per tick. |
| `patchCalls` | count | total | Executions of this mod's own Harmony patches. Only used to estimate what measuring costs. |
| `parTickMs` | ms | total | The game's own figure for how long its parallel tick took, from starting it to the last worker finishing. Compare with parWaitMs. |
| `mainCpuMs` | ms | average per frame | Processor time the game thread used in the frame. Windows only. Charged in scheduler quanta, so one frame's figure is coarse and only S rows are exact. |
| `mainMcyc` | Mcycles | average per frame | Processor cycles the game thread used in the frame, in millions. Windows only. |
| `procCpuMs` | ms | average per frame | Processor time the whole process used in the frame (all threads). Windows only. |
| `plTime` | ms | average per frame | Unity frame phase: Unity's time update. The wait for the previous frame to be presented can be here. |
| `plInit` | ms | average per frame | Unity frame phase: Unity's initialization phase. |
| `plEarly` | ms | average per frame | Unity frame phase: Early update (input). |
| `plFixed` | ms | average per frame | Unity frame phase: Fixed update (physics). |
| `plPre` | ms | average per frame | Unity frame phase: Pre-update. |
| `plUpdate` | ms | average per frame | Unity frame phase: Update: every script's Update, including the game's tick loop and the singleton updates. |
| `plLate` | ms | average per frame | Unity frame phase: Pre late update: every script's LateUpdate, animation and cameras. The game's own late singletons (lateMs) are only a small part of it, so a big plLate with a small lateMs is work in Unity or another script that this log cannot name. |
| `plPost` | ms | average per frame | Unity frame phase: Post late update: drawing, presenting the frame and the wait for vertical sync. |
| `prGcBytes` | bytes | average per frame | Unity profiler: bytes allocated on the managed heap in the last frame (0 if the counter is not available). |
| `prGcCount` | count | average per frame | Unity profiler: number of managed allocations in the last frame. |
| `prDraw` | count | average per frame | Unity profiler: draw calls. Where Unity 6 has no single counter, the sum of its standard, instanced, SRP-batcher, indirect and BRG draw call counters (the header says which were found). |
| `prSetPass` | count | average per frame | Unity profiler: set-pass calls (material switches). |
| `prBatches` | count | average per frame | Unity profiler: batches (Unity 6 players do not have this counter, so it stays 0 there). |
| `prTris` | count | average per frame | Unity profiler: triangles drawn. |
| `ftCpu` | ms | average per frame | Unity frame timing: the frame on the processor. 0 when the game's player settings leave frame timing off. |
| `ftMain` | ms | average per frame | Unity frame timing: the main thread. |
| `ftRender` | ms | average per frame | Unity frame timing: the render thread. |
| `ftGpu` | ms | average per frame | Unity frame timing: the graphics card. |
| `ftWait` | ms | average per frame | Unity frame timing: the main thread waiting to present (vertical sync, or a graphics card that is behind). |
| `monoHeapMB` | MB | last | Unity's managed heap, reserved. Read only when a row is written. |
| `monoUsedMB` | MB | last | Unity's managed heap, in use. Read only when a row is written. |
| `nativeMB` | MB | last | All memory Unity has allocated, including the native side. Read only when a row is written. |
| `workingMB` | MB | last | The process's working set (memory the game holds in RAM). Read only when a row is written. |
| `colEntities` | count | last | Colony size: all entities in the game. Read only when a row is written. |
| `colBeavers` | count | last | Colony size: beavers alive. Read only when a row is written. |
| `colBots` | count | last | Colony size: bots alive. Read only when a row is written. |
| `colDay` | day | last | Colony size: the day number. Read only when a row is written. |
| `fh0` | frames | total | Frames whose time fell in frame-time bucket 0 (the bucket edges are in the '# histogram/frameEdgesMs' header line). Vertical sync shows as peaks at 16.7 and 33.3 ms. |
| `fh1` | frames | total | Frames whose time fell in frame-time bucket 1 (the bucket edges are in the '# histogram/frameEdgesMs' header line). Vertical sync shows as peaks at 16.7 and 33.3 ms. |
| `fh2` | frames | total | Frames whose time fell in frame-time bucket 2 (the bucket edges are in the '# histogram/frameEdgesMs' header line). Vertical sync shows as peaks at 16.7 and 33.3 ms. |
| `fh3` | frames | total | Frames whose time fell in frame-time bucket 3 (the bucket edges are in the '# histogram/frameEdgesMs' header line). Vertical sync shows as peaks at 16.7 and 33.3 ms. |
| `fh4` | frames | total | Frames whose time fell in frame-time bucket 4 (the bucket edges are in the '# histogram/frameEdgesMs' header line). Vertical sync shows as peaks at 16.7 and 33.3 ms. |
| `fh5` | frames | total | Frames whose time fell in frame-time bucket 5 (the bucket edges are in the '# histogram/frameEdgesMs' header line). Vertical sync shows as peaks at 16.7 and 33.3 ms. |
| `fh6` | frames | total | Frames whose time fell in frame-time bucket 6 (the bucket edges are in the '# histogram/frameEdgesMs' header line). Vertical sync shows as peaks at 16.7 and 33.3 ms. |
| `fh7` | frames | total | Frames whose time fell in frame-time bucket 7 (the bucket edges are in the '# histogram/frameEdgesMs' header line). Vertical sync shows as peaks at 16.7 and 33.3 ms. |
| `fh8` | frames | total | Frames whose time fell in frame-time bucket 8 (the bucket edges are in the '# histogram/frameEdgesMs' header line). Vertical sync shows as peaks at 16.7 and 33.3 ms. |
| `fh9` | frames | total | Frames whose time fell in frame-time bucket 9 (the bucket edges are in the '# histogram/frameEdgesMs' header line). Vertical sync shows as peaks at 16.7 and 33.3 ms. |
| `fh10` | frames | total | Frames whose time fell in frame-time bucket 10 (the bucket edges are in the '# histogram/frameEdgesMs' header line). Vertical sync shows as peaks at 16.7 and 33.3 ms. |
| `fh11` | frames | total | Frames whose time fell in frame-time bucket 11 (the bucket edges are in the '# histogram/frameEdgesMs' header line). Vertical sync shows as peaks at 16.7 and 33.3 ms. |
| `fh12` | frames | total | Frames whose time fell in frame-time bucket 12 (the bucket edges are in the '# histogram/frameEdgesMs' header line). Vertical sync shows as peaks at 16.7 and 33.3 ms. |
| `fh13` | frames | total | Frames whose time fell in frame-time bucket 13 (the bucket edges are in the '# histogram/frameEdgesMs' header line). Vertical sync shows as peaks at 16.7 and 33.3 ms. |
| `fh14` | frames | total | Frames whose time fell in frame-time bucket 14 (the bucket edges are in the '# histogram/frameEdgesMs' header line). Vertical sync shows as peaks at 16.7 and 33.3 ms. |
| `fh15` | frames | total | Frames whose time fell in frame-time bucket 15 (the bucket edges are in the '# histogram/frameEdgesMs' header line). Vertical sync shows as peaks at 16.7 and 33.3 ms. |
| `fh16` | frames | total | Frames whose time fell in frame-time bucket 16 (the bucket edges are in the '# histogram/frameEdgesMs' header line). Vertical sync shows as peaks at 16.7 and 33.3 ms. |
| `th0` | frames | total | Frames that ran 0 ticks. |
| `th1` | frames | total | Frames that ran 1 tick. |
| `th2` | frames | total | Frames that ran 2 ticks. |
| `th3` | frames | total | Frames that ran 3 to 4 ticks. |
| `th4` | frames | total | Frames that ran 5 to 9 ticks. |
| `th5` | frames | total | Frames that ran 10 or more ticks. |

## profile.csv

One row per key per window. The last three columns (`name`, `assembly`, `mod`) are text written after the numbers. `kind` is one of:

- `tick-singleton`: A singleton's Tick (once per simulation tick). Every call is timed.
- `update-singleton`: A singleton's UpdateSingleton (once per frame). Every call is timed.
- `late-singleton`: A singleton's LateUpdateSingleton (once per frame). Every call is timed.
- `parallel-start`: A parallel singleton's StartParallelTick on the game thread: scheduling only, the work itself runs on worker threads and is not visible here. Every call is timed.
- `entity`: All the ticks of one kind of entity (a prefab such as a beaver or a farm house). Only every Nth call is timed and the result is scaled up (see 'sampled').
- `component`: All the ticks of one kind of entity component (a class such as Walker). Only every Nth call is timed and the result is scaled up. Only recorded with Profile = deep.
- `method`: One method from the Watch list in the config, or (with AutoWatch) another mod's patch method on a hot method, timed including everything inside it and every patch on it. Calls are counted exactly; the first call in each window and about every Nth after it are timed. N is chosen from the method's own calls in the last window it ran in and the share of the budget it splits with the other watched methods that ran then, so it widens while the method is busy or many watched methods are. It has a row for every window it ran in.
- `load`: A singleton's Load while the game was loading (one row per singleton, window 0).
- `load-non-singleton`: A non-singleton loader's LoadNonSingletons while the game was loading (window 0).
- `post-load`: A singleton's PostLoad while the game was loading (window 0).
- `post-load-non-singleton`: A non-singleton post-loader's PostLoadNonSingletons while the game was loading (window 0).

| Column | Unit | In S rows | Meaning |
|---|---|---|---|
| `kind` |  | last | What the row measures: tick-singleton, update-singleton, late-singleton, parallel-start, entity, component, method, or a load step. |
| `window` | count | last | Profile window number. 0 is the load (steps taken while the game loaded). |
| `tick` | count | last | Simulation ticks since the log started, at the end of the window. |
| `id` |  | last | The key's number, the same in profile.csv and spikes.csv. |
| `calls` | count | total | Calls in the window. Exact for singletons and watched methods, estimated (samples times the interval) for entities and components. |
| `sampled` | count | total | Calls that were actually timed. ms and allocKB are scaled up from these, so a small number means a rough estimate. 0 (a watched method whose timed calls all threw) means none was: ms and allocKB are then unknown, not zero. |
| `ms` | ms | total | Time spent in all the calls in the window (scaled up from the sampled ones), including everything inside them. |
| `allocKB` | KB | total | Managed memory allocated by all the calls in the window (scaled up). Coarse when the allocation source is the heap size. For load steps (window 0) it is how much the managed heap grew during the step; a collection in the middle makes it read low. |
| `maxMs` | ms | largest | The slowest timed call in the window. A large value with a small ms is a rare hitch. |
| `name` |  | last | The singleton's or component's class, the entity's prefab name, or the method. Written after the numbers. |
| `assembly` |  | last | The DLL the class lives in. |
| `mod` |  | last | The mod that DLL belongs to, or 'game' for the game itself. Best effort. |

## spikes.csv

For each slow frame, the singletons that took the most of it. The last two columns (`name`, `mod`) are text written after the numbers.

| Column | Unit | In S rows | Meaning |
|---|---|---|---|
| `frame` | count | last | The frame number of the slow frame (matches an F row in frames.csv). |
| `tick` | count | last | Simulation ticks since the log started. |
| `utcMs` | ms | last | Wall clock, milliseconds since 1970 UTC. |
| `frameMs` | ms | last | The slow frame's time. |
| `rank` |  | last | 1 is the biggest contributor in this frame. |
| `kind` |  | last | The kind of key (only kinds that are timed on every call appear here). |
| `id` |  | last | The key's number, the same in profile.csv. |
| `ms` | ms | last | Time the key spent in this frame. |
| `share` | % | last | That time as a percentage of the frame. |
| `name` |  | last | The key's class. Written after the numbers. |
| `mod` |  | last | The mod it belongs to, or 'game'. |

## events.csv

`utcMs,tick,kind,ms,detail`. `kind` is `session-start`, `session-end`, `save`, `load`, `load-phase` or `warning`. `detail` is text in quotes.
