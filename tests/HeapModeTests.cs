using System;
using System.Collections.Generic;
using System.Linq;
using static PerformanceLog.Tests.Assert;

namespace PerformanceLog.Tests
{
    /// <summary>
    /// Allocation read from the size of the managed heap (the only source the game's Mono offers, see the allocSource capability line) falls at
    /// a garbage collection, so a frame that has one loses what it allocated. Such a frame must be counted as not measured, not read as having
    /// allocated nothing, and without a new column: the count goes into the capability-final line and the summary.
    /// </summary>
    internal static class HeapModeTests
    {
        public static IEnumerable<(string, Action)> All()
        {
            yield return ("Heap mode: a frame whose allocation counter falls at a collection is marked as not measured, not read as 0", CounterThatFalls);
            yield return ("Heap mode: a frame with a collection is not measured even when the heap still grew; an exact counter is not affected", CollectionWithGrowth);
            yield return ("Heap mode: the summary's allocation per second leaves out the frames that were not measured", SummaryRate);
        }

        static void CounterThatFalls()
        {
            // The heap size is the counter the game gets. No collection may run while it is read here, or the test process's own collections
            // would count as well: the region is opened before the rig, so a collection it needs first happens before the probe's first reading.
            bool noGc = StartNoGc();
            var rig = new Rig();
            try
            {
                Alloc.UseTestSource(() => rig.Bytes, asHeapSize: true);
                Equal(Alloc.ModeHeap, Alloc.Mode);
                rig.Bytes = 500L * 1024 * 1024;
                rig.Advance(10); rig.Frame();
                rig.FrameRows();
                rig.Bytes += 300 * 1024;          // 300 KB allocated outside every section
                rig.Bytes -= 100L * 1024 * 1024;  // then a collection frees 100 MB, and the heap-size counter falls
                rig.Advance(60); rig.Frame();     // slow, so it gets an F row
                double[] f = rig.FrameRows().Single(r => r[Columns.Type] == Columns.FrameRow);
                string summary = Summary.Render(new SummaryInput { SessionId = "gc", Row = Probe.SessionRow(), Stats = Probe.Stats, Seconds = 1 });
                Check(f[Columns.OtherKB] > 0 || summary.Contains("not measured in 1 frame"),
                    "the frame with the collection reads otherKB " + f[Columns.OtherKB] + " and nothing says its allocation was not measured");
                if (noGc)
                {
                    Equal(1L, Probe.Stats.AllocUnmeasuredFrames, "frames counted as not measured");
                    Near(60, Probe.Stats.AllocUnmeasuredMs, .001, "and their time");
                    Check(Probe.AllocFinalLine().StartsWith("# capability-final|allocSource|heap size|allocation not measured in 1 frame (0.1 s) with a garbage collection"),
                        Probe.AllocFinalLine());
                }
                long before = Probe.Stats.AllocUnmeasuredFrames;

                // A counter that also falls inside a timed part, with the frame as a whole still growing, is caught there.
                long scope = Probe.Begin(Slot.Update);
                rig.Bytes -= 1024 * 1024;
                Probe.End(scope);
                rig.Bytes += 4 * 1024 * 1024;
                rig.Advance(16); rig.Frame();
                Equal(before + 1, Probe.Stats.AllocUnmeasuredFrames, "a fall inside a timed part");

                // Ordinary frames are measured, and not counted.
                if (noGc)
                {
                    for (int i = 0; i < 5; i++) { rig.Bytes += 64 * 1024; rig.Advance(16); rig.Frame(); }
                    Equal(before + 1, Probe.Stats.AllocUnmeasuredFrames, "frames whose counter only grew");
                }
            }
            finally { rig.Dispose(); EndNoGc(noGc); }

            noGc = StartNoGc();
            rig = new Rig();
            try
            {
                Alloc.UseTestSource(() => rig.Bytes, asHeapSize: true);
                for (int i = 0; i < 5; i++) { rig.Bytes += 64 * 1024; rig.Advance(16); rig.Frame(); }
                if (noGc) Equal("# capability-final|allocSource|heap size|measured in every frame", Probe.AllocFinalLine());
            }
            finally { rig.Dispose(); EndNoGc(noGc); }

            rig = new Rig();
            try
            {
                for (int i = 0; i < 5; i++) { rig.Bytes += 64 * 1024; rig.Advance(16); rig.Frame(); }
                Equal("# capability-final|allocSource|exact|measured in every frame", Probe.AllocFinalLine());
            }
            finally { rig.Dispose(); }
        }

