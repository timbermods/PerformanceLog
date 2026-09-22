# What has been checked, and how to check the rest in a game

Version 0.1.0 was the first to run in a game (the first recording, below). 0.1.1 fixed what that showed, 0.1.2 added an in-game settings page, and 0.1.3
changed the defaults to `Profile = deep` and a wider sampling budget, for the most detail with nothing configured. 0.1.1 and 0.1.3 have both been played
since. A 0.1.3 recording (below) shows the `deep` default working, and every 0.1.1 fix that a recording can show. This is the honest list.

## Verified by the automated checks

`dotnet run --project tests -c Release` (91 checks) and `python -m unittest discover -s tools -p "test_perflog.py"` (44 checks).

| What | How |
|---|---|
| Frame accounting: slots are exclusive and add up to the frame; unbalanced scopes; other threads ignored; allocation attribution; flags; ticks and buckets; Unity phases; summaries and histograms | Real `Probe` against a scripted clock (`CoreTests`) |
| The per-frame path allocates nothing | `GC.GetAllocatedBytesForCurrentThread` around 2000 frames; also for a wrapper with the log off |
| Failure containment: a failing clock switches the probe off, a full ring drops rows and counts them | `CoreTests` |
| The profile: exact singleton timing, scaled sampling, random gaps that do not alias with a repeating pattern, budget adaptation, spike attribution, mod resolution | `ProfileTests` |
| The files: header, columns, invariant number format in any language, text tails, events, a file rewritten whole, an unopenable path, dropped rows, flush on stop | `WriterTests` |
| `summary.md`, `README.md` and `columns.md` generation | `SummaryTests`, `WriterTests.EndToEnd` |
| Every patch target exists in the installed game (1.1.2.4), has no exception filter, and takes only parameters Harmony can supply | `GameBindingTests.TargetsResolve` |
| The singleton wrappers work on the game's own `SingletonLifecycleService` and `TickableSingletonService` (built by their real constructors, their real load and update loops run through the wrappers), including exceptions and double wrapping | `GameBindingTests` |
| The entity bucket patch against the game's real `TickableEntityBucket`; the game splits a tick into 128 entity buckets | `GameBindingTests` |
| The wrappers go in on the first tick and frame, once per service (also when two services alternate every frame), so other mods' `Load` postfixes see the game's own singletons | `GameBindingTests.WrappingWaitsForTheFirstTick`, `WrappingIsOncePerService`, `WrappingHandlesServicesThatAlternate` |
| Files are not held open while the game runs (the strictest share mode can read them, a copy works), and a file someone else holds is retried without losing rows | `WriterTests.ReadableWhileRunning`, `RetriesWhenHeld` |
| The resolver the game side installs names a mod's DLL, `game` for the game's own and leaves the rest unknown; loading steps carry the heap growth | `ProfileTests` |
| Profile = off leaves the entity tick unpatched; an abandoned save does not block the next; the parallel tick figure is not counted twice; slow-frame rows are limited per minute; the summary is made on the writer thread | `GameBindingTests`, `CoreTests`, `WriterTests` |
| The config file, the watch list resolution against the real game types | `GameBindingTests` |
| The analysis tool reads what the mod writes (fixtures come from the real writer) and each finding fires on its situation and not on a healthy one | `tools/test_perflog.py`, `WriterTests.FixturesAreCurrent` |
| The in-game settings panel's values are clamped onto a `Config` the same way `PerformanceLog.cfg` is, only the six settings that can be, and the panel starts from what the .cfg file already had | `GameBindingTests.SettingsApplyTo`, `SettingsSeedFromTheCfgFile` |
| A fresh `Config` (nothing set) is `Profile = deep`, `SpikeContributors = 8` and `OverheadBudgetPercent = 1`; a bad or missing `Profile` line falls back to `deep`, not `standard` | `GameBindingTests.ConfigParsing`, `ConfigProblems` |

