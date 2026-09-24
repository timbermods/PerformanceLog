# Working on Performance Log

Notes for a Claude session (or a person) changing this repository. For what the mod is and how to use it, read README.md. To *diagnose a recording*, read
`docs/SESSION-README.md` (the guide the mod copies into every session folder) and run `python tools/perflog.py report <folder>`.

## The rules this code is built around

1. **It only observes.** It must never change what the game simulates, never throw into game code, never block the game thread on disk, and never
   grow without bound. Every patch body and wrapper starts by reading `Probe.Enabled` and is wrapped so that a failure switches a part off instead of
   reaching the game. If you add a patch, add it to `Instrumentation.CreateSpecs` so it is validated against the real game assemblies by a test.
2. **The per-frame path allocates nothing.** The mod exists to find garbage; it must not make any. `CoreTests.FramePathAllocatesNothing` and
   `GameBindingTests.WrapperWhenOff` check this. Text is only built on the writer thread, on slow frames, or once a window.
3. **A zero must never be mistaken for a measurement.** Every optional source reports at the start (`# capability|`) and the end (`# capability-final|`)
   whether it worked. If you add a source, add both lines.
4. **Do not patch a method with an exception filter (`catch ... when`).** Harmony cannot regenerate those under Mono; the failed attempt leaves a broken
   dynamic type that crashes the game later. `GameSaver.Save` is one (that is why saves are timed through `SaveQueued` and friends). `Instrumentation.InstallOne`
   and `Watch.Resolve` refuse them; `GameBindingTests.TargetsResolve` checks every target.
5. **No NuGet packages.** The build must work offline (`NuGet.Config` clears all sources). Reach the game's internal members by reflection or Harmony's
   `AccessTools`, not by publicizing the assemblies.

## Writing README and website text

Kyler, 2026-09-24: "simplicity and elegance is effective and desirable." Every change to the README, the website
text and the player docs follows these rules.

- **Write for a Timberborn player** who wants to download, install and use the mod. Developer detail goes in
  `docs/TESTING.md` (building, the checks, what is verified), this file, or `CHANGELOG.md`; link to it rather than
  repeating it.
- **Short.** One idea per sentence, most under about 20 words. A paragraph or FAQ answer is one to three sentences,
  a troubleshooting answer a few numbered steps.
- **Lead with the action.** Menu paths as arrow chains; on-screen labels in bold, exactly as in game.
- **Say each thing once**, where a player would look for it; link to it elsewhere.
- **Plain words.** No internals (class names, ids, formats) unless the player needs them to act.
- **Cut** filler, repeated caveats, edge cases a player won't meet, and history ("since …", "no longer", older
  builds). Describe the mod as it is now.
- **Check every fact against the code** before writing it; changelogs lag.
- **Keep, briefly:** credits, the unofficial line, the status, and safety facts.
- **Reread as a new player before publishing.** Every step works as written, and nothing is said twice.

## Layout

