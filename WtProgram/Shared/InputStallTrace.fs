namespace Bemo

#if DEBUG
open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text
open System.Threading

// A/B experiments only. Module values snapshot the environment once at startup;
// neither the switches nor their environment-variable names exist in Release.
module StallExperiment =
    let noMouseHook = Environment.GetEnvironmentVariable("WINDOWTABS_DEBUG_NO_MOUSE_HOOK") = "1"
    let noCaptionQuery = Environment.GetEnvironmentVariable("WINDOWTABS_DEBUG_NO_CAPTION_QUERY") = "1"
    let syncFollowers = Environment.GetEnvironmentVariable("WINDOWTABS_DEBUG_SYNC_FOLLOWERS") = "1"

// Numeric evidence only. Producers never format text or touch the filesystem.
module InputStallTrace =
    [<Literal>]
    let lagMs = 100u
    [<Literal>]
    let gapMs = 150u
    [<Literal>]
    let gapPixels = 40.0
    [<Literal>]
    let movingIntervalMs = 50u
    [<Literal>]
    let movingPixels = 2.0
    [<Literal>]
    let slowMs = 20.0
    [<Literal>]
    let summaryMs = 60000L
    [<Literal>]
    let maxBytes = 8L * 1024L * 1024L
    [<Literal>]
    let queueCapacity = 1024

    type Kind = Lag = 0 | Gap = 1 | Slow = 2 | Minimize = 3 | Maximize = 4 | Restore = 5 | Watchdog = 6
              | Operation = 7 | PingAck = 8 | HookThreadBusy = 9

    [<System.Runtime.InteropServices.DllImport("kernel32.dll")>]
    extern uint32 GetCurrentThreadId()
    type Record =
        { at: DateTime; kind: Kind; ms: float; distance: float
          message: int; x: int; y: int; tick: uint32; hwnd: IntPtr
          gc0: int; gc1: int; gc2: int; gcDelta0: int; gcDelta1: int; gcDelta2: int
          gcBaseline: int; priority: int; tid: uint32; qpc: int64; stage: string }

    let private queue = new BlockingCollection<Record>(queueCapacity)
    let mutable private dropped = 0
    let mutable private started = 0
    let private enqueueAt qpc stage kind ms distance message x y tick hwnd gc0 gc1 gc2 delta0 delta1 delta2 baseline priority =
        let record = { at = DateTime.Now; kind = kind; ms = ms; distance = distance
                       message = message; x = x; y = y; tick = tick; hwnd = hwnd
                       gc0 = gc0; gc1 = gc1; gc2 = gc2; gcDelta0 = delta0; gcDelta1 = delta1
                       gcDelta2 = delta2; gcBaseline = baseline; priority = priority
                       tid = GetCurrentThreadId(); qpc = qpc; stage = stage }
        if not (queue.TryAdd(record)) then Interlocked.Increment(&dropped) |> ignore

    let private enqueue kind ms distance message x y tick hwnd gc0 gc1 gc2 delta0 delta1 delta2 baseline priority =
        enqueueAt (Stopwatch.GetTimestamp()) "-" kind ms distance message x y tick hwnd
            gc0 gc1 gc2 delta0 delta1 delta2 baseline priority

    // qpc marks completion; subtract ms * qpcHz / 1000 for the start. Preserve
    // this stamp even if the perf counter lock delayed delivery to the queue.
    let completed kind stage ms ended =
        enqueueAt ended stage kind ms 0.0 0 0 0 0u IntPtr.Zero -1 -1 -1 0 0 0 0 -1

    let private operation name ms ended =
        match name with
        | "captionButtons.query" | "group.follower.style" | "group.follower.frameBounds"
        | "stripRender" | "layered.update" | "layered.move" -> completed Kind.Operation name ms ended
        | _ -> ()

    // One detector per hook thread. Event stamps, not arrival times, measure
    // gaps so a delayed batch is not mistaken for a gap in generated events.
    // Require a short, moving step immediately before the gap to reject
    // rest-then-flick input without preceding motion.
    type Detector(sink: Kind -> float -> float -> int -> int -> int -> uint32 -> IntPtr ->
                        int -> int -> int -> int -> int -> int -> int -> int -> unit) =
        let mutable gc0, gc1, gc2 = 0, 0, 0
        let mutable delta0, delta1, delta2 = 0, 0, 0
        let mutable havePrevious = false
        let mutable baseline = 0
        let emit kind ms distance message x y tick hwnd =
            sink kind ms distance message x y tick hwnd gc0 gc1 gc2 delta0 delta1 delta2 baseline
                (int Thread.CurrentThread.Priority)
        let mutable move : (uint32 * int * int) option = None
        let mutable moving = false
        member this.Reset() =
            havePrevious <- false
            move <- None
            moving <- false
        member this.Observe(now: uint32, tick: uint32, message: int, x: int, y: int) =
            // Sample every event, not just incidents. Counts establish correlation,
            // not pause duration: background collections also change these counts.
            let current0 = GC.CollectionCount(0)
            let current1 = GC.CollectionCount(1)
            let current2 = GC.CollectionCount(2)
            baseline <- if havePrevious then 1 else 0
            delta0 <- if havePrevious then current0 - gc0 else 0
            delta1 <- if havePrevious then current1 - gc1 else 0
            delta2 <- if havePrevious then current2 - gc2 else 0
            gc0 <- current0
            gc1 <- current1
            gc2 <- current2
            havePrevious <- true
            let lag = now - tick
            let wasMoving = moving
            let distance, gap =
                if message = 0x200 then
                    let result =
                        match move with
                        | Some(previous, px, py) ->
                            let dx, dy = float x - float px, float y - float py
                            sqrt (dx * dx + dy * dy), tick - previous
                        | None -> 0.0, 0u
                    let distance, interval = result
                    moving <- interval <= movingIntervalMs && distance >= movingPixels
                    move <- Some(tick, x, y)
                    result
                else 0.0, 0u
            // Unsigned subtraction survives TickCount wrap; reject reversed stamps.
            if lag >= lagMs && lag <= uint32 Int32.MaxValue then
                emit Kind.Lag (float lag) distance message x y tick IntPtr.Zero
            if wasMoving && gap >= gapMs && gap <= uint32 Int32.MaxValue && distance >= gapPixels then
                emit Kind.Gap (float gap) distance message x y tick IntPtr.Zero
            distance
        member this.Complete(ms, distance, message, x, y, tick) =
            if ms >= slowMs then emit Kind.Slow ms distance message x y tick IntPtr.Zero

    let detector () = Detector(enqueue)
    let context kind hwnd ms = enqueue kind ms 0.0 0 0 0 0u hwnd -1 -1 -1 0 0 0 0 -1

    let format version record =
        sprintf "%s version=%s kind=%A ms=%.2f distance=%.2f msg=0x%X x=%d y=%d tick=%u hwnd=0x%X gc0=%d gc1=%d gc2=%d gcDelta0=%d gcDelta1=%d gcDelta2=%d gcBaseline=%d priority=%d tid=%u qpc=%d qpcHz=%d stage=%s"
            (record.at.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)) version
            record.kind record.ms record.distance record.message record.x record.y record.tick (record.hwnd.ToInt64())
            record.gc0 record.gc1 record.gc2 record.gcDelta0 record.gcDelta1 record.gcDelta2 record.gcBaseline record.priority
            record.tid record.qpc Stopwatch.Frequency record.stage

    // Rotation is checked on every write, including during a multi-day run.
    let append (path: string) (line: string) =
        Directory.CreateDirectory(Path.GetDirectoryName(path)) |> ignore
        let bytes = Encoding.UTF8.GetBytes(line + "\r\n")
        let file = FileInfo(path)
        if file.Exists && file.Length + int64 bytes.Length > maxBytes then
            let backup = path + ".1"
            if File.Exists(backup) then File.Delete(backup)
            File.Move(path, backup)
        use stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)
        stream.Write(bytes, 0, bytes.Length)

    let private writeLoop path version =
        let counts = Array.zeroCreate<int64> 10
        let maxima = Array.zeroCreate<float> 10
        let minute = Stopwatch.StartNew()
        let write line = try append path line with _ -> ()
        while true do
            let mutable record = Unchecked.defaultof<Record>
            if queue.TryTake(&record, 1000) then
                let index = int record.kind
                counts.[index] <- counts.[index] + 1L
                maxima.[index] <- max maxima.[index] record.ms
                write (format version record)
            if minute.ElapsedMilliseconds >= summaryMs then
                let lost = Interlocked.Exchange(&dropped, 0)
                if Array.exists ((<) 0L) counts || lost > 0 then
                    write (sprintf "%s version=%s kind=Summary lag=%d gap=%d slow=%d minimize=%d maximize=%d restore=%d watchdog=%d operation=%d pingAck=%d hookThreadBusy=%d maxMs=%.2f lagMaxMs=%.2f gapMaxMs=%.2f slowMaxMs=%.2f dropped=%d"
                        (DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)) version
                        counts.[0] counts.[1] counts.[2] counts.[3] counts.[4] counts.[5] counts.[6] counts.[7] counts.[8] counts.[9]
                        (Array.max maxima) maxima.[0] maxima.[1] maxima.[2] lost)
                Array.Clear(counts, 0, counts.Length)
                Array.Clear(maxima, 0, maxima.Length)
                minute.Restart()

    let start version =
        if Interlocked.CompareExchange(&started, 1, 0) = 0 then
            PerfTrace.slowOperation <- operation
            HookThreadTiming.completed <- fun name ms ended -> completed Kind.HookThreadBusy name ms ended
            let worker = Thread(ThreadStart(fun () ->
                let path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                        "WindowTabs", "input_stall.log")
                writeLoop path version), IsBackground = true, Name = "Input stall trace")
            worker.Start()
#endif
