namespace Bemo

open System.Runtime.InteropServices

// The production native operation is also used by the isolated-desktop probe.
module TopEdgeGuardPlacement =
    let private guards = System.Collections.Concurrent.ConcurrentDictionary<nativeint, byte>()
    let registerGuard hwnd = guards.[hwnd] <- 0uy
    let unregisterGuard hwnd = guards.TryRemove(hwnd) |> ignore
    let isGuard hwnd = guards.ContainsKey hwnd

    module private Native =
        [<Struct; StructLayout(LayoutKind.Sequential)>]
        type RECT =
            val mutable left: int
            val mutable top: int
            val mutable right: int
            val mutable bottom: int
#if DEBUG
        [<DllImport("user32.dll", EntryPoint = "GetWindowRect")>]
        extern bool TraceNative_GetWindowRect(nativeint hwnd, RECT& rect)
        let GetWindowRect(hwnd: nativeint, rect: byref<RECT>) =
            let call = Bemo.Win32.GroupCallTrace.Begin("GetWindowRect", hwnd)
            try TraceNative_GetWindowRect(hwnd, &rect)
            finally Bemo.Win32.GroupCallTrace.End(call)
#else
        [<DllImport("user32.dll")>]
        extern bool GetWindowRect(nativeint hwnd, RECT& rect)
#endif
#if DEBUG
        [<DllImport("user32.dll", EntryPoint = "GetDpiForWindow")>]
        extern uint32 TraceNative_GetDpiForWindow(nativeint hwnd)
        let GetDpiForWindow(hwnd: nativeint) =
            let call = Bemo.Win32.GroupCallTrace.Begin("GetDpiForWindow", hwnd)
            try TraceNative_GetDpiForWindow(hwnd)
            finally Bemo.Win32.GroupCallTrace.End(call)
#else
        [<DllImport("user32.dll")>]
        extern uint32 GetDpiForWindow(nativeint hwnd)
#endif
        [<DllImport("user32.dll")>]
        extern nativeint GetWindow(nativeint hwnd, uint32 command)
        [<DllImport("user32.dll")>]
        extern nativeint GetTopWindow(nativeint hwnd)
#if DEBUG
        [<DllImport("user32.dll", EntryPoint = "GetWindowLongW")>]
        extern int TraceNative_GetWindowLongW(nativeint hwnd, int index)
        let GetWindowLongW(hwnd: nativeint, index: int) =
            let call = Bemo.Win32.GroupCallTrace.Begin("GetWindowLongW", hwnd)
            try TraceNative_GetWindowLongW(hwnd, index)
            finally Bemo.Win32.GroupCallTrace.End(call)
#else
        [<DllImport("user32.dll")>]
        extern int GetWindowLongW(nativeint hwnd, int index)
