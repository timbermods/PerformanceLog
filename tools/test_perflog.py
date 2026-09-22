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
        # A watched method keeps its class (a patch method is nearly always called Prefix or Postfix), and drops its parameters only if it must.
        self.assertEqual("TickableEntityTickPatcher.Prefix(TickableEntity)",
                         perflog.short_method("BeaverBuddies.DeterminismService+TickableEntityTickPatcher.Prefix(TickableEntity)", 58))
        self.assertEqual("VeryLongClassNameForTesting.Method(...)",
                         perflog.short_method("A.B.VeryLongClassNameForTesting.Method(Int32,String,Boolean,Single,Double,Int64)", 40))
        self.assertEqual("Some.Mod.Method()", perflog.short_method("Some.Mod.Method()"))

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
        self.assertNotIn("KNOWN ISSUE", self.report(s), "the version that fixed them has none")
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

    def test_auto_watched_patch_methods_say_which_hot_method_they_are_on(self):
        s = Synthetic(header={"mod": "0.1.3"})
        # What the mod writes with AutoWatch = true: a # watch| line for each patch method it chose, and profile.csv rows named like a Watch entry
        # (whose commas the CSV turns into ;).
        entity = "BeaverBuddies.DeterminismService+TickableEntityTickPatcher.Prefix(TickableEntity)"
        panel = "LateGamePerformance.UiThrottle.PanelPrefix(EntityPanel,Boolean)"
        s.pipes.append(["watch", entity, "watching", "auto", "prefix on Timberborn.TickSystem.TickableEntity.Tick", "timbermods.BeaverBuddiesMultiColony"])
        s.pipes.append(["watch", panel, "watching", "auto", "prefix on Timberborn.EntityPanelSystem.EntityPanel.UpdateSingleton", "kyler.lategameperformance.UiThrottle"])
        s.pipes.append(["watch", "Some.Mod.Method()", "watching"])
        for w in range(1, 7):
            s.window()
            for i, (name, assembly) in enumerate(((entity, "BeaverBuddies"), (panel.replace(",", ";"), "LateGamePerformance"), ("Some.Mod.Method()", "SomeMod"))):
                s.profile.append({"kind": "method", "window": w, "tick": s.tick, "id": 5 + i, "calls": 1000, "sampled": 50, "ms": 20.0 - i, "allocKB": 0,
                                  "maxMs": 0.1, "name": name, "assembly": assembly, "mod": assembly.lower()})
        text = self.report(s)
        # A patch method is named by its class, not only as "Prefix", and says which hot method it is on and whose patch it is.
        self.assertIn("TickableEntityTickPatcher.Prefix(TickableEntity)", text)
        self.assertIn("auto watch: prefix on Timberborn.TickSystem.TickableEntity.Tick (timbermods.BeaverBuddiesMultiColony)", text)
        self.assertIn("auto watch: prefix on Timberborn.EntityPanelSystem.EntityPanel.UpdateSingleton (kyler.lategameperformance.UiThrottle)", text)
        self.assertRegex(text, r"Some\.Mod\.Method\(\)")
        self.assertEqual(2, text.count("auto watch: "), "a method the config's Watch named is not called an auto watch")

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
