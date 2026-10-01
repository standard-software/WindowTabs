namespace Bemo

#if DEBUG
open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text
open System.Threading

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
    type Record =
        { at: DateTime; kind: Kind; ms: float; distance: float
          message: int; x: int; y: int; tick: uint32; hwnd: IntPtr }

    let private queue = new BlockingCollection<Record>(queueCapacity)
    let mutable private dropped = 0
    let mutable private started = 0
    let private enqueue kind ms distance message x y tick hwnd =
        let record = { at = DateTime.Now; kind = kind; ms = ms; distance = distance
                       message = message; x = x; y = y; tick = tick; hwnd = hwnd }
        if not (queue.TryAdd(record)) then Interlocked.Increment(&dropped) |> ignore

    // One detector per hook thread. Event stamps, not arrival times, measure
    // gaps so a delayed batch is not mistaken for a gap in generated events.
    // Require a short, moving step immediately before the gap to reject
    // rest-then-flick input without preceding motion.
    type Detector(emit: Kind -> float -> float -> int -> int -> int -> uint32 -> IntPtr -> unit) =
        let mutable move : (uint32 * int * int) option = None
        let mutable moving = false
        member this.Reset() =
            move <- None
            moving <- false
        member this.Observe(now: uint32, tick: uint32, message: int, x: int, y: int) =
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
    let context kind hwnd ms = enqueue kind ms 0.0 0 0 0 0u hwnd

    let format version record =
        sprintf "%s version=%s kind=%A ms=%.2f distance=%.2f msg=0x%X x=%d y=%d tick=%u hwnd=0x%X"
            (record.at.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)) version
            record.kind record.ms record.distance record.message record.x record.y record.tick (record.hwnd.ToInt64())

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
        let counts = Array.zeroCreate<int64> 7
        let maxima = Array.zeroCreate<float> 7
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
                    write (sprintf "%s version=%s kind=Summary lag=%d gap=%d slow=%d minimize=%d maximize=%d restore=%d watchdog=%d maxMs=%.2f lagMaxMs=%.2f gapMaxMs=%.2f slowMaxMs=%.2f dropped=%d"
                        (DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)) version
                        counts.[0] counts.[1] counts.[2] counts.[3] counts.[4] counts.[5] counts.[6]
                        (Array.max maxima) maxima.[0] maxima.[1] maxima.[2] lost)
                Array.Clear(counts, 0, counts.Length)
                Array.Clear(maxima, 0, maxima.Length)
                minute.Restart()

    let start version =
        if Interlocked.CompareExchange(&started, 1, 0) = 0 then
            let worker = Thread(ThreadStart(fun () ->
                let path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                        "WindowTabs", "input_stall.log")
                writeLoop path version), IsBackground = true, Name = "Input stall trace")
            worker.Start()
#endif
