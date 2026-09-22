# Timberborn performance log: how to read this folder

This folder is one recording of one Timberborn session, made by the **Performance Log** mod (version {{VERSION}}). It holds measurements
only. The mod never changes what the game simulates, so a recording shows the game as it was with the mods that were enabled.

**If you are a Claude chat asked to find out why this session was slow: read `summary.md` first, then this file's "Diagnosing"
section, then dig into the CSV files with the questions it gives you.** Nothing here is a verdict. The numbers say where time and
memory went; what to change is a judgment you make from them, and you should say how sure you are.

## The files

| File | What it is |
|---|---|
| `columns.md` | The definition of every column of the CSV files. |
| `summary.md` | The whole session in one page: frame rate, where an average frame goes, ticks, memory, the biggest singletons/entities/mods, the worst frames, what each measurement source could do, the computer and the mod list. **Start here.** Rewritten about once a minute and at exit. |
| `frames.csv` | The frame log. One row per slow frame (`F`) and one per summary window (`S`, every few seconds). A `#` header (skip lines starting with `#`, e.g. pandas `comment='#'`) says which game, mods and computer this was, which mod patches which hot method, and what each measurement source could do. |
| `profile.csv` | Long table. Per window and per key: how much time and memory each singleton, entity kind, component and watched method used. |
| `spikes.csv` | For each slow frame, the singletons that took most of it, biggest first. Join to `frames.csv` on `frame`. |
| `events.csv` | One-off events with the time and game tick: session start and end, each save with its stages, loading. |

## Rules for reading the numbers

1. **Think in milliseconds per frame, not frames per second.** `frameMs` is the time from the start of one frame to the start of the next,
   so it includes drawing and the wait for vertical sync. 16.7 ms is 60 fps.
2. **Time is exclusive.** `tickMs`, `singMs`, `entMs`, `parWaitMs`, `parStartMs`, `updMs`, `lateMs`, `saveMs` and `otherMs` never overlap and add
   up to `frameMs`. A part that sits inside another is taken out of the outer one.
3. **The game speed matters.** At speed 3 the game runs about three times the simulation work per second, so frames are longer and there are
   more `ticks` per frame. Only compare frames at the same `speed`, and never treat a paused frame (`paused` = 1) as gameplay.
4. **Ignore `unfocused` = 1 frames.** The system throttles a window in the background.
5. **Ignore the first half minute.** Loading and shader warm-up make the first frames slow. `events.csv` says when the session started.
6. **A 0 may mean "not measured", not "free".** The header's `# capability|` lines and the `# capability-final|` lines at the end say which
   sources worked on this computer. `prGcBytes`, `ftGpu` and friends are 0 when Unity's release build does not provide them; `mainCpuMs` is 0
   off Windows.
7. **`profile.csv` is partly estimated.** Singletons are timed on every call. Entity kinds, components and watched methods are timed on every Nth
   call and scaled up (`sampled` says how many real timings a row rests on; a small number means a rough figure, and a watched method's row with
   `sampled` 0 was not timed at all, so its `ms` is unknown, not 0). `allocKB` is coarse when the
   allocation source is the heap size (see the `allocSource` capability line).
8. **Measuring costs something.** `overheadUs` (the estimate) plus `probeUs` (closing the frame) are microseconds per frame that the mod itself used.
   If they are more than about 2% of `frameMs`, say so before trusting small differences.
9. **The `mod` column is best effort.** It is found from the DLL a class lives in. `game` is Timberborn itself; an empty value means unknown.
10. **A window is a time span, not a number of ticks.** `S` rows cover the same wall-clock time (`summarySeconds` in the header), so a paused game still writes them.

## Diagnosing

Start by writing down what the complaint is, because the causes differ:

| The complaint | Look at |
|---|---|
| Low frame rate all the time (say 20 fps at speed 1) | `S` rows: which part of the frame is big? Follow the tree below. |
| Regular hitches (every N seconds) | `F` rows: is there a rhythm? Compare with autosave (`saving`, `events.csv`), garbage collection (`gcDelta`), and any mod's periodic work (`spikes.csv`). |
| Long freezes (a second or more) | `F` rows with a huge `frameMs`: `saving`, `gcDelta`, `plPost`, or one singleton in `spikes.csv`. |
| Fast at speed 1, bad at speed 3 | Simulation-bound: `ticks` per frame > 1 and the tick parts of the frame. Follow the simulation branch. |
| Slow loading | `profile.csv` rows of kind `load`, `post-load` (window 0) and the `# milestone|` lines: which step and which mod took the time. |
| The game gets slower the longer it runs | Compare `S` rows early and late: does `heapMB`, `colEntities`, or a singleton's `ms` grow? |

