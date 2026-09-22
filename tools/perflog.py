#!/usr/bin/env python3
"""Reads Performance Log sessions: says where a Timberborn session's time and memory went, and compares two sessions.

    python perflog.py report  <session folder | frames.csv>   [--warmup 30] [--top 12] [--all] [--json]
    python perflog.py compare <folder A> <folder B>           [--warmup 30] [--top 15]
    python perflog.py list    [<PerformanceLog folder>]

Standard library only (Python 3.8 or newer). A session folder is what the mod writes to Documents/Timberborn/PerformanceLog/<time>/;
read the README.md and columns.md inside it for what the numbers are.

The report states what the numbers say and what they are consistent with. It ends with pointers, not proofs: a pointer says which
evidence it rests on and what to check next, and the tool never claims more than the data supports.
"""
import argparse
import bisect
import collections
import csv
import json
import math
import os
import re
import statistics
import sys

SLOTS = ["tickMs", "singMs", "entMs", "parWaitMs", "parStartMs", "updMs", "lateMs", "saveMs"]
TICK_SLOTS = ["tickMs", "singMs", "entMs", "parWaitMs", "parStartMs"]
SLOT_MEANING = {
    "tickMs": "the tick loop itself (the game's bookkeeping and other mods' patches on it)",
    "singMs": "the once-per-tick singletons", "entMs": "every entity's tick",
    "parWaitMs": "the game thread waiting for the parallel tick", "parStartMs": "starting the parallel tick",
    "updMs": "per-frame singleton updates", "lateMs": "per-frame late updates", "saveMs": "saving the game",
    "otherMs": "the rest: drawing, other scripts, other mods, the system",
}
PHASES = ["plTime", "plInit", "plEarly", "plFixed", "plPre", "plUpdate", "plLate", "plPost"]
# The timed parts that run inside Unity's Update phase (the game's tick loop and its singleton updates). lateMs runs in the LateUpdate phase
# (plLate); nothing this mod times runs in the phases before Update.
UPDATE_PHASE_SLOTS = TICK_SLOTS + ["updMs"]
EARLY_PHASES = ["plTime", "plInit", "plEarly", "plFixed", "plPre"]
# How otherMs splits by phase: (key, label, what it is). Summary.OtherByPhase in the mod splits the same way.
OTHER_SPLIT = [
    ("update", "Update phase, outside the timed parts", "other scripts' Update (the game's and mods' MonoBehaviours) and coroutines"),
    ("late", "LateUpdate phase, outside lateMs", "other work in Unity's LateUpdate phase: animation, UI Toolkit, scripts' LateUpdate"),
    ("post", "plPost", "drawing, presenting the frame and the wait for vertical sync"),
    ("phases", "Unity's other phases", "plTime to plPre: time, input, physics"),
    ("between", "between the phases", "what falls between Unity's phases"),
]
FRAME_EDGES_DEFAULT = [4, 6, 8.5, 11.5, 14, 17.5, 21, 25, 30, 35, 42, 50, 75, 100, 200, 400]
REQUIRED = ["type", "frame", "tick", "utcMs", "frames", "frameMs", "maxFrameMs", "ticks", "speed", "paused", "saving", "unfocused"] + SLOTS + \
    ["otherMs", "gcDelta", "heapMB", "allocKB", "overheadUs", "probeUs"]
KIND_TITLES = collections.OrderedDict([
    ("tick-singleton", "Singletons ticked once per simulation tick"),
    ("update-singleton", "Singletons updated once per frame"),
    ("late-singleton", "Singletons late-updated once per frame"),
    ("parallel-start", "Parallel singletons: the game thread starting them"),
    ("entity", "Entity kinds (sampled)"),
    ("component", "Entity components (sampled; Profile = deep)"),
    ("method", "Watched methods (config Watch)"),
])
# A session whose mean frame is faster than this (50 fps) is not what anyone complains about, so a share of its frame is not a finding.
SLOW_MEAN_MS = 20.0
# A singleton is blamed for a slow frame only when it took at least this share of the frame (percent) or this many milliseconds, as in the
# mod's summary.md (Summary.BlameMinShare, BlameMinMs): the biggest singleton of a frame a save or a collection made slow is a millisecond or two.
BLAME_MIN_SHARE = 10.0
BLAME_MIN_MS = 5.0
LOAD_KINDS = ("load", "load-non-singleton", "post-load", "post-load-non-singleton")

# Up to 0.1.3 the `# calibration|` line's patchCallNs timed an empty patch instead of the patch bodies, and every recording shows 0 there. What
# overheadUs charged each call of the per-call patches then depends on a reading the header rounds away: exactly 0 made the mod charge an assumed
# 40 ns a call, a reading above 0 made it charge that. A reading above 0 in the header, or a row whose overheadUs is below 40 ns a patch call, proves
# the second, and an empty patch leaves the bodies out, so those calls were undercharged.
# Neither note is printed for a recording whose calibration line has patchBodyNs (KNOWN_ISSUE_APPLIES): a build of the fix that still carries an
# older version number measures the patch bodies already.
PATCH_CHARGE_ASSUMED_NS = 40.0
PATCH_COST_NOTE = ("overheadUs understates what the mod itself cost: every call of its per-call patches (entity ticks, components, watched "
                   "methods; the patchCalls column) was charged less than they cost. patchCallNs in the `# calibration|` line timed an empty patch "
                   "instead of the real bodies and came out above 0 (the header says so, or this recording has rows whose overheadUs is below "
                   "the 40 ns a patch call the mod assumes when that reading is exactly 0), so those calls (about 100,000 a second at speed 7 with Profile = deep) "
                   "are missing from overheadUs and from the 'measuring cost more than 2% of a frame' warning. The real bodies take a few "
                   "nanoseconds a call, plus what Harmony adds.")
PATCH_COST_GUESS_NOTE = ("overheadUs's charge for the per-call patches (entity ticks, components, watched methods; the patchCalls column) is not "
                         "a measurement. patchCallNs|0 in the `# calibration|` line timed an empty patch instead of the real bodies: a reading of "
                         "exactly 0 made the mod charge an assumed 40 ns a call, one just above 0 made it charge almost nothing, and no row in "
                         "this recording shows which. The real bodies take a few nanoseconds a call, plus what Harmony adds.")

# Printed only for a recording whose entity rows really are split (KNOWN_ISSUE_APPLIES): a build of the fix that still carries an older version number
# writes kinds already.
ENTITY_SPLIT_NOTE = ("Entity rows are split by name: a beaver or bot loaded from the save is keyed by its own name ('BeaverAdult <name>'; the game renames "
                     "characters as it loads them) and one born during play by 'BeaverAdult(Clone)', so profile.csv has rows named after single beavers and "
                     "the summary.md written in the game ranks beavers far too low. This report adds entity rows up by kind (the name up to its first space "
                     "or '('); the recording's own files do not.")

# What is wrong with recordings made by an older Performance Log, found when a recording was first read. Each entry is (fixed in, note): the note
# is printed at the top of the report for a recording made by an earlier version, so nobody trusts a figure that was known to be off.
KNOWN_ISSUES = [
    ("0.1.1", "The mod swapped its timing wrappers into the game's singleton arrays about four times every frame (it remembered only one of the two "
              "services that alternate). The garbage that made, and the time (inside otherMs, before the timed parts), are in this recording: allocation "
              "figures (otherKB, allocKB, the garbage-collection section) probably read too high, and the mod's own cost is understated by overheadUs."),
    ("0.1.1", "In the by-singleton tables every singleton of the game itself is labelled with an empty mod (shown as unknown); read those as the game's own."),
    ("0.1.1", "workingMB is 0 (the process's memory was not read), loading steps carry no allocation figure, and `# capability-final|patchCalls|SingletonLifecycleService.LoadAll|0|never ran` "
              "is wrong (it ran before the session's counters were cleared)."),
    ("0.1.1", "prDraw and prBatches are 0: Unity 6 has no counter by those names (its draw calls are split into several). The other columns are unaffected."),
    ("0.1.1", "While the game ran, frames.csv, profile.csv, spikes.csv and events.csv were held open by the mod, so copying or zipping the folder could leave them out "
              "(the folder listing shows size 0). Exit the game first, or read them with shared access."),
    ("0.1.4", PATCH_COST_NOTE),
    ("0.1.4", PATCH_COST_GUESS_NOTE),
    ("0.1.4", ENTITY_SPLIT_NOTE),
]


def _empty_patch_charges(session):
    """For a recording whose `# calibration|` line has patchCallNs and no patchBodyNs (an empty patch was timed, not the bodies): how many
    rows ran patches, how many of those were charged less than the 40 ns a patch call assumed for a reading of exactly 0, and the reading.
    None otherwise."""
    calibration = session.pipe("calibration")
    if not calibration or any("patchBodyNs" in parts for parts in calibration):
        return None
    reading = [parts[i + 1] for parts in calibration for i in range(len(parts) - 1) if parts[i] == "patchCallNs"]
    try:
        ns = float(reading[0]) if reading else None
    except ValueError:
        return None
    if ns is None:
        return None
    ran = below = 0
    for r in session.rows:
        calls, frames = r.get("patchCalls", 0.0), r.get("frames", 0.0)
        if calls <= 0 or frames <= 0:
            continue
        ran += 1
        # overheadUs is a mean per frame, written to 0.1 us; patchCalls is the total over the row's frames.
        if (r.get("overheadUs", 0.0) + 0.05) * frames < PATCH_CHARGE_ASSUMED_NS / 1000.0 * calls:
            below += 1
    return ran, below, ns


def _patch_cost_was_understated(session):
    charges = _empty_patch_charges(session)
    return charges is not None and charges[0] > 0 and (charges[1] > 0 or charges[2] > 0)


def _patch_cost_was_a_guess(session):
    charges = _empty_patch_charges(session)
    return charges is not None and charges[0] > 0 and charges[1] == 0 and charges[2] == 0


def _entity_rows_are_split(session):
    return any(r["kind"] == "entity" and entity_kind(r.get("name", "")) != r.get("name", "") for r in session.profile)


