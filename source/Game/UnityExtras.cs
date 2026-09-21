using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;

namespace PerformanceLog
{
    /// <summary>
    /// What Unity itself can say about a frame: draw calls and allocation counters from its profiler, the main thread's wait for the
    /// graphics card from its frame timing, and the sizes of its memory pools. Each source is optional: a release build may not provide it,
    /// so each says whether it worked (in the header and again at the end of the file) and a source that does not work is left at zero.
    /// </summary>
    internal static class UnityExtras
    {
        /// <summary>One per-frame column fed from Unity's profiler: usually one counter, but a build that renamed it can feed it from several that are added up.</summary>
        struct Counter
        {
            public string Name;
            public ProfilerRecorder[] Recorders;
            public string[] Started;
            public long Largest;
            public bool Valid => Recorders != null && Recorders.Length > 0;
        }

        static readonly List<Counter> counters = new List<Counter>();
        static readonly FrameTiming[] timings = new FrameTiming[1];
        static bool frameTimingOn, frameTimingSeen;
        static int frames;

        /// <summary>The colony's size, refreshed by the session when a row is about to be written. Zero until known.</summary>
        internal static Func<double[]> ColonySampler;

        // The order is the order of the per-frame extra columns: prGcBytes, prGcCount, prDraw, prSetPass, prBatches, prTris.
        // Name is the counter Unity documents. If a build does not have it, the Instead counters are started and added up; they are the names
        // Unity 6 gives the pieces of the old ones (checked against the strings in this game's UnityPlayer.dll). "GC Allocated In Frame" and
        // "Batches Count" are simply not in that player, so those columns stay 0 and the header says so.
        static readonly (ProfilerCategory Category, string Name, string[] Instead)[] wanted =
        {
            (ProfilerCategory.Memory, "GC Allocated In Frame", new string[0]),
            (ProfilerCategory.Memory, "GC Allocation In Frame Count", new string[0]),
            (ProfilerCategory.Render, "Draw Calls Count", new[]
            {
                "Standard Draw Calls Count", "Standard Instanced Draw Calls Count", "SRP Batcher Draw Calls Count", "Standard Indirect Draw Calls Count",
                "BRG Draw Calls Count", "BRG Indirect Draw Calls Count", "Null Geometry Draw Calls Count", "Null Geometry Indirect Draw Calls Count",
            }),
            (ProfilerCategory.Render, "SetPass Calls Count", new string[0]),
            (ProfilerCategory.Render, "Batches Count", new string[0]),
            (ProfilerCategory.Render, "Triangles Count", new string[0]),
        };

        static bool TryStart(ProfilerCategory category, string name, out ProfilerRecorder recorder)
        {
            recorder = default;
            try
            {
                recorder = ProfilerRecorder.StartNew(category, name);
                return recorder.Valid;
            }
            catch (Exception) { return false; }
        }

        internal static void Start()
        {
            Stop();
            frames = 0;
            foreach (var (category, name, instead) in wanted)
            {
                var counter = new Counter { Name = name };
                var recorders = new List<ProfilerRecorder>();
                var started = new List<string>();
                if (TryStart(category, name, out ProfilerRecorder recorder)) { recorders.Add(recorder); started.Add(name); }
                else
                {
                    DisposeQuietly(recorder);
                    foreach (string other in instead)
                    {
                        if (TryStart(category, other, out ProfilerRecorder piece)) { recorders.Add(piece); started.Add(other); }
                        else DisposeQuietly(piece);
                    }
                }
                counter.Recorders = recorders.ToArray();
                counter.Started = started.ToArray();
                counters.Add(counter);
            }
            try { frameTimingOn = FrameTimingManager.IsFeatureEnabled(); }
            catch (Exception) { frameTimingOn = false; }
            frameTimingSeen = false;
            Probe.HeavySampler = Heavy;
        }