### Where does an average frame go? (`S` rows, or the table in `summary.md`)

1. **`otherMs` is the biggest part**, and `plPost` (or `ftWait`, `ftGpu`) is large: the game thread is waiting for the graphics card or vertical
   sync, not computing. Look at `mainCpuMs` against `frameMs`: a game thread far under 100% busy confirms it. **First rule out that this is vertical sync
   doing its job:** with vsync on (`# display:` says `vSyncCount=1`) frames sit at the refresh interval (16.7 ms at 60 Hz) whenever the computer has time to
   spare, and that wait is healthy, not slowness. It only points at the graphics card when the frames are *longer* than the sync interval (or vsync is off) and the
   game thread is still not busy. Then check `prDraw`/`prSetPass`/`prBatches`/`prTris` (a lot of drawing), the resolution and `# gpu:`. This is a
   graphics-settings problem, not a mod problem, unless a mod adds drawing.
   When frames are pinned at the sync interval, **compare work, not frame time**: the sum of the timed parts (everything but `otherMs`) is what a change in the game or a mod moves.
   **If `otherMs` is big but `plPost` is not**, the time is code, not drawing. `summary.md` (and `perflog.py report`) split `otherMs` by phase:
   `plUpdate` less the timed parts that run in it (`tickMs` to `parStartMs`, `updMs`) is other scripts' Update (the game's own MonoBehaviours, mods'
   scripts, coroutines); `plLate` less `lateMs` is other work in Unity's LateUpdate phase (animation, UI Toolkit, scripts' LateUpdate), which is not
   necessarily a mod. A save runs in `plLate` for the game's own and in `plUpdate` when a mod defers it to the end of a tick.
2. **`updMs` is big**: per-frame singleton updates (the user interface, the camera, input, and many mods). `profile.csv` rows of kind
   `update-singleton` name them, with `mod`.
3. **`tickMs`, `singMs`, `entMs`, `parWaitMs`, `parStartMs` are big**: the simulation. Divide by `ticks` to get ms per tick.
   - `singMs`: rows of kind `tick-singleton` (once per tick, timed on every call).
   - `entMs`: rows of kind `entity` (which prefab) and, with Profile = deep, `component` (which class: a `Walker` is beaver movement, and so on).
   - `parWaitMs`: the game thread waits for the worker threads. `parTickMs` is the game's own figure for how long they took. If `parWaitMs` is large the
     slow part is on the workers (pathfinding, water and so on), which this mod cannot see into; `parallel-start` rows show only the scheduling.
   - `tickMs` itself (the tick loop minus its parts) is large when other mods patch the tick loop. Check the `# patch|` lines for `Ticker.Update` and
     `TickableBucketService.TickBuckets`.
4. **`saveMs`**: the game saving. `events.csv` has each save with its stages. (A mod that defers the save to the end of a tick, such as BeaverBuddies, makes the `queued save` event read about 0 ms; the
   `save (writing the world)` event is the real one.)
5. **Garbage collection**: `gcDelta` > 0 in slow frames; a sawtooth `heapMB`; large allocation per tick (`tickKB`, `singKB`, `entKB`, and `allocKB` per
   second in `summary.md`). Long collection pauses on one computer and not another point at settings (`# gc:`, `# bootconfig|` for
   `gc-max-time-slice`, `# cmdline|`). Which singleton or entity kind allocates the most is in `profile.csv` (`allocKB`).

### Which mod is it?

- `profile.csv` and `spikes.csv` carry `mod`. Add up `ms` per mod within a kind (do not add kinds together where they overlap: `entity` and
  `component` rows both live inside `entMs`).
