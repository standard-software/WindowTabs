namespace Bemo
open System
#if DEBUG
open System.Collections.Generic
open System.Diagnostics
#endif

// What this process spends its time on, one line per second in
// %APPDATA%\WindowTabs\perf_trace.log. Counters are named by the caller;
// `time` adds both a count and the milliseconds spent. The line also carries
// the process's own CPU, memory and handle figures, so a run that grows over
// hours is visible in the same file.
//
// A Debug build only: a shipped copy measures nothing and writes nothing. The
// calls stay where they are, so measuring a run again needs no more than a
// Debug build.
module PerfTrace =
#if DEBUG
    [<System.Runtime.InteropServices.DllImport("user32.dll")>]
    extern uint32 GetGuiResources(nativeint hProcess, uint32 uiFlags)

    // Allocate once on the registering thread, then capture these instances
    // before posting hook registration. Increment never enters the trace gate.
    [<Sealed>]
    type AtomicCounter() =
        let mutable value = 0
        member _.Increment() = System.Threading.Interlocked.Increment(&value) |> ignore
        member _.Take() = System.Threading.Interlocked.Exchange(&value, 0)

    let winEventCallback = AtomicCounter()
    let winEventFiltered = AtomicCounter()
    let winEventPosted = AtomicCounter()
    let private atomicCounters =
        [| "winEvent.callback", winEventCallback
           "winEvent.filtered", winEventFiltered
           "winEvent.posted", winEventPosted |]

    // All storage is allocated at registration. Callback work is bounded and
    // uses only scalar arithmetic and Interlocked operations, without a gate.
    [<Sealed>]
    type WinEventBurstCounter(minEvent: int, maxEvent: int, pid: int, tid: int) =
        let events = Array.zeroCreate<int> (max 1 (min 65536 (maxEvent - minEvent + 1)))
        let objects = Array.zeroCreate<int> 2
        // Pack the thread ID and count together so draining cannot associate a
        // new thread with an old count. Slots are reusable every interval.
        let threads = Array.zeroCreate<int64> 256
        let mutable overflow = 0
        let mutable outsideRange = 0
        member _.Range = minEvent, maxEvent, pid = 0, tid = 0
        member _.Record(evt: int, objectZero: bool, threadId: int) =
            let index = evt - minEvent
            if index >= 0 && index < events.Length then
                System.Threading.Interlocked.Increment(&events.[index]) |> ignore
            else System.Threading.Interlocked.Increment(&outsideRange) |> ignore
            let objectIndex = if objectZero then 0 else 1
            System.Threading.Interlocked.Increment(&objects.[objectIndex]) |> ignore
            let key = int64 (uint32 threadId) <<< 32
            let mutable probe = 0
            let mutable recorded = false
            while probe < 8 && not recorded do
                let slot = ((threadId ^^^ (threadId >>> 8)) + probe) &&& 255
                let old = System.Threading.Interlocked.Read(&threads.[slot])
                if old = 0L || (old &&& -4294967296L) = key then
                    let next = key ||| ((old &&& 4294967295L) + 1L)
                    recorded <- System.Threading.Interlocked.CompareExchange(&threads.[slot], next, old) = old
                probe <- probe + 1
            if not recorded then System.Threading.Interlocked.Increment(&overflow) |> ignore
        member _.Take() =
            let eventCounts = events |> Array.mapi (fun i _ -> minEvent + i, System.Threading.Interlocked.Exchange(&events.[i], 0))
            let threadCounts = threads |> Array.mapi (fun i _ ->
                let packed = System.Threading.Interlocked.Exchange(&threads.[i], 0L)
                int (packed >>> 32), int (packed &&& 4294967295L))
            let zero = System.Threading.Interlocked.Exchange(&objects.[0], 0)
            let other = System.Threading.Interlocked.Exchange(&objects.[1], 0)
            eventCounts, threadCounts, zero, other,
                System.Threading.Interlocked.Exchange(&overflow, 0),
                System.Threading.Interlocked.Exchange(&outsideRange, 0)

    [<System.Runtime.InteropServices.DllImport("kernel32.dll")>]
    extern nativeint OpenThread(uint32 access, bool inheritHandle, uint32 threadId)
    [<System.Runtime.InteropServices.DllImport("kernel32.dll")>]
    extern uint32 GetProcessIdOfThread(nativeint handle)
    [<System.Runtime.InteropServices.DllImport("kernel32.dll")>]
    extern bool CloseHandle(nativeint handle)
    [<System.Runtime.InteropServices.DllImport("user32.dll")>]
    extern uint32 GetWindowThreadProcessId(nativeint hwnd, uint32& pid)

    let private burstGate = obj()
    let private burstHooks = Dictionary<int, WinEventBurstCounter * bool>()
    let private burstLines = Queue<string>()
    let private slowQueries = Queue<uint32 * float>()
    let mutable private nextHook = 0
    let mutable private hooksChanged = false
    let mutable private hooksDisabled = false
    let mutable private subscriberCount = 0

    let winEventSubscribers count =
        lock burstGate (fun () ->
            subscriberCount <- count
            hooksChanged <- true)

    let registerWinEventBurst counter =
        lock burstGate (fun () ->
            nextHook <- nextHook + 1
            burstHooks.Add(nextHook, (counter, true))
            hooksChanged <- true
            nextHook)

    let unregisterWinEventBurst hookId =
        lock burstGate (fun () ->
            let counter, _ = burstHooks.[hookId]
            // Retain the last interval until the writer has drained it.
            burstHooks.[hookId] <- counter, false
            hooksChanged <- true)

    let private inventory reason =
        let active = burstHooks |> Seq.filter (fun kv -> snd kv.Value) |> Seq.toArray
        let ranges = active |> Array.map (fun kv ->
            let counter, _ = kv.Value
            let lo, hi, pidZero, tidZero = counter.Range
            sprintf "hook=%d:min=%d,max=%d,pidZero=%b,tidZero=%b" kv.Key lo hi pidZero tidZero)
        sprintf "winEvent.hooks reason=%s disabled=%b count=%d subscribers=%d [%s]"
            reason hooksDisabled active.Length subscriberCount (String.concat ";" ranges)

    // Called via the pump, after registrations already queued by the caller.
    // Later asynchronous group changes produce a fresh inventory at flush.
    let winEventHookInventory reason disabled =
        lock burstGate (fun () ->
            hooksDisabled <- disabled
            burstLines.Enqueue(inventory reason))

    let captionQueryPid hwnd =
        let mutable pid = 0u
        GetWindowThreadProcessId(hwnd, &pid) |> ignore
        pid

    let captionQuerySlow pid ms =
        if ms > 100.0 then lock burstGate (fun () -> slowQueries.Enqueue(pid, ms))

    let private processExe pid =
        if pid = 0u then "pid=unknown"
        else
            try
                use target = Process.GetProcessById(int pid)
                // ProcessName is a basename; never read a title or module path.
                let name = target.ProcessName.Replace("\\", "_").Replace("/", "_")
                let safe = name |> Seq.map (fun c -> if Char.IsControl(c) || " ,;=[]".IndexOf(c) >= 0 then '_' else c) |> Seq.toArray |> String
                "exe=" + safe + ".exe"
            with _ -> sprintf "pid=%u" pid

    let private threadSource tid =
        let handle = OpenThread(0x0800u, false, uint32 tid)
        if handle = nativeint 0 then sprintf "tid=%u,pid=unknown" (uint32 tid)
        else
            try sprintf "tid=%u,%s" (uint32 tid) (processExe (GetProcessIdOfThread(handle)))
            finally CloseHandle(handle) |> ignore

    let private topCounts values =
        values |> Seq.filter (fun (_, count) -> count > 0)
        |> Seq.groupBy fst |> Seq.map (fun (key, items) -> key, items |> Seq.sumBy snd)
        |> Seq.sortBy (fun (key, count) -> -count, key) |> Seq.truncate 3 |> Seq.toArray

    let private takeBurstLines () =
        let snapshots, pending, queries =
            lock burstGate (fun () ->
                if hooksChanged then
                    burstLines.Enqueue(inventory "hooksChanged")
                    hooksChanged <- false
                let snapshots = burstHooks |> Seq.map (fun kv -> kv.Key, (fst kv.Value).Take()) |> Seq.toArray
                let retired = burstHooks |> Seq.filter (fun kv -> not (snd kv.Value)) |> Seq.map (fun kv -> kv.Key) |> Seq.toArray
                for key in retired do burstHooks.Remove(key) |> ignore
                let pending = burstLines.ToArray()
                burstLines.Clear()
                let queries = slowQueries.ToArray()
                slowQueries.Clear()
                snapshots, pending, queries)
        let total = snapshots |> Array.sumBy (fun (_, (events, _, _, _, _, outside)) -> outside + Array.sumBy snd events)
        let lines = ResizeArray<string>(pending)
        if total > 1000 then
            let resolved = Dictionary<int, string>()
            let source tid =
                match resolved.TryGetValue(tid) with
                | true, value -> value
                | _ ->
                    let value = threadSource tid
                    resolved.Add(tid, value)
                    value
            let events = snapshots |> Seq.collect (fun (_, (events, _, _, _, _, _)) -> events) |> topCounts
            let sources = snapshots |> Seq.collect (fun (_, (_, threads, _, _, _, _)) -> threads) |> topCounts
            let zero = snapshots |> Array.sumBy (fun (_, (_, _, n, _, _, _)) -> n)
            let other = snapshots |> Array.sumBy (fun (_, (_, _, _, n, _, _)) -> n)
            let overflow = snapshots |> Array.sumBy (fun (_, (_, _, _, _, n, _)) -> n)
            let outside = snapshots |> Array.sumBy (fun (_, (_, _, _, _, _, n)) -> n)
            let eventText = events |> Array.map (fun (evt, n) -> sprintf "%d:%d" evt n) |> String.concat ","
            let sourceText = sources |> Array.map (fun (tid, n) -> sprintf "%s,count=%d" (source tid) n) |> String.concat ";"
            let hookText = snapshots |> Array.choose (fun (key, (events, threads, _, _, lost, outside)) ->
                let count = outside + Array.sumBy snd events
                if count = 0 then None else
                let ev = topCounts events |> Array.map (fun (e, n) -> sprintf "%d:%d" e n) |> String.concat ","
                let src = topCounts threads |> Array.map (fun (t, n) -> sprintf "%s,count=%d" (source t) n) |> String.concat ","
                Some(sprintf "hook=%d,count=%d,events=[%s],sources=[%s],sourceOverflow=%d" key count ev src lost)) |> String.concat ";"
            lines.Add(sprintf "winEvent.burst total=%d objectZero=%d objectNonzero=%d sourceOverflow=%d eventOutsideRange=%d events=[%s] sources=[%s] hooks=[%s]"
                total zero other overflow outside eventText sourceText hookText)
        for pid, ms in queries do
            lines.Add(sprintf "captionButtons.querySlow %s elapsedMs=%.1f" (processExe pid) ms)
        lines.ToArray()

    let private gate = obj()
    let private counts = Dictionary<string, int>()
    let private millis = Dictionary<string, float>()
    let private gauges = Dictionary<string, int>()
    let mutable private started = false
    let mutable private lastCpu = TimeSpan.Zero
    let mutable private lastWrite = DateTime.MinValue
    let mutable private quietLines = 0
    let private proc = Process.GetCurrentProcess()
    // A run of days must not fill the disk: the file is moved aside at 8 MB and
    // only one older copy is kept.
    let private maxBytes = 8L * 1024L * 1024L

    let private path =
        IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowTabs", "perf_trace.log")

    let count (name: string) =
        lock gate (fun () ->
            counts.[name] <- (match counts.TryGetValue name with | true, v -> v + 1 | _ -> 1))

    // Installed by InputStallTrace. Invoke outside gate: incident recording must
    // not hold the counter lock shared with the low-level mouse hook.
    let mutable slowOperation : (string -> float -> int64 -> unit) = fun _ _ _ -> ()

    let private recordTimeAt name ms ended =
        lock gate (fun () ->
            counts.[name] <- (match counts.TryGetValue name with | true, v -> v + 1 | _ -> 1)
            millis.[name] <- (match millis.TryGetValue name with | true, v -> v + ms | _ -> ms))
        if ms >= 20.0 then
            try slowOperation name ms ended with _ -> ()

    let recordTime (name: string) (ms: float) =
        recordTimeAt name ms (Stopwatch.GetTimestamp())

    let time (name: string) (f: unit -> 'a) =
        let previous = Bemo.Win32.GroupCallTrace.Enter(name)
        let start = Stopwatch.GetTimestamp()
        try f()
        finally
            let ended = Stopwatch.GetTimestamp()
            let ms = float (ended - start) * 1000.0 / float Stopwatch.Frequency
            Bemo.Win32.GroupCallTrace.NoteStage(ms)
            Bemo.Win32.GroupCallTrace.Leave(previous)
            recordTimeAt name ms ended

    let timeCaptionQuery hwnd f =
        let pid = captionQueryPid hwnd
        let start = Stopwatch.GetTimestamp()
        try time "captionButtons.query" f
        finally
            let ms = float (Stopwatch.GetTimestamp() - start) * 1000.0 / float Stopwatch.Frequency
            captionQuerySlow pid ms

    let timeNamed name f = time (name()) f
    let label build = build()

    /// A number that describes the current state rather than an event - the
    /// number of tab groups, tabs, and so on. The last value set is written.
    let gauge (name: string) (value: int) =
        lock gate (fun () -> gauges.[name] <- value)

    /// Writes one line and clears the counters. Called once a second.
    let flush (version: string) =
        try
            let now = DateTime.Now
            let snapshot =
                lock gate (fun () ->
                    for name, counter in atomicCounters do
                        let value = counter.Take()
                        if value <> 0 then
                            counts.[name] <- (match counts.TryGetValue name with | true, v -> v + value | _ -> value)
                    let events =
                        counts
                        |> Seq.map (fun kv ->
                            let ms = match millis.TryGetValue kv.Key with | true, v -> v | _ -> 0.0
                            if ms > 0.0 then sprintf "%s=%d(%.1fms)" kv.Key kv.Value ms
                            else sprintf "%s=%d" kv.Key kv.Value)
                        |> Seq.sort
                    let state = gauges |> Seq.map (fun kv -> sprintf "%s=%d" kv.Key kv.Value) |> Seq.sort
                    let line = Seq.append state events |> String.concat " "
                    let hadEvents = counts.Count > 0
                    counts.Clear()
                    millis.Clear()
                    line, hadEvents)
            proc.Refresh()
            let cpu = proc.TotalProcessorTime
            let elapsed = if lastWrite = DateTime.MinValue then 1.0 else (now - lastWrite).TotalSeconds
            let cpuPercent = if elapsed <= 0.0 then 0.0 else (cpu - lastCpu).TotalSeconds / elapsed * 100.0
            lastCpu <- cpu
            lastWrite <- now
            if not started then
                started <- true
                try
                    let info = IO.FileInfo(path)
                    if info.Exists && info.Length > maxBytes then
                        try IO.File.Delete(path + ".1") with _ -> ()
                        IO.File.Move(path, path + ".1")
                with _ -> ()
                IO.File.AppendAllText(path,
                    sprintf "==== %s started %s version %s\r\n"
                        (now.ToString("yyyy-MM-dd")) (proc.StartTime.ToString("HH:mm:ss")) version)
            // Resolution and file output stay on the existing PerfTrace writer.
            for detail in takeBurstLines() do
                IO.File.AppendAllText(path, sprintf "%s %s\r\n" (now.ToString("HH:mm:ss")) detail)
            // An idle second says nothing new. One line a minute is enough to
            // show the shape of a run that spans hours; anything with events in
            // it is always written.
            let line, hadEvents = snapshot
            let busy = hadEvents || cpuPercent >= 1.0
            quietLines <- if busy then 0 else quietLines + 1
            if busy || quietLines % 60 = 1 then
                let uptime = now - proc.StartTime
                IO.File.AppendAllText(path,
                    sprintf "%s up=%dh%02dm cpu=%.1f%% mem=%dMB heap=%dMB gc=%d/%d/%d threads=%d handles=%d gdi=%d user=%d %s\r\n"
                        (now.ToString("HH:mm:ss")) (int uptime.TotalHours) uptime.Minutes
                        cpuPercent (proc.WorkingSet64 / 1048576L) (GC.GetTotalMemory(false) / 1048576L)
                        (GC.CollectionCount 0) (GC.CollectionCount 1) (GC.CollectionCount 2)
                        proc.Threads.Count proc.HandleCount
                        (int (GetGuiResources(proc.Handle, 0u)))
                        (int (GetGuiResources(proc.Handle, 1u)))
                        line)
        with _ -> ()
#else
    let count (_name: string) = ()
    // Inline, so a Release build calls the timed work directly: no closure
    // is allocated for it at the call site.
    let recordTime (_name: string) (_ms: float) = ()
    let inline time (_name: string) (f: unit -> 'a) = f()
    let inline timeNamed (_name: unit -> string) (f: unit -> 'a) = f()
    let inline label (_build: unit -> string) = ""
    let gauge (_name: string) (_value: int) = ()
    let flush (_version: string) = ()
#endif
