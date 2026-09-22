using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using static PerformanceLog.Tests.Assert;

namespace PerformanceLog.Tests
{
    // PL1: a watched method's sampling interval follows that method's own call rate, in both directions, and every window it ran in has a row.
    internal static class WatchSamplingTests
    {
        public static IEnumerable<(string, Action)> All()
        {
            yield return ("Profile: a rare watched method next to a hot one is still timed in every window it runs", RareMethodNextToHotOne);
            yield return ("Profile: a watched method's interval widens with its own load, inside the budget, and comes back down when the load goes away", IntervalFollowsTheLoad);
            yield return ("Profile: a watched method that ran but was never timed gets a row with sampled 0 and adds no made-up 0 ms to the totals", UntimedWindowIsWrittenNotGuessed);
        }

        const double PairNs = 50;   // what the real recordings calibrate for a sampled pair (samplePairNs)

        static Rig rig;

        static void Prepare()
        {
            rig = new Rig();
            Profile.Configure(1, 1, 1);
            Profile.BudgetFraction = 0.01;
            Profile.ModResolver = null;
            Profile.SeedSampling(12345);
            // A double, not Rig.Ms: Rig.Ms truncates to whole Stopwatch ticks, and 50 ns is 0 ticks at 10 MHz, which switches the adaptation off.
            Probe.SamplePairTicks = PairNs * 1e-9 * Stopwatch.Frequency;
        }

        static void Call(int id, double ms)
        {
            Sample s = Profile.BeginMethod(id);
            rig.Advance(ms);
            Profile.EndMethod(id, s);
        }

        static double[] RowOf(List<double[]> rows, int id) => rows.FirstOrDefault(r => (int)r[3] == id);

        static void RareMethodNextToHotOne()
        {
            Prepare();
            using (rig)
            {
                int hot = Profile.RegisterMethod("Hot.Method", "M", 8);
                int rare = Profile.RegisterMethod("Rare.Method", "M", 8);
                // Window 1: the hot one runs 2 million times in 10 s (200,000 a second, 1 us a call); the rare one once.
                Call(rare, 5);
                for (int i = 0; i < 2000000; i++) Call(hot, 0.001);
                Profile.FlushWindow(1, 1, 10, rig.Prof);
                rig.ProfileRows();
                // Windows 2 to 5: only the rare one runs, 20 times a window at 5 ms a call. Its own rate (2 a second) needs no more than the
                // interval it was given (8), so every window times its first call and, with gaps of at most 15, at least one more.
                for (int w = 2; w <= 5; w++)
                {
                    for (int i = 0; i < 20; i++) Call(rare, 5);
                    Profile.FlushWindow(w, w, 10, rig.Prof);
                    double[] row = RowOf(rig.ProfileRows(), rare);
                    Console.WriteLine("     window " + w + ": rare method " + (row == null ? "has no row" : "calls=" + row[4] + " sampled=" + row[5] + " ms=" + row[6]));
                    Check(row != null, "window " + w + ": the rare method's 20 calls of 5 ms left no row (none was timed)");
                    Equal(20.0, row[4], "window " + w + ": calls");
                    Check(row[5] >= 2, "window " + w + ": only " + row[5] + " of 20 calls were timed; the interval is still the one the hot method needed");
                    Near(100, row[6], .01, "window " + w + ": 20 calls of 5 ms");
                }
                Profile.Total total = Profile.Totals().Single(t => t.Id == rare);
                Equal(81.0, total.Calls, "session calls");
                Near(405, total.Ms, .01, "session ms: 5 ms, then four windows of 100 ms");
            }
        }

        static void IntervalFollowsTheLoad()
        {
            Prepare();
            using (rig)
            {
                int hot = Profile.RegisterMethod("Hot.Method", "M", 8);
                // Two busy windows of 200,000 calls a second: the second is timed at the interval the first asked for, which keeps measuring inside
                // the watched methods' share of the budget (1% of a second, a tenth of it for methods).
                double[] busy = null;
                for (int w = 1; w <= 2; w++)
                {
                    for (int i = 0; i < 2000000; i++) Call(hot, 0.001);
                    Profile.FlushWindow(w, w, 10, rig.Prof);
                    busy = RowOf(rig.ProfileRows(), hot);
                    Check(busy != null, "busy window " + w + ": no row");
                }
                double pairSeconds = PairNs * 1e-9 + 100e-9;   // the pair plus the key lookup, as Profile charges it
                double share = busy[5] * pairSeconds / 10;
                Console.WriteLine("     busy window: " + busy[5] + " of 2000000 calls timed, " + (share * 100).ToString("F3") + "% of a second");
                Check(busy[5] < 2000000 / 20.0, "a hot method's interval widens: " + busy[5] + " of 2000000 calls were timed");
                Check(share <= 0.01 * Profile.BudgetShareMethod * 1.05, "measuring stays inside the methods' share of the budget: " + share);
                // Then the load goes away: 100 calls of 1 ms a window. The first quiet window is still timed at the busy interval; after it the
                // interval is back to 8, so each later window times its first call and, with gaps of at most 15, at least six more.
                for (int w = 3; w <= 6; w++)
                {
                    for (int i = 0; i < 100; i++) Call(hot, 1);
                    Profile.FlushWindow(w, w, 10, rig.Prof);
                    double[] row = RowOf(rig.ProfileRows(), hot);
                    Console.WriteLine("     quiet window " + w + ": sampled=" + (row == null ? "no row" : row[5].ToString()) + " of 100 calls");
                    Check(row != null, "window " + w + ": no row");
                    Near(100, row[6], .01, "window " + w + ": 100 calls of 1 ms");
                    if (w >= 4) Check(row[5] >= 7, "window " + w + ": only " + row[5] + " of 100 calls were timed; the interval never came back down after the busy windows");
                }
            }
        }

        static void UntimedWindowIsWrittenNotGuessed()
        {
            Prepare();
            using (rig)
            {
                int id = Profile.RegisterMethod("Throwing.Method", "M", 8);
                Call(id, 2);
                Profile.FlushWindow(1, 1, 10, rig.Prof);
                double[] first = RowOf(rig.ProfileRows(), id);
                Check(first != null && first[5] == 1, "window 1: the one call was timed");
                // Window 2: three calls whose postfix never runs (the method threw, and Harmony skips a postfix then), so none is timed.
                for (int i = 0; i < 3; i++) Profile.BeginMethod(id);
                Profile.FlushWindow(2, 2, 10, rig.Prof);
                double[] row = RowOf(rig.ProfileRows(), id);
                Console.WriteLine("     untimed window: " + (row == null ? "no row" : "calls=" + row[4] + " sampled=" + row[5] + " ms=" + row[6]));
                Check(row != null, "window 2: the method ran 3 times and left no row, which reads as 'not called'");
                Equal(3.0, row[4], "calls are still exact");
                Equal(0.0, row[5], "sampled 0 says nothing was timed");
                Profile.Total total = Profile.Totals().Single(t => t.Id == id);
                Console.WriteLine("     totals: calls=" + total.Calls + " ms=" + total.Ms);
                Equal(1.0, total.Calls, "the session totals leave out the calls nobody timed, instead of adding them at 0 ms");
                Near(2, total.Ms, .001, "session ms");
            }
        }
    }
}