#endif
        [<DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")>]
        extern nativeint SetOwner64(nativeint hwnd, int index, nativeint owner)
        [<DllImport("user32.dll", EntryPoint = "SetWindowLongW")>]
        extern nativeint SetOwner32(nativeint hwnd, int index, nativeint owner)
        [<DllImport("user32.dll")>]
        extern nativeint GetForegroundWindow()
        [<DllImport("user32.dll")>]
        extern bool IsWindowVisible(nativeint hwnd)
        [<DllImport("user32.dll")>]
        extern bool IsWindow(nativeint hwnd)
        [<DllImport("user32.dll")>]
        extern bool IsIconic(nativeint hwnd)
        [<DllImport("user32.dll")>]
        extern bool IsZoomed(nativeint hwnd)
        [<DllImport("user32.dll")>]
        extern bool ShowWindow(nativeint hwnd, int command)
        [<DllImport("user32.dll")>]
        extern bool SetWindowPos(nativeint hwnd, nativeint after, int x, int y, int width, int height, uint32 flags)

    let private dpiOf hwnd = try max 96 (int (Native.GetDpiForWindow hwnd)) with _ -> 96

    let visible hwnd = Native.IsWindowVisible hwnd
    let isTopMost hwnd = Native.GetWindowLongW(hwnd, -20) &&& 8 <> 0
    let above hwnd = Native.GetWindow(hwnd, 3u)
    let ownerOf hwnd = Native.GetWindow(hwnd, 4u)
    let rectOf hwnd : TopEdgeGuardPolicy.Band option =
        let mutable r = Native.RECT()
        if Native.GetWindowRect(hwnd, &r) then
            Some { x = r.left; y = r.top
                   width = r.right - r.left; height = r.bottom - r.top }
        else None

    // Bound the walk even if another process changes the chain while reading.
    // An incomplete observation is not evidence that the band is safe.
    let isAbove upper lower =
        let rec walk hwnd remaining =
            if hwnd = 0n || remaining = 0 then false
            elif hwnd = lower then true
            else walk (Native.GetWindow(hwnd, 2u)) (remaining - 1)
        upper <> 0n && lower <> 0n && upper <> lower &&
        walk (Native.GetWindow(upper, 2u)) 4096

    // Snapshots are taken on the window thread; only immutable text goes to
    // the writer. No titles, class names, executable names or exception text.
#if DEBUG
    module private FileTrace =
        let enabled = System.Environment.GetEnvironmentVariable("WINDOWTABS_DEBUG_GUARD_TRACE") = "1"
        let gate = obj()
        let pending = System.Collections.Generic.Queue<string>()
        let mutable writing = false
        let mutable sequence = 0L
        let mutable dropped = 0
        let append line =
            let start =
                lock gate (fun () ->
                    sequence <- sequence + 1L
                    if pending.Count < 4096 then
                        pending.Enqueue(sprintf "%d %s %s" sequence
                            (System.DateTime.UtcNow.ToString("O")) line)
                    else dropped <- dropped + 1
                    if writing then false
                    else
                        writing <- true
                        true)
            if start then
                System.Threading.ThreadPool.QueueUserWorkItem(fun _ ->
                    let mutable running = true
                    while running do
                        let batch =
                            lock gate (fun () ->
                                if pending.Count = 0 then
                                    writing <- false
                                    running <- false
                                    [||]
                                else
                                    let lines = pending.ToArray()
                                    pending.Clear()
                                    let lost = dropped
                                    dropped <- 0
                                    if lost = 0 then lines
                                    else Array.append [| sprintf "dropped=%d" lost |] lines)
                        if batch.Length > 0 then
                            try
                                let directory = System.IO.Path.Combine(
                                    System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "WindowTabs")
                                System.IO.Directory.CreateDirectory(directory) |> ignore
                                let path = System.IO.Path.Combine(directory, "guard_trace.log")
                                if System.IO.File.Exists(path) && System.IO.FileInfo(path).Length > 8388608L then
                                    let previous = path + ".1"
                                    if System.IO.File.Exists(previous) then System.IO.File.Delete(previous)
                                    System.IO.File.Move(path, previous)
                                System.IO.File.AppendAllLines(path, batch, System.Text.Encoding.UTF8)
                            with _ -> ()) |> ignore
#endif

    let traceDecision phase band strip owner (requested: TopEdgeGuardPolicy.Band option)
                      (anchor: nativeint option) (swp: bool option) =
#if DEBUG
        // Off unless WINDOWTABS_DEBUG_GUARD_TRACE=1: every decision walks the z-order.
        if FileTrace.enabled then
            try
                let ownerRect = rectOf owner
                let guardRect = rectOf band
                let foreground = Native.GetForegroundWindow()
                let bandRect = match requested with Some _ -> requested | None -> guardRect
                // One bounded front-to-back read supplies ranks and frame candidates.
                let rec walk hwnd rank rows =
                    if hwnd = 0n then true, List.rev rows
                    elif rank = 4096 then false, List.rev rows
                    else
                        let parent = ownerOf hwnd
                        let rows =
                            if hwnd = owner || hwnd = band || hwnd = strip || (owner <> 0n && parent = owner) then
                                let rect = rectOf hwnd
                                let covered, external =
                                    match bandRect, ownerRect, rect with
                                    | Some b, Some o, Some r ->
                                        TopEdgeGuardPolicy.frameInBand b r, TopEdgeGuardPolicy.topFrame (dpiOf owner) o b r
                                    | _ -> false, false
                                let row = sprintf "{hwnd=%X owner=%X rect=%A z=%d prev=%X next=%X visible=%b topmost=%b covered=%b external=%b}"
                                              (int64 hwnd) (int64 parent) rect rank (int64 (above hwnd))
                                              (int64 (Native.GetWindow(hwnd, 2u))) (visible hwnd) (isTopMost hwnd) covered external
                                row :: rows
                            else rows
                        walk (Native.GetWindow(hwnd, 2u)) (rank + 1) rows
                let complete, rows = walk (Native.GetTopWindow 0n) 0 []
                let line = sprintf "phase=%s owner=%X ownerRect=%A guard=%X strip=%X foreground=%X behind=%b aboveOwner=%X requested=%A insertAfter=%s swp=%A finalRect=%A visible=%b ownerValid=%b ownerVisible=%b iconic=%b zoomed=%b stripVisible=%b stripAboveGuard=%b complete=%b z=[%s]"
                               phase (int64 owner) ownerRect (int64 band) (int64 strip) (int64 foreground)
                               (owner = 0n || foreground <> owner) (int64 (above owner)) requested
                               (anchor |> Option.map (fun hwnd -> sprintf "0x%X" (int64 hwnd)) |> Option.defaultValue "not-called") swp guardRect (visible band)
                               (Native.IsWindow owner) (visible owner) (Native.IsIconic owner) (Native.IsZoomed owner)
                               (visible strip) (isAbove strip band) complete (System.String.Join(";", rows))
                FileTrace.append (line.Replace("\r", " ").Replace("\n", " "))
            with _ -> ()
#else
        ()
#endif

    let private setPosition band strip owner anchor x y width height flags =
        let ok = Native.SetWindowPos(band, anchor, x, y, width, height, flags)
        // Capture BEFORE validation can hide the guard or a queued repair runs.
#if DEBUG
        let requested : TopEdgeGuardPolicy.Band = { x = x; y = y; width = width; height = height }
        traceDecision (sprintf "SetWindowPos(flags=%X)" flags) band strip owner (Some requested) (Some anchor) (Some ok)
#endif
        ok

    type private StripOwnerControl = {
        thread: int
        choose: nativeint -> nativeint
        trace: string -> nativeint -> nativeint -> nativeint -> nativeint -> unit
    }
    let private stripOwners = System.Collections.Concurrent.ConcurrentDictionary<nativeint, StripOwnerControl>()

    // Both ordinary placement and guard repair use this writer. The policy
    // belongs to the strip's group thread; never call it from another thread.
    let registerStripOwner strip choose trace =
        stripOwners.[strip] <- {
            thread = System.Threading.Thread.CurrentThread.ManagedThreadId
            choose = choose; trace = trace }
    let unregisterStripOwner strip = stripOwners.TryRemove(strip) |> ignore
    let isRegisteredStrip strip = stripOwners.ContainsKey strip

    let reownStrip strip requested reason =
        let previous = ownerOf strip
        let control = match stripOwners.TryGetValue(strip) with true, c -> Some c | _ -> None
        let onOwnerThread = control |> Option.forall (fun c -> c.thread = System.Threading.Thread.CurrentThread.ManagedThreadId)
        let owner =
            if not onOwnerThread then previous
            else control |> Option.map (fun c -> c.choose requested) |> Option.defaultValue requested
        if onOwnerThread && Native.IsWindow strip && (owner = 0n || Native.IsWindow owner) && previous <> owner then
            if System.IntPtr.Size = 8 then Native.SetOwner64(strip, -8, owner) |> ignore
            else Native.SetOwner32(strip, -8, owner) |> ignore
        let actual = ownerOf strip
        if previous <> owner || requested <> owner || actual <> owner || not onOwnerThread then
            match control with
            | Some c -> c.trace (if onOwnerThread then reason else "wrong-thread:" + reason) previous requested owner actual
            | None ->
#if DEBUG
                traceDecision (sprintf "strip-reown(reason=%s,old=%X,new=%X,actual=%X)"
                    reason (int64 previous) (int64 owner) (int64 actual)) 0n strip owner None None None
#else
                ()
#endif
        // A guard whose request was suppressed must not subsequently reorder
        // the ownerless strip or demote its temporary topmost layer.
        onOwnerThread && actual = requested && owner = requested

    let repairStripAboveGuard band strip owner =
        if Native.GetForegroundWindow() = owner && Native.IsWindow strip then
            if visible strip && visible band && not (isAbove strip band) then
                // The group positions its strip above the frame. Keep the
                // guard below that strip without changing the strip's layer.
                setPosition band strip owner strip 0 0 0 0 0x0213u |> ignore

    let hide band =
        if visible band then Native.ShowWindow(band, 0) |> ignore

    let observe band strip owner rect : TopEdgeGuardPolicy.Observation =
        { ownerReady = Native.IsWindow(owner) && visible owner &&
                       not (Native.IsIconic owner) && not (Native.IsZoomed owner)
          stripReady = Native.IsWindow(strip) && visible strip
          stripAboveOwner = isAbove strip owner
          adjacent = above band = strip
          sameLayer = isTopMost band = isTopMost strip
          bandAboveOwner = isAbove band owner
          bandVisible = visible band
          sameRectangle = rectOf band = Some rect
          sameOwner = ownerOf band = owner
          stripTopmost = isTopMost strip }

    type Result = {
        decision: TopEdgeGuardPolicy.Placement
        before: TopEdgeGuardPolicy.Observation
        after: TopEdgeGuardPolicy.Observation
        placements: int; zorders: int; succeeded: bool
    }

    let place band strip owner keepOnTop rect =
        let before = observe band strip owner rect
        let decision = TopEdgeGuardPolicy.decide keepOnTop before
        let mutable placements = 0
        let mutable zorders = 0
        let mutable ok = true
        try
            if decision = TopEdgeGuardPolicy.Hidden then hide band
            else
                if not before.sameOwner then
                    hide band
                    if System.IntPtr.Size = 8 then Native.SetOwner64(band, -8, owner) |> ignore
                    else Native.SetOwner32(band, -8, owner) |> ignore
                // When strip is the last topmost window and band is already
                // adjacent, inserting after strip can be a native no-op that
                // leaves band non-topmost. Promote while HIDDEN, then insert:
                // the intermediate topmost position must never take tab input.
                if before.stripTopmost && not (isTopMost band) then
                    hide band
                    placements <- placements + 1
                    zorders <- zorders + 1
                    ok <- setPosition band strip owner -1n 0 0 0 0 0x0213u
                // Owner changes may reorder owned windows. Re-read before deciding.
                let current = observe band strip owner rect
                let action = TopEdgeGuardPolicy.decide keepOnTop current
                if not ok || action = TopEdgeGuardPolicy.Hidden || not current.sameOwner then
                    ok <- false
                    hide band
                elif action <> TopEdgeGuardPolicy.Unchanged then
                    let reorder = action = TopEdgeGuardPolicy.Inserted
                    placements <- placements + 1
                    zorders <- zorders + (if reorder then 1 else 0)
                    let flags = 0x0250u ||| (if reorder then 0u else 0x0004u)
                    ok <- setPosition band strip owner (if reorder then strip else 0n)
                                      rect.x rect.y rect.width rect.height flags
                // Never accept adjacency alone, or a successful API return alone.
                let final = observe band strip owner rect
                if not ok || TopEdgeGuardPolicy.decide keepOnTop final <> TopEdgeGuardPolicy.Unchanged then
                    ok <- false
                    hide band
        with _ ->
            hide band
            reraise()
        { decision = decision; before = before; after = observe band strip owner rect
          placements = placements; zorders = zorders; succeeded = ok }

    let marginOrder band strip owner =
        let rec walk hwnd remaining seen windows =
            if hwnd = 0n then true, List.rev windows
            elif remaining = 0 || Set.contains hwnd seen then false, List.rev windows
            else
                let w : TopEdgeGuardPolicy.OwnedWindow =
                    { hwnd = hwnd; owner = ownerOf hwnd; topmost = isTopMost hwnd }
                walk (Native.GetWindow(hwnd, 2u)) (remaining - 1) (Set.add hwnd seen) (w :: windows)
        let complete, windows = walk (Native.GetTopWindow 0n) 4096 Set.empty []
        TopEdgeGuardPolicy.marginOrder complete band strip owner windows

    let marginSafe band strip owner =
        marginOrder band strip owner = TopEdgeGuardPolicy.Ordered &&
        above band = strip && not (isTopMost band)

    // Only our strip and band are changed. Hide before moving either one:
    // intermediate positions must never let the band intercept tab input.
    let placeMargin band strip owner rect =
        let before = observe band strip owner rect
        let mutable moves = 0
        let mutable ok = before.ownerReady && before.stripReady
        try
            if ok then
                match marginOrder band strip owner with
                | TopEdgeGuardPolicy.Unproven -> ok <- false
                | TopEdgeGuardPolicy.Ordered -> ()
                | TopEdgeGuardPolicy.Before target ->
                    hide band
                    if isTopMost strip then
                        moves <- moves + 1
                        ok <- Native.SetWindowPos(strip, -2n, 0, 0, 0, 0, 0x0213u)
                    // Demotion can reorder owned windows; read the anchor again.
                    let previous = above target
                    let anchor = if previous = 0n || isTopMost previous then 0n else previous
                    if ok && previous <> strip then
                        moves <- moves + 1
                        ok <- Native.SetWindowPos(strip, anchor, 0, 0, 0, 0, 0x0213u)
            if not ok then hide band
            let result =
                if ok then place band strip owner false rect
                else { decision = TopEdgeGuardPolicy.Hidden; before = before
                       after = observe band strip owner rect
                       placements = 0; zorders = 0; succeeded = false }
            ok <- ok && result.succeeded && marginSafe band strip owner
            if not ok then hide band
            { result with before = before; after = observe band strip owner rect
                          placements = result.placements + moves
                          zorders = result.zorders + moves; succeeded = ok }
        with _ ->
            hide band
            reraise()

    // Discover external top frames independently of configured margins. Only
    // visible windows directly owned by this target participate; no class or
    // process names, and no messages are sent to another process.
    let coverOwnedTopFrames band strip owner rect =
        match rectOf owner with
        | None -> None
        | Some ownerRect ->
            let dpi = dpiOf owner
            let rect = TopEdgeGuardPolicy.clipHorizontal ownerRect 0 rect
            let rec walk hwnd remaining frames =
                if hwnd = 0n then Some (TopEdgeGuardPolicy.coverTopFrames dpi ownerRect rect frames)
                elif remaining = 0 then None
                else
                    let frames =
                        if hwnd <> band && hwnd <> strip && ownerOf hwnd = owner && visible hwnd then
                            match rectOf hwnd with
                            | Some frame -> frame :: frames
                            | None -> frames
                        else frames
                    walk (above hwnd) (remaining - 1) frames
            if rect.width <= 0 then None else walk (above owner) 4096 []

    // Find the highest covered frame in the whole order above the owner.
    // Insertion immediately before that frame preserves every window above it;
    // stopping at the first unrelated sibling could leave a higher frame exposed.
    let targetBelow band strip owner (rect: TopEdgeGuardPolicy.Band) =
        let rec walk hwnd remaining candidates =
            if hwnd = 0n then
                TopEdgeGuardPolicy.highestCoveredFrame owner (List.rev candidates)
            elif remaining = 0 then 0n
            else
                let covered =
                    hwnd <> band && hwnd <> strip && ownerOf hwnd = owner &&
                    visible hwnd && isTopMost hwnd = isTopMost owner &&
                    (match rectOf hwnd with
                     | Some frame -> TopEdgeGuardPolicy.frameInBand rect frame
                     | None -> false)
                walk (above hwnd) (remaining - 1) ((hwnd, covered) :: candidates)
        walk (above owner) 4096 []

    let foregroundOwner owner = owner <> 0n && Native.GetForegroundWindow() = owner

    let ownerSafe band strip owner =
        if not (Native.IsWindow owner) ||
           not (visible owner) || Native.IsIconic owner || Native.IsZoomed owner then false
        else
            let target =
                match rectOf band with
                | Some rect -> targetBelow band strip owner rect
                | None -> 0n
            // Foreground and strip order do not determine whether an exposed
            // background border is locked. Only the guard's own native order does.
            target <> 0n && ownerOf band = owner &&
            not (isTopMost band) && above target = band

    // A stale frame snapshot can fail adjacency after a successful insertion.
    // Keep that best position only while it cannot intercept another group's
    // window: every intervening window down to the owner must belong to it.
    let canKeepShown band owner =
        let rec walk hwnd remaining =
            if hwnd = owner then true
            elif hwnd = 0n || remaining = 0 || ownerOf hwnd <> owner then false
            else walk (Native.GetWindow(hwnd, 2u)) (remaining - 1)
        foregroundOwner owner && visible owner && visible band &&
        not (Native.IsIconic owner) && not (Native.IsZoomed owner) &&
        ownerOf band = owner && isTopMost band = isTopMost owner &&
        walk (Native.GetWindow(band, 2u)) 4096

    // The owner belongs to another process; GWLP_HWNDPARENT is written only on
    // our popup. Verify the relationship, since a zero API return is ambiguous.
    // Hide during owner/layer changes, and show only in the final insertion.
    let placeAtOwner band strip owner (rect: TopEdgeGuardPolicy.Band) =
        traceDecision "place-owner-enter" band strip owner (Some rect) None None
        let mutable ok = Native.IsWindow owner && visible owner &&
                         not (Native.IsIconic owner) && not (Native.IsZoomed owner)
        if ok && ownerSafe band strip owner && visible band && rectOf band = Some rect then
            traceDecision "place-owner-unchanged" band strip owner (Some rect) None None
            true
        else
            // Keep a previously shown band during a plain reorder. If the
            // native call loses a race, its safe previous position still locks
            // part of the border instead of disappearing for a retry cycle.
            if not ok || ownerOf band <> owner || isTopMost band then hide band
            if ok && ownerOf band <> owner then
                if System.IntPtr.Size = 8 then Native.SetOwner64(band, -8, owner) |> ignore
                else Native.SetOwner32(band, -8, owner) |> ignore
                ok <- ownerOf band = owner
            if ok && isTopMost band then
                ok <- setPosition band strip owner -2n 0 0 0 0 0x0213u
            let target = if ok then targetBelow band strip owner rect else 0n
#if DEBUG
            traceDecision (sprintf "selected-frame(target=%X)" (int64 target)) band strip owner (Some rect) None None
#endif
            if target = 0n then ok <- false
            if ok then
                let previous = above target
                let previous = if previous = band then above band else previous
                // HWND_TOP keeps an ordinary band below the topmost layer.
                let anchor = if isTopMost previous then 0n else previous
                ok <- setPosition band strip owner anchor rect.x rect.y rect.width rect.height 0x0250u
                ok <- ok && ownerSafe band strip owner && rectOf band = Some rect
                traceDecision (if ok then "validated" else "validation-failed") band strip owner (Some rect) (Some anchor) None
            if not ok && canKeepShown band owner then
                traceDecision "validation-deferred-keep-shown" band strip owner (Some rect) None None
                ok <- true
            if not ok then hide band
            ok

    // Used when relinquishing foreground, including the UWP compatibility layer.
    let hideAndDemote band =
        hide band
        if isTopMost band then
            setPosition band 0n (ownerOf band) -2n 0 0 0 0 0x0213u |> ignore

    let safeForOwner band strip owner keepTopmost =
        if not keepTopmost || not (foregroundOwner owner) then ownerSafe band strip owner
        else
            match rectOf band with
            | None -> false
            | Some rect ->
                let observed = observe band strip owner rect
                observed.ownerReady && observed.stripReady && observed.sameOwner &&
                observed.stripTopmost && TopEdgeGuardPolicy.safeOrder observed

    let placeForOwner band strip owner keepTopmost rect =
        // A stale UWP request must fall back to ordinary placement on focus loss.
        let keepTopmost = keepTopmost && foregroundOwner owner
        let ok =
            if not keepTopmost then placeAtOwner band strip owner rect
            elif foregroundOwner owner && isTopMost strip then
                // UWP frame composition needs the same layer as its strip.
                // Reuse its verified, non-activating insertion behind the strip.
                let result = place band strip owner true rect
                result.succeeded && safeForOwner band strip owner true
            else false
        if ok then repairStripAboveGuard band strip owner
        else hideAndDemote band
        traceDecision (if ok then "place-final-shown" else "place-final-hidden") band strip owner (Some rect) None None
        ok