```
source/Core/     Pure C# (netstandard2.1), no Unity or Timberborn types: this is what is unit tested with a scripted clock.
  Columns.cs     THE definition of every column of frames.csv (name, kind, how S rows combine it, unit, meaning). Docs are generated from it.
  Probe.cs       Frame accounting: exclusive-time slot stack, frame rows, summary windows, session totals. Static, game thread only.
  Profile.cs     Keyed profile (singletons, entity kinds, components, watched methods): exact and sampled timing, budgeted sampling, spike attribution.
  LogWriter.cs   One thread that writes every file (tables from rings, an events queue, files rewritten whole). Never blocks the game thread.
  Summary.cs     summary.md (pure formatting). Header.cs: header lines, and README.md / columns.md text. PatchReport.cs: which mod patches which hot method.
  AutoWatch.cs   Which of other mods' patch methods the auto watch (AutoWatch = true) times, in the free Watch slots.
  Alloc/Cpu/Milestones/Ring.cs   Small sources.
source/Game/     The Timberborn side (needs the game's assemblies to compile).
  Plugin.cs      IModStarter, and the Bindito configurators. Reads config, installs patches.
  SessionService Bindito singleton: PostLoad starts a Session, Unload stops it.
  Session.cs     One log from load to unload: folders, header, writer, probe start/stop, summary refresh, events.
  Instrumentation.cs   The Harmony patch list (CreateSpecs) and every patch body. Wrappers.cs: timing wrappers put into the game's singleton arrays, LoadRecorder, SaveTracker.
  Watch.cs       Config-driven method timing, and the auto watch's patches on other mods' patch methods. PlayerLoopTiming.cs: Unity phase markers and the frame boundary. UnityExtras.cs: profiler counters, frame timing.
  Environment.cs Computer/game facts, mod list, assembly-to-mod map, Harmony patch reader. Config.cs: PerformanceLog.cfg.
  Settings.cs    The in-game settings page (the Mod Settings mod). The only file that touches ModSettings.Core/.Common types.
tests/           .NET 8 console checks (no test framework). Core checks run for real; game checks run against the installed game's real assemblies.
tests/fixtures/  Sample sessions written by the real mod code (tests/SampleSession.cs). tools/ reads them. Regenerate after changing any column (see below).
tools/perflog.py Analysis: report / compare / list. Standard library only. tools/test_perflog.py: its tests.
docs/            SESSION-README.md is embedded in the mod and copied into every session folder. TESTING.md: what is and is not verified.
packaging/       manifest.json, PerformanceLog.cfg. build.ps1 builds, tests and zips.
```

## Commands

```
.\build.ps1 -SkipTests                                  build + package
dotnet run --project tests -c Release                   all C# checks (113 at 0.1.4; docs/TESTING.md keeps the current count)
python -m unittest discover -s tools -p "test_perflog.py"
dotnet run --project tests -c Release -- --print-columns
dotnet run --project tests -c Release -- --write-sample tests/fixtures/sample-with-mod
dotnet run --project tests -c Release -- --write-sample tests/fixtures/sample-without-mod --without-mod
```

The game is expected at `C:\Program Files (x86)\Steam\steamapps\common\Timberborn` and Harmony (Workshop item 3284904751) under
`C:\Program Files (x86)\Steam\steamapps\workshop\content\1062090`; pass `-p:GameDir=...` / `-p:HarmonyPath=...` (or `-GameDir` to build.ps1) otherwise.
To read the game's own code (the way every patch target here was checked): `ilspycmd "<GameDir>\Timberborn_Data\Managed\Timberborn.TickSystem.dll"`.

## Recipes

- **Add or rename a column:** edit `Columns.cs` only (the constants, the arrays and the descriptions). Fill it in `Probe.Frame`. Then regenerate the two fixtures (commands above) and
  run both test suites: `WriterTests.FixturesAreCurrent` fails until you do, and `perflog.REQUIRED` in `tools/perflog.py` lists what the analysis needs. Column order and names are the
  file format; bump `HeaderBuilder.FramesFormat` if you change the meaning of an existing column.
- **Add a patch:** add a `PatchSpec` in `Instrumentation.CreateSpecs` with its body next to the others (`internal static`, so tests can call it), a `Hit*` index and a name in `hitNames`.
  Prefer `Scope = true` pairs (`Probe.Begin` in the prefix, `Probe.End(__state)` in the postfix). Run the tests: they resolve the target in the real game and check the parameters.
- **Time another singleton-like array:** see `SingletonArrays.Swap<T>`; a wrapper implements the interface, calls the inner object, and uses `Profile.BeginExact/EndExact`.
- **Add a finding to the analysis:** `findings_for` in `tools/perflog.py`. A finding needs evidence (numbers from the session) and a "next" step, must not fire on a healthy
  session (there are tests for that: `FindingTests`), and must say "consistent with", not "caused by", unless the data proves it.
- **Release:** bump `<Version>` in `source/PerformanceLog.csproj` AND `Version` in `packaging/manifest.json` (build.ps1 refuses a mismatch), update `CHANGELOG.md` and the README's
  status (by *Writing README and website text*, above), run `.\build.ps1`,
  tag `vX.Y.Z`, and create a GitHub release with `dist\PerformanceLog-X.Y.Z.zip`. Publish it as the latest release, not a pre-release (the README's install step says to
  pick the one marked Latest); the 0.x version says it is a preview, and `CHANGELOG.md` and `docs/TESTING.md` say what has not been played. **Do not add this mod to the
  timbermods catalog site or create a website for it until asked.**

