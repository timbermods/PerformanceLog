using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace PerformanceLog
{
    /// <summary>The start of one sampled call, or nothing if this call was not sampled.</summary>
    public struct Sample
    {
        public bool On;
        public long Start;
        public long Alloc;
    }

    /// <summary>The start of one call that is always timed. <see cref="Alloc"/> is -1 when allocation was not read for this call.</summary>
    public struct Timing
    {
        public long Start;
        public long Alloc;
    }

    /// <summary>
    /// Says which singletons, entity kinds, components and watched methods the time and allocation go to (profile.csv), and which of
    /// them a slow frame's time went to (spikes.csv). Two ways of measuring:
    ///
    ///  * Exact kinds (singletons, load steps): every call is timed, so a slow frame can be blamed on them exactly. Allocation is
    ///    read on some calls only and scaled up, because reading it is the expensive part.
    ///  * Sampled kinds (entities, components, watched methods): only every Nth call is timed, and the time and allocation are
    ///    scaled up by the calls that were not. N is chosen again every window so that measuring stays inside a small budget however
    ///    many calls there are.
    ///
    /// Everything is recorded on the game thread only, and only observed.
    /// </summary>
    public static class Profile
    {
        public static readonly Table Table = new Table(new[]
        {
            new Column { Name = "kind", Kind = ColumnKind.Word, Aggregate = Aggregate.Last, Words = ProfileKinds.Words, Unit = "", Description = "What the row measures: tick-singleton, update-singleton, late-singleton, parallel-start, entity, component, method, or a load step." },
            new Column { Name = "window", Kind = ColumnKind.Int, Aggregate = Aggregate.Last, Unit = "count", Description = "Profile window number. 0 is the load (steps taken while the game loaded)." },
            new Column { Name = "tick", Kind = ColumnKind.Int, Aggregate = Aggregate.Last, Unit = "count", Description = "Simulation ticks since the log started, at the end of the window." },
            new Column { Name = "id", Kind = ColumnKind.Int, Aggregate = Aggregate.Last, Unit = "", Description = "The key's number, the same in profile.csv and spikes.csv." },
            new Column { Name = "calls", Kind = ColumnKind.Int, Aggregate = Aggregate.Sum, Unit = "count", Description = "Calls in the window. Exact for singletons and watched methods, estimated (samples times the interval) for entities and components." },
            new Column { Name = "sampled", Kind = ColumnKind.Int, Aggregate = Aggregate.Sum, Unit = "count", Description = "Calls that were actually timed. ms and allocKB are scaled up from these, so a small number means a rough estimate. 0 (a watched method whose timed calls all threw) means none was: ms and allocKB are then unknown, not zero." },
            new Column { Name = "ms", Kind = ColumnKind.Fixed2, Aggregate = Aggregate.Sum, Unit = "ms", Description = "Time spent in all the calls in the window (scaled up from the sampled ones), including everything inside them." },
            new Column { Name = "allocKB", Kind = ColumnKind.Fixed1, Aggregate = Aggregate.Sum, Unit = "KB", Description = "Managed memory allocated by all the calls in the window (scaled up). Coarse when the allocation source is the heap size. For load steps (window 0) it is how much the managed heap grew during the step; a collection in the middle makes it read low." },
            new Column { Name = "maxMs", Kind = ColumnKind.Fixed2, Aggregate = Aggregate.Max, Unit = "ms", Description = "The slowest timed call in the window. A large value with a small ms is a rare hitch." },
            new Column { Name = "name", Kind = ColumnKind.Tail, Aggregate = Aggregate.Last, Unit = "", Description = "The singleton's or component's class, the entity's prefab name, or the method. Written after the numbers." },
            new Column { Name = "assembly", Kind = ColumnKind.Tail, Aggregate = Aggregate.Last, Unit = "", Description = "The DLL the class lives in." },
            new Column { Name = "mod", Kind = ColumnKind.Tail, Aggregate = Aggregate.Last, Unit = "", Description = "The mod that DLL belongs to, or 'game' for the game itself. Best effort." },
        });

        public static readonly Table SpikeTable = new Table(new[]
        {
            new Column { Name = "frame", Kind = ColumnKind.Int, Aggregate = Aggregate.Last, Unit = "count", Description = "The frame number of the slow frame (matches an F row in frames.csv)." },
            new Column { Name = "tick", Kind = ColumnKind.Int, Aggregate = Aggregate.Last, Unit = "count", Description = "Simulation ticks since the log started." },
            new Column { Name = "utcMs", Kind = ColumnKind.Int, Aggregate = Aggregate.Last, Unit = "ms", Description = "Wall clock, milliseconds since 1970 UTC." },
            new Column { Name = "frameMs", Kind = ColumnKind.Fixed2, Aggregate = Aggregate.Last, Unit = "ms", Description = "The slow frame's time." },
            new Column { Name = "rank", Kind = ColumnKind.Int, Aggregate = Aggregate.Last, Unit = "", Description = "1 is the biggest contributor in this frame." },
            new Column { Name = "kind", Kind = ColumnKind.Word, Aggregate = Aggregate.Last, Words = ProfileKinds.Words, Unit = "", Description = "The kind of key (only kinds that are timed on every call appear here)." },
            new Column { Name = "id", Kind = ColumnKind.Int, Aggregate = Aggregate.Last, Unit = "", Description = "The key's number, the same in profile.csv." },
            new Column { Name = "ms", Kind = ColumnKind.Fixed2, Aggregate = Aggregate.Last, Unit = "ms", Description = "Time the key spent in this frame." },
            new Column { Name = "share", Kind = ColumnKind.Fixed1, Aggregate = Aggregate.Last, Unit = "%", Description = "That time as a percentage of the frame." },
            new Column { Name = "name", Kind = ColumnKind.Tail, Aggregate = Aggregate.Last, Unit = "", Description = "The key's class. Written after the numbers." },
            new Column { Name = "mod", Kind = ColumnKind.Tail, Aggregate = Aggregate.Last, Unit = "", Description = "The mod it belongs to, or 'game'." },
        });

        public const int TopK = 8;
        public const double BudgetShareEntity = 0.4, BudgetShareComponent = 0.3, BudgetShareMethod = 0.1, BudgetShareAlloc = 0.2;

        /// <summary>Every Nth entity tick is timed. Chosen again each window.</summary>
        public static int EntityInterval { get; private set; } = 16;
        /// <summary>Every Nth component tick is timed. Chosen again each window.</summary>
        public static int ComponentInterval { get; private set; } = 64;
        /// <summary>Allocation is read on every Nth call of a singleton. Chosen again each window.</summary>
        public static int AllocEvery { get; private set; } = 4;
        /// <summary>How much of a second measuring may take, per second, before the intervals are widened (0.005 is half a percent).</summary>
        public static double BudgetFraction { get; set; } = 0.005;
        /// <summary>Changes when the keys are forgotten, so cached ids in wrappers know to look theirs up again.</summary>
        public static int Generation { get; private set; } = 1;

        /// <summary>Says which mod an assembly belongs to. Set by the game side; the default only knows the game's own assemblies.</summary>
        public static Func<string, string> ModResolver
        {
            get => modResolver;
            set => modResolver = value ?? DefaultModOf;
        }
        static Func<string, string> modResolver = DefaultModOf;

        sealed class Entry
        {
            public ProfileKind Kind;
            public string Name = "";
            public string Assembly = "";
            public string Mod = "";
            /// <summary>A watched method's interval for this window, and the one it was registered with (the least it goes back to).</summary>
            public int MethodInterval = 8, GivenInterval = 8;
        }

        static readonly object gate = new object();
        static readonly List<Entry> entries = new List<Entry>();
        static readonly Dictionary<(ProfileKind, Type), int> idsByType = new Dictionary<(ProfileKind, Type), int>();
        static readonly Dictionary<(ProfileKind, string), int> idsByName = new Dictionary<(ProfileKind, string), int>();
        static readonly char[] entityKindEnds = { ' ', '(' };

        // Per key, for the window being collected.
        static long[] calls = new long[64], timed = new long[64], ticks = new long[64], allocN = new long[64], allocB = new long[64], max = new long[64];
        static int[] methodCounter = new int[64];
        // Per key, for the frame being collected (only keys that are timed on every call).
        static long[] frameTicks = new long[64];
        static int[] touched = new int[64];
        static int touchedCount;
        // Per key, for the whole session.
        static double[] totalMs = new double[64], totalKb = new double[64], totalCalls = new double[64], totalMax = new double[64];

        // Sampling counts down to a random gap (mean = the interval) instead of taking every Nth call: the game calls its singletons in the
        // same order every frame, and a fixed stride can land on the same few of them every time and never on the rest.
        static int entityCountdown = 1, componentCountdown = 1, allocCountdown = 1;
        static uint random = 2463534242;
        static long windowEntityCalls, windowComponentCalls, windowExactCalls;
        static int frameExact, frameAllocPairs, frameSampledPairs;
        static readonly double[] row = new double[Table.Count];
        static readonly double[] spikeRow = new double[SpikeTable.Count];
        static readonly StringBuilder text = new StringBuilder();

        /// <summary>The biggest contributors to the frame just closed, largest first: key id and ticks. Valid until the next frame closes.</summary>
        public static readonly int[] TopIds = new int[TopK];
        public static readonly long[] TopTicks = new long[TopK];
        public static int TopCount { get; private set; }

        public static int Count { get { lock (gate) return entries.Count; } }

        /// <summary>
        /// The resolver the game side installs: the mod whose folder holds the assembly, else "game" for the game's own assemblies, else unknown.
        /// (The first recording labelled every game singleton "(unknown)" because the mod map alone was installed.)
        /// </summary>
        public static Func<string, string> ResolverFor(IReadOnlyDictionary<string, string> owners) =>
            assembly => owners.TryGetValue(assembly ?? "", out string mod) ? mod : DefaultModOf(assembly);

        /// <summary>"game" for the game's own assemblies, otherwise empty (unknown).</summary>
        internal static string DefaultModOf(string assembly)
        {
            if (string.IsNullOrEmpty(assembly)) return "";
            if (assembly.StartsWith("Timberborn.", StringComparison.Ordinal) || assembly.StartsWith("Bindito.", StringComparison.Ordinal) ||
                assembly.StartsWith("UnityEngine", StringComparison.Ordinal) || assembly.StartsWith("Unity.", StringComparison.Ordinal) ||
                assembly == "Assembly-CSharp" || assembly.StartsWith("System", StringComparison.Ordinal) || assembly == "mscorlib")
                return "game";
            return "";
        }

        /// <summary>Forgets everything. Called when a log starts.</summary>
        public static void Reset()
        {
            lock (gate)
            {
                entries.Clear();
                idsByType.Clear();
                idsByName.Clear();
                Generation++;
            }
            Array.Clear(calls, 0, calls.Length); Array.Clear(timed, 0, timed.Length); Array.Clear(ticks, 0, ticks.Length);
            Array.Clear(allocN, 0, allocN.Length); Array.Clear(allocB, 0, allocB.Length); Array.Clear(max, 0, max.Length);
            for (int i = 0; i < methodCounter.Length; i++) methodCounter[i] = 1;
            Array.Clear(frameTicks, 0, frameTicks.Length);
            Array.Clear(totalMs, 0, totalMs.Length); Array.Clear(totalKb, 0, totalKb.Length);
            Array.Clear(totalCalls, 0, totalCalls.Length); Array.Clear(totalMax, 0, totalMax.Length);
            touchedCount = 0; TopCount = 0;
            SeedSampling(2463534242);
            windowEntityCalls = 0; windowComponentCalls = 0; windowExactCalls = 0;
            frameExact = 0; frameAllocPairs = 0; frameSampledPairs = 0;
            EntityInterval = 16; ComponentInterval = 64; AllocEvery = 4;
            entityCountdown = Gap(EntityInterval); componentCountdown = Gap(ComponentInterval); allocCountdown = Gap(AllocEvery);
        }

        /// <summary>Restarts the random gaps from a known state. For tests, so they can be repeated.</summary>
        public static void SeedSampling(uint seed)
        {
            random = seed == 0 ? 2463534242 : seed;
            entityCountdown = Gap(EntityInterval); componentCountdown = Gap(ComponentInterval); allocCountdown = Gap(AllocEvery);
        }

        /// <summary>The number of calls until the next sample: 1 to 2N-1, so N on average. (xorshift, no allocation.)</summary>
        static int Gap(int interval)
        {
            if (interval <= 1) return 1;
            random ^= random << 13; random ^= random >> 17; random ^= random << 5;
            return 1 + (int)(random % (uint)(2 * interval - 1));
        }

        /// <summary>Sets the sampling by hand. For tests only: the log chooses it itself from what measuring costs.</summary>
        public static void Configure(int entityInterval, int componentInterval, int allocEvery)
        {
            EntityInterval = Math.Max(1, entityInterval);
            ComponentInterval = Math.Max(1, componentInterval);
            AllocEvery = Math.Max(1, allocEvery);
            entityCountdown = Gap(EntityInterval); componentCountdown = Gap(ComponentInterval); allocCountdown = Gap(AllocEvery);
        }

        /// <summary>
        /// Holds entity and component sampling off, so no call is one of the sampled ones, until the next <see cref="Reset"/> or
        /// <see cref="Configure"/>. For timing what the patch bodies cost on the calls that are not sampled (<see cref="Probe.MeasureUnsampled"/>).
        /// </summary>
        internal static void HoldSampling()
        {
            entityCountdown = int.MaxValue; componentCountdown = int.MaxValue;
        }

        // ---- keys ----

        /// <summary>The id of a class's key, registering it on first use. Game thread only.</summary>
        public static int IdFor(ProfileKind kind, Type type)
        {
            if (type == null) type = typeof(object);
            if (idsByType.TryGetValue((kind, type), out int id)) return id;
            string assembly = SafeAssemblyName(type);
            id = Register(kind, type.FullName ?? type.Name, assembly);
            idsByType[(kind, type)] = id;
            return id;
        }

        /// <summary>The id of a named key (an entity's kind, a method), registering it on first use. Game thread only.</summary>
        public static int IdFor(ProfileKind kind, string name, string assembly = "")
        {
            string key = name ?? "?";
            if (idsByName.TryGetValue((kind, key), out int id)) return id;
            // An entity is keyed by its kind, not by its own name (see EntityKindOf). The name is remembered as well, so this runs once
            // per name and every later tick of it is the lookup above, which allocates nothing.
            string keyName = kind == ProfileKind.Entity ? EntityKindOf(key) : key;
            if (!idsByName.TryGetValue((kind, keyName), out id))
            {
                id = Register(kind, keyName, assembly ?? "");
                idsByName[(kind, keyName)] = id;
            }
            idsByName[(kind, key)] = id;
            return id;
        }

        /// <summary>
        /// The kind of entity a tickable entity's name stands for: the text before its first space or '(', trimmed, or the whole name if that
        /// leaves nothing. The tick system records a character's name after the game has renamed one loaded from a save to
        /// "&lt;template&gt; &lt;its own name&gt;" (NamedEntityGameObjectSynchronizer), while one made during play keeps Unity's "&lt;template&gt;(Clone)",
        /// so "BeaverAdult Malak" and "BeaverAdult(Clone)" are both "BeaverAdult". tools/perflog.py (entity_kind) applies the same rule to
        /// recordings made before the mod did, so change both together.
        /// </summary>
        internal static string EntityKindOf(string name)
        {
            string trimmed = name.Trim();
            int cut = trimmed.IndexOfAny(entityKindEnds);
            string kind = cut < 0 ? trimmed : trimmed.Substring(0, cut).Trim();
            return kind.Length > 0 ? kind : name;
        }

        static string SafeAssemblyName(Type type)
        {
            try { return type.Assembly.GetName().Name ?? ""; }
            catch (Exception) { return ""; }
        }

        static int Register(ProfileKind kind, string name, string assembly)
        {
            string mod = "";
            try { mod = modResolver(assembly) ?? ""; } catch (Exception) { }
            int id;
            lock (gate)
            {
                id = entries.Count;
                entries.Add(new Entry { Kind = kind, Name = name, Assembly = assembly, Mod = mod });
            }
            if (id < methodCounter.Length) methodCounter[id] = 1;
            if (id >= calls.Length)
            {
                int size = calls.Length * 2;
                Array.Resize(ref calls, size); Array.Resize(ref timed, size); Array.Resize(ref ticks, size);
                Array.Resize(ref allocN, size); Array.Resize(ref allocB, size); Array.Resize(ref max, size);
                int old = methodCounter.Length;
                Array.Resize(ref methodCounter, size); Array.Resize(ref frameTicks, size); Array.Resize(ref touched, size);
                for (int i = old; i < size; i++) methodCounter[i] = 1;
                Array.Resize(ref totalMs, size); Array.Resize(ref totalKb, size); Array.Resize(ref totalCalls, size); Array.Resize(ref totalMax, size);
            }
            return id;
        }

        public static ProfileKind KindOf(int id) { lock (gate) return entries[id].Kind; }
        public static string NameOf(int id) { lock (gate) return entries[id].Name; }
        public static string AssemblyOf(int id) { lock (gate) return entries[id].Assembly; }
        public static string ModOf(int id) { lock (gate) return entries[id].Mod; }

        // ---- exact kinds: every call timed ----

        /// <summary>Starts timing a call of a singleton or load step. Pair with <see cref="EndExact"/>.</summary>
        public static Timing BeginExact()
        {
            Timing t;
            if (--allocCountdown <= 0)
            {
                allocCountdown = Gap(AllocEvery);
                frameAllocPairs++;
                t.Alloc = Alloc.Read();
            }
            else t.Alloc = -1;
            t.Start = Probe.Timestamp();
            return t;
        }

        public static void EndExact(int id, in Timing t)
        {
            long elapsed = Probe.Timestamp() - t.Start;
            long allocated = t.Alloc >= 0 ? Math.Max(0, Alloc.Read() - t.Alloc) : -1;
            calls[id]++; timed[id]++; ticks[id] += elapsed;
            if (elapsed > max[id]) max[id] = elapsed;
            if (allocated >= 0) { allocN[id]++; allocB[id] += allocated; }
            if (frameTicks[id] == 0)
            {
                if (touchedCount == touched.Length) Array.Resize(ref touched, touched.Length * 2);
                touched[touchedCount++] = id;
            }
            frameTicks[id] += elapsed > 0 ? elapsed : 1;
            windowExactCalls++; frameExact++;
        }

        // ---- sampled kinds ----

        /// <summary>Called before an entity ticks. Cheap unless this call is one of the sampled ones.</summary>
        public static Sample BeginEntity()
        {
            windowEntityCalls++;
            if (--entityCountdown > 0) return default;
            entityCountdown = Gap(EntityInterval);
            return Take();
        }

        public static void EndEntity(string prefab, in Sample s)
        {
            if (!s.On) return;
            try { EndSampled(ProfileKind.Entity, IdFor(ProfileKind.Entity, prefab ?? "?"), EntityInterval, s); }
            catch (Exception) { /* a failed measurement is not worth the game's time */ }
        }

        public static Sample BeginComponent()
        {
            windowComponentCalls++;
            if (--componentCountdown > 0) return default;
            componentCountdown = Gap(ComponentInterval);
            return Take();
        }

        public static void EndComponent(Type component, in Sample s)
        {
            if (!s.On) return;
            try { EndSampled(ProfileKind.Component, IdFor(ProfileKind.Component, component), ComponentInterval, s); }
            catch (Exception) { }
        }

        /// <summary>Called before a watched method runs: counts the call exactly, and times the first call of each window and about every Nth after it.</summary>
        public static Sample BeginMethod(int id)
        {
            calls[id]++;
            if (--methodCounter[id] > 0) return default;
            methodCounter[id] = Gap(entries[id].MethodInterval);
            return Take();
        }

        public static void EndMethod(int id, in Sample s)
        {
            if (!s.On) return;
            try
            {
                long elapsed = Probe.Timestamp() - s.Start;
                long allocated = Alloc.Enabled ? Math.Max(0, Alloc.Read() - s.Alloc) : 0;
                timed[id]++; ticks[id] += elapsed;
                if (elapsed > max[id]) max[id] = elapsed;
                allocN[id]++; allocB[id] += allocated;
            }
            catch (Exception) { }
        }

        /// <summary>Registers a watched method and returns its key id. Game thread, before its patch is applied.</summary>
        public static int RegisterMethod(string name, string assembly, int interval)
        {
            int id = IdFor(ProfileKind.Method, name, assembly);
            lock (gate) entries[id].MethodInterval = entries[id].GivenInterval = Math.Max(1, interval);
            return id;
        }

        static Sample Take()
        {
            if (!Probe.OnGameThread) return default;
            frameSampledPairs++;
            return new Sample { On = true, Start = Probe.Timestamp(), Alloc = Alloc.Enabled ? Alloc.Read() : 0 };
        }

        static void EndSampled(ProfileKind kind, int id, int interval, in Sample s)
        {
            long elapsed = Probe.Timestamp() - s.Start;
            long allocated = Alloc.Enabled ? Math.Max(0, Alloc.Read() - s.Alloc) : 0;
            calls[id] += interval; timed[id]++; ticks[id] += elapsed;
            if (elapsed > max[id]) max[id] = elapsed;
            allocN[id]++; allocB[id] += allocated;
        }

        // ---- load steps ----

        /// <summary>Writes one row for a step of the game's loading (window 0) straight into the profile file.</summary>
        public static void WriteLoadRow(ProfileKind kind, string name, string assembly, long elapsedTicks, int tick, Ring target, long allocBytes = 0)
        {
            int id = IdFor(kind, name, assembly);
            double ms = elapsedTicks * 1000.0 / Stopwatch.Frequency;
            double kb = Math.Max(0, allocBytes) / 1024.0;
            Array.Clear(row, 0, row.Length);
            row[0] = (int)kind; row[1] = 0; row[2] = tick; row[3] = id;
            row[4] = 1; row[5] = 1; row[6] = ms; row[7] = kb; row[8] = ms;
            target?.TryPush(row);
            totalMs[id] += ms; totalKb[id] += kb; totalCalls[id] += 1; if (ms > totalMax[id]) totalMax[id] = ms;
        }

        // ---- frames ----

        /// <summary>
        /// Called when a frame closes. Works out which timed-on-every-call keys took most of it, writes them to the spike file when
        /// the frame was slow, forgets this frame's per-key times, and returns what measuring cost in Stopwatch ticks.
        /// </summary>
        public static double EndFrame(bool slow, int frame, int tick, double utcMs, double frameMs, Ring spikeTarget, int topK)
        {
            TopCount = 0;
            if (touchedCount > 0)
            {
                int k = Math.Min(Math.Min(topK, TopK), touchedCount);
                for (int i = 0; i < touchedCount; i++)
                {
                    int id = touched[i];
                    long value = frameTicks[id];
                    frameTicks[id] = 0;
                    if (slow) Insert(id, value, k);
                }
                touchedCount = 0;
                if (slow && spikeTarget != null)
                {
                    double msPerTick = 1000.0 / Stopwatch.Frequency;
                    for (int r = 0; r < TopCount; r++)
                    {
                        double ms = TopTicks[r] * msPerTick;
                        Array.Clear(spikeRow, 0, spikeRow.Length);
                        spikeRow[0] = frame; spikeRow[1] = tick; spikeRow[2] = utcMs; spikeRow[3] = frameMs; spikeRow[4] = r + 1;
                        spikeRow[5] = (int)KindOf(TopIds[r]); spikeRow[6] = TopIds[r]; spikeRow[7] = ms;
                        spikeRow[8] = frameMs > 0 ? 100.0 * ms / frameMs : 0;
                        spikeTarget.TryPush(spikeRow);
                    }
                }
            }
            double cost = frameExact * Probe.ExactCallTicks + frameAllocPairs * Probe.AllocPairTicks + frameSampledPairs * Probe.SamplePairTicks;
            frameExact = 0; frameAllocPairs = 0; frameSampledPairs = 0;
            return cost;
        }

        static void Insert(int id, long value, int k)
        {
            if (k <= 0) return;
            if (TopCount == k && value <= TopTicks[TopCount - 1]) return;
            int at = TopCount < k ? TopCount++ : k - 1;
            while (at > 0 && TopTicks[at - 1] < value)
            {
                TopTicks[at] = TopTicks[at - 1]; TopIds[at] = TopIds[at - 1];
                at--;
            }
            TopTicks[at] = value; TopIds[at] = id;
        }

        // ---- windows ----

        /// <summary>
        /// Writes one row per key seen since the last call into <paramref name="target"/>, scaled up for the sampling, then forgets
        /// them and chooses the sampling for the next window.
        /// </summary>
        public static void FlushWindow(int window, int tick, double windowSeconds, Ring target)
        {
            double msPerTick = 1000.0 / Stopwatch.Frequency;
            int count;
            lock (gate) count = entries.Count;
            AdaptMethods(windowSeconds);
            for (int id = 0; id < count; id++)
            {
                if (calls[id] == 0 && timed[id] == 0) continue;
                if (timed[id] == 0)
                {
                    // Counted but never timed: a watched method whose timed calls all threw (Harmony skips the postfix then). The row says so
                    // with sampled 0; its time is unknown, so the session totals get nothing rather than these calls at 0 ms.
                    Array.Clear(row, 0, row.Length);
                    row[0] = (int)KindOf(id); row[1] = window; row[2] = tick; row[3] = id; row[4] = calls[id];
                    target?.TryPush(row);
                    calls[id] = 0; ticks[id] = 0; allocN[id] = 0; allocB[id] = 0; max[id] = 0;
                    continue;
                }
                double factor = (double)Math.Max(calls[id], timed[id]) / timed[id];
                double ms = ticks[id] * msPerTick * factor;
                double kb = allocN[id] > 0 ? allocB[id] / 1024.0 * ((double)Math.Max(calls[id], allocN[id]) / allocN[id]) : 0;
                double maxMs = max[id] * msPerTick;
                totalMs[id] += ms; totalKb[id] += kb; totalCalls[id] += calls[id]; if (maxMs > totalMax[id]) totalMax[id] = maxMs;
                // A watched method gets a row for every window it ran in, however little it took (there are at most a few dozen), so a
                // missing row always means it was not called.
                if (ms >= 0.005 || kb >= 0.5 || maxMs >= 0.5 || KindOf(id) == ProfileKind.Method)
                {
                    Array.Clear(row, 0, row.Length);
                    row[0] = (int)KindOf(id); row[1] = window; row[2] = tick; row[3] = id;
                    row[4] = calls[id]; row[5] = timed[id]; row[6] = ms; row[7] = kb; row[8] = maxMs;
                    target?.TryPush(row);
                }
                calls[id] = 0; timed[id] = 0; ticks[id] = 0; allocN[id] = 0; allocB[id] = 0; max[id] = 0;
            }
            Adapt(windowSeconds);
        }

        static void Adapt(double windowSeconds)
        {
            try
            {
                if (windowSeconds > 0 && Probe.SamplePairTicks > 0)
                {
                    double frequency = Stopwatch.Frequency;
                    double pairSeconds = Probe.SamplePairTicks / frequency + 100e-9; // the key lookup too
                    double budget = BudgetFraction;
                    EntityInterval = Clamp((int)Math.Ceiling(windowEntityCalls / windowSeconds * pairSeconds / (budget * BudgetShareEntity)), 1, 4096);
                    ComponentInterval = Clamp((int)Math.Ceiling(windowComponentCalls / windowSeconds * pairSeconds / (budget * BudgetShareComponent)), 1, 8192);
                    double allocPairSeconds = Math.Max(Probe.AllocPairTicks / frequency, 20e-9);
                    AllocEvery = Clamp((int)Math.Ceiling(windowExactCalls / windowSeconds * allocPairSeconds / (budget * BudgetShareAlloc)), 1, 1024);
                }
            }
            catch (Exception) { }
            windowEntityCalls = 0; windowComponentCalls = 0; windowExactCalls = 0;
        }

        /// <summary>
        /// Chooses each watched method's interval for the next window from that method's own calls in this one, before they are forgotten.
        /// It is set again every window the method ran in and never below the interval the method was given, so a method that was busy once
        /// comes back down when it runs less, and a rare one next to a busy one keeps its own. A window it did not run in says nothing about
        /// its rate, so its interval is kept: a method that is busy in bursts is timed at the interval its last burst needed, not at the given
        /// one. The methods' share of the budget is split between the methods that ran. Each countdown starts again at 1, so a method that
        /// runs in the next window has its first call timed.
        /// </summary>
        static void AdaptMethods(double windowSeconds)
        {
            try
            {
                lock (gate)
                {
                    int ran = 0;
                    for (int id = 0; id < entries.Count; id++)
                        if (entries[id].Kind == ProfileKind.Method && calls[id] > 0) ran++;
                    bool measured = windowSeconds > 0 && Probe.SamplePairTicks > 0;
                    double pairSeconds = Probe.SamplePairTicks / Stopwatch.Frequency + 100e-9; // the key lookup too
                    double budget = BudgetFraction * BudgetShareMethod / Math.Max(1, ran);
                    for (int id = 0; id < entries.Count; id++)
                    {
                        Entry e = entries[id];
                        if (e.Kind != ProfileKind.Method) continue;
                        methodCounter[id] = 1;
                        if (calls[id] == 0) continue;
                        double wanted = measured ? Math.Ceiling(calls[id] / windowSeconds * pairSeconds / budget) : 0;
                        int most = Math.Max(e.GivenInterval, 4096);
                        e.MethodInterval = wanted >= most ? most : Math.Max(e.GivenInterval, (int)wanted);
                    }
                }
            }
            catch (Exception) { }
        }

        static int Clamp(int value, int low, int high) => value < low ? low : value > high ? high : value;

        // ---- text columns, added by the writer after the numbers ----

        /// <summary>Appends ",name,assembly,mod" for a profile row, made safe for a CSV field. Called on the writer thread.</summary>
        public static void AppendProfileText(ReadOnlySpan<double> profileRow, StringBuilder into)
        {
            int id = (int)profileRow[3];
            Entry e = EntryOrNull(id);
            into.Append(',').Append(Clean(e?.Name)).Append(',').Append(Clean(e?.Assembly)).Append(',').Append(Clean(e?.Mod));
        }

        /// <summary>Appends ",name,mod" for a spike row. Called on the writer thread.</summary>
        public static void AppendSpikeText(ReadOnlySpan<double> spike, StringBuilder into)
        {
            int id = (int)spike[6];
            Entry e = EntryOrNull(id);
            into.Append(',').Append(Clean(e?.Name)).Append(',').Append(Clean(e?.Mod));
        }

        static Entry EntryOrNull(int id)
        {
            lock (gate) return id >= 0 && id < entries.Count ? entries[id] : null;
        }

        /// <summary>Makes text safe for one CSV field: no separator, quote or line break.</summary>
        public static string Clean(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            bool dirty = false;
            foreach (char c in value)
                if (c == ',' || c == '"' || c == '\r' || c == '\n' || c == '\t' || c == '|') { dirty = true; break; }
            if (!dirty) return value;
            var chars = value.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (c == ',') chars[i] = ';';
                else if (c == '"') chars[i] = '\'';
                else if (c == '\r' || c == '\n' || c == '\t' || c == '|') chars[i] = ' ';
            }
            return new string(chars);
        }

        // ---- the whole session, for the summary ----

        public sealed class Total
        {
            public int Id;
            public ProfileKind Kind;
            public string Name, Assembly, Mod;
            public double Ms, Kb, Calls, MaxMs;
        }

        /// <summary>Every key's totals over the session so far (windows already flushed). Call on the game thread.</summary>
        public static List<Total> Totals()
        {
            var result = new List<Total>();
            lock (gate)
            {
                for (int id = 0; id < entries.Count; id++)
                {
                    Entry e = entries[id];
                    if (totalCalls[id] == 0 && totalMs[id] == 0) continue;
                    result.Add(new Total { Id = id, Kind = e.Kind, Name = e.Name, Assembly = e.Assembly, Mod = e.Mod, Ms = totalMs[id], Kb = totalKb[id], Calls = totalCalls[id], MaxMs = totalMax[id] });
                }
            }
            return result;
        }
    }
}
