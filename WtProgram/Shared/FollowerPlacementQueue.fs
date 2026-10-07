namespace Bemo
open System
open System.Collections.Generic
open System.Threading

type private FollowerLane() =
    member val operation = obj() with get
    member val revision = 0L with get, set
    member val pending : (int64 * (unit -> unit)) option = None with get, set
    member val running = false with get, set

// Only immutable native-window snapshots cross this boundary. No group Cells,
// service calls or Invoker waits run on a worker. Each HWND has its own serial
// lane, so a busy application does not delay dispatch to the other followers.
type FollowerPlacementQueue() =
    let gate = obj()
    let lanes = Dictionary<IntPtr, FollowerLane>()
    let mutable nextRevision = 0L
    let lane hwnd =
        match lanes.TryGetValue hwnd with
        | true, value -> value
        | _ -> let value = FollowerLane() in lanes.Add(hwnd,value); value
    let bump (item: FollowerLane) =
        nextRevision <- nextRevision + 1L
        item.revision <- nextRevision
        nextRevision
    let rec drain (item: FollowerLane) =
        let work = lock gate <| fun () ->
            match item.pending with
            | None -> item.running <- false; None
            | Some work -> item.pending <- None; Some work
        match work with
        | None -> ()
        | Some(revision, action) ->
            lock item.operation <| fun () ->
                if lock gate (fun () -> item.revision = revision) then
                    try action() with _ -> PerfTrace.count "group.follower.workerFailed"
            drain item

    member _.IsCurrent(hwnd, revision) = lock gate <| fun () ->
        match lanes.TryGetValue hwnd with
        | true, item -> item.revision = revision
        | _ -> false

    member _.HasPending(hwnd) = lock gate <| fun () ->
        match lanes.TryGetValue hwnd with
        | true, item -> item.running
        | _ -> false

    member _.Cancel(hwnd) = lock gate <| fun () ->
        match lanes.TryGetValue hwnd with
        | true, item -> bump item |> ignore; item.pending <- None
        | _ -> ()

    member _.CancelAll() = lock gate <| fun () ->
        for item in lanes.Values do bump item |> ignore; item.pending <- None

    // Exceptional paths (DPI migration, native placement fallback, activation)
    // must finish after an older native operation, not race it.
    member _.Synchronous(hwnd, action: unit -> unit) =
        let item = lock gate <| fun () ->
            let item = lane hwnd
            bump item |> ignore
            item.pending <- None
            item
#if DEBUG
        // The group can wait for a worker before reaching any native API.
        // Measure acquisition separately so a quiet API trace does not hide it.
        let call = Bemo.Win32.GroupCallTrace.Begin("FollowerPlacementQueue.wait", hwnd)
        let mutable entered = false
        try
            lock item.operation (fun () ->
                entered <- true
                Bemo.Win32.GroupCallTrace.End(call)
                action())
        finally
            if not entered then Bemo.Win32.GroupCallTrace.End(call)
#else
        lock item.operation action
#endif

    member this.Post(hwnd, source, sourceBounds: Rect, maximized, bounds: Rect, refused: int64 -> unit) =
        PerfTrace.time "group.follower.post" <| fun () ->
        let pid, tid = Win32Helper.GetWindowProcessId(hwnd), Win32Helper.GetWindowThreadId(hwnd)
        let dpi = WinUserApi.GetDpiForWindow(hwnd)
        let monitor = WinUserApi.MonitorFromWindow(hwnd, MonitorFlags.MONITOR_DEFAULTTONEAREST)
        let item, start, revision = lock gate <| fun () ->
            let item = lane hwnd
            let revision = bump item
            let valid() = this.IsCurrent(hwnd,revision) && WinUserApi.IsWindow(hwnd) &&
                          Win32Helper.GetWindowProcessId(hwnd) = pid && Win32Helper.GetWindowThreadId(hwnd) = tid
            let work() =
                try
                    let mutable liveSource = RECT()
                    let sourceCurrent = WinUserApi.GetWindowRect(source, &liveSource) &&
                                        liveSource.Rect = sourceBounds && WinUserApi.IsZoomed(source) = maximized &&
                                        not (WinUserApi.IsIconic(source))
                    if valid() && not sourceCurrent then
                        // This may be the last LOCATIONCHANGE: the source can
                        // settle after the group snapshots it. Do not silently
                        // strand a still-current follower at its previous state.
                        // The group rereads live state and performs one bounded
                        // synchronous fallback; it does not repost this snapshot.
#if DEBUG
                        PerfTrace.count "group.follower.sourceChanged"
#endif
                        refused revision
                    elif valid() && sourceCurrent then
                        if WinUserApi.IsIconic(hwnd) || WinUserApi.GetDpiForWindow(hwnd) <> dpi ||
                           WinUserApi.MonitorFromWindow(hwnd, MonitorFlags.MONITOR_DEFAULTTONEAREST) <> monitor then
                            if valid() then refused revision
                        else
                            let mutable disabled = 1
                            PerfTrace.time "group.follower.transitions" <| fun () ->
                                DwmApi.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_TRANSITIONS_FORCEDISABLED, &disabled, sizeof<int>) |> ignore
                            try
                                let ok = Win32Helper.SetWindowMaximizedNoActivate(hwnd, maximized, bounds.RECT,
                                            Win32Helper.PlacementTiming(fun name ms -> PerfTrace.recordTime name ms),
                                            Win32Helper.PlacementCurrent(valid))
                                if valid() && ok && not (WinUserApi.IsIconic(hwnd)) then
                                    let mutable current = RECT()
                                    if WinUserApi.GetWindowRect(hwnd, &current) && current.Rect <> bounds then
                                        PerfTrace.time "group.follower.correctBounds" <| fun () ->
                                            WinUserApi.SetWindowPos(hwnd, IntPtr.Zero, bounds.x, bounds.y, bounds.width, bounds.height,
                                                SetWindowPosFlags.SWP_NOACTIVATE ||| SetWindowPosFlags.SWP_NOZORDER ||| SetWindowPosFlags.SWP_NOOWNERZORDER) |> ignore
                                elif valid() && not ok then refused revision
                            finally
                                // Calls above complete on the target thread before
                                // returning. Keep suppression until that completion.
                                if WinUserApi.IsWindow(hwnd) && Win32Helper.GetWindowProcessId(hwnd) = pid then
                                    let mutable enabled = 0
                                    DwmApi.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_TRANSITIONS_FORCEDISABLED, &enabled, sizeof<int>) |> ignore
                with _ -> if valid() then refused revision
            item.pending <- Some(revision,work)
            let start = not item.running
            item.running <- true
            item,start,revision
        if start then ThreadPool.QueueUserWorkItem(WaitCallback(fun _ -> drain item)) |> ignore
        revision
