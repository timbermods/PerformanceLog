using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Timberborn.Modding;
using Timberborn.PlatformUtilities;
using UnityEngine;

namespace PerformanceLog
{
    /// <summary>Game objects the session reads. All optional: anything that is missing or throws reads as unknown.</summary>
    internal sealed class SessionServices
    {
        public ModRepository Mods;
        public Func<float> Speed = () => 1f;
        public Func<double[]> Colony = () => null;
        public Func<double> TickIntervalSeconds = () => 0;
    }

    /// <summary>
    /// One log, from the moment a game has finished loading to the moment it is left. It exists only when the mod is enabled in the
    /// config, and it only observes: the timing lives in <see cref="Probe"/> and <see cref="Profile"/>, and the files are written by
    /// <see cref="LogWriter"/> on a thread of its own. Anything that goes wrong is logged and switches off that part, never the game.
    /// </summary>
    internal static class Session
    {
        const int FrameRingRows = 512, ProfileRingRows = 4096, SpikeRingRows = 1024;
        const double SummaryRefreshSeconds = 60;

        static Config config;
        static SessionServices services;
        static LogWriter writer;
        static TextChannel events;
        static string folder, summaryPath, sessionName, startedLocal, columnsPath;
        static long nextSummaryRefresh;
        static bool quittingHooked;
        static double[] lastColony;
        static List<KeyValuePair<string, string>> environment;
        static List<string[]> modList;
        static long startedTicks;
        static bool markersRetried;
        static List<string> finalLines = new List<string>();
        static readonly List<string> warnings = new List<string>();

        internal static bool Active => writer != null;
        internal static string Folder => folder;

        // ---- start ----

        internal static void Start(Config cfg, SessionServices context, string modVersion)
        {
            Stop("a new session started");
            config = cfg; services = context;
            if (cfg == null || !cfg.Enabled) return;
            bool started = false;
            try
            {
                Milestones.Mark("session-start");
                warnings.Clear(); finalLines = new List<string>(); lastColony = null;
                Instrumentation.ResetForSession();
                // What does not change during a session is read once, so refreshing the summary does not have to ask the system again.
                environment = EnvironmentInfo.Collect();
                modList = services.Mods != null ? EnvironmentInfo.Mods(services.Mods) : new List<string[]>();
                string root = string.IsNullOrWhiteSpace(cfg.OutputFolder) ? Path.Combine(UserDataFolder.Folder, "PerformanceLog") : cfg.OutputFolder;
                DateTime now = DateTime.Now;
                sessionName = now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
                folder = Path.Combine(root, sessionName);
                for (int n = 2; Directory.Exists(folder); n++) folder = Path.Combine(root, sessionName + "-" + n);
                Directory.CreateDirectory(folder);
                startedLocal = now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture) + " local, " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";

                // Find out what this computer can measure, and what measuring costs, before anything is written.
                Alloc.Init();
                Cpu.Init();
                Probe.Calibrate();
                Profile.BudgetFraction = cfg.OverheadBudgetPercent / 100.0;
                Dictionary<string, string> owners = services.Mods != null ? EnvironmentInfo.AssemblyOwners(services.Mods) : new Dictionary<string, string>();
                // A mod's DLL names the mod; anything else is the game's own if its assembly says so, and otherwise unknown.
                Profile.ModResolver = Profile.ResolverFor(owners);
                UnityExtras.Start();
                UnityExtras.ColonySampler = SampleColony;
                PlayerLoopTiming.Install();
                startedTicks = Stopwatch.GetTimestamp(); markersRetried = false;

                var frames = new Ring(Columns.Count, FrameRingRows);
                var profile = new Ring(Profile.Table.Count, ProfileRingRows);
                var spikes = new Ring(Profile.SpikeTable.Count, SpikeRingRows);
                profileRingForLoad = profile;
                string framesPath = Path.Combine(folder, "frames.csv"), profilePath = Path.Combine(folder, "profile.csv"),
                    spikesPath = Path.Combine(folder, "spikes.csv"), eventsPath = Path.Combine(folder, "events.csv"), readmePath = Path.Combine(folder, "README.md"),
                    columnsFile = Path.Combine(folder, "columns.md");
                columnsPath = columnsFile;
                summaryPath = Path.Combine(folder, "summary.md");

                var newWriter = new LogWriter(message => Log.Warning(message));
                newWriter.AddTable(framesPath, BuildHeader(modVersion), Columns.Main, frames, null, w => { foreach (string line in finalLines) w.WriteLine(line); });
                newWriter.AddTable(profilePath, SmallHeader("profile", modVersion), Profile.Table, profile, Profile.AppendProfileText);
                newWriter.AddTable(spikesPath, SmallHeader("spikes", modVersion), Profile.SpikeTable, spikes, Profile.AppendSpikeText);
                events = newWriter.AddText(eventsPath, "utcMs,tick,kind,ms,detail");
                newWriter.AddFile(summaryPath);
                newWriter.AddFile(readmePath);
                newWriter.AddFile(columnsFile);
                if (!newWriter.Start())
                {
                    Log.Warning("The performance log could not be started: " + newWriter.Failure);
                    Cleanup();
                    return;
                }
                writer = newWriter;
                if (newWriter.Failure != null) Warn("A file of the log could not be opened: " + newWriter.Failure);

                Probe.Start(new ProbeSettings
                {
                    Frames = frames, Profile = profile, Spikes = spikes, GameThreadId = Thread.CurrentThread.ManagedThreadId,
                    ThresholdMs = cfg.SlowFrameMs, SummarySeconds = cfg.SummarySeconds, ProfileSeconds = cfg.ProfileSeconds, SpikeContributors = cfg.SpikeContributors,
                    MaxSlowRowsPerMinute = cfg.MaxSlowRowsPerMinute,
                });
                Watch.OnSessionStart();
                started = true;

                writer.SetFile(readmePath, SessionReadme.Render(ReadTemplate(), modVersion));
                writer.SetFile(columnsPath, SessionReadme.RenderColumns(modVersion));
                Event("session-start", 0, "Performance Log " + modVersion + ", profile " + cfg.Profile);
                FlushLoad();
                nextSummaryRefresh = Stopwatch.GetTimestamp() + (long)(SummaryRefreshSeconds * Stopwatch.Frequency);
                RefreshSummary("recording");
                if (!quittingHooked)
                {
                    quittingHooked = true;
                    Application.quitting += () => Stop("the game was closed", quitting: true);
                }
                Log.Info("Recording to " + folder);
            }
            catch (Exception error)
            {
                Log.Warning("The performance log could not be started: " + error.Message);
                if (!started) Cleanup();
            }
        }

