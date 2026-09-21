using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PerformanceLog
{
    /// <summary>The places whose time is measured, per frame. Times are exclusive: a scope nested in another is taken out of the outer one.</summary>
    public enum Slot
    {
        Tick = 0,
        Singletons,
        Entities,
        ParallelWait,
        ParallelStart,
        Update,
        LateUpdate,
        Save,
    }

    /// <summary>Things that are counted per frame instead of timed.</summary>
    public enum Counter
    {
        /// <summary>Entity ticks that ran (each tickable entity, once per tick).</summary>
        Entities = 0,
        /// <summary>Executions of this mod's own Harmony patches, for the estimate of what measuring costs.</summary>
        PatchCalls,
    }

    /// <summary>What a row of the profile or spike files is about. Written as a word.</summary>
    public enum ProfileKind
    {
        TickSingleton = 0,
        UpdateSingleton,
        LateSingleton,
        ParallelStart,
        Entity,
        Component,
        Method,
        Load,
        LoadNonSingleton,
        PostLoad,
        PostLoadNonSingleton,
    }

    /// <summary>
    /// Text is one character. Word is a number that stands for one of a fixed list of words. Tail is free text that the writer adds after
    /// the numbers of a row (a name); tail columns come last and are not part of the row array.
    /// </summary>
    public enum ColumnKind { Text, Int, Fixed1, Fixed2, Word, Tail }

    /// <summary>How a column of frame rows turns into a column of a summary row over many frames.</summary>
    public enum Aggregate { Last, Sum, SumPositive, Mean, Max }

    /// <summary>One column: its name, how it is written and combined, its unit and what it means. The docs are generated from this.</summary>
    public sealed class Column
    {
        public string Name;
        public ColumnKind Kind;
        public Aggregate Aggregate;
        public string Unit;
        public string Description;
        /// <summary>For <see cref="ColumnKind.Word"/>: the text written for value 0, 1, 2...</summary>
        public string[] Words;
    }

    /// <summary>A set of columns and the text of a row. Nothing here allocates per row.</summary>
    public sealed class Table
    {
        public readonly Column[] Columns;
        public readonly string[] Names;
        /// <summary>How many columns a row array has: every column except the trailing text ones.</summary>
        public readonly int Count;

        public Table(IList<Column> columns)
        {
            Columns = new Column[columns.Count];
            Names = new string[columns.Count];
            Count = columns.Count;
            for (int i = 0; i < Columns.Length; i++)
            {
                Columns[i] = columns[i]; Names[i] = columns[i].Name;
                if (columns[i].Kind == ColumnKind.Tail && Count == columns.Count) Count = i;
            }
        }

        public string HeaderLine() => string.Join(",", Names);

        public int IndexOf(string name) => Array.IndexOf(Names, name);

        /// <summary>
        /// Writes one row as comma separated text, without a line break, and without allocating. Numbers are written the invariant way,
        /// so a language that writes 1,5 cannot add columns. False if it did not fit.
        /// </summary>
        public bool TryFormatRow(ReadOnlySpan<double> row, Span<char> destination, out int written)
        {
            written = 0;
            int position = 0;
            for (int i = 0; i < Count; i++)
            {
                if (i > 0)
                {
                    if (position >= destination.Length) return false;
                    destination[position++] = ',';
                }
                double value = row[i];
                if (double.IsNaN(value) || double.IsInfinity(value)) value = 0;
                int length;
                Column column = Columns[i];
                switch (column.Kind)
                {
                    case ColumnKind.Text:
                        if (position >= destination.Length) return false;
                        destination[position++] = (char)(int)value;
                        continue;
                    case ColumnKind.Word:
                    {
                        string[] words = column.Words;
                        int index = (int)Math.Round(value);
                        string word = words != null && index >= 0 && index < words.Length ? words[index] : "?";
                        if (position + word.Length > destination.Length) return false;
                        word.AsSpan().CopyTo(destination.Slice(position));
                        position += word.Length;
                        continue;
                    }
                    case ColumnKind.Int:
                        if (!((long)Math.Round(value)).TryFormat(destination.Slice(position), out length, default, CultureInfo.InvariantCulture)) return false;
                        break;
                    case ColumnKind.Fixed1:
                        if (!value.TryFormat(destination.Slice(position), out length, "F1", CultureInfo.InvariantCulture)) return false;
                        break;
                    default:
                        if (!value.TryFormat(destination.Slice(position), out length, "F2", CultureInfo.InvariantCulture)) return false;
                        break;
                }
                position += length;
            }
            written = position;
            return true;
        }

        /// <summary>The table as a Markdown glossary: name, unit, how a summary row combines it, meaning.</summary>
        public string GlossaryMarkdown()
        {
            var text = new StringBuilder();
            text.Append("| Column | Unit | In S rows | Meaning |\n|---|---|---|---|\n");
            foreach (Column c in Columns)
                text.Append("| `").Append(c.Name).Append("` | ").Append(c.Unit).Append(" | ").Append(AggregateText(c.Aggregate)).Append(" | ")
                    .Append(c.Description.Replace("|", "/")).Append(" |\n");
            return text.ToString();
        }

        public static string AggregateText(Aggregate aggregate)
        {
            switch (aggregate)
            {
                case Aggregate.Sum: return "total";
                case Aggregate.SumPositive: return "total of the increases";
                case Aggregate.Mean: return "average per frame";
                case Aggregate.Max: return "largest";
                default: return "last";
            }
        }
    }

    /// <summary>
    /// The columns of frames.csv, in order. Every row has all of them. There are two kinds of row: F (one frame that took at least the
    /// slow-frame threshold) and S (a summary of every frame in one window). In S rows times and Unity's figures are averages per frame;
    /// allocation, counts and the two histograms are totals over the window's frames.
    /// </summary>
    public static class Columns
    {
        public const int SlotCount = 8;
        public const int CounterCount = 2;
        public const int PhaseCount = 8;
        public const int ExtraPerFrameCount = 11;
        public const int ExtraHeavyCount = 8;
        public const int FrameHistCount = 17;
        public const int TickHistCount = 6;

        public const char FrameRow = 'F', SummaryRow = 'S';

        /// <summary>Upper edges in milliseconds of the frame time buckets; the last bucket is everything above.</summary>
        public static readonly double[] FrameEdgesMs = { 4, 6, 8.5, 11.5, 14, 17.5, 21, 25, 30, 35, 42, 50, 75, 100, 200, 400 };

        public static readonly string[] SlotTimeNames = { "tickMs", "singMs", "entMs", "parWaitMs", "parStartMs", "updMs", "lateMs", "saveMs" };
        public static readonly string[] SlotAllocNames = { "tickKB", "singKB", "entKB", "parWaitKB", "parStartKB", "updKB", "lateKB", "saveKB" };

        static readonly string[] slotMeaning =
        {
            "The tick loop itself: Ticker.Update minus the parts below. The game's own tick bookkeeping, and other mods' patches on the tick loop.",
            "The once-per-tick singletons (the game's and mods'), which run once at the start of every tick.",
            "Every entity's tick: beavers, buildings, plants and the rest, across all of the tick's entity buckets.",
            "The game thread waiting for the parallel part of the tick (pathfinding, water and so on) to finish on the worker threads.",
            "The game thread starting the parallel part of the tick: scheduling work for the worker threads.",
            "Per-frame singleton updates (IUpdatableSingleton): the user interface, camera, input and many mods.",
            "Per-frame late singleton updates (ILateUpdatableSingleton).",
            "Saving the game (an autosave or a manual save).",
        };

        public static readonly string[] CounterNames = { "entities", "patchCalls" };
        static readonly string[] counterMeaning =
        {
            "Entity ticks that ran: each tickable entity counts once per tick.",
            "Executions of this mod's own Harmony patches. Only used to estimate what measuring costs.",
        };

        public static readonly string[] PhaseNames = { "plTime", "plInit", "plEarly", "plFixed", "plPre", "plUpdate", "plLate", "plPost" };
        static readonly string[] phaseMeaning =
        {
            "Unity's time update. The wait for the previous frame to be presented can be here.",
            "Unity's initialization phase.",
            "Early update (input).",
            "Fixed update (physics).",
            "Pre-update.",
            "Update: every script's Update, including the game's tick loop and the singleton updates.",
            "Late update (LateUpdate scripts, cameras).",
            "Post late update: drawing, presenting the frame and the wait for vertical sync.",
        };

        public static readonly string[] ExtraPerFrameNames =
        {
            "prGcBytes", "prGcCount", "prDraw", "prSetPass", "prBatches", "prTris", "ftCpu", "ftMain", "ftRender", "ftGpu", "ftWait",
        };
        static readonly string[] extraPerFrameMeaning =
        {
            "Unity profiler: bytes allocated on the managed heap in the last frame (0 if the counter is not available).",
            "Unity profiler: number of managed allocations in the last frame.",
            "Unity profiler: draw calls.",
            "Unity profiler: set-pass calls (material switches).",
            "Unity profiler: batches.",
            "Unity profiler: triangles drawn.",
            "Unity frame timing: the frame on the processor. 0 when the game's player settings leave frame timing off.",
            "Unity frame timing: the main thread.",
            "Unity frame timing: the render thread.",
            "Unity frame timing: the graphics card.",
            "Unity frame timing: the main thread waiting to present (vertical sync, or a graphics card that is behind).",
        };
        static readonly string[] extraPerFrameUnit = { "bytes", "count", "count", "count", "count", "count", "ms", "ms", "ms", "ms", "ms" };

        public static readonly string[] ExtraHeavyNames =
        {
            "monoHeapMB", "monoUsedMB", "nativeMB", "workingMB", "colEntities", "colBeavers", "colBots", "colDay",
        };
        static readonly string[] extraHeavyMeaning =
        {
            "Unity's managed heap, reserved.",
            "Unity's managed heap, in use.",
            "All memory Unity has allocated, including the native side.",
            "The process's working set (memory the game holds in RAM).",
            "Colony size: all entities in the game.",
            "Colony size: beavers alive.",
            "Colony size: bots alive.",
            "Colony size: the day number.",
        };
        static readonly string[] extraHeavyUnit = { "MB", "MB", "MB", "MB", "count", "count", "count", "day" };
        static readonly ColumnKind[] extraHeavyKind =
            { ColumnKind.Fixed1, ColumnKind.Fixed1, ColumnKind.Fixed1, ColumnKind.Fixed1, ColumnKind.Int, ColumnKind.Int, ColumnKind.Int, ColumnKind.Int };

        static readonly List<Column> list = new List<Column>();

        static int Add(string name, ColumnKind kind, Aggregate aggregate, string unit, string description, string[] words = null)
        {
            list.Add(new Column { Name = name, Kind = kind, Aggregate = aggregate, Unit = unit, Description = description, Words = words });
            return list.Count - 1;
        }

        public static readonly int Type = Add("type", ColumnKind.Text, Aggregate.Last, "", "F: one frame that took at least the slow-frame threshold. S: a summary of every frame in one window.");
        public static readonly int Frame = Add("frame", ColumnKind.Int, Aggregate.Last, "count", "Frame number since the log started.");
        public static readonly int Tick = Add("tick", ColumnKind.Int, Aggregate.Last, "count", "Simulation ticks since the log started (not the save's own tick count).");
        public static readonly int UtcMs = Add("utcMs", ColumnKind.Int, Aggregate.Last, "ms", "Wall clock, milliseconds since 1970 UTC.");
        public static readonly int Frames = Add("frames", ColumnKind.Int, Aggregate.Sum, "count", "How many frames the row covers: 1 in F rows, the count in S rows.");
        public static readonly int FrameMs = Add("frameMs", ColumnKind.Fixed2, Aggregate.Mean, "ms", "Frame time: from the start of one frame to the start of the next, so it includes drawing and the wait for vertical sync.");
        public static readonly int MaxFrameMs = Add("maxFrameMs", ColumnKind.Fixed2, Aggregate.Max, "ms", "The slowest frame in the row.");
        public static readonly int Ticks = Add("ticks", ColumnKind.Int, Aggregate.Sum, "count", "Simulation ticks run in the row's frames. More than 1 per frame means the game is catching up or running at a high speed.");
        public static readonly int Buckets = Add("buckets", ColumnKind.Int, Aggregate.Sum, "count", "Tick buckets run. The game splits a tick into 129 buckets (one for the singletons, 128 for entities) and runs as many per frame as time allows.");
        public static readonly int Speed = Add("speed", ColumnKind.Fixed2, Aggregate.Last, "x", "The game speed: 0 paused, 1, 2, 3 and so on.");
        public static readonly int Paused = Add("paused", ColumnKind.Int, Aggregate.Sum, "frames", "1 if the game was paused (speed 0) in the frame.");
        public static readonly int Saving = Add("saving", ColumnKind.Int, Aggregate.Sum, "frames", "1 if a save ran in the frame.");
        public static readonly int Unfocused = Add("unfocused", ColumnKind.Int, Aggregate.Sum, "frames", "1 if the game window was in the background. The system throttles a background window, so such frames say little.");

        public static readonly int SlotBase = list.Count;
        static readonly int slotsAdded = AddSlots();
        static int AddSlots()
        {
            for (int i = 0; i < SlotCount; i++)
                Add(SlotTimeNames[i], ColumnKind.Fixed2, Aggregate.Mean, "ms", slotMeaning[i]);
            return SlotCount;
        }
        public static readonly int OtherMs = Add("otherMs", ColumnKind.Fixed2, Aggregate.Mean, "ms", "The rest of the frame: drawing, animation, other scripts, other mods, the system. Every column above adds up with this one to frameMs.");

        public static readonly int AllocBase = list.Count;
        static readonly int allocAdded = AddAllocs();
        static int AddAllocs()
        {
            for (int i = 0; i < SlotCount; i++)
                Add(SlotAllocNames[i], ColumnKind.Fixed1, Aggregate.Sum, "KB", "Kilobytes allocated inside the slot of the same name. (Coarse: see the allocation source in the capability lines.)");
            return SlotCount;
        }
        public static readonly int OtherKB = Add("otherKB", ColumnKind.Fixed1, Aggregate.Sum, "KB", "Kilobytes allocated outside every timed slot.");

        public static readonly int GcDelta = Add("gcDelta", ColumnKind.Int, Aggregate.Sum, "count", "Garbage collections during the row's frames. A frame with one is usually one long frame.");
        public static readonly int HeapMB = Add("heapMB", ColumnKind.Fixed1, Aggregate.Last, "MB", "Managed memory in use.");
        public static readonly int AllocKB = Add("allocKB", ColumnKind.Fixed1, Aggregate.SumPositive, "KB", "How much heapMB grew since the previous frame. Negative (not counted in S rows) means a collection freed memory.");
        public static readonly int ProbeUs = Add("probeUs", ColumnKind.Fixed1, Aggregate.Mean, "us", "What closing this frame cost this mod.");
        public static readonly int OverheadUs = Add("overheadUs", ColumnKind.Fixed1, Aggregate.Mean, "us", "Estimated cost of this mod's timers, samples and patches in the frame. Add probeUs for the whole cost of measuring.");
        public static readonly int Dropped = Add("dropped", ColumnKind.Int, Aggregate.Last, "rows", "Rows lost because the file writer fell behind. Should be 0.");

        public static readonly int CounterBase = list.Count;
        static readonly int countersAdded = AddCounters();
        static int AddCounters()
        {
            for (int i = 0; i < CounterCount; i++)
                Add(CounterNames[i], ColumnKind.Int, Aggregate.Sum, "count", counterMeaning[i]);
            return CounterCount;
        }
        public static readonly int ParTickMs = Add("parTickMs", ColumnKind.Fixed2, Aggregate.Sum, "ms", "The game's own figure for how long its parallel tick took, from starting it to the last worker finishing. Compare with parWaitMs.");

        public static readonly int MainCpuMs = Add("mainCpuMs", ColumnKind.Fixed2, Aggregate.Mean, "ms", "Processor time the game thread used in the frame. Windows only. Charged in scheduler quanta, so one frame's figure is coarse and only S rows are exact.");
        public static readonly int MainMcyc = Add("mainMcyc", ColumnKind.Fixed2, Aggregate.Mean, "Mcycles", "Processor cycles the game thread used in the frame, in millions. Windows only.");
        public static readonly int ProcCpuMs = Add("procCpuMs", ColumnKind.Fixed2, Aggregate.Mean, "ms", "Processor time the whole process used in the frame (all threads). Windows only.");

        public static readonly int PhaseBase = list.Count;
        static readonly int phasesAdded = AddPhases();
        static int AddPhases()
        {
            for (int i = 0; i < PhaseCount; i++)
                Add(PhaseNames[i], ColumnKind.Fixed2, Aggregate.Mean, "ms", "Unity frame phase: " + phaseMeaning[i]);
            return PhaseCount;
        }

        public static readonly int ExtraBase = list.Count;
        static readonly int extraAdded = AddExtras();
        static int AddExtras()
        {
            for (int i = 0; i < ExtraPerFrameCount; i++)
                Add(ExtraPerFrameNames[i], ColumnKind.Fixed1, Aggregate.Mean, extraPerFrameUnit[i], extraPerFrameMeaning[i]);
            return ExtraPerFrameCount;
        }

        public static readonly int HeavyBase = list.Count;
        static readonly int heavyAdded = AddHeavy();
        static int AddHeavy()
        {
            for (int i = 0; i < ExtraHeavyCount; i++)
                Add(ExtraHeavyNames[i], extraHeavyKind[i], Aggregate.Last, extraHeavyUnit[i], extraHeavyMeaning[i] + " Read only when a row is written.");
            return ExtraHeavyCount;
        }

        public static readonly int FrameHistBase = list.Count;
        static readonly int frameHistAdded = AddFrameHist();
        static int AddFrameHist()
        {
            for (int i = 0; i < FrameHistCount; i++)
                Add("fh" + i, ColumnKind.Int, Aggregate.Sum, "frames", "Frames whose time fell in frame-time bucket " + i + " (the bucket edges are in the '# histogram|frameEdgesMs' header line). Vertical sync shows as peaks at 16.7 and 33.3 ms.");
            return FrameHistCount;
        }

        static readonly string[] tickHistLabels = { "0 ticks", "1 tick", "2 ticks", "3 to 4 ticks", "5 to 9 ticks", "10 or more ticks" };
        public static readonly int TickHistBase = list.Count;
        static readonly int tickHistAdded = AddTickHist();
        static int AddTickHist()
        {
            for (int i = 0; i < TickHistCount; i++)
                Add("th" + i, ColumnKind.Int, Aggregate.Sum, "frames", "Frames that ran " + tickHistLabels[i] + ".");
            return TickHistCount;
        }

        public static readonly int Count = list.Count;
        public static readonly Table Main = new Table(list);
        public static readonly string[] Names = Main.Names;
        public static readonly Aggregate[] Aggregates = MakeAggregates();

        static Aggregate[] MakeAggregates()
        {
            var result = new Aggregate[Count];
            for (int i = 0; i < Count; i++) result[i] = Main.Columns[i].Aggregate;
            return result;
        }

        public static string HeaderLine() => Main.HeaderLine();

        public static bool TryFormatRow(ReadOnlySpan<double> row, Span<char> destination, out int written) =>
            Main.TryFormatRow(row, destination, out written);

        /// <summary>Which frame time bucket a frame of this many milliseconds falls in.</summary>
        public static int FrameBucket(double milliseconds)
        {
            for (int i = 0; i < FrameEdgesMs.Length; i++)
                if (milliseconds < FrameEdgesMs[i]) return i;
            return FrameEdgesMs.Length;
        }

        /// <summary>Which bucket a count of ticks run in one frame falls in: 0, 1, 2, 3-4, 5-9, 10 or more.</summary>
        public static int TickBucket(int ticks) => ticks <= 2 ? Math.Max(0, ticks) : ticks <= 4 ? 3 : ticks <= 9 ? 4 : 5;

        /// <summary>True for the slots whose allocation is measured.</summary>
        public static readonly bool[] SlotTracksAlloc = { true, true, true, true, true, true, true, true };
    }

    /// <summary>The words written for a <see cref="ProfileKind"/>, and what each kind measures.</summary>
    public static class ProfileKinds
    {
        public static readonly string[] Words =
        {
            "tick-singleton", "update-singleton", "late-singleton", "parallel-start", "entity", "component", "method",
            "load", "load-non-singleton", "post-load", "post-load-non-singleton",
        };

        public static readonly string[] Meaning =
        {
            "A singleton's Tick (once per simulation tick). Every call is timed.",
            "A singleton's UpdateSingleton (once per frame). Every call is timed.",
            "A singleton's LateUpdateSingleton (once per frame). Every call is timed.",
            "A parallel singleton's StartParallelTick on the game thread: scheduling only, the work itself runs on worker threads and is not visible here. Every call is timed.",
            "All the ticks of one kind of entity (a prefab such as a beaver or a farm house). Only every Nth call is timed and the result is scaled up (see 'sampled').",
            "All the ticks of one kind of entity component (a class such as Walker). Only every Nth call is timed and the result is scaled up. Only recorded with Profile = deep.",
            "One method from the Watch list in the config, timed including everything inside it and every patch on it. Calls are counted exactly and every Nth is timed.",
            "A singleton's Load while the game was loading (one row per singleton, window 0).",
            "A non-singleton loader's LoadNonSingletons while the game was loading (window 0).",
            "A singleton's PostLoad while the game was loading (window 0).",
            "A non-singleton post-loader's PostLoadNonSingletons while the game was loading (window 0).",
        };

        /// <summary>Kinds whose every call is timed, so a slow frame can be blamed on them exactly.</summary>
        public static bool IsExact(ProfileKind kind) =>
            kind == ProfileKind.TickSingleton || kind == ProfileKind.UpdateSingleton || kind == ProfileKind.LateSingleton ||
            kind == ProfileKind.ParallelStart || kind >= ProfileKind.Load;
    }
}
