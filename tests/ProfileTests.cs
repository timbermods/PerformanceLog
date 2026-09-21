using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static PerformanceLog.Tests.Assert;

namespace PerformanceLog.Tests
{
    internal static class ProfileTests
    {
        public static IEnumerable<(string, Action)> All()
        {
            yield return ("Profile: a singleton timed on every call is scaled by nothing and keeps its slowest call", ExactSingletonRow);
            yield return ("Profile: allocation is read on some calls and scaled up to all of them", AllocationScaledUp);
            yield return ("Profile: about every Nth entity tick is timed and the result scaled up by the same factor", SampledEntities);
            yield return ("Profile: random gaps average the interval and do not miss kinds in a repeating pattern", NoAliasing);
            yield return ("Profile: a watched method counts every call and times every Nth", WatchedMethod);
            yield return ("Profile: a slow frame names the singletons that took its time, biggest first", SpikeAttribution);
            yield return ("Profile: an ordinary frame writes no spike rows but still forgets its times", NoSpikeForFastFrames);
            yield return ("Profile: the same class gets the same id, and Reset makes cached ids stale", IdsAreStable);
            yield return ("Profile: sampling widens with load and stays inside the budget", SamplingAdapts);
            yield return ("Profile: sampling never goes below one and ignores an unmeasured cost", SamplingBounds);
            yield return ("Profile: load steps are written straight to the file as window 0", LoadRows);
            yield return ("Profile: totals over the session add up across windows", SessionTotals);
            yield return ("Profile: mods are found by assembly, and the game's own are named game", ModResolution);
            yield return ("Profile: the resolver the game side installs names a mod's DLL, the game's DLLs and leaves the rest unknown", ResolverForOwners);
            yield return ("Profile: a load step's heap growth is written as its allocation", LoadRowAllocation);
            yield return ("Profile: rows too small to matter are left out", TinyRowsSkipped);
        }

        static void Prepare(out Rig rig, double threshold = 50)
        {
            rig = new Rig(thresholdMs: threshold);
            Profile.Configure(1, 1, 1);
            Profile.BudgetFraction = 0.005;
            Profile.ModResolver = null;
            Profile.SeedSampling(12345);
        }

        static void ExactSingletonRow()
        {
            Prepare(out Rig rig);
            using (rig)
            {
                int id = Profile.IdFor(ProfileKind.TickSingleton, typeof(ProfileTests));
                foreach (double ms in new[] { 2.0, 3.0, 10.0 })
                {
                    Timing t = Profile.BeginExact();
                    rig.Advance(ms);
                    Profile.EndExact(id, t);
                    rig.Advance(1);
                }
                Profile.FlushWindow(1, 42, 10, rig.Prof);
                List<double[]> rows = rig.ProfileRows();
                Equal(1, rows.Count);
                double[] r = rows[0];
                Equal((double)(int)ProfileKind.TickSingleton, r[0]);
                Equal(1.0, r[1]); Equal(42.0, r[2]); Equal((double)id, r[3]);
                Equal(3.0, r[4], "calls"); Equal(3.0, r[5], "sampled");
                Near(15, r[6], .001, "ms");
                Near(10, r[8], .001, "slowest call");
                Equal(0, rig.ProfileRows().Count, "the window was forgotten");
                Profile.FlushWindow(2, 43, 10, rig.Prof);
                Equal(0, rig.ProfileRows().Count, "and an idle window writes nothing");
            }
        }

        static void AllocationScaledUp()
        {
            Prepare(out Rig rig);
            using (rig)
            {
                Profile.Configure(1, 1, 4);   // allocation read on every fourth call
                int id = Profile.IdFor(ProfileKind.UpdateSingleton, "Alloc.Singleton");
                for (int i = 0; i < 8; i++)
                {
                    Timing t = Profile.BeginExact();
                    rig.Bytes += 2048;
                    rig.Advance(1);
                    Profile.EndExact(id, t);
                }
                Profile.FlushWindow(1, 1, 10, rig.Prof);
                double[] r = rig.ProfileRows().Single();
                Equal(8.0, r[4]);
                Near(16, r[7], .001, "two reads of 2 KB scale up to 8 calls of 2 KB");
            }
        }

