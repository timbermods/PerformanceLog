using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using static PerformanceLog.Tests.Assert;

namespace PerformanceLog.Tests
{
    /// <summary>The probe running on this thread, with a clock and an allocation counter the test moves by hand.</summary>
    internal sealed class Rig : IDisposable
    {
        public long Now = Ms(1000);
        public long Bytes;
        public readonly Ring Frames = new Ring(Columns.Count, 512);
        public readonly Ring Prof = new Ring(Profile.Table.Count, 512);
        public readonly Ring Spikes = new Ring(Profile.SpikeTable.Count, 512);

        public static long Ms(double milliseconds) => (long)(milliseconds * Stopwatch.Frequency / 1000.0);

        public Rig(double thresholdMs = 50, double summarySeconds = 100000, double profileSeconds = 100000, int spikeTop = 5)
        {
            Probe.Stop();
            Probe.TestClock = () => Now;
            Alloc.UseTestSource(() => Bytes);
            Probe.ScopePairTicks = 0; Probe.SamplePairTicks = 0; Probe.ExactCallTicks = 0; Probe.AllocPairTicks = 0; Probe.PatchCallTicks = 0;
            Probe.HeavySampler = null;
            Probe.Start(new ProbeSettings
            {
                Frames = Frames, Profile = Prof, Spikes = Spikes, GameThreadId = Environment.CurrentManagedThreadId,
                ThresholdMs = thresholdMs, SummarySeconds = summarySeconds, ProfileSeconds = profileSeconds, SpikeContributors = spikeTop,
            });
            Frame(); // the first call only sets the clocks
        }

        public void Advance(double milliseconds) => Now += Ms(milliseconds);

        public void Frame(float speed = 1f, bool focused = true) => Probe.OnFrame(speed, focused);

        public static List<double[]> Drain(Ring ring, int columns)
        {
            var buffer = new double[512 * columns];
            int count = ring.Drain(buffer, 512);
            var rows = new List<double[]>();
            for (int i = 0; i < count; i++) rows.Add(buffer.AsSpan(i * columns, columns).ToArray());
            return rows;
        }

        public List<double[]> FrameRows() => Drain(Frames, Columns.Count);
        public List<double[]> ProfileRows() => Drain(Prof, Profile.Table.Count);
        public List<double[]> SpikeRows() => Drain(Spikes, Profile.SpikeTable.Count);

        public void Dispose()
        {
            Probe.Stop();
            Probe.TestClock = null;
            Probe.HeavySampler = null;
            Alloc.Init();
        }
    }

    internal static class CoreTests
    {
        public static IEnumerable<(string, Action)> All()
        {
            yield return ("Columns: names are unique and the groups match their sizes", ColumnsAreConsistent);
            yield return ("Columns: every column has a unit and a meaning, and the glossary lists them all", ColumnsAreDocumented);
            yield return ("Columns: indexes point at the columns of the same name", ColumnIndexesMatchNames);
            yield return ("Table: numbers are written the invariant way whatever the language", InvariantFormatting);
            yield return ("Table: words, text and tail columns, and a row that does not fit", WordsAndTails);
            yield return ("Ring: rows come out oldest first, wrap around, and a full ring drops and counts", RingBehaviour);
            yield return ("Probe: time is exclusive, so slots never overlap and add up to the frame", ExclusiveTime);
            yield return ("Probe: an inner scope that never ended is closed by the outer one", UnbalancedScopes);
            yield return ("Probe: time measured elsewhere is added to a slot and taken out of the open scope", AddNested);
            yield return ("Probe: only the game thread is timed", OtherThreadsIgnored);
            yield return ("Probe: allocation is charged to the slot that made it, nested slots taken out", AllocationAttribution);
            yield return ("Probe: only slow frames get a row; summaries average times and total counts", SlowRowsAndSummaries);
            yield return ("Probe: speed, pause, save and focus are recorded and counted", FlagsAreRecorded);
            yield return ("Probe: ticks, buckets and their histogram", TicksAndBuckets);
            yield return ("Probe: Unity phases are timed from their markers", PhaseMarks);
            yield return ("Probe: the frame path allocates nothing", FramePathAllocatesNothing);
            yield return ("Probe: a failing clock switches the probe off instead of reaching the caller", FailureSwitchesOff);
            yield return ("Probe: nothing is recorded and Begin is free when the probe is off", OffIsFree);
            yield return ("Probe: a full ring drops rows and says so in the dropped column", DroppedRowsAreCounted);
            yield return ("Probe: the slowest frames are kept in order with their biggest contributors", WorstFramesKept);
            yield return ("Probe: session counts (speed mix, slow frames with a collection) add up", SessionStatsAdd);
            yield return ("Probe: the last colony and memory readings carry over frames and windows", HeavyReadingsPersist);
            yield return ("Probe: every summary window is remembered for the view over time", WindowsAreKept);
            yield return ("Probe: measuring cost is estimated for each frame", OverheadEstimate);
            yield return ("Probe: calibration measures something and leaves the probe clean", CalibrationWorks);
            yield return ("Alloc: a counter is chosen, and a test source is followed", AllocSources);
            yield return ("Milestones: marks are kept in order and safe for the header", MilestoneLines);
            yield return ("PatchBuilder: hot, shared and other patches are listed, the mod's own are only counted", PatchReport);
            yield return ("PatchBuilder: a long report is cut and says how much", PatchReportTruncates);
        }

