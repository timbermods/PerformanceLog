using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PerformanceLog
{
    /// <summary>Everything the summary is made from. Filled by the game side; <see cref="Summary.Render"/> only formats it.</summary>
    public sealed class SummaryInput
    {
        public string SessionId = "", Status = "recording", ModVersion = "", GameVersion = "", StartedLocal = "";
        public double ThresholdMs = 50, SummarySeconds = 10;
        public string Profile = "standard";
        public double Seconds;
        public int Ticks;
        /// <summary>The game's length of one simulation tick in seconds of game time, or 0 if unknown.</summary>
        public double TickIntervalSeconds;
        /// <summary>The session as one row in the layout of an S row (see <see cref="Probe.SessionRow"/>). Null before the first frame.</summary>
        public double[] Row;
        public SessionStats Stats = new SessionStats();
        public List<WorstFrame> Worst = new List<WorstFrame>();
        /// <summary>One entry per summary window, oldest first (see <see cref="Probe.Windows"/>).</summary>
        public List<WindowStat> Windows = new List<WindowStat>();
        public List<Profile.Total> Totals = new List<Profile.Total>();
        /// <summary>Name and value, in the order to show them.</summary>
        public List<KeyValuePair<string, string>> Environment = new List<KeyValuePair<string, string>>();
        /// <summary>Id, name, version.</summary>
        public List<string[]> Mods = new List<string[]>();
        /// <summary>What each measurement source can do and did, one line each.</summary>
        public List<string> Capabilities = new List<string>();
        /// <summary>Things that went wrong or that the reader should know (a patch that could not be made, a source that failed).</summary>
        public List<string> Warnings = new List<string>();
        public string Folder = "";
        public string Colony = "";
    }

    /// <summary>
    /// Writes summary.md: the facts of a session in a form that a person, or a later Claude chat, can read in one go before deciding what
    /// to look at in the CSV files. It states what was measured and what stands out; it does not diagnose. Pure formatting, so it is
    /// tested without the game.
    /// </summary>
    public static class Summary
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        static string F(double value, int decimals = 1) => value.ToString("F" + decimals, C);
        static string Pct(double part, double whole) => whole > 0 ? (100.0 * part / whole).ToString("F1", C) + "%" : "-";

        public static string Render(SummaryInput s)
        {
            var t = new StringBuilder(8192);
            t.Append("# Performance log summary\n\n");
            t.Append("Session `").Append(s.SessionId).Append("`, status **").Append(s.Status).Append("**. ");
            t.Append("This file is rewritten about once a minute while the game runs and once more when the session ends, so it is complete after a clean exit and at most a minute stale after a crash. ");
            t.Append("Read `README.md` in this folder for what every file and column means.\n\n");

            double[] r = s.Row;
            Session(t, s, r);
            if (r == null)
            {
                t.Append("_No frames have been recorded yet._\n\n");
                Tail(t, s);
                return t.ToString();
            }
            Frames(t, s, r);
            WhereFramesGo(t, s, r);
            Ticks(t, s, r);
            Trend(t, s);
            Memory(t, s, r);
            Cpu(t, s, r);
            ProfileTables(t, s);
            WorstFrames(t, s);
            Tail(t, s);
            return t.ToString();
        }

        static void Session(StringBuilder t, SummaryInput s, double[] r)
        {
            t.Append("## Session\n\n| | |\n|---|---|\n");
            void Line(string key, string value) => t.Append("| ").Append(key).Append(" | ").Append(value).Append(" |\n");
            Line("Started", s.StartedLocal);
            Line("Recorded", F(s.Seconds, 0) + " s (" + F(s.Seconds / 60, 1) + " min), " + (r == null ? 0 : (long)r[Columns.Frames]) + " frames, " + s.Ticks + " simulation ticks");
            Line("Game / mod", "Timberborn " + s.GameVersion + " / Performance Log " + s.ModVersion);
            Line("Profile level", s.Profile + " (slow frame = " + F(s.ThresholdMs, 0) + " ms or more; summary rows every " + F(s.SummarySeconds, 0) + " s)");
            if (!string.IsNullOrEmpty(s.Colony)) Line("Colony at the last sample", s.Colony);
            Line("Mods enabled", s.Mods.Count + " (listed at the end)");
            t.Append('\n');
            if (s.Warnings.Count > 0)
            {
                t.Append("**Read first:**\n\n");
                foreach (string w in s.Warnings) t.Append("- ").Append(w).Append('\n');
                t.Append('\n');
            }
        }

        static void Frames(StringBuilder t, SummaryInput s, double[] r)
        {
            double frames = r[Columns.Frames];
            double mean = r[Columns.FrameMs];
            double max = r[Columns.MaxFrameMs];
            double[] counts = new double[Columns.FrameHistCount];
            for (int i = 0; i < counts.Length; i++) counts[i] = r[Columns.FrameHistBase + i];
            t.Append("## Frame rate\n\n");
            t.Append("- Mean frame time **").Append(F(mean)).Append(" ms** (").Append(F(mean > 0 ? 1000 / mean : 0, 0)).Append(" fps on average); ");
            t.Append("median about ").Append(F(Percentile(counts, 0.5, max))).Append(" ms, 90th percentile about ").Append(F(Percentile(counts, 0.9, max)))
                .Append(" ms, 99th about ").Append(F(Percentile(counts, 0.99, max))).Append(" ms, slowest ").Append(F(max)).Append(" ms. ");
            t.Append("(Percentiles are read off the frame-time histogram, so they are only as fine as its buckets.)\n");
            SessionStats st = s.Stats;
            double totalMs = mean * frames;
            t.Append("- **").Append(st.SlowFrames).Append("** frames took ").Append(F(s.ThresholdMs, 0)).Append(" ms or more (").Append(Pct(st.SlowFrames, frames))
                .Append(" of frames, ").Append(Pct(st.SlowMs, totalMs)).Append(" of all frame time). Among them: ")
                .Append(st.SlowGcFrames).Append(" contained a garbage collection, ").Append(st.SlowSaveFrames).Append(" a save, ")
                .Append(st.SlowUnfocusedFrames).Append(" were with the window in the background, ").Append(st.SlowPausedFrames).Append(" were with the game paused.\n");
            t.Append("- The very first frame after the log started took ").Append(F(st.FirstFrameMs, 0)).Append(" ms (start-up cost, not gameplay).\n\n");

            t.Append("| Game speed | Frames | Share | Mean frame time |\n|---|---|---|---|\n");
            for (int i = 0; i < st.SpeedFrames.Length; i++)
            {
                if (st.SpeedFrames[i] == 0) continue;
                t.Append("| ").Append(i == 0 ? "paused" : i == 7 ? "7 or more" : i.ToString(C)).Append(" | ").Append(st.SpeedFrames[i]).Append(" | ")
                    .Append(Pct(st.SpeedFrames[i], frames)).Append(" | ").Append(F(st.SpeedMs[i] / st.SpeedFrames[i])).Append(" ms |\n");
            }
            t.Append("\nFrame times are only comparable at the same game speed: at speed 3 the game runs three simulation ticks' worth of work per second.\n\n");
        }

        static void WhereFramesGo(StringBuilder t, SummaryInput s, double[] r)
        {
            double frames = r[Columns.Frames], mean = r[Columns.FrameMs];
            double ticks = r[Columns.Ticks];
            t.Append("## Where an average frame goes\n\n");
            t.Append("Times are exclusive (a part inside another is taken out of the outer one), so the rows add up to the frame time.\n\n");
            t.Append("| Part | ms per frame | Share of frame | ms per tick | KB per frame |\n|---|---|---|---|---|\n");
            for (int i = 0; i < Columns.SlotCount; i++)
            {
                double ms = r[Columns.SlotBase + i];
                bool perTick = i <= (int)Slot.ParallelStart;
                t.Append("| `").Append(Columns.SlotTimeNames[i]).Append("` | ").Append(F(ms, 2)).Append(" | ").Append(Pct(ms, mean)).Append(" | ")
                    .Append(perTick && ticks > 0 ? F(ms * frames / ticks, 2) : "").Append(" | ").Append(F(r[Columns.AllocBase + i] / Math.Max(1, frames), 1)).Append(" |\n");
            }
            t.Append("| `otherMs` (drawing, other scripts, other mods, the system) | ").Append(F(r[Columns.OtherMs], 2)).Append(" | ").Append(Pct(r[Columns.OtherMs], mean))
                .Append(" | | ").Append(F(r[Columns.OtherKB] / Math.Max(1, frames), 1)).Append(" |\n\n");

            double phases = 0;
            for (int i = 0; i < Columns.PhaseCount; i++) phases += r[Columns.PhaseBase + i];
            if (phases > 0)
            {
                t.Append("Unity's phases of a frame (the wait for vertical sync is in one of them, usually `plPost`):\n\n| Phase | ms per frame | Share |\n|---|---|---|\n");
                for (int i = 0; i < Columns.PhaseCount; i++)
                {
                    double ms = r[Columns.PhaseBase + i];
                    if (ms < 0.005) continue;
                    t.Append("| `").Append(Columns.PhaseNames[i]).Append("` | ").Append(F(ms, 2)).Append(" | ").Append(Pct(ms, mean)).Append(" |\n");
                }
                t.Append('\n');
            }
            else t.Append("_Unity's frame phases were not measured (see the capabilities below)._\n\n");
        }

        static void Ticks(StringBuilder t, SummaryInput s, double[] r)
        {
            double frames = r[Columns.Frames], ticks = r[Columns.Ticks];
            t.Append("## Simulation ticks\n\n");
            if (ticks <= 0) { t.Append("No simulation tick ran while the log was on (the game was paused or in a menu).\n\n"); return; }
            double tickWork = 0;
            for (int i = 0; i <= (int)Slot.ParallelStart; i++) tickWork += r[Columns.SlotBase + i] * frames;
            t.Append("- ").Append(F(ticks, 0)).Append(" ticks in ").Append(F(s.Seconds, 0)).Append(" s, so ").Append(F(ticks / Math.Max(1, s.Seconds))).Append(" ticks per second on average. ");
            if (s.TickIntervalSeconds > 0)
                t.Append("The game's tick is ").Append(F(s.TickIntervalSeconds, 2)).Append(" s of game time, so at speed 1 it runs ").Append(F(1 / s.TickIntervalSeconds)).Append(" ticks per second, proportionally more at higher speeds.");
            t.Append('\n');
            t.Append("- One tick costs **").Append(F(tickWork / ticks, 2)).Append(" ms** on the game thread (tick loop, singletons, entities and the parallel tick's start and wait). ");
            double entities = r[Columns.CounterBase + (int)Counter.Entities];
            t.Append("Entity ticks per simulation tick: ").Append(F(entities / ticks, 0)).Append(".\n");
            double parTick = r[Columns.ParTickMs];
            t.Append("- The game reports its parallel tick (work on the worker threads) at ").Append(F(parTick / ticks, 2)).Append(" ms per tick; the game thread waited ")
                .Append(F(r[Columns.SlotBase + (int)Slot.ParallelWait] * frames / ticks, 2)).Append(" ms per tick for it.\n");
            string[] labels = { "0", "1", "2", "3-4", "5-9", "10+" };
            var parts = new List<string>();
            for (int i = 0; i < Columns.TickHistCount; i++)
                parts.Add(labels[i] + ": " + Pct(r[Columns.TickHistBase + i], frames));
            t.Append("- Ticks run per frame: ").Append(string.Join(", ", parts)).Append(".\n\n");
        }

        static void Trend(StringBuilder t, SummaryInput s)
        {
            List<WindowStat> w = s.Windows;
            if (w.Count < 4) return;
            int segments = Math.Min(8, w.Count / 2);
            t.Append("## How the session changed over time\n\n");
            t.Append("The session cut into ").Append(segments).Append(" equal stretches of time. A frame or tick that gets more expensive as the game goes on, or memory that only climbs, shows here. ");
            t.Append("Compare stretches at the same average speed: a stretch at a higher speed has longer frames.\n\n");
            t.Append("| Stretch | Frames | Mean frame ms | Slowest frame ms | Avg speed | Ticks/s | ms per tick | Collections | Heap MB | Entities | Beavers |\n|---|---|---|---|---|---|---|---|---|---|---|\n");
            double origin = w[0].UtcMs - w[0].Seconds * 1000;
            double span = Math.Max(1, w[w.Count - 1].UtcMs - origin);
            for (int seg = 0; seg < segments; seg++)
            {
                double from = origin + span * seg / segments, to = origin + span * (seg + 1) / segments;
                var inside = w.Where(x => x.UtcMs > from && (x.UtcMs <= to || seg == segments - 1)).ToList();
                if (inside.Count == 0) continue;
                double frames = inside.Sum(x => x.Frames), seconds = inside.Sum(x => x.Seconds), ticks = inside.Sum(x => x.Ticks);
                double mean = frames > 0 ? inside.Sum(x => x.FrameMs * x.Frames) / frames : 0;
                double speed = frames > 0 ? inside.Sum(x => x.Speed * x.Frames) / frames : 0;
                double perTick = ticks > 0 ? inside.Sum(x => x.MsPerTick * x.Ticks) / ticks : 0;
                WindowStat last = inside[inside.Count - 1];
                t.Append("| ").Append(F((from - origin) / 60000, 1)).Append("-").Append(F((to - origin) / 60000, 1)).Append(" min | ").Append(F(frames, 0)).Append(" | ").Append(F(mean)).Append(" | ")
                    .Append(F(inside.Max(x => x.MaxFrameMs), 0)).Append(" | ").Append(F(speed)).Append(" | ").Append(F(seconds > 0 ? ticks / seconds : 0)).Append(" | ").Append(ticks > 0 ? F(perTick, 2) : "")
                    .Append(" | ").Append(F(inside.Sum(x => x.Collections), 0)).Append(" | ").Append(F(last.HeapMB, 0)).Append(" | ").Append(F(last.Entities, 0)).Append(" | ").Append(F(last.Beavers, 0)).Append(" |\n");
            }
            t.Append('\n');
        }

        static void Memory(StringBuilder t, SummaryInput s, double[] r)
        {
            double frames = r[Columns.Frames];
            double gc = r[Columns.GcDelta];
            double seconds = Math.Max(1, s.Seconds);
            SessionStats st = s.Stats;
            t.Append("## Garbage collection and memory\n\n");
            t.Append("- ").Append(F(gc, 0)).Append(" garbage collections (").Append(F(gc / (seconds / 60), 1)).Append(" per minute). The game allocated about ")
                .Append(F(r[Columns.AllocKB] / seconds, 0)).Append(" KB per second (").Append(F(r[Columns.AllocKB] / Math.Max(1, r[Columns.Ticks]), 0)).Append(" KB per tick).\n");
            t.Append("- Managed heap ranged ").Append(F(st.HeapMinMB, 0)).Append(" to ").Append(F(st.HeapMaxMB, 0)).Append(" MB; at the last sample Unity's managed heap was ")
                .Append(F(r[Columns.HeavyBase], 0)).Append(" MB reserved, ").Append(F(r[Columns.HeavyBase + 1], 0)).Append(" MB in use, ").Append(F(r[Columns.HeavyBase + 2], 0))
                .Append(" MB in all with native memory, and the process held ").Append(F(r[Columns.HeavyBase + 3], 0)).Append(" MB in RAM.\n");
            t.Append("- Allocation by part (KB per tick, from the timed slots; coarse if the allocation source is the heap size): ");
            var alloc = new List<KeyValuePair<string, double>>();
            double ticks = Math.Max(1, r[Columns.Ticks]);
            for (int i = 0; i < Columns.SlotCount; i++) alloc.Add(new KeyValuePair<string, double>(Columns.SlotAllocNames[i], r[Columns.AllocBase + i] / ticks));
            alloc.Add(new KeyValuePair<string, double>("otherKB", r[Columns.OtherKB] / ticks));
            t.Append(string.Join(", ", alloc.Where(p => p.Value >= 0.5).OrderByDescending(p => p.Value).Take(6).Select(p => p.Key + " " + F(p.Value, 0)))).Append(".\n\n");
        }

        static void Cpu(StringBuilder t, SummaryInput s, double[] r)
        {
            double mean = r[Columns.FrameMs];
            double main = r[Columns.MainCpuMs], proc = r[Columns.ProcCpuMs];
            t.Append("## Is the game thread working or waiting?\n\n");
            if (main <= 0 && proc <= 0) t.Append("Processor time was not available on this computer.\n\n");
            else
            {
                t.Append("- The game thread used ").Append(F(main)).Append(" ms of processor time per ").Append(F(mean)).Append(" ms frame (").Append(Pct(main, mean))
                    .Append(" busy). The whole process used ").Append(F(proc)).Append(" ms, or ").Append(F(mean > 0 ? proc / mean : 0, 1)).Append(" cores' worth. ");
                t.Append("A game thread well under 100% busy in a slow session is waiting (for the graphics card, vertical sync or the worker threads), not computing.\n");
                if (r[Columns.ExtraBase + 7] > 0)
                    t.Append("- Unity's frame timing: main thread ").Append(F(r[Columns.ExtraBase + 7])).Append(" ms, render thread ").Append(F(r[Columns.ExtraBase + 8])).Append(" ms, graphics card ")
                        .Append(F(r[Columns.ExtraBase + 9])).Append(" ms, main thread waiting to present ").Append(F(r[Columns.ExtraBase + 10])).Append(" ms.\n");
                if (r[Columns.ExtraBase + 2] > 0)
                    t.Append("- Drawing, per frame: ").Append(F(r[Columns.ExtraBase + 2], 0)).Append(" draw calls, ").Append(F(r[Columns.ExtraBase + 3], 0)).Append(" set-pass calls, ")
                        .Append(F(r[Columns.ExtraBase + 4], 0)).Append(" batches, ").Append(F(r[Columns.ExtraBase + 5] / 1000, 0)).Append(" thousand triangles.\n");
                t.Append('\n');
            }
        }

        static void ProfileTables(StringBuilder t, SummaryInput s)
        {
            t.Append("## Where the time goes, by part of the game or mod\n\n");
            if (s.Totals.Count == 0)
            {
                t.Append("_The profile is empty: no singleton, entity or watched method was timed (see the capabilities below)._\n\n");
                return;
            }
            double seconds = Math.Max(1, s.Seconds);
            t.Append("`ms/s` is milliseconds of the game thread's time per second of play, so 10 ms/s is one hundredth of a core. Singletons are timed on every call; entity kinds, components and watched methods are sampled and scaled up (see `profile.csv`).\n\n");

            void Table(string title, Func<Profile.Total, bool> where, int take)
            {
                var rows = s.Totals.Where(where).OrderByDescending(x => x.Ms).Take(take).ToList();
                if (rows.Count == 0) return;
                double all = s.Totals.Where(where).Sum(x => x.Ms);
                t.Append("**").Append(title).Append("** (together ").Append(F(all / seconds)).Append(" ms/s)\n\n| Name | Mod | ms/s | Share | calls/s | us per call | KB/s | slowest call ms |\n|---|---|---|---|---|---|---|---|\n");
                foreach (Profile.Total x in rows)
                    t.Append("| `").Append(Short(x.Name)).Append("` | ").Append(x.Mod).Append(" | ").Append(F(x.Ms / seconds, 2)).Append(" | ").Append(Pct(x.Ms, all)).Append(" | ")
                        .Append(F(x.Calls / seconds, 0)).Append(" | ").Append(x.Calls > 0 ? F(x.Ms * 1000 / x.Calls, 1) : "").Append(" | ").Append(F(x.Kb / seconds, 1)).Append(" | ").Append(F(x.MaxMs, 2)).Append(" |\n");
                t.Append('\n');
            }
            Table("Singletons ticked once per simulation tick", x => x.Kind == ProfileKind.TickSingleton, 15);
            Table("Singletons updated once per frame", x => x.Kind == ProfileKind.UpdateSingleton, 15);
            Table("Singletons late-updated once per frame", x => x.Kind == ProfileKind.LateSingleton, 8);
            Table("Parallel singletons: the game thread starting them", x => x.Kind == ProfileKind.ParallelStart, 8);
            Table("Entity kinds (sampled)", x => x.Kind == ProfileKind.Entity, 15);
            Table("Entity components (sampled; Profile = deep only)", x => x.Kind == ProfileKind.Component, 15);
            Table("Watched methods (config Watch)", x => x.Kind == ProfileKind.Method, 15);

            var mods = s.Totals.Where(x => x.Kind == ProfileKind.TickSingleton || x.Kind == ProfileKind.UpdateSingleton || x.Kind == ProfileKind.LateSingleton)
                .GroupBy(x => string.IsNullOrEmpty(x.Mod) ? "(unknown)" : x.Mod).Select(g => new { Mod = g.Key, Ms = g.Sum(x => x.Ms), Kb = g.Sum(x => x.Kb) })
                .OrderByDescending(g => g.Ms).Take(12).ToList();
            if (mods.Count > 0)
            {
                t.Append("**Singleton time by mod** (tick, update and late-update singletons together; 'game' is Timberborn itself)\n\n| Mod | ms/s | KB/s |\n|---|---|---|\n");
                foreach (var m in mods) t.Append("| ").Append(m.Mod).Append(" | ").Append(F(m.Ms / seconds, 2)).Append(" | ").Append(F(m.Kb / seconds, 1)).Append(" |\n");
                t.Append('\n');
            }
            var loads = s.Totals.Where(x => x.Kind >= ProfileKind.Load).OrderByDescending(x => x.Ms).Take(10).ToList();
            if (loads.Count > 0)
            {
                t.Append("**Slowest steps of loading the game** (one time; ").Append(F(s.Totals.Where(x => x.Kind >= ProfileKind.Load).Sum(x => x.Ms), 0)).Append(" ms in all steps)\n\n| Step | Name | Mod | ms |\n|---|---|---|---|\n");
                foreach (Profile.Total x in loads)
                    t.Append("| ").Append(ProfileKinds.Words[(int)x.Kind]).Append(" | `").Append(Short(x.Name)).Append("` | ").Append(x.Mod).Append(" | ").Append(F(x.Ms, 1)).Append(" |\n");
                t.Append('\n');
            }
        }

        static void WorstFrames(StringBuilder t, SummaryInput s)
        {
            t.Append("## The slowest frames\n\n");
            if (s.Worst.Count == 0) { t.Append("No frame reached the slow-frame threshold.\n\n"); return; }
            t.Append("`frames.csv` has a row for every slow frame and `spikes.csv` the biggest contributors to each; these are the worst ").Append(s.Worst.Count)
                .Append(". `Biggest parts` are the timed slots of the frame; `Blame` are the singletons that spent the most time in it.\n\n");
            t.Append("| Frame | Tick | Frame ms | Speed | Ticks | GC | Save | Biggest parts | Blame |\n|---|---|---|---|---|---|---|---|---|\n");
            foreach (WorstFrame w in s.Worst)
            {
                double[] r = w.Row;
                var parts = new List<KeyValuePair<string, double>>();
                for (int i = 0; i < Columns.SlotCount; i++) parts.Add(new KeyValuePair<string, double>(Columns.SlotTimeNames[i], r[Columns.SlotBase + i]));
                parts.Add(new KeyValuePair<string, double>("otherMs", r[Columns.OtherMs]));
                string top = string.Join(", ", parts.OrderByDescending(p => p.Value).Take(3).Select(p => p.Key + " " + F(p.Value, 0)));
                var blame = new List<string>();
                for (int i = 0; i < w.TopIds.Length && i < 3; i++)
                    blame.Add(Short(Profile.NameOf(w.TopIds[i])) + " " + F(w.TopMs[i], 0));
                t.Append("| ").Append(F(r[Columns.Frame], 0)).Append(" | ").Append(F(r[Columns.Tick], 0)).Append(" | ").Append(F(r[Columns.FrameMs], 0)).Append(" | ")
                    .Append(F(r[Columns.Speed], 0)).Append(" | ").Append(F(r[Columns.Ticks], 0)).Append(" | ").Append(r[Columns.GcDelta] > 0 ? "yes" : "").Append(" | ")
                    .Append(r[Columns.Saving] > 0 ? "yes" : "").Append(" | ").Append(top).Append(" | ").Append(string.Join(", ", blame)).Append(" |\n");
            }
            t.Append('\n');
        }

        static void Tail(StringBuilder t, SummaryInput s)
        {
            if (s.Capabilities.Count > 0)
            {
                t.Append("## What each measurement source could do\n\n");
                foreach (string c in s.Capabilities) t.Append("- ").Append(c).Append('\n');
                t.Append("\nA source that says it produced nothing leaves its columns at 0; do not read a 0 in them as a measurement.\n\n");
            }
            t.Append("## Computer and game settings\n\n");
            foreach (var e in s.Environment) t.Append("- **").Append(e.Key).Append(":** ").Append(e.Value).Append('\n');
            t.Append('\n');
            t.Append("## Mods enabled\n\n| Id | Name | Version |\n|---|---|---|\n");
            foreach (string[] m in s.Mods.OrderBy(m => m[0], StringComparer.Ordinal))
                t.Append("| ").Append(m[0]).Append(" | ").Append(m[1]).Append(" | ").Append(m[2]).Append(" |\n");
            t.Append("\nWhich mod patches which hot method of the game is in the header of `frames.csv` (`# patch|` lines).\n\n");
            t.Append("## Files\n\n- `README.md`: what the files and columns mean.\n- `summary.md`: this file.\n- `frames.csv`: one row per slow frame (`F`) and per summary window (`S`).\n" +
                     "- `profile.csv`: per window, the time and allocation of each singleton, entity kind, component and watched method.\n" +
                     "- `spikes.csv`: for each slow frame, the singletons that took most of it.\n- `events.csv`: saves, loading and other one-off events.\n");
            if (!string.IsNullOrEmpty(s.Folder)) t.Append("\nFolder: `").Append(s.Folder).Append("`\n");
        }

        /// <summary>A class name without its namespace when it is long, so a table stays readable.</summary>
        public static string Short(string name)
        {
            if (string.IsNullOrEmpty(name)) return "?";
            if (name.Length <= 48) return name;
            int dot = name.LastIndexOf('.');
            string tail = dot >= 0 ? name.Substring(dot + 1) : name;
            return tail.Length > 60 ? tail.Substring(0, 60) : tail;
        }

        /// <summary>
        /// The frame time below which the fraction <paramref name="p"/> of the frames fell, worked out from the histogram
        /// (linear inside a bucket). The last bucket is open at the top, so <paramref name="max"/> stands in for its upper edge.
        /// </summary>
        public static double Percentile(double[] counts, double p, double max)
        {
            double total = 0;
            foreach (double c in counts) total += c;
            if (total <= 0) return 0;
            double target = total * p, running = 0;
            for (int i = 0; i < counts.Length; i++)
            {
                double next = running + counts[i];
                if (next >= target && counts[i] > 0)
                {
                    double lower = i == 0 ? 0 : Columns.FrameEdgesMs[i - 1];
                    double upper = i < Columns.FrameEdgesMs.Length ? Columns.FrameEdgesMs[i] : Math.Max(max, lower);
                    return lower + (upper - lower) * ((target - running) / counts[i]);
                }
                running = next;
            }
            return max;
        }
    }
}
