using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using static PerformanceLog.Tests.Assert;

namespace PerformanceLog.Tests
{
    internal static class WriterTests
    {
        public static IEnumerable<(string, Action)> All()
        {
            yield return ("Writer: header, column names, rows and the closing line end up in the file", WritesTable);
            yield return ("Writer: text after the numbers, an events file and a file rewritten whole", WritesTailsAndFiles);
            yield return ("Writer: a file that cannot be opened is given up and the others carry on", UnopenablePath);
            yield return ("Writer: rows that were dropped are reported in the file", ReportsDrops);
            yield return ("Writer: rows pushed while it stops are still written", FlushesOnStop);
            yield return ("Writer + probe: a scripted session produces files a reader can parse", EndToEnd);
            yield return ("Fixtures: the checked-in sample sessions have the columns and text the mod writes now", FixturesAreCurrent);
        }

        static string TempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "perflog-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        static void WritesTable()
        {
            string dir = TempDir();
            try
            {
                var ring = new Ring(Columns.Count, 16);
                var writer = new LogWriter(null);
                writer.AddTable(Path.Combine(dir, "frames.csv"), new[] { "# header line", "# key: value" }, Columns.Main, ring, null, w => w.WriteLine("# trailer"));
                Check(writer.Start(), "the writer starts: " + writer.Failure);
                var row = new double[Columns.Count];
                row[Columns.Type] = 'F'; row[Columns.Frame] = 7; row[Columns.FrameMs] = 61.25;
                ring.TryPush(row);
                writer.Stop();
                Check(writer.Failure == null, "no failure: " + writer.Failure);
                string[] lines = File.ReadAllLines(Path.Combine(dir, "frames.csv"));
                Equal("# header line", lines[0]);
                Equal("# key: value", lines[1]);
                Equal(Columns.HeaderLine(), lines[2]);
                string[] fields = lines[3].Split(',');
                Equal(Columns.Count, fields.Length);
                Equal("F", fields[Columns.Type]); Equal("7", fields[Columns.Frame]); Equal("61.25", fields[Columns.FrameMs]);
                Equal("# trailer", lines[4]);
                Equal("# end", lines[5]);
                Equal(6, lines.Length);
            }
            finally { Directory.Delete(dir, true); }
        }

        static void WritesTailsAndFiles()
        {
            string dir = TempDir();
            try
            {
                Profile.Reset();
                Profile.ModResolver = null;
                int id = Profile.IdFor(ProfileKind.UpdateSingleton, typeof(WriterTests));
                var ring = new Ring(Profile.Table.Count, 16);
                var writer = new LogWriter(null);
                writer.AddTable(Path.Combine(dir, "profile.csv"), new string[0], Profile.Table, ring, Profile.AppendProfileText);
                TextChannel events = writer.AddText(Path.Combine(dir, "events.csv"), "utcMs,tick,kind,ms,detail");
                writer.AddFile(Path.Combine(dir, "summary.md"));
                Check(writer.Start());
                var row = new double[Profile.Table.Count];
                row[0] = (int)ProfileKind.UpdateSingleton; row[1] = 1; row[3] = id; row[4] = 10; row[5] = 10; row[6] = 1.5;
                ring.TryPush(row);
                events.Write("1,2,save,350.0,\"to autosave\"");
                writer.SetFile(Path.Combine(dir, "summary.md"), "# first");
                writer.SetFile(Path.Combine(dir, "summary.md"), "# second");
                writer.Stop();
                string[] profile = File.ReadAllLines(Path.Combine(dir, "profile.csv"));
                Equal(string.Join(",", Profile.Table.Names), profile[0]);
                Equal("update-singleton,1,0,0,10,10,1.50,0.0,0.00,PerformanceLog.Tests.WriterTests,PerformanceLog.Tests,", profile[1]);
                Equal(0, id, "the first key of a fresh registry");
                string[] ev = File.ReadAllLines(Path.Combine(dir, "events.csv"));
                Equal("utcMs,tick,kind,ms,detail", ev[0]);
                Equal("1,2,save,350.0,\"to autosave\"", ev[1]);
                Equal("# second", File.ReadAllText(Path.Combine(dir, "summary.md")));
                Check(!File.Exists(Path.Combine(dir, "summary.md.tmp")), "no temporary file is left");
            }
            finally { Directory.Delete(dir, true); }
        }

        static void UnopenablePath()
        {
            string dir = TempDir();
            try
            {
                var good = new Ring(Columns.Count, 4);
                var bad = new Ring(Columns.Count, 4);
                var writer = new LogWriter(null);
                writer.AddTable(Path.Combine(dir, "missing-folder", "x.csv"), new string[0], Columns.Main, bad);
                writer.AddTable(Path.Combine(dir, "good.csv"), new string[0], Columns.Main, good);
                Check(writer.Start(), "one file opened, so the writer runs");
                Check(writer.Failure != null && writer.Failure.Contains("x.csv"), "and it says which failed: " + writer.Failure);
                good.TryPush(new double[Columns.Count]);
                writer.Stop();
                Check(File.ReadAllLines(Path.Combine(dir, "good.csv")).Length >= 3);

                var none = new LogWriter(null);
                none.AddTable(Path.Combine(dir, "nope", "y.csv"), new string[0], Columns.Main, new Ring(Columns.Count, 1));
                Check(!none.Start(), "no file opened: not started");
            }
            finally { Directory.Delete(dir, true); }
        }