        // ---- columns ----

        static void ColumnsAreConsistent()
        {
            Equal(Columns.Count, Columns.Names.Length);
            Equal(Columns.Count, Columns.Aggregates.Length);
            Equal(Columns.Count, Columns.HeaderLine().Split(',').Length);
            Check(Columns.Names.Distinct().Count() == Columns.Count, "column names must be unique");
            Equal(Columns.SlotCount, Columns.SlotTimeNames.Length);
            Equal(Columns.SlotCount, Columns.SlotAllocNames.Length);
            Equal(Columns.SlotCount, Enum.GetValues(typeof(Slot)).Length);
            Equal(Columns.CounterCount, Enum.GetValues(typeof(Counter)).Length);
            Equal(Columns.CounterCount, Columns.CounterNames.Length);
            Equal(Columns.PhaseCount, Columns.PhaseNames.Length);
            Equal(Columns.ExtraPerFrameCount, Columns.ExtraPerFrameNames.Length);
            Equal(Columns.ExtraHeavyCount, Columns.ExtraHeavyNames.Length);
            Equal(Columns.SlotCount, Columns.SlotTracksAlloc.Length);
            Equal(ProfileKinds.Words.Length, Enum.GetValues(typeof(ProfileKind)).Length);
            Equal(ProfileKinds.Meaning.Length, ProfileKinds.Words.Length);
            Equal(Columns.FrameHistCount, Columns.FrameEdgesMs.Length + 1);
            Check(Profile.Table.Names.Distinct().Count() == Profile.Table.Names.Length, "profile column names must be unique");
            Check(Profile.SpikeTable.Names.Distinct().Count() == Profile.SpikeTable.Names.Length, "spike column names must be unique");
            Equal(9, Profile.Table.Count, "profile numeric columns");
            Equal(12, Profile.Table.Names.Length, "profile columns with the text tail");
            Equal(9, Profile.SpikeTable.Count, "spike numeric columns");
        }

        static void ColumnsAreDocumented()
        {
            foreach (Table table in new[] { Columns.Main, Profile.Table, Profile.SpikeTable })
            {
                string glossary = table.GlossaryMarkdown();
                foreach (Column c in table.Columns)
                {
                    Check(!string.IsNullOrWhiteSpace(c.Description), c.Name + " needs a description");
                    Check(c.Unit != null, c.Name + " needs a unit (may be empty)");
                    Check(glossary.Contains("`" + c.Name + "`"), c.Name + " is missing from the glossary");
                }
            }
        }

