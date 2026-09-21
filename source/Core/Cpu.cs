using System;
using System.Runtime.InteropServices;

namespace PerformanceLog
{
    /// <summary>
    /// How much processor time the game thread and the whole process used, from Windows. Set beside the wall clock it says whether a slow
    /// frame was the game thread working or waiting (for the graphics card, for vertical sync, for another thread, or because the
    /// system did not run it). Windows only; anywhere else, or if the calls fail, it reports that it is unavailable and stays out of the way.
    /// The thread's time is charged in scheduler quanta, so one frame's figure is coarse and only sums over many frames are exact.
    /// </summary>
    public static class Cpu
    {
        public static bool Available { get; private set; }

        static IntPtr processHandle;

        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll")] static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll")] static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

        /// <summary>Call on the game thread. False if this system cannot say.</summary>
        public static bool Init()
        {
            Available = false;
            try
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return false;
                processHandle = GetCurrentProcess();
                Available = Sample(out _, out _, out _);
            }
            catch (Exception)
            {
                Available = false;
            }
            return Available;
        }

        /// <summary>
        /// The game thread's and the process's processor time so far, in 100 nanosecond units, and the game thread's cycle count.
        /// Must be called from the thread being measured.
        /// </summary>
        public static bool Sample(out long threadCpu, out ulong threadCycles, out long processCpu)
        {
            threadCpu = 0; threadCycles = 0; processCpu = 0;
            try
            {
                IntPtr thread = GetCurrentThread();
                if (!GetThreadTimes(thread, out _, out _, out long threadKernel, out long threadUser)) return false;
                if (!QueryThreadCycleTime(thread, out threadCycles)) return false;
                if (!GetProcessTimes(processHandle, out _, out _, out long processKernel, out long processUser)) return false;
                threadCpu = threadKernel + threadUser;
                processCpu = processKernel + processUser;
                return true;
            }
            catch (Exception)
            {
                Available = false;
                return false;
            }
        }
    }

    /// <summary>
    /// How much memory the process holds in RAM (its working set). Unity's Mono reports 0 for <c>Environment.WorkingSet</c>, so on Windows
    /// it is asked of the system directly; anywhere else, or if that fails, the runtime's own figure is used and 0 means "not available".
    /// </summary>
    public static class ProcessMemory
    {
        [StructLayout(LayoutKind.Sequential)]
        struct Counters
        {
            public uint Size;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
        }

        [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")]
        static extern bool GetProcessMemoryInfo(IntPtr process, ref Counters counters, uint size);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();

        static bool windowsFailed;

        /// <summary>The working set in bytes, or 0 if it cannot be read.</summary>
        public static long WorkingSetBytes()
        {
            if (!windowsFailed)
            {
                try
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        var counters = new Counters { Size = (uint)Marshal.SizeOf(typeof(Counters)) };
                        if (GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.Size)) return (long)(ulong)counters.WorkingSetSize;
                    }
                    windowsFailed = true;
                }
                catch (Exception) { windowsFailed = true; }
            }
            try { return Environment.WorkingSet; }
            catch (Exception) { return 0; }
        }

        /// <summary>For the header: where the working set figure comes from, or that there is none.</summary>
        public static string Describe() =>
            WorkingSetBytes() <= 0 ? "not available (the working set column stays 0)" :
            windowsFailed ? "from the runtime" : "from Windows";
    }
}