An independent read-only review of the game-facing code (looking for anything that could break the game or give wrong data, checked against the game's decompiled code and BeaverBuddies) found no
crash or hang; what it did find (wrappers put in at `Load` time could hide singleton types from BeaverBuddies' reordering, the previous game kept alive in the menu, BeaverBuddies' deferred save read as 0 ms,
state not reset between sessions, `Profile = off` still patching every entity tick, the summary built on the game thread, unbounded slow-frame rows) is fixed in 0.1.0.

A mutation check confirmed the game-binding tests fail when a private field name the mod relies on is changed.

## Verified in a real game: the first recording (0.1.0, 2026-09-20)

Timberborn 1.1.2.4, Unity 6000.5.5f1, Windows 11, Ryzen 7 9800X3D, RTX 4080 SUPER, nine mods (BeaverBuddies Stability Fork 1.1.10, Late Game Performance, MixedStorage, Persistent Work Areas,
Optimized Local Housing, The Tipsy Tail, Mod Settings, Harmony). A 31 minute session: 122150 frames, 12821 ticks (paused for the first 12 minutes, then speed 7), a colony of 359 beavers and
11.6 thousand entities, four saves (three autosaves and the save on exit), and a normal exit.

Worked:
- **Harmony applied all 22 patches**, none failed, and `Player.log` has no warning or exception from the mod. The mod and the game both exited cleanly (`session-end`, and `# end` in every file).
- **Bindito injection of `SessionService`**, and the session starting in `PostLoad` and ending on unload.
- **Unity's player loop**: eight phase markers installed and timed; calling `SetPlayerLoop` during scene load was harmless; nothing went wrong at quit.
- **Processor times, `FrameTimingManager`, and the `SetPass Calls Count` and `Triangles Count` profiler counters** produced values.
- **Timing every kind of singleton** (tick, update, late update, parallel start), sampled entity kinds, ticks counted right alongside BeaverBuddies (which replaces the tick loop): 1641139
  entity-bucket calls over 12821 ticks is 128 each plus part of one more.
- **Saves under BeaverBuddies**: the queued save reads about 0 ms and the real one is `save (writing the world)` at 221 to 231 ms (snapshot 207 to 215 ms, thumbnail 14 to 15 ms), as designed. The
  exit save was caught by the `SaveInstantlySkippingNameValidation` patch (669 ms), so Mono did not inline it.
- **Loading steps and the milestones**, and the summary rewritten every minute.
- **Coexisting with Late Game Performance** (which patches the same save methods): both mods' patches were installed on the same methods without a failure; the header lists them side by side.

Did not work, or was wrong (all fixed in 0.1.1, see the changelog): the wrapper swapping four times a frame, every game singleton labeled unknown, `workingMB` 0, files held open, no allocation on
loading steps, the `LoadAll never ran` counter, and `prDraw`/`prBatches` 0.

Not available in this game, and not going to be: `GC Allocated In Frame`, `GC Allocation In Frame Count` and `Batches Count` do not exist in Unity 6's player (they are not in `UnityPlayer.dll`), and
the mod's probe rejected `GC.GetAllocatedBytesForCurrentThread` (the method is in the game's `mscorlib.dll` and Mono runtime; 0.1.0 did not record why, and every recording since says it
`did not count a 64 KB allocation (read 0, then 0)`), so **allocation is the size of the managed heap and is coarse**. The per-singleton `KB/s` figures are therefore only good in aggregate.

## Verified in a real game: 0.1.3 with its defaults (2026-09-21)

Session `2026-09-21_23-13-11`: the same computer, game and Unity as the first recording, ten mods (BeaverBuddies MultiColony 1.4.0-beta2, Late Game Performance 0.4.23,
MixedStorage 0.5.8, Hungry Pathing 0.1.0, Optimized Local Housing 1.0.1, Persistent Work Areas 0.1.3, The Tipsy Tail 0.2.7.0, Mod Settings, Harmony and this mod) and every
setting at its default (the `# config:` header line). 43.9 minutes: 236917 frames and 25511 ticks (76% of the frames at speed 7), a colony of 359 beavers and 11.7 thousand
entities, nine saves (eight queued, one on exit) and a normal exit (`session-end`, "the game was left"). The window was in the background for 81% of the frames, so this
recording says little about the frame rate.