        static void SampledEntities()
        {
            Prepare(out Rig rig);
            using (rig)
            {
                Profile.Configure(4, 1, 1);
                for (int i = 0; i < 400; i++)
                {
                    Sample s = Profile.BeginEntity();
                    rig.Advance(1);
                    rig.Bytes += 1024;
                    Profile.EndEntity("Beaver", s);
                }
                Profile.FlushWindow(1, 1, 10, rig.Prof);
                double[] r = rig.ProfileRows().Single();
                Equal((double)(int)ProfileKind.Entity, r[0]);
                double sampled = r[5];
                Check(sampled >= 60 && sampled <= 140, "about a quarter of 400 calls were timed, got " + sampled);
                Equal(sampled * 4, r[4], "calls are estimated as samples times the interval");
                Near(sampled * 4, r[6], .001, "each sample took 1 ms, scaled by 4");
                Near(sampled * 4, r[7], .001, "and allocated 1 KB, scaled by 4");
            }
        }

        static void NoAliasing()
        {
            Prepare(out Rig rig);
            using (rig)
            {
                // Six kinds of entity in a fixed cycle and an interval of 8: a fixed stride would only ever land on every other kind.
                Profile.Configure(8, 1, 1);
                string[] kinds = { "A", "B", "C", "D", "E", "F" };
                for (int i = 0; i < 60000; i++)
                {
                    Sample s = Profile.BeginEntity();
                    rig.Advance(0.001);
                    Profile.EndEntity(kinds[i % 6], s);
                }
                Profile.FlushWindow(1, 1, 10, rig.Prof);
                List<double[]> rows = rig.ProfileRows();
                Equal(6, rows.Count, "every kind was sampled");
                double total = rows.Sum(x => x[5]);
                Check(total > 5000 && total < 10000, "about 60000/8 samples, got " + total);
                foreach (double[] row in rows)
                    Check(row[5] / total > 0.13 && row[5] / total < 0.21, "each kind gets about a sixth of the samples: " + row[5] / total);
                Near(60000, rows.Sum(x => x[4]), 6000, "the estimate of all calls is close to the truth");
            }
        }

        static void WatchedMethod()
        {
            Prepare(out Rig rig);
            using (rig)
            {
                int id = Profile.RegisterMethod("Some.Type.Method", "SomeMod", 3);
                for (int i = 0; i < 9; i++)
                {
                    Sample s = Profile.BeginMethod(id);
                    rig.Advance(2);
                    Profile.EndMethod(id, s);
                }
                Profile.FlushWindow(1, 1, 10, rig.Prof);
                double[] r = rig.ProfileRows().Single();
                Equal((double)(int)ProfileKind.Method, r[0]);
                Equal(9.0, r[4], "every call is counted");
                Equal(3.0, r[5], "every third is timed");
                Near(18, r[6], .001, "3 timed calls of 2 ms scaled to 9 calls");
                Near(2, r[8], .001);
                Equal("SomeMod", Profile.AssemblyOf(id));
            }
        }

        static void SpikeAttribution()
        {
            Prepare(out Rig rig);
            using (rig)
            {
                int a = Profile.IdFor(ProfileKind.UpdateSingleton, "Mod.A");
                int b = Profile.IdFor(ProfileKind.TickSingleton, "Mod.B");
                int c = Profile.IdFor(ProfileKind.LateSingleton, "Mod.C");
                foreach ((int id, double ms) in new[] { (a, 10.0), (b, 60.0), (c, 5.0), (a, 15.0) })
                {
                    Timing t = Profile.BeginExact();
                    rig.Advance(ms);
                    Profile.EndExact(id, t);
                }
                rig.Advance(30);
                rig.Frame();  // 120 ms: slow
                List<double[]> spikes = rig.SpikeRows();
                Equal(3, spikes.Count);
                Equal((double)b, spikes[0][6]); Equal(1.0, spikes[0][4]);
                Near(60, spikes[0][7], .001);
                Near(50, spikes[0][8], .1, "share of the frame");
                Equal((double)a, spikes[1][6]); Near(25, spikes[1][7], .001, "its two calls in the frame add up");
                Equal((double)c, spikes[2][6]);
                Equal((double)(int)ProfileKind.TickSingleton, spikes[0][5]);
                Equal(120.0, spikes[0][3]);
                Equal((double)Probe.FrameNumber, spikes[0][0]);
                Equal(3, Profile.TopCount);
            }
        }

        static void NoSpikeForFastFrames()
        {
            Prepare(out Rig rig, threshold: 50);
            using (rig)
            {
                int a = Profile.IdFor(ProfileKind.UpdateSingleton, "Mod.A");
                Timing t = Profile.BeginExact();
                rig.Advance(5);
                Profile.EndExact(a, t);
                rig.Advance(5);
                rig.Frame();
                Equal(0, rig.SpikeRows().Count);
                // The next slow frame must not inherit the fast frame's 5 ms.
                rig.Advance(100);
                rig.Frame();
                Equal(0, rig.SpikeRows().Count, "nothing ran in the slow frame");
            }
        }