## Things that are not obvious

- **The Workshop build of Harmony only runs under Mono**, so in the .NET 8 test process patches are *validated* (target resolves, no exception filter, parameters valid) and their
  bodies are called by hand around the game's own classes, not applied. `GameBindingTests` does this against real `SingletonLifecycleService`, `TickableSingletonService` and
  `TickableEntityBucket` instances. `HarmonyInfo` prints whether patching works in the current environment.
- **Singletons are timed with wrappers, not patches.** The game builds arrays of `ITickableSingleton` (inside a private struct), `IUpdatableSingleton` and so on when a scene loads; the mod
  swaps each element for a wrapper (`TimedUpdatable`...) **on the first tick and the first frame** (`Instrumentation.EnsureTickSingletonsWrapped` and friends, called from the prefixes of `TickAll`,
  `TickSingletons`, `UpdateSingletons` and `LateUpdateSingletons`), not in a postfix on `Load`: every other mod's `Load` postfix must run first, because BeaverBuddies reorders the array by
  `is IEarlyTickableSingleton` and a wrapper would hide the type and change tick order. The reference to the wrapped service is weak. There is no Harmony patch in the hot loop, so
  nothing depends on the runtime not inlining a method, and a mod's own patch on the singleton is inside the measurement. Entity kinds are the one place a per-call patch is unavoidable
  (`TickableEntity.Tick`), so that is sampled.
- **An entity is keyed by its kind, not by the name the tick system recorded** (`Profile.EntityKindOf`: the name up to its first space or `(`). The game renames a character loaded from a
  save to `<template> <its own name>` before the tick system records it, and one made during play is `<template>(Clone)`, so up to 0.1.3 every loaded beaver was its own row and beavers
  ranked far too low. `tools/perflog.py` (`entity_kind`) applies the same rule to older recordings, so compare lines old and new up: change both together. Its known-issue
  note is printed only when a recording's entity rows really are split. No template name in the game or the installed mods contains a space or `(`; a modded template whose name did would be cut short.