        static void ReportsDrops()
        {
            string dir = TempDir();
            try
            {
                var ring = new Ring(Columns.Count, 1);
                var writer = new LogWriter(null);
                writer.AddTable(Path.Combine(dir, "f.csv"), new string[0], Columns.Main, ring);
                Check(writer.Start());
                ring.TryPush(new double[Columns.Count]);
                ring.TryPush(new double[Columns.Count]);
                ring.TryPush(new double[Columns.Count]);
                writer.Stop();
                Check(File.ReadAllText(Path.Combine(dir, "f.csv")).Contains("# rows dropped so far because the writer fell behind: 2"));
            }
            finally { Directory.Delete(dir, true); }
        }

        static void FlushesOnStop()
        {
            string dir = TempDir();
            try
            {
                var ring = new Ring(Columns.Count, 400);
                var writer = new LogWriter(null);
                writer.AddTable(Path.Combine(dir, "f.csv"), new string[0], Columns.Main, ring);
                Check(writer.Start());
                var row = new double[Columns.Count];
                for (int i = 0; i < 300; i++) { row[Columns.Frame] = i; ring.TryPush(row); }
                writer.Stop();
                string[] lines = File.ReadAllLines(Path.Combine(dir, "f.csv"));
                Equal(300, lines.Count(l => l.Length > 0 && !l.StartsWith("#") && !l.StartsWith("type")));
            }
            finally { Directory.Delete(dir, true); }
        }

        static string RepoRoot()
        {
            string directory = AppContext.BaseDirectory;
            while (directory != null && !File.Exists(Path.Combine(directory, "docs", "SESSION-README.md"))) directory = Path.GetDirectoryName(directory);
            if (directory == null) throw new FileNotFoundException("the repository root was not found above " + AppContext.BaseDirectory);
            return directory;
        }

        static string HeaderOf(string path, string firstColumn) => File.ReadLines(path).First(l => l.StartsWith(firstColumn + ","));

        // tools/perflog.py and its tests read tests/fixtures. If a column is added or renamed and the fixtures are not regenerated, the Python
        // tests would go on passing against the old format; this catches that. Regenerate with:
        //   dotnet run --project tests -- --write-sample tests/fixtures/sample-with-mod
        //   dotnet run --project tests -- --write-sample tests/fixtures/sample-without-mod --without-mod
        static void FixturesAreCurrent()
        {
            string root = RepoRoot();
            string fresh = TempDir();
            try
            {
                SampleSession.Write(fresh);
                foreach (string name in new[] { "sample-with-mod", "sample-without-mod" })
                {
                    string fixture = Path.Combine(root, "tests", "fixtures", name);
                    Check(Directory.Exists(fixture), name + " is missing: regenerate the fixtures");
                    Equal(HeaderOf(Path.Combine(fresh, "frames.csv"), "type"), HeaderOf(Path.Combine(fixture, "frames.csv"), "type"), name + " frames.csv columns");
                    Equal(HeaderOf(Path.Combine(fresh, "profile.csv"), "kind"), HeaderOf(Path.Combine(fixture, "profile.csv"), "kind"), name + " profile.csv columns");
                    Equal(HeaderOf(Path.Combine(fresh, "spikes.csv"), "frame"), HeaderOf(Path.Combine(fixture, "spikes.csv"), "frame"), name + " spikes.csv columns");
                    Equal(File.ReadAllText(Path.Combine(fresh, "columns.md")), File.ReadAllText(Path.Combine(fixture, "columns.md")), name + " columns.md");
                    Equal(File.ReadAllText(Path.Combine(fresh, "README.md")), File.ReadAllText(Path.Combine(fixture, "README.md")), name + " README.md");
                }
            }
            finally { try { Directory.Delete(fresh, true); } catch { } }
        }

        static void EndToEnd()
        {
            string dir = TempDir();
            try
            {
                SampleSession.Write(dir);
                foreach (string file in new[] { "frames.csv", "profile.csv", "spikes.csv", "events.csv", "summary.md", "README.md", "columns.md" })
                    Check(File.Exists(Path.Combine(dir, file)), file + " was written");
                string[] frames = File.ReadAllLines(Path.Combine(dir, "frames.csv"));
                string header = frames.Single(l => l.StartsWith("type,"));
                Equal(Columns.HeaderLine(), header);
                int columns = header.Split(',').Length;
                var data = frames.Where(l => l.Length > 0 && !l.StartsWith("#") && !l.StartsWith("type,")).ToList();
                Check(data.Count > 10, "there are rows");
                foreach (string line in data) Equal(columns, line.Split(',').Length, "every row has every column: " + line.Substring(0, Math.Min(40, line.Length)));
                Check(data.Any(l => l.StartsWith("F,")), "slow frames");
                Check(data.Any(l => l.StartsWith("S,")), "summaries");
                Check(frames.Any(l => l.StartsWith("# mod|")), "the mod list is in the header");
                Check(frames.Last() == "# end", "a closing line");
                string[] profile = File.ReadAllLines(Path.Combine(dir, "profile.csv"));
                Check(profile.Any(l => l.Contains("update-singleton,")), "profile rows");
                string[] spikes = File.ReadAllLines(Path.Combine(dir, "spikes.csv"));
                Check(spikes.Length > 2, "spike rows");
                string summary = File.ReadAllText(Path.Combine(dir, "summary.md"));
                Check(summary.Contains("## Where an average frame goes") && summary.Contains("## The slowest frames"), "the summary has its sections");
                Check(summary.Contains("**finished**"), "and says it finished");
                string readme = File.ReadAllText(Path.Combine(dir, "README.md"));
                Check(readme.Contains("## Diagnosing") && readme.Contains("columns.md"), "the readme has the guide and points to the columns");
                Check(File.ReadAllText(Path.Combine(dir, "columns.md")).Contains("`frameMs`"), "columns.md defines the columns");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }
}