        static void IdsAreStable()
        {
            Prepare(out Rig rig);
            using (rig)
            {
                int first = Profile.IdFor(ProfileKind.TickSingleton, typeof(string));
                Equal(first, Profile.IdFor(ProfileKind.TickSingleton, typeof(string)));
                Check(first != Profile.IdFor(ProfileKind.UpdateSingleton, typeof(string)), "the same class as another kind is another key");
                Check(first != Profile.IdFor(ProfileKind.TickSingleton, typeof(int)));
                Equal("System.String", Profile.NameOf(first));
                Equal(ProfileKind.TickSingleton, Profile.KindOf(first));
                int generation = Profile.Generation;
                Profile.Reset();
                Check(Profile.Generation != generation, "Reset changes the generation so cached ids are looked up again");
                Equal(0, Profile.Count);
            }
        }

        static void SamplingAdapts()
        {
            Prepare(out Rig rig);
            using (rig)
            {
                Probe.SamplePairTicks = Rig.Ms(0.001);   // 1 us for a sampled pair
                Probe.AllocPairTicks = Rig.Ms(0.0004);
                Profile.Configure(1, 1, 1);
                // 100,000 entity ticks in a 10 s window: 10,000 a second at ~1.1 us is 1.1% of a second; the entity budget is 0.4 of 0.5%.
                for (int i = 0; i < 100000; i++) Profile.BeginEntity();
                Profile.FlushWindow(1, 1, 10, rig.Prof);
                int interval = Profile.EntityInterval;
                Check(interval >= 5 && interval <= 6, "expected about 5 or 6, got " + interval);
                double cost = 100000.0 / interval * (0.001e-3 + 100e-9);
                Check(cost / 10 <= 0.005 * Profile.BudgetShareEntity * 1.01, "measuring stays inside its share of the budget");
                // Twice the load, twice the interval.
                for (int i = 0; i < 200000; i++) Profile.BeginEntity();
                Profile.FlushWindow(2, 2, 10, rig.Prof);
                Check(Profile.EntityInterval >= 2 * interval - 1, "twice the load widens the interval, got " + Profile.EntityInterval);
                for (int i = 0; i < 100; i++) Profile.BeginComponent();
                Profile.FlushWindow(3, 3, 10, rig.Prof);
                Equal(1, Profile.ComponentInterval, "a quiet kind is timed on every call");
            }
        }

        static void SamplingBounds()
        {
            Prepare(out Rig rig);
            using (rig)
            {
                Probe.SamplePairTicks = 0;               // never calibrated
                for (int i = 0; i < 1000; i++) Profile.BeginEntity();
                Profile.Configure(7, 9, 3);
                Profile.FlushWindow(1, 1, 10, rig.Prof);
                Equal(7, Profile.EntityInterval, "an unmeasured cost leaves the intervals alone");
                Probe.SamplePairTicks = Rig.Ms(1);        // an absurdly expensive sample
                for (int i = 0; i < 100000000 / 1000; i++) Profile.BeginEntity();
                for (int i = 0; i < 1000; i++) Profile.BeginEntity();
                Profile.FlushWindow(2, 2, 0.001, rig.Prof);
                Check(Profile.EntityInterval <= 4096, "the interval is capped");
                Probe.SamplePairTicks = 0;
                Profile.Configure(0, -5, 0);
                Equal(1, Profile.EntityInterval); Equal(1, Profile.ComponentInterval); Equal(1, Profile.AllocEvery);
            }
        }

        static void LoadRows()
        {
            Prepare(out Rig rig);
            using (rig)
            {
                Profile.WriteLoadRow(ProfileKind.Load, "Some.Loader", "SomeMod", Rig.Ms(250), 0, rig.Prof);
                Profile.WriteLoadRow(ProfileKind.PostLoad, "Some.Loader", "SomeMod", Rig.Ms(5), 0, rig.Prof);
                List<double[]> rows = rig.ProfileRows();
                Equal(2, rows.Count);
                Equal((double)(int)ProfileKind.Load, rows[0][0]);
                Equal(0.0, rows[0][1], "window 0");
                Near(250, rows[0][6], .001);
                Check(rows[0][3] != rows[1][3], "a load and a post-load of the same class are different keys");
                var totals = Profile.Totals();
                Near(250, totals.Single(x => x.Kind == ProfileKind.Load).Ms, .001);
            }
        }