What it shows, and the line that shows it:
- **The 0.1.1 fixes** (item 1 of the list below until this recording; zipping a folder while the game runs is still there):
  - Each singleton service is wrapped once: `# capability-final|patchCalls|singleton wrappers put in place|3` for the whole recording (0.1.0: 488601 in 122150 frames). Every 0.1.1 and
    0.1.3 recording that reached its closing lines says 2 to 5.
  - `workingMB` is 5587 to 7090 in the `S` rows, and `# capability|workingSet|from Windows`.
  - The game's own singletons are labeled `game`: no singleton row in `profile.csv` has an empty `mod`.
  - Loading steps carry the heap growth: 310 of the 618 loading rows in `profile.csv` have a non-zero `allocKB`, and `summary.md` lists the steps that grew the heap most
    (`WorldEntitiesLoader` 411 MB of 2212 MB in all steps).
  - `prDraw` is non-zero: `# capability-final|profilerRecorder|Draw Calls Count|produced values, largest 37554`, about 16.6 thousand draw calls a frame on average.
  - `# capability-final|patchCalls|SingletonLifecycleService.LoadAll|1`, not `never ran`, and the allocation source line says why the exact counter is not used (above).
- **`Profile = deep` as the default** (item 8 of the list below until this recording): `# capability|patch|TickableEntity.Tick|installed` and
  `# capability|patch|MeteredTickableComponent.Tick|installed`, `# capability-final|patchCalls|MeteredTickableComponent.Tick (sampled calls)|47081184`, and 3475 `component`
  rows in `profile.csv` for 47 component classes (`BehaviorManager` the biggest). All eight 0.1.3 recordings so far say `# profile: deep`.
- **The settings page's `Load()` against the real Mod Settings services** (part of item 7): `profile.csv` has a `load` row for `PerformanceLog.PerformanceSettings`
  (once, 0.10 ms), and the `# config:` header line has the six numbers at their defaults after `PerformanceSettings.ApplyTo` ran on them; a setting `Load()` had not
  filled would have read 0 and been clamped to its minimum (`SlowFrameMs` 1, `SpikeContributors` 0). `Player.log` (it keeps only the latest launch, session
  `2026-09-22_00-39-06`) has no warning or exception from this mod or Mod Settings.
- **What measuring cost, by the mod's own estimate:** `overheadUs` + `probeUs` averaged 50 µs of an 11.1 ms frame, 0.45% (0.5% in the windows at speed 7 with the game in front,
  0.8% in the costliest window), under the report's 2% line. That estimate rests on a patch cost the calibration read as 0 (`# calibration|...|patchCallNs|0`, as in every
  recording so far), which charges each per-call patch either the 40 ns default or next to nothing, not a measurement; item 2 still stands.

## Still not verified: needs the running game

1. **Copying or zipping a session folder while the game runs** (0.1.1): a recording cannot show it. `WriterTests.ReadableWhileRunning` and `RetriesWhenHeld` check it outside
   the game. The other 0.1.1 fixes are verified in the game (above).
2. **Overhead** measured against a game running without the mod. The mod's own estimate (0.1.0: 0.3% of a frame paused, 0.8% at speed 7) left out the wrapper swapping and used a default cost for a
   patch; 0.1.1 measures the patch cost, but nobody has compared the frame rate with the mod off. See the checklist.
