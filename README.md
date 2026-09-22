# Performance Log

A Timberborn 1.1 mod (built against **1.1.2.4**) that measures **where the game's time and memory go**, and writes it down in a form that a
person, or an AI assistant such as Claude, can read to find out why a game is slow. It is a diagnostic tool for finding performance problems in
the game and in other mods. It does nothing else.

Version **0.1.2** is a **preview**. Its automated checks run against the game's real assemblies, and version 0.1.0 has been played once (a 31 minute session with
nine mods, ending in a normal exit): every patch applied and the recording was complete. That first recording also showed several defects, fixed in 0.1.1; 0.1.2
adds an in-game settings page and has not itself been played yet. See [CHANGELOG.md](CHANGELOG.md) for what changed in each version. If a part fails to start it
says so in the log and the summary, that part stays off, and the game carries on. Read [docs/TESTING.md](docs/TESTING.md) for what is and is not verified, and
how to check it in a game in five minutes.

It only observes. It never records or replays an action, never uses the game's random numbers and never touches anything the simulation reads, so
it should not cause a desync in co-op. (That has not been played in co-op yet.)

## Install

1. Close Timberborn. Extract the release ZIP into `Documents\Timberborn\Mods`. It contains one `PerformanceLog` folder.
2. It requires the **Harmony** mod (2.4.1 or newer) and the **Mod Settings** mod (1.1.0.0 or newer) from the Steam Workshop.
3. Launch Timberborn, enable **Performance Log** in the mod manager, and restart.
4. Play. Leave normally (menu → exit) so the files are finished; if the game crashes the files are still readable and the summary is at most a
   minute stale. (From 0.1.1 you can also copy or zip the folder while the game runs; 0.1.0 held four of its files open, so a zip made then left them out.) Look for `[PerformanceLog]` lines in `Player.log`
   (`%USERPROFILE%\AppData\LocalLow\Mechanistry\Timberborn\Player.log`).