        static void Cleanup()
        {
            try { PlayerLoopTiming.Uninstall(); } catch (Exception) { }
            try { UnityExtras.Stop(); } catch (Exception) { }
            try { Probe.Stop(); } catch (Exception) { }
            try { writer?.Stop(); } catch (Exception) { }
            writer = null; events = null;
        }

        static string ReadTemplate()
        {
            try
            {
                using (Stream stream = typeof(Session).Assembly.GetManifestResourceStream("SESSION-README.md"))
                {
                    if (stream != null)
                        using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
                }
            }
            catch (Exception) { }
            return "# Timberborn performance log {{VERSION}}\n\nThe readme could not be found inside the mod. See https://github.com/timbermods/PerformanceLog for how to read these files; columns.md in this folder defines the columns.\n";
        }

        static List<string> SmallHeader(string kind, string modVersion)
        {
            var header = new HeaderBuilder(kind, 1);
            header.Add("session", sessionName);
            header.Add("mod", modVersion);
            header.Note("Read README.md in this folder for what the columns mean.");
            return header.Lines;
        }

        static List<string> BuildHeader(string modVersion)
        {
            var header = new HeaderBuilder("frames", HeaderBuilder.FramesFormat);
            header.Add("session", sessionName);
            header.Add("started", startedLocal);
            header.Add("mod", modVersion);
            foreach (var pair in environment) header.Add(pair.Key, pair.Value);
            header.Add("profile", config.Profile);
            header.Add("thresholdMs", config.SlowFrameMs.ToString(CultureInfo.InvariantCulture));
            header.Add("summarySeconds", config.SummarySeconds.ToString(CultureInfo.InvariantCulture));
            header.Add("profileSeconds", config.ProfileSeconds.ToString(CultureInfo.InvariantCulture));
            header.Add("clockHz", Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture));
            double tick = 0;
            try { tick = services.TickIntervalSeconds(); } catch (Exception) { }
            header.Add("tickSeconds", tick.ToString("F2", CultureInfo.InvariantCulture));
            header.Add("entityBucketsPerTick", Probe.EntityBucketsPerTick.ToString(CultureInfo.InvariantCulture));
            header.Add("config", config.ToString());
            header.Note("times are exclusive, so the slots never overlap: tickMs is the tick loop minus singMs, entMs, parWaitMs and parStartMs; otherMs is the rest of the frame. See README.md.");
            header.Note("in summary (S) rows times and Unity's figures are averages per frame; allocation (KB), counts and the two histograms are totals over the window's frames.");
            foreach (string problem in config.Problems) header.Note("config: " + problem);

