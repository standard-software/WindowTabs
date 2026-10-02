namespace Bemo

open System.Runtime.InteropServices

// The production native operation is also used by the isolated-desktop probe.
module TopEdgeGuardPlacement =
    module private Native =
        [<Struct; StructLayout(LayoutKind.Sequential)>]
        type RECT =
            val mutable left: int
            val mutable top: int
            val mutable right: int
            val mutable bottom: int
        [<DllImport("user32.dll")>]
        extern bool GetWindowRect(nativeint hwnd, RECT& rect)
        [<DllImport("user32.dll")>]
        extern nativeint GetWindow(nativeint hwnd, uint32 command)
        [<DllImport("user32.dll")>]
        extern nativeint GetTopWindow(nativeint hwnd)
        [<DllImport("user32.dll")>]
        extern int GetWindowLongW(nativeint hwnd, int index)
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
                    ok <- Native.SetWindowPos(band, -1n, 0, 0, 0, 0, 0x0213u)
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
                    ok <- Native.SetWindowPos(band, (if reorder then strip else 0n),
                                             rect.x, rect.y, rect.width, rect.height, flags)
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

    // A frame owned by the target may occupy the margin outside its rectangle.
    // Cover only the contiguous run of its owned frames. Never cross the strip
    // or ANY unrelated window, even if that window did not take foreground.
    let targetBelow band strip owner (rect: TopEdgeGuardPolicy.Band) =
        let rec walk target remaining =
            if remaining = 0 then 0n
            else
                let previous = above target
                let previous = if previous = band then above band else previous
                let isFrame =
                    match rectOf previous with
                    | Some r -> TopEdgeGuardPolicy.frameInBand rect r
                    | None -> false
                if previous <> 0n && previous <> strip && ownerOf previous = owner &&
                   isFrame && isTopMost previous = isTopMost owner then walk previous (remaining - 1)
                else target
        walk owner 4096

    let foregroundOwner owner = owner <> 0n && Native.GetForegroundWindow() = owner

    let ownerSafe band strip owner =
        if not (foregroundOwner owner) || not (Native.IsWindow owner) ||
           not (visible owner) || Native.IsIconic owner || Native.IsZoomed owner then false
        else
            let target =
                match rectOf band with
                | Some rect -> targetBelow band strip owner rect
                | None -> 0n
            target <> 0n && ownerOf band = owner &&
            isTopMost band = isTopMost owner && above target = band &&
            (not (visible strip) || isAbove strip band)

    // The owner belongs to another process; GWLP_HWNDPARENT is written only on
    // our popup. Verify the relationship, since a zero API return is ambiguous.
    // Hide during owner/layer changes, and show only in the final insertion.
    let placeAtOwner band strip owner (rect: TopEdgeGuardPolicy.Band) =
        let mutable ok = foregroundOwner owner && Native.IsWindow owner && visible owner &&
                         not (Native.IsIconic owner) && not (Native.IsZoomed owner)
        if ok && ownerSafe band strip owner && visible band && rectOf band = Some rect then true
        else
            hide band
            if ok && ownerOf band <> owner then
                if System.IntPtr.Size = 8 then Native.SetOwner64(band, -8, owner) |> ignore
                else Native.SetOwner32(band, -8, owner) |> ignore
                ok <- ownerOf band = owner
            if ok && isTopMost band <> isTopMost owner then
                let layer = if isTopMost owner then -1n else -2n
                ok <- Native.SetWindowPos(band, layer, 0, 0, 0, 0, 0x0213u)
            let target = if ok then targetBelow band strip owner rect else 0n
            if target = 0n then ok <- false
            if ok then
                let previous = above target
                let previous = if previous = band then above band else previous
                // HWND_TOP keeps an ordinary band below the topmost layer.
                let anchor = if not (isTopMost owner) && isTopMost previous then 0n else previous
                ok <- Native.SetWindowPos(band, anchor, rect.x, rect.y, rect.width, rect.height, 0x0250u)
                ok <- ok && ownerSafe band strip owner && rectOf band = Some rect
            if not ok then hide band
            ok

    // Used when relinquishing foreground, including the UWP compatibility layer.
    let hideAndDemote band =
        hide band
        if isTopMost band then
            Native.SetWindowPos(band, -2n, 0, 0, 0, 0, 0x0213u) |> ignore

    let safeForOwner band strip owner keepTopmost =
        if not keepTopmost then ownerSafe band strip owner
        elif not (foregroundOwner owner) then false
        else
            match rectOf band with
            | None -> false
            | Some rect ->
                let observed = observe band strip owner rect
                observed.ownerReady && observed.stripReady && observed.sameOwner &&
                observed.stripTopmost && TopEdgeGuardPolicy.safeOrder observed

    let placeForOwner band strip owner keepTopmost rect =
        let ok =
            if not keepTopmost then placeAtOwner band strip owner rect
            elif foregroundOwner owner && isTopMost strip then
                // UWP frame composition needs the same layer as its strip.
                // Reuse its verified, non-activating insertion behind the strip.
                let result = place band strip owner true rect
                result.succeeded && safeForOwner band strip owner true
            else false
        if not ok then hideAndDemote band
        ok
