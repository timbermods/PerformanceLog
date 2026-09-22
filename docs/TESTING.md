# What has been checked, and how to check the rest in a game

Version 0.1.0 was run in a game once (the first recording, below); 0.1.1 fixed what that showed, 0.1.2 added an in-game settings page, and 0.1.3 changed
the defaults to `Profile = deep` and a wider sampling budget, for the most detail with nothing configured. Nothing from 0.1.1 onward has been tested with
the game running. This is the honest list.

## Verified by the automated checks

`dotnet run --project tests -c Release` (104 checks) and `python -m unittest discover -s tools -p "test_perflog.py"` (47 checks).

| What | How |
|---|---|
| Frame accounting: slots are exclusive and add up to the frame; unbalanced scopes; other threads ignored; allocation attribution; flags; ticks and buckets; Unity phases; summaries and histograms | Real `Probe` against a scripted clock (`CoreTests`) |
| The per-frame path allocates nothing | `GC.GetAllocatedBytesForCurrentThread` around 2000 frames; also for a wrapper with the log off |
| Failure containment: a failing clock switches the probe off, a full ring drops rows and counts them | `CoreTests` |
| The cost charged for each patch call (`patchCallNs`) is at least what the entity patch's own bodies take on a call that is not sampled, timed with the log on and sampling held off (the part Harmony adds needs the game); the `# calibration|` line names both, or says `unmeasured` | `CoreTests.UnsampledBodyTiming`, `CoreTests.CalibrationLine`, `GameBindingTests.PatchCostCoversTheBodies` |
| The profile: exact singleton timing, scaled sampling, random gaps that do not alias with a repeating pattern, budget adaptation, spike attribution, mod resolution | `ProfileTests` |
| Watched methods: each is sampled at its own rate, widening with its own load inside the budget, kept through windows it is not called in (so bursts stay inside it too) and coming back down when it runs less, with a row (and at least one timing) for every window it ran in; calls nobody timed get a `sampled` 0 row and stay out of the totals | `WatchSamplingTests`, `test_perflog` |
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
| The auto watch (`AutoWatch = true`): only other mods' prefixes, postfixes and finalizers on the methods behind the profile's rows or on the hot list are taken, in name order whatever order Harmony lists them in, one watch per patch method; never more than the slots the Watch entries left, a Watch entry's method is not watched twice, and a refused or failed patch takes no slot; a patch is examined (label, exception filter, generic) the way a Watch entry is, against real methods; a watched patch method is counted, timed and named in `profile.csv`, and its watch allocates nothing; the report names each by its class and the hot method it is on | `AutoWatchTests`, `test_perflog` |
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

Did not work, or was wrong (all fixed in 0.1.1, see the changelog): the wrapper swapping four times a frame, every game singleton labelled unknown, `workingMB` 0, files held open, no allocation on
loading steps, the `LoadAll never ran` counter, and `prDraw`/`prBatches` 0.

Not available in this game, and not going to be: `GC Allocated In Frame`, `GC Allocation In Frame Count` and `Batches Count` do not exist in Unity 6's player (they are not in `UnityPlayer.dll`), and
the mod's probe rejected `GC.GetAllocatedBytesForCurrentThread` (the method is in the game's `mscorlib.dll` and Mono runtime; 0.1.0 did not record why, 0.1.1 does), so **allocation is the size of the managed
heap and is coarse**. The per-singleton `KB/s` figures are therefore only good in aggregate.

## Still not verified: needs the running game

1. **The 0.1.1 fixes themselves**: that each service is wrapped once (`# capability-final|patchCalls|singleton wrappers put in place` should be a handful, not hundreds of thousands), that the four files
   can be zipped while the game runs, that `workingMB` is non-zero, that game singletons show `game`, that loading steps show a heap growth, and that `prDraw` is non-zero.
2. **Overhead** measured against a game running without the mod. The mod's own estimate (0.1.0: 0.3% of a frame paused, 0.8% at speed 7) left out the wrapper swapping and used a default cost for a
   patch. 0.1.1 to 0.1.3 measured the patch cost on an empty patch, which shows 0 in every recording (`patchCallNs|0`): where it read exactly 0 the per-call patches were still charged the
   40 ns default, where it read a fraction of a nanosecond they were charged almost nothing (`perflog.py` says which when a recording's rows show it). The cost is now the real bodies
   (`patchBodyNs`) plus what Harmony adds (`patchCallNs`), but that has not run in a game yet, and nobody has compared the frame rate with the mod off. See the checklist.
3. **Co-op**: with BeaverBuddies actually connected to another player. It has only been seen running with BeaverBuddies loaded in a single-player game.
4. **`Ticker.FinishFullTick`** (one of the four save-stage patches) is counted inside `save stages`, so it has not been seen separately; the stages of three saves were recorded.
5. **The mod attribution** (which DLL belongs to which mod) worked for the mods in the first recording (`beaverbuddies`, `Kyler.OptimizedLocalHousing`, `eMka.ModSettings`, `kyler.persistentworkareas`);
   a mod whose DLL is not in its own folder may still read as unknown.
6. **The report's advice on a bad recording.** The first recording was mostly paused and in the background, so the findings for real problems (a slow simulation, a saving hitch, a memory leak,
   a mod that costs too much) have been exercised on made-up sessions and one real, healthy one.