# Notes that only some recordings of the versions they name have, each with the check that finds the problem in a recording (a note not listed
# here is printed for every recording made before its fix).
KNOWN_ISSUE_APPLIES = {PATCH_COST_NOTE: _patch_cost_was_understated, PATCH_COST_GUESS_NOTE: _patch_cost_was_a_guess,
                       ENTITY_SPLIT_NOTE: _entity_rows_are_split}

GAME_ASSEMBLY_PREFIXES = ("Timberborn.", "Bindito.", "UnityEngine", "Unity.", "System")


def game_assembly(assembly):
    """The same rule the mod uses to call a DLL the game's own, for recordings made before the mod applied it."""
    return bool(assembly) and (assembly.startswith(GAME_ASSEMBLY_PREFIXES) or assembly in ("Assembly-CSharp", "mscorlib"))


def version_tuple(text):
    numbers = re.findall(r"\d+", text or "")
    return tuple(int(n) for n in numbers[:3]) if numbers else None


def known_issues(session):
    """Notes about figures in this recording that are known to be off, from the version of the mod that made it."""
    have = version_tuple(session.h("mod"))
    if have is None:
        return []
    return [note for fixed, note in KNOWN_ISSUES if have < version_tuple(fixed) and KNOWN_ISSUE_APPLIES.get(note, lambda _: True)(session)]


# ---------------------------------------------------------------- reading

class Session:
    """One recording: the header, the frame rows, the profile, the spikes and the events."""

    def __init__(self, folder):
        self.folder = folder
        self.name = os.path.basename(os.path.normpath(folder))
        self.header = {}
        self.pipes = collections.defaultdict(list)   # '# kind|a|b' lines
        self.columns = []
        self.rows = []
        self.profile = []
        self.spikes = []
        self.events = []
        self.ended = False
        self.skipped = 0
        self.problems = []

    def rows_of(self, kind):
        return [r for r in self.rows if r["type"] == kind]

    @property
    def summaries(self):
        return [r for r in self.rows if r["type"] == "S" and r["frames"] > 0]

    @property
    def slow(self):
        return self.rows_of("F")

    @property
    def mods(self):
        result = {}
        for p in self.pipes.get("mod", []):
            if len(p) >= 3:
                result[p[0]] = (p[1], p[2])
        return result

    def pipe(self, kind, key=None):
        found = self.pipes.get(kind, [])
        if key is None:
            return found
        return [p for p in found if p and p[0] == key]

    def h(self, key, default=""):
        return self.header.get(key, default)

    def num(self, key, default=0.0):
        try:
            return float(self.header.get(key, default))
        except ValueError:
            return default

    @property
    def label(self):
        return self.name


def _parse_header_line(session, line):
    text = line[1:].strip()
    if text == "end":
        session.ended = True
        return
    if text.startswith("rows dropped"):
        session.problems.append(text)
        return
    head = text.split("|", 1)[0]
    if "|" in text and " " not in head and ":" not in head:
        parts = text.split("|")
        session.pipes[parts[0]].append(parts[1:])
    elif ": " in text and not text.startswith(("note:", "patches-note:")):
        key, _, value = text.partition(": ")
        session.header[key.strip()] = value.strip()


def _read_table(path, numeric_tail):
    """Yields (columns, [row dicts], header lines, skipped) from a CSV that may start with # lines and may be cut short."""
    columns, rows, comments, skipped = [], [], [], 0
    if not os.path.exists(path):
        return columns, rows, comments, skipped
    with open(path, "r", encoding="utf-8", errors="replace", newline="") as f:
        for line in f.read().splitlines():
            if not line.strip():
                continue
            if line.startswith("#"):
                comments.append(line)
                continue
            fields = line.split(",")
            if not columns:
                columns = fields
                continue
            if len(fields) != len(columns):
                skipped += 1  # a file cut short by a crash ends in half a line
                continue
            row = {}
            for name, value in zip(columns, fields):
                if name in numeric_tail:
                    row[name] = value
                else:
                    try:
                        row[name] = float(value)
                    except ValueError:
                        row[name] = value if name in ("type", "kind") else 0.0
            rows.append(row)
    return columns, rows, comments, skipped


def load_session(path):
    """Loads a session from its folder, or from the frames.csv in it."""
    if os.path.isfile(path):
        path = os.path.dirname(os.path.abspath(path))
    if not os.path.isdir(path):
        raise FileNotFoundError("not a folder or a frames.csv: %s" % path)
    session = Session(path)
    frames = os.path.join(path, "frames.csv")
    if not os.path.exists(frames):
        raise FileNotFoundError("no frames.csv in %s (is this a Performance Log session folder?)" % path)
    session.columns, session.rows, comments, session.skipped = _read_table(frames, ())
    for line in comments:
        _parse_header_line(session, line)
    if not session.columns or session.columns[0] != "type":
        raise ValueError("%s does not look like a Performance Log frames.csv" % frames)
    missing = [c for c in REQUIRED if c not in session.columns]
    if missing:
        session.problems.append("frames.csv lacks columns this tool expects (%s); it may be from another version of the mod" % ", ".join(missing[:6]))
    for row in session.rows:
        for c in REQUIRED:
            row.setdefault(c, 0.0)
    _, session.profile, _, _ = _read_table(os.path.join(path, "profile.csv"), ("name", "assembly", "mod"))
    _, session.spikes, _, _ = _read_table(os.path.join(path, "spikes.csv"), ("name", "mod"))
    session.events = _read_events(os.path.join(path, "events.csv"))
    return session


def _read_events(path):
    events = []
    if not os.path.exists(path):
        return events
    with open(path, "r", encoding="utf-8", errors="replace", newline="") as f:
        for i, fields in enumerate(csv.reader(f)):
            if i == 0 or len(fields) < 5:
                continue
            try:
                events.append({"utcMs": float(fields[0]), "tick": float(fields[1]), "kind": fields[2], "ms": float(fields[3]), "detail": ",".join(fields[4:])})
            except ValueError:
                continue
    return events


# ---------------------------------------------------------------- numbers

def wmean(rows, column):
    frames = sum(r["frames"] for r in rows)
    return sum(r[column] * r["frames"] for r in rows) / frames if frames else 0.0


def wsum(rows, column):
    """A total over rows whose value is an average per frame."""
    return sum(r[column] * r["frames"] for r in rows)


def total(rows, column):
    return sum(r[column] for r in rows)


def seconds_of(rows):
    return sum(r["frameMs"] * r["frames"] for r in rows) / 1000.0


def heap_alloc(session):
    """True when allocation was read from the size of the managed heap (the allocSource capability), which falls at a garbage collection."""
    return any(len(p) >= 2 and p[1].startswith("GC.GetTotalMemory") for p in session.pipe("capability", "allocSource"))


def alloc_unmeasured(session, rows):
    """The slow frames inside the summary rows `rows` whose allocation was not measured: how many, their milliseconds, and the heap growth
    they still showed (their positive allocKB, which the rows' allocKB totals hold). With the heap size as the counter a frame with a
    collection loses what it allocated; its row has gcDelta > 0 or (the heap shrank) a negative allocKB. That holds for recordings of every
    version. Frames too short to have a row of their own are not known here; they are short."""
    if not heap_alloc(session) or not rows:
        return 0, 0.0, 0.0
    ordered = sorted(rows, key=lambda r: r["frame"])
    ends = [r["frame"] for r in ordered]
    count, ms, kb = 0, 0.0, 0.0
    for f in session.slow:
        if f["gcDelta"] <= 0 and f["allocKB"] >= 0:
            continue
        i = bisect.bisect_left(ends, f["frame"])   # the first window that ends at or after the frame; a window covers (frame - frames, frame]
        if i < len(ends) and ordered[i]["frame"] - ordered[i]["frames"] < f["frame"]:
            count += 1
            ms += f["frameMs"]
            kb += max(0.0, f["allocKB"])
    return count, ms, kb


def alloc_seconds(session, rows):
    """Seconds of the rows whose allocation was measured: a rate of allocation divides by these, since the lost frames add nothing to it."""
    return max(0.0, seconds_of(rows) - alloc_unmeasured(session, rows)[1] / 1000.0)


def alloc_rate(session, rows):
    """KB allocated per second over the rows (allocKB, the heap growth), leaving out the frames whose allocation was not measured: both their
    time and what growth they still showed, since a collection took away an unknown part of what they allocated."""
    secs = alloc_seconds(session, rows)
    return max(0.0, total(rows, "allocKB") - alloc_unmeasured(session, rows)[2]) / secs if secs > 0 else 0.0


def alloc_unmeasured_note(session, rows):
    """What the report says about frames whose allocation was not measured (heap-size counter), or None. The mod counts them over the whole
    session in its '# capability-final|allocSource|' line; a recording made before it did has only its slow rows to go on. Either way the
    figures above cover only the rows `rows`, so the note also says how many of the frames are in them."""
    if not heap_alloc(session):
        return None
    counted = None
    for p in session.pipe("capability-final", "allocSource"):
        found = re.search(r"not measured in (\d+) frame", "|".join(p))
        if found:
            counted = int(found.group(1))
    source = ""
    if counted is None:
        counted = sum(1 for f in session.slow if f["gcDelta"] > 0 or f["allocKB"] < 0)
        if not counted:
            return None
        source = " (this recording does not count them, so these are its slow rows with a collection)"
        frames = "at least %d frame%s" % (counted, "" if counted == 1 else "s")
    else:
        frames = "%d frame%s" % (counted, "" if counted == 1 else "s")
    inside, inside_ms, _ = alloc_unmeasured(session, rows)
    scope = ("The per-second figure leaves out the %d slow frame%s (%.1f s) of them in these windows." % (inside, "" if inside == 1 else "s", inside_ms / 1000.0)
             if inside else "None of the slow ones is in these windows.")
    return ("allocation not measured in %s in the whole session, each with a collection%s: the heap-size counter falls at one, so what was "
            "allocated in them is lost. %s" % (frames, source, scope))


def phases_measured(session, rows):
    """True when the recording timed Unity's phases (the playerLoop capability), so otherMs can be split by them."""
    return all(p in session.columns for p in PHASES) and sum(wmean(rows, p) for p in PHASES) > 0


