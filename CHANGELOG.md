# Changelog

## 0.1.3 (preview)

**The defaults now capture the most detail a session can hold without anyone touching a setting**, so every recording made from a plain
install is as informative as this mod can make it.

- `Profile` default: `standard` → **`deep`**. Every kind of entity component is now sampled (`Walker`, `Workplace`, and so on), not just
  entity kinds. This is the first time `deep` mode's patch on `MeteredTickableComponent.Tick` will run in a real game outside a hand-set
  `.cfg` file; see `docs/TESTING.md`.
- `SpikeContributors` default: `5` → **`8`** (`Profile.TopK`, the most a slow frame's `spikes.csv` rows can hold), tied to `Profile.TopK`
  so the two can never drift apart.
- `OverheadBudgetPercent` default: `0.5` → **`1`**. The sampling intervals this drives (entity, component, allocation) are chosen to stay
  under this budget; doubling it lets them run about twice as dense, so a `profile.csv` row rests on more real samples and less scaling
  up. Still well under the 2% the report itself calls "measuring costs too much."
- `SlowFrameMs`, `SummarySeconds`, `ProfileSeconds` and `MaxSlowRowsPerMinute` are unchanged: they trade off row count and file size
  rather than the depth of any one row, and the budget system above already throttles itself if measuring gets expensive, so there was no
  clear "more detail" case for moving them, only a "more rows" one.
- Expect a real, measurable increase in the mod's own cost from this alone — deep's component sampling plus double the sampling budget.
  Nobody had played it when it was released (it has been since: see `docs/TESTING.md`); step 10 of the five-minute check there measures
  what it costs.

## 0.1.2 (preview, not yet played)

An in-game settings page, hooked into the **Mod Settings** mod (now a required dependency, like Harmony).

- `SlowFrameMs`, `SummarySeconds`, `ProfileSeconds`, `OverheadBudgetPercent`, `SpikeContributors` and `MaxSlowRowsPerMinute` can now be set from
  Timberborn's Mod Settings menu, from the main menu or in a running game, and apply from the next game or save loaded — no restart.
  `PerformanceLog.cfg` still works and seeds the menu's first value; once a setting is touched in the menu, the menu owns it.
- `Enabled`, `Profile`, `Watch` and `OutputFolder` stay `PerformanceLog.cfg`-only: they decide which Harmony patches this mod makes, which is
  settled in `Plugin.StartMod`, before Bindito (and so Mod Settings) exists, so putting them in the menu would mean either re-patching the game
  live or always installing the entity-tick patch even when `Profile = off` asks not to — this release does neither. The menu's own note says so.
- `source/Game/Settings.cs` is the only file that touches Mod Settings types (`PerformanceSettings`, a `ModSettingsOwner`), so the rest of the mod
  does not depend on that assembly.

## 0.1.1 (preview)

Fixes for what the first recording from a real game showed (Timberborn 1.1.2.4, Unity 6000.5.5f1, nine mods, 31 minutes, a normal exit). Everything below was found by reading
that recording. Each one that can be checked outside the game has a check that fails without the fix (the Unity draw-call counters, the patch cost measurement and the allocation note need the game).

- **The mod swapped its timing wrappers into the game's singleton arrays about four times every frame** (488601 swaps in 122150 frames). The game keeps two singleton services alive
  (the application's and the game's) and they alternate each frame, and the mod remembered only one. The cost was made before the timed parts, so it sat in `otherMs` and `otherKB` (the recording's `otherKB` is about
  29 KB per frame, most of it probably this), and 0.1.0's `overheadUs` did not include it. Now each service is wrapped once (`Instrumentation.FirstTime`). **In 0.1.0 recordings, distrust the allocation
  figures and the garbage-collection section**; `tools/perflog.py` says so.
- **Files are no longer held open.** 0.1.0 kept `frames.csv`, `profile.csv`, `spikes.csv` and `events.csv` open for writing, so zipping or copying the folder while the game ran silently left them
  out (and a folder listing showed size 0). They are now opened, appended to and closed on each write, shared with readers, and retried if someone else holds them.
- **Every singleton of the game itself was labeled with no mod ("(unknown)")**; the mod map was installed without the game's own assemblies. They are now `game`. The analysis tool applies the same
  rule to older recordings.
- **`workingMB` was always 0** (Unity's Mono reports 0 for the process's memory). It now asks Windows, and the header says where the figure comes from (`# capability|workingSet|...`).
- **Loading steps now record how much the managed heap grew** during each one (`allocKB` of the load rows, "heap grew MB" in `summary.md`, and a finding in the report). The first recording's
  heap went from 54 MB to 1717 MB while loading; nothing said which steps did it.
- **`# capability-final|patchCalls|SingletonLifecycleService.LoadAll|0|never ran` was wrong**: it ran, and the counter was cleared when the session started in the middle of the load.
- **Draw calls**: Unity 6 has no counter called `Draw Calls Count` (or `Batches Count`, or `GC Allocated In Frame`; checked against the strings in this game's `UnityPlayer.dll`). `prDraw` is now the sum of
  Unity 6's `Standard`, `Standard Instanced`, `SRP Batcher`, `Standard Indirect`, `BRG` and `Null Geometry` draw call counters, and the header names the ones it found.
- The allocation source line now says why the exact counter was not used. `GC.GetAllocatedBytesForCurrentThread` is present in this game's `mscorlib.dll` and Mono runtime, but 0.1.0's probe
  rejected it without saying why, so allocation was the size of the managed heap; the next recording says why.
- The cost of a Harmony patch is now measured after warming up and as the best of several rounds. 0.1.0's figure read 0 (most likely the unwarmed baseline included compiling the method), which left
  `overheadUs` using a default of 40 ns per patch call.
- The report tells you about known defects of the version that made the recording (`KNOWN ISSUE` lines), and no longer reports the false "LoadAll never ran" for 0.1.0.

## 0.1.0 (preview)

First release. A standalone mod that records where a Timberborn session's time and memory go, built from the frame rate log of the BeaverBuddies Stability Fork
(`1.0.10-perflog-preview2`), rebuilt to hook the game itself.

- Per-frame timing with exclusive slots: tick loop, once-per-tick singletons, entity ticks, wait for and start of the parallel tick, per-frame updates and late updates, saving.
  Unity's frame phases, processor time, profiler counters and frame timing where the build provides them; garbage collection, heap and allocation.
- A profile of every singleton (timed on every call), every kind of entity (sampled inside an overhead budget), every entity component (`Profile = deep`) and any method named in
  the config (`Watch`), each tagged with the mod it belongs to. Slow frames are attributed to the singletons that took their time (`spikes.csv`).
- Saves with their stages, and loading: the time of every singleton's `Load` and `PostLoad`.
- The computer, game and settings, every enabled mod with its version, which mod patches which hot method, what each measurement source could do, and what measuring costs.
- Each session folder holds `summary.md` (one page, refreshed every minute), `README.md` (how to read and diagnose), `columns.md`, `frames.csv`, `profile.csv`, `spikes.csv` and `events.csv`.
- Made to coexist with BeaverBuddies: timing wrappers go into the game's singleton arrays on the first tick and frame (after every other mod's `Load` patches), the deferred save is timed where the world is written, and ticks are counted by entity buckets.
- Nothing a finished session held stays alive in the menu; slow-frame rows are limited per minute (`MaxSlowRowsPerMinute`); the summary text is made on the writer thread; `Profile = off` leaves the entity tick unpatched.
- `tools/perflog.py`: `report` (findings with evidence and next steps), `compare` (two sessions, with what else differed), `list`. Standard library only.
