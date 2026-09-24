# Performance Log

A Timberborn mod that measures where the game's time and memory go. It writes each session to a folder that you, or an
AI assistant such as Claude, can read. Use it to find out why a game is slow, in the game itself or in another mod.

It only observes: it never changes what the game simulates. If one of its parts fails, that part switches off, the
recording says so, and the game carries on.

**Preview.** Version 0.1.4 has not been played yet; its automated checks run against the game's own code. Versions
0.1.0, 0.1.1 and 0.1.3 have been played. A 44 minute 0.1.3 recording with ten mods showed the default settings working.
Not yet seen in a game: a value changed in Mod Settings reaching a recording, and [co-op](#co-op-and-other-mods). See
[what has been checked](docs/TESTING.md) and the [changelog](CHANGELOG.md).

## What it records

- **Each frame's time**, in parts that add up: the simulation tick, updates, saving, and the rest (drawing, scripts).
- **Who the time goes to:** each singleton (a game service), kind of entity and entity component, tagged with its mod.
- **Memory, saves and loading:** garbage collections and the heap; each save, and each loading step with its mod.
- **Context:** colony size, game speed, your computer, every enabled mod, and which mod patches which busy method.
- **What each measuring part could do,** so a zero is never mistaken for a measurement.

## Install

You need Timberborn 1.1 (the mod is built against 1.1.2.4) and two Steam Workshop mods: **Harmony** 2.4.1 or newer and
**Mod Settings** 1.1.0.0 or newer.

1. Open the [Releases page](https://github.com/timbermods/PerformanceLog/releases) and pick the release marked
   **Latest**. Under **Assets**, download `PerformanceLog-<version>.zip`, not "Source code".
2. Close Timberborn. Extract the zip into `Documents\Timberborn\Mods`. It holds one `PerformanceLog` folder.
3. Start Timberborn, enable **Harmony**, **Mod Settings** and **Performance Log** in the mod manager, and restart.
4. Play. Leave the game through its menu so the files are finished.

Each game is recorded from when it finishes loading until you leave it, into its own folder in
`Documents\Timberborn\PerformanceLog`, named by the local date and time (for example `2026-09-21_14-05-33`). After a
crash the files are still readable, and `summary.md` is at most a minute old. `[PerformanceLog]` lines in `Player.log`
(`%USERPROFILE%\AppData\LocalLow\Mechanistry\Timberborn\Player.log`) say what the mod did.

## Find out why a game is slow

Give the whole session folder to Claude, or zip it and attach it. Say what the problem felt like and ask it to
diagnose. A prompt that works:

> This is a Performance Log session folder from Timberborn. Read `summary.md` and `README.md` first, then work out why the game was slow. It felt like: *(low frame rate all the time / hitches every minute / slow at speed 3 / slow to load)*. Say what the evidence is, how sure you are, and what to change or record next.

### What is in the folder

| File | What it holds |
|---|---|
| `summary.md` | The whole session on one page. **Start here.** A **Read first** list at the top, if there is one, says what did not work. |
| `frames.csv` | One row per slow frame and one per summary window, with where the time went. Its header describes the game, mods and computer. |
| `profile.csv` | Time and memory per singleton, kind of entity, component and watched method, each tagged with its mod. |
| `spikes.csv` | The biggest contributors to each slow frame. |
| `events.csv` | Saves, loading, and the start and end of the session. |
| `README.md`, `columns.md` | How to read the numbers, a step-by-step way to diagnose, and what every column means. |

### The analysis tool

It needs Python 3.8 or newer and nothing else. It comes in the zip: run it from
`Documents\Timberborn\Mods\PerformanceLog`.

```
python tools/perflog.py report  "C:\Users\you\Documents\Timberborn\PerformanceLog\2026-09-21_14-05-33"
python tools/perflog.py compare <folder A> <folder B>
python tools/perflog.py list
```

- `report` says what stands out, with the evidence and what to check next.
- `compare` says what changed between two sessions, and what else differed: mods, game speed, colony size, computer.
- `list` shows every session in `Documents\Timberborn\PerformanceLog`, or in a folder you name.

For a fair comparison, record the same save at the same game speed for at least three minutes each, with the window in
front. Change one thing.

## Settings

The defaults record the most detail a session can hold, at a little more cost than a lighter profile. To measure more
lightly, set `Profile = standard` or lower `OverheadBudgetPercent`.

Six settings are in Mod Settings → **Performance Log**, and a change applies from the next game you load. The rest are
only in `PerformanceLog.cfg`, in `Documents\Timberborn\Mods\PerformanceLog\version-1.1`; restart Timberborn after
editing it. The Mod Settings page starts from the file's values, then keeps its own.

| In the file | In Mod Settings | Default | What it does |
|---|---|---|---|
| `Enabled` | | `true` | `false` turns the mod off: no patches, no files. |
| `SlowFrameMs` | **Slow frame threshold (ms)** | `50` | A frame this long gets its own row in `frames.csv`. |
| `SummarySeconds` | **Summary window (s)** | `10` | How often a summary row is written. |
| `ProfileSeconds` | **Profile window (s)** | `30` | How often `profile.csv` gets new rows. |
| `Profile` | | `deep` | `off` (frames, ticks and singletons only), `standard` (also entity kinds) or `deep` (also every entity component, such as `Walker`). |
| `OverheadBudgetPercent` | **Overhead budget (% of a frame)** | `1` | How much of the time sampling may spend measuring. Sampling thins out to stay under it. |
| `SpikeContributors` | **Spike contributors** | `8` | How many of each slow frame's biggest contributors go to `spikes.csv`, 0 to 8. 0 turns `spikes.csv` off. |
| `MaxSlowRowsPerMinute` | **Slow frame rows per minute, at most** | `300` | Slow frames beyond this each minute are only counted, so a slow game can't fill the disk. |
| `OutputFolder` | | *(empty)* | Where session folders go. Empty means `Documents\Timberborn\PerformanceLog`. |
| `Watch` | | *(none)* | Methods to time by name. See [Time a method by name](#time-a-method-by-name). |
| `AutoWatch` | | `false` | `true` also times other mods' patches on the game's busiest methods. |

## Time a method by name

To time a mod's own work, or a game method that mods patch, add a `Watch` line to `PerformanceLog.cfg`. List the full
names (`Namespace.Type.Method`), separated by `;`:

```
Watch = LateGamePerformance.HaulCache.OnTickStarted; LateGamePerformance.MetricsDump.OnTickStarted
```

- Use as many `Watch` lines as you like, up to 40 methods. Every overload of a name is timed.
- The mod that owns the method must be enabled.
- Each method gets a `method` row in `profile.csv`. The `# watch|` lines in the `frames.csv` header say whether each
  name is timed, or why not.
- Every call of a watched method costs a little, so one called thousands of times a tick costs more. The cost is in
  `overheadUs`.

**`AutoWatch = true`** times the patches other mods put on the game's busiest methods, without naming them. A prefix on
every entity's tick, for example, is otherwise hidden inside the game's own row. When the first game loads, it takes the
Watch slots your `Watch` lines leave free. Each patch gets its own `method` row, tagged with its mod; the `frames.csv`
header lists what it took and what it left out. It is off by default because of the cost per call: compare
`overheadUs` with it on and off.

## Co-op and other mods

Because it never changes the simulation, it should not cause a desync, and co-op players may use different settings.
Other mods' patches, and the order the game runs its parts in, stay as they would be without it.

**Keep `AutoWatch` off in co-op** until it has been played there. Under BeaverBuddies or Timber Together, making a
patch uses up some of the game's random numbers. The auto watch puts them back exactly, but that is reasoned from the
code, not yet seen in a two-player game.

## What it costs, and what it can't see

It measures its own cost in every row (`overheadUs` and `probeUs`). `summary.md` warns when that is more than 2% of a
frame. In the menus it costs next to nothing.

- It times the game thread only: it sees the wait for the parallel tick, not the worker threads' work.
- It can't see mods that hook the game without Harmony.
- Allocation figures are coarse unless the game provides an exact counter. The recording says which.

## Credits and license

The measuring core comes from the frame rate log in the
[BeaverBuddies Stability Fork](https://github.com/timbermods/BeaverBuddies-Stability-Fork/tree/perflog-preview),
rebuilt as a standalone mod. MIT license: see [LICENSE](LICENSE).

To build the mod or run its checks, see [docs/TESTING.md](docs/TESTING.md). [CLAUDE.md](CLAUDE.md) explains the code.
