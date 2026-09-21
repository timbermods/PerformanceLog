# What has been checked, and how to check the rest in a game

Version 0.1.0 has been tested without the game running. This is the honest list.

## Verified by the automated checks

`dotnet run --project tests -c Release` (82 checks) and `python -m unittest discover -s tools -p "test_perflog.py"` (40 checks).

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
| The wrappers go in on the first tick and frame, once per service, so other mods' `Load` postfixes see the game's own singletons | `GameBindingTests.WrappingWaitsForTheFirstTick`, `WrappingIsOncePerService` |
| Profile = off leaves the entity tick unpatched; an abandoned save does not block the next; the parallel tick figure is not counted twice; slow-frame rows are limited per minute; the summary is made on the writer thread | `GameBindingTests`, `CoreTests`, `WriterTests` |
| The config file, the watch list resolution against the real game types | `GameBindingTests` |
| The analysis tool reads what the mod writes (fixtures come from the real writer) and each finding fires on its situation and not on a healthy one | `tools/test_perflog.py`, `WriterTests.FixturesAreCurrent` |

An independent read-only review of the game-facing code (looking for anything that could break the game or give wrong data, checked against the game's decompiled code and BeaverBuddies) found no
crash or hang; what it did find (wrappers put in at `Load` time could hide singleton types from BeaverBuddies' reordering, the previous game kept alive in the menu, BeaverBuddies' deferred save read as 0 ms,
state not reset between sessions, `Profile = off` still patching every entity tick, the summary built on the game thread, unbounded slow-frame rows) is fixed in 0.1.0.

A mutation check confirmed the game-binding tests fail when a private field name the mod relies on is changed.

## Not verified: needs the running game

1. **Harmony actually applying the patches** (the Workshop build only runs under Mono). The targets and signatures are validated; that the game accepts them is not.
2. **Unity's player loop**: that the phase markers install, that the first phase's marker really is called once per frame before anything else, and that calling `SetPlayerLoop` during
   scene load is harmless.
3. **Bindito injection** of `SessionService` (six constructor parameters, all checked to be bound in the Game context, none exercised).
4. **`ProfilerRecorder` counters and `FrameTimingManager`** in a release build: they may produce nothing (the log says so, and the columns stay 0).
5. **Which allocation source the runtime offers** (`GC.GetAllocatedBytesForCurrentThread` may not exist under Unity's Mono; the log falls back to the heap size and says so).
6. **Overhead**: the estimate is computed by the mod itself, not measured against a game running without it. Compare the frame rate with the mod turned off (see the checklist).
7. **Co-op**: alongside BeaverBuddies (which replaces the tick loop). Counting ticks by entity buckets is meant to survive that; it has not been seen to.
8. **Two small save targets may be inlined by Mono** (`GameSaver.SaveInstantlySkippingNameValidation`, `Ticker.FinishFullTick`): if so their patches never fire, and the log says `never ran`. `SaveQueued`
   and `SaveWriter.WriteToSaveStream` cover the same saves.
9. **`PlayerLoop.SetPlayerLoop` called while a scene loads**, and (deliberately not) at quit: the sibling mods never call it, so nothing proves it safe in this game. If the game misbehaves at load or exit with
   this mod on, that is the first place to look; `Enabled = false` removes it.
10. **The exact order of Harmony patches against BeaverBuddies** (this mod's scope patches use `Priority.First` / `Priority.Last`).
11. **The mod attribution** (which DLL belongs to which mod) depends on how the game lays out mod folders.

## Five-minute check in a game

1. Install (README), enable, start a game from a save, play **3 minutes**, including a while at speed 3, then leave through the menu.
2. Open `Documents\Timberborn\PerformanceLog\<newest folder>`. There should be `summary.md`, `frames.csv`, `profile.csv`, `spikes.csv`, `events.csv`, `README.md` and `columns.md`.
3. In `Player.log`, search for `[PerformanceLog]`. Expect `Patches: N installed, 0 could not be made.` and `Recording to ...` and `Finished: ...`. Any warning names the part that is off.
4. Open `summary.md`. The **"Read first"** list at the top (if any) says what did not work. Then check:
   - **Where an average frame goes** has non-zero `tickMs`/`entMs`/`updMs` and the parts add up (`otherMs` is the remainder).
   - **Simulation ticks** says a plausible number of ticks per second (about 3.3 at speed 1 if the tick is 0.3 s).
   - **Where the time goes** lists singletons with their mods (the game's own show as `game`; a mod you have enabled should show under its id).
   - The **mod list** matches what you enabled.
5. In the `frames.csv` header: every `# capability|patch|...` line says `installed`; the `# capability-final|patchCalls|...` lines at the end say non-zero counts, and none says `never ran`.
   `# capability-final|profilerRecorder|...` and `frameTiming` may legitimately say `never produced a value` in a release build.
6. Compare the frame rate the game shows with `summary.md`'s mean; they should agree.
7. Run `python tools/perflog.py report <folder>` and confirm it reads the folder without complaint.
8. To see the cost, play the same save for the same time at the same speed with the mod turned off in the mod manager and compare the frame rate with something outside the mod (Steam's
   or the game's own FPS counter). The mod should cost under 1-2% of a frame. (With `Enabled = false` the mod writes no session at all, so there is nothing to `compare`.)
   `overheadUs` and `probeUs` in the log are the mod's own estimate of what it costs; the report warns if they are more than 2% of a frame.

If something is wrong, send Claude the session folder and the `[PerformanceLog]` lines from `Player.log`.
