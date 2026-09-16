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

    let time (name: string) (f: unit -> 'a) =
        let start = Stopwatch.GetTimestamp()
        try f()
        finally
            let ms = float (Stopwatch.GetTimestamp() - start) * 1000.0 / float Stopwatch.Frequency
            lock gate (fun () ->
                counts.[name] <- (match counts.TryGetValue name with | true, v -> v + 1 | _ -> 1)
                millis.[name] <- (match millis.TryGetValue name with | true, v -> v + ms | _ -> ms))

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
    let time (_name: string) (f: unit -> 'a) = f()
    let gauge (_name: string) (_value: int) = ()
    let flush (_version: string) = ()
#endif
