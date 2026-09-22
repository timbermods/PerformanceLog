using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using PerformanceLog;

namespace PerformanceLog.Tests
{
    // Runs the mod's checks without the game. The core (timing, rows, files, summary) runs for real against a scripted clock; the game
    // side is checked against the installed game's real assemblies (patch targets exist, none has an exception filter, the singleton
    // wrappers work on the game's own classes). What needs the running game (Unity's player loop, Harmony applying a patch, the
    // profiler counters) cannot run here and is listed in docs/TESTING.md.
    //
    //   dotnet run --project tests                         run every check
    //   dotnet run --project tests -- --print-columns      print the columns of frames.csv, profile.csv and spikes.csv
    //   dotnet run --project tests -- --write-sample DIR   write an example session (made up, not from a game) into DIR
    internal static class Program
    {
        static string managed = @"C:\Program Files (x86)\Steam\steamapps\common\Timberborn\Timberborn_Data\Managed";
        // Where the Mod Settings mod's assemblies live (Settings.cs references ModSettings.Core and ModSettings.Common).
        const string modSettingsDirectory = @"C:\Program Files (x86)\Steam\steamapps\workshop\content\1062090\3283831040\version-1.1\Scripts";

        static int Main(string[] args)
        {
            int managedAt = Array.IndexOf(args, "--managed");
            if (managedAt >= 0 && managedAt + 1 < args.Length) managed = args[managedAt + 1];
            AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
            {
                foreach (string directory in new[] { managed, modSettingsDirectory })
                {
                    string path = Path.Combine(directory, new AssemblyName(e.Name).Name + ".dll");
                    if (File.Exists(path)) return Assembly.LoadFrom(path);
                }
                return null;
            };
            return Run(args);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static int Run(string[] args)
        {
            if (args.Contains("--print-columns"))
            {
                Console.WriteLine("frames.csv: " + Columns.HeaderLine());
                Console.WriteLine("profile.csv: " + Profile.Table.HeaderLine());
                Console.WriteLine("spikes.csv: " + Profile.SpikeTable.HeaderLine());
                return 0;
            }
            int sampleAt = Array.IndexOf(args, "--write-sample");
            if (sampleAt >= 0 && sampleAt + 1 < args.Length)
            {
                // --write-sample DIR [--without-mod]: the second form is the "after" side of a comparison.
                SampleSession.Write(args[sampleAt + 1], withMod: !args.Contains("--without-mod"), seed: args.Contains("--without-mod") ? 11 : 7);
                Console.WriteLine("Wrote an example session (made up, not from a game) to " + args[sampleAt + 1]);
                return 0;
            }

            var tests = new List<(string Name, Action Run)>();
            tests.AddRange(CoreTests.All());
            tests.AddRange(ProfileTests.All());
            tests.AddRange(WatchSamplingTests.All());
            tests.AddRange(WriterTests.All());
            tests.AddRange(SummaryTests.All());
            tests.AddRange(GameBindingTests.All(managed));

            int failures = 0;
            foreach (var test in tests)
            {
                try
                {
                    Task task = Task.Run(test.Run);
                    if (!task.Wait(20000)) throw new TimeoutException("The check took longer than 20 seconds");
                    Console.WriteLine("PASS " + test.Name);
                }
                catch (Exception e)
                {
                    failures++;
                    Console.WriteLine("FAIL " + test.Name + ": " + e.GetBaseException().Message);
                }
            }
            Console.WriteLine((tests.Count - failures) + "/" + tests.Count + " passed");
            return failures == 0 ? 0 : 1;
        }
    }

    internal static class Assert
    {
        public static void Check(bool value, string message = "assertion failed")
        {
            if (!value) throw new Exception(message);
        }

        public static void Equal<T>(T expected, T actual, string what = "")
        {
            Check(EqualityComparer<T>.Default.Equals(expected, actual), (what == "" ? "" : what + ": ") + "expected " + expected + ", got " + actual);
        }

        public static void Near(double expected, double actual, double tolerance = .01, string what = "")
        {
            Check(Math.Abs(expected - actual) <= tolerance, (what == "" ? "" : what + ": ") + "expected about " + expected + ", got " + actual);
        }
    }
}