        static void ColumnIndexesMatchNames()
        {
            Equal("frameMs", Columns.Names[Columns.FrameMs]);
            Equal("otherMs", Columns.Names[Columns.OtherMs]);
            Equal("otherKB", Columns.Names[Columns.OtherKB]);
            Equal("tickMs", Columns.Names[Columns.SlotBase + (int)Slot.Tick]);
            Equal("saveMs", Columns.Names[Columns.SlotBase + (int)Slot.Save]);
            Equal("tickKB", Columns.Names[Columns.AllocBase]);
            Equal("plPost", Columns.Names[Columns.PhaseBase + 7]);
            Equal("prGcBytes", Columns.Names[Columns.ExtraBase]);
            Equal("monoHeapMB", Columns.Names[Columns.HeavyBase]);
            Equal("colDay", Columns.Names[Columns.HeavyBase + Columns.ExtraHeavyCount - 1]);
            Equal("fh0", Columns.Names[Columns.FrameHistBase]);
            Equal("th5", Columns.Names[Columns.TickHistBase + 5]);
            Equal("entities", Columns.Names[Columns.CounterBase + (int)Counter.Entities]);
            Equal("parTickMs", Columns.Names[Columns.ParTickMs]);
            Equal("dropped", Columns.Names[Columns.Dropped]);
            Equal(Columns.Count - 1, Columns.TickHistBase + Columns.TickHistCount - 1);
        }

        // ---- text of rows ----

        static string Format(Table table, double[] row)
        {
            var buffer = new char[4096];
            Check(table.TryFormatRow(row, buffer, out int written), "the row should fit");
            return new string(buffer, 0, written);
        }

        static void InvariantFormatting()
        {
            CultureInfo saved = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                var row = new double[Columns.Count];
                row[Columns.Type] = 'F'; row[Columns.Frame] = 12; row[Columns.FrameMs] = 16.6789; row[Columns.AllocKB] = 1234.56;
                row[Columns.HeapMB] = double.NaN; row[Columns.Tick] = double.PositiveInfinity;
                string[] fields = Format(Columns.Main, row).Split(',');
                Equal(Columns.Count, fields.Length);
                Equal("F", fields[Columns.Type]);
                Equal("12", fields[Columns.Frame]);
                Equal("16.68", fields[Columns.FrameMs]);
                Equal("1234.6", fields[Columns.AllocKB]);
                Equal("0.0", fields[Columns.HeapMB]);
                Equal("0", fields[Columns.Tick]);
            }
            finally { CultureInfo.CurrentCulture = saved; }
        }

        static void WordsAndTails()
        {
            var row = new double[Profile.Table.Count];
            row[0] = (int)ProfileKind.UpdateSingleton; row[1] = 3; row[3] = 17; row[6] = 2.5;
            Equal("update-singleton,3,0,17,0,0,2.50,0.0,0.00", Format(Profile.Table, row));
            row[0] = 99;
            Check(Format(Profile.Table, row).StartsWith("?,"), "an unknown word is written as ?");
            var tiny = new char[8];
            Check(!Profile.Table.TryFormatRow(row, tiny, out _), "a row that does not fit is refused, not cut");
            var text = new System.Text.StringBuilder();
            int id = Profile.IdFor(ProfileKind.Entity, "Beaver, \"Adult\"|x");
            row[3] = id;
            Profile.AppendProfileText(row, text);
            Equal(",Beaver; 'Adult' x,,", text.ToString());
        }

        // ---- ring ----

        static void RingBehaviour()
        {
            var ring = new Ring(2, 3);
            for (int i = 1; i <= 3; i++) Check(ring.TryPush(new double[] { i, i * 10 }));
            Check(!ring.TryPush(new double[] { 4, 40 }), "a full ring refuses");
            Equal(1L, ring.Dropped);
            var buffer = new double[4];
            Equal(2, ring.Drain(buffer, 2));
            Equal(1.0, buffer[0]); Equal(20.0, buffer[3]);
            Check(ring.TryPush(new double[] { 5, 50 }));
            Check(ring.TryPush(new double[] { 6, 60 }));
            var all = new double[8];
            Equal(3, ring.Drain(all, 4));
            Equal(3.0, all[0]); Equal(5.0, all[2]); Equal(6.0, all[4]);
            Equal(0, ring.Count);
        }

        // ---- probe ----

        static double[] OnlyRow(Rig rig, char type)
        {
            List<double[]> rows = rig.FrameRows().Where(r => r[Columns.Type] == type).ToList();
            Equal(1, rows.Count, "rows of type " + type);
            return rows[0];
        }

