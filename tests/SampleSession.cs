using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PerformanceLog.Tests
{
    /// <summary>
    /// Writes a made-up session folder (not from a game) with the real probe, profile, writer, summary and readme, on a scripted clock.
    /// Five minutes of play: speed 1, then speed 3, a pause, an autosave, garbage collections and a hitch in one mod's singleton. Used by
    /// the end-to-end check and as the fixture for tools/perflog.py. With <c>withMod</c> false the mod that causes the hitches is left
    /// out, which gives the "after" side of a comparison.
    /// </summary>
    internal static class SampleSession
    {
        const string ModId = "kyler.lategameperformance";
        const string ModAssembly = "LateGamePerformance";

        public static void Write(string directory, bool withMod = true, int seed = 7)
        {
            Directory.CreateDirectory(directory);
            string frames = Path.Combine(directory, "frames.csv"), profile = Path.Combine(directory, "profile.csv"),
                spikes = Path.Combine(directory, "spikes.csv"), events = Path.Combine(directory, "events.csv"), summary = Path.Combine(directory, "summary.md");

            var random = new Random(seed);
            long now = 5_000_000_000L;
            long bytes = 0;
            Probe.Stop();
            Probe.TestClock = () => now;
            Alloc.UseTestSource(() => bytes);
            Probe.ScopePairTicks = Ms(0.0004); Probe.SamplePairTicks = Ms(0.0009); Probe.AllocPairTicks = Ms(0.0004); Probe.ExactCallTicks = Ms(0.0002);
            Probe.PatchCallTicks = Ms(0.00005);
            Func<string, string> resolver = a => a == ModAssembly ? ModId : a.StartsWith("Timberborn.") ? "game" : "";
            Profile.ModResolver = resolver;

            var ringFrames = new Ring(Columns.Count, 4096);
            var ringProfile = new Ring(Profile.Table.Count, 4096);
            var ringSpikes = new Ring(Profile.SpikeTable.Count, 4096);

            var header = new HeaderBuilder("frames", HeaderBuilder.FramesFormat);
            header.Add("session", "sample-" + (withMod ? "with-mod" : "without-mod"));
            header.Add("started", "2026-01-01 12:00:00 +00:00 local, 2026-01-01 12:00:00 UTC");
            header.Add("mod", "0.1.4");
            header.Add("game", "1.1.2.4 (this is a made-up sample, not a game)");
            header.Add("unity", "6000.0.0f1");
            header.Add("os", "Windows 11 (10.0.26200) 64bit");
            header.Add("cpu", "Sample CPU x16 @ 3600 MHz");
            header.Add("memoryMB", "32768");
            header.Add("gpu", "Sample GPU (8192 MB)");
            header.Add("graphics", "Direct3D11 Direct3D 11.0");
            header.Add("display", "vSyncCount=1 targetFrameRate=-1 resolution=2560x1440 refreshHz=60.00 fullScreen=FullScreenWindow");
            header.Add("quality", "High antiAliasing=2");
            header.Add("gc", "mode=Enabled incremental=False timeSliceNs=0 maxGeneration=2");
            header.Add("profile", "standard");
            header.Add("thresholdMs", "50");
            header.Add("summarySeconds", "10");
            header.Add("profileSeconds", "30");
            header.Add("clockHz", Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture));
            header.Add("tickSeconds", "0.30");
            header.Note("Made-up sample data written by tests/SampleSession.cs on a scripted clock.");
            header.Pipe("capability", "allocSource", "scripted counter");
            header.Pipe("capability", "cpuTimes", "unavailable");
            header.Pipe("capability", "frameTiming", "off (sample)");
            header.Pipe("histogram", "frameEdgesMs", string.Join(",", Columns.FrameEdgesMs.Select(e => e.ToString(CultureInfo.InvariantCulture))));
            header.Pipe("histogram", "ticksPerFrameBuckets", "0,1,2,3-4,5-9,10+");
            header.Pipe("phases", string.Join(",", Columns.PhaseNames), "each is the time from the start to the end of that phase of Unity's frame");
            header.Pipe("bootconfig", "gc-max-time-slice=3");
            header.Pipe("cmdline", "Timberborn.exe");
            header.Add("mods", withMod ? "3" : "2");
            header.Mod("Harmony", "Harmony", "v2.4.1");
            header.Mod("kyler.performancelog", "Performance Log", "v0.1.3");
            if (withMod) header.Mod(ModId, "Late Game Performance", "v0.4.9");
            header.Pipe("patch", "hot", "Timberborn.TickSystem.Ticker.Update", "prefix", "kyler.performancelog", "priority=0", "index=0", "before=", "after=", "PerformanceLog", "PerformanceLog.Instrumentation.TickPrefix");

            var writer = new LogWriter(Console.Error.WriteLine);
            writer.AddTable(frames, header.Lines, Columns.Main, ringFrames, null, w => w.WriteLine("# capability-final|allocSource|scripted counter produced values"));
            var profileHeader = new HeaderBuilder("profile", 1);
            writer.AddTable(profile, profileHeader.Lines, Profile.Table, ringProfile, Profile.AppendProfileText);
            var spikeHeader = new HeaderBuilder("spikes", 1);
            writer.AddTable(spikes, spikeHeader.Lines, Profile.SpikeTable, ringSpikes, Profile.AppendSpikeText);
            TextChannel eventChannel = writer.AddText(events, "utcMs,tick,kind,ms,detail");
            writer.AddFile(summary);
            if (!writer.Start()) throw new Exception("the sample writer did not start: " + writer.Failure);

            Probe.HeavySampler = extra =>
            {
                int at = Columns.ExtraPerFrameCount;
                extra[at] = 300; extra[at + 1] = 210 + random.Next(20); extra[at + 2] = 1400; extra[at + 3] = 3100;
                extra[at + 4] = 9000 + seconds(now) * 2; extra[at + 5] = 240 + seconds(now) / 10; extra[at + 6] = 12; extra[at + 7] = 20 + seconds(now) / 100;
            };
            Probe.Start(new ProbeSettings
            {
                Frames = ringFrames, Profile = ringProfile, Spikes = ringSpikes, GameThreadId = Environment.CurrentManagedThreadId,
                ThresholdMs = 50, SummarySeconds = 10, ProfileSeconds = 30, SpikeContributors = 5,
            });
            Profile.Configure(8, 32, 4);
            Profile.ModResolver = resolver;

            eventChannel.Write(Utc(now) + ",0,session-start,0.0,\"sample session\"");
            int idWater = Profile.IdFor(ProfileKind.TickSingleton, "Timberborn.WaterSystem.WaterSimulator", "Timberborn.WaterSystem");
            int idPop = Profile.IdFor(ProfileKind.TickSingleton, "Timberborn.Population.PopulationService", "Timberborn.Population");
            int idHaul = Profile.IdFor(ProfileKind.TickSingleton, "LateGamePerformance.HaulCache", ModAssembly);
            int idCam = Profile.IdFor(ProfileKind.UpdateSingleton, "Timberborn.CameraSystem.CameraService", "Timberborn.CameraSystem");
            int idUi = Profile.IdFor(ProfileKind.UpdateSingleton, "Timberborn.CoreUI.PanelStack", "Timberborn.CoreUI");
            int idRoute = Profile.IdFor(ProfileKind.UpdateSingleton, "LateGamePerformance.RouteMapsBackground", ModAssembly);
            int idSpeed = Profile.IdFor(ProfileKind.LateSingleton, "Timberborn.TimeSystem.SpeedManager", "Timberborn.TimeSystem");
            int idPar = Profile.IdFor(ProfileKind.ParallelStart, "Timberborn.Navigation.NavigationSynchronizer", "Timberborn.Navigation");
            string[] prefabs = { "Beaver.Adult", "Beaver.Child", "FarmHouse", "Lodge", "Pine", "Bot.Worker" };
            double[] prefabCost = { 5.5, 4.0, 1.4, 2.2, 0.7, 6.0 };

            void Work(int id, double ms)
            {
                Timing t = Profile.BeginExact();
                bytes += 1500;
                now += Ms(ms);
                Profile.EndExact(id, t);
            }

            // The game spreads a tick over the frames as 129 buckets (the singletons, then 128 buckets of entities) and runs as many
            // per frame as the time that passed allows, so a frame usually holds part of a tick.
            double bucketDebt = 0;
            int bucketIndex = 0;
            int frameIndex = 0;
            bool saved = false;
            var gcAt = new HashSet<int> { 700, 1500, 2600, 3900, 5100, 6800 };
            var dt = 16.7;
            for (double t = 0; t < 300; )
            {
                frameIndex++;
                double speed = t < 60 ? 1 : t < 240 ? 3 : t < 255 ? 0 : 3;
                long begin = now;

                Probe.PhaseMark(0, true); now += Ms(0.2); Probe.PhaseMark(0, false);
                Probe.PhaseMark(5, true);
                bucketDebt += speed * dt / 1000.0 / 0.3 * 129;
                int buckets = (int)bucketDebt;
                bucketDebt -= buckets;
                long updateScope = Probe.Begin(Slot.Update);
                Work(idCam, 0.35 + random.NextDouble() * 0.1);
                Work(idUi, 0.8 + random.NextDouble() * 0.3 + (speed == 0 ? 0.6 : 0));
                if (withMod) Work(idRoute, 0.25 + random.NextDouble() * 0.1);
                bytes += 12000;
                Probe.End(updateScope);
                long lateScope = Probe.Begin(Slot.LateUpdate);
                Work(idSpeed, 0.05);
                Probe.End(lateScope);

                if (buckets > 0)
                {
                    long tickScope = Probe.Begin(Slot.Tick);
                    now += Ms(0.02);
                    for (int i = 0; i < buckets; i++)
                    {
                        if (bucketIndex == 0)
                        {
                            Probe.NoteBucket();
                            long wait = Probe.Begin(Slot.ParallelWait); now += Ms(0.4 + random.NextDouble() * 0.3); Probe.End(wait);
                            Probe.NoteParallelTick(2.1);
                            long sing = Probe.Begin(Slot.Singletons);
                            Work(idWater, 1.1 + random.NextDouble() * 0.3);
                            Work(idPop, 0.2);
                            if (withMod) Work(idHaul, 0.3 + random.NextDouble() * 0.1);
                            Probe.End(sing);
                            long start = Probe.Begin(Slot.ParallelStart);
                            Work(idPar, 0.45);
                            Probe.End(start);
                        }
                        else
                        {
                            Probe.NoteEntityBucket();
                            long ent = Probe.Begin(Slot.Entities);
                            const int perBucket = 7;
                            for (int e = 0; e < perBucket; e++)
                            {
                                Sample sample = Profile.BeginEntity();
                                int p = random.Next(prefabs.Length);
                                now += Ms(prefabCost[p] / 250);
                                bytes += 260;
                                Profile.EndEntity(prefabs[p], sample);
                            }
                            Probe.Count(Counter.Entities, perBucket);
                            Probe.End(ent);
                        }
                        bucketIndex = (bucketIndex + 1) % 129;
                    }
                    Probe.End(tickScope);
                }
                Probe.PhaseMark(5, false);

                // A hitch in one mod's singleton, twice. It is the reason for the comparison.
                if (withMod && (frameIndex == 3300 || frameIndex == 4700)) { long u = Probe.Begin(Slot.Update); Work(idRoute, 95); Probe.End(u); }
                // The autosave.
                if (!saved && t >= 120)
                {
                    saved = true;
                    long save = Probe.Begin(Slot.Save);
                    now += Ms(430);
                    bytes += 6_000_000;
                    Probe.End(save);
                    eventChannel.Write(Utc(now) + "," + Probe.TickCount + ",save," + (430).ToString("F1", CultureInfo.InvariantCulture) + ",\"autosave: finishing the tick 20 ms, snapshot 150 ms, world json 200 ms, thumbnail 60 ms\"");
                }
                // A garbage collection: a real one, so the collection counter moves, and a long frame.
                if (gcAt.Contains(frameIndex)) { GC.Collect(0); now += Ms(95 + random.Next(40)); }

                // The wait for the next vertical sync: the frame is at least 16.7 ms.
                double used = (now - begin) * 1000.0 / Stopwatch.Frequency;
                Probe.PhaseMark(7, true);
                if (used < dt) now += Ms(dt - used);
                Probe.PhaseMark(7, false);
                Probe.Extra[2] = 1500; Probe.Extra[3] = 300; Probe.Extra[4] = 500; Probe.Extra[5] = 900000;
                Probe.OnFrame((float)speed, true);
                t = seconds(now) - seconds(5_000_000_000L);
                if (frameIndex % 3600 == 0) writer.SetFile(summary, Summary.Render(BuildInput(withMod, "recording")));
            }

            eventChannel.Write(Utc(now) + "," + Probe.TickCount + ",session-end,0.0,\"sample session ended\"");
            Probe.Stop();
            writer.SetFile(summary, Summary.Render(BuildInput(withMod, "finished")));
            writer.Stop();
            if (writer.Failure != null) throw new Exception("the sample writer failed: " + writer.Failure);
            File.WriteAllText(Path.Combine(directory, "README.md"), SessionReadme.Render(ReadTemplate(), "0.1.4"));
            File.WriteAllText(Path.Combine(directory, "columns.md"), SessionReadme.RenderColumns("0.1.4"));
            Probe.TestClock = null;
            Probe.HeavySampler = null;
            Alloc.Init();
            Profile.ModResolver = null;
        }

        static SummaryInput BuildInput(bool withMod, string status)
        {
            var input = new SummaryInput
            {
                SessionId = "sample-" + (withMod ? "with-mod" : "without-mod"), Status = status, ModVersion = "0.1.4", GameVersion = "1.1.2.4 (sample)",
                StartedLocal = "2026-01-01 12:00:00", Seconds = Probe.SessionSeconds, Ticks = Probe.TickCount, Row = Probe.SessionRow(), Stats = Probe.Stats,
                Worst = Probe.WorstFrames(), Windows = Probe.Windows(), Totals = Profile.Totals(), TickIntervalSeconds = 0.3, Folder = "sample",
                Colony = "240 beavers, 12 bots, 9000 entities, day 20",
            };
            input.Environment.Add(new KeyValuePair<string, string>("Computer", "Sample CPU x16, Sample GPU 8192 MB, 32768 MB RAM"));
            input.Environment.Add(new KeyValuePair<string, string>("Display", "vSync on, 60 Hz, 2560x1440"));
            input.Environment.Add(new KeyValuePair<string, string>("Garbage collector", "mode=Enabled incremental=False"));
            input.Mods.Add(new[] { "Harmony", "Harmony", "v2.4.1" });
            input.Mods.Add(new[] { "kyler.performancelog", "Performance Log", "v0.1.3" });
            if (withMod) input.Mods.Add(new[] { ModId, "Late Game Performance", "v0.4.9" });
            input.Capabilities.Add("allocation source: scripted counter");
            input.Capabilities.Add("processor times: unavailable in this sample");
            input.Warnings.Add("This is made-up sample data written by the tests, not a recording of a game.");
            return input;
        }

        static string ReadTemplate()
        {
            string directory = AppContext.BaseDirectory;
            while (directory != null && !File.Exists(Path.Combine(directory, "docs", "SESSION-README.md")))
                directory = Path.GetDirectoryName(directory);
            if (directory == null) throw new FileNotFoundException("docs/SESSION-README.md was not found above " + AppContext.BaseDirectory);
            return File.ReadAllText(Path.Combine(directory, "docs", "SESSION-README.md"));
        }

        static long Ms(double milliseconds) => (long)(milliseconds * Stopwatch.Frequency / 1000.0);
        static double seconds(long ticks) => ticks / (double)Stopwatch.Frequency;
        static long Utc(long now) => 1767268800000L + (long)((now - 5_000_000_000L) * 1000.0 / Stopwatch.Frequency);
    }
}
