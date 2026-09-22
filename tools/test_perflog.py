"""Tests for perflog.py.  Run:  python -m unittest discover -s tools -p "test_perflog.py"

Two kinds of test. The checked-in fixtures (tests/fixtures/sample-*) are written by the mod's real probe, writer and summary on a
scripted clock (see tests/SampleSession.cs), so they check that this tool reads what the mod writes. The synthetic sessions are built
here, column by column, to check that each finding fires on the situation it describes and stays quiet otherwise.
"""
import io
import json
import os
import shutil
import tempfile
import unittest

import perflog

HERE = os.path.dirname(os.path.abspath(__file__))
FIXTURES = os.path.join(HERE, "..", "tests", "fixtures")
WITH_MOD = os.path.join(FIXTURES, "sample-with-mod")
WITHOUT_MOD = os.path.join(FIXTURES, "sample-without-mod")


def read(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


def write(path, text):
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write(text)


def run(*argv):
    out = io.StringIO()
    code = perflog.main(list(argv), out)
    return code, out.getvalue()


def fixture_columns():
    with open(os.path.join(WITH_MOD, "frames.csv"), encoding="utf-8") as f:
        for line in f:
            if line.startswith("type,"):
                return line.strip().split(",")
    raise AssertionError("no column header in the fixture")


PROFILE_COLUMNS = "kind,window,tick,id,calls,sampled,ms,allocKB,maxMs,name,assembly,mod".split(",")
SPIKE_COLUMNS = "frame,tick,utcMs,frameMs,rank,kind,id,ms,share,name,mod".split(",")


class Synthetic:
    """A session folder built row by row: only the columns a test cares about are set, the rest are 0."""

    def __init__(self, header=None, mods=None):
        self.dir = tempfile.mkdtemp(prefix="perflog-test-")
        self.columns = fixture_columns()
        self.rows = []
        self.profile = []
        self.spikes = []
        self.events = []
        self.header = {"session": "synthetic", "game": "1.1.2.4", "unity": "6000", "cpu": "cpu", "gpu": "gpu", "os": "os",
                       "display": "vSyncCount=0 targetFrameRate=-1 resolution=1920x1080 refreshHz=60.00 fullScreen=Windowed",
                       "gc": "mode=Enabled incremental=False timeSliceNs=0 maxGeneration=2", "thresholdMs": "50", "summarySeconds": "10"}
        self.header.update(header or {})
        self.mods = mods if mods is not None else [("Harmony", "Harmony", "v2.4.1")]
        self.pipes = []
        self.utc = 1_800_000_000_000.0
        self.frame = 0
        self.tick = 0

    def window(self, seconds=10.0, frame_ms=16.7, speed=1.0, **columns):
        """One summary row covering `seconds` of play."""
        frames = seconds * 1000.0 / frame_ms
        self.utc += seconds * 1000.0
        self.frame += int(frames)
        row = {"type": "S", "frame": self.frame, "utcMs": self.utc, "frames": frames, "frameMs": frame_ms, "maxFrameMs": frame_ms * 1.2,
               "speed": speed, "heapMB": 500.0}
        row["ticks"] = columns.pop("ticks", speed * seconds * 3.33)
        self.tick += int(row["ticks"])
        row["tick"] = self.tick
        # everything the slots do not cover is "other", as the mod computes it
        slots = ["tickMs", "singMs", "entMs", "parWaitMs", "parStartMs", "updMs", "lateMs", "saveMs"]
        for s in slots:
            row[s] = columns.pop(s, 0.0)
        row["otherMs"] = max(0.0, frame_ms - sum(row[s] for s in slots))
        row.update(columns)
        self.rows.append(row)
        return row

    def slow_frame(self, ms, **columns):
        self.frame += 1
        row = {"type": "F", "frame": self.frame, "tick": self.tick, "utcMs": self.utc, "frames": 1, "frameMs": ms, "maxFrameMs": ms, "speed": 1.0}
        row.update(columns)
        row.setdefault("otherMs", ms)
        self.rows.append(row)
        return row

    def write(self, ended=True, profile=True, spikes=True, events=True):
        with open(os.path.join(self.dir, "frames.csv"), "w", encoding="utf-8", newline="") as f:
            f.write("# PerformanceLog frames, format 1\n")
            for k, v in self.header.items():
                f.write("# %s: %s\n" % (k, v))
            for m in self.mods:
                f.write("# mod|%s|%s|%s\n" % m)
            for p in self.pipes:
                f.write("# " + "|".join(p) + "\n")
            f.write(",".join(self.columns) + "\n")
            for row in self.rows:
                f.write(",".join(self._cell(row, c) for c in self.columns) + "\n")
            if ended:
                f.write("# end\n")
        if profile:
            with open(os.path.join(self.dir, "profile.csv"), "w", encoding="utf-8", newline="") as f:
                f.write(",".join(PROFILE_COLUMNS) + "\n")
                for r in self.profile:
                    f.write(",".join(str(r.get(c, 0)) for c in PROFILE_COLUMNS) + "\n")
        if spikes:
            with open(os.path.join(self.dir, "spikes.csv"), "w", encoding="utf-8", newline="") as f:
                f.write(",".join(SPIKE_COLUMNS) + "\n")
                for r in self.spikes:
                    f.write(",".join(str(r.get(c, 0)) for c in SPIKE_COLUMNS) + "\n")
        if events:
            with open(os.path.join(self.dir, "events.csv"), "w", encoding="utf-8", newline="") as f:
                f.write("utcMs,tick,kind,ms,detail\n")
                for e in self.events:
                    f.write("%s,%s,%s,%s,\"%s\"\n" % (e["utcMs"], e.get("tick", 0), e["kind"], e["ms"], e.get("detail", "")))
        return self.dir

    @staticmethod
    def _cell(row, column):
        value = row.get(column, 0)
        if column == "type":
            return value if isinstance(value, str) else "S"
        return "%.2f" % value

    def cleanup(self):
        shutil.rmtree(self.dir, ignore_errors=True)


class FixtureTests(unittest.TestCase):
    def test_reads_what_the_mod_writes(self):
        s = perflog.load_session(WITH_MOD)
        self.assertEqual("sample-with-mod", s.name)
        self.assertTrue(s.ended, "the file has its closing line")
        self.assertEqual(0, s.skipped)
        self.assertEqual([], [p for p in s.problems if "lacks columns" in p], "every column this tool needs is in the file")
        self.assertEqual(30, len(s.summaries))
        self.assertEqual(9, len(s.slow))
        self.assertEqual(3, len(s.mods))
        self.assertEqual("v0.4.9", s.mods["kyler.lategameperformance"][1])
        self.assertIn("incremental=False", s.h("gc"))
        self.assertEqual([["frameEdgesMs"]], [[p[0]] for p in s.pipe("histogram", "frameEdgesMs")])
        self.assertEqual(16, len(perflog.frame_edges(s)))

    def test_every_expected_column_exists(self):
        s = perflog.load_session(WITH_MOD)
        for c in perflog.REQUIRED + perflog.PHASES:
            self.assertIn(c, s.columns, "perflog.py expects the column " + c)
        for i in range(17):
            self.assertIn("fh%d" % i, s.columns)

    def test_profile_spikes_and_events(self):
        s = perflog.load_session(WITH_MOD)
        kinds = {r["kind"] for r in s.profile}
        self.assertTrue({"tick-singleton", "update-singleton", "late-singleton", "parallel-start", "entity"} <= kinds)
        mods = {r["mod"] for r in s.profile}
        self.assertIn("kyler.lategameperformance", mods)
        self.assertIn("game", mods)
        self.assertTrue(all(sp["rank"] >= 1 for sp in s.spikes))
        self.assertTrue(any(sp["name"] == "LateGamePerformance.RouteMapsBackground" and sp["ms"] > 90 for sp in s.spikes))
        self.assertEqual(["session-start", "save", "session-end"], [e["kind"] for e in s.events])
        self.assertEqual(430.0, s.events[1]["ms"])
        self.assertIn("finishing the tick", s.events[1]["detail"])

    def test_a_frames_csv_path_works_like_its_folder(self):
        s = perflog.load_session(os.path.join(WITH_MOD, "frames.csv"))
        self.assertEqual("sample-with-mod", s.name)


class ReportTests(unittest.TestCase):
    def test_report_finds_the_planted_causes(self):
        code, text = run("report", WITH_MOD, "--warmup", "10")
        self.assertEqual(0, code)
        for section in ("1. THE SESSION", "2. FRAME RATE", "3. WHERE AN AVERAGE FRAME GOES", "4. WHAT THIS POINTS TO", "5. THE SLOWEST FRAMES",
                        "6. WHERE THE TIME GOES", "7. GARBAGE COLLECTION", "8. WHAT EACH SOURCE COULD MEASURE"):
            self.assertIn(section, text)
        self.assertIn("Garbage collection causes most of the hitches", text)
        self.assertIn("Saving the game stalls the frame", text)
        self.assertIn("LateGamePerformance.RouteMapsBackground takes most of 2 slow frame(s)", text)
        self.assertIn("kyler.lategameperformance", text)
        self.assertIn("keeping up with the display", text, "frames at the vsync rate are healthy, not a graphics problem")
        self.assertNotIn("Most of the frame is not the game's", text)
        self.assertIn("The collector is not incremental", text)

    def test_json_output_is_valid_and_has_the_findings(self):
        code, text = run("report", WITH_MOD, "--warmup", "10", "--json")
        self.assertEqual(0, code)
        data = json.loads(text)
        self.assertEqual("sample-with-mod", data["session"])
        self.assertGreater(len(data["findings"]), 3)
        self.assertAlmostEqual(16.8, data["meanFrameMs"], delta=0.5)
        self.assertIn("kyler.lategameperformance", data["mods"])

    def test_top_and_all(self):
        _, short_text = run("report", WITH_MOD, "--top", "2")
        _, long_text = run("report", WITH_MOD, "--all")
        self.assertLess(short_text.count("\n"), long_text.count("\n"))

    def test_list(self):
        code, text = run("list", FIXTURES)
        self.assertEqual(0, code)
        self.assertIn("sample-with-mod", text)
        self.assertIn("sample-without-mod", text)


class CompareTests(unittest.TestCase):
    def test_compare_shows_what_the_mod_costs(self):
        code, text = run("compare", WITH_MOD, WITHOUT_MOD, "--warmup", "10")
        self.assertEqual(0, code)
        self.assertIn("only sample-with-mod has mod kyler.lategameperformance", text)
        self.assertIn("LateGamePerformance.RouteMapsBackground", text)
        self.assertIn("(only in A)", text)
        self.assertIn("game-thread work ms/frame", text)
        self.assertIn("pinned by vertical sync", text, "an unchanged frame time with less work is explained")
        self.assertIn("Only A has these", text)
        line = [l for l in text.splitlines() if l.strip().startswith("game-thread work")][0]
        self.assertIn("-", line.split()[-1], "B does less work")

    def test_swapping_the_sessions_swaps_the_direction(self):
        _, text = run("compare", WITHOUT_MOD, WITH_MOD, "--warmup", "10")
        self.assertIn("(only in B)", text)
        self.assertIn("Only B has these", text)

    def test_comparing_a_session_with_itself_says_nothing_changed(self):
        _, text = run("compare", WITH_MOD, WITH_MOD, "--warmup", "10")
        self.assertIn("Same mods, game, computer and settings.", text)
        self.assertIn("inside the noise", text)
        self.assertNotIn("Only A has these", text)

    def test_patches_that_differ_are_listed(self):
        a, b = Synthetic(), Synthetic()
        try:
            shared = ["patch", "hot", "Timberborn.TickSystem.TickableEntity.Tick", "prefix", "same.mod", "priority=400", "index=0", "before=", "after=", "Same", "Same.P"]
            a.pipes.append(shared)
            b.pipes.append(["patch", "shared"] + shared[2:])   # the same patch, listed under another tag, is not a difference
            a.pipes.append(["patch", "hot", "Timberborn.InputSystem.InputService.UpdateSingleton", "prefix", "old.mod", "priority=400", "index=0", "before=", "after=", "Old", "Old.P"])
            b.pipes.append(["patch", "other", "Timberborn.Navigation.NavigationSynchronizer.Tick", "postfix", "new.mod", "priority=400", "index=0", "before=", "after=", "New", "New.P"])
            b.pipes.append(["patch", "other", "Timberborn.Navigation.NavigationSynchronizer.LateUpdateSingleton", "postfix", "new.mod", "priority=400", "index=0", "before=", "after=", "New", "New.P"])
            # This mod's own measuring patches (Profile = deep in B only) are how it measures, not a difference between the games.
            for kind in ("prefix", "postfix"):
                b.pipes.append(["patch", "hot", "Timberborn.TickSystem.MeteredTickableComponent.Tick", kind, "kyler.performancelog", "priority=800", "index=0", "before=", "after=",
                                "PerformanceLog", "PerformanceLog.P"])
            for _ in range(8):
                a.window()
                b.window()
            _, text = run("compare", a.write(), b.write(), "--warmup", "0")
            section = text.split("1. ARE THE TWO SESSIONS COMPARABLE?")[1].split("2. FRAME TIME")[0]
            only_a = [l for l in section.splitlines() if "only %s has" % os.path.basename(a.dir) in l and "patch" in l]
            only_b = [l for l in section.splitlines() if "only %s has" % os.path.basename(b.dir) in l and "patch" in l]
            self.assertEqual(1, len(only_a), section)
            self.assertIn("1 patch", only_a[0])
            self.assertIn("old.mod", only_a[0])
            self.assertIn("InputService.UpdateSingleton", only_a[0])
            self.assertEqual(1, len(only_b), section)
            self.assertIn("2 patches", only_b[0])
            self.assertIn("new.mod 2", only_b[0])
            self.assertNotIn("same.mod", section, "a patch both have is not a difference")
            self.assertNotIn("kyler.performancelog", section, "this mod's own patches are not a difference")
        finally:
            a.cleanup(); b.cleanup()

    def test_patches_are_not_compared_when_one_session_did_not_record_them(self):
        a, b = Synthetic(), Synthetic(header={"patches-unavailable": "InvalidOperationException no"})
        try:
            a.pipes.append(["patch", "hot", "Timberborn.TickSystem.TickableEntity.Tick", "prefix", "some.mod", "priority=400", "index=0", "before=", "after=", "Some", "Some.P"])
            for _ in range(8):
                a.window()
                b.window()
            _, text = run("compare", a.write(), b.write(), "--warmup", "0")
            section = text.split("1. ARE THE TWO SESSIONS COMPARABLE?")[1].split("2. FRAME TIME")[0]
            self.assertIn("not recorded in %s" % os.path.basename(b.dir), section)
            self.assertNotIn("some.mod", section, "a missing list is not a list of missing patches")
        finally:
            a.cleanup(); b.cleanup()

    def test_cautions_for_different_workloads(self):
        a, b = Synthetic(), Synthetic()
        try:
            for _ in range(8):
                a.window(speed=1, colEntities=4000)
                b.window(speed=3, colEntities=9000)
            _, text = run("compare", a.write(), b.write(), "--warmup", "0")
            self.assertIn("average game speed differs", text)
            self.assertIn("colony size differs", text)
            self.assertIn("Read the cautions", text)
        finally:
            a.cleanup(); b.cleanup()


class RobustnessTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp(prefix="perflog-copy-")
        self.copy = os.path.join(self.tmp, "session")
        shutil.copytree(WITH_MOD, self.copy)

    def tearDown(self):
        shutil.rmtree(self.tmp, ignore_errors=True)

    def test_a_file_cut_short_by_a_crash(self):
        path = os.path.join(self.copy, "frames.csv")
        data = read(path)
        data = data[:data.rindex("\nS,") + 60]          # half a row, and no closing line
        write(path, data)
        code, text = run("report", self.copy)
        self.assertEqual(0, code)
        self.assertIn("no closing line", text)
        s = perflog.load_session(self.copy)
        self.assertEqual(1, s.skipped)
        self.assertFalse(s.ended)

    def test_missing_optional_files(self):
        for name in ("profile.csv", "spikes.csv", "events.csv", "summary.md", "README.md"):
            os.remove(os.path.join(self.copy, name))
        code, text = run("report", self.copy)
        self.assertEqual(0, code)
        self.assertIn("2. FRAME RATE", text)
        self.assertNotIn("6. WHERE THE TIME GOES", text)

    def test_only_slow_rows_and_no_summaries(self):
        path = os.path.join(self.copy, "frames.csv")
        lines = [l for l in read(path).splitlines() if not l.startswith("S,")]
        write(path, "\n".join(lines) + "\n")
        code, text = run("report", self.copy)
        self.assertEqual(0, code)
        self.assertIn("No summary rows", text)

    def test_not_a_session(self):
        junk = os.path.join(self.tmp, "junk")
        os.makedirs(junk)
        write(os.path.join(junk, "frames.csv"), "this,is,not\n1,2,3\n")
        code, text = run("report", junk)
        self.assertEqual(1, code)
        self.assertIn("does not look like", text)
        code, text = run("report", os.path.join(self.tmp, "nothing"))
        self.assertEqual(1, code)
        code, text = run("report", self.tmp)
        self.assertEqual(1, code)
        self.assertIn("no frames.csv", text)

    def test_a_file_from_another_version_warns(self):
        path = os.path.join(self.copy, "frames.csv")
        write(path, read(path).replace(",updMs,", ",updateMs,", 1))
        code, out = run("report", self.copy)
        self.assertEqual(0, code)
        self.assertIn("lacks columns", out)

    def test_the_whole_session_is_warm_up(self):
        code, text = run("report", WITH_MOD, "--warmup", "100000")
        self.assertEqual(0, code)
        self.assertIn("Nothing is left after the warm-up", text)
        code, text = run("report", WITH_MOD, "--warmup", "0")
        self.assertEqual(0, code)

    def test_empty_list(self):
        empty = os.path.join(self.tmp, "empty")
        os.makedirs(empty)
        code, text = run("list", empty)
        self.assertEqual(0, code)
        self.assertIn("No sessions", text)
        code, text = run("list", os.path.join(self.tmp, "absent"))
        self.assertEqual(1, code)

    def test_frame_times_of_zero_do_not_crash(self):
        # Found by fuzzing: a damaged file whose frame times are all 0 divided by zero.
        s = Synthetic()
        try:
            for _ in range(5):
                s.window(frame_ms=0.0001, mainCpuMs=3.0)
            s.rows[0]["frameMs"] = 0.0
            for r in s.rows:
                r["frameMs"] = 0.0
            code, text = run("report", s.write(), "--warmup", "0")
            self.assertEqual(0, code)
            self.assertIn("add up to zero", text)
            code, text = run("compare", s.dir, s.dir, "--warmup", "0")
            self.assertEqual(0, code)
        finally:
            s.cleanup()

    def test_no_command_prints_help(self):
        code, text = run()
        self.assertEqual(2, code)
        self.assertIn("report", text)


class HelperTests(unittest.TestCase):
    def test_percentile_is_linear_inside_a_bucket(self):
        edges = perflog.FRAME_EDGES_DEFAULT
        row = {"fh%d" % i: 0.0 for i in range(17)}
        row["fh3"] = 100.0            # 8.5 .. 11.5 ms
        self.assertAlmostEqual(10.0, perflog.percentile(edges, [row], 0.5), places=6)
        row2 = {"fh%d" % i: 0.0 for i in range(17)}
        row2["fh16"] = 10.0
        self.assertAlmostEqual(650.0, perflog.percentile(edges, [row2], 0.5, 900.0), places=6)
        self.assertEqual(0.0, perflog.percentile(edges, [{"fh%d" % i: 0.0 for i in range(17)}], 0.5))

    def test_rhythm(self):
        def row(frame, seconds):
            return {"frame": frame, "utcMs": seconds * 1000.0}
        regular = [row(100 * i, 30.0 * i) for i in range(1, 9)]
        found = perflog.rhythm(regular)
        self.assertTrue(found["regular"])
        self.assertAlmostEqual(30.0, found["period_s"])
        irregular = [row(100 * i, t) for i, t in enumerate([3, 9, 40, 44, 90, 91, 200, 260], start=1)]
        self.assertFalse(perflog.rhythm(irregular)["regular"])
        self.assertIsNone(perflog.rhythm(regular[:3]), "three events are not a rhythm")

    def test_explain_period(self):
        known = [(60.0, "an autosave"), (7.0, "garbage collection")]
        self.assertEqual("an autosave", perflog.explain_period(62.0, known))
        self.assertEqual("garbage collection", perflog.explain_period(7.5, known))
        self.assertIsNone(perflog.explain_period(23.0, known))

    def test_vsync_interval(self):
        class S:
            def __init__(self, display): self.display = display
            def h(self, key, default=""): return self.display
        self.assertAlmostEqual(16.667, perflog.vsync_interval(S("vSyncCount=1 refreshHz=60.00")), places=2)
        self.assertAlmostEqual(33.333, perflog.vsync_interval(S("vSyncCount=2 refreshHz=60.00")), places=2)
        self.assertIsNone(perflog.vsync_interval(S("vSyncCount=0 refreshHz=60.00")))
        self.assertIsNone(perflog.vsync_interval(S("")))

    def test_change_and_short(self):
        self.assertEqual("+10.0%", perflog.change(100, 110))
        self.assertEqual("new", perflog.change(0, 5))
        self.assertEqual("-", perflog.change(0, 0))
        self.assertEqual("TheClass", perflog.short("Some.Very.Long.Namespace.That.Goes.On.And.On.Forever.And.Ever.TheClass", 40))
        self.assertEqual("Short.Name", perflog.short("Short.Name"))

    def test_other_ms_is_split_by_unity_phase(self):
        # A 20 ms frame: 4 ms of entity ticks and 2 ms of singleton updates inside a 9 ms Update phase, 0.5 ms of late singletons inside a
        # 2.5 ms LateUpdate phase, 7.5 ms of plPost, 0.2 ms of plTime and 0.8 ms between the phases. otherMs is the 13.5 ms not timed.
        def row(**columns):
            r = {"frames": 100.0, "frameMs": 20.0, "tickMs": 0.0, "singMs": 0.0, "entMs": 4.0, "parWaitMs": 0.0, "parStartMs": 0.0, "updMs": 2.0,
                 "lateMs": 0.5, "saveMs": 0.0, "plTime": 0.2, "plInit": 0.0, "plEarly": 0.0, "plFixed": 0.0, "plPre": 0.0, "plUpdate": 9.0,
                 "plLate": 2.5, "plPost": 7.5}
            r.update(columns)
            r["otherMs"] = r["frameMs"] - sum(r[s] for s in perflog.SLOTS)
            return r
        expect = {"update": 3.0, "late": 2.0, "post": 7.5, "phases": 0.2, "between": 0.8}
        for name, r in (("no save", row()),
                        ("a save deferred to the end of a tick runs in Update", row(frameMs=23.0, saveMs=3.0, plUpdate=12.0)),
                        ("the game's own save runs in LateUpdate", row(frameMs=23.0, saveMs=3.0, plLate=5.5))):
            got = perflog.split_other(r)
            for key, value in expect.items():
                self.assertAlmostEqual(value, got[key], places=6, msg="%s: %s" % (name, key))
        both = perflog.other_by_phase([row(), row(frames=300.0, plUpdate=11.0, frameMs=22.0)])
        self.assertAlmostEqual((3.0 * 100 + 5.0 * 300) / 400, both["update"], places=6, msg="weighted by frames")

    def test_steady_leaves_out_warm_up_paused_and_background(self):
        s = Synthetic()
        try:
            s.window(seconds=10)                      # warm-up
            s.window(seconds=10)
            s.window(seconds=10, paused=600)          # mostly paused
            s.window(seconds=10, unfocused=600)       # mostly in the background
            s.window(seconds=10)
            session = perflog.load_session(s.write())
            self.assertEqual(2, len(perflog.steady(session, 15)))
            self.assertEqual(4, len(perflog.steady(session, 15, keep_paused=True, keep_unfocused=True)))
        finally:
            s.cleanup()


# Entity rows as Performance Log 0.1.3 and older wrote them: the game renames a character loaded from a save to "<template> <name>", and one
# born during play keeps Unity's "(Clone)". The mod now writes the kind itself.
OLD_ENTITY_ROWS = (("BeaverAdult(Clone)", 100.0), ("BeaverAdult Malak", 300.0), ("BeaverAdult Zengu", 300.0), ("BeaverChild Malak", 50.0),
                   ("DistrictCenter.Folktails(Clone)", 150.0))
NEW_ENTITY_ROWS = (("BeaverAdult", 700.0), ("BeaverChild", 50.0), ("DistrictCenter.Folktails", 150.0))


def add_entity_rows(s, rows, windows=8):
    for w in range(1, windows + 1):
        s.window(frame_ms=20.0, entMs=5.0)
        for i, (name, ms) in enumerate(rows):
            s.profile.append({"kind": "entity", "window": w, "tick": s.tick, "id": i, "calls": 1000, "sampled": 60, "ms": ms, "allocKB": 1,
                              "maxMs": 0.5, "name": name, "assembly": "", "mod": ""})
        s.profile.append({"kind": "method", "window": w, "tick": s.tick, "id": 99, "calls": 10, "sampled": 10, "ms": 20.0, "allocKB": 0,
                          "maxMs": 3, "name": "Some.Type.Method(int)", "assembly": "SomeMod", "mod": "kyler.somemod"})


class EntityRollupTests(unittest.TestCase):
    def test_an_entity_name_is_cut_at_its_first_space_or_bracket(self):
        for name, kind in (("BeaverAdult Malak", "BeaverAdult"), ("BeaverAdult(Clone)", "BeaverAdult"), (" BeaverAdult (Clone)", "BeaverAdult"),
                           ("DistrictCenter.Folktails(Clone)", "DistrictCenter.Folktails"), ("BeaverAdult", "BeaverAdult"),
                           ("BeaverAdultX", "BeaverAdultX"), ("(Clone)", "(Clone)"), ("?", "?"), ("", "")):
            self.assertEqual(kind, perflog.entity_kind(name), name)

    def test_named_entity_rows_are_rolled_up_to_their_kind(self):
        s = Synthetic(header={"mod": "0.1.3"})
        add_entity_rows(s, OLD_ENTITY_ROWS)
        try:
            totals = perflog.profile_totals(perflog.load_session(s.write()))
            self.assertEqual([("entity", "BeaverAdult"), ("entity", "BeaverChild"), ("entity", "DistrictCenter.Folktails"), ("method", "Some.Type.Method(int)")],
                             sorted(totals), "one row per kind of entity; a watched method keeps its name")
            adult = totals[("entity", "BeaverAdult")]
            self.assertAlmostEqual(8 * 700.0, adult.ms)
            self.assertEqual(8 * 3000, adult.calls)
            self.assertEqual(8 * 180, adult.sampled)
            code, text = run("report", s.dir, "--warmup", "0")
            self.assertEqual(0, code)
            line = [l for l in text.splitlines() if l.strip().startswith("BeaverAdult ")][0]
            self.assertIn("78%", line, "adults are 700 of the 900 ms of entity time")
            self.assertNotIn("Malak", text)
            self.assertIn("KNOWN ISSUE in Performance Log 0.1.3: " + perflog.ENTITY_SPLIT_NOTE, text,
                          "the recording's own profile.csv and summary.md still split them, and the report says so")
        finally:
            s.cleanup()

    def test_a_recording_whose_entity_rows_are_kinds_gets_no_split_note(self):
        # A build of the fix that still says 0.1.3 (it is not released yet), and the checked-in fixture the mod's own code wrote.
        s = Synthetic(header={"mod": "0.1.3"})
        add_entity_rows(s, NEW_ENTITY_ROWS)
        try:
            folder = s.write()
            self.assertNotIn(perflog.ENTITY_SPLIT_NOTE, perflog.known_issues(perflog.load_session(folder)))
            code, text = run("report", folder, "--warmup", "0")
            self.assertEqual(0, code)
            self.assertNotIn("rows named after single beavers", text)
        finally:
            s.cleanup()
        self.assertEqual("0.1.3", perflog.load_session(WITH_MOD).h("mod"), "the fixture is old enough for the note to be in question")
        _, text = run("report", WITH_MOD, "--warmup", "10")
        self.assertNotIn("rows named after single beavers", text)

    def test_an_old_recording_lines_up_with_a_new_one(self):
        a, b = Synthetic(header={"mod": "0.1.3"}), Synthetic(header={"mod": "0.1.4"})
        add_entity_rows(a, OLD_ENTITY_ROWS)
        add_entity_rows(b, NEW_ENTITY_ROWS)
        try:
            _, text = run("compare", a.write(), b.write(), "--warmup", "0")
            self.assertNotIn("(only in", text, "every kind is in both")
            self.assertNotIn("Only A has these", text)
            line = [l for l in text.splitlines() if l.strip().startswith("[entity] BeaverAdult ")][0]
            self.assertIn("+0.00", line)
        finally:
            a.cleanup(); b.cleanup()


class FindingTests(unittest.TestCase):
    def report(self, synthetic, *extra):
        try:
            code, text = run("report", synthetic.write(), "--warmup", "0", *extra)
            self.assertEqual(0, code)
            return text
        finally:
            synthetic.cleanup()

    def test_graphics_bound_without_vsync(self):
        s = Synthetic()
        for _ in range(8):
            s.window(frame_ms=40.0, updMs=1.0, entMs=1.0, plPost=30.0, plUpdate=5.0, mainCpuMs=8.0, procCpuMs=12.0)
        text = self.report(s)
        self.assertIn("Most of the frame is not the game's or any mod's code", text)
        self.assertIn("busy for only", text)

    def test_the_report_splits_other_ms_by_unity_phase(self):
        s = Synthetic()
        for _ in range(8):
            s.window(frame_ms=20.0, entMs=4.0, updMs=2.0, lateMs=0.5, plTime=0.2, plUpdate=9.0, plLate=2.5, plPost=7.5)
        text = self.report(s)
        section = text.split("3. WHERE AN AVERAGE FRAME GOES")[1].split("4. WHAT THIS POINTS TO")[0]
        self.assertIn("otherMs by Unity phase", section)

        def part(label):
            return [l for l in section.splitlines() if l.strip().startswith(label)][0]
        self.assertIn("3.00 ms", part("Update phase"))
        self.assertIn("2.00 ms", part("LateUpdate phase"))
        self.assertIn("other work in Unity's LateUpdate phase", part("LateUpdate phase"), "not blamed on mods")
        self.assertIn("7.50 ms", part("plPost"))
        self.assertIn("0.80 ms", part("between the phases"))

    def test_other_ms_in_the_update_phase_points_at_scripts_not_the_graphics_card(self):
        s = Synthetic()
        for _ in range(8):
            s.window(frame_ms=40.0, updMs=1.0, entMs=1.0, plUpdate=31.0, plLate=1.0, plPost=4.0, mainCpuMs=38.0, procCpuMs=40.0)
        text = self.report(s)
        self.assertIn("Most of the frame is other work in Unity's Update phase", text)
        self.assertNotIn("Most of the frame is not the game's or any mod's code", text)
        self.assertNotIn("Likely the graphics card", text)

    def test_a_wait_before_the_update_phase_still_points_at_the_graphics_card(self):
        # Unity can wait for the last frame to be presented in its first phase (plTime), not plPost. Here that wait holds 14 of 25 ms and the
        # game thread is busy for 40% of the frame, while the Update phase's untimed 2 ms is still more than plPost's 1 ms.
        s = Synthetic()
        for _ in range(8):
            s.window(frame_ms=25.0, updMs=3.0, entMs=3.0, plTime=14.0, plUpdate=8.0, plLate=1.5, plPost=1.0, mainCpuMs=10.0, procCpuMs=12.0)
        text = self.report(s)
        self.assertIn("[!] Most of the frame is not the game's or any mod's code", text, "the high-severity graphics card finding stays")
        self.assertIn("before Update", text, "the evidence says where the time is")
        self.assertNotIn("other work in Unity's Update phase, outside", text)
        self.assertNotIn("The graphics card is not what holds the frame", text)

    def test_update_phase_time_with_an_idle_game_thread_is_not_called_work(self):
        # Most of the frame is in the Update phase, outside the timed parts, but the game thread is busy for only 30% of it: something there
        # waits. The finding still points at the Update phase, and does not rule the graphics card out.
        s = Synthetic()
        for _ in range(8):
            s.window(frame_ms=40.0, updMs=1.0, entMs=1.0, plUpdate=31.0, plLate=1.0, plPost=4.0, mainCpuMs=12.0, procCpuMs=14.0)
        text = self.report(s)
        self.assertIn("Most of the frame is other work in Unity's Update phase", text)
        self.assertNotIn("The graphics card is not what holds the frame", text)
        self.assertIn("busy for only 30%", text)

    def test_vsync_capped_is_healthy(self):
        s = Synthetic(header={"display": "vSyncCount=1 targetFrameRate=-1 resolution=1920x1080 refreshHz=60.00 fullScreen=Windowed"})
        for _ in range(8):
            s.window(frame_ms=16.7, updMs=1.0, entMs=1.0, plPost=12.0)
        text = self.report(s)
        self.assertIn("keeping up with the display", text)
        self.assertNotIn("Most of the frame is not the game's", text)

    def test_update_bound_names_the_singleton(self):
        s = Synthetic(mods=[("Harmony", "Harmony", "v"), ("kyler.slowmod", "Slow Mod", "v1")])
        for w in range(1, 9):
            s.window(frame_ms=16.7, updMs=6.0)
            s.profile.append({"kind": "update-singleton", "window": w, "tick": s.tick, "id": 0, "calls": 600, "sampled": 600, "ms": 3000.0, "allocKB": 10,
                              "maxMs": 9, "name": "Slow.Mod.Panel", "assembly": "SlowMod", "mod": "kyler.slowmod"})
        text = self.report(s)
        self.assertIn("Per-frame singleton updates take", text)
        self.assertIn("Slow.Mod.Panel", text)
        self.assertIn("kyler.slowmod", text)

    def test_component_time_is_rolled_up_by_mod(self):
        s = Synthetic(mods=[("Harmony", "Harmony", "v"), ("kyler.walkers", "Walkers", "v1")])
        for w in range(1, 9):
            s.window(frame_ms=20.0, speed=3, entMs=6.0, ticks=100)
            for i, (name, mod, ms) in enumerate((("Walkers.SlowWalker", "kyler.walkers", 900.0), ("Timberborn.Walking.Walker", "game", 300.0))):
                s.profile.append({"kind": "component", "window": w, "tick": s.tick, "id": i, "calls": 5000, "sampled": 300, "ms": ms, "allocKB": 5,
                                  "maxMs": 0.2, "name": name, "assembly": "A", "mod": mod})
        text = self.report(s)
        self.assertIn("entity component time by mod", text)
        line = [l for l in text.splitlines() if l.strip().startswith("kyler.walkers")][-1]
        self.assertIn("ms/s", line)

    def test_simulation_bound(self):
        s = Synthetic()
        for _ in range(8):
            s.window(frame_ms=30.0, speed=3, entMs=12.0, singMs=4.0, parWaitMs=2.0, ticks=100)
        text = self.report(s)
        self.assertIn("The simulation takes", text)
        self.assertIn("entMs", text)

    def test_gc_advice_reads_the_boot_config(self):
        def gc_session(pipes):
            s = Synthetic()
            s.pipes.extend(pipes)
            for _ in range(6):
                s.window(gcDelta=1)
            for i in range(6):
                s.slow_frame(120.0, gcDelta=1.0)
            return s
        text = self.report(gc_session([["bootconfig", "gc-max-time-slice=3"]]))
        self.assertIn("although boot.config has 'gc-max-time-slice=3'", text)
        text = self.report(gc_session([["bootconfig", "gfx-enable-gfx-jobs=1"]]))
        self.assertIn("boot.config has no gc-max-time-slice line", text)

    def test_a_tick_that_gets_slower(self):
        s = Synthetic()
        for i in range(12):
            s.window(frame_ms=20.0, speed=3, entMs=2.0 + i * 0.8, ticks=100, colEntities=5000)
        text = self.report(s)
        self.assertIn("A tick got", text)
        self.assertIn("more expensive over the session", text)
        self.assertIn("about the same size", text)

    def test_heap_growth(self):
        s = Synthetic()
        for i in range(12):
            s.window(heapMB=400 + i * 60)
        text = self.report(s)
        self.assertIn("The managed heap grew from", text)

    def test_unfocused_session_warns(self):
        s = Synthetic()
        for _ in range(6):
            s.window(unfocused=600)
        text = self.report(s)
        self.assertIn("in the background for", text)

    def test_measuring_overhead_warns(self):
        s = Synthetic()
        for _ in range(6):
            s.window(frame_ms=16.7, overheadUs=600.0, probeUs=100.0)
        text = self.report(s)
        self.assertIn("Measuring itself cost", text)

    def test_a_patch_that_never_ran_is_flagged(self):
        s = Synthetic()
        s.pipes.append(["capability-final", "patchCalls", "TickableEntityBucket.TickAll", "0", "never ran"])
        for _ in range(6):
            s.window()
        text = self.report(s)
        self.assertIn("The patch on TickableEntityBucket.TickAll never ran", text)

    def test_an_older_mod_version_gets_its_known_issues_listed(self):
        s = Synthetic(header={"mod": "0.1.0"})
        for _ in range(6):
            s.window()
        text = self.report(s)
        self.assertIn("KNOWN ISSUE in Performance Log 0.1.0", text)
        self.assertIn("four times every frame", text)
        s = Synthetic(header={"mod": "0.1.1"})
        for _ in range(6):
            s.window()
        text = self.report(s)
        for fixed, note in perflog.KNOWN_ISSUES:
            if perflog.version_tuple(fixed) <= (0, 1, 1):
                self.assertNotIn(note, text, "the version that fixed them does not have them")
        newest = max((fixed for fixed, _ in perflog.KNOWN_ISSUES), key=perflog.version_tuple)
        s = Synthetic(header={"mod": newest})
        for _ in range(6):
            s.window()
        self.assertNotIn("KNOWN ISSUE", self.report(s), "the version that fixed the last of them has none")
        s = Synthetic(header={"mod": "something else"})
        for _ in range(6):
            s.window()
        self.assertNotIn("KNOWN ISSUE", self.report(s), "an unreadable version is not guessed at")

    def test_a_recording_whose_patch_cost_read_0_is_told_what_overhead_charged_the_patches(self):
        old = ["calibration", "clockReadNs", "23", "allocReadNs", "12", "scopePairNs", "109", "samplePairNs", "51", "patchCallNs", "0"]
        new = ["calibration", "clockReadNs", "23", "allocReadNs", "12", "scopePairNs", "109", "samplePairNs", "51", "patchBodyNs", "6.2", "patchCallNs", "6.9"]
        understated, guess = "overheadUs understates what the mod itself cost", "overheadUs's charge for the per-call patches"
        # A 10 s window of 598.8 frames with 1000 patch calls a frame. overheadUs 10 us a frame is 10 ns a patch call, less than the 40 ns the mod
        # charged when the empty patch read exactly 0, so the reading was above 0 and the patches were charged almost nothing. 50 us a frame is
        # what the 40 ns charge (plus the other costs) gives, and could also be almost nothing plus a lot of sampling: it proves neither.
        cheap, dear, none = dict(overheadUs=10.0, patchCalls=598800.0), dict(overheadUs=50.0, patchCalls=598800.0), dict(overheadUs=5.0)
        for version, calibration, rows, slow, expect in (
                ("0.1.3", old, cheap, None, understated),
                ("0.1.1", old, dear, dict(overheadUs=30.0, patchCalls=1000.0), understated),  # one slow frame at 30 ns a call proves it
                ("0.1.0", old, dear, dict(overheadUs=45.0, patchCalls=1000.0), guess),
                ("0.1.3", old[:-1] + ["2"], dear, None, understated),                         # read 2 ns: charged that, and the bodies left out
                ("0.1.3", old, none, None, None),                                             # no patch ran: nothing to say
                ("0.1.3", new, cheap, None, None),                                            # the bodies were measured
                ("0.1.3", None, cheap, None, None),
                ("0.1.4", old, cheap, None, None)):
            s = Synthetic(header={"mod": version})
            if calibration:
                s.pipes.append(calibration)
            for _ in range(6):
                s.window(**rows)
            if slow:
                s.slow_frame(60.0, **slow)
            text = self.report(s)
            case = (version, calibration and calibration[-4:], rows, slow)
            self.assertEqual(expect == understated, understated in text, case)
            self.assertEqual(expect == guess, guess in text, case)

    def test_the_loadall_counter_bug_of_0_1_0_is_not_reported_as_a_finding(self):
        for version, expect in (("0.1.0", False), ("0.1.1", True)):
            s = Synthetic(header={"mod": version})
            s.pipes.append(["capability-final", "patchCalls", "SingletonLifecycleService.LoadAll", "0", "never ran"])
            for _ in range(6):
                s.window()
            text = self.report(s)
            self.assertEqual(expect, "The patch on SingletonLifecycleService.LoadAll never ran" in text, version)

    def test_game_singletons_with_an_empty_mod_are_read_as_the_game(self):
        s = Synthetic(header={"mod": "0.1.0"})
        for i in range(6):
            s.window(frame_ms=30.0, updMs=12.0)
        for w in range(1, 7):
            s.profile.append({"kind": "update-singleton", "window": w, "tick": w * 30, "id": 1, "calls": 300, "sampled": 300, "ms": 90.0, "allocKB": 0, "maxMs": 1.0,
                              "name": "Timberborn.SomethingUI.Panel", "assembly": "Timberborn.SomethingUI", "mod": ""})
            s.profile.append({"kind": "update-singleton", "window": w, "tick": w * 30, "id": 2, "calls": 300, "sampled": 300, "ms": 30.0, "allocKB": 0, "maxMs": 1.0,
                              "name": "Other.Library.Thing", "assembly": "OtherLibrary", "mod": ""})
        text = self.report(s)
        self.assertRegex(text, r"Timberborn\.SomethingUI\.Panel\s+game\b")
        self.assertRegex(text, r"Other\.Library\.Thing\s+\(unknown\)")

    def test_a_watched_method_window_nobody_timed_is_not_counted_at_0_ms(self):
        s = Synthetic()
        for w in range(1, 7):
            s.window()
            # Five windows timed at 0.5 ms a call; in the sixth every timed call threw, so the mod wrote the calls with sampled 0 and no time.
            timed = w <= 5
            s.profile.append({"kind": "method", "window": w, "tick": s.tick, "id": 3, "calls": 100 if timed else 40, "sampled": 10 if timed else 0,
                              "ms": 50.0 if timed else 0.0, "allocKB": 0, "maxMs": 0.6 if timed else 0.0, "name": "Some.Mod.Method()", "assembly": "SomeMod", "mod": "somemod"})
        text = self.report(s)
        self.assertRegex(text, r"Some\.Mod\.Method\(\).*\b500\.0 us/call.*\(\+40 calls never timed\)")

    def test_loading_that_grew_the_heap_is_reported(self):
        s = Synthetic()
        for _ in range(6):
            s.window()
        s.profile.append({"kind": "load-non-singleton", "window": 0, "tick": 0, "id": 5, "calls": 1, "sampled": 1, "ms": 8000.0, "allocKB": 900 * 1024, "maxMs": 8000.0,
                          "name": "Timberborn.WorldPersistence.WorldEntitiesLoader", "assembly": "Timberborn.WorldPersistence", "mod": "game"})
        s.profile.append({"kind": "load", "window": 0, "tick": 0, "id": 6, "calls": 1, "sampled": 1, "ms": 300.0, "allocKB": 400 * 1024, "maxMs": 300.0,
                          "name": "Timberborn.SomethingSystem.Thing", "assembly": "Timberborn.SomethingSystem", "mod": "game"})
        text = self.report(s)
        self.assertIn("Loading grew the managed heap by 1300 MB", text)
        self.assertIn("WorldEntitiesLoader 900 MB", text)

    def test_quiet_session_says_nothing_stands_out(self):
        s = Synthetic(header={"display": "vSyncCount=0 refreshHz=60.00"})
        for _ in range(6):
            s.window(frame_ms=8.0, updMs=0.5, entMs=0.5, plPost=1.0, mainCpuMs=6.0)
        text = self.report(s)
        self.assertIn("Nothing stands out", text)

    def test_slow_frames_blame_only_a_singleton_that_took_a_real_part(self):
        s = Synthetic()
        for _ in range(6):
            s.window()
        # As in the real 0.1.3 session: a save of over a second whose biggest singleton took about 1 ms; as in the sample, a collection
        # whose biggest singleton took 1 ms of 132; a mod's hitch; and a 6 ms singleton in a 215 ms frame (under 10%, but 5 ms or more).
        save = s.slow_frame(1142.0, saving=1.0, saveMs=1133.0, gcDelta=1.0, otherMs=7.6)
        gc = s.slow_frame(132.0, gcDelta=1.0, otherMs=131.0)
        hitch = s.slow_frame(99.0, updMs=96.0, otherMs=3.0)
        mixed = s.slow_frame(215.0, gcDelta=1.0, singMs=7.0, otherMs=208.0)
        for row, rank, name, ms in ((save, 1, "Timberborn.TimbermeshAnimations.AnimatorRegistry", 1.4), (gc, 1, "Timberborn.CoreUI.PanelStack", 1.0),
                                    (hitch, 1, "LateGamePerformance.RouteMapsBackground", 95.0), (hitch, 2, "Timberborn.CoreUI.PanelStack", 1.0),
                                    (mixed, 1, "Timberborn.GameDistricts.DistrictCitizenAssigner", 6.0), (mixed, 2, "Timberborn.CoreUI.PanelStack", 1.0)):
            s.spikes.append({"frame": row["frame"], "tick": row["tick"], "utcMs": row["utcMs"], "frameMs": row["frameMs"], "rank": rank, "kind": "update-singleton",
                             "id": rank, "ms": ms, "share": 100.0 * ms / row["frameMs"], "name": name, "mod": "game"})
        text = self.report(s)
        section = text.split("5. THE SLOWEST FRAMES")[1].split("\n\n")[0]

        def line(row):
            return [l for l in section.splitlines() if l.strip().startswith("frame %d " % row["frame"])][0]
        self.assertNotIn("AnimatorRegistry", line(save), "a 1 ms singleton is not blamed for a 1142 ms save")
        self.assertIn("no singleton stood out", line(save))
        self.assertIn("save", line(save).split("<-")[1])
        self.assertNotIn("PanelStack", line(gc), "a 1 ms singleton is not blamed for a 132 ms collection")
        self.assertIn("garbage collection", line(gc).split("<-")[1])
        self.assertIn("<- LateGamePerformance.RouteMapsBackground 95 ms", line(hitch))
        self.assertNotIn("PanelStack", line(hitch), "the 1 ms one behind it is not named")
        self.assertIn("DistrictCitizenAssigner 6 ms", line(mixed), "5 ms or more is named whatever the share")
        self.assertNotIn("PanelStack", line(mixed))

    def test_singletons_other_mods_patch_are_marked_and_hot_patches_listed(self):
        s = Synthetic(mods=[("Harmony", "Harmony", "v"), ("some.mod", "Some Mod", "v1"), ("other.mod", "Other Mod", "v1"), ("third.mod", "Third Mod", "v1")])

        def patch(tag, method, kind, owner):
            s.pipes.append(["patch", tag, method, kind, owner, "priority=400", "index=0", "before=", "after=", owner.split(".")[0], owner + ".Patch." + kind])
        patch("hot", "Timberborn.InputSystem.InputService.UpdateSingleton", "prefix", "some.mod")
        patch("hot", "Timberborn.InputSystem.InputService.UpdateSingleton", "finalizer", "some.mod")
        patch("shared", "Timberborn.Navigation.NavigationSynchronizer.Tick", "prefix", "other.mod")
        patch("shared", "Timberborn.Navigation.NavigationSynchronizer.Tick", "postfix", "kyler.performancelog")
        patch("other", "Timberborn.Navigation.NavigationSynchronizer.LateUpdateSingleton", "prefix", "third.mod")   # not the Tick row's method
        patch("hot", "Timberborn.TickSystem.Ticker.Update", "prefix", "kyler.performancelog")                      # this mod's own
        patch("hot", "Timberborn.TickSystem.TickableEntity.Tick", "prefix", "other.mod")
        for w in range(1, 9):
            s.window(frame_ms=16.7, updMs=3.0, singMs=1.0)
            for i, (kind, name) in enumerate((("update-singleton", "Timberborn.InputSystem.InputService"), ("tick-singleton", "Timberborn.Navigation.NavigationSynchronizer"),
                                              ("update-singleton", "Timberborn.CameraSystem.CameraService"))):
                s.profile.append({"kind": kind, "window": w, "tick": s.tick, "id": i, "calls": 600, "sampled": 600, "ms": 300.0 - 50 * i, "allocKB": 1,
                                  "maxMs": 1, "name": name, "assembly": name.rsplit(".", 1)[0], "mod": "game"})
        text = self.report(s)
        section = text.split("6. WHERE THE TIME GOES")[1].split("7. GARBAGE COLLECTION")[0]

        def row(name):
            return [l for l in section.splitlines() if l.strip().startswith(name)][0]
        self.assertIn("some.mod", row("Timberborn.InputSystem.InputService"), "a singleton whose UpdateSingleton another mod patches says so")
        self.assertIn("other.mod", row("Timberborn.Navigation.NavigationSynchronizer"))
        self.assertNotIn("third.mod", row("Timberborn.Navigation.NavigationSynchronizer"), "a patch on its LateUpdateSingleton is not in its tick time")
        self.assertNotIn("kyler.performancelog", row("Timberborn.Navigation.NavigationSynchronizer"), "this mod's own patches are not listed")
        self.assertNotIn("patch", row("Timberborn.CameraSystem.CameraService"), "an unpatched singleton says nothing")
        hot = section.split("hot methods other mods patch")[1]
        self.assertIn("InputService.UpdateSingleton", hot)
        self.assertIn("some.mod (prefix, finalizer)", hot)
        self.assertIn("TickableEntity.Tick", hot)
        self.assertNotIn("Ticker.Update", hot, "a hot method only this mod patches is not listed")
        self.assertNotIn("LateUpdateSingleton", hot, "only hot methods are listed")

    def test_no_hot_patch_list_without_hot_patches_by_other_mods(self):
        def session(header=None):
            s = Synthetic(header=header)
            s.pipes.append(["patch", "hot", "Timberborn.TickSystem.Ticker.Update", "prefix", "kyler.performancelog", "priority=400", "index=0", "before=", "after=",
                            "PerformanceLog", "PerformanceLog.P"])   # only this mod's own
            for w in range(1, 9):
                s.window(frame_ms=16.7, updMs=3.0)
                s.profile.append({"kind": "update-singleton", "window": w, "tick": s.tick, "id": 0, "calls": 600, "sampled": 600, "ms": 300.0, "allocKB": 1,
                                  "maxMs": 1, "name": "Timberborn.InputSystem.InputService", "assembly": "Timberborn.InputSystem", "mod": "game"})
            return s
        section = self.report(session()).split("6. WHERE THE TIME GOES")[1].split("7. GARBAGE COLLECTION")[0]
        self.assertNotIn("hot methods other mods patch", section, "no heading over an empty list")
        self.assertNotIn("not recorded", section)
        section = self.report(session({"patches-unavailable": "InvalidOperationException no"})).split("6. WHERE THE TIME GOES")[1].split("7. GARBAGE COLLECTION")[0]
        self.assertNotIn("hot methods other mods patch (", section)
        self.assertIn("patches were not recorded", section, "a recording without the patch list says so, not that nothing is patched")

    def test_heap_mode_leaves_frames_with_a_collection_out_of_allocation_per_second(self):
        def session(source, final=None):
            s = Synthetic()
            s.pipes.append(["capability", "allocSource", source])
            if final:
                s.pipes.append(["capability-final", "allocSource", "heap size", final])
            for i in range(6):
                if i == 3:
                    s.slow_frame(5000.0, gcDelta=1.0, allocKB=-100000.0)   # a 5 s frame with a collection: the heap shrank, what it allocated is lost
                s.window(allocKB=1000.0)
            return s
        heap = "GC.GetTotalMemory(false) (coarse: moves only when the heap grows)"
        text = self.report(session(heap))
        self.assertIn("allocated about 109 KB per second", text, "6000 KB over the 55 s whose allocation was measured")
        self.assertIn("not measured in at least 1 frame", text, "an older recording does not count them, so the slow rows are the evidence")
        text = self.report(session(heap, "allocation not measured in 2 frames (5.0 s)"))
        self.assertIn("not measured in 2 frames", text, "the mod's own count is used when the recording has it")
        text = self.report(session("GC.GetAllocatedBytesForCurrentThread (exact)"))
        self.assertIn("allocated about 100 KB per second", text, "an exact counter does not fall at a collection")
        self.assertNotIn("not measured in", text)

    def test_heap_mode_counts_are_scoped_and_a_lost_frames_growth_is_left_out(self):
        heap = "GC.GetTotalMemory(false) (coarse: grows with allocation, falls at a garbage collection)"
        s = Synthetic()
        s.pipes.append(["capability", "allocSource", heap])
        for i in range(7):
            if i == 1:
                s.slow_frame(3000.0, gcDelta=1.0, allocKB=-50000.0, paused=1.0)    # in a paused window, which the steady state leaves out
            if i == 4:
                s.slow_frame(5000.0, gcDelta=1.0, allocKB=500.0)                   # a collection that freed less than the frame allocated
            s.window(allocKB=1000.0, paused=10 * 1000.0 / 16.7 if i == 1 else 0.0)
        text = self.report(s)
        section = text.split("7. GARBAGE COLLECTION")[1].split("8. WHAT EACH SOURCE")[0]
        self.assertIn("allocated about 100 KB per second", section, "(6000 - 500) KB over the 55 s whose allocation was measured")
        self.assertIn("at least 2 frames in the whole session", section, "the count says it is the whole session's")
        self.assertIn("leaves out the 1 slow frame (5.0 s) of them in these windows", section, "and how many of them are in the windows the figures cover")

    def test_hitches_on_a_rhythm_are_matched_to_the_autosave(self):
        s = Synthetic()
        for i in range(12):
            s.window()
            s.utc += 0.0
            s.slow_frame(180.0, saving=1.0)
            s.events.append({"utcMs": s.utc, "kind": "save", "ms": 170.0, "detail": "autosave"})
        text = self.report(s)
        self.assertIn("Slow frames come every", text)
        self.assertIn("the game's autosave", text)


if __name__ == "__main__":
    unittest.main()