Recordings go to `Documents\Timberborn\PerformanceLog\<date and time>\`, one folder per game session (a session is from a save finishing loading to leaving it).

## What to do with a recording

Give the **whole session folder** to Claude (or zip it and attach it), say what the problem felt like, and ask it to diagnose. The folder explains itself:
`README.md` in it is a guide for reading the numbers (with a step-by-step way to diagnose), `summary.md` is the whole session on one page, and the CSV
files hold the detail. A prompt that works:

> This is a Performance Log session folder from Timberborn. Read `summary.md` and `README.md` first, then work out why the game was slow. It felt like: *(low frame rate all the time / hitches every minute / slow at speed 3 / slow to load)*. Say what the evidence is, how sure you are, and what to change or record next.

To compare two recordings (for example the same save with and without a mod), use the analysis tool. It needs Python 3.8+ and nothing else:

```
python tools/perflog.py report  "C:\Users\you\Documents\Timberborn\PerformanceLog\2026-09-21_14-05-33"
python tools/perflog.py compare <folder A> <folder B>
python tools/perflog.py list
```

`report` says what stands out, with the evidence and what to check next; `compare` lines up two sessions and says what changed and what else differed
(different mods or Harmony patches, game speed, colony size, computer), and `report` names the other mods whose patches run inside a singleton's time.
The tool is also in the release ZIP, in `PerformanceLog\tools`. For a fair comparison, record the same save at the same game speed for at least three
minutes each, with the window in front, and change one thing.

## What it records

- **Every frame's time and where it went**, exclusive so the parts add up: the simulation tick loop, the once-per-tick singletons, every entity's tick, the wait for
  the parallel tick, the per-frame singleton updates, saving, and the rest (drawing, other scripts, other mods, the system). Unity's own phases of
  a frame. Whether the game thread is busy or waiting, and Unity's frame timing where the build provides it.
- **Which singleton, kind of entity and mod the time goes to.** Singletons are timed on every call, so a slow frame is blamed on the exact singleton
  (`spikes.csv`). Entity kinds are sampled inside an overhead budget and scaled up. With `Profile = deep`, every kind of entity component too.
  Each is tagged with the **mod** it belongs to.
- **Garbage and memory:** collections, allocation per part and per singleton, the managed heap, Unity's memory pools.
- **Saves** with their stages, and **loading**: the time of every singleton's `Load` and `PostLoad`, so a mod that slows loading is named.
- **The colony** (entities, beavers, bots, day), the **game speed** and whether the game was paused or in the background.
- **The computer and the game:** processor, graphics card, display and vertical sync, garbage collector settings, `boot.config`, launch options, every enabled mod
  with its version, and **which mod patches which hot method** (from Harmony's own records).
- **What each measurement source could do** and whether it actually produced anything, so a zero is never mistaken for a measurement.
- **What measuring itself costs**, per frame, in the file.

Any method can also be timed by name (`Watch` in the config). See the file.

## Settings

**The defaults capture the most detail a session can hold without touching anything**: deep profiling (every entity component sampled,
not just entity kinds), every spike slot filled, and a sampling budget wide enough that `profile.csv`'s numbers rest on real samples
rather than a rough scale-up. This costs a bit more than a lighter profile — see `OverheadBudgetPercent` below — so lower these if that
ever matters more than the detail.

Six of the settings can be changed from Timberborn's **Mod Settings** menu, with no restart: they take effect from the next game or save you load.
The rest — `Enabled`, `Profile`, `Watch` and `OutputFolder` — decide which parts of the game get patched, which is settled before that menu exists,
so they live only in `PerformanceLog.cfg` (next to `manifest.json` in the mod's `version-1.1` folder) and need the game restarted after editing.
Nothing here changes what the game simulates, so co-op players may use different values.

| Setting | Default | Where | What it does |
|---|---|---|---|
| `Enabled` | `true` | .cfg only | `false` = the mod does nothing at all (no patches, no files). |
| `SlowFrameMs` | `50` | .cfg or Mod Settings | A frame this long gets its own row in `frames.csv`. |
| `SummarySeconds` | `10` | .cfg or Mod Settings | How often a summary row is written. |
| `ProfileSeconds` | `30` | .cfg or Mod Settings | How often `profile.csv` is written. |
| `Profile` | `deep` | .cfg only | `off` (no patch on the entity tick at all), `standard` (entity kinds sampled) or `deep` (also samples every entity component, e.g. `Walker`, `Workplace`). |
| `OverheadBudgetPercent` | `1` | .cfg or Mod Settings | How much of a second the sampling may spend measuring; it widens the sampling on its own when there are many entities. |
| `SpikeContributors` | `8` | .cfg or Mod Settings | How many of the biggest contributors to each slow frame are written to `spikes.csv` (8 is the most it can hold). |
| `MaxSlowRowsPerMinute` | `300` | .cfg or Mod Settings | At most this many slow frames get a row a minute; the rest are only counted, so a game that is slow all the time cannot fill the disk. |
| `OutputFolder` | *(empty)* | .cfg only | Where session folders go. Empty = `Documents\Timberborn\PerformanceLog`. |
| `Watch` | *(none)* | .cfg only | Full names of methods to time: `Namespace.Type.Method`, separated by `;`, on as many lines as you like. Rows appear in `profile.csv` as kind `method`. |

The first time you open the Mod Settings page it starts from whatever `PerformanceLog.cfg` already says; after that, whatever you set there is what
is used, and editing that number in the file no longer does anything (Mod Settings remembers it, not this mod).

To measure another mod's method, for example:

```
Watch = LateGamePerformance.HaulCache.OnTickStarted; LateGamePerformance.MetricsDump.OnTickStarted
```

The mod must be enabled so its type can be found. Every call of a watched method pays for a Harmony wrapper and a lookup even when it is not timed, so watching a method that runs thousands of times a tick costs
more than watching a rare one; the cost is in `overheadUs`. Methods with a `catch ... when` clause are refused (Harmony cannot patch them under Mono and the
attempt can crash the game) and the log says so.

## Working with other mods

The mod puts a timing wrapper in front of each of the game's singletons, but only on the first tick and first frame of a game, after every other mod's `Load` patches have run, so a mod that looks at
those singletons (BeaverBuddies reorders the once-per-tick ones by their type) still sees the game's own and tick order is what it would be without this mod. It never replaces a game method and
never skips the original. When another mod defers the game's save to the end of a tick (BeaverBuddies does), the `queued save` event reads about 0 ms and the real one is the `save (writing the world)`
event.

## What it costs, and what it cannot see

It aims to stay small, and it measures itself: `overheadUs` (the estimate of the timers, samples and patches in a frame) and `probeUs` (closing the frame) are
in every row, the calibration is in the header, and `summary.md` warns if the total is more than 2% of a frame. While no log is running (in a menu, say) each patch costs one flag
read. The per-frame path allocates nothing (a test checks this), rows go into buffers allocated once, and a thread of its own writes the files twice a
second, so no disk work happens on the frame being measured.

It cannot see inside the parallel tick (the worker threads), only the game thread's wait for it. It cannot see mods that hook the game without Harmony (MonoMod,
native detours). Allocation is exact only if the game's runtime provides a per-thread allocation counter; otherwise it uses the heap size and says the figures are coarse.
Everything is timed on the game thread only.

## How it relates to the BeaverBuddies frame rate log

The measuring core is the frame rate log built for the BeaverBuddies Stability Fork in `1.0.10-perflog-preview` and `preview2`
([`perflog-preview` branch](https://github.com/timbermods/BeaverBuddies-Stability-Fork/tree/perflog-preview)), rebuilt as a standalone mod that works in single
player and with any mods. What is new: it hooks the game itself (no other mod's code has to be edited), attributes time to mods, records loading and saves,
generates its own readme and summary, and comes with the analysis tool. What is left out because it is about the co-op layer: the network and event-hash
timings, waits for the other player, and the garbage collection experiment (this mod does not change how the game runs).

## Build from source

Install the .NET 8 SDK and Python 3, and have Timberborn (and the Harmony Workshop mod) installed:

```powershell
.\build.ps1 -GameDir 'C:\Program Files (x86)\Steam\steamapps\common\Timberborn'
```

builds the mod, runs the checks, and creates `dist\PerformanceLog-0.1.2.zip`. `.\build.ps1 -Install` also copies it into your `Mods` folder. The checks alone:

```
dotnet run --project tests -c Release
python -m unittest discover -s tools -p "test_perflog.py"
```

No game, Unity or Harmony DLLs are redistributed; they are only build references. See [CLAUDE.md](CLAUDE.md) for how the code is laid out and how to change it.

## License

MIT. See [LICENSE](LICENSE).
