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
        /// <summary>More lines than this waiting means the file is not being written; the rest are dropped so the queue cannot grow without bound.</summary>
        const int MaxQueued = 20000;

        internal readonly ConcurrentQueue<string> Queue = new ConcurrentQueue<string>();
        internal readonly string Path;
        internal readonly string Header;
        internal volatile bool Failed;
        internal int OpenFailures;

        internal TextChannel(string path, string header) { Path = path; Header = header; }

        public void Write(string line)
        {
            if (Failed || Queue.Count >= MaxQueued) return;
            Queue.Enqueue(line);
        }
    }

    /// <summary>
    /// Writes every output file of a session on one thread of its own, twice a second, so no disk work ever happens on the frame being
    /// measured. Table rows come from rings allocated once and are formatted into buffers allocated once. A file is opened only for the
    /// moment something is appended to it and shared with readers, so the folder can be copied or zipped while the game runs (a file held
    /// open for writing cannot be, and shows a size of 0 in a folder listing until it is closed). If a file cannot be opened for a while or
    /// cannot be written, that file is given up quietly and the game carries on; the reason is kept in <see cref="Failure"/>.
    /// </summary>
    public sealed class LogWriter
    {
        public const int DrainMilliseconds = 500;
        const int BatchRows = 128;
        const int LineChars = 4096;
        /// <summary>A file someone else holds open without sharing is retried at each drain for this many drains (a minute) before it is given up.</summary>
        const int MaxOpenFailures = 120;

        sealed class TableOutput
        {
            public string Path;
            public IReadOnlyList<string> Header;
            public Table Table;
            public Ring Ring;
            public TailWriter Tail;
            public Action<TextWriter> Trailer;
            public long DroppedReported;
            public bool Failed;
            public int OpenFailures;
        }

        sealed class FileOutput
        {
            public string Path;
            public volatile string Pending;
            public volatile Func<string> PendingProducer;
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
                if (f.Path == path) { f.PendingProducer = null; f.Pending = content; return; }
        }

        /// <summary>
        /// Like <see cref="SetFile(string,string)"/>, but the text is made on the writer thread, so building a big report costs the game thread
        /// nothing. The producer must only read data that is safe to read from another thread (a snapshot).
        /// </summary>
        public void SetFile(string path, Func<string> producer)
        {
            foreach (FileOutput f in files)
                if (f.Path == path) { f.Pending = null; f.PendingProducer = producer; return; }
        }

        /// <summary>Creates the files and starts writing. False, with <see cref="Failure"/> set, if none could be created.</summary>
        public bool Start()
        {
            int opened = 0;
            foreach (TableOutput t in tables)
            {
                try
                {
                    using (StreamWriter w = OpenWriter(t.Path, FileMode.Create))
                    {
                        foreach (string line in t.Header) w.WriteLine(line);
                        w.WriteLine(t.Table.HeaderLine());
                    }
                    opened++;
                }
                catch (Exception e) { Fail(t, e); }
            }
            foreach (TextChannel c in channels)
            {
                try
                {
                    using (StreamWriter w = OpenWriter(c.Path, FileMode.Create)) w.WriteLine(c.Header);
                    opened++;
                }
                catch (Exception e) { GiveUp(c, e); }
            }
            if (opened == 0) return false;
            thread = new Thread(Run) { IsBackground = true, Name = "PerformanceLog writer" };
            thread.Start();
            return true;
        }

        /// <summary>Opens a file for writing, letting other programs read, write and delete it meanwhile.</summary>
        static StreamWriter OpenWriter(string path, FileMode mode)
        {
            var stream = new FileStream(path, mode, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096);
            return new StreamWriter(stream, new UTF8Encoding(false), 4096) { NewLine = "\n", AutoFlush = false };
        }

        void Fail(TableOutput t, Exception e)
        {
            t.Failed = true;
            Failure = Failure ?? (Path.GetFileName(t.Path) + ": " + e.Message);
        }

        void GiveUp(TextChannel c, Exception e)
        {
            c.Failed = true;
            Failure = Failure ?? (Path.GetFileName(c.Path) + ": " + e.Message);
            while (c.Queue.TryDequeue(out _)) { }
        }

        /// <summary>Writes what is left, closes the files and waits (briefly) for the writer thread to finish.</summary>
        public void Stop()
        {
            stopping = true;
            wake.Set();
            Thread t = thread;
            if (t != null && !t.Join(2000)) Failure = Failure ?? "The writer did not finish in time.";
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
                if (t.Failed) continue;
                try { DrainTable(t, batch, line, tail); }
                catch (Exception e)
                {
                    Fail(t, e);
                    try { report?.Invoke("The performance log gave up on " + Path.GetFileName(t.Path) + ": " + e.Message); } catch { }
                }
            }
            foreach (TextChannel c in channels)
            {
                if (c.Failed || c.Queue.IsEmpty) continue;
                StreamWriter w;
                try { w = OpenWriter(c.Path, FileMode.Append); }
                catch (Exception e)
                {
                    if (++c.OpenFailures >= MaxOpenFailures) GiveUp(c, e);
                    continue;
                }
                c.OpenFailures = 0;
                try
                {
                    using (w)
                        while (c.Queue.TryDequeue(out string text)) w.WriteLine(text);
                }
                catch (Exception e) { GiveUp(c, e); }
            }
            foreach (FileOutput f in files)
            {
                Func<string> producer = f.PendingProducer;
                if (producer != null)
                {
                    f.PendingProducer = null;
                    try { f.Pending = producer(); }
                    catch (Exception e) { Failure = Failure ?? (Path.GetFileName(f.Path) + ": " + e.Message); continue; }
                }
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

        void DrainTable(TableOutput t, double[] batch, char[] line, StringBuilder tail)
        {
            // Nothing to write: do not touch the file at all. If it cannot be opened the rows stay in the ring (which drops new ones, counted,
            // rather than growing) and the next drain tries again.
            if (t.Ring.Count == 0 && t.Ring.Dropped == t.DroppedReported) return;
            StreamWriter w;
            try { w = OpenWriter(t.Path, FileMode.Append); }
            catch (Exception e)
            {
                if (++t.OpenFailures >= MaxOpenFailures) throw new IOException("could not be opened for a minute: " + e.Message);
                return;
            }
            t.OpenFailures = 0;
            using (w)
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
                        w.Write(line, 0, written);
                        if (t.Tail != null)
                        {
                            tail.Clear();
                            t.Tail(row, tail);
                            w.Write(tail.ToString());
                        }
                        w.Write('\n');
                    }
                } while (n == BatchRows);
                long dropped = t.Ring.Dropped;
                if (dropped != t.DroppedReported)
                {
                    t.DroppedReported = dropped;
                    w.WriteLine("# rows dropped so far because the writer fell behind: " + dropped);
                }
            }
        }

        void CloseAll()
        {
            foreach (TableOutput t in tables)
            {
                if (t.Failed) continue;
                // The end of the file: what only the end knows, then a line that says the file is complete. A reader may hold the file for a
                // moment, so this tries a few times.
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    try
                    {
                        using (StreamWriter w = OpenWriter(t.Path, FileMode.Append))
                        {
                            t.Trailer?.Invoke(w);
                            w.WriteLine("# end");
                        }
                        break;
                    }
                    catch (Exception e)
                    {
                        if (attempt == 9) Failure = Failure ?? (Path.GetFileName(t.Path) + ": " + e.Message);
                        else Thread.Sleep(50);
                    }
                }
            }
        }
    }
}