        static void ExclusiveTime()
        {
            using (var rig = new Rig(thresholdMs: 1))
            {
                long tick = Probe.Begin(Slot.Tick);
                rig.Advance(4);
                long sing = Probe.Begin(Slot.Singletons);
                rig.Advance(10);
                Probe.End(sing);
                rig.Advance(6);
                Probe.End(tick);
                rig.Advance(30);
                rig.Frame();
                double[] r = OnlyRow(rig, 'F');
                Near(50, r[Columns.FrameMs], .001);
                Near(10, r[Columns.SlotBase + (int)Slot.Tick], .001, "tick is its own 4+6 ms, not the singletons'");
                Near(10, r[Columns.SlotBase + (int)Slot.Singletons], .001);
                Near(30, r[Columns.OtherMs], .001);
                double sum = 0;
                for (int i = 0; i < Columns.SlotCount; i++) sum += r[Columns.SlotBase + i];
                Near(r[Columns.FrameMs], sum + r[Columns.OtherMs], .001, "slots and other add up to the frame");
            }
        }

        static void UnbalancedScopes()
        {
            using (var rig = new Rig(thresholdMs: 1))
            {
                long outer = Probe.Begin(Slot.Tick);
                rig.Advance(5);
                Probe.Begin(Slot.Entities); // never ended, as if an exception skipped it
                rig.Advance(7);
                Probe.End(outer);
                rig.Frame();
                double[] r = OnlyRow(rig, 'F');
                Near(5, r[Columns.SlotBase + (int)Slot.Tick], .001);
                Near(7, r[Columns.SlotBase + (int)Slot.Entities], .001);
                // A token from an earlier frame does nothing.
                long stale = Probe.Begin(Slot.Save);
                rig.Frame();
                Probe.End(stale);
                rig.Advance(3);
                rig.Frame();
                Near(0, rig.FrameRows().Where(x => x[Columns.Type] == 'F').Sum(x => x[Columns.SlotBase + (int)Slot.Save]), .001);
            }
        }

        static void AddNested()
        {
            using (var rig = new Rig(thresholdMs: 1))
            {
                long tick = Probe.Begin(Slot.Tick);
                rig.Advance(10);
                Probe.AddNested(Slot.Update, Rig.Ms(4));
                Probe.End(tick);
                rig.Frame();
                double[] r = OnlyRow(rig, 'F');
                Near(4, r[Columns.SlotBase + (int)Slot.Update], .001);
                Near(6, r[Columns.SlotBase + (int)Slot.Tick], .001, "the nested time comes out of the open scope");
            }
        }

        static void OtherThreadsIgnored()
        {
            using (var rig = new Rig(thresholdMs: 1))
            {
                long token = -1;
                var thread = new Thread(() => token = Probe.Begin(Slot.Tick));
                thread.Start(); thread.Join();
                Equal(0L, token);
                Check(Probe.OnGameThread, "the test thread is the game thread");
                bool otherThreadSays = true;
                var second = new Thread(() => otherThreadSays = Probe.OnGameThread);
                second.Start(); second.Join();
                Check(!otherThreadSays, "another thread is not the game thread");
            }
        }

        static void AllocationAttribution()
        {
            using (var rig = new Rig(thresholdMs: 1))
            {
                long tick = Probe.Begin(Slot.Tick);
                rig.Bytes += 1024;
                long sing = Probe.Begin(Slot.Singletons);
                rig.Bytes += 4096;
                Probe.End(sing);
                rig.Bytes += 2048;
                Probe.End(tick);
                rig.Bytes += 512; // outside every slot
                rig.Advance(5);
                rig.Frame();
                double[] r = OnlyRow(rig, 'F');
                Near(3, r[Columns.AllocBase + (int)Slot.Tick], .001, "1 KB before and 2 KB after the nested part");
                Near(4, r[Columns.AllocBase + (int)Slot.Singletons], .001);
                Near(0.5, r[Columns.OtherKB], .001);
            }
        }