- **Saves are timed at three hooks** (`SaveQueued`, `SaveInstantlySkippingNameValidation`, `SaveWriter.WriteToSaveStream`); whichever is entered first owns the save (`SaveTracker`), and a save open for
  a minute is treated as abandoned (the game's save throws on an IO error and skips its postfix). BeaverBuddies defers the real save, so the `SaveWriter` hook is what times it.
- **Nothing a session holds may outlive it**: `Session.Stop` clears `services` (its delegates reach the whole colony), the colony sampler, the mod resolver and the milestones, and `Session.Start`
  resets the patch counters and the save tracker. Static state added later must be reset there.
- **The summary text is made on the writer thread** (`LogWriter.SetFile(path, Func<string>)`) from a snapshot taken on the game thread; anything the producer reads must be immutable or cloned
  (`SessionStats.Clone`).
- **A tick is counted as 128 entity buckets** (`Probe.NoteEntityBucket`), not where a tick starts, so the count stays right when BeaverBuddies replaces `TickableBucketService.TickBuckets`.
- **The frame boundary is the start of Unity's first player-loop phase** (`PlayerLoopTiming`), so `frameMs` includes drawing and the vsync wait. Do not move it into a game script.
- **Sampling uses random gaps** (mean N), not every Nth call: the game calls singletons in the same order every frame, and a fixed stride can land on the same few of them forever
  (`ProfileTests.NoAliasing`).
- **`Columns` is initialised in textual order** (C# static field initialisers). Declare arrays before the groups that use them.
- **Only some settings can live in the in-game Mod Settings menu** (`Settings.cs`, `PerformanceSettings`). `Plugin.StartMod` reads `PerformanceLog.cfg`
  and decides `Enabled`/`Profile`/`Watch`/`AutoWatch` (which Harmony patches get made, including whether the entity tick is patched at all) before Bindito, and so
  Mod Settings, exists; making those live would mean re-patching the game while it runs or always paying for the entity-tick patch even when `Profile = off`
  asks not to. Only the six numbers `Session.Start` reads fresh each session (`SlowFrameMs`, `SummarySeconds`, `ProfileSeconds`, `OverheadBudgetPercent`,
  `SpikeContributors`, `MaxSlowRowsPerMinute`) are in the menu; `SessionService.PostLoad` calls `PerformanceSettings.ApplyTo` to fold them onto `Plugin.Config`
  before each `Session.Start`. A `ModSetting<T>`'s `.Value` is `default(T)` until Mod Settings calls `Load()` (which needs a real `ISettings`/`ModRepository`);
  what this mod controls at construction, and what `Load()` seeds `.Value` from the first time, is `.DefaultValue` — tests read that, not `.Value`.
- **The auto watch is the one patching done after `StartMod`** (`Watch.AutoInstall`, from `Session.Start` when `AutoWatch = true`): once per run of the
  game, when the first log starts, because only then are other mods' patches (made at their start and while the game loads) in Harmony's registry, and
  nothing ticks yet. It only adds this mod's own prefix and postfix (id `kyler.performancelog.watch`) around another mod's patch method; it never unpatches,
  reorders or changes anyone else's patch. Which methods it takes is decided in `AutoWatch.Plan` (pure, tested in `AutoWatchTests`): name order, not time,
  because ranking by what the recording measured would mean patching while the game runs.
- **Under BeaverBuddies, making a Harmony patch draws from the game's random numbers.** Every `Harmony.Patch` builds a MonoMod `DynamicMethodDefinition`,
  which calls `Guid.NewGuid()`, and BeaverBuddies' `GuidPatcher` turns each `Guid.NewGuid()` into 16 draws from `UnityEngine.Random`, the state the
  simulation's `RandomNumberGenerator` uses and that BeaverBuddies seeds when a game loads. Patching at `StartMod` is harmless (the seed comes later);
  any patch made after a game has started loading must run inside `Watch.KeepUnityRandom` (which puts `UnityEngine.Random.state` back as it was), or
  one player's random numbers move and co-op desyncs. Never patch mid-game, not even with that guard (it is only proven at load, before the first tick).
- Colony and memory columns are read only when a row is written and carried over otherwise (`lastHeavy` in `Probe`).
- The tests run each check on a thread-pool thread, and `Probe` is static and remembers the game thread's id: every check that uses it goes through `Rig` in `CoreTests.cs`.

## What is and is not verified

0.1.0 was first run in the real game on 2026-09-20 (31 minutes, nine mods including BeaverBuddies, clean exit). Its recording is why 0.1.1 exists; 0.1.1 and 0.1.3 have been
recorded in the game since. See `CHANGELOG.md` and `docs/TESTING.md` (what those runs proved, what the first one broke, and what is still unproven). When you are given a
recording, start with the `# capability` and `# capability-final` lines in the `frames.csv` header and the "Read first" section of `summary.md`: they say which parts worked.
`python tools/perflog.py report <folder>` prints a `KNOWN ISSUE` line for every known defect of the version that made it (`KNOWN_ISSUES` in `tools/perflog.py`); add to that list
whenever a version is found to record something wrong.

Lessons from the first run that shape how to change this code:
- **Count what a patch really does.** `# capability-final|patchCalls|...` showed 488601 wrapper swaps in 122150 frames: the hit counters are how a defect that only exists in the game shows up.
  Keep a counter for anything done once per game or per frame.
- **The game keeps more than one singleton service alive** (the application's and the game's, both updated every frame), so anything remembered "per service" must hold several
  (`Instrumentation.FirstTime`, a `ConditionalWeakTable`), never one.
- Files are opened, appended to and closed on each write (`LogWriter`), never held open: a file held open for writing cannot be zipped or copied, and shows size 0 in a listing.

## Provenance

The measuring core descends from the frame rate log in the BeaverBuddies Stability Fork (`v1.0.10-perflog-preview2`, branch `perflog-preview`; see its `PERFORMANCE-LOG.md` and
`RuntimeChecks/`). That log was embedded in BeaverBuddies' own code and timed its sync layer; this one hooks the game, so it works alone and with any mods.
What was about the co-op layer is left out: the network and event-hash timings, the waits for the other player, and the garbage collection experiment.