            header.Pipe("capability", "allocSource", Alloc.Describe());
            header.Pipe("capability", "cpuTimes", Cpu.Available ? "available" : "unavailable (Windows only)");
            header.Pipe("capability", "workingSet", ProcessMemory.Describe());
            header.Append(UnityExtras.StartLines());
            header.Pipe("capability", "playerLoop", PlayerLoopTiming.Installed > 0 ? "timing " + PlayerLoopTiming.Installed + " phases" : "not installed");
            foreach (string result in Instrumentation.Results)
            {
                string[] parts = result.Split('|');
                header.Pipe("capability", "patch", parts[0], parts.Length > 1 ? parts[1] : "", parts.Length > 2 ? parts[2] : "");
            }
            foreach (string result in Watch.Results) { string[] parts = result.Split('|'); header.Pipe("watch", parts[0], parts.Length > 1 ? parts[1] : ""); }
            header.Pipe("calibration", Probe.CalibrationParts());
            header.Pipe("sampling", "budgetPercent", config.OverheadBudgetPercent.ToString(CultureInfo.InvariantCulture),
                "note", "the intervals are chosen again every profile window; profile.csv says how many calls each row rests on");
            header.Pipe("histogram", "frameEdgesMs", string.Join(",", Columns.FrameEdgesMs.Select(e => e.ToString(CultureInfo.InvariantCulture))));
            header.Pipe("histogram", "ticksPerFrameBuckets", "0,1,2,3-4,5-9,10+");
            header.Pipe("phases", string.Join(",", Columns.PhaseNames), "each is the time from the start to the end of that phase of Unity's frame; the vertical sync wait is in one of them");
            header.Append(Milestones.Lines());
            foreach (string line in EnvironmentInfo.BootConfig()) header.Pipe("bootconfig", line);
            foreach (string arg in EnvironmentInfo.CommandLine()) header.Pipe("cmdline", arg);
            List<string[]> mods = modList;
            header.Add("mods", mods.Count.ToString(CultureInfo.InvariantCulture));
            foreach (string[] mod in mods.OrderBy(m => m[0], StringComparer.Ordinal)) header.Mod(mod[0], mod[1], mod[2]);
            var patches = new List<string>();
            PatchReporter.Append(patches);
            header.Append(patches);
            return header.Lines;
        }

        // ---- during the game ----

        /// <summary>Called at the start of each of Unity's frames, from the player loop.</summary>
        internal static void OnFrameStart()
        {
            if (!Probe.Enabled) return;
            try
            {
                UnityExtras.Sample();
                float speed = 1f;
                try { speed = services.Speed(); } catch (Exception) { }
                Probe.OnFrame(speed, Application.isFocused);
                if (!Probe.Enabled)
                {
                    Warn("The measuring switched itself off: " + Probe.LastFailure);
                    return;
                }
                if (Stopwatch.GetTimestamp() >= nextSummaryRefresh)
                {
                    nextSummaryRefresh = Stopwatch.GetTimestamp() + (long)(SummaryRefreshSeconds * Stopwatch.Frequency);
                    RefreshSummary("recording");
                }
            }
            catch (Exception error)
            {
                Probe.Stop();
                Warn("The measuring stopped: " + error.Message);
            }
        }

        /// <summary>A one-off event for events.csv. Callable from the game thread.</summary>
        internal static void Event(string kind, double ms, string detail)
        {
            try
            {
                TextChannel channel = events;
                if (channel == null) return;
                long utc = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                channel.Write(utc.ToString(CultureInfo.InvariantCulture) + "," + Probe.TickCount.ToString(CultureInfo.InvariantCulture) + "," + kind + "," +
                              ms.ToString("F1", CultureInfo.InvariantCulture) + ",\"" + (detail ?? "").Replace('"', '\'').Replace('\r', ' ').Replace('\n', ' ') + "\"");
            }
            catch (Exception) { }
        }

        static void Warn(string message)
        {
            if (!warnings.Contains(message)) warnings.Add(message);
            Log.Warning(message);
            Event("warning", 0, message);
        }

        /// <summary>Loading has finished: write down the steps taken since the last time.</summary>
        internal static void OnLoadFinished()
        {
            if (!Active) return;
            FlushLoad();
            Event("load", LoadRecorder.TotalTicks * 1000.0 / Stopwatch.Frequency, "the game's singletons loaded (Load, non-singleton loaders, PostLoad)");
        }

        static void FlushLoad()
        {
            try
            {
                var steps = new List<LoadRecorder.Step>();
                var phases = new List<KeyValuePair<string, long>>();
                LoadRecorder.TakeNew(steps, phases);
                foreach (LoadRecorder.Step step in steps) Profile.WriteLoadRow(step.Kind, step.Name, step.Assembly, step.Ticks, 0, ProfileRing(), step.Bytes);
                foreach (var phase in phases) Event("load-phase", phase.Value * 1000.0 / Stopwatch.Frequency, phase.Key);
            }
            catch (Exception e) { Log.Warning("Could not write the load steps: " + e.Message); }
        }

        // The profile ring belongs to the probe; the load steps go to the same file through it.
        static Ring ProfileRing() => profileRingForLoad;
        static Ring profileRingForLoad;

        static double[] SampleColony()
        {
            try
            {
                double[] colony = services.Colony();
                if (colony != null) lastColony = colony;
                return colony;
            }
            catch (Exception) { return null; }
        }

        // ---- the summary ----

        static void RefreshSummary(string status)
        {
            try
            {
                if (writer == null) return;
                // The snapshot is taken here, on the game thread; the text is made on the writer thread so it costs the frame nothing.
                SummaryInput input = BuildSummaryInput(status);
                writer.SetFile(summaryPath, () => Summary.Render(input));
            }
            catch (Exception e) { Log.Warning("Could not write the summary: " + e.Message); }
        }

        static SummaryInput BuildSummaryInput(string status)
        {
            var input = new SummaryInput
            {
                SessionId = sessionName, Status = status, StartedLocal = startedLocal, Folder = folder,
                ModVersion = Plugin.Version, ThresholdMs = config.SlowFrameMs, SummarySeconds = config.SummarySeconds, Profile = config.Profile,
                Seconds = Probe.SessionSeconds, Ticks = Probe.TickCount, Row = Probe.SessionRow(), Stats = Probe.Stats.Clone(),
                Worst = Probe.WorstFrames(), Windows = Probe.Windows(), Totals = Profile.Totals(),
            };
            try { input.TickIntervalSeconds = services.TickIntervalSeconds(); } catch (Exception) { }
            foreach (var pair in environment)
            {
                if (pair.Key == "game") input.GameVersion = pair.Value;
                input.Environment.Add(pair);
            }
            input.Mods = new List<string[]>(modList);
            double[] colony = lastColony;
            if (colony != null && colony.Length >= 4)
                input.Colony = colony[1].ToString("F0", CultureInfo.InvariantCulture) + " beavers, " + colony[2].ToString("F0", CultureInfo.InvariantCulture) + " bots, " +
                               colony[0].ToString("F0", CultureInfo.InvariantCulture) + " entities, day " + colony[3].ToString("F0", CultureInfo.InvariantCulture);
            input.Capabilities.Add("allocation source: " + Alloc.Describe());
            input.Capabilities.Add("processor times: " + (Cpu.Available ? "available" : "unavailable (Windows only)"));
            input.Capabilities.Add("Unity's frame phases: " + (PlayerLoopTiming.Installed > 0 ? "timed (" + PlayerLoopTiming.Installed + " phases)" : "NOT measured"));
            foreach (string line in UnityExtras.StartLines()) input.Capabilities.Add(line.Replace("# capability|", "").Replace("|", ": "));
            input.Capabilities.Add("patches on the game: " + Instrumentation.Installed + " installed, " + Instrumentation.Failed + " could not be made; each patch's calls so far are listed at the end of frames.csv");
            foreach (string line in Watch.Results) input.Capabilities.Add("watch " + line.Replace("|", ": "));

            foreach (string w in warnings) input.Warnings.Add(w);
            foreach (string result in Instrumentation.Results)
                if (result.Contains("|not installed")) input.Warnings.Add("Patch " + result.Split('|')[0] + " could not be made, so " + (result.Split('|').Length > 2 ? result.Split('|')[2] : "part of the log") + " is missing.");
            double[] r = input.Row;
            if (r != null && r[Columns.FrameMs] > 0 && (r[Columns.OverheadUs] + r[Columns.ProbeUs]) / 1000.0 / r[Columns.FrameMs] > 0.02)
                input.Warnings.Add("Measuring itself cost about " + ((r[Columns.OverheadUs] + r[Columns.ProbeUs]) / 10.0 / r[Columns.FrameMs]).ToString("F1", CultureInfo.InvariantCulture) + "% of a frame; treat small differences with care.");
            if (r != null && r[Columns.Unfocused] > 0.2 * r[Columns.Frames])
                input.Warnings.Add("The game window was in the background for " + (100 * r[Columns.Unfocused] / r[Columns.Frames]).ToString("F0", CultureInfo.InvariantCulture) + "% of the frames, and the system throttles a background window.");
            if (Probe.LastFailure != null) input.Warnings.Add("The measuring switched itself off: " + Probe.LastFailure);
            if (writer?.Failure != null) input.Warnings.Add("A file of the log had a problem: " + writer.Failure);
            return input;
        }

        // ---- stop ----

        /// <summary>
        /// Called each frame, from the tick loop's patch, until the first frame has been closed. If none has after a few seconds, another mod has probably replaced
        /// Unity's player loop and taken the frame markers with it: say so, and put them back once.
        /// </summary>
        internal static void CheckFramesArriving()
        {
            if (markersRetried || Probe.FrameNumber > 0 || !Probe.Enabled) return;
            if (Stopwatch.GetTimestamp() - startedTicks < 3 * Stopwatch.Frequency) return;
            markersRetried = true;
            Warn("No frame reached the log three seconds after it started. Another mod may have replaced Unity's player loop, so the frame markers are being put back once.");
            PlayerLoopTiming.Install();
        }

        internal static void Stop(string reason, bool quitting = false)
        {
            if (writer == null && !Probe.Enabled) return;
            try
            {
                // What each source produced has to be read here, on the game thread, before they are shut down.
                var final = new List<string>(UnityExtras.FinalLines());
                final.Add("# capability-final|playerLoop|" + (PlayerLoopTiming.Installed > 0 ? "timed " + PlayerLoopTiming.Installed + " phases" : "not installed"));
                final.Add(Probe.AllocFinalLine());
                for (int i = 0; i < Instrumentation.HitCount; i++)
                    final.Add("# capability-final|patchCalls|" + Instrumentation.HitName(i) + "|" +
                              (Instrumentation.InstalledHit[i] ? Instrumentation.Hits[i] + (Instrumentation.Hits[i] == 0 ? "|never ran" : "") : "0|patch not installed"));
                final.Add("# capability-final|profile|" + Profile.Totals().Count + " keys were timed");
                finalLines = final;
                Event("session-end", 0, reason);
                // Not while the game is quitting: the engine may already be taking the player loop down.
                if (!quitting) PlayerLoopTiming.Uninstall();
                Probe.Stop();
                UnityExtras.Stop();
                if (Probe.LastFailure != null) Log.Warning("The performance log switched itself off: " + Probe.LastFailure);
                RefreshSummary("finished");
                writer?.Stop();
                if (writer?.Failure != null) Log.Warning("The performance log had a problem: " + writer.Failure);
                else Log.Info("Finished: " + folder);
            }
            catch (Exception error)
            {
                Log.Warning("Could not finish the performance log cleanly: " + error.Message);
            }
            finally
            {
                // Nothing a finished session held may keep the game it measured alive: the delegates in `services` reach the whole colony.
                writer = null; events = null; profileRingForLoad = null;
                services = null; environment = null; modList = null; lastColony = null;
                UnityExtras.ColonySampler = null;
                Profile.ModResolver = null;
                Milestones.Clear();
            }
        }
    }
}