        // A region in which the runtime runs no collection, as long as less than this much is allocated in it. False when it could not start one;
        // the checks that need it are then skipped rather than made to fail by a collection of the test process.
        static bool StartNoGc()
        {
            bool started;
            try { started = GC.TryStartNoGCRegion(64L * 1024 * 1024); }
            catch (InvalidOperationException) { started = false; }
            if (!started) Console.WriteLine("     info: the runtime would not hold off collections here, so the heap-mode counts are not checked");
            return started;
        }

        static void EndNoGc(bool started)
        {
            if (started && System.Runtime.GCSettings.LatencyMode == System.Runtime.GCLatencyMode.NoGCRegion)
                try { GC.EndNoGCRegion(); } catch (InvalidOperationException) { }
        }

        static void CollectionWithGrowth()
        {
            var rig = new Rig();
            try
            {
                // The exact counter does not lose anything at a collection, so a collection alone is not a reason to doubt it.
                rig.Bytes += 64 * 1024; rig.Advance(16); rig.Frame();
                GC.Collect(0);
                rig.Bytes += 64 * 1024; rig.Advance(16); rig.Frame();
                Equal(0L, Probe.Stats.AllocUnmeasuredFrames, "a collection under the exact counter");

                // The heap size: a collection that freed less than the frame allocated still took some of it away.
                Alloc.UseTestSource(() => rig.Bytes, asHeapSize: true);
                Equal(Alloc.ModeHeap, Alloc.Mode);
                rig.Bytes += 64 * 1024; rig.Advance(16); rig.Frame();
                rig.FrameRows();
                long before = Probe.Stats.AllocUnmeasuredFrames;
                double kbBefore = Probe.Stats.AllocUnmeasuredKB;
                GC.Collect(0);
                rig.Bytes += 64 * 1024; rig.Advance(60); rig.Frame();   // slow, so its F row shows the allocKB the frame was charged
                Check(Probe.Stats.AllocUnmeasuredFrames == before + 1, "a frame with a collection is not measured when the counter is the heap size");
                // Whatever growth the heap still showed in it is in the session's allocKB total, and counted here so the rate can take it out again.
                double[] f = rig.FrameRows().Single(r => r[Columns.Type] == Columns.FrameRow);
                Near(Math.Max(0, f[Columns.AllocKB]), Probe.Stats.AllocUnmeasuredKB - kbBefore, .001, "the lost frame's heap growth");
                Check(Probe.AllocFinalLine().Contains("|heap size|allocation not measured in") && Probe.AllocFinalLine().Contains("with a garbage collection"), Probe.AllocFinalLine());
            }
            finally { rig.Dispose(); }
        }

        static void SummaryRate()
        {
            var rig = new Rig(thresholdMs: 1000);
            try
            {
                for (int i = 0; i < 5; i++) { rig.Advance(16); rig.Frame(); }
                double[] row = Probe.SessionRow();
                row[Columns.AllocKB] = 6000;   // 6000 KB over 60 s
                SessionStats stats = Probe.Stats.Clone();
                string text = Summary.Render(new SummaryInput { SessionId = "rate", Row = row, Stats = stats, Seconds = 60 });
                Check(text.Contains("allocated about 100 KB per second"), "every frame measured: 6000 KB in 60 s");
                Check(!text.Contains("not measured in"), "and nothing said about unmeasured frames");
                stats.AllocUnmeasuredFrames = 2; stats.AllocUnmeasuredMs = 5000;
                text = Summary.Render(new SummaryInput { SessionId = "rate", Row = row, Stats = stats, Seconds = 60 });
                Check(text.Contains("allocated about 109 KB per second"), "6000 KB over the 55 s whose allocation was measured");
                Check(text.Contains("not measured in 2 frames"), "and the summary says why");
                Check(text.Contains("the slow ones are the `F` rows"), "and that only the slow ones have rows");
                // A frame whose collection freed less than it allocated still grew the heap; that growth is in the total but is not its allocation.
                stats.AllocUnmeasuredKB = 500;
                text = Summary.Render(new SummaryInput { SessionId = "rate", Row = row, Stats = stats, Seconds = 60 });
                Check(text.Contains("allocated about 100 KB per second"), "(6000 - 500) KB over the 55 s whose allocation was measured");
            }
            finally { rig.Dispose(); }
        }
    }
}