def split_other(r):
    """otherMs of one row (times per frame) by Unity phase: each phase less the timed parts that run in it, plPost, the phases before Update,
    and what falls between the phases. The game saves in its LateUpdate and a mod that defers the save to the end of a tick (BeaverBuddies)
    in Update; the row does not say which, so saveMs is taken out of the phase with more room left. That is the phase it ran in, except for a
    save shorter than the gap between the two phases' own remainders: then one phase reads high and the other low by up to the save. A summary
    row that holds both kinds of save is split approximately, and the mod's summary.md (Summary.OtherByPhase) splits the session's mean row,
    this report each window, so the two can differ by as much in a session that has both."""
    update = r.get("plUpdate", 0.0) - sum(r[s] for s in UPDATE_PHASE_SLOTS)
    late = r.get("plLate", 0.0) - r["lateMs"]
    if r["saveMs"] > 0:
        if late >= update:
            late -= r["saveMs"]
        else:
            update -= r["saveMs"]
    parts = collections.OrderedDict([("update", max(0.0, update)), ("late", max(0.0, late)), ("post", r.get("plPost", 0.0)),
                                     ("phases", sum(r.get(p, 0.0) for p in EARLY_PHASES))])
    parts["between"] = max(0.0, r["otherMs"] - sum(parts.values()))
    return parts


def other_by_phase(rows):
    """split_other over summary rows, in ms per frame (each row weighted by its frames)."""
    frames = sum(r["frames"] for r in rows)
    out = collections.OrderedDict((key, 0.0) for key, _, _ in OTHER_SPLIT)
    for r in rows:
        for key, value in split_other(r).items():
            out[key] += value * r["frames"]
    return collections.OrderedDict((k, v / frames) for k, v in out.items()) if frames else out


def frame_edges(session):
    for p in session.pipe("histogram", "frameEdgesMs"):
        try:
            return [float(x) for x in p[1].split(",")]
        except (IndexError, ValueError):
            pass
    return FRAME_EDGES_DEFAULT


def percentile(session_or_edges, rows, p, max_ms=None):
    """A frame-time percentile from the histogram columns of summary rows, linear inside a bucket."""
    edges = frame_edges(session_or_edges) if isinstance(session_or_edges, Session) else session_or_edges
    counts = [sum(r.get("fh%d" % i, 0.0) for r in rows) for i in range(len(edges) + 1)]
    n = sum(counts)
    if n <= 0:
        return 0.0
    target, running = n * p, 0.0
    for i, c in enumerate(counts):
        if running + c >= target and c > 0:
            lower = 0.0 if i == 0 else edges[i - 1]
            upper = edges[i] if i < len(edges) else max(max_ms or lower, lower)
            return lower + (upper - lower) * ((target - running) / c)
        running += c
    return max_ms or 0.0


def steady(session, warmup, keep_paused=False, keep_unfocused=False):
    """Summary rows after the warm-up, without windows that were mostly paused or in the background."""
    rows = session.summaries
    if not rows:
        return []
    start = rows[0]["utcMs"] - rows[0]["frameMs"] * rows[0]["frames"]
    kept = []
    for r in rows:
        if (r["utcMs"] - start) / 1000.0 < warmup:
            continue
        if not keep_paused and r["paused"] > 0.5 * r["frames"]:
            continue
        if not keep_unfocused and r["unfocused"] > 0.5 * r["frames"]:
            continue
        kept.append(r)
    return kept


def fmt_ms(v):
    return "%.0f" % v if abs(v) >= 100 else "%.1f" % v if abs(v) >= 10 else "%.2f" % v


def pct(part, whole):
    return "%.0f%%" % (100.0 * part / whole) if whole else "-"


def short(name, width=60):
    if len(name) <= width:
        return name
    tail = name.rsplit(".", 1)[-1]
    return tail if len(tail) <= width else tail[:width]


# ---------------------------------------------------------------- the profile

class KeyTotal:
    def __init__(self, kind, name, mod, assembly):
        self.kind, self.name, self.mod, self.assembly = kind, name, mod, assembly
        self.ms = self.kb = self.calls = self.sampled = self.max_ms = 0.0
        self.untimed = 0.0   # calls in rows with sampled 0 (a watched method nobody timed in that window): not in calls, so ms/calls stays honest


def entity_kind(name):
    """The kind of entity an entity row's name stands for: the text before its first space or '(', trimmed, or the whole name if that leaves
    nothing. Up to 0.1.3 the mod keyed a character loaded from a save by its own name ('BeaverAdult Malak') and one born during play by
    'BeaverAdult(Clone)'; it now applies this same rule itself (Profile.EntityKindOf), so recordings old and new line up. Change both together."""
    trimmed = name.strip()
    cut = re.search(r"[ (]", trimmed)
    kind = trimmed[:cut.start()].strip() if cut else trimmed
    return kind or name


def profile_totals(session, tick_from=0, tick_to=None):
    """Totals per (kind, name) over the profile windows that end after tick_from (load rows, window 0, are separate). Entity rows are added up
    by kind (entity_kind)."""
    totals = collections.OrderedDict()
    for r in session.profile:
        if r.get("window", 0) == 0:
            continue
        if r["tick"] < tick_from or (tick_to is not None and r["tick"] > tick_to):
            continue
        name = r.get("name", "")
        if r["kind"] == "entity":
            name = entity_kind(name)
        key = (r["kind"], name)
        t = totals.get(key)
        if t is None:
            t = totals[key] = KeyTotal(r["kind"], name, r.get("mod", ""), r.get("assembly", ""))
        if r["sampled"] <= 0 and r["calls"] > 0:
            t.untimed += r["calls"]
            continue
        t.ms += r["ms"]; t.kb += r["allocKB"]; t.calls += r["calls"]; t.sampled += r["sampled"]; t.max_ms = max(t.max_ms, r["maxMs"])
    return totals


def profile_window_seconds(session, rows, tick_from=0):
    """Seconds of play the profile windows after tick_from cover, from the summary rows in the same span of ticks."""
    profile_ticks = [r["tick"] for r in session.profile if r.get("window", 0) > 0 and r["tick"] >= tick_from]
    if not profile_ticks:
        return 0.0
    last = max(profile_ticks)
    return seconds_of([r for r in session.summaries if r["tick"] >= tick_from and r["tick"] <= last]) or seconds_of(rows)


def mod_of(session, t):
    if not t.mod and game_assembly(t.assembly):
        return "game"
    return t.mod or ("(unknown)" if t.kind not in ("entity",) else "")


# ---------------------------------------------------------------- other mods' patches (the '# patch|' header lines)

# The Harmony id this mod patches under; its own patches are how it measures, not something to point at.
OWN_OWNER = "kyler.performancelog"
# The method a singleton row of each kind times: the wrapper calls it, so another mod's patch on it runs inside that row's time.
SINGLETON_METHOD = {"tick-singleton": "Tick", "update-singleton": "UpdateSingleton", "late-singleton": "LateUpdateSingleton",
                    "parallel-start": "StartParallelTick"}


def patch_map(session):
    """The '# patch|tag|method|kind|owner|...' header lines as method -> [(tag, kind, owner)], in the order the header lists them. The header
    lists every method another mod patches, and every hot one (one that runs every tick or frame), up to a limit (patches-truncated)."""
    out = collections.OrderedDict()
    for p in session.pipe("patch"):
        if len(p) >= 4 and p[1]:
            out.setdefault(p[1], []).append((p[0], p[2], p[3]))
    return out


def other_patchers(entries):
    """The owners of patches that are not this mod's, each with its kinds of patch, in the order listed."""
    owners = collections.OrderedDict()
    for _, kind, owner in entries:
        if owner and not owner.startswith(OWN_OWNER):
            kinds = owners.setdefault(owner, [])
            if kind not in kinds:
                kinds.append(kind)
    return owners


def singleton_patchers(patches, t):
    """Other mods whose patches run inside a singleton row's time: the ones patching the method that row's kind times."""
    method = SINGLETON_METHOD.get(t.kind)
    return other_patchers(patches.get(t.name + "." + method, [])) if method else {}


def short_method(name, width=58):
    """A method's full name, cut to its class and method when it is long."""
    if len(name) <= width:
        return name
    tail = ".".join(name.split(".")[-2:])
    return tail if len(tail) <= width else tail[-width:]


def hot_patches(p, session, hot, args):
    """Prints the hot methods (run every tick or frame) that other mods patch, with who patches them and how. Nothing when there are none, and
    a note instead when the recording could not list the patches (patches-unavailable), so an empty list is not read as 'nothing is patched'."""
    if session.h("patches-unavailable"):
        p("   (the patches were not recorded, so this report cannot say which other mods patch what: %s)" % session.h("patches-unavailable"))
    if not hot:
        return
    p("   hot methods other mods patch (the patches run inside whatever part of the frame calls the method):")
    shown = hot if args.all else hot[:args.top]
    for method, owners in shown:
        p("     %-58s %s" % (short_method(method), "; ".join("%s (%s)" % (owner, ", ".join(kinds)) for owner, kinds in owners.items())))
    if len(shown) < len(hot):
        p("     ... and %d more (--all lists them)" % (len(hot) - len(shown)))
    if session.h("patches-truncated"):
        p("     (the header lists only part of the patches: %s)" % session.h("patches-truncated"))


def patch_set(session):
    """Every other mod's patch in the header as (method, kind, owner). The tag is left out: it changes when another mod starts patching the same
    method. This mod's own patches are left out as in other_patchers: they follow its Profile setting and version, which compare lists apart."""
    return {(method, kind, owner) for method, entries in patch_map(session).items() for _, kind, owner in entries
            if owner and not owner.startswith(OWN_OWNER)}


# ---------------------------------------------------------------- findings

class Finding:
    def __init__(self, severity, title, evidence, check=None, key=None):
        self.severity, self.title, self.evidence, self.check, self.key = severity, title, evidence, check, key


