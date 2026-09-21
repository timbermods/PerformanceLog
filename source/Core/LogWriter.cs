using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace PerformanceLog
{
    /// <summary>A queue of text lines that the writer thread appends to a file. Callable from any thread.</summary>
    public sealed class TextChannel
    {
        internal readonly ConcurrentQueue<string> Queue = new ConcurrentQueue<string>();
        internal readonly string Path;
        internal readonly string Header;
        internal StreamWriter Stream;

        internal TextChannel(string path, string header) { Path = path; Header = header; }

        public void Write(string line) => Queue.Enqueue(line);
    }

    /// <summary>
    /// Writes every output file of a session on one thread of its own, twice a second, so no disk work ever happens on the frame being
    /// measured. Table rows come from rings allocated once and are formatted into buffers allocated once. If a file cannot be opened or
    /// written that file is given up quietly and the game carries on; the reason is kept in <see cref="Failure"/>.
    /// </summary>
    public sealed class LogWriter
    {
        public const int DrainMilliseconds = 500;
        const int BatchRows = 128;
        const int LineChars = 4096;

        sealed class TableOutput
        {
            public string Path;
            public IReadOnlyList<string> Header;
            public Table Table;
            public Ring Ring;
            public TailWriter Tail;
            public Action<TextWriter> Trailer;
            public StreamWriter Stream;
            public long DroppedReported;
            public bool Failed;
        }

        sealed class FileOutput
        {
            public string Path;
            public volatile string Pending;
            public string Written;
        }

        /// <summary>Adds the text columns of one row (each starting with a comma) after its numbers.</summary>
        public delegate void TailWriter(ReadOnlySpan<double> row, StringBuilder into);

        readonly List<TableOutput> tables = new List<TableOutput>();
        readonly List<TextChannel> channels = new List<TextChannel>();
        readonly List<FileOutput> files = new List<FileOutput>();
        readonly Action<string> report;
        readonly ManualResetEventSlim wake = new ManualResetEventSlim(false);
        Thread thread;
        volatile bool stopping;

        /// <summary>The first problem, if any output had to be given up.</summary>
        public string Failure { get; private set; }

        public LogWriter(Action<string> report) { this.report = report; }

        /// <summary>Adds a CSV file fed from <paramref name="ring"/>. Call before <see cref="Start"/>.</summary>
        /// <param name="header">Lines written before the column names, each starting with #. No newlines inside.</param>
        public void AddTable(string path, IReadOnlyList<string> header, Table table, Ring ring, TailWriter tail = null, Action<TextWriter> trailer = null)
        {
            tables.Add(new TableOutput { Path = path, Header = header, Table = table, Ring = ring, Tail = tail, Trailer = trailer });
        }

        /// <summary>Adds a text file whose lines are queued by <see cref="TextChannel.Write"/>. Call before <see cref="Start"/>.</summary>
        public TextChannel AddText(string path, string header)
        {
            var channel = new TextChannel(path, header);
            channels.Add(channel);
            return channel;
        }

        /// <summary>Adds a file that is rewritten whole, by the writer thread, whenever new content is set with <see cref="SetFile"/>.</summary>
        public void AddFile(string path) => files.Add(new FileOutput { Path = path });

        /// <summary>Sets what a file added with <see cref="AddFile"/> should contain. Callable from any thread.</summary>
        public void SetFile(string path, string content)
        {
            foreach (FileOutput f in files)
                if (f.Path == path) { f.Pending = content; return; }
        }

        /// <summary>Opens the files and starts writing. False, with <see cref="Failure"/> set, if none could be opened.</summary>
        public bool Start()
        {
            int opened = 0;
            foreach (TableOutput t in tables)
            {
                try
                {
                    t.Stream = Open(t.Path);
                    foreach (string line in t.Header) t.Stream.WriteLine(line);
                    t.Stream.WriteLine(t.Table.HeaderLine());
                    t.Stream.Flush();
                    opened++;
                }
                catch (Exception e) { Fail(t, e); }
            }
            foreach (TextChannel c in channels)
            {
                try
                {
                    c.Stream = Open(c.Path);
                    c.Stream.WriteLine(c.Header);
                    c.Stream.Flush();
                    opened++;
                }
                catch (Exception e) { Failure = Failure ?? (c.Path + ": " + e.Message); }
            }
            if (opened == 0) return false;
            thread = new Thread(Run) { IsBackground = true, Name = "PerformanceLog writer" };
            thread.Start();
            return true;
        }

        static StreamWriter Open(string path)
        {
            var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            return new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = false };
        }

        void Fail(TableOutput t, Exception e)
        {
            t.Failed = true;
            Failure = Failure ?? (Path.GetFileName(t.Path) + ": " + e.Message);
            try { t.Stream?.Dispose(); } catch { }
            t.Stream = null;
        }

        /// <summary>Writes what is left, closes the files and waits (briefly) for the writer thread to finish.</summary>
        public void Stop()
        {
            stopping = true;
            wake.Set();
            Thread t = thread;
            if (t != null && !t.Join(3000)) Failure = Failure ?? "The writer did not finish in time.";
            thread = null;
        }

        void Run()
        {
            var batch = new double[BatchRows * MaxColumns()];
            var line = new char[LineChars];
            var tail = new StringBuilder(256);
            try
            {
                while (!stopping)
                {
                    wake.Wait(DrainMilliseconds);
                    DrainAll(batch, line, tail);
                }
                DrainAll(batch, line, tail);
            }
            catch (Exception e)
            {
                Failure = Failure ?? e.Message;
                try { report?.Invoke("The performance log writer stopped: " + e.Message); } catch { }
            }
            finally { CloseAll(); }
        }

        int MaxColumns()
        {
            int max = 1;
            foreach (TableOutput t in tables) max = Math.Max(max, t.Table.Count);
            return max;
        }

        void DrainAll(double[] batch, char[] line, StringBuilder tail)
        {
            foreach (TableOutput t in tables)
            {
                if (t.Failed || t.Stream == null) continue;
                try { DrainTable(t, batch, line, tail); }
                catch (Exception e)
                {
                    Fail(t, e);
                    try { report?.Invoke("The performance log gave up on " + Path.GetFileName(t.Path) + ": " + e.Message); } catch { }
                }
            }
            foreach (TextChannel c in channels)
            {
                if (c.Stream == null) continue;
                try
                {
                    while (c.Queue.TryDequeue(out string text)) c.Stream.WriteLine(text);
                    c.Stream.Flush();
                }
                catch (Exception e)
                {
                    Failure = Failure ?? (Path.GetFileName(c.Path) + ": " + e.Message);
                    try { c.Stream.Dispose(); } catch { }
                    c.Stream = null;
                }
            }
            foreach (FileOutput f in files)
            {
                string content = f.Pending;
                if (content == null || ReferenceEquals(content, f.Written)) continue;
                try
                {
                    string temp = f.Path + ".tmp";
                    File.WriteAllText(temp, content, new UTF8Encoding(false));
                    if (File.Exists(f.Path)) File.Delete(f.Path);
                    File.Move(temp, f.Path);
                    f.Written = content;
                }
                catch (Exception e) { Failure = Failure ?? (Path.GetFileName(f.Path) + ": " + e.Message); f.Written = content; }
            }
        }

        static void DrainTable(TableOutput t, double[] batch, char[] line, StringBuilder tail)
        {
            int columns = t.Table.Count;
            int n;
            do
            {
                n = t.Ring.Drain(batch, BatchRows);
                for (int i = 0; i < n; i++)
                {
                    var row = new ReadOnlySpan<double>(batch, i * columns, columns);
                    if (!t.Table.TryFormatRow(row, line, out int written)) continue;
                    t.Stream.Write(line, 0, written);
                    if (t.Tail != null)
                    {
                        tail.Clear();
                        t.Tail(row, tail);
                        t.Stream.Write(tail.ToString());
                    }
                    t.Stream.Write('\n');
                }
            } while (n == BatchRows);
            long dropped = t.Ring.Dropped;
            if (dropped != t.DroppedReported)
            {
                t.DroppedReported = dropped;
                t.Stream.WriteLine("# rows dropped so far because the writer fell behind: " + dropped);
            }
            t.Stream.Flush();
        }

        void CloseAll()
        {
            foreach (TableOutput t in tables)
            {
                try
                {
                    if (t.Stream != null && !t.Failed)
                    {
                        t.Trailer?.Invoke(t.Stream);
                        t.Stream.WriteLine("# end");
                        t.Stream.Flush();
                    }
                }
                catch (Exception e) { Failure = Failure ?? e.Message; }
                try { t.Stream?.Dispose(); } catch { }
                t.Stream = null;
            }
            foreach (TextChannel c in channels)
            {
                try { c.Stream?.Dispose(); } catch { }
                c.Stream = null;
            }
        }
    }
}