- `# patch|` lines in the `frames.csv` header list which mod patches which hot method (`hot`), which methods several mods patch (`shared`), every
  other method another mod patches (`other`), and the order they run in. A mod patching `Ticker.Update` or `TickableEntity.Tick` puts its cost
  into `tickMs` or `entMs` without a row of its own; a `Watch` entry in the config (see the repo's README) times such a method directly (kind `method`).
- **A mod's patch on an event handler is charged to whatever raised the event.** The game calls a handler (an `[OnEvent]` method, or one hooked to a
  C# event, such as `StockpileVisualizers.OnInventoryChanged`) at once, inside the code that changed something, so the patch has no row of its own: its
  time is in whatever was running (an entity's tick, in `entMs` and that entity's `entity` and `component` rows; a singleton's row; or `otherMs` when
  nothing timed was running). An event posted while the game loads waits for `EventBus`'s `post-load` step, so its `[OnEvent]` handlers are charged
  there. The `# patch|other|` and `# patch|shared|` lines name these handlers in full; a `Watch` entry with that name times one, with every patch on it
  (kind `method`).
- With `AutoWatch = true` in the config (the `# capability|autoWatch|` line says whether it was on and what it did), the patch methods other mods put on
  hot methods are timed themselves: a `method` row each, named after the patch method (its class says what it is for, e.g.
  `...TickableEntityTickPatcher.Prefix(TickableEntity)`, and `mod` is the mod that patches), and a `# watch|<method>|watching|auto|<prefix on
  Namespace.Type.Method>|<owner>` line each in the header. It takes the patches on the methods behind the profile's own rows first (a singleton's
  `Tick`, `UpdateSingleton`, `LateUpdateSingleton` or `StartParallelTick`, an entity's or component's `Tick`, whose time is otherwise inside a row that
  names the game or the singleton's own mod), then the rest of the hot methods, in name order, in the Watch slots the config's `Watch` entries left
  (40 in all), leaving out patches on the random numbers, `Guid.NewGuid` and `DateTime.ToString`; the `# watch|` lines of the ones left out say why. A patch method's time is inside the row of what it patches, so do not add the two.
  One that `# capability-final|autoWatch|` lists as never seen called was either not called, called only off the game thread (the watch times the game
  thread only), or so small that the runtime copied it into the method it patches, where no watch can see it: a missing row there is not a measurement of 0.
  A `# watch|` line that says `(can replace it)` is a prefix that returns a bool: when it returns false the game's own method does not run, and the
  prefix's row holds the work it did instead, so that time is the mod doing the game's job, not cost on top of it (compare a recording without that
  mod to see what it saves or costs in all). A prefix that replaces a tick loop (`TickableBucketService.TickBuckets`, say) holds nearly the whole tick.
- To be sure it is a mod, **compare two recordings** of the same save at the same speed with and without it:
  `python tools/perflog.py compare <folder A> <folder B>` (in the Performance Log repository). Say what else differed.

### Before you conclude

- Is the sample big enough (a few minutes at one speed, more than a handful of slow frames)?
- Was the game window focused, and was the game paused a lot (`paused`, and the speed table in `summary.md`)?
- Were the computers or mod lists the same, if you are comparing? The header lists every mod and version.
- Did the mod itself cost much (`overheadUs`)?

## What the mod cannot tell you

- What the worker threads do inside the parallel tick, or what Unity does inside a frame beyond its phases.
- The cost of a mod that hooks the game without Harmony (MonoMod, native detours): it does not appear in `# patch|` lines, only in the parts of the frame it
  runs in.
- Exact allocation, unless the allocation source says `exact`.
- Anything before the game finished loading, except the coarse `# milestone|` marks and the load steps in `profile.csv`.

## Columns

`columns.md` in this folder defines every column of `frames.csv`, `profile.csv` and `spikes.csv`, with its unit and how a summary row combines it. It
is generated by the mod from its own column list, so it always matches the files. Values in `S` rows are averages per frame for times and Unity's figures,
and totals for allocation, counts and the two histograms. The frame-time histogram's bucket edges in milliseconds are {{FRAME_EDGES}} (`fh0`..`fh16`;
the last bucket is everything above), and `th0`..`th5` count frames that ran 0, 1, 2, 3-4, 5-9 and 10 or more ticks.

`profile.csv` `kind` is one of:

{{PROFILE_KINDS}}

`events.csv` is `utcMs,tick,kind,ms,detail`. `kind` is `session-start`, `session-end`, `save`, `load`, `load-phase` or `warning`; `detail` is text in quotes.
