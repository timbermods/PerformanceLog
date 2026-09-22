using System;
using System.Diagnostics;
using System.Reflection;

namespace PerformanceLog
{
    /// <summary>
    /// How many bytes this program has allocated, as cheaply as the runtime allows. Differences of it say which sections allocate.
    /// Unity's Mono may or may not have a per-thread counter, so it is tried, checked against a known allocation, and otherwise the
    /// size of the managed heap is used (which grows only when the heap takes new blocks and falls at a garbage collection, so individual
    /// readings are coarse, a frame with a collection loses what it allocated, and only sums over many readings mean anything).
    /// </summary>
    public static class Alloc
    {
        public const int ModeNone = 0, ModeThread = 1, ModeHeap = 2;

        /// <summary>Which counter <see cref="Read"/> uses.</summary>
        public static int Mode { get; private set; }

        public static string ModeName => Mode == ModeThread ? "GC.GetAllocatedBytesForCurrentThread (exact)" :
                                         Mode == ModeHeap ? "GC.GetTotalMemory(false) (coarse: grows with allocation, falls at a garbage collection)" : "none";

        /// <summary>Why the exact counter was not used, when it was tried and rejected. Empty otherwise. For the header.</summary>
        public static string Note { get; private set; } = "";

        /// <summary>The mode's name and, if the exact counter was rejected, why (one line, no pipes).</summary>
        public static string Describe() => Note.Length == 0 ? ModeName : ModeName + "; " + Note;

        static Func<long> threadBytes;
        // A test's stand-in for the heap size (see UseTestSource); null in a game.
        static Func<long> heapBytes;

        /// <summary>True when allocation can be measured at all.</summary>
        public static bool Enabled => Mode != ModeNone;

        /// <summary>Chooses the counter. Safe to call more than once. <paramref name="preferHeap"/> is for tests.</summary>
        public static void Init(bool preferHeap = false)
        {
            threadBytes = null;
            heapBytes = null;
            Mode = ModeNone;
            Note = "";
            try
            {
                if (!preferHeap)
                {
                    MethodInfo method = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                    if (method == null) Note = "GC.GetAllocatedBytesForCurrentThread does not exist in this runtime";
                    else
                    {
                        var candidate = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), method);
                        long before = candidate();
                        // Allocate something that cannot be optimized away and see whether the counter noticed.
                        byte[] probe = new byte[64 * 1024];
                        probe[0] = 1;
                        long after = candidate();
                        GC.KeepAlive(probe);
                        if (after - before >= 60 * 1024) { threadBytes = candidate; Mode = ModeThread; return; }
                        Note = "GC.GetAllocatedBytesForCurrentThread exists but did not count a 64 KB allocation (read " + before + ", then " + after + ")";
                    }
                }
                GC.GetTotalMemory(false);
                Mode = ModeHeap;
            }
            catch (Exception)
            {
                threadBytes = null;
                Mode = ModeNone;
            }
        }

        /// <summary>
        /// Replaces the counter with one the caller moves by hand. For tests only. Null goes back to <see cref="Init"/>. With
        /// <paramref name="asHeapSize"/> it stands in for the heap size (<see cref="ModeHeap"/>), the counter the game gets.
        /// </summary>
        public static void UseTestSource(Func<long> source, bool asHeapSize = false)
        {
            if (source == null) { Init(); return; }
            threadBytes = asHeapSize ? null : source;
            heapBytes = asHeapSize ? source : null;
            Mode = asHeapSize ? ModeHeap : ModeThread;
        }

        /// <summary>The counter now, in bytes. Differences between two readings are what matters.</summary>
        public static long Read()
        {
            switch (Mode)
            {
                case ModeThread: return threadBytes();
                case ModeHeap: return heapBytes != null ? heapBytes() : GC.GetTotalMemory(false);
                default: return 0;
            }
        }

        /// <summary>What one <see cref="Read"/> costs, in Stopwatch ticks, averaged over many.</summary>
        public static double MeasureReadTicks(int repeats = 2000)
        {
            if (Mode == ModeNone) return 0;
            long total = 0;
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < repeats; i++) total += Read();
            long end = Stopwatch.GetTimestamp();
            GC.KeepAlive(total.ToString());
            return (end - start) / (double)repeats;
        }
    }
}