def rhythm(rows, gap_frames=5):
    """Looks for regular spacing in slow frames. Returns None or a dict describing it."""
    ordered = sorted(rows, key=lambda r: r["frame"])
    groups = []
    for r in ordered:
        if groups and r["frame"] - groups[-1][-1]["frame"] <= gap_frames:
            groups[-1].append(r)
        else:
            groups.append([r])
    if len(groups) < 5:   # four events can be evenly spaced by chance
        return None
    starts = [g[0]["utcMs"] / 1000.0 for g in groups]
    gaps = [b - a for a, b in zip(starts, starts[1:])]
    median = statistics.median(gaps)
    if median <= 0:
        return None
    near = sum(1 for g in gaps if abs(g - median) <= 0.2 * median) / len(gaps)
    return {"episodes": len(groups), "period_s": median, "share": near, "regular": near >= 0.7}


def explain_period(period, known):
    for seconds, name in known:
        if seconds > 0 and abs(period - seconds) <= 0.15 * seconds:
            return name
    return None


def vsync_interval(session):
    """The time between two vertical syncs in milliseconds, if vertical sync is on and the refresh rate was recorded."""
    display = session.h("display")
    count, hz = re.search(r"vSyncCount=(\d+)", display), re.search(r"refreshHz=([\d.]+)", display)
    if count and hz and int(count.group(1)) > 0 and float(hz.group(1)) > 0:
        return 1000.0 / float(hz.group(1)) * int(count.group(1))
    return None


def gc_advice(session, incremental_off):
    """What the recorded garbage collector settings say about long collection pauses."""
    if not incremental_off:
        return "The collector is incremental (%s), so long pauses are not from a stop-the-world collection; look at allocation. " % session.h("gc")
    slice_lines = [b[0] for b in session.pipe("bootconfig") if b and b[0].lower().startswith("gc-max-time-slice")]
    if slice_lines:
        return ("The collector is not incremental (%s) although boot.config has '%s', so that setting is not taking effect in this session "
                "(check it is in the game folder's Timberborn_Data/boot.config and that Steam has not restored the file). " % (session.h("gc"), slice_lines[0]))
    return ("The collector is not incremental (%s) and boot.config has no gc-max-time-slice line. Incremental collection turns one long pause into many short ones; "
            "it is set in boot.config before the game starts, not by a mod. " % session.h("gc"))


