namespace Bemo

// Fallback for "lock window position" (LockWindowPosition) when the input
// hook could not stop a drag before it started: a custom title bar that
// answers HTCLIENT, a drag bar that is a child window with a caption of its
// own, a hit test that timed out on a busy window, an app that starts the
// system move loop from its own code. The hook lets such a press through and
// records it (CaptionDragTargets.Press); if the tabbed window then enters a
// move/size loop (EVENT_SYSTEM_MOVESIZESTART), WindowGroup puts the window
// back on every rectangle change, as the observe-and-restore branch does.
// That is level 2 of the requirement (returns during the drag) instead of
// level 1, and only for what the hook missed.
//
// No Win32 and no UI here; Tools/CaptionDragFallback.Tests.fsx exercises it.
//
// Differences from a stand-alone observe-and-restore lock, all because the
// hook saw the press:
//  - The rectangle and the point are read at the moment of the press, before
//    the target received it, not when the START event is finally delivered.
//    A fast flick cannot slip through and the anchor does not drift.
//  - The hit-test answer belongs to the press point, not to wherever the
//    cursor has travelled by the time START arrives.
//  - The settle time after MOVESIZEEND ends at the next mouse press, so a
//    WindowTabs menu command (which needs a click) is never undone.
module CaptionDragFallback =

    // A window rectangle in screen pixels. Not Bemo.Rect: that type lives
    // with the Win32 layer, and this module must load in fsi on its own.
    type Box =
        {
            x: int
            y: int
            width: int
            height: int
        }
        member this.right = this.x + this.width
        member this.bottom = this.y + this.height

    let box x y width height = { x = x; y = y; width = width; height = height }

    let isDegenerate (b: Box) = b.width <= 0 || b.height <= 0

    let sameSize (a: Box) (b: Box) = a.width = b.width && a.height = b.height

    // What the press grabbed.
    type Grab =
        // The top-level window's caption. The hook normally swallows these;
        // one only reaches here through a race with group membership.
        | CaptionGrab
        // A border or corner answer; actual size changes decide resizing.
        | BorderGrab
        // Not known (client area, child window, no answer). The rectangles
        // decide (see decideFromGeometry).
        | UnknownGrab

    let hitCaption = 2
    let hitGrowBox = 4 // HTSIZE
    let hitLeft = 10 // HTLEFT..HTBOTTOMRIGHT are the sizing borders
    let hitBottomRight = 17

    let isSizingHit code = code = hitGrowBox || (code >= hitLeft && code <= hitBottomRight)

    // Width, in pixels, of the strip along a window edge where a border grab
    // can happen: about 8 px at 96 DPI, doubled for tolerance, scaled by DPI.
    let edgeBand (dpi: int) =
        let dpi = if dpi <= 0 then 96 else dpi
        max 1 (16 * dpi / 96)

    // Classify the recorded press. rootHit is the top-level window's own
    // answer (DPI-corrected by the hook), None when only a child answered or
    // nothing did.
    //  - A sizing answer near an edge is a border grab.
    //  - A caption answer inside the window, away from the side and bottom
    //    bands, is a caption grab.
    //  - Any other press inside the left, right or bottom band is treated as
    //    a border grab. A custom frame that answered HTCLIENT there is most
    //    likely resizing, and a resize that keeps its centre (which geometry
    //    would take for a move) must not be undone. The top band is left to
    //    geometry: custom title bars sit right under the top edge.
    let grabOfPress (rootHit: int option) (bounds: Box) (pressX: int) (pressY: int) (band: int) =
        let inside =
            pressX >= bounds.x && pressX < bounds.right &&
            pressY >= bounds.y && pressY < bounds.bottom
        let nearSideOrBottom =
            pressX - bounds.x < band ||
            bounds.right - 1 - pressX < band ||
            bounds.bottom - 1 - pressY < band
        let nearAnyEdge = nearSideOrBottom || pressY - bounds.y < band
        match rootHit with
        | Some code when inside && isSizingHit code && nearAnyEdge -> BorderGrab
        | Some code when inside && code = hitCaption && not nearSideOrBottom -> CaptionGrab
        | _ when inside && nearSideOrBottom -> BorderGrab
        | _ -> UnknownGrab

    // Does the recorded press explain the move/size loop that just started?
    //  - Button still held: yes. A newer press elsewhere would have replaced
    //    the record, so this is the press being held.
    //  - Released already: only if the window has left the rectangle it had
    //    at the press (a flick that finished before START was delivered) and
    //    the press is recent. A keyboard move (Alt+Space) starts with the
    //    rectangle untouched, so it is never taken for a drag.
    let recentPressMilliseconds = 2000

    let pressApplies (buttonHeld: bool) (millisecondsSincePress: int) (pressBounds: Box) (current: Box) =
        buttonHeld ||
        (millisecondsSincePress >= 0 && millisecondsSincePress <= recentPressMilliseconds &&
         current <> pressBounds)

    // Latched for the rest of the drag: one system loop is a move or a resize
    // from beginning to end.
    type Verdict =
        | Undecided
        | Moving
        | Resizing

    type Session =
        {
            anchor: Box
            grab: Grab
            verdict: Verdict
            // None while the loop runs. After MOVESIZEEND a session that had
            // to undo a move stays armed until this tick (ms), because the
            // shell can apply an Aero Snap after it reports the end.
            settleUntil: int64 option
        }

    type Correction =
        | NoCorrection
        | RestorePosition of int * int
        // Size changed too (snap, DPI change): put the whole rectangle back.
        | RestoreBounds of Box

    let settleMilliseconds = 400L

    let start (grab: Grab) (anchor: Box) : Session option =
        match grab with
        | _ when isDegenerate anchor -> None
        | _ -> Some { anchor = anchor; grab = grab; verdict = Undecided; settleUntil = None }

    // On each axis at least one edge stayed where it was: what a border or
    // corner drag does. This is also the left/top border decision the
    // requirement leaves to the implementer: x or y may change, because the
    // edge the user is not holding stays put.
    let isEdgeAnchored (anchor: Box) (current: Box) =
        let horizontal = anchor.x = current.x || anchor.right = current.right
        let vertical = anchor.y = current.y || anchor.bottom = current.bottom
        horizontal && vertical

    // Verdict for UnknownGrab from the first rectangle that differs.
    // Known weak spot: a snap that keeps one edge per axis (a window at the
    // work-area origin snapped to the left half) as the first change counts
    // as a resize. A real drag reports small moves long before that.
    let decideFromGeometry (anchor: Box) (current: Box) =
        if sameSize anchor current then Moving
        elif isEdgeAnchored anchor current then Resizing
        else Moving

    let private correctionFor (anchor: Box) (current: Box) =
        if sameSize anchor current then RestorePosition(anchor.x, anchor.y)
        else RestoreBounds anchor

    // One observation. isNormal is false once the window is minimized,
    // maximized or gone. newerPress is true when a mouse button went down
    // after the press that armed the session.
    let observe (nowMs: int64) (isNormal: bool) (newerPress: bool) (session: Session) (current: Box) : Session option * Correction =
        let settled =
            match session.settleUntil with
            // The settle time exists for a snap the shell applies right after
            // the loop; a new click means the user has moved on, and whatever
            // it does (a WindowTabs menu move, another app) must stand.
            | Some(deadline) -> nowMs > deadline || newerPress
            | None -> false
        if settled then None, NoCorrection
        // A show-state change ends the lock instead of being fought: the
        // saved normal rectangle means nothing to a maximized or minimized
        // window, and the state was requested some other way.
        elif not isNormal || isDegenerate current then None, NoCorrection
        elif current = session.anchor then Some session, NoCorrection
        else
            let verdict =
                match session.verdict with
                | Undecided ->
                    match session.grab with
                    | CaptionGrab -> Moving
                    // Custom drag surfaces can answer a sizing hit. Only a
                    // same-size translation is blocked; all actual resizes pass.
                    | BorderGrab -> if sameSize session.anchor current then Moving else Resizing
                    | _ -> decideFromGeometry session.anchor current
                | decided -> decided
            match verdict with
            | Moving -> Some { session with verdict = Moving }, correctionFor session.anchor current
            | _ -> None, NoCorrection

    // The loop ended. A session that never corrected anything (a click, the
    // first half of a double-click) leaves nothing behind.
    let finish (nowMs: int64) (session: Session) : Session option =
        match session.verdict, session.settleUntil with
        | Moving, None -> Some { session with settleUntil = Some(nowMs + settleMilliseconds) }
        | Moving, Some(_) -> Some session
        | _ -> None