        static void SessionTotals()
        {
            Prepare(out Rig rig);
            using (rig)
            {
                int id = Profile.IdFor(ProfileKind.UpdateSingleton, "Mod.Total");
                for (int window = 1; window <= 3; window++)
                {
                    for (int i = 0; i < 4; i++) { Timing t = Profile.BeginExact(); rig.Advance(2); Profile.EndExact(id, t); }
                    Profile.FlushWindow(window, window, 10, rig.Prof);
                }
                Profile.Total total = Profile.Totals().Single(x => x.Id == id);
                Near(24, total.Ms, .001);
                Equal(12.0, total.Calls);
                Near(2, total.MaxMs, .001);
                Equal("Mod.Total", total.Name);
            }
        }

        static void ModResolution()
        {
            Prepare(out Rig rig);
            using (rig)
            {
                Profile.ModResolver = a => a == "MyModAssembly" ? "kyler.mymod" : "";
                Equal("kyler.mymod", Profile.ModOf(Profile.IdFor(ProfileKind.TickSingleton, "X", "MyModAssembly")));
                Equal("", Profile.ModOf(Profile.IdFor(ProfileKind.TickSingleton, "Y", "Unknown")));
                Profile.ModResolver = null;
                Equal("game", Profile.ModOf(Profile.IdFor(ProfileKind.TickSingleton, "Z", "Timberborn.TickSystem")));
                Equal("game", Profile.ModOf(Profile.IdFor(ProfileKind.TickSingleton, "Z2", "UnityEngine.CoreModule")));
                Profile.ModResolver = a => throw new InvalidOperationException("resolver failed");
                Equal("", Profile.ModOf(Profile.IdFor(ProfileKind.TickSingleton, "Z3", "Anything")), "a failing resolver does not stop a key being registered");
                Profile.ModResolver = null;
            }
        }

        static void ResolverForOwners()
        {
            Prepare(out Rig rig);
            using (rig)
            {
                var owners = new Dictionary<string, string> { ["MyModAssembly"] = "kyler.mymod" };
                Profile.ModResolver = Profile.ResolverFor(owners);
                Equal("kyler.mymod", Profile.ModOf(Profile.IdFor(ProfileKind.TickSingleton, "X", "MyModAssembly")));
                Equal("game", Profile.ModOf(Profile.IdFor(ProfileKind.TickSingleton, "Y", "Timberborn.WaterSystem")), "the game's own singletons are not unknown");
                Equal("game", Profile.ModOf(Profile.IdFor(ProfileKind.UpdateSingleton, "Y2", "Bindito.Core")));
                Equal("", Profile.ModOf(Profile.IdFor(ProfileKind.TickSingleton, "Z", "SomeOtherLibrary")));
                Equal("", Profile.ModOf(Profile.IdFor(ProfileKind.TickSingleton, "Z2", "")), "no assembly, no answer");
                Profile.ModResolver = null;
            }
        }

        static void LoadRowAllocation()
        {
            Prepare(out Rig rig);
            using (rig)
            {
                Profile.WriteLoadRow(ProfileKind.Load, "Big.Loader", "SomeMod", Rig.Ms(100), 0, rig.Prof, 300L * 1024 * 1024);
                Profile.WriteLoadRow(ProfileKind.Load, "Small.Loader", "SomeMod", Rig.Ms(100), 0, rig.Prof);
                List<double[]> rows = rig.ProfileRows();
                Equal(300.0 * 1024, rows[0][7], "allocKB of the step that grew the heap by 300 MB");
                Equal(0.0, rows[1][7], "a step measured with nothing stays 0");
                Near(300.0 * 1024, Profile.Totals().Single(x => x.Name == "Big.Loader").Kb, .001, "and the session total has it");
            }
        }

        static void TinyRowsSkipped()
        {
            Prepare(out Rig rig);
            using (rig)
            {
                int id = Profile.IdFor(ProfileKind.UpdateSingleton, "Mod.Tiny");
                Timing t = Profile.BeginExact();
                rig.Advance(0.001);
                Profile.EndExact(id, t);
                Profile.FlushWindow(1, 1, 10, rig.Prof);
                Equal(0, rig.ProfileRows().Count, "a microsecond a window is not worth a row");
                Near(0.001, Profile.Totals().Single(x => x.Id == id).Ms, .0005, "but it still counts in the totals");
            }
        }
    }
}
