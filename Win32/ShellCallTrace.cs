using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Bemo.Win32
{
    // Fixed numeric counters only. No window identity or property value is retained.
    public static class ShellCallTrace
    {
#if DEBUG
        // IDs are local sequence numbers, never HWNDs, titles or paths.
        private sealed class GuardCounts
        {
            public int Id;
            public long Show, Hide;
            public bool Retired;
            public long RetiredAt;
        }
        private static readonly System.Collections.Generic.Dictionary<IntPtr, GuardCounts> Guards =
            new System.Collections.Generic.Dictionary<IntPtr, GuardCounts>();
        private static readonly Dictionary<int, GuardCounts> GuardTotals = new Dictionary<int, GuardCounts>();
        private static int nextGuard;
        public static void GuardRegister(IntPtr hwnd)
        {
            lock (Gate)
            {
                GuardCounts old;
                if (Guards.TryGetValue(hwnd, out old)) { old.Retired = true; old.RetiredAt = Stopwatch.GetTimestamp(); }
                var counts = new GuardCounts { Id = ++nextGuard };
                Guards[hwnd] = counts;
                GuardTotals[counts.Id] = counts;
            }
        }
        public static void GuardUnregister(IntPtr hwnd)
        {
            lock (Gate) { GuardCounts counts; if (Guards.TryGetValue(hwnd, out counts)) { counts.Retired = true; counts.RetiredAt = Stopwatch.GetTimestamp(); } }
        }
        public static void GuardVisibility(IntPtr hwnd, bool before, bool after)
        {
            if (before == after) return;
            lock (Gate)
            {
                GuardCounts counts;
                if (!Guards.TryGetValue(hwnd, out counts)) return;
                if (after) counts.Show++; else counts.Hide++;
            }
        }
        private static readonly string[] Names = { "desktopId", "onCurrent", "propertyStore", "idRead", "idWrite", "taskbarUia", "captionUia", "foreignMessage", "foreignPosition", "explorerMessage", "explorerPosition", "pin", "rebuild", "ownPosition", "explorerCaptionUia" };
        private static readonly long[] Counts = new long[80];
        private static readonly long[] Completed = new long[15];
        private static readonly long[] Ticks = new long[15];
        private sealed class Sample
        {
            public long At;
            public long[] Counts, Completed, Ticks;
            public Dictionary<int, GuardCounts> Guards = new Dictionary<int, GuardCounts>();
        }
        private static readonly object Gate = new object();
        private static readonly System.Collections.Generic.Queue<Sample> History = new System.Collections.Generic.Queue<Sample>();
        private static readonly long Started = Stopwatch.GetTimestamp();
        private static long lastPeriodic = Started, lastStall;
        private static readonly StringBuilder RetryResults = new StringBuilder();
        private static int retryOverflow;
        public static void Retry(string verdict, int attempt, int afterMs)
        {
            lock (Gate)
            {
                if (RetryResults.Length > 4096) { retryOverflow++; return; }
                RetryResults.Append(" action=").Append(verdict == "merged" ? "retry-recovered" : "retry-failed")
                    .Append(" attempt=").Append(attempt).Append(" after-ms=").Append(afterMs).Append(" verify=").Append(verdict);
            }
        }
        private static int explorerPid;
        private static readonly Timer Timer = new Timer(Tick, null, 1000, 1000);
        public static int ExplorerPid { get { return Interlocked.CompareExchange(ref explorerPid, 0, 0); } }
        private static long[] Copy(long[] values)
        {
            var result = new long[values.Length];
            for (int i = 0; i < values.Length; i++) result[i] = Interlocked.Read(ref values[i]);
            return result;
        }
        // Snapshots and queue ownership share Gate; no native discovery or I/O does.
        private sealed class Output
        {
            public string Reason, Retries;
            public int Overflow;
            public Sample Now, Before;
            public bool RefreshExplorer;
        }
        private static readonly Queue<Output> Pending = new Queue<Output>();
        private static bool writing;
        private const int QueueLimit = 4;
        private static Sample Read()
        {
            var sample = new Sample { At = Stopwatch.GetTimestamp(), Counts = Copy(Counts), Completed = Copy(Completed), Ticks = Copy(Ticks) };
            foreach (var pair in GuardTotals)
                sample.Guards.Add(pair.Key, new GuardCounts { Id = pair.Key, Show = pair.Value.Show, Hide = pair.Value.Hide });
            return sample;
        }
        private static Output Capture(string reason, Sample now)
        {
            Sample before = null;
            foreach (var sample in History)
                if (sample.At <= now.At - 60L * Stopwatch.Frequency) before = sample;
            if (before == null && now.At - Started > 61L * Stopwatch.Frequency && History.Count > 0) before = History.Peek();
            if (before == null) before = new Sample { At = Started, Counts = new long[80], Completed = new long[15], Ticks = new long[15] };
            var output = new Output { Reason = reason, Now = now, Before = before,
                Retries = RetryResults.ToString(), Overflow = retryOverflow };
            if (reason == "periodic") { RetryResults.Length = 0; retryOverflow = 0; }
            return output;
        }
        private static void Enqueue(Output output)
        {
            // A stuck writer retains at most one active and four pending snapshots.
            if (Pending.Count == QueueLimit) Pending.Dequeue();
            Pending.Enqueue(output);
            if (writing) return;
            writing = true;
            ThreadPool.QueueUserWorkItem(Drain);
        }
        private static void Drain(object state)
        {
            while (true)
            {
                Output output;
                lock (Gate)
                {
                    if (Pending.Count == 0) { writing = false; return; }
                    output = Pending.Dequeue();
                }
                try
                {
                    if (output.RefreshExplorer)
                    {
                        using (var current = Process.GetCurrentProcess())
                        foreach (var process in Process.GetProcessesByName("explorer"))
                            using (process)
                                if (process.SessionId == current.SessionId) Interlocked.Exchange(ref explorerPid, process.Id);
                    }
                }
                catch { }
                try { if (output.Now != null) Write(output); }
                catch { }
            }
        }
        private static void Tick(object state)
        {
            try
            {
                lock (Gate)
                {
                    var now = Read();
                    History.Enqueue(now);
                    while (History.Count > 62) History.Dequeue();
                    if (now.At - lastPeriodic >= 60L * Stopwatch.Frequency)
                    {
                        var output = Capture("periodic", now);
                        output.RefreshExplorer = true;
                        Enqueue(output);
                        lastPeriodic = now.At;
                    }
                    else if (explorerPid == 0 && !writing)
                        Enqueue(new Output { RefreshExplorer = true });
                    // Retired totals survive the complete look-back interval, including reuse.
                    var retired = new List<int>();
                    foreach (var pair in GuardTotals)
                        if (pair.Value.Retired && now.At - pair.Value.RetiredAt > 62L * Stopwatch.Frequency) retired.Add(pair.Key);
                    foreach (var id in retired) GuardTotals.Remove(id);
                    var handles = new List<IntPtr>();
                    foreach (var pair in Guards)
                        if (!GuardTotals.ContainsKey(pair.Value.Id)) handles.Add(pair.Key);
                    foreach (var hwnd in handles) Guards.Remove(hwnd);
                }
            }
            catch { }
        }
        private static string Format(Output output)
        {
            var now = output.Now;
            var before = output.Before;
            var line = new StringBuilder(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            line.Append(" reason=").Append(output.Reason).Append(" window-ms=").Append((now.At - before.At) * 1000 / Stopwatch.Frequency);
            for (int i = 0; i < Names.Length; i++)
                line.Append(' ').Append(Names[i]).Append('=').Append(now.Counts[i] - before.Counts[i])
                    .Append('/').Append(now.Completed[i] - before.Completed[i]).Append('/').Append(((now.Ticks[i] - before.Ticks[i]) * 1000.0 / Stopwatch.Frequency).ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
            line.Append(" events=[");
            for (int i = 0; i < 65; i++)
            {
                long n = now.Counts[15 + i] - before.Counts[15 + i];
                if (n != 0) line.Append(i < 32 ? i : i < 64 ? 0x8000 + i - 32 : -1).Append(':').Append(n).Append(',');
            }
            line.Append("]").Append(output.Retries).Append(" retry-overflow=").Append(output.Overflow);
            foreach (var pair in now.Guards)
            {
                GuardCounts previous;
                before.Guards.TryGetValue(pair.Key, out previous);
                line.Append(" guardId=").Append(pair.Key)
                    .Append(" guardShow=").Append(pair.Value.Show - (previous == null ? 0 : previous.Show))
                    .Append(" guardHide=").Append(pair.Value.Hide - (previous == null ? 0 : previous.Hide));
            }
            return line.ToString();
        }
        private static void Write(Output output)
        {
            string line = Format(output);
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowTabs");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "shell_calls.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1048576) { File.Copy(path, path + ".old", true); File.Delete(path); }
            File.AppendAllText(path, line + "\r\n");
        }
        public static void Stall()
        {
            try
            {
                lock (Gate)
                {
                    var now = Read();
                    if (lastStall != 0 && now.At - lastStall < 60L * Stopwatch.Frequency) return;
                    Enqueue(Capture("stall", now));
                    lastStall = now.At;
                }
            }
            catch { }
        }
        public static void Event(int value)
        {
            int index = value >= 0 && value < 32 ? value : value >= 0x8000 && value < 0x8020 ? value - 0x8000 + 32 : 64;
            Interlocked.Increment(ref Counts[15 + index]);
        }
        public static long Begin(int kind) { Interlocked.Increment(ref Counts[kind]); return Stopwatch.GetTimestamp(); }
        public static void End(int kind, long start)
        {
            Interlocked.Increment(ref Completed[kind]);
            Interlocked.Add(ref Ticks[kind], Stopwatch.GetTimestamp() - start);
        }
        private sealed class Scope : IDisposable
        {
            private readonly int kind;
            private readonly long start;
            public Scope(int kind) { this.kind = kind; start = Begin(kind); }
            public void Dispose() { End(kind, start); }
        }
        public static IDisposable Measure(int kind) { return new Scope(kind); }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        private sealed class CaptionScope : IDisposable
        {
            private readonly long start = Begin(6);
            private readonly long explorerStart;
            public CaptionScope(IntPtr hwnd)
            {
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid != 0 && pid == (uint)ExplorerPid) explorerStart = Begin(14);
            }
            public void Dispose() { End(6, start); if (explorerStart != 0) End(14, explorerStart); }
        }
        public static IDisposable Caption(IntPtr hwnd) { return new CaptionScope(hwnd); }

#else
        public static void Stall() { }
#endif
    }
}
