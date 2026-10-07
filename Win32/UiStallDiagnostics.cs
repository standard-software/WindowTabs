// Diagnostic workers must never block the watchdog/restart path.
#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Bemo.Win32
{
    // Thread-local diagnostics only: no cross-group reads and no extra worker.
    public static class GroupCallTrace
    {
        public delegate void Incident(string stage, double ms, long ended, IntPtr target, IntPtr strip);
        public static Incident Sink;
        [ThreadStatic] private static IntPtr strip;
        [ThreadStatic] private static string stage;
        [ThreadStatic] private static string recent;
        [ThreadStatic] private static string recentApi;
        [ThreadStatic] private static long beat;
        [ThreadStatic] private static double recentMs;
        [ThreadStatic] private static Dictionary<IntPtr, List<IntPtr>> batches;
        private static readonly uint processId = GetCurrentProcessId();
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("kernel32.dll", EntryPoint = "SetLastError", SetLastError = true)]
        private static extern void RestoreError(uint error);

        public static void Register(IntPtr hwnd)
        {
            strip = hwnd;
            stage = "group.loop";
            recent = "none";
            recentApi = "none";
            recentMs = 0;
            beat = Stopwatch.GetTimestamp();
        }
        public static void Unregister()
        {
            strip = IntPtr.Zero;
            stage = null;
            recent = null;
            recentApi = null;
            batches = null;
        }
        public static string Enter(string name)
        {
            string previous = stage;
            if (strip != IntPtr.Zero) stage = previous + "/" + name;
            return previous;
        }
        public static void Leave(string previous) { stage = previous; }
        public static void NoteStage(double ms)
        {
            if (strip != IntPtr.Zero && ms >= 300.0 && ms > recentMs)
            {
                recentMs = ms;
                recent = stage;
            }
        }
        private sealed class OperationScope : IDisposable
        {
            private readonly string previous;
            private readonly long start;
            public OperationScope(string name) { previous = Enter(name); start = Stopwatch.GetTimestamp(); }
            public void Dispose()
            {
                NoteStage((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
                Leave(previous);
            }
        }
        public static IDisposable Operation(string name) { return new OperationScope(name); }

        public struct Call
        {
            internal long start;
            internal string stage;
            internal IntPtr target;
            internal IntPtr strip;
            internal List<IntPtr> batchTargets;
        }
        public static Call Begin(string api, IntPtr target)
        {
            Call call = new Call();
            if (strip == IntPtr.Zero || target == IntPtr.Zero) return call;
            uint error = (uint)Marshal.GetLastWin32Error();
            try
            {
                uint pid;
                GetWindowThreadProcessId(target, out pid);
                if (pid != 0 && pid != processId)
                {
                    call.target = target;
                    call.strip = strip;
                    call.stage = stage + "/" + api;
                    call.start = Stopwatch.GetTimestamp();
                }
            }
            finally { RestoreError(error); }
            return call;
        }
        public static void End(Call call)
        {
            if (call.start == 0) return;
            uint error = (uint)Marshal.GetLastWin32Error();
            long ended = Stopwatch.GetTimestamp();
            try
            {
                double ms = (ended - call.start) * 1000.0 / Stopwatch.Frequency;
                if (ms >= 300.0)
                {
                    string name = call.stage;
                    if (call.batchTargets != null)
                    {
                        StringBuilder targets = new StringBuilder();
                        foreach (IntPtr hwnd in call.batchTargets)
                        {
                            if (targets.Length != 0) targets.Append(',');
                            targets.Append("0x").Append(hwnd.ToInt64().ToString("X"));
                        }
                        name += " batchTargets=" + targets.ToString();
                    }
                    recentApi = call.stage;
                    Emit(name, ms, ended, call.target, call.strip);
                }
            }
            catch { }
            finally { RestoreError(error); }
        }
        private static void Emit(string name, double ms, long ended, IntPtr target, IntPtr groupStrip)
        {
            Incident sink = Sink;
            if (sink != null) sink(name, ms, ended, target, groupStrip);
        }
        public static void Beat()
        {
            if (strip == IntPtr.Zero) return;
            long now = Stopwatch.GetTimestamp();
            double ms = (now - beat) * 1000.0 / Stopwatch.Frequency;
            beat = now;
            try
            {
                // A timer gap is observed on recovery, not proof of a native wait.
                if (ms >= 1000.0) Emit("group.loop-gap/current=" + stage + "/recent=" + recent + "/recentApi=" + recentApi, ms, now, IntPtr.Zero, strip);
            }
            catch { }
            recent = "none";
            recentApi = "none";
            recentMs = 0;
        }
        public static void TrackBatch(IntPtr oldBatch, IntPtr newBatch, Call call)
        {
            if (strip == IntPtr.Zero) return;
            List<IntPtr> targets = null;
            if (batches != null)
            {
                batches.TryGetValue(oldBatch, out targets);
                batches.Remove(oldBatch);
            }
            if (newBatch == IntPtr.Zero) return;
            if (call.target != IntPtr.Zero)
            {
                if (targets == null) targets = new List<IntPtr>();
                if (!targets.Contains(call.target)) targets.Add(call.target);
            }
            if (targets != null)
            {
                if (batches == null) batches = new Dictionary<IntPtr, List<IntPtr>>();
                batches[newBatch] = targets;
            }
        }
        public static Call BeginBatch(IntPtr batch)
        {
            List<IntPtr> targets;
            if (batches == null || !batches.TryGetValue(batch, out targets)) return new Call();
            batches.Remove(batch);
            // Keep the pre-call targets even if a member has since disappeared.
            // hwnd is the first foreign member; batchTargets lists all of them.
            Call call = new Call();
            call.target = targets[0];
            call.strip = strip;
            call.batchTargets = targets;
            call.stage = stage + "/EndDeferWindowPos[batch]";
            call.start = Stopwatch.GetTimestamp();
            return call;
        }
    }

    public static class UiStallDiagnostics
    {
        public delegate void LogLine(string line);
        private delegate bool EnumWindow(IntPtr hwnd, IntPtr state);
        private static int probing, dumping;
        private static long lastDump;
        private static readonly object dumpGate = new object();
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private const int KeepDumps = 3;

        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowsHookEx(int hook, HookProc callback, IntPtr module, uint tid);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)]
        private struct CallMessage { public IntPtr lParam, wParam; public uint message; public IntPtr hwnd; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ReturnMessage { public IntPtr result, lParam, wParam; public uint message; public IntPtr hwnd; }
        private static HookProc entering, leaving, getting;
        private static long enterCount, leaveCount, getCount, lastEnter, lastLeave, lastGet;
        private static int enterMessage, leaveMessage, getMessage;
        [StructLayout(LayoutKind.Sequential)]
        private struct QueueMessage { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; }
        private static long Age(ref long stamp)
        {
            long last = Interlocked.Read(ref stamp);
            return (Stopwatch.GetTimestamp() - last) * 1000 / Stopwatch.Frequency;
        }
        public static string PumpSnapshot(uint tid)
        {
            long cpu = -1;
            using (Process process = Process.GetCurrentProcess())
                foreach (ProcessThread thread in process.Threads)
                    if (thread.Id == tid) cpu = (long)thread.TotalProcessorTime.TotalMilliseconds;
            return String.Format("[NativePump] enter={0}/{1:X}/{2} leave={3}/{4:X}/{5} get={6}/{7:X}/{8} cpu-ms={9}",
                Interlocked.Read(ref enterCount), enterMessage, Age(ref lastEnter),
                Interlocked.Read(ref leaveCount), leaveMessage, Age(ref lastLeave),
                Interlocked.Read(ref getCount), getMessage, Age(ref lastGet), cpu);
        }
        private static bool DisplayMessage(uint message)
        {
            return message == 1 || message == 0x1a || message == 0x7e || message == 0x2e0 || message == 0x2e2 || message == 0x2e3;
        }
        // Observe native dispatch too: a managed stack can end at Application.Run.
        public static void InstallDisplayProbe(LogLine log)
        {
            entering = delegate(int code, IntPtr w, IntPtr l) {
                if (code >= 0) {
                    CallMessage m = (CallMessage)Marshal.PtrToStructure(l, typeof(CallMessage));
                    enterMessage = (int)m.message;
                    Interlocked.Increment(ref enterCount); Interlocked.Exchange(ref lastEnter, Stopwatch.GetTimestamp());
                    if (DisplayMessage(m.message)) {
                        StringBuilder cls = new StringBuilder(256);
                        GetClassName(m.hwnd, cls, cls.Capacity);
                        log(String.Format("[DisplayProbe] enter msg={0:X} class={1}", m.message, cls));
                    }
                }
                return CallNextHookEx(IntPtr.Zero, code, w, l);
            };
            leaving = delegate(int code, IntPtr w, IntPtr l) {
                if (code >= 0) {
                    ReturnMessage m = (ReturnMessage)Marshal.PtrToStructure(l, typeof(ReturnMessage));
                    leaveMessage = (int)m.message;
                    Interlocked.Increment(ref leaveCount); Interlocked.Exchange(ref lastLeave, Stopwatch.GetTimestamp());
                    if (DisplayMessage(m.message)) {
                        StringBuilder cls = new StringBuilder(256);
                        GetClassName(m.hwnd, cls, cls.Capacity);
                        log(String.Format("[DisplayProbe] leave msg={0:X} class={1}", m.message, cls));
                    }
                }
                return CallNextHookEx(IntPtr.Zero, code, w, l);
            };
            getting = delegate(int code, IntPtr w, IntPtr l) {
                if (code >= 0) {
                    QueueMessage m = (QueueMessage)Marshal.PtrToStructure(l, typeof(QueueMessage));
                    getMessage = (int)m.message;
                    Interlocked.Increment(ref getCount); Interlocked.Exchange(ref lastGet, Stopwatch.GetTimestamp());
                }
                return CallNextHookEx(IntPtr.Zero, code, w, l);
            };
            SetWindowsHookEx(3, getting, IntPtr.Zero, GetCurrentThreadId());
            SetWindowsHookEx(4, entering, IntPtr.Zero, GetCurrentThreadId());
            SetWindowsHookEx(12, leaving, IntPtr.Zero, GetCurrentThreadId());
        }


        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern IntPtr OpenThreadWaitChainSession(uint flags, IntPtr callback);
        [DllImport("advapi32.dll")]
        private static extern void CloseThreadWaitChainSession(IntPtr session);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetThreadWaitChain(IntPtr session, IntPtr context, uint flags,
            uint tid, ref uint count, IntPtr nodes, out bool cycle);
        [DllImport("advapi32.dll")]
        private static extern void RegisterWaitChainCOMCallback(IntPtr callState, IntPtr activationState);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumThreadWindows(uint tid, EnumWindow callback, IntPtr state);
        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindow callback, IntPtr state);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int capacity);
        [DllImport("dbghelp.dll", SetLastError = true)]
        private static extern bool MiniDumpWriteDump(IntPtr process, uint pid, IntPtr file,
            uint flags, IntPtr exception, IntPtr streams, IntPtr callback);

        private enum ObjectType { CriticalSection = 1, SendMessage, Mutex, Alpc, Com,
            ThreadWait, ProcessWait, Thread, ComActivation, Unknown, SocketIo, SmbIo }
        private enum ObjectStatus { NoAccess = 1, Running, Blocked, PidOnly, PidOnlyRpcss,
            Owned, NotOwned, Abandoned, Unknown, Error }

        private static string ProcessName(uint pid)
        {
            try { using (Process p = Process.GetProcessById((int)pid))
                return Path.GetFileName(p.ProcessName) + ".exe"; }
            catch { return "unavailable"; }
        }
        private static string OneLine(string value)
        {
            return value.Replace("\r", "?").Replace("\n", "?").Replace("\t", "?");
        }
        private static void Windows(uint tid, LogLine log)
        {
            Dictionary<IntPtr, bool> seen = new Dictionary<IntPtr, bool>();
            SortedDictionary<string, int> classes = new SortedDictionary<string, int>();
            EnumWindow add = delegate(IntPtr hwnd, IntPtr unused) {
                uint pid;
                if (GetWindowThreadProcessId(hwnd, out pid) != tid || seen.ContainsKey(hwnd)) return true;
                seen.Add(hwnd, true);
                StringBuilder name = new StringBuilder(256);
                string cls = GetClassName(hwnd, name, name.Capacity) == 0 ? "unavailable" : OneLine(name.ToString());
                if (!classes.ContainsKey(cls)) classes.Add(cls, 0);
                classes[cls]++;
                return true;
            };
            EnumWindow top = delegate(IntPtr hwnd, IntPtr unused) {
                add(hwnd, IntPtr.Zero);
                EnumChildWindows(hwnd, add, IntPtr.Zero);
                return true;
            };
            EnumThreadWindows(tid, top, IntPtr.Zero);
            // EnumThreadWindows excludes message-only windows (e.g. the tray icon).
            IntPtr message = IntPtr.Zero;
            while ((message = FindWindowEx(new IntPtr(-3), message, null, null)) != IntPtr.Zero)
                top(message, IntPtr.Zero);
            StringBuilder summary = new StringBuilder();
            foreach (KeyValuePair<string, int> pair in classes)
                summary.AppendFormat(" [{0} x{1}]", pair.Key, pair.Value);
            log(String.Format("NATIVE tid={0} windows={1} classes:{2}", tid, seen.Count, summary));
            GC.KeepAlive(top); GC.KeepAlive(add);
        }

        private static void WaitChain(uint tid, LogLine log)
        {
            // Keep ole32 loaded for the lifetime of its registered callbacks.
            // This worker runs once at a time; registration occurs only once.
            if (!comInitialized)
            {
                IntPtr ole = LoadLibrary("ole32.dll");
                if (ole != IntPtr.Zero)
                {
                    IntPtr call = GetProcAddress(ole, "CoGetCallState");
                    IntPtr activation = GetProcAddress(ole, "CoGetActivationState");
                    if (call != IntPtr.Zero && activation != IntPtr.Zero)
                        RegisterWaitChainCOMCallback(call, activation);
                }
                comInitialized = true;
            }
            IntPtr session = OpenThreadWaitChainSession(0, IntPtr.Zero);
            if (session == IntPtr.Zero) { log("WCT open error=" + Marshal.GetLastWin32Error()); return; }
            // wct.h: two DWORD enums + 272-byte union, total 280 on both x86/x64.
            IntPtr nodes = IntPtr.Zero;
            try
            {
                nodes = Marshal.AllocHGlobal(16 * 280);
                uint count = 16; bool cycle;
                if (!GetThreadWaitChain(session, IntPtr.Zero, 7, tid, ref count, nodes, out cycle))
                { log("WCT tid=" + tid + " error=" + Marshal.GetLastWin32Error()); return; }
                StringBuilder chain = new StringBuilder();
                for (int i = 0; i < Math.Min(count, 16); i++)
                {
                    IntPtr node = new IntPtr(nodes.ToInt64() + i * 280);
                    ObjectType type = (ObjectType)Marshal.ReadInt32(node, 0);
                    ObjectStatus status = (ObjectStatus)Marshal.ReadInt32(node, 4);
                    chain.AppendFormat(" [{0}:{1}/{2}", i, type, status);
                    if (type == ObjectType.Thread)
                    {
                        uint pid = unchecked((uint)Marshal.ReadInt32(node, 8));
                        uint thread = unchecked((uint)Marshal.ReadInt32(node, 12));
                        chain.AppendFormat(" pid={0} tid={1} exe={2}", pid, thread, OneLine(ProcessName(pid)));
                    }
                    else chain.Append(" pid=- tid=-"); // Lock union has no IDs; never emit ObjectName.
                    chain.Append("]");
                }
                log(String.Format("WCT tid={0} nodes={1} cycle={2}:{3}", tid, count, cycle, chain));
            }
            finally
            {
                if (nodes != IntPtr.Zero) Marshal.FreeHGlobal(nodes);
                CloseThreadWaitChainSession(session);
            }
        }
        private static bool comInitialized;

        private static void Dump(uint tid, string directory, LogLine log)
        {
            // A dedicated thread is essential for in-process MiniDumpWriteDump.
            // No managed thread suspension and no watchdog Join/lock on this worker.
            // If native capture wedges, the busy flag prevents accumulating workers.
            Directory.CreateDirectory(directory);
            string[] old = Directory.GetFiles(directory, "ui-stall-*.dmp");
            // Honor the limit across watchdog restarts too, without IO on the watchdog.
            // Only files from this diagnostic are touched.
            foreach (string previous in old)
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(previous) < TimeSpan.FromMinutes(10))
                {
                    log("NATIVE dump skipped: recent retained dump");
                    return;
                }
            Array.Sort(old, StringComparer.Ordinal);
            for (int i = 0; i <= old.Length - KeepDumps; i++) File.Delete(old[i]);
            string name = "ui-stall-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + tid + ".dmp";
            string path = Path.Combine(directory, name);
            bool complete = false;
            try
            {
                using (Process process = Process.GetCurrentProcess())
                using (FileStream file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    // Normal thread contexts/stacks, plus thread info; strip module paths.
                    // Do not request full memory, handle data, or unloaded-module paths.
                    // In-process dump capture can itself delay input. Bracket it
                    // so an input lag is not attributed to the original stall.
                    long started = Stopwatch.GetTimestamp();
                    log("NATIVE dump begin mainTid=" + tid + " qpc=" + started + " qpcHz=" + Stopwatch.Frequency);
                    complete = MiniDumpWriteDump(process.Handle, (uint)process.Id,
                        file.SafeFileHandle.DangerousGetHandle(), 0x1080, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                    int error = Marshal.GetLastWin32Error();
                    long ended = Stopwatch.GetTimestamp();
                    log((complete ? "NATIVE dump=" + name + " mainTid=" + tid : "NATIVE dump error=" + error) +
                        " qpc=" + ended + " elapsedMs=" + ((ended - started) * 1000.0 / Stopwatch.Frequency).ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            finally { if (!complete && File.Exists(path)) File.Delete(path); }
        }

        public static void Capture(uint mainTid, LogLine log)
        {
            CaptureTo(mainTid, Path.Combine(Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData), "WindowTabs"), "dumps"), log);
        }
        // Destination injection lets a disposable harness avoid the user's diagnostics.
        public static void CaptureTo(uint mainTid, string dumpDirectory, LogLine log)
        {
            if (Interlocked.CompareExchange(ref probing, 1, 0) == 0)
            {
                Thread probe = new Thread(delegate() {
                    try { Windows(mainTid, log); WaitChain(mainTid, log); }
                    catch (Exception ex) { log("NATIVE probe failed=" + ex.GetType().Name); }
                    finally { Interlocked.Exchange(ref probing, 0); }
                });
                probe.IsBackground = true; probe.Name = "WindowTabs Native Stall Probe";
                try { probe.Start(); }
                catch { Interlocked.Exchange(ref probing, 0); throw; }
            }
            else log("NATIVE probe pending; tid=" + mainTid);
            string skipped = null;
            lock (dumpGate)
            {
                long now = clock.ElapsedMilliseconds;
                if (dumping != 0) skipped = "NATIVE dump pending";
                else if (lastDump != 0 && now - lastDump < 600000) skipped = "NATIVE dump skipped: 10-minute limit";
                else
                {
                    lastDump = Math.Max(1, now);
                    dumping = 1;
                }
            }
            if (skipped != null)
            {
                try { log(skipped); } catch { }
                return;
            }
            Thread writer = new Thread(delegate() {
                try { Dump(mainTid, dumpDirectory, log); }
                catch (Exception ex) { log("NATIVE dump failed=" + ex.GetType().Name); }
                finally { Interlocked.Exchange(ref dumping, 0); }
            });
            writer.IsBackground = true; writer.Name = "WindowTabs Native Stall Dump";
            try { writer.Start(); }
            catch { Interlocked.Exchange(ref dumping, 0); throw; }
        }
    }
}
#endif