        static void SlowRowsAndSummaries()
        {
            using (var rig = new Rig(thresholdMs: 50, summarySeconds: 1))
            {
                for (int i = 0; i < 4; i++) { rig.Advance(20); rig.Frame(); }      // 4 ordinary frames: 80 ms
                rig.Advance(120); rig.Frame();                                       // one slow frame
                rig.Advance(500); rig.Advance(400); rig.Frame();                     // pushes the clock past one second: a summary
                List<double[]> rows = rig.FrameRows();
                Equal(2, rows.Count(r => r[Columns.Type] == 'F'), "the 120 ms and the 900 ms frames");
                double[] s = rows.Single(r => r[Columns.Type] == 'S');
                Equal(6.0, s[Columns.Frames]);
                Near((4 * 20 + 120 + 900) / 6.0, s[Columns.FrameMs], .01, "the mean frame time");
                Near(900, s[Columns.MaxFrameMs], .001);
                Near(1, s[Columns.FrameHistBase + Columns.FrameBucket(120)] , 0.001, "one frame of 120 ms in its bucket");
                Near(4, s[Columns.FrameHistBase + Columns.FrameBucket(20)], .001, "four frames of 20 ms in theirs");
                Near(6, Enumerable.Range(0, Columns.FrameHistCount).Sum(i => s[Columns.FrameHistBase + i]), .001, "the histogram covers every frame");
                Near(6, s[Columns.Frames], .001);
            }
        }

        static void FlagsAreRecorded()
        {
            using (var rig = new Rig(thresholdMs: 1))
            {
                rig.Advance(10);
                long save = Probe.Begin(Slot.Save);
                rig.Advance(5);
                Probe.End(save);
                rig.Frame(speed: 0f, focused: false);
                double[] r = OnlyRow(rig, 'F');
                Equal(1.0, r[Columns.Paused]);
                Equal(1.0, r[Columns.Saving]);
                Equal(1.0, r[Columns.Unfocused]);
                Equal(0.0, r[Columns.Speed]);
                rig.Advance(10);
                rig.Frame(speed: 3f, focused: true);
                double[] second = rig.FrameRows().Single(x => x[Columns.Type] == 'F');
                Equal(0.0, second[Columns.Paused]);
                Equal(0.0, second[Columns.Saving]);
                Equal(0.0, second[Columns.Unfocused]);
                Equal(3.0, second[Columns.Speed]);
            }
        }

        static void TicksAndBuckets()
        {
            using (var rig = new Rig(thresholdMs: 1))
            {
                rig.Advance(30);
                for (int i = 0; i < 3; i++) Probe.NoteTickStart();
                for (int i = 0; i < 129; i++) Probe.NoteBucket();
                rig.Frame();
                double[] r = OnlyRow(rig, 'F');
                Equal(3.0, r[Columns.Ticks]);
                Equal(129.0, r[Columns.Buckets]);
                Equal(3.0, r[Columns.Tick]);
                Equal(1.0, r[Columns.TickHistBase + 3], "three ticks fall in the 3-4 bucket");
                rig.Advance(30);
                rig.Frame();
                double[] next = rig.FrameRows().Single();
                Equal(0.0, next[Columns.Ticks]);
                Equal(3.0, next[Columns.Tick], "the tick count carries on");
                Equal(1.0, next[Columns.TickHistBase]);
                Equal(0, Columns.TickBucket(0)); Equal(2, Columns.TickBucket(2)); Equal(3, Columns.TickBucket(4)); Equal(4, Columns.TickBucket(9)); Equal(5, Columns.TickBucket(10));
            }
        }

        static void PhaseMarks()
        {
            using (var rig = new Rig(thresholdMs: 1))
            {
                Probe.PhaseMark(5, true); rig.Advance(12); Probe.PhaseMark(5, false);
                Probe.PhaseMark(7, true); rig.Advance(3); Probe.PhaseMark(7, false);
                Probe.PhaseMark(7, false); // an end without a start does nothing
                Probe.PhaseMark(99, true); // an unknown phase does nothing
                rig.Frame();
                double[] r = OnlyRow(rig, 'F');
                Near(12, r[Columns.PhaseBase + 5], .001);
                Near(3, r[Columns.PhaseBase + 7], .001);
                Near(0, r[Columns.PhaseBase], .001);
            }
        }

