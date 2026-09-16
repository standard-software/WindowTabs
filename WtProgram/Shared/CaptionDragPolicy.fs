namespace Bemo

// Input intent, not rectangle shape, distinguishes moving from resizing.
// Left/top borders and all corners pass through, including position changes:
// fixing their origin would change the resize gesture the user requested.
// Every decision the input hook makes is here, free of Win32, so that
// Tools/CaptionDragPolicy.Tests.fsx can exercise it in fsi.
module CaptionDragPolicy =
    // x/y are the physical cursor position (double-click distance). receiver
    // is the window that answered HTCAPTION - the root, or a child drag bar -
    // and hitX/hitY the same point in that window's own coordinate space,
    // which is what a message posted to it must carry.
    type Click = { target: int64; receiver: int64; x: int; y: int; hitX: int; hitY: int; time: uint32 }
    type State = { pendingUp: bool; previous: Click option }
    type Input = Down of Click option | Up | Other | Disable
    type Action = Pass | Block | Activate of int64 | DoubleClick of Click
    let empty = { pendingUp = false; previous = None }

    let hitTransparent = -1
    let hitCaption = 2
    // HTTOP, HTTOPLEFT, HTTOPRIGHT. Dragging any of these resizes the window by
    // its top edge, which is right where the tab strip sits: aiming at a tab
    // and missing by a pixel resized the window instead. The setting that stops
    // the window being moved stops this too. The left, right and bottom borders
    // resize as before - they never overlap the tabs.
    let hitTop = 12
    let hitTopLeft = 13
    let hitTopRight = 14
    let isTopBorder (hit: int) = hit = hitTop || hit = hitTopLeft || hit = hitTopRight

    /// Off: the invisible band over the top border (TopEdgeGuard) takes the
    /// press before the window ever sees it, and it keeps the plain arrow
    /// cursor while it is there - which swallowing the press in the hook never
    /// could. Swallowing as well would only take the top-left corner, the one
    /// place the band deliberately leaves free, away from the user. The code
    /// stays for the case where the band cannot be shown.
    let mutable blockTopBorderPress = false

    // HTSIZE, and HTLEFT..HTBOTTOMRIGHT: the borders and corners that start a
    // resize.
    let hitGrowBox = 4
    let hitLeft = 10
    let hitBottomRight = 17
    let isSizingHit (hit: int) = hit = hitGrowBox || (hit >= hitLeft && hit <= hitBottomRight)

    /// A press the hook passed, on a resize border or corner of a managed
    /// window. The window's own move/size loop follows and runs until the
    /// button comes back up, and the hook has nothing left to decide in
    /// between: it only watches for a press. Every mouse move of the drag
    /// would still detour through this process, and that detour is what makes
    /// a resize of a tabbed window lag. The hook comes off for the rest of the
    /// drag and goes back on when the button is released, which a poll finds -
    /// the release itself is no longer seen.
    ///
    /// Waiting for the system's MOVESIZESTART instead was tried and was too
    /// late to make a difference: it arrives on a group's thread, several
    /// frames into the drag.
    let mutable suspendHookOnResizePress = true

    /// swallowed: the press was taken for a caption drag, and its release is
    /// still wanted. rootHit: what the top-level window answered.
    let suspendsHook (swallowed: bool) (rootHit: int option) =
        suspendHookOnResizePress && not swallowed &&
        (match rootHit with Some code -> isSizingHit code | None -> false)

    // Only used for a standard non-client title bar. A custom frame
    // may deliberately resize inside its reported caption, so callers must
    // validate native rendering, DPI awareness and the geometry first.
    type CaptionRect = { left: int; top: int; right: int; bottom: int }

    let nativeCaptionBoundary hit supported titleVisible
                                (window: CaptionRect) (title: CaptionRect)
                                clientTop (buttons: CaptionRect) x y =
        let valid (r: CaptionRect) = r.left < r.right && r.top < r.bottom
        hit = 12 && supported && titleVisible &&
        valid window && valid title && valid buttons &&
        window.top < title.top && title.bottom = clientTop &&
        title.bottom < window.bottom &&
        window.left <= title.left && title.right <= window.right &&
        window.left <= buttons.left && buttons.right <= window.right &&
        window.top <= buttons.top && buttons.bottom <= window.bottom &&
        title.left <= x && x < title.right && title.top <= y && y < title.bottom &&
        // Exclude the entire horizontal span of the native caption buttons,
        // not just their vertical bounds, including RTL caption layouts.
        (x < buttons.left || x >= buttons.right)

    let step doubleClickTime halfWidth halfHeight state input =
        match input with
        | Disable -> { state with previous = None }, Pass
        | Other -> state, Pass
        | Up -> { state with pendingUp = false }, (if state.pendingUp then Block else Pass)
        | Down None -> empty, Pass
        | Down (Some click) ->
            let doubleClick =
                not state.pendingUp &&
                (state.previous |> Option.exists (fun previous ->
                    previous.target = click.target &&
                    uint32 (click.time - previous.time) <= doubleClickTime &&
                    abs (int64 click.x - int64 previous.x) <= int64 halfWidth &&
                    abs (int64 click.y - int64 previous.y) <= int64 halfHeight))
            { pendingUp = true; previous = if doubleClick then None else Some click },
            (if doubleClick then DoubleClick click else Activate click.target)

    // MAKELPARAM(x, y). The receiver reads each half as a signed 16-bit value,
    // so monitors left of or above the primary one (negative coordinates)
    // survive the packing.
    let packPoint (x: int) (y: int) = int ((uint32 (y &&& 0xffff) <<< 16) ||| uint32 (x &&& 0xffff))

    // DPI_AWARENESS values returned by GetAwarenessFromDpiAwarenessContext.
    let awarenessUnaware = 0
    let awarenessSystem = 1
    let awarenessPerMonitor = 2

    // Which point to put in WM_NCHITTEST. The hook reads the cursor in
    // physical pixels (WindowTabs is PerMonitorV2), and another process's
    // WM_NCHITTEST is not translated on the way in. A per-monitor aware
    // window works in physical pixels already. A DPI-unaware or system-aware
    // window reads the lParam as its own logical coordinates, so the point
    // must be converted first (PhysicalToLogicalPointForPerMonitorDPI);
    // sending it unconverted can make a client-area point land on the
    // caption. Unknown awareness, or a failed conversion, means no hit test:
    // the click passes through untouched.
    let hitTestPoint (awareness: int) (physical: int * int) (converted: (int * int) option) =
        if awareness = awarenessPerMonitor then Some physical
        elif awareness = awarenessUnaware || awareness = awarenessSystem then converted
        else None

    // Second guard for DPI-virtualized windows only: their caption is a
    // standard non-client caption, so a caption answer for a point inside the
    // client area means the coordinates did not line up, and the click must
    // pass. Per-monitor aware windows are exempt: custom frames (Chrome,
    // Terminal) legitimately answer HTCAPTION inside their client area, and
    // they get exact coordinates anyway.
    let trustCaption (awareness: int) (physicalY: int) (clientTopPhysicalY: int option) =
        awareness = awarenessPerMonitor ||
        (clientTopPhysicalY |> Option.exists (fun top -> physicalY < top))

    // One window's answer while walking from the window under the cursor up
    // to its top-level window.
    type HitStep =
        // Swallow the press: this is a caption of the managed window.
        | CaptionHit
        // HTTRANSPARENT: the system would ask the parent next.
        | Climb
        // Anything else decides the press, and it passes.
        | OtherHit of int

    // A child that answers HTCAPTION is a drag surface of its top-level window
    // (Windows Terminal's drag bar, VS Code's title bar), unless it has a
    // caption of its own: an MDI child's title bar moves the child inside its
    // parent, which is not the tabbed window's position. HTTRANSPARENT is
    // followed only to a parent on the same thread, as the system itself does.
    //
    // `hostsControls` is the exception: Office draws the search box, the
    // file-name menu and the account button inside one child (NetUIHWND) that
    // answers HTCAPTION for all of it, so swallowing the press took those
    // clicks away with the drag. Such a press goes through and the fallback
    // puts the window back if it starts moving - level 2 instead of level 1,
    // for that band only.
    let hitStep (isRoot: bool) (hasOwnCaption: bool) (hostsControls: bool) (parentOnSameThread: bool) (hit: int) =
        if hit = hitCaption then
            if isRoot then CaptionHit
            elif hasOwnCaption || hostsControls then OtherHit hit
            else CaptionHit
        elif hit = hitTransparent && not isRoot && parentOnSameThread then Climb
        else OtherHit hit

    // Window classes whose caption band is full of controls (see hitStep).
    // Office's ribbon/title host is the one case seen so far.
    let captionHostsControls (className: string) =
        className = "NetUIHWND"

    // What the hook thread should do with its hook and timer.
    type HookPlan =
        | Exit
        // ON: a hook is wanted; install one if missing.
        | Install
        // ON with a live hook: only the health check timer runs.
        | Keep
        // OFF, but a swallowed press still waits for its release.
        | WaitForRelease
        // OFF: no hook, no timer.
        | Remove

    let plan (stopping: bool) (enabled: bool) (installed: bool) (pendingUp: bool) (primaryHeld: bool) =
        if stopping then Exit
        elif enabled then (if installed then Keep else Install)
        elif pendingUp && primaryHeld then WaitForRelease
        else Remove

    // The hook sees logical button messages, but GetAsyncKeyState reads the
    // physical buttons: with swapped buttons the primary one is VK_RBUTTON.
    let primaryButtonKey (swapped: bool) = if swapped then 2 else 1

    // Windows silently removes a low-level hook that exceeds
    // LowLevelHooksTimeout, and keeps the handle valid. A cursor that moved
    // while no callback arrived for a while is the observable symptom.
    let hookLooksLost (millisecondsSinceCallback: int) (cursorMovedSinceCallback: bool) =
        cursorMovedSinceCallback && millisecondsSinceCallback >= 1000

    // Alternative to posting WM_NCLBUTTONDBLCLK, for windows that ignore a
    // double-click without the button-down/up before it: send the command
    // DefWindowProc would have chosen. None: the window cannot maximize.
    let scMaximize = 0xF030
    let scRestore = 0xF120
    let doubleClickCommand (zoomed: bool) (hasMaximizeBox: bool) =
        if zoomed then Some scRestore
        elif hasMaximizeBox then Some scMaximize
        else None