def findings_for(session, args):
    """The pointers: what stands out, with the evidence and what to check next. Ordered by how much of the time they explain."""
    out = []
    S = steady(session, args.warmup)
    allS = session.summaries
    if not allS:
        return [Finding("info", "No summary rows were written", "The session was shorter than one summary window (%.0f s), or the file is empty." % session.num("summarySeconds", 10))]
    if not S:
        out.append(Finding("warn", "Nothing is left after the warm-up and the paused and background windows",
                           "The session is %.0f s long and --warmup is %.0f s; pass --warmup 0 to look at all of it." % (seconds_of(allS), args.warmup)))
        S = allS
    frames = sum(r["frames"] for r in S)
    mean = wmean(S, "frameMs")
    secs = seconds_of(S)
    ticks = sum(r["ticks"] for r in S)
    if mean <= 0 or secs <= 0:
        return [Finding("warn", "The frame times in this file add up to zero", "The file is empty or damaged, so there is nothing to find.")]

    # --- can the numbers be trusted?
    unfocused = sum(r["unfocused"] for r in allS) / max(1, sum(r["frames"] for r in allS))
    if unfocused > 0.2:
        out.append(Finding("warn", "The game window was in the background for %.0f%% of the session" % (100 * unfocused),
                           "The system throttles a background window, so those frames say little about the game.", "Record again with the game in front."))
    overhead = (wmean(S, "overheadUs") + wmean(S, "probeUs")) / 1000.0
    if mean > 0 and overhead / mean > 0.02:
        out.append(Finding("warn", "Measuring itself cost %.1f%% of a frame" % (100 * overhead / mean),
                           "overheadUs + probeUs = %.0f us per frame against a %.1f ms frame." % (overhead * 1000, mean),
                           "Treat small differences with care; try Profile = standard or a lower OverheadBudgetPercent."))
    have_version = version_tuple(session.h("mod"))
    for p in session.pipe("capability-final"):
        if len(p) >= 3 and p[0] == "patchCalls" and p[-1] == "never ran":
            # 0.1.0 cleared this counter when the session started, in the middle of loading, so it said the loading patch never ran when it had.
            if p[1] == "SingletonLifecycleService.LoadAll" and have_version is not None and have_version < (0, 1, 1):
                continue
            out.append(Finding("warn", "The patch on %s never ran" % p[1], "The columns it feeds are 0 in this session; do not read those zeros as measurements.",
                               "Check the capability lines in the frames.csv header and Player.log for why the patch could not be made."))
    if session.problems:
        for p in session.problems[:3]:
            out.append(Finding("warn", "File problem", p))
    if session.skipped:
        out.append(Finding("info", "%d unreadable row(s) skipped" % session.skipped, "The file was cut short (a crash, or it was copied while the game ran)."))

    # --- slow frames and what they contained
    slow = [r for r in session.slow if not r["unfocused"] and not r["paused"]]
    slow_ms = sum(r["frameMs"] for r in slow)
    all_ms = sum(r["frameMs"] * r["frames"] for r in allS)
    if slow:
        gc_rows = [r for r in slow if r["gcDelta"] > 0 and not r["saving"]]
        save_rows = [r for r in slow if r["saving"]]
        if len(gc_rows) >= max(3, 0.5 * len(slow)):
            gm = statistics.median([r["frameMs"] for r in gc_rows])
            inc = "incremental=False" in session.h("gc")
            out.append(Finding("high", "Garbage collection causes most of the hitches",
                               "%d of %d slow frames contain a collection (median %.0f ms, worst %.0f ms); the game collects %.1f times a minute and allocates about %.0f KB per second." %
                               (len(gc_rows), len(slow), gm, max(r["frameMs"] for r in gc_rows), total(S, "gcDelta") / (secs / 60) if secs else 0,
                                alloc_rate(session, S)),
                               gc_advice(session, inc) + "Find what allocates most: the allocation table in the report and allocKB in profile.csv.", key="gc"))
        if save_rows:
            ev = [e for e in session.events if e["kind"] == "save"]
            detail = "; ".join("%.0f ms" % e["ms"] for e in ev[:5])
            out.append(Finding("high" if max(r["frameMs"] for r in save_rows) > 300 else "info", "Saving the game stalls the frame",
                               "%d slow frame(s) contain a save (longest %.0f ms). Saves in events.csv: %s." % (len(save_rows), max(r["frameMs"] for r in save_rows), detail or "none recorded"),
                               "The stages of each save are in events.csv (finishing the tick, snapshot, world JSON, thumbnail). A large snapshot or JSON stage grows with the colony.", key="save"))
        # who is named in the spikes
        blamed = collections.defaultdict(lambda: [0, 0.0, "", 0.0])
        slow_frames = {int(r["frame"]) for r in slow}
        for sp in session.spikes:
            if int(sp["frame"]) not in slow_frames:
                continue
            if sp["rank"] == 1 and sp["share"] >= 40:
                b = blamed[sp.get("name", "?")]
                b[0] += 1; b[1] += sp["ms"]; b[2] = sp.get("mod", ""); b[3] = max(b[3], sp["ms"])
        for name, (count, ms, mod, worst) in sorted(blamed.items(), key=lambda kv: -kv[1][1])[:3]:
            if count >= 2 or ms >= 80:
                out.append(Finding("high", "%s takes most of %d slow frame(s)" % (short(name), count),
                                   "In each it is the biggest contributor and holds at least 40%% of the frame: %.0f ms in all, %.0f ms at worst%s." %
                                   (ms, worst, " (mod %s)" % mod if mod and mod != "game" else " (the game itself)" if mod == "game" else ""),
                                   "Watch it with a Watch entry to see how often and how long, or look at what it does when it is slow.", key=name))
        if len(slow) >= 5:
            r = rhythm(slow)
            if r and r["regular"]:
                known = [(60.0, "an autosave interval of a minute")]
                saves = [e["utcMs"] / 1000.0 for e in session.events if e["kind"] == "save"]
                if len(saves) >= 3:
                    known.append((statistics.median([b - a for a, b in zip(saves, saves[1:])]), "the game's autosave"))
                gcs = total(S, "gcDelta")
                if gcs and secs:
                    known.append((secs / gcs, "garbage collection"))
                what = explain_period(r["period_s"], known)
                out.append(Finding("info", "Slow frames come every %.0f s" % r["period_s"],
                                   "%d episodes, %.0f%% within 20%% of that spacing. %s" % (r["episodes"], 100 * r["share"], ("That matches " + what + ".") if what else "It matches none of the periodic things this tool knows about."),
                                   "Find what runs on that timer (a mod's periodic work shows in spikes.csv)."))

    # --- where an average frame goes
    if mean > 0:
        shares = {s: wmean(S, s) / mean for s in SLOTS + ["otherMs"]}
        other = shares["otherMs"]
        plpost = wmean(S, "plPost") / mean if "plPost" in session.columns else 0
        busy = wmean(S, "mainCpuMs") / mean if wmean(S, "mainCpuMs") > 0 else None
        interval = vsync_interval(session)
        if interval and mean <= 1.15 * interval and other >= 0.3:
            out.append(Finding("info", "The game is keeping up with the display: frames sit at its vertical-sync rate (%.0f fps)" % (1000 / interval),
                               "The mean frame is %.1f ms against a %.1f ms sync interval; the rest of each frame (%.0f%% of it, mostly Unity's post-late-update phase) is the wait for the next sync." % (mean, interval, 100 * other),
                               "That wait is free: the computer had time to spare. Look for slowness in the slow frames and in the simulation instead."))
        elif other >= 0.5 and mean >= SLOW_MEAN_MS:
            evidence = "%.0f%% of an average frame is outside every part this mod times" % (100 * other)
            # Which part holds it decides where to look. The Update and LateUpdate remainders are code that runs every frame; plPost is drawing and
            # waiting, and Unity can also wait for the last frame to be presented in its first phase (plTime), so the phases before Update and
            # what falls between phases point at the graphics card or vertical sync as plPost does.
            split = other_by_phase(S) if phases_measured(session, S) else None
            biggest = max(("update", "late", "post", "phases", "between"), key=lambda k: split[k]) if split else "post"
            if biggest in ("update", "late"):
                evidence += ("; per frame, %.1f ms of it is in Unity's Update phase outside the timed parts, %.1f ms in the LateUpdate phase outside lateMs "
                             "and %.1f ms in plPost (drawing and the wait for vertical sync)" % (split["update"], split["late"], split["post"]))
                # A game thread that is mostly idle is waiting inside that phase (on another thread, the disk or the graphics card), not working.
                idle = busy is not None and busy < 0.7
                if idle:
                    evidence += "; the game thread was busy for only %.0f%% of the frame" % (100 * busy)
                if biggest == "update":
                    title = "Most of the frame is other work in Unity's Update phase, outside every part this mod times"
                    check = ("That is code that runs every frame beside the game's tick loop and singletons: the game's own and other mods' scripts "
                             "(MonoBehaviour Update, coroutines). " +
                             ("The game thread is idle for much of it, so a script there is waiting: on another thread, the disk or the graphics card. " if idle
                              else "The graphics card is not what holds the frame. ") +
                             "Compare a recording without a suspected mod, or time a suspect method with a Watch entry.")
                else:
                    title = "Most of the frame is other work in Unity's LateUpdate phase, outside every part this mod times"
                    check = ("Unity's animation and user interface (UI Toolkit) run in this phase beside scripts' LateUpdate, and grow with what is on screen. "
                             "Compare a recording with fewer animated characters in view or no panel open, and one without a suspected mod.")
                out.append(Finding("high" if split[biggest] / mean >= 0.4 else "info", title, evidence + ".", check, key="other-" + biggest))
            else:
                if split and biggest == "phases":
                    evidence += ("; %.0f%% of it is in Unity's phases before Update (plTime to plPre), where Unity also waits for the last frame to be presented"
                                 % (100 * split["phases"] / mean))
                if plpost >= 0.4:
                    evidence += "; %.0f%% of it is Unity's post-late-update phase (drawing, presenting, the wait for vertical sync)" % (100 * plpost)
                if busy is not None:
                    evidence += "; the game thread was busy for only %.0f%% of the frame" % (100 * busy)
                display = session.h("display")
                out.append(Finding("high" if plpost >= 0.4 or (busy is not None and busy < 0.7) else "info", "Most of the frame is not the game's or any mod's code",
                                   evidence + ".", "Likely the graphics card or vertical sync (%s). Check draw calls (prDraw, prSetPass), the resolution and quality settings; a mod is unlikely to be the cause." % (display or "display settings not recorded"), key="gpu"))
        if shares["updMs"] >= 0.15 and mean >= SLOW_MEAN_MS * 0.8:
            out.append(Finding("high" if shares["updMs"] >= 0.3 else "info", "Per-frame singleton updates take %.0f%% of a frame (%.1f ms)" % (100 * shares["updMs"], wmean(S, "updMs")),
                               "These run every frame whatever the game speed: the user interface, the camera, input and many mods.",
                               "The 'Singletons updated once per frame' table names them, with their mod.", key="upd"))
        tick_ms = sum(wsum(S, s) for s in TICK_SLOTS)
        if ticks > 0:
            per_tick = tick_ms / ticks
            tick_share = sum(shares[s] for s in TICK_SLOTS)
            ticks_per_s = ticks / secs if secs else 0
            if tick_share >= 0.25 and mean >= SLOW_MEAN_MS * 0.8:
                parts = sorted(((wsum(S, s) / ticks, s) for s in TICK_SLOTS), reverse=True)[:3]
                out.append(Finding("high" if tick_share >= 0.4 else "info", "The simulation takes %.0f%% of a frame: %.1f ms per tick at %.1f ticks per second" % (100 * tick_share, per_tick, ticks_per_s),
                                   "Biggest parts per tick: " + ", ".join("%s %.1f ms" % (s, v) for v, s in parts) + ".",
                                   "Follow the part: singMs -> tick singletons, entMs -> entity kinds (and components with Profile = deep), parWaitMs -> the worker threads (see parTickMs).", key="tick"))
            wait = wsum(S, "parWaitMs")
            if tick_ms and wait / tick_ms >= 0.25 and wait / ticks >= 1.0:
                out.append(Finding("info", "The game thread waits %.1f ms per tick for the parallel tick (%.0f%% of tick time)" % (wait / ticks, 100 * wait / tick_ms),
                                   "The game reports its parallel tick at %.1f ms per tick." % (total(S, "parTickMs") / ticks),
                                   "The slow part is on the worker threads (pathfinding, water and so on), which this mod cannot look inside."))

    # --- memory
    if secs and total(S, "gcDelta") > 0:
        pass  # covered above when GC dominates the slow frames
    heaps = [r["heapMB"] for r in S if r["heapMB"] > 0]
    if len(heaps) >= 6:
        third = max(1, len(heaps) // 3)
        early, late = statistics.mean(heaps[:third]), statistics.mean(heaps[-third:])
        if late > 1.3 * early and late - early > 100:
            out.append(Finding("info", "The managed heap grew from %.0f to %.0f MB over the session" % (early, late),
                               "Averages of the first and last third of the session.", "Steady growth that a collection does not undo is a leak or a cache; compare colEntities to see whether it just follows the colony.", key="heap"))

    # --- getting slower
    if len(S) >= 9:
        third = len(S) // 3
        a, b = S[:third], S[-third:]
        ta, tb = sum(r["ticks"] for r in a), sum(r["ticks"] for r in b)
        if ta > 10 and tb > 10:
            pa = sum(wsum([r], s) for r in a for s in TICK_SLOTS) / ta
            pb = sum(wsum([r], s) for r in b for s in TICK_SLOTS) / tb
            ea, eb = wmean(a, "colEntities"), wmean(b, "colEntities")
            if pa > 0 and pb / pa >= 1.3 and pb - pa >= 1.0:
                grow = " while the colony grew from %.0f to %.0f entities" % (ea, eb) if eb > ea * 1.05 else " with the colony about the same size"
                out.append(Finding("high", "A tick got %.0f%% more expensive over the session (%.1f -> %.1f ms)" % (100 * (pb / pa - 1), pa, pb),
                                   "First third against last third of the steady windows%s." % grow,
                                   "Compare the two thirds' profile rows to see which singleton or entity kind grew (report --all, or compare two recordings from early and late).", key="grow"))

    # --- which mods
    totals = profile_totals(session, tick_from=S[0]["tick"] - 1 if S else 0)
    if totals and secs:
        mods = collections.defaultdict(float)
        everything = 0.0
        for t in totals.values():
            if t.kind in ("tick-singleton", "update-singleton", "late-singleton"):
                mods[mod_of(session, t)] += t.ms
                everything += t.ms
        window_secs = profile_window_seconds(session, S, S[0]["tick"] - 1 if S else 0) or secs
        for mod, ms in sorted(mods.items(), key=lambda kv: -kv[1]):
            if mod in ("game", "(unknown)", "") or window_secs <= 0:
                continue
            per_frame = ms / window_secs / 1000.0 * mean if mean else 0
            if ms / everything >= 0.10 or per_frame >= 0.5:
                out.append(Finding("info", "Mod %s: %.1f ms per second in singletons (%.0f%% of all singleton time)" % (mod, ms / window_secs, 100 * ms / everything),
                                   "Tick, update and late-update singletons of that mod together, over the steady windows.",
                                   "The per-singleton tables name them. Compare a recording without the mod to see what it adds."))
    loads = [r for r in session.profile if r.get("window", 0) == 0 and r["kind"] in LOAD_KINDS]
    if loads:
        loads.sort(key=lambda r: -r["ms"])
        load_total = sum(r["ms"] for r in loads)
        slowest = loads[0]
        if load_total > 3000 or slowest["ms"] > 1500:
            out.append(Finding("info", "Loading the game's singletons took %.1f s; the slowest step was %s (%.0f ms)" % (load_total / 1000, short(slowest.get("name", "?")), slowest["ms"]),
                               "Steps of kind %s are in profile.csv (window 0); the mod is %s." % (slowest["kind"], slowest.get("mod") or "unknown"), "Loading a save also includes work outside these steps (reading the file, creating entities)."))
        grew = sorted((r for r in loads if r.get("allocKB", 0) >= 1024), key=lambda r: -r["allocKB"])
        grew_total = sum(r.get("allocKB", 0) for r in loads) / 1024.0
        if grew_total >= 100:
            top = "; ".join("%s %.0f MB (%s)" % (short(r.get("name", "?"), 48), r["allocKB"] / 1024.0, r["kind"]) for r in grew[:4])
            out.append(Finding("info", "Loading grew the managed heap by %.0f MB" % grew_total,
                               "Biggest steps by heap growth: %s. (The heap size before and after each step; a collection in the middle makes a step read low.)" % top,
                               "That memory is still held after loading if the heap does not fall back at the first collection; compare `heapMB` after the first collections in frames.csv."))
    if not out:
        out.append(Finding("info", "Nothing stands out", "No slow-frame cause, part of the frame or mod crossed the thresholds this tool uses.",
                           "If the drops are real, record a longer session, or lower SlowFrameMs in PerformanceLog.cfg."))
    order = {"high": 0, "warn": 1, "info": 2}
    return sorted(out, key=lambda f: order.get(f.severity, 3))


# ---------------------------------------------------------------- report

def blame_text(frame_row, spikes):
    """What a slow frame is blamed on in section 5: the singletons that took a real part of it, or that none did and what else the frame had.
    Empty when spikes.csv has nothing for the frame (no singleton was timed in it)."""
    ranked = sorted(spikes, key=lambda x: x["rank"])
    if not ranked:
        return ""
    frame_ms = frame_row["frameMs"]

    def share(x):
        return x["share"] if "share" in x else (100.0 * x["ms"] / frame_ms if frame_ms else 0.0)
    named = []
    for x in ranked[:2]:
        if x["ms"] < BLAME_MIN_MS and share(x) < BLAME_MIN_SHARE:
            break  # biggest first, so nothing after it is bigger
        named.append(x)
    if named:
        return "  <- " + ", ".join("%s %.0f ms" % (short(x.get("name", "?"), 40), x["ms"]) for x in named)
    had = [what for what, on in (("a save", frame_row["saving"]), ("a garbage collection", frame_row["gcDelta"] > 0)) if on]
    return "  <- no singleton stood out (largest %.1f ms, %.1f%% of the frame)%s" % (ranked[0]["ms"], share(ranked[0]),
                                                                                 "; the frame had " + " and ".join(had) if had else "")


def report(session, args, out):
    p = lambda text="": out.write(text + "\n")
    S = steady(session, args.warmup)
    allS = session.summaries
    p("=" * 78)
    p("PERFORMANCE LOG REPORT: %s" % session.name)
    p("=" * 78)
    p("game %s, Unity %s, mod %s; %s" % (session.h("game", "?"), session.h("unity", "?"), session.h("mod", "?"), session.h("started", "?")))
    p("%s | %s | %s" % (session.h("cpu", "?"), session.h("gpu", "?"), session.h("os", "?")))
    if not session.ended:
        p("note: the file has no closing line (the game closed abnormally, or the file was copied while the game ran)")
    for note in known_issues(session):
        p("KNOWN ISSUE in Performance Log %s: %s" % (session.h("mod", "?"), note))
    p()
    if not allS:
        p("No summary rows: the session was shorter than one summary window, or is empty.")
        return 0

    frames = sum(r["frames"] for r in allS)
    secs = seconds_of(allS)
    if secs <= 0 or wmean(S or allS, "frameMs") <= 0:
        p("The frame times in this file add up to zero: it is empty or damaged, so there is nothing to report.")
        return 0
    p("1. THE SESSION")
    p("   %d frames over %.0f s (%.1f min), %d simulation ticks, %d slow frames (>= %s ms), %d mods enabled" % (
        frames, secs, secs / 60, sum(r["ticks"] for r in allS), len(session.slow), session.h("thresholdMs", "?"), len(session.mods)))
    if S and S != allS:
        p("   steady state = %d of %d windows (%.0f s): after a %.0f s warm-up, without windows that were mostly paused or in the background" % (len(S), len(allS), seconds_of(S), args.warmup))
    p()

    body = S or allS
    p("2. FRAME RATE (steady state)")
    mean = wmean(body, "frameMs")
    mx = max(r["maxFrameMs"] for r in body)
    p("   mean %.1f ms (%.0f fps); median ~%.1f ms, p90 ~%.1f ms, p99 ~%.1f ms, slowest %.0f ms (percentiles from the histogram: only as fine as its buckets)" % (
        mean, 1000 / mean if mean else 0, percentile(session, body, 0.5, mx), percentile(session, body, 0.9, mx), percentile(session, body, 0.99, mx), mx))
    speeds = collections.OrderedDict()
    for r in allS:
        speeds.setdefault(round(r["speed"]) if r["speed"] > 0 else 0, []).append(r)
    p("   by game speed (a window is labelled with the speed it ended at):")
    for speed, rows in sorted(speeds.items()):
        p("     %-8s %5.1f%% of the time, mean frame %.1f ms, %.1f ticks/s" % ("paused" if speed == 0 else "speed %d" % speed, 100 * seconds_of(rows) / secs,
                                                                            wmean(rows, "frameMs"), sum(r["ticks"] for r in rows) / max(1e-9, seconds_of(rows))))
    p()

    p("3. WHERE AN AVERAGE FRAME GOES (steady state)")
    for s in SLOTS + ["otherMs"]:
        v = wmean(body, s)
        extra = ""
        if s in TICK_SLOTS and sum(r["ticks"] for r in body):
            extra = "  %.2f ms per tick" % (wsum(body, s) / sum(r["ticks"] for r in body))
        p("   %-10s %6.2f ms %4s   %s%s" % (s, v, pct(v, mean), SLOT_MEANING[s], extra))
    ph = {x: wmean(body, x) for x in PHASES if x in session.columns}
    if ph and sum(ph.values()) > 0:
        p("   Unity's phases: " + ", ".join("%s %.2f ms" % (k, v) for k, v in ph.items() if v >= 0.05) + "   (the wait for vertical sync is in one of them, usually plPost)")
    if phases_measured(session, body):
        other = wmean(body, "otherMs")
        split = other_by_phase(body)
        p("   otherMs by Unity phase (each phase less the timed parts that run in it):")
        for key, label, meaning in OTHER_SPLIT:
            p("     %-38s %6.2f ms %4s   %s" % (label, split[key], pct(split[key], other), meaning))
    if wmean(body, "mainCpuMs") > 0:
        p("   the game thread was busy %.0f%% of the frame (%.1f of %.1f ms); the process used %.1f cores' worth" % (
            100 * wmean(body, "mainCpuMs") / mean, wmean(body, "mainCpuMs"), mean, wmean(body, "procCpuMs") / mean if mean else 0))
    p()

    p("4. WHAT THIS POINTS TO")
    for f in findings_for(session, args):
        tag = {"high": "[!]", "warn": "[?]", "info": "[ ]"}.get(f.severity, "[ ]")
        p("   %s %s" % (tag, f.title))
        p("       evidence: %s" % f.evidence)
        if f.check:
            p("       next: %s" % f.check)
    p()

    p("5. THE SLOWEST FRAMES")
    slow = sorted(session.slow, key=lambda r: -r["frameMs"])[:args.top]
    if not slow:
        p("   none reached the threshold")
    blame = collections.defaultdict(list)
    for sp in session.spikes:
        blame[int(sp["frame"])].append(sp)
    for r in slow:
        parts = sorted(((r[s], s) for s in SLOTS + ["otherMs"]), reverse=True)[:3]
        why = []
        if r["gcDelta"] > 0:
            why.append("GC")
        if r["saving"]:
            why.append("save")
        if r["unfocused"]:
            why.append("background")
        if r["paused"]:
            why.append("paused")
        p("   frame %-6d tick %-6d %6.0f ms  speed %g  %s%s%s" % (r["frame"], r["tick"], r["frameMs"], r["speed"],
                                                                "[" + ",".join(why) + "] " if why else "", ", ".join("%s %.0f" % (s, v) for v, s in parts),
                                                                blame_text(r, blame.get(int(r["frame"]), []))))
    p()

    first = S[0]["tick"] - 1 if S else 0
    totals = profile_totals(session, tick_from=first)
    window_secs = profile_window_seconds(session, body, first) or secs
    patches = patch_map(session)
    hot = [(method, owners) for method, owners in ((m, other_patchers(e)) for m, e in patches.items() if any(tag == "hot" for tag, _, _ in e)) if owners]
    hot.sort(key=lambda h: -len(h[1]))   # methods several mods patch first; otherwise in the header's order (by name)
    if totals and window_secs > 0:
        p("6. WHERE THE TIME GOES, BY SINGLETON, ENTITY KIND AND METHOD (steady state, %.0f s of profile windows)" % window_secs)
        p("   ms/s = milliseconds of game-thread time per second of play; 'calls' are exact for singletons and watched methods, estimated for sampled kinds.")
        if any(other_patchers(e) for e in patches.values()):
            p("   A singleton's time includes other mods' patches on the method it times; its row names them ('includes patches by').")
        for kind, title in KIND_TITLES.items():
            rows = sorted((t for t in totals.values() if t.kind == kind), key=lambda t: -t.ms)
            if not rows:
                continue
            all_ms = sum(t.ms for t in rows)
            p("   %s: %.1f ms/s in all" % (title, all_ms / window_secs))
            for t in rows[:(len(rows) if args.all else args.top)]:
                by = singleton_patchers(patches, t)
                p("     %-58s %-26s %8.2f ms/s %4s  %6.1f us/call  %7.1f KB/s  slowest %.2f ms%s%s" % (
                    short(t.name, 58), (mod_of(session, t) or "")[:26], t.ms / window_secs, pct(t.ms, all_ms), t.ms * 1000 / t.calls if t.calls else 0, t.kb / window_secs, t.max_ms,
                    "  (+%d calls never timed)" % t.untimed if t.untimed else "",
                    "  (includes patches by %s)" % ", ".join(by) if by else ""))
        mods = collections.defaultdict(lambda: [0.0, 0.0])
        for t in totals.values():
            if t.kind in ("tick-singleton", "update-singleton", "late-singleton"):
                m = mods[mod_of(session, t)]
                m[0] += t.ms; m[1] += t.kb
        if mods:
            p("   singleton time by mod (tick + update + late; 'game' is Timberborn):")
            for mod, (ms, kb) in sorted(mods.items(), key=lambda kv: -kv[1][0])[:args.top]:
                p("     %-34s %8.2f ms/s  %8.1f KB/s" % (mod, ms / window_secs, kb / window_secs))
        comp_mods = collections.defaultdict(lambda: [0.0, 0.0])
        for t in totals.values():
            if t.kind == "component":
                m = comp_mods[mod_of(session, t)]
                m[0] += t.ms; m[1] += t.kb
        if comp_mods:
            p("   entity component time by mod (sampled; these tick inside entMs, so do not add them to the singletons above):")
            for mod, (ms, kb) in sorted(comp_mods.items(), key=lambda kv: -kv[1][0])[:args.top]:
                p("     %-34s %8.2f ms/s  %8.1f KB/s" % (mod, ms / window_secs, kb / window_secs))
        hot_patches(p, session, hot, args)
        p()
    elif hot or session.h("patches-unavailable"):
        p("6. HOT METHODS OTHER MODS PATCH (this session has no profile)")
        hot_patches(p, session, hot, args)
        p()
    watched = [t for t in totals.values() if t.kind == "method"]
    if not watched and session.pipe("watch"):
        p("   (Watch entries were configured, but none produced rows: %s)" % "; ".join("|".join(w) for w in session.pipe("watch")[:3]))
        p()

    p("7. GARBAGE COLLECTION AND MEMORY")
    gc = total(body, "gcDelta")
    p("   %d collections (%.1f per minute); the game allocated about %.0f KB per second (%.0f KB per tick); managed heap %.0f-%.0f MB" % (
        gc, gc / (seconds_of(body) / 60) if seconds_of(body) else 0, alloc_rate(session, body),
        total(body, "allocKB") / max(1, sum(r["ticks"] for r in body)), min(r["heapMB"] for r in body if r["heapMB"] > 0) if any(r["heapMB"] > 0 for r in body) else 0,
        max(r["heapMB"] for r in body)))
    note = alloc_unmeasured_note(session, body)
    if note:
        p("   " + note)
    ticks = max(1, sum(r["ticks"] for r in body))
    alloc = {}
    for s in SLOTS:
        col = s.replace("Ms", "KB")
        if col in session.columns:
            alloc[col] = total(body, col) / ticks
    if "otherKB" in session.columns:
        alloc["otherKB"] = total(body, "otherKB") / ticks
    top = [(v, k) for k, v in alloc.items() if v >= 0.5]
    if top:
        p("   allocation per tick by part (%s): %s" % (session.pipe("capability", "allocSource")[0][1] if session.pipe("capability", "allocSource") else "?",
                                                      ", ".join("%s %.0f KB" % (k, v) for v, k in sorted(top, reverse=True)[:6])))
    p("   collector: %s" % (session.h("gc") or "not recorded"))
    boot = [b[0] for b in session.pipe("bootconfig") if b and "gc" in b[0].lower()]
    if boot:
        p("   boot.config: %s" % "; ".join(boot))
    p()

    p("8. WHAT EACH SOURCE COULD MEASURE")
    for k in ("capability", "capability-final"):
        for parts in session.pipe(k):
            if len(parts) >= 2 and not (parts[0] == "patchCalls" and parts[-1] != "never ran" and not args.all):
                p("   %s: %s" % (k, " | ".join(parts)))
    p()
    if session.pipe("calibration"):
        p("   what measuring costs: %s" % " | ".join(session.pipe("calibration")[0]))
    return 0


def report_json(session, args):
    S = steady(session, args.warmup) or session.summaries
    mean = wmean(S, "frameMs")
    return {
        "session": session.name, "game": session.h("game"), "mod": session.h("mod"), "mods": {k: v[1] for k, v in session.mods.items()},
        "frames": sum(r["frames"] for r in session.summaries), "seconds": seconds_of(session.summaries), "steadyWindows": len(S),
        "meanFrameMs": mean, "p50": percentile(session, S, 0.5), "p90": percentile(session, S, 0.9), "p99": percentile(session, S, 0.99),
        "slowFrames": len(session.slow), "knownIssues": known_issues(session),
        "slotsMsPerFrame": {s: wmean(S, s) for s in SLOTS + ["otherMs"]},
        "otherMsByPhase": dict(other_by_phase(S)) if phases_measured(session, S) else None,
        "collections": total(S, "gcDelta"),
        "findings": [{"severity": f.severity, "title": f.title, "evidence": f.evidence, "check": f.check} for f in findings_for(session, args)],
    }


# ---------------------------------------------------------------- compare

def env_differences(a, b):
    lines = []
    ma, mb = a.mods, b.mods
    for mod_id in sorted(set(ma) - set(mb)):
        lines.append("only %s has mod %s (%s %s)" % (a.label, mod_id, ma[mod_id][0], ma[mod_id][1]))
    for mod_id in sorted(set(mb) - set(ma)):
        lines.append("only %s has mod %s (%s %s)" % (b.label, mod_id, mb[mod_id][0], mb[mod_id][1]))
    for mod_id in sorted(set(ma) & set(mb)):
        if ma[mod_id][1] != mb[mod_id][1]:
            lines.append("mod %s is %s in %s but %s in %s" % (mod_id, ma[mod_id][1], a.label, mb[mod_id][1], b.label))
    for key in ("game", "unity", "os", "cpu", "gpu", "graphics", "display", "quality", "gc", "memoryMB", "profile", "thresholdMs", "config"):
        va, vb = a.h(key, None), b.h(key, None)
        if va != vb and (va is not None or vb is not None):
            lines.append("%s differs: %s | %s" % (key, va, vb))
    ba, bb = {x[0] for x in a.pipe("bootconfig")}, {x[0] for x in b.pipe("bootconfig")}
    if ba != bb:
        lines.append("boot.config differs: only in %s: %s; only in %s: %s" % (a.label, sorted(ba - bb), b.label, sorted(bb - ba)))
    unrecorded = [s.label for s in (a, b) if s.h("patches-unavailable")]
    if unrecorded:
        lines.append("the patches by other mods were not recorded in %s, so they cannot be compared" % " or ".join(unrecorded))
    pa, pb = (patch_set(a), patch_set(b)) if not unrecorded else (set(), set())
    for session, only in ((a, pa - pb), (b, pb - pa)):
        if not only:
            continue
        owners = collections.Counter(owner for _, _, owner in only)
        examples = sorted(only)[:3]
        lines.append("only %s has %d patch%s (%s), e.g. %s%s" % (
            session.label, len(only), "" if len(only) == 1 else "es", ", ".join("%s %d" % kv for kv in sorted(owners.items(), key=lambda kv: (-kv[1], kv[0]))[:4]),
            "; ".join("%s %s by %s" % (short_method(m), kind, owner) for m, kind, owner in examples),
            " (the header of one lists only part of its patches)" if a.h("patches-truncated") or b.h("patches-truncated") else ""))
    return lines


def workload(session, S):
    secs = seconds_of(S)
    return {
        "seconds": secs,
        "speed": wmean(S, "speed"),
        "entities": wmean(S, "colEntities"), "beavers": wmean(S, "colBeavers"),
        "tickShare": sum(wsum(S, s) for s in TICK_SLOTS) / max(1e-9, wmean(S, "frameMs") * sum(r["frames"] for r in S)),
    }


def compare(a, b, args, out):
    p = lambda text="": out.write(text + "\n")
    Sa, Sb = steady(a, args.warmup) or a.summaries, steady(b, args.warmup) or b.summaries
    p("=" * 78)
    p("PERFORMANCE LOG COMPARISON")
    p("=" * 78)
    p("A: %s   (%s, %d mods)" % (a.name, a.h("started", "?"), len(a.mods)))
    p("B: %s   (%s, %d mods)" % (b.name, b.h("started", "?"), len(b.mods)))
    p()
    p("1. ARE THE TWO SESSIONS COMPARABLE?")
    diffs = env_differences(a, b)
    if diffs:
        p("   What differs between the two (each of these could explain a difference in the numbers on its own):")
        for d in diffs:
            p("   - " + d)
    else:
        p("   Same mods, game, computer and settings.")
    wa, wb = workload(a, Sa), workload(b, Sb)
    cautions = []
    if abs(wa["speed"] - wb["speed"]) > 0.5:
        cautions.append("the average game speed differs (%.1f vs %.1f): frame times are only comparable at the same speed (see the per-speed table)" % (wa["speed"], wb["speed"]))
    if wa["entities"] and wb["entities"] and abs(wa["entities"] - wb["entities"]) > 0.1 * max(wa["entities"], wb["entities"]):
        cautions.append("the colony size differs (%.0f vs %.0f entities): per-tick cost grows with it" % (wa["entities"], wb["entities"]))
    if min(wa["seconds"], wb["seconds"]) < 60:
        cautions.append("one session has less than a minute of steady play (%.0f s and %.0f s): a short sample is noisy" % (wa["seconds"], wb["seconds"]))
    if max(wa["seconds"], wb["seconds"]) > 2 * max(1.0, min(wa["seconds"], wb["seconds"])):
        cautions.append("the sessions differ a lot in length (%.0f s and %.0f s)" % (wa["seconds"], wb["seconds"]))
    for s, label in ((a, "A"), (b, "B")):
        unfocused = sum(r["unfocused"] for r in s.summaries) / max(1, sum(r["frames"] for r in s.summaries))
        if unfocused > 0.1:
            cautions.append("%s was in the background for %.0f%% of its frames" % (label, 100 * unfocused))
    for c in cautions:
        p("   caution: " + c)
    if not cautions and not diffs:
        p("   Nothing else differs that this tool can see.")
    p("   (Run-to-run noise in a game is typically a few percent; a rule of thumb, not a measurement: treat differences under about 5% as noise unless they repeat.)")
    p()

    p("2. FRAME TIME (steady state)")
    ma, mb = wmean(Sa, "frameMs"), wmean(Sb, "frameMs")
    p("   %-26s %10s %10s %10s" % ("", "A", "B", "B vs A"))
    rows = [("mean frame ms", ma, mb),
            ("game-thread work ms/frame", sum(wmean(Sa, x) for x in SLOTS), sum(wmean(Sb, x) for x in SLOTS))]
    for label, q in (("median ms", .5), ("p90 ms", .9), ("p99 ms", .99)):
        rows.append((label, percentile(a, Sa, q, max(r["maxFrameMs"] for r in Sa)), percentile(b, Sb, q, max(r["maxFrameMs"] for r in Sb))))
    rows.append(("slow frames per minute", len(a.slow) / (seconds_of(a.summaries) / 60) if seconds_of(a.summaries) else 0, len(b.slow) / (seconds_of(b.summaries) / 60) if seconds_of(b.summaries) else 0))
    ta, tb = sum(r["ticks"] for r in Sa), sum(r["ticks"] for r in Sb)
    if ta and tb:
        rows.append(("ms per tick (game thread)", sum(wsum(Sa, s) for s in TICK_SLOTS) / ta, sum(wsum(Sb, s) for s in TICK_SLOTS) / tb))
    rows.append(("collections per minute", total(Sa, "gcDelta") / (seconds_of(Sa) / 60) if seconds_of(Sa) else 0, total(Sb, "gcDelta") / (seconds_of(Sb) / 60) if seconds_of(Sb) else 0))
    rows.append(("allocation KB per second", alloc_rate(a, Sa), alloc_rate(b, Sb)))
    for label, va, vb in rows:
        p("   %-26s %10.2f %10.2f %10s" % (label, va, vb, change(va, vb)))
    speeds_a = collections.defaultdict(list)
    speeds_b = collections.defaultdict(list)
    for r in Sa:
        speeds_a[round(r["speed"])].append(r)
    for r in Sb:
        speeds_b[round(r["speed"])].append(r)
    common = [s for s in sorted(set(speeds_a) & set(speeds_b)) if seconds_of(speeds_a[s]) >= 20 and seconds_of(speeds_b[s]) >= 20]
    if common:
        p("   at the same game speed (windows of at least 20 s in both):")
        for s in common:
            va, vb = wmean(speeds_a[s], "frameMs"), wmean(speeds_b[s], "frameMs")
            p("     speed %d: mean frame %.2f ms -> %.2f ms (%s)" % (s, va, vb, change(va, vb)))
    p()

    p("3. WHERE AN AVERAGE FRAME GOES")
    p("   %-10s %9s %9s %10s   per tick: %8s %8s" % ("", "A ms", "B ms", "B vs A", "A", "B"))
    for s in SLOTS + ["otherMs"]:
        va, vb = wmean(Sa, s), wmean(Sb, s)
        pt = ""
        if s in TICK_SLOTS and ta and tb:
            pt = "           %8.2f %8.2f" % (wsum(Sa, s) / ta, wsum(Sb, s) / tb)
        p("   %-10s %9.2f %9.2f %10s%s" % (s, va, vb, change(va, vb), pt))
    p()

    key_movers = []
    fa, fb = (Sa[0]["tick"] - 1 if Sa else 0), (Sb[0]["tick"] - 1 if Sb else 0)
    pa, pb = profile_totals(a, tick_from=fa), profile_totals(b, tick_from=fb)
    sa, sb = profile_window_seconds(a, Sa, fa) or seconds_of(Sa), profile_window_seconds(b, Sb, fb) or seconds_of(Sb)
    if pa and pb and sa > 0 and sb > 0:
        p("4. WHICH SINGLETONS, ENTITY KINDS AND METHODS CHANGED (ms per second of play)")
        keys = set(pa) | set(pb)
        movers = []
        for k in keys:
            va = pa[k].ms / sa if k in pa else 0.0
            vb = pb[k].ms / sb if k in pb else 0.0
            t = pa.get(k) or pb.get(k)
            movers.append((vb - va, k, va, vb, t))
        movers.sort(key=lambda m: -abs(m[0]))
        key_movers = movers
        p("   %-58s %-22s %9s %9s %9s" % ("", "mod", "A ms/s", "B ms/s", "change"))
        for delta, k, va, vb, t in movers[:args.top]:
            note = "  (only in B)" if k not in pa else "  (only in A)" if k not in pb else ""
            p("   %-58s %-22s %9.2f %9.2f %+9.2f%s" % ("[%s] %s" % (k[0].split("-")[0], short(k[1], 52)), (mod_of(a, t) or "")[:22], va, vb, delta, note))
        mods = collections.defaultdict(lambda: [0.0, 0.0])
        for k, t in pa.items():
            if k[0] in ("tick-singleton", "update-singleton", "late-singleton"):
                mods[mod_of(a, t)][0] += t.ms / sa
        for k, t in pb.items():
            if k[0] in ("tick-singleton", "update-singleton", "late-singleton"):
                mods[mod_of(b, t)][1] += t.ms / sb
        p("   singleton time by mod (ms/s):")
        for mod, (va, vb) in sorted(mods.items(), key=lambda kv: -abs(kv[1][1] - kv[1][0]))[:args.top]:
            p("     %-34s %9.2f %9.2f %+9.2f" % (mod, va, vb, vb - va))
        p()

    p("5. WHAT THIS POINTS TO")
    for line in compare_pointers(a, b, Sa, Sb, diffs, cautions, rows, key_movers):
        p("   - " + line)
    p()
    return 0


def change(va, vb):
    if va == 0 and vb == 0:
        return "-"
    if va == 0:
        return "new"
    return "%+.1f%%" % (100.0 * (vb - va) / va)


def compare_pointers(a, b, Sa, Sb, diffs, cautions, rows, movers=()):
    lines = []
    lookup = {r[0]: (r[1], r[2]) for r in rows}
    va, vb = lookup["mean frame ms"]
    wa_, wb_ = lookup["game-thread work ms/frame"]
    frame_same = va > 0 and abs(vb - va) / va < 0.05
    if va > 0:
        rel = (vb - va) / va
        if frame_same:
            lines.append("The mean frame time is within %.1f%% (%.2f ms and %.2f ms): inside the noise this tool assumes." % (100 * abs(rel), va, vb))
        else:
            lines.append("B's mean frame time is %.1f%% %s than A's (%.2f ms against %.2f ms)." % (100 * abs(rel), "lower" if rel < 0 else "higher", vb, va))
    if wa_ > 0 and abs(wb_ - wa_) / wa_ >= 0.05 and abs(wb_ - wa_) >= 0.05:
        why = ""
        if frame_same:
            why = " The frame time did not move, which happens when it is pinned by vertical sync or a frame limiter: the computer had time to spare, so the saving is headroom (and less heat and power), not a higher frame rate."
        lines.append("The game thread does %.2f ms of timed work per frame in B against %.2f ms in A (%s).%s" % (wb_, wa_, change(wa_, wb_), why))
    if "ms per tick (game thread)" in lookup:
        ta, tb = lookup["ms per tick (game thread)"]
        if ta > 0 and abs(tb - ta) / ta >= 0.05:
            lines.append("A simulation tick costs %.2f ms in B and %.2f ms in A (%s)." % (tb, ta, change(ta, tb)))
    sa = {s: wmean(Sa, s) for s in SLOTS + ["otherMs"]}
    sb = {s: wmean(Sb, s) for s in SLOTS + ["otherMs"]}
    biggest = sorted(SLOTS + ["otherMs"], key=lambda s: -abs(sb[s] - sa[s]))[:2]
    for s in biggest:
        if abs(sb[s] - sa[s]) >= 0.1:
            lines.append("Most of the difference is in %s (%s): %.2f ms per frame in A, %.2f ms in B." % (s, SLOT_MEANING[s], sa[s], sb[s]))
    gone = [(k, va_) for _, k, va_, vb_, t in movers if vb_ == 0 and va_ >= 0.5]
    added = [(k, vb_) for _, k, va_, vb_, t in movers if va_ == 0 and vb_ >= 0.5]
    if gone:
        lines.append("Only A has these (the ones above 0.5 ms/s): " + "; ".join("%s (%.1f ms/s)" % (short(k[1], 48), v) for k, v in gone[:4]) + ". Time they took is time B does not spend.")
    if added:
        lines.append("Only B has these (the ones above 0.5 ms/s): " + "; ".join("%s (%.1f ms/s)" % (short(k[1], 48), v) for k, v in added[:4]) + ". Time B spends that A does not.")
    only_a = [m for m in a.mods if m not in b.mods]
    only_b = [m for m in b.mods if m not in a.mods]
    if only_a or only_b:
        lines.append("The mod lists differ (only in A: %s; only in B: %s), so the change may be that mod's doing -- if the mod's own singletons or watched methods are listed in section 4, that is direct evidence; otherwise it is only a correlation." % (
            ", ".join(only_a) or "none", ", ".join(only_b) or "none"))
    elif not diffs:
        lines.append("Nothing differs in the environment, so a real difference here would come from the workload (what the players did) or from chance.")
    if cautions:
        lines.append("Read the cautions in section 1 before believing any of the above.")
    return lines


# ---------------------------------------------------------------- list

def default_root():
    docs = os.path.join(os.path.expanduser("~"), "Documents", "Timberborn", "PerformanceLog")
    return docs


def list_sessions(root, out):
    if not os.path.isdir(root):
        out.write("No folder %s\n" % root)
        return 1
    found = []
    for name in sorted(os.listdir(root)):
        path = os.path.join(root, name)
        if os.path.isfile(os.path.join(path, "frames.csv")):
            found.append(path)
    if not found:
        out.write("No sessions in %s\n" % root)
        return 0
    out.write("%-24s %8s %8s %9s %6s %s\n" % ("session", "minutes", "frames", "mean ms", "mods", "game"))
    for path in found:
        try:
            s = load_session(path)
            S = s.summaries
            out.write("%-24s %8.1f %8d %9.1f %6d %s%s\n" % (s.name, seconds_of(S) / 60, sum(r["frames"] for r in S), wmean(S, "frameMs"), len(s.mods), s.h("game", "?"),
                                                            "" if s.ended else "  (no closing line)"))
        except (OSError, ValueError) as error:
            out.write("%-24s unreadable: %s\n" % (os.path.basename(path), error))
    return 0


# ---------------------------------------------------------------- main

def main(argv=None, out=None):
    out = out or sys.stdout
    parser = argparse.ArgumentParser(description="Read Performance Log sessions: report on one, compare two, or list them.")
    sub = parser.add_subparsers(dest="command")
    for name in ("report", "compare"):
        sp = sub.add_parser(name)
        sp.add_argument("paths", nargs="+" if name == "report" else 2, help="a session folder (or its frames.csv)")
        sp.add_argument("--warmup", type=float, default=30.0, help="seconds at the start to leave out (loading, shader warm-up); default 30")
        sp.add_argument("--top", type=int, default=12 if name == "report" else 15, help="rows to list in each table")
        if name == "report":
            sp.add_argument("--all", action="store_true", help="list every key, and every capability line")
            sp.add_argument("--json", action="store_true", help="print the findings and key numbers as JSON instead")
    sp = sub.add_parser("list")
    sp.add_argument("root", nargs="?", default=default_root())
    args = parser.parse_args(argv)
    if args.command is None:
        parser.print_help(out)
        return 2
    try:
        if args.command == "list":
            return list_sessions(args.root, out)
        if args.command == "report":
            if len(args.paths) != 1:
                parser.error("report takes one session; use compare for two")
            session = load_session(args.paths[0])
            if args.json:
                out.write(json.dumps(report_json(session, args), indent=2) + "\n")
                return 0
            return report(session, args, out)
        a, b = load_session(args.paths[0]), load_session(args.paths[1])
        return compare(a, b, args, out)
    except (OSError, ValueError) as error:
        out.write("Cannot read: %s\n" % error)
        return 1


if __name__ == "__main__":
    sys.exit(main())
