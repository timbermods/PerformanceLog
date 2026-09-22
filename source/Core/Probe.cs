using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace PerformanceLog
{
    /// <summary>What a log needs to start: where rows go and what counts as slow.</summary>
    public sealed class ProbeSettings
    {
        /// <summary>frames.csv rows.</summary>
        public Ring Frames;
        /// <summary>profile.csv rows.</summary>
        public Ring Profile;
        /// <summary>spikes.csv rows.</summary>
        public Ring Spikes;
        /// <summary>The managed id of the game thread: the only one whose time is recorded.</summary>
        public int GameThreadId;
        /// <summary>A frame this long or longer gets a row of its own.</summary>
        public double ThresholdMs = 50;
        /// <summary>A summary row is written this often (wall clock).</summary>
        public double SummarySeconds = 10;
        /// <summary>The profile is written this often (wall clock).</summary>
        public double ProfileSeconds = 30;
        /// <summary>How many of the biggest contributors to a slow frame are written to the spike file.</summary>
        public int SpikeContributors = 5;
        /// <summary>At most this many slow frames get a row a minute; the rest are only counted. A game that is slow all the time would otherwise write without limit.</summary>
        public int MaxSlowRowsPerMinute = 300;
    }

    /// <summary>One of the slowest frames of the session, kept for the summary.</summary>
    public sealed class WorstFrame
    {
        public double[] Row;
        public int[] TopIds = new int[0];
        public double[] TopMs = new double[0];
    }

    /// <summary>One summary window, kept for the summary's view of how the session changed over time.</summary>
    public sealed class WindowStat
    {
        public double UtcMs, Seconds, Frames, FrameMs, MaxFrameMs, Ticks, MsPerTick, Speed, PausedFrames, HeapMB, Entities, Beavers, Collections;
    }

    /// <summary>Counts over the whole session that the frame rows do not carry, for the summary.</summary>
    public sealed class SessionStats
    {
        /// <summary>Frames at speed 0, 1, 2, ... 7 or more (index = the speed rounded).</summary>
        public long[] SpeedFrames = new long[8];
        /// <summary>The time of those frames, in milliseconds.</summary>
        public double[] SpeedMs = new double[8];
        public double HeapMinMB, HeapMaxMB;
        public long SlowFrames, SlowGcFrames, SlowSaveFrames, SlowUnfocusedFrames, SlowPausedFrames;
        /// <summary>Slow frames that were counted but got no row because of the per-minute limit.</summary>
        public long SlowRowsSkipped;
        public double SlowMs;
        public double FirstFrameMs;
        /// <summary>
        /// Frames whose allocation could not be measured, and their time: the allocation counter fell during them. With the heap size as the
        /// counter (<see cref="Alloc.ModeHeap"/>) that is every frame with a garbage collection, which loses what the frame allocated.
        /// </summary>
        public long AllocUnmeasuredFrames;
        public double AllocUnmeasuredMs;
        /// <summary>
        /// The heap growth those frames still showed (their positive <c>allocKB</c>, which the session's <c>allocKB</c> total holds). A collection
        /// took an unknown part of what they allocated, so an allocation rate leaves this out along with their time.
        /// </summary>
        public double AllocUnmeasuredKB;

        /// <summary>A copy that another thread can read while the game thread goes on counting.</summary>
        public SessionStats Clone()
        {
            var copy = (SessionStats)MemberwiseClone();
            copy.SpeedFrames = (long[])SpeedFrames.Clone();
            copy.SpeedMs = (double[])SpeedMs.Clone();
            return copy;
        }
    }

    /// <summary>
    /// Measures where a frame's time goes. It only observes: it reads clocks and counters, writes into buffers it allocated up front,
    /// and never touches anything the game reads. Every measured point starts with one read of <see cref="Enabled"/>, so with the log
    /// off the cost is a predictable branch.
    ///
    /// Time is recorded per slot (see <see cref="Slot"/>) as exclusive time, and only on the game thread. A frame is the time between two
    /// calls of <see cref="OnFrame"/>, which is made once per frame, at the start of Unity's frame. Anything that goes wrong here
    /// switches the probe off instead of reaching the game.
    /// </summary>
    public static class Probe
    {
        /// <summary>True while a log is being written.</summary>
        public static volatile bool Enabled;

        /// <summary>Replaces the clock. For tests only; the values are in <see cref="Stopwatch"/> ticks.</summary>
        public static Func<long> TestClock;

        /// <summary>Why the probe switched itself off, if it did.</summary>
        public static string LastFailure { get; private set; }

        /// <summary>Values the game side writes before each <see cref="OnFrame"/>: first the per-frame extras, then the heavy ones.</summary>
        public static readonly double[] Extra = new double[Columns.ExtraPerFrameCount + Columns.ExtraHeavyCount];

        /// <summary>Fills the heavy extras. Called only when a row is about to be written.</summary>
        public static Action<double[]> HeavySampler;

        // What measuring costs, in Stopwatch ticks. Set by Calibrate (and the two patch costs by the game side, with MeasureUnsampled).
        public static double ClockReadTicks { get; set; }
        public static double AllocReadTicks { get; set; }
        /// <summary>One timed scope: Begin and End.</summary>
        public static double ScopePairTicks { get; set; }
        /// <summary>One sampled call: two clock and two allocation readings.</summary>
        public static double SamplePairTicks { get; set; }
        /// <summary>Two allocation readings.</summary>
        public static double AllocPairTicks { get; set; }
        /// <summary>One call of a singleton that is always timed, without the allocation readings.</summary>
        public static double ExactCallTicks { get; set; }
        /// <summary>The bodies of this mod's per-call patch (the entity tick's prefix and postfix) on a call that is not sampled, which is almost every call.</summary>
        public static double PatchBodyTicks { get; set; }
        /// <summary>One call of this mod's per-call patches: <see cref="PatchBodyTicks"/> plus what Harmony adds to call a prefix and a postfix. 0 if not measured.</summary>
        public static double PatchCallTicks { get; set; }
        /// <summary>What each patch call is charged in overheadUs: <see cref="PatchCallTicks"/>, or 40 ns when it could not be measured (the calibration line says so).</summary>
        public static double PatchCallTicksCharged => PatchCallTicks > 0 ? PatchCallTicks : 40e-9 * Stopwatch.Frequency;

        /// <summary>
        /// The header's `# calibration|` line after its kind: what each part of measuring costs, in nanoseconds. patchBodyNs is the entity tick's
        /// prefix and postfix on a call that is not sampled; patchCallNs is that plus what Harmony adds, which is what overheadUs charges each patch
        /// call. Up to 0.1.3 there was no patchBodyNs and patchCallNs (an empty patch) read 0; tools/perflog.py tells the two apart by patchBodyNs.
        /// </summary>
        public static string[] CalibrationParts() => new[]
        {
            "clockReadNs", Ns(ClockReadTicks), "allocReadNs", Ns(AllocReadTicks), "scopePairNs", Ns(ScopePairTicks), "samplePairNs", Ns(SamplePairTicks),
            "patchBodyNs", PatchBodyTicks > 0 ? Ns(PatchBodyTicks, "F1") : "unmeasured",
            "patchCallNs", PatchCallTicks > 0 ? Ns(PatchCallTicks, "F1") : "unmeasured (" + Ns(PatchCallTicksCharged) + " assumed)",
        };

        static string Ns(double ticks, string format = "F0") => (ticks * 1e9 / Stopwatch.Frequency).ToString(format, System.Globalization.CultureInfo.InvariantCulture);

        public static bool OnGameThread => Environment.CurrentManagedThreadId == mainThreadId;

        /// <summary>Simulation ticks since the log started.</summary>
        public static int TickCount => tickCount;

        /// <summary>Frames since the log started.</summary>
        public static int FrameNumber => frameNo;

        /// <summary>Seconds of wall clock the log has covered.</summary>
        public static double SessionSeconds => sessionStartTs == 0 ? 0 : (Now() - sessionStartTs) * msPerTick / 1000.0;

        const int MaxDepth = 32;
        static readonly int[] stackSlot = new int[MaxDepth];
        static readonly long[] stackStart = new long[MaxDepth];
        static readonly long[] stackChild = new long[MaxDepth];
        static readonly long[] stackAlloc = new long[MaxDepth];
        static readonly long[] stackChildAlloc = new long[MaxDepth];
        static int depth, generation, mainThreadId;

        static readonly long[] frameSelf = new long[Columns.SlotCount];
        static readonly long[] frameAlloc = new long[Columns.SlotCount];
        static readonly long[] counters = new long[Columns.CounterCount];
        static readonly long[] phaseStart = new long[Columns.PhaseCount];
        static readonly long[] framePhase = new long[Columns.PhaseCount];
        static readonly double[] row = new double[Columns.Count];
        static readonly double[] window = new double[Columns.Count];
        static readonly double[] session = new double[Columns.Count];
        static int windowFrames, sessionFrames, windowNumber, profileWindowNumber;

        static Ring frameRing, profileRing, spikeRing;
        static readonly double msPerTick = 1000.0 / Stopwatch.Frequency;
        static readonly long epochTicks = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        static double thresholdMs;
        static long summaryTicks, profileTicks, nextSummaryTs, nextProfileTs, windowStartTs, profileStartTs, sessionStartTs;
        static int spikeTop, maxSlowPerMinute, slowInWindow;
        static long slowWindowStart;
        static long lastFrameTs;
        static int frameNo, lastGc, tickCount;
        static long lastMemory, lastAllocSource;
        static long lastThreadCpu, lastProcessCpu;
        static ulong lastCycles;

        static int frameTicks, frameBuckets, frameScopes, bucketsInTick;
        static double frameParTickMs;
        // The allocation counter went down inside a timed scope this frame (a collection, when it is the heap size).
        static bool frameAllocFell;

        const int WorstKept = 10;
        static readonly List<WorstFrame> worst = new List<WorstFrame>();
        static SessionStats stats = new SessionStats();
        static readonly double[] lastHeavy = new double[Columns.ExtraHeavyCount];
        const int MaxWindows = 20000;
        static readonly List<WindowStat> windows = new List<WindowStat>();

        static long Now()
        {
            Func<long> clock = TestClock;
            return clock != null ? clock() : Stopwatch.GetTimestamp();
        }

        /// <summary>The probe's clock, for callers that time something themselves and report it with <see cref="AddNested"/>.</summary>
        public static long Timestamp() => Now();

        // ---- lifecycle ----

        /// <summary>Measures what the probe itself costs, so the log can say how much it distorts. Call before <see cref="Start"/>, on the game thread.</summary>
        public static void Calibrate()
        {
            try
            {
                int savedThread = mainThreadId;
                mainThreadId = Environment.CurrentManagedThreadId;
                Func<long> savedClock = TestClock;
                TestClock = null;
                const int repeats = 4000; // a stable average is all that is needed, and a slow counter would otherwise cost the session a visible hitch
                long sink = 0;
                long t0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < repeats; i++) sink += Stopwatch.GetTimestamp();
                ClockReadTicks = (Stopwatch.GetTimestamp() - t0) / (double)repeats;
                AllocReadTicks = Alloc.MeasureReadTicks(repeats);
                AllocPairTicks = 2 * AllocReadTicks;

                t0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < repeats; i++) { long token = BeginCore((int)Slot.Update); EndCore(token); }
                ScopePairTicks = (Stopwatch.GetTimestamp() - t0) / (double)repeats;

                t0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < repeats; i++) sink += Stopwatch.GetTimestamp() + Alloc.Read() + Stopwatch.GetTimestamp() + Alloc.Read();
                SamplePairTicks = (Stopwatch.GetTimestamp() - t0) / (double)repeats;

                int id = Profile.IdFor(ProfileKind.Method, "calibration");
                t0 = Stopwatch.GetTimestamp();
                for (int i = 0; i < repeats; i++) { Timing t = default; t.Start = Stopwatch.GetTimestamp(); t.Alloc = -1; Profile.EndExact(id, t); }
                ExactCallTicks = (Stopwatch.GetTimestamp() - t0) / (double)repeats + ClockReadTicks;
                Profile.Reset();

                GC.KeepAlive(sink.ToString());
                TestClock = savedClock;
                mainThreadId = savedThread;
                ResetFrame();
                Array.Clear(frameSelf, 0, frameSelf.Length);
                Array.Clear(frameAlloc, 0, frameAlloc.Length);
            }
            catch (Exception e) { LastFailure = e.Message; }
        }

        /// <summary>
        /// Stopwatch ticks for one of the calls <paramref name="calls"/> makes when asked for n, timed the way a patch body runs on almost every
        /// call while a log is on: the probe switched on, and entity and component sampling held off so that no call is one of the sampled ones
        /// (those are charged separately). The fastest of a few rounds after a warm-up, so compiling and the scheduler do not decide the figure.
        /// Only while no log is running (0 otherwise, or if it fails); the probe is off and the profile empty afterwards, as a log start expects.
        /// </summary>
        public static double MeasureUnsampled(Action<int> calls, int repeats = 4000, int rounds = 5)
        {
            if (Enabled || calls == null) return 0;
            int savedThread = mainThreadId;
            Func<long> savedClock = TestClock;
            double best = 0;
            try
            {
                mainThreadId = Environment.CurrentManagedThreadId;
                TestClock = null;
                Enabled = true;
                Profile.HoldSampling();
                calls(repeats / 10 + 1);
                best = double.MaxValue;
                for (int round = 0; round < rounds; round++)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    calls(repeats);
                    best = Math.Min(best, (Stopwatch.GetTimestamp() - t0) / (double)repeats);
                }
            }
            catch (Exception e) { LastFailure = e.Message; best = 0; }
            finally
            {
                Enabled = false;
                TestClock = savedClock;
                mainThreadId = savedThread;
                Array.Clear(counters, 0, counters.Length);
                Profile.Reset();
            }
            return best;
        }

        public static void Start(ProbeSettings settings)
        {
            Stop();
            frameRing = settings.Frames;
            profileRing = settings.Profile;
            spikeRing = settings.Spikes;
            mainThreadId = settings.GameThreadId;
            thresholdMs = settings.ThresholdMs;
            summaryTicks = Math.Max(1, (long)(settings.SummarySeconds * Stopwatch.Frequency));
            profileTicks = Math.Max(1, (long)(settings.ProfileSeconds * Stopwatch.Frequency));
            spikeTop = Math.Max(0, settings.SpikeContributors);
            maxSlowPerMinute = Math.Max(1, settings.MaxSlowRowsPerMinute);
            slowInWindow = 0; slowWindowStart = 0;
            depth = 0; generation++;
            Array.Clear(frameSelf, 0, frameSelf.Length);
            Array.Clear(frameAlloc, 0, frameAlloc.Length);
            Array.Clear(counters, 0, counters.Length);
            Array.Clear(phaseStart, 0, phaseStart.Length);
            Array.Clear(framePhase, 0, framePhase.Length);
            Array.Clear(Extra, 0, Extra.Length);
            Array.Clear(window, 0, window.Length);
            Array.Clear(session, 0, session.Length);
            windowFrames = 0; sessionFrames = 0; windowNumber = 0; profileWindowNumber = 1;
            frameNo = 0; lastFrameTs = 0; tickCount = 0; bucketsInTick = 0; nextSummaryTs = 0; nextProfileTs = 0; windowStartTs = 0; profileStartTs = 0; sessionStartTs = 0;
            lastThreadCpu = 0; lastProcessCpu = 0; lastCycles = 0; lastAllocSource = 0;
            worst.Clear();
            windows.Clear();
            Array.Clear(lastHeavy, 0, lastHeavy.Length);
            stats = new SessionStats();
            ResetFrame();
            Profile.Reset();
            LastFailure = null;
            Enabled = true;
        }

        /// <summary>Writes the summary and the profile of the frames not yet written, and stops.</summary>
        public static void Stop()
        {
            if (Enabled)
            {
                try
                {
                    if (windowFrames > 0) EmitSummary();
                    FlushProfile(Now());
                }
                catch (Exception e) { LastFailure = e.Message; }
            }
            Enabled = false;
            frameRing = null;
            profileRing = null;
            spikeRing = null;
        }

        static void Fail(Exception e)
        {
            LastFailure = e.ToString();
            Enabled = false;
        }

        // ---- scopes ----

        /// <summary>Starts timing a slot. Pass the result to <see cref="End"/>. Zero, and free, when the log is off.</summary>
        public static long Begin(Slot slot)
        {
            if (!Enabled) return 0;
            return BeginCore((int)slot);
        }

        /// <summary>Stops the timing that <see cref="Begin"/> started.</summary>
        public static void End(long token)
        {
            if (token != 0) EndCore(token);
        }

        static long BeginCore(int slot)
        {
            try
            {
                if (Environment.CurrentManagedThreadId != mainThreadId) return 0;
                int d = depth;
                if (d >= MaxDepth) return 0;
                stackSlot[d] = slot;
                stackChild[d] = 0;
                stackChildAlloc[d] = 0;
                stackAlloc[d] = Columns.SlotTracksAlloc[slot] && Alloc.Enabled ? Alloc.Read() : -1;
                frameScopes++;
                stackStart[d] = Now();
                depth = d + 1;
                return ((long)generation << 32) | (uint)(d + 1);
            }
            catch (Exception e) { Fail(e); return 0; }
        }

        static void EndCore(long token)
        {
            try
            {
                if ((int)(token >> 32) != generation) return;
                int level = (int)(token & 0xFFFFFFFF);
                if (level > depth) return;
                long now = Now();
                long allocNow = Alloc.Enabled ? Alloc.Read() : 0;
                // A scope inside this one that never ended (an exception skipped it) is closed here.
                while (depth >= level)
                {
                    int d = depth - 1;
                    long elapsed = now - stackStart[d];
                    long self = elapsed - stackChild[d];
                    if (self < 0) self = 0;
                    frameSelf[stackSlot[d]] += self;
                    long childAlloc = stackChildAlloc[d];
                    long a0 = stackAlloc[d];
                    long passUp;
                    if (a0 >= 0)
                    {
                        if (allocNow < a0) frameAllocFell = true;
                        long elapsedAlloc = Math.Max(0, allocNow - a0);
                        frameAlloc[stackSlot[d]] += Math.Max(0, elapsedAlloc - childAlloc);
                        passUp = elapsedAlloc;
                    }
                    else passUp = childAlloc; // an unmeasured scope hands what its measured children took on to the one around it
                    depth = d;
                    if (d > 0)
                    {
                        stackChild[d - 1] += elapsed;
                        stackChildAlloc[d - 1] += passUp;
                    }
                }
            }
            catch (Exception e) { Fail(e); }
        }

        /// <summary>
        /// Adds time that was measured some other way (a few timestamps taken by the caller) to a slot, and takes it out of the scope
        /// that is open, so the slots still add up.
        /// </summary>
        public static void AddNested(Slot slot, long ticks)
        {
            if (!Enabled || ticks <= 0 || Environment.CurrentManagedThreadId != mainThreadId) return;
            frameSelf[(int)slot] += ticks;
            if (depth > 0) stackChild[depth - 1] += ticks;
        }

        // ---- counters ----

        public static void Count(Counter counter)
        {
            if (Enabled) counters[(int)counter]++;
        }

        public static void Count(Counter counter, int amount)
        {
            if (Enabled) counters[(int)counter] += amount;
        }

        /// <summary>A simulation tick started. Called by the patch on the tick's first bucket.</summary>
        public static void NoteTickStart()
        {
            if (!Enabled) return;
            frameTicks++;
            tickCount++;
        }

        /// <summary>The tick loop ran one bucket.</summary>
        public static void NoteBucket()
        {
            if (Enabled) frameBuckets++;
        }

        /// <summary>How many buckets of entities make up one simulation tick. The game uses 128; the game side reads it from the game.</summary>
        public static int EntityBucketsPerTick { get; set; } = 128;

        /// <summary>
        /// One bucket of entities ran. A tick has finished after <see cref="EntityBucketsPerTick"/> of them, so ticks are counted here and not
        /// where a tick starts: that stays right when another mod replaces the tick loop.
        /// </summary>
        public static void NoteEntityBucket()
        {
            if (!Enabled) return;
            frameBuckets++;
            if (++bucketsInTick >= EntityBucketsPerTick)
            {
                bucketsInTick = 0;
                frameTicks++;
                tickCount++;
            }
        }

        /// <summary>How long the game says the last parallel tick took, in milliseconds.</summary>
        public static void NoteParallelTick(double milliseconds)
        {
            if (Enabled) frameParTickMs += milliseconds;
        }

        /// <summary>A phase of Unity's frame began or ended (see <see cref="Columns.PhaseNames"/>). Called from Unity's player loop.</summary>
        public static void PhaseMark(int phase, bool start)
        {
            if (!Enabled || (uint)phase >= Columns.PhaseCount) return;
            long now = Now();
            if (start) phaseStart[phase] = now;
            else if (phaseStart[phase] != 0)
            {
                framePhase[phase] += now - phaseStart[phase];
                phaseStart[phase] = 0;
            }
        }

        // ---- frames ----

        /// <summary>
        /// Called once per frame, at the same place every time. Ends the previous frame: writes a row for it if it was slow, adds it to
        /// the running summary, and starts the next one.
        /// </summary>
        public static void OnFrame(float speed, bool focused)
        {
            if (!Enabled) return;
            try { Frame(speed, focused); }
            catch (Exception e) { Fail(e); }
        }

        static void Frame(float speed, bool focused)
        {
            long start = Now();
            bool cpuWanted = Cpu.Available && TestClock == null;
            long threadCpu = 0, processCpu = 0;
            ulong cycles = 0;
            if (cpuWanted && !Cpu.Sample(out threadCpu, out cycles, out processCpu)) cpuWanted = false;

            if (lastFrameTs == 0)
            {
                // The first call only sets the clocks.
                lastFrameTs = start;
                sessionStartTs = start; windowStartTs = start; profileStartTs = start;
                nextSummaryTs = start + summaryTicks; nextProfileTs = start + profileTicks;
                lastGc = GC.CollectionCount(0);
                lastMemory = GC.GetTotalMemory(false);
                lastAllocSource = Alloc.Enabled ? Alloc.Read() : 0;
                lastThreadCpu = threadCpu; lastProcessCpu = processCpu; lastCycles = cycles;
                ResetFrame();
                return;
            }

            double frameMs = (start - lastFrameTs) * msPerTick;
            lastFrameTs = start;
            frameNo++;

            int gc = GC.CollectionCount(0);
            long memory = GC.GetTotalMemory(false);

            bool slow = frameMs >= thresholdMs;
            bool summaryDue = start >= nextSummaryTs;
            if (slow || summaryDue) HeavySampler?.Invoke(Extra);
            double utcMs = UnixMs();

            bool writeSlow = slow && AllowSlowRow(start);
            double profileCost = Profile.EndFrame(slow, frameNo, tickCount, utcMs, frameMs, writeSlow ? spikeRing : null, spikeTop);

            double[] r = row;
            Array.Clear(r, 0, r.Length);
            r[Columns.Type] = Columns.FrameRow;
            r[Columns.Frame] = frameNo;
            r[Columns.Tick] = tickCount;
            r[Columns.UtcMs] = utcMs;
            r[Columns.Frames] = 1;
            r[Columns.FrameMs] = frameMs;
            r[Columns.MaxFrameMs] = frameMs;
            r[Columns.Ticks] = frameTicks;
            r[Columns.Buckets] = frameBuckets;
            r[Columns.Speed] = speed;
            r[Columns.Paused] = speed <= 0 ? 1 : 0;
            r[Columns.Saving] = frameSelf[(int)Slot.Save] > 0 ? 1 : 0;
            r[Columns.Unfocused] = focused ? 0 : 1;

            double accounted = 0;
            double allocAccounted = 0;
            for (int i = 0; i < Columns.SlotCount; i++)
            {
                double ms = frameSelf[i] * msPerTick;
                r[Columns.SlotBase + i] = ms;
                accounted += ms;
                double kb = frameAlloc[i] / 1024.0;
                r[Columns.AllocBase + i] = kb;
                allocAccounted += kb;
            }
            r[Columns.OtherMs] = Math.Max(0, frameMs - accounted);

            double allocKb = (memory - lastMemory) / 1024.0;
            // What was allocated outside every measured section, from the same counter the sections use. Only the exact per-thread counter never
            // goes down; the heap size (Alloc.ModeHeap, what the game's Mono offers) falls at a collection, and a frame with one has lost what it
            // allocated, so its KB columns read low (0 when the heap shrank). No column says so: the frame is counted instead (SessionStats.
            // AllocUnmeasuredFrames, and the capability-final line from AllocFinalLine), and if it is slow its F row is one with gcDelta > 0 or a
            // negative allocKB.
            long allocSource = Alloc.Enabled ? Alloc.Read() : 0;
            bool allocUnmeasured = Alloc.Enabled && (frameAllocFell || allocSource < lastAllocSource || (Alloc.Mode == Alloc.ModeHeap && gc != lastGc));
            r[Columns.OtherKB] = Math.Max(0, (allocSource - lastAllocSource) / 1024.0 - allocAccounted);
            lastAllocSource = allocSource;
            r[Columns.GcDelta] = gc - lastGc;
            r[Columns.HeapMB] = memory / 1048576.0;
            r[Columns.AllocKB] = allocKb;
            r[Columns.Dropped] = frameRing != null ? frameRing.Dropped : 0;
            double patchTicks = counters[(int)Counter.PatchCalls] * PatchCallTicksCharged;
            r[Columns.OverheadUs] = (frameScopes * ScopePairTicks + profileCost + patchTicks) * msPerTick * 1000;

            for (int i = 0; i < Columns.CounterCount; i++) r[Columns.CounterBase + i] = counters[i];
            r[Columns.ParTickMs] = frameParTickMs;

            if (cpuWanted)
            {
                r[Columns.MainCpuMs] = (threadCpu - lastThreadCpu) / 10000.0;
                r[Columns.MainMcyc] = (cycles - lastCycles) / 1000000.0;
                r[Columns.ProcCpuMs] = (processCpu - lastProcessCpu) / 10000.0;
                lastThreadCpu = threadCpu; lastProcessCpu = processCpu; lastCycles = cycles;
            }

            for (int i = 0; i < Columns.PhaseCount; i++) r[Columns.PhaseBase + i] = framePhase[i] * msPerTick;
            for (int i = 0; i < Columns.ExtraPerFrameCount; i++) r[Columns.ExtraBase + i] = Extra[i];
            // The heavy figures are read only when a row is about to be written; the other frames carry the last reading.
            if (slow || summaryDue)
                for (int i = 0; i < Columns.ExtraHeavyCount; i++) lastHeavy[i] = Extra[Columns.ExtraPerFrameCount + i];
            for (int i = 0; i < Columns.ExtraHeavyCount; i++) r[Columns.HeavyBase + i] = lastHeavy[i];

            r[Columns.FrameHistBase + Columns.FrameBucket(frameMs)] = 1;
            r[Columns.TickHistBase + Columns.TickBucket(frameTicks)] = 1;

            SessionStats s = stats;
            int speedIndex = speed <= 0 ? 0 : Math.Min(7, (int)Math.Round(speed));
            s.SpeedFrames[speedIndex]++; s.SpeedMs[speedIndex] += frameMs;
            double heapMb = memory / 1048576.0;
            if (sessionFrames == 0 || heapMb < s.HeapMinMB) s.HeapMinMB = heapMb;
            if (heapMb > s.HeapMaxMB) s.HeapMaxMB = heapMb;
            if (sessionFrames == 0) s.FirstFrameMs = frameMs;
            if (allocUnmeasured) { s.AllocUnmeasuredFrames++; s.AllocUnmeasuredMs += frameMs; s.AllocUnmeasuredKB += Math.Max(0, allocKb); }
            if (slow)
            {
                s.SlowFrames++; s.SlowMs += frameMs;
                if (gc > lastGc) s.SlowGcFrames++;
                if (r[Columns.Saving] > 0) s.SlowSaveFrames++;
                if (!focused) s.SlowUnfocusedFrames++;
                if (speed <= 0) s.SlowPausedFrames++;
            }

            lastGc = gc;
            lastMemory = memory;
            ResetFrame();

            Accumulate(window, r, windowFrames == 0);
            Accumulate(session, r, sessionFrames == 0);
            windowFrames++; sessionFrames++;
            // What this call itself cost, so the log shows whether it is a problem of its own making.
            r[Columns.ProbeUs] = (Now() - start) * msPerTick * 1000;
            window[Columns.ProbeUs] += r[Columns.ProbeUs];
            session[Columns.ProbeUs] += r[Columns.ProbeUs];
            if (slow)
            {
                if (writeSlow) frameRing?.TryPush(r);
                else stats.SlowRowsSkipped++;
                KeepIfWorst(r);
            }
            if (summaryDue)
            {
                EmitSummary();
                nextSummaryTs = start + summaryTicks;
            }
            if (start >= nextProfileTs) FlushProfile(start);
        }

        static void ResetFrame()
        {
            Array.Clear(frameSelf, 0, frameSelf.Length);
            Array.Clear(frameAlloc, 0, frameAlloc.Length);
            Array.Clear(counters, 0, counters.Length);
            Array.Clear(framePhase, 0, framePhase.Length);
            depth = 0; generation++;
            frameTicks = 0; frameBuckets = 0; frameScopes = 0; frameParTickMs = 0;
            frameAllocFell = false;
        }

        static void Accumulate(double[] into, double[] r, bool first)
        {
            for (int c = 0; c < Columns.Count; c++)
            {
                switch (Columns.Aggregates[c])
                {
                    case Aggregate.Sum:
                    case Aggregate.Mean:
                        into[c] += r[c];
                        break;
                    case Aggregate.SumPositive:
                        if (r[c] > 0) into[c] += r[c];
                        break;
                    case Aggregate.Max:
                        if (first || r[c] > into[c]) into[c] = r[c];
                        break;
                    default:
                        into[c] = r[c];
                        break;
                }
            }
        }

        static bool AllowSlowRow(long now)
        {
            if (slowWindowStart == 0 || now - slowWindowStart >= 60L * Stopwatch.Frequency)
            {
                slowWindowStart = now;
                slowInWindow = 0;
            }
            return ++slowInWindow <= maxSlowPerMinute;
        }

        static void KeepIfWorst(double[] r)
        {
            double ms = r[Columns.FrameMs];
            if (worst.Count >= WorstKept && ms <= worst[worst.Count - 1].Row[Columns.FrameMs]) return;
            var w = new WorstFrame { Row = (double[])r.Clone() };
            int n = Profile.TopCount;
            w.TopIds = new int[n]; w.TopMs = new double[n];
            for (int i = 0; i < n; i++) { w.TopIds[i] = Profile.TopIds[i]; w.TopMs[i] = Profile.TopTicks[i] * msPerTick; }
            int at = worst.Count;
            while (at > 0 && worst[at - 1].Row[Columns.FrameMs] < ms) at--;
            worst.Insert(at, w);
            if (worst.Count > WorstKept) worst.RemoveAt(worst.Count - 1);
        }

        static void EmitSummary()
        {
            if (windowFrames == 0) return;
            double[] r = row;
            Array.Copy(window, r, r.Length);
            for (int c = 0; c < Columns.Count; c++)
                if (Columns.Aggregates[c] == Aggregate.Mean) r[c] /= windowFrames;
            r[Columns.Type] = Columns.SummaryRow;
            r[Columns.Dropped] = frameRing != null ? frameRing.Dropped : 0;
            frameRing?.TryPush(r);
            RememberWindow(r);
            Array.Clear(window, 0, window.Length);
            windowFrames = 0;
            windowNumber++;
        }

        static void RememberWindow(double[] r)
        {
            if (windows.Count >= MaxWindows) return;
            double frames = r[Columns.Frames], ticks = r[Columns.Ticks];
            double tickWork = 0;
            for (int i = 0; i <= (int)Slot.ParallelStart; i++) tickWork += r[Columns.SlotBase + i] * frames;
            windows.Add(new WindowStat
            {
                UtcMs = r[Columns.UtcMs], Frames = frames, FrameMs = r[Columns.FrameMs], MaxFrameMs = r[Columns.MaxFrameMs],
                Seconds = r[Columns.FrameMs] * frames / 1000.0, Ticks = ticks, MsPerTick = ticks > 0 ? tickWork / ticks : 0,
                Speed = r[Columns.Speed], PausedFrames = r[Columns.Paused], HeapMB = r[Columns.HeapMB], Entities = r[Columns.HeavyBase + 4],
                Beavers = r[Columns.HeavyBase + 5], Collections = r[Columns.GcDelta],
            });
        }

        /// <summary>One entry for each summary window written so far, oldest first.</summary>
        public static List<WindowStat> Windows() => new List<WindowStat>(windows);

        static void FlushProfile(long now)
        {
            double seconds = profileStartTs == 0 ? 0 : (now - profileStartTs) * msPerTick / 1000.0;
            Profile.FlushWindow(profileWindowNumber++, tickCount, seconds, profileRing);
            profileStartTs = now;
            nextProfileTs = now + profileTicks;
        }

        // ---- what the whole session looked like, for the summary ----

        /// <summary>Frames in the session so far.</summary>
        public static int SessionFrames => sessionFrames;

        /// <summary>
        /// A row of the whole session so far in the layout of an S row: times averaged per frame, counts and allocation totalled.
        /// (Frames not yet closed are not in it.) Null before the first frame.
        /// </summary>
        public static double[] SessionRow()
        {
            if (sessionFrames == 0) return null;
            var r = (double[])session.Clone();
            for (int c = 0; c < Columns.Count; c++)
                if (Columns.Aggregates[c] == Aggregate.Mean) r[c] /= sessionFrames;
            r[Columns.Type] = Columns.SummaryRow;
            return r;
        }

        /// <summary>The slowest frames of the session, slowest first.</summary>
        public static List<WorstFrame> WorstFrames() => new List<WorstFrame>(worst);

        /// <summary>Counts over the session so far. The object is replaced when a new log starts.</summary>
        public static SessionStats Stats => stats;

        /// <summary>
        /// The <c># capability-final|allocSource|</c> line: whether allocation was measured in every frame. The frames it was not measured in
        /// (see <see cref="SessionStats.AllocUnmeasuredFrames"/>) are counted here rather than in a column, so the file format stays the same.
        /// </summary>
        public static string AllocFinalLine()
        {
            const string head = "# capability-final|allocSource|";
            if (!Alloc.Enabled) return head + "none|allocation was not measured";
            bool heap = Alloc.Mode == Alloc.ModeHeap;
            long frames = stats.AllocUnmeasuredFrames;
            if (frames == 0) return head + (heap ? "heap size" : "exact") + "|measured in every frame";
            string count = "allocation not measured in " + frames + (frames == 1 ? " frame (" : " frames (") +
                           (stats.AllocUnmeasuredMs / 1000).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " s)";
            if (!heap) return head + "exact|" + count + ": the counter fell during them, so their KB columns read low";
            return head + "heap size|" + count + " with a garbage collection: the heap-size counter falls at one, so what they allocated is lost and their KB " +
                   "columns read low. In frames.csv the slow ones are the F rows with gcDelta above 0 or a negative allocKB.";
        }

        static double UnixMs()
        {
            Func<long> clock = TestClock;
            if (clock != null) return 1700000000000.0 + clock() * msPerTick;
            return (DateTime.UtcNow.Ticks - epochTicks) / 10000.0;
        }
    }
}