7. **The in-game settings page (0.1.2, entirely unplayed)**: that Mod Settings actually shows it, that `RangeIntModSetting` renders as something usable and
   the readonly note as readable text, and that `PerformanceSettings.Load()` does not throw against the real `ISettings`/`ModRepository`/`ModSettingsOwnerRegistry`
   (only checked against `null`s standing in for them, which never calls `Load()`; see `Settings.cs`'s own notes). Then that a value changed there actually
   reaches `Session.Start` — change `SlowFrameMs`, load a save, and check `frames.csv`'s `# thresholdMs` header line against what was set.
8. **`Profile = deep` as the default (0.1.3, entirely unplayed)**: the first recording used `standard`, so the patch on `MeteredTickableComponent.Tick`
   (every entity component, sampled) has never actually run in a real game — only had its target validated against the real assemblies. Check that it
   installs (`# capability|patch|TickableEntity.Tick`/`MeteredTickableComponent.Tick|installed` in the header) and that `profile.csv` gets `component`
   rows. Check the real cost too: the wider sampling budget (0.5% → 1%) plus component sampling should cost more than the first recording's 0.3-0.8% of
   a frame, and `overheadUs`/`probeUs` should still land well under the report's 2% warning line — if not, that is exactly what `OverheadBudgetPercent`
   is for, and it is worth lowering the default again.

9. **`AutoWatch = true` (not yet played)**: Harmony cannot apply a patch in the test process, so that the auto watch's patches on other mods' patch methods
   are made, and that Harmony hands them the method the watch looks up, is only reasoned, not seen. Set `AutoWatch = true` in `PerformanceLog.cfg`, restart,
   load a save with BeaverBuddies (or any mod that patches `TickableEntity.Tick`) and play 3 minutes. Check that `Player.log` says `Auto watch: watching N of M
   patch methods...` with N > 0 and no warning, that the `frames.csv` header has `# capability|autoWatch|watching ...` and a `# watch|...|watching|auto|...`
   line per method, that `profile.csv` has `method` rows for them (BeaverBuddies' `TickableEntityTickPatcher.Prefix` should be one of the busiest), that
   `# capability-final|autoWatch|` says most of them were called, and what `overheadUs` costs compared with the same save with `AutoWatch = false`. A patch
   method listed there as never seen called is either not called or inlined by the runtime into the method it patches, which no watch can see.

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
   - The **mod list** matches what you enabled.
6. In the `frames.csv` header: `# thresholdMs: 77` (the value set in step 1, proving the settings page reached the session). Every `# capability|patch|...`
   line says `installed`, **including `MeteredTickableComponent.Tick`** (deep is the default now, so this should install without being asked); the
   `# capability-final|patchCalls|...` lines at the end say non-zero counts, and none says `never ran`, including `MeteredTickableComponent.Tick (sampled calls)`.
   `singleton wrappers put in place` should be a handful (one or two per array). `# capability|workingSet|...` should say `from Windows`.
   `# capability-final|profilerRecorder|...` and `frameTiming` may legitimately say `never produced a value` in a release build.
   `# calibration|...` has `patchBodyNs` and `patchCallNs` of a few nanoseconds each (not `0` and not `unmeasured`), `patchCallNs` at least `patchBodyNs`.
7. `profile.csv` has rows of kind `component`, not just `entity`.
8. Compare the frame rate the game shows with `summary.md`'s mean; they should agree.
9. Run `python tools/perflog.py report <folder>` and confirm it reads the folder without complaint.
10. To see the cost, play the same save for the same time at the same speed with the mod turned off in the mod manager and compare the frame rate with something outside the mod (Steam's
    or the game's own FPS counter). At the new, deeper defaults the mod costs more than the 0.3-0.8% of a frame the first recording measured at the old ones; the report still warns if
    `overheadUs` + `probeUs` are more than 2% of a frame, and `OverheadBudgetPercent` is the setting to lower if it runs that high. (With `Enabled = false` the mod writes no session at
    all, so there is nothing to `compare`.)

If something is wrong, send Claude the session folder and the `[PerformanceLog]` lines from `Player.log`.