        static void FramePathAllocatesNothing()
        {
            using (var rig = new Rig(thresholdMs: 1000))
            {
                for (int i = 0; i < 50; i++) { long t = Probe.Begin(Slot.Tick); rig.Advance(1); Probe.End(t); rig.Advance(15); rig.Frame(); }
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 2000; i++)
                {
                    long t = Probe.Begin(Slot.Tick);
                    long u = Probe.Begin(Slot.Update);
                    rig.Now += 1000; Probe.End(u);
                    Probe.NoteTickStart(); Probe.NoteBucket(); Probe.Count(Counter.Entities, 5);
                    Probe.PhaseMark(5, true); Probe.PhaseMark(5, false);
                    Probe.End(t);
                    rig.Now += 100000;
                    rig.Frame();
                }
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Equal(0L, allocated, "bytes allocated by 2000 ordinary frames");
            }
        }

        static void FailureSwitchesOff()
        {
            var rig = new Rig(thresholdMs: 1);
            try
            {
                Probe.TestClock = () => throw new InvalidOperationException("clock failed");
                Probe.OnFrame(1f, true);            // must not throw
                Check(!Probe.Enabled, "the probe switches itself off");
                Check(Probe.LastFailure != null && Probe.LastFailure.Contains("clock failed"), "and says why");
                Equal(0L, Probe.Begin(Slot.Tick));
            }
            finally { rig.Dispose(); }
        }

        static void OffIsFree()
        {
            Probe.Stop();
            Check(!Probe.Enabled);
            Equal(0L, Probe.Begin(Slot.Tick));
            Probe.End(0);
            Probe.Count(Counter.Entities);
            Probe.NoteTickStart();
            Probe.OnFrame(1f, true);
            Equal(0, Probe.TickCount);
            Equal(0, Probe.FrameNumber);
        }

        static void DroppedRowsAreCounted()
        {
            var rig = new Rig(thresholdMs: 1);
            try
            {
                var tiny = new Ring(Columns.Count, 2);
                Probe.Start(new ProbeSettings { Frames = tiny, Profile = rig.Prof, Spikes = rig.Spikes, GameThreadId = Environment.CurrentManagedThreadId, ThresholdMs = 1, SummarySeconds = 100000 });
                rig.Frame();
                for (int i = 0; i < 6; i++) { rig.Advance(10); rig.Frame(); }
                Equal(4L, tiny.Dropped);
                var rows = Rig.Drain(tiny, Columns.Count);
                Equal(2, rows.Count);
                Equal(2.0, rows[1][Columns.Frame]);
                Check(rows[1][Columns.Dropped] >= 0, "the column exists");
            }
            finally { rig.Dispose(); }
        }

        static void WorstFramesKept()
        {
            using (var rig = new Rig(thresholdMs: 50))
            {
                int id = Profile.IdFor(ProfileKind.UpdateSingleton, "Mod.Culprit");
                double[] sizes = { 60, 300, 80, 55, 900, 70, 65, 52, 200, 150, 110, 95, 500 };
                foreach (double size in sizes)
                {
                    Timing t = Profile.BeginExact();
                    rig.Advance(size / 2);
                    Profile.EndExact(id, t);
                    rig.Advance(size / 2);
                    rig.Frame();
                }
                List<WorstFrame> worst = Probe.WorstFrames();
                Equal(10, worst.Count);
                Equal(900.0, worst[0].Row[Columns.FrameMs]);
                Equal(500.0, worst[1].Row[Columns.FrameMs]);
                Check(worst.Zip(worst.Skip(1), (a, b) => a.Row[Columns.FrameMs] >= b.Row[Columns.FrameMs]).All(x => x), "slowest first");
                Equal(65.0, worst[9].Row[Columns.FrameMs]);
                Equal(1, worst[0].TopIds.Length);
                Near(450, worst[0].TopMs[0], .001, "the singleton spent half the frame");
            }
        }

        static void SessionStatsAdd()
        {
            using (var rig = new Rig(thresholdMs: 50))
            {
                rig.Advance(16); rig.Frame(speed: 1f);
                rig.Advance(16); rig.Frame(speed: 3f);
                rig.Advance(16); rig.Frame(speed: 3f);
                rig.Advance(100); rig.Frame(speed: 0f);
                rig.Advance(100); rig.Frame(speed: 3f, focused: false);
                SessionStats s = Probe.Stats;
                Equal(1L, s.SpeedFrames[1]); Equal(3L, s.SpeedFrames[3]); Equal(1L, s.SpeedFrames[0]);
                Equal(2L, s.SlowFrames);
                Equal(1L, s.SlowPausedFrames);
                Equal(1L, s.SlowUnfocusedFrames);
                Near(200, s.SlowMs, .001);
                Near(16, s.FirstFrameMs, .001);
                double[] session = Probe.SessionRow();
                Equal(5.0, session[Columns.Frames]);
                Near((16 * 3 + 200) / 5.0, session[Columns.FrameMs], .01);
                Equal((double)'S', session[Columns.Type]);
            }
        }

        static void HeavyReadingsPersist()
        {
            using (var rig = new Rig(thresholdMs: 1000, summarySeconds: 1))
            {
                int reads = 0;
                Probe.HeavySampler = extra =>
                {
                    reads++;
                    extra[Columns.ExtraPerFrameCount + 4] = 9000 + reads;   // colEntities
                    extra[Columns.ExtraPerFrameCount + 3] = 3000;           // workingMB
                };
                for (int i = 0; i < 250; i++) { rig.Advance(20); rig.Frame(); }   // five seconds, five windows
                List<double[]> summaries = rig.FrameRows().Where(r => r[Columns.Type] == 'S').ToList();
                Check(summaries.Count >= 4, "several windows were written");
                foreach (double[] s in summaries)
                {
                    Equal(3000.0, s[Columns.HeavyBase + 3], "working set in every window");
                    Check(s[Columns.HeavyBase + 4] > 9000, "colony size in every window, not zero after the first");
                }
                double[] session = Probe.SessionRow();
                Check(session[Columns.HeavyBase + 4] > 9000, "and in the session row");
                Equal((long)(summaries.Count), (long)reads, "the heavy figures were read once per window, not every frame");
            }
        }

        static void WindowsAreKept()
        {
            using (var rig = new Rig(thresholdMs: 1000, summarySeconds: 1))
            {
                for (int i = 0; i < 300; i++)
                {
                    if (i % 10 == 0) Probe.NoteTickStart();
                    rig.Advance(20); rig.Frame(speed: i < 150 ? 1f : 3f);
                }
                List<WindowStat> windows = Probe.Windows();
                Check(windows.Count >= 5, "windows were kept: " + windows.Count);
                Check(windows.Zip(windows.Skip(1), (a, b) => a.UtcMs < b.UtcMs).All(x => x), "oldest first");
                WindowStat first = windows[0], last = windows[windows.Count - 1];
                Equal(1.0, first.Speed); Equal(3.0, last.Speed);
                Near(20, first.FrameMs, .01);
                Check(first.Frames > 0 && first.Seconds > 0.9 && first.Seconds < 1.1, "a window covers about a second: " + first.Seconds);
            }
        }

        static void OverheadEstimate()
        {
            using (var rig = new Rig(thresholdMs: 1))
            {
                Probe.ScopePairTicks = Rig.Ms(0.001); // 1 us per timed scope
                for (int i = 0; i < 10; i++) { long t = Probe.Begin(Slot.Tick); Probe.End(t); }
                rig.Advance(10);
                rig.Frame();
                double[] r = OnlyRow(rig, 'F');
                Near(10.0, r[Columns.OverheadUs], .01, "ten scopes at a microsecond each");
                Check(r[Columns.ProbeUs] >= 0);
            }
        }

        static void CalibrationWorks()
        {
            Probe.Stop();
            Alloc.Init();
            Probe.Calibrate();
            Check(Probe.LastFailure == null, "calibration failed: " + Probe.LastFailure);
            Check(Probe.ClockReadTicks > 0, "a clock read costs something");
            Check(Probe.ScopePairTicks > 0 && Probe.SamplePairTicks > 0 && Probe.ExactCallTicks > 0, "each cost was measured");
            Check(Probe.SamplePairTicks >= Probe.ClockReadTicks, "a sampled pair costs at least a clock read");
            Equal(0, Profile.Count, "calibration leaves no keys behind");
            Check(!Probe.Enabled, "and does not switch the probe on");
        }

        // ---- allocation counter, milestones ----

        static void AllocSources()
        {
            try
            {
                Alloc.Init();
                Check(Alloc.Enabled, "some counter works in this process");
                long a = Alloc.Read();
                var garbage = new byte[100000];
                garbage[0] = 1;
                Check(Alloc.Read() - a >= 90000, "the counter noticed 100 KB");
                GC.KeepAlive(garbage);
                Alloc.Init(preferHeap: true);
                Equal(Alloc.ModeHeap, Alloc.Mode);
                long bytes = 5;
                Alloc.UseTestSource(() => bytes);
                bytes += 100;
                Equal(105L, Alloc.Read());
                Check(Alloc.MeasureReadTicks(100) >= 0);
            }
            finally { Alloc.Init(); }
        }

        static void MilestoneLines()
        {
            Milestones.Clear();
            Milestones.Mark("mod-started");
            Milestones.Mark("a|b\nc");
            List<string> lines = Milestones.Lines();
            Equal(2, lines.Count);
            string[] parts = lines[0].Split('|');
            Equal("# milestone", parts[0]);
            Equal("mod-started", parts[1]);
            Equal(6, parts.Length);
            Check(lines[1].Contains("a/b c"), "the separator and the line break are removed");
            for (int i = 0; i < 100; i++) Milestones.Mark("x");
            Check(Milestones.Lines().Count <= 48, "the list is bounded");
            Milestones.Clear();
        }

        // ---- patch report ----

        static PatchRecord P(string kind, string owner, int priority = 400, string assembly = "SomeMod") =>
            new PatchRecord { Kind = kind, Owner = owner, Priority = priority, Assembly = assembly, PatchMethod = assembly + ".Patch", Before = new string[0], After = new[] { "x" } };

        static PatchedMethodRecord M(string type, string method, params PatchRecord[] patches) =>
            new PatchedMethodRecord { TypeName = type.Substring(type.LastIndexOf('.') + 1), MethodName = method, FullName = type + "." + method, Patches = patches.ToList() };

        static void PatchReport()
        {
            var lines = new List<string>();
            PatchBuilder.Build(new[]
            {
                M("Timberborn.TickSystem.Ticker", "Update", P("prefix", "other.mod")),                           // hot, another mod's
                M("Timberborn.TickSystem.TickableEntity", "Tick", P("prefix", "kyler.performancelog")),          // hot, only ours
                M("Timberborn.Foo.Bar", "Baz", P("prefix", "a.mod"), P("postfix", "b.mod")),                     // shared
                M("Timberborn.Foo.Bar", "Only", P("postfix", "c.mod")),                                          // other
                M("Timberborn.Foo.Bar", "Ours", P("postfix", "kyler.performancelog.extra")),                     // ours, not hot: counted only
            }, "kyler.performancelog", lines);
            Check(lines[0].StartsWith("# patches: methods=5 hot=2 shared=1 other=1 ownedOnlyByThisMod=1"), lines[0]);
            Check(lines.Any(l => l.StartsWith("# patch|hot|Timberborn.TickSystem.Ticker.Update|prefix|other.mod|priority=400|index=0|before=|after=x|SomeMod|SomeMod.Patch")));
            Check(lines.Any(l => l.StartsWith("# patch|hot|Timberborn.TickSystem.TickableEntity.Tick|prefix|kyler.performancelog")), "hot methods are listed even when the mod patched them");
            Equal(2, lines.Count(l => l.StartsWith("# patch|shared|")));
            Check(lines.Any(l => l.StartsWith("# patch|other|Timberborn.Foo.Bar.Only")));
            Check(!lines.Any(l => l.Contains("Bar.Ours")), "the mod's own non-hot patches are not listed");
            Check(lines.Any(l => l == "# patch-owner|a.mod|methods=1"));
            Check(PatchFormat.IsHot("Ticker", "Update") && !PatchFormat.IsHot("Ticker", "Nothing"));
            Check(PatchFormat.HotMethodCount > 30);
            Equal("a/b c", PatchFormat.Clean(" a|b\tc\n"));
        }

        static void PatchReportTruncates()
        {
            var methods = new List<PatchedMethodRecord>();
            for (int i = 0; i < 700; i++) methods.Add(M("Timberborn.Foo.Type" + i.ToString("D3"), "M", P("prefix", "x.mod")));
            var lines = new List<string>();
            PatchBuilder.Build(methods, "kyler.performancelog", lines);
            Equal(PatchBuilder.MaxDetailLines, lines.Count(l => l.StartsWith("# patch|")));
            Check(lines.Last() == "# patches-truncated: 100 more patch lines not shown", lines.Last());
        }
    }
}
