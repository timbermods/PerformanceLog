# Changelog

## 0.1.0 (preview, not yet run in a game)

First release. A standalone mod that records where a Timberborn session's time and memory go, built from the frame rate log of the BeaverBuddies Stability Fork
(`1.0.10-perflog-preview2`), rebuilt to hook the game itself.

- Per-frame timing with exclusive slots: tick loop, once-per-tick singletons, entity ticks, wait for and start of the parallel tick, per-frame updates and late updates, saving.
  Unity's frame phases, processor time, profiler counters and frame timing where the build provides them; garbage collection, heap and allocation.
- A profile of every singleton (timed on every call), every kind of entity (sampled inside an overhead budget), every entity component (`Profile = deep`) and any method named in
  the config (`Watch`), each tagged with the mod it belongs to. Slow frames are attributed to the singletons that took their time (`spikes.csv`).
- Saves with their stages, and loading: the time of every singleton's `Load` and `PostLoad`.
- The computer, game and settings, every enabled mod with its version, which mod patches which hot method, what each measurement source could do, and what measuring costs.
- Each session folder holds `summary.md` (one page, refreshed every minute), `README.md` (how to read and diagnose), `columns.md`, `frames.csv`, `profile.csv`, `spikes.csv` and `events.csv`.
- `tools/perflog.py`: `report` (findings with evidence and next steps), `compare` (two sessions, with what else differed), `list`. Standard library only.
