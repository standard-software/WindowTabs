namespace Bemo

open System

// Build caller-owned data before taking a writer gate. The append callback is
// the logger's own writer and receives only the finished immutable message.
module TraceWriter =
    let write gate build append =
        try
            let message = build()
            lock gate (fun () -> append message)
        with _ -> ()

    // One queue for both ordinary and handover lines prevents a later synchronous
    // line from overtaking an earlier handover from the same thread.
    type Queued(append: string -> unit) =
        let gate = obj()
        let pending = Collections.Generic.Queue<string>()
        let mutable running = false
        let drain () =
            let mutable draining = true
            while draining do
                let next = lock gate (fun () ->
                    if pending.Count = 0 then
                        running <- false
                        None
                    else Some(pending.Dequeue()))
                match next with
                | Some line -> try append line with _ -> ()
                | None -> draining <- false
        member _.Post(build: unit -> string) =
            try
                let line = build()
                let start = lock gate (fun () ->
                    pending.Enqueue(line)
                    if running then false
                    else running <- true; true)
                if start then Threading.ThreadPool.QueueUserWorkItem(fun _ -> drain()) |> ignore
            with _ -> ()

// Debug-only trace of the session restore: which saved entry each window
// claimed, by which route, and where it was placed. Truncated at each start.
module RestoreTrace =
#if DEBUG
    let private path =
        IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WindowTabs", "restore_trace.log")
    let mutable private started = false
    let private gate = obj()
#endif
    // Takes a thunk, not a string: an argument is evaluated before the call,
    // so taking the message itself would leave every sprintf at every call
    // site running in Release - including two that walk the whole tab strip -
    // with only the file write compiled out.
#if DEBUG
    let log (f: unit -> string) =
#else
    let inline log (f: unit -> string) =
#endif
#if DEBUG
        TraceWriter.write gate (fun () ->
            sprintf "%s %s\r\n" (DateTime.Now.ToString("HH:mm:ss.fff")) (f())) (fun line ->
            try
                if not started then
                    started <- true
                    try IO.File.WriteAllText(path, "") with _ -> ()
                IO.File.AppendAllText(path, line)
            with _ -> ())
#else
        ignore f
#endif


#if DEBUG
    let private retentionGate = obj()
    let private retentionReasons = Collections.Generic.Dictionary<IntPtr * int * IntPtr, string * string>()
#endif

    // Remember each entry independently: interleaved saves must not make an
    // unchanged reason noisy again. A changed reason or detail is a new event.
#if DEBUG
    let retention (hwnd: IntPtr) (token: IntPtr) rank reason (detail: unit -> string) =
#else
    let inline retention (hwnd: IntPtr) (token: IntPtr) rank reason (detail: unit -> string) =
#endif
#if DEBUG
        let text = detail()
        let changed = lock retentionGate (fun () ->
            let key = token, rank, hwnd
            match retentionReasons.TryGetValue key with
            | true, previous when previous = (reason, text) -> false
            | _ ->
                retentionReasons.[key] <- reason, text
                true)
        if changed then
            log (fun () ->
                sprintf "restore-retention hwnd=%X token=%X rank=%d reason=%s%s"
                    (hwnd.ToInt64()) (token.ToInt64()) rank reason text)
#else
        ()
#endif