3. **Co-op**: with BeaverBuddies actually connected to another player. It has only been seen running with BeaverBuddies loaded in a single-player game.
4. **`Ticker.FinishFullTick`** (one of the four save-stage patches) is counted inside `save stages`, so it has not been seen separately; the stages of three saves were recorded.
5. **The mod attribution** (which DLL belongs to which mod) worked for the mods in the first recording (`beaverbuddies`, `Kyler.OptimizedLocalHousing`, `eMka.ModSettings`, `kyler.persistentworkareas`);
   a mod whose DLL is not in its own folder may still read as unknown.
6. **The report's advice on a bad recording.** The first recording was mostly paused and in the background, so the findings for real problems (a slow simulation, a saving hitch, a memory leak,
   a mod that costs too much) have been exercised on made-up sessions and one real, healthy one.
7. **The in-game settings page (0.1.2)**: that Mod Settings actually shows it, that `RangeIntModSetting` renders as something usable and the readonly note as
   readable text, and that a value changed there actually reaches `Session.Start` — change `SlowFrameMs`, load a save, and check `frames.csv`'s `# thresholdMs`
   header line against what was set. Every 0.1.3 recording so far has the default `# thresholdMs: 50`. (`PerformanceSettings.Load()` against the real
   `ISettings`/`ModRepository`/`ModSettingsOwnerRegistry` is verified in the game, above.)

## Five-minute check in a game

1. Install (README, now including the **Mod Settings** mod), enable, open Mod Settings from the main menu and confirm **Performance Log** is listed with
   its six sliders and the readonly note. Change `SlowFrameMs` to something distinctive, e.g. 77.
2. Start a game from a save, play **3 minutes with the game in front**, including a while at speed 3, then leave through the menu.
3. Open `Documents\Timberborn\PerformanceLog\<newest folder>`. There should be `summary.md`, `frames.csv`, `profile.csv`, `spikes.csv`, `events.csv`, `README.md` and `columns.md`.
4. In `Player.log`, search for `[PerformanceLog]`. Expect `Patches: N installed, 0 could not be made.` and `Recording to ...` and `Finished: ...`. Any warning names the part that is off.
5. Open `summary.md`. The **"Read first"** list at the top (if any) says what did not work. Then check:
   - **Where an average frame goes** has non-zero `tickMs`/`entMs`/`updMs` and the parts add up (`otherMs` is the remainder).
   - **Simulation ticks** says a plausible number of ticks per second (about 3.3 at speed 1 if the tick is 0.3 s).
   - **Where the time goes** lists singletons with their mods (the game's own show as `game`; a mod you have enabled should show under its id).
   - **Mods enabled** matches what you enabled.
6. In the `frames.csv` header: `# thresholdMs: 77` (the value set in step 1, proving the settings page reached the session). Every `# capability|patch|...`
   line says `installed`, **including `MeteredTickableComponent.Tick`** (deep is the default now, so this should install without being asked); the
   `# capability-final|patchCalls|...` lines at the end say non-zero counts, and none says `never ran`, including `MeteredTickableComponent.Tick (sampled calls)`.
   `singleton wrappers put in place` should be a handful (one or two per array). `# capability|workingSet|...` should say `from Windows`.
   `# capability-final|profilerRecorder|...` and `frameTiming` may legitimately say `never produced a value` in a release build.
7. `profile.csv` has rows of kind `component`, not just `entity`.
8. Compare the frame rate the game shows with `summary.md`'s mean; they should agree.
9. Run `python tools/perflog.py report <folder>` and confirm it reads the folder without complaint.
10. To see the cost, play the same save for the same time at the same speed with the mod turned off in the mod manager and compare the frame rate with something outside the mod (Steam's
    or the game's own FPS counter). At the new, deeper defaults the mod costs more than the 0.3-0.8% of a frame the first recording measured at the old ones; the report still warns if
    `overheadUs` + `probeUs` are more than 2% of a frame, and `OverheadBudgetPercent` is the setting to lower if it runs that high. (With `Enabled = false` the mod writes no session at
    all, so there is nothing to `compare`.)

If something is wrong, send Claude the session folder and the `[PerformanceLog]` lines from `Player.log`.
