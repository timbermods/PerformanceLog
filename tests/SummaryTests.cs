using System;
using System.Collections.Generic;
using System.Linq;
using static PerformanceLog.Tests.Assert;

namespace PerformanceLog.Tests
{
    internal static class SummaryTests
    {
        public static IEnumerable<(string, Action)> All()
        {
            yield return ("Summary: percentiles come from the histogram, linear inside a bucket", Percentiles);
            yield return ("Summary: a session with no frames says so instead of failing", NoFrames);
            yield return ("Summary: every section is there for a session with data, and names the mods and singletons", FullSummary);
            yield return ("Summary: a session with no profile and no slow frames still renders", NothingSlow);
            yield return ("Summary: component time is rolled up by mod, and load steps are listed slowest first", ComponentAndLoadTables);
            yield return ("Summary: long class names are shortened and short ones kept", ShortNames);
            yield return ("Readme: placeholders are filled and no placeholder is left", ReadmePlaceholders);
        }

        static void Percentiles()
        {
            var counts = new double[Columns.FrameHistCount];
            counts[3] = 100;   // 8.5 .. 11.5 ms
            Near(10.0, Summary.Percentile(counts, 0.5, 20), .001, "the middle of the only bucket");
            Near(8.5, Summary.Percentile(counts, 0.0, 20), .001);
            counts[3] = 50; counts[5] = 50;   // 14 .. 17.5 ms
            Near(11.5, Summary.Percentile(counts, 0.5, 20), .001, "half the frames are in the first bucket");
            counts = new double[Columns.FrameHistCount];
            counts[16] = 10;                   // 400 ms and over
            Near(400 + (900 - 400) * 0.5, Summary.Percentile(counts, 0.5, 900), .001, "the open last bucket uses the slowest frame");
            Equal(0.0, Summary.Percentile(new double[Columns.FrameHistCount], 0.9, 100));
        }

        static void NoFrames()
        {
            string text = Summary.Render(new SummaryInput { SessionId = "x", Status = "recording" });
            Check(text.Contains("No frames have been recorded yet"));
            Check(text.Contains("## Mods enabled"));
        }

        static void FullSummary()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "perflog-summary-" + Guid.NewGuid().ToString("N"));
            try
            {
                SampleSession.Write(dir);
                string text = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "summary.md"));
                foreach (string section in new[]
                {
                    "# Performance log summary", "## Session", "## Frame rate", "## Where an average frame goes", "## Simulation ticks", "## How the session changed over time",
                    "## Garbage collection and memory", "## Is the game thread working or waiting?", "## Where the time goes, by part of the game or mod",
                    "## The slowest frames", "## What each measurement source could do", "## Computer and game settings", "## Mods enabled", "## Files",
                })
                    Check(text.Contains(section), "missing section " + section);
                Check(text.Contains("LateGamePerformance.RouteMapsBackground"), "the mod's singleton is named");
                Check(text.Contains("kyler.lategameperformance"), "and its mod");
                Check(text.Contains("Timberborn.WaterSystem.WaterSimulator"), "and the game's");
                Check(text.Contains("| paused |"), "the speed table has the paused row");
                Check(text.Contains("`tickMs`") && text.Contains("`otherMs`"), "the slot table is there");
                Check(text.Contains("Singleton time by mod"), "the per-mod roll-up is there");
                Check(text.Contains("**finished**"));
                Check(!text.Contains("NaN") && !text.Contains("Infinity"), "no NaN or Infinity in the text");
                Check(text.Contains("Made-up sample") || text.Contains("made-up sample"), "warnings are shown near the top");
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        static void NothingSlow()
        {
            var rig = new Rig(thresholdMs: 1000);
            try
            {
                for (int i = 0; i < 5; i++) { rig.Advance(16); rig.Frame(); }
                var input = new SummaryInput { SessionId = "quiet", Row = Probe.SessionRow(), Stats = Probe.Stats, Worst = Probe.WorstFrames(), Seconds = 1, Ticks = 0 };
                string text = Summary.Render(input);
                Check(text.Contains("No frame reached the slow-frame threshold."));
                Check(text.Contains("The profile is empty"));
                Check(text.Contains("No simulation tick ran"));
                Check(!text.Contains("NaN"));
            }
            finally { rig.Dispose(); }
        }

        static void ComponentAndLoadTables()
        {
            var rig = new Rig(thresholdMs: 1000);
            try
            {
                for (int i = 0; i < 5; i++) { rig.Advance(16); rig.Frame(); }
                var input = new SummaryInput { SessionId = "deep", Row = Probe.SessionRow(), Stats = Probe.Stats, Seconds = 10, Ticks = 100 };
                input.Totals.Add(new Profile.Total { Id = 0, Kind = ProfileKind.Component, Name = "Walkers.SlowWalker", Mod = "kyler.walkers", Ms = 900, Kb = 10, Calls = 5000, MaxMs = 0.4 });
                input.Totals.Add(new Profile.Total { Id = 1, Kind = ProfileKind.Component, Name = "Timberborn.Walking.Walker", Mod = "game", Ms = 300, Kb = 5, Calls = 5000, MaxMs = 0.2 });
                input.Totals.Add(new Profile.Total { Id = 2, Kind = ProfileKind.Load, Name = "Some.Loader", Mod = "kyler.slow", Ms = 1800, Calls = 1, MaxMs = 1800 });
                input.Totals.Add(new Profile.Total { Id = 3, Kind = ProfileKind.PostLoad, Name = "Other.Loader", Mod = "game", Ms = 40, Calls = 1, MaxMs = 40 });
                string text = Summary.Render(input);
                Check(text.Contains("**Entity component time by mod**"), "the component roll-up is there");
                Check(text.IndexOf("kyler.walkers | 90.00") > 0 || text.Contains("| kyler.walkers | 90"), "and adds the mod's components: " + text);
                Check(text.Contains("Slowest steps of loading the game"));
                Check(text.IndexOf("Some.Loader") < text.IndexOf("Other.Loader"), "the slowest load step comes first");
                Check(!text.Contains("NaN"));
            }
            finally { rig.Dispose(); }
        }

        static void ShortNames()
        {
            Equal("Timberborn.Foo.Bar", Summary.Short("Timberborn.Foo.Bar"));
            string longName = "Some.Very.Long.Namespace.That.Goes.On.And.On.Forever.And.Ever.TheClass";
            Equal("TheClass", Summary.Short(longName));
            Equal("?", Summary.Short(null));
        }

        static void ReadmePlaceholders()
        {
            string template = "v{{VERSION}} {{PROFILE_KINDS}} {{FRAME_EDGES}}";
            string text = SessionReadme.Render(template, "9.9.9");
            Check(!text.Contains("{{"), "no placeholder is left");
            Check(text.StartsWith("v9.9.9 "));
            Check(text.Contains("`tick-singleton`:") && text.Contains("`post-load-non-singleton`:"));
            Check(text.Contains("4, 6, 8.5, 11.5"));

            string columns = SessionReadme.RenderColumns("9.9.9");
            Check(columns.Contains("| `frameMs` |") && columns.Contains("| `allocKB` |") && columns.Contains("| `share` |") && columns.Contains("| `tickMs` |"));
            Check(columns.Contains("## frames.csv") && columns.Contains("## profile.csv") && columns.Contains("## spikes.csv"));
            foreach (string name in Columns.Names) Check(columns.Contains("`" + name + "`"), name + " is in columns.md");
            Check(!columns.Contains("{{"));
        }
    }
}
