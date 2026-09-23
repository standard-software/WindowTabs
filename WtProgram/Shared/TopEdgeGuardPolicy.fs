namespace Bemo

// Physical-pixel geometry and ordering decisions, independent of Win32.
module TopEdgeGuardPolicy =
    type Band = { x: int; y: int; width: int; height: int }
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

    let canPlace wanted ownerValid minimized maximized inside stripValid =
        wanted && ownerValid && not minimized && not maximized &&
        (not inside || stripValid)

    let rectangle x y widthBeforeButtons marginTop borderHeight leftGap =
        { x = x + leftGap; y = y
          width = max 1 (widthBeforeButtons - leftGap)
          height = marginTop + borderHeight }

    let stacking i =
        if not i.tabsInside then
            // The upward branch retains its existing placement rules.
            if i.keepOnTop || i.marginTop > 0 then Topmost
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
    let marginOrder complete band strip owner (windows: OwnedWindow list) =
        let peers = windows |> List.filter (fun w ->
            w.hwnd <> band && w.hwnd <> strip && (w.hwnd = owner || w.owner = owner))
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

    let useMarginOrder inside marginTop = inside && marginTop > 0
    let temporaryStripTopmost inside uwp hasMargin = inside && not uwp && not hasMargin

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

    // Compare state before formatting diagnostics or taking extra native reads.
    type ChangeTrace<'Key when 'Key: equality>() =
        let mutable last = None
        member this.Write(key: 'Key, build: unit -> string, write: string -> unit) =
            if last <> Some key then
                last <- Some key
                write (build())

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
