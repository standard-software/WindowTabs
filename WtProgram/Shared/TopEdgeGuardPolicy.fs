namespace Bemo

// Physical-pixel geometry and ordering decisions, independent of Win32.
module TopEdgeGuardPolicy =
    type Band = { x: int; y: int; width: int; height: int }
    // Group margins can extend horizontally beyond the native window. Keep
    // the intentional corner gap relative to the native edge, and clip right.
    let clipHorizontal (owner: Band) leftGap (band: Band) =
        let left = max band.x (owner.x + leftGap)
        let right = min (band.x + band.width) (owner.x + owner.width)
        { band with x = left; width = max 0 (right - left) }

    // Candidates arrive from nearest-owner to highest in z-order. Intervening
    // non-frame windows do not terminate the search for an owned top frame.
    let highestCoveredFrame owner (candidates: (nativeint * bool) list) =
        candidates |> List.fold (fun target (hwnd, covered) -> if covered then hwnd else target) owner

    // Only a thin owned frame contained in the guarded band can be covered.
    // Shared ownership alone must not let the guard cover a dialog or popup.
    let frameInBand (band: Band) (frame: Band) =
        frame.height > 0 && frame.y >= band.y &&
        frame.y + frame.height <= band.y + band.height &&
        frame.x <= band.x && frame.x + frame.width >= band.x + band.width

    // Use the same DPI-scaled thickness, span and overlap rules as the margin
    // detector. A narrow initial guard must still discover a six-pixel glow.
    let private frameMargin dpi (owner: Band) (frame: Band) =
        let bounds (r: Band) : WindowFrameMargin.Bounds =
            { x = r.x; y = r.y; width = r.width; height = r.height }
        WindowFrameMargin.detect dpi (bounds owner) [bounds frame]

    let topFrame dpi (owner: Band) (band: Band) (frame: Band) =
        (frameMargin dpi owner frame).top > 0 &&
        frame.x <= band.x && frame.x + frame.width >= band.x + band.width

    let otherEdgeFrame dpi (owner: Band) (band: Band) (frame: Band) =
        let margin = frameMargin dpi owner frame
        (margin.bottom > 0 && frame.x <= band.x && frame.x + frame.width >= band.x + band.width) ||
        margin.left > 0 || margin.right > 0

    let coverTopFrames dpi (owner: Band) (band: Band) (ownedFrames: Band list) =
        let top, bottom =
            ownedFrames
            |> List.filter (topFrame dpi owner band)
            |> List.fold (fun (top, bottom) frame ->
                min top frame.y, max bottom (frame.y + frame.height)) (band.y, band.y + band.height)
        { band with y = top; height = bottom - top }

    type Stacking = Topmost | LeaveTopmost | Keep | Hide | Behind of nativeint
    type StackingInput = {
        tabsInside: bool; keepOnTop: bool; marginTop: int
        bandIsTopMost: bool; strip: nativeint; stripIsTopMost: bool
        immediatelyBehind: bool
    }

    let wanted locked hasBounds moving inside fullscreen stripVisible =
        locked && hasBounds && not moving &&
        // Preserve upward availability; avoid new fullscreen input interception.
        (not inside || (not fullscreen && stripVisible))

    // What the band stands on (four-edge tabs, 7-3). A group with tabs on
    // the top edge has its top strip, drawn outside or inside, shown or not,
    // and nothing about it changes. A group whose tabs are all on other
    // edges has no top strip to show, and the band still guards the top
    // border while the lock is on - said here in so many words rather than
    // by calling the group's tabs "outside": there is no strip to stand
    // behind, so it is placed as it is over upward tabs, and a fullscreen
    // window, which upward tabs never face, is still never covered.
    type TopView =
        | TopStrip of inside: bool * visible: bool
        | NoTopTabs

    let wantedFor locked hasBounds moving fullscreen view =
        match view with
        | TopStrip(inside, visible) -> wanted locked hasBounds moving inside fullscreen visible
        | NoTopTabs -> locked && hasBounds && not moving && not fullscreen

    // Whether the band goes behind the top strip, inside the window.
    let behindStrip view =
        match view with
        | TopStrip(inside, _) -> inside
        | NoTopTabs -> false

    let canPlace wanted ownerValid minimized maximized inside stripValid =
        wanted && ownerValid && not minimized && not maximized &&
        (not inside || stripValid)

    let rectangle x y widthBeforeButtons marginTop borderHeight leftGap =
        { x = x + leftGap; y = y
          width = max 1 (widthBeforeButtons - leftGap)
          height = marginTop + borderHeight }

    let stacking i =
        if not i.tabsInside then
            // A margin is geometry, never permission to enter the topmost layer.
            if i.keepOnTop then Topmost
            elif i.bandIsTopMost then LeaveTopmost
            else Keep
        elif i.stripIsTopMost && not i.keepOnTop then
            // tabActivate may leave the strip topmost indefinitely: its timer
            // is immediately disposed. Never promote the band on that basis.
            Hide
        elif i.immediatelyBehind && i.bandIsTopMost = i.stripIsTopMost then Keep
        else Behind i.strip

    let needsPlacement shown sameRectangle sameOwner stacking =
        not shown || not sameRectangle || not sameOwner || stacking <> Keep

    // Evidence survives only until the next native observation. Eligibility is
    // retained across a native hide so batch-end display can request one retry.
    let stripChange eligible disposed shown visible safe =
        if not eligible || disposed then false, false
        elif shown then not (visible && safe), visible && not safe
        else false, visible

    // Keep native reads lazy: excluded groups must not walk the z-order.
    let stripNotification eligible disposed shown visible safe hide =
        if not eligible || disposed then false
        else
            let visible = visible()
            let safe = shown && visible && safe()
            let hideNow, repair = stripChange eligible disposed shown visible safe
            if hideNow then hide()
            repair

    let followsStrip inside locked hasBounds moving fullscreen =
        inside && locked && hasBounds && not moving && not fullscreen

    // Shared by the decorator and the native message-loop fixture.
    let dispatchStripMessage message eligible changed queue =
        if message = 0x0047 && eligible && changed() then queue()

    type Placement = Hidden | Unchanged | Moved | Inserted
    type OwnedWindow = { hwnd: nativeint; owner: nativeint; topmost: bool }
    type MarginOrder = Unproven | Ordered | Before of nativeint

    // The snapshot is front-to-back and complete. The first sibling is ahead
    // of every other frame, so placing before it covers multiple frame windows.
    // `others`: the group's strips of the other edges. They share the owner
    // with the top strip and are not margin frames.
    let marginOrderAmong complete band strip (others: nativeint list) owner (windows: OwnedWindow list) =
        let peers = windows |> List.filter (fun w ->
            w.hwnd <> band && w.hwnd <> strip && not (List.contains w.hwnd others) &&
            (w.hwnd = owner || w.owner = owner))
        let stripWindow = windows |> List.tryFind (fun w -> w.hwnd = strip)
        if not complete || not (windows |> List.exists (fun w -> w.hwnd = owner)) ||
           stripWindow.IsNone || stripWindow.Value.owner <> owner ||
           (peers |> List.exists (fun w -> w.topmost)) then Unproven
        else
            match peers with
            | [] -> Unproven
            | first :: _ ->
                let index hwnd = windows |> List.findIndex (fun w -> w.hwnd = hwnd)
                if not stripWindow.Value.topmost && index strip < index first.hwnd then Ordered
                else Before first.hwnd

    let marginOrder complete band strip owner (windows: OwnedWindow list) =
        marginOrderAmong complete band strip [] owner windows

    let useMarginOrder inside marginTop = inside && marginTop > 0
    let temporaryStripTopmost inside uwp hasMargin locked =
        inside && not uwp && not (hasMargin && locked)

    type Observation = {
        ownerReady: bool; stripReady: bool; stripAboveOwner: bool
        adjacent: bool; sameLayer: bool; bandAboveOwner: bool
        bandVisible: bool; sameRectangle: bool; sameOwner: bool
        stripTopmost: bool
    }
    let safeOrder o = o.adjacent && o.sameLayer && o.bandAboveOwner
    type HiddenReason = OwnerUnavailable | StripUnavailable | OwnerOrderUnproven | StripRaisedForSwitch
    let hiddenReason keepOnTop o =
        if not o.ownerReady then Some OwnerUnavailable
        elif not o.stripReady then Some StripUnavailable
        elif not o.stripAboveOwner then Some OwnerOrderUnproven
        elif o.stripTopmost && not keepOnTop then Some StripRaisedForSwitch
        else None
    let decide keepOnTop o =
        if (hiddenReason keepOnTop o).IsSome then Hidden
        elif not (safeOrder o) then Inserted
        elif o.bandVisible && o.sameRectangle && o.sameOwner then Unchanged
        else Moved

    // Invisible windows cannot intercept input. Keep every visible sibling as
    // a barrier, regardless of ownership or overlap. Exhaustion is not proof.
    let visiblePredecessor previous visible start =
        let rec walk hwnd remaining =
            if hwnd = 0n then Some 0n
            elif remaining = 0 then None
            elif visible hwnd then Some hwnd
            else walk (previous hwnd) (remaining - 1)
        walk (previous start) 4096

    // Unknown geometry remains a barrier; only proven empty rectangles vanish.
    let blocksInput visible (rect: Band option) =
        visible && (rect |> Option.forall (fun r -> r.width > 0 && r.height > 0))

    // Never insert after a topmost HWND. HWND_TOP is safe only when no ordinary
    // input window lies above that predecessor. Interleaved layers fail closed.
    let ownerInsertion band predecessor previous blocks topmost =
        match predecessor with
        | None -> None
        | Some hwnd when hwnd = band -> Some(0n, true)
        | Some hwnd when hwnd = 0n || not (topmost hwnd) -> Some(hwnd, false)
        | Some hwnd ->
            let rec allTopmost current remaining =
                if current = 0n then true
                elif remaining = 0 then false
                elif current <> band && blocks current && not (topmost current) then false
                else allTopmost (previous current) (remaining - 1)
            if allTopmost (previous hwnd) 4096 then Some(0n, false) else None

    // A failed staging call or validation can never reach the show operation.
    let placeVerified wasVisible stage validate show =
        if stage() && validate() then
            if wasVisible then true else show() && validate()
        else false

    type HiddenRetry<'Key when 'Key: equality>() =
        let mutable failure : ('Key * int64) option = None
        member this.Ready now = failure |> Option.forall (fun (_, time) -> now - time >= 10000L)
        member this.Allow(now, key) =
            failure |> Option.forall (fun (old, time) -> old <> key || now - time >= 10000L)
        member this.Fail(now, key) = failure <- Some(key, now)
        member this.Reset() = failure <- None

    let positionFlags visible sameOrder =
        0x0210u ||| (if visible then 0u else 0x0040u) |||
        (if sameOrder then 0x0004u else 0u)

    // Compare state before formatting diagnostics or taking extra native reads.
    type ChangeTrace<'Key when 'Key: equality>() =
        let mutable last = None
        member this.Write(key: 'Key, build: unit -> string, write: string -> unit) =
            try
                if last <> Some key then
                    last <- Some key
                    let line = build()
                    write line
            with _ -> ()

    // Coalesce pending requests, but retain changes received during execution.
    // The production post is asynchronous, so follow-ups do not grow the stack.
    type UpdateQueue() =
        let mutable pending = false
        let mutable running = false
        let mutable again = false
        let mutable disposed = false
        member this.Request(post: (unit -> unit) -> unit, update: unit -> unit) =
            if running && not disposed then again <- true
            elif not pending && not disposed then
                pending <- true
                try
                    post (fun () ->
                        running <- true
                        try if not disposed then update()
                        finally
                            running <- false
                            pending <- false
                            let retry = again && not disposed
                            again <- false
                            if retry then this.Request(post, update))
                with _ -> pending <- false; reraise()
        member this.Dispose() = disposed <- true