        static void DisposeQuietly(ProfilerRecorder recorder)
        {
            try { if (recorder.Valid) recorder.Dispose(); } catch (Exception) { }
        }

        internal static void Stop()
        {
            foreach (Counter counter in counters)
                if (counter.Recorders != null)
                    foreach (ProfilerRecorder recorder in counter.Recorders) DisposeQuietly(recorder);
            counters.Clear();
            Probe.HeavySampler = null;
        }

        /// <summary>Reads this frame's values into the probe. Called once per frame, just before the frame is closed.</summary>
        internal static void Sample()
        {
            frames++;
            double[] extra = Probe.Extra;
            try
            {
                for (int i = 0; i < counters.Count; i++)
                {
                    Counter counter = counters[i];
                    ProfilerRecorder[] recorders = counter.Recorders;
                    if (recorders.Length == 0) { extra[i] = 0; continue; }
                    long value = 0;
                    for (int r = 0; r < recorders.Length; r++) value += recorders[r].LastValue;
                    extra[i] = value;
                    if (value > counter.Largest) { counter.Largest = value; counters[i] = counter; }
                }
            }
            catch (Exception) { }
            if (!frameTimingOn) return;
            try
            {
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, timings) > 0)
                {
                    FrameTiming t = timings[0];
                    extra[6] = t.cpuFrameTime; extra[7] = t.cpuMainThreadFrameTime; extra[8] = t.cpuRenderThreadFrameTime;
                    extra[9] = t.gpuFrameTime; extra[10] = t.cpuMainThreadPresentWaitTime;
                    if (t.cpuFrameTime > 0 || t.gpuFrameTime > 0) frameTimingSeen = true;
                }
            }
            catch (Exception) { frameTimingOn = false; }
        }

        /// <summary>The sizes of Unity's memory pools and the colony, read only when a row is written.</summary>
        static void Heavy(double[] extra)
        {
            int at = Columns.ExtraPerFrameCount;
            try
            {
                extra[at] = Profiler.GetMonoHeapSizeLong() / 1048576.0;
                extra[at + 1] = Profiler.GetMonoUsedSizeLong() / 1048576.0;
                extra[at + 2] = Profiler.GetTotalAllocatedMemoryLong() / 1048576.0;
                extra[at + 3] = ProcessMemory.WorkingSetBytes() / 1048576.0;
            }
            catch (Exception) { }
            try
            {
                double[] colony = ColonySampler?.Invoke();
                if (colony != null) for (int i = 0; i < colony.Length && at + 4 + i < extra.Length; i++) extra[at + 4 + i] = colony[i];
            }
            catch (Exception) { }
        }

        /// <summary>Says which sources are supposed to work, for the header.</summary>
        internal static List<string> StartLines()
        {
            var lines = new List<string>
            {
                "# capability|frameTiming|" + (frameTimingOn ? "enabled" : "off (the game's player settings leave Unity's frame timing off, or it is unavailable)"),
            };
            foreach (Counter counter in counters)
                lines.Add("# capability|profilerRecorder|" + counter.Name + "|" + Describe(counter));
            return lines;
        }

        static string Describe(Counter counter)
        {
            if (!counter.Valid) return "not available";
            if (counter.Started.Length == 1 && counter.Started[0] == counter.Name) return "started";
            return "started, as the sum of " + string.Join(" + ", counter.Started);
        }

        /// <summary>Says which sources actually produced a value, for the end of the file. Read on the game thread, before the log stops.</summary>
        internal static List<string> FinalLines()
        {
            var lines = new List<string>
            {
                "# capability-final|frameTiming|" + (frameTimingSeen ? "produced values" : "never produced a value"),
            };
            foreach (Counter counter in counters)
                lines.Add("# capability-final|profilerRecorder|" + counter.Name + "|" +
                    (counter.Largest > 0 ? "produced values, largest " + counter.Largest : "never produced a value") + "|frames=" + frames);
            return lines;
        }
    }
}
