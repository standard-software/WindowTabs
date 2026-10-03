namespace Bemo
open System

// A sliver of a window laid over the top border of a tabbed window while
// "prevent moving windows and resizing them from the top" is on.
//
// The hook (CaptionDragPlugin) and the fallback (CaptionDragFallback) stop the
// drag itself, but only once it has started, and neither can change the
// cursor: the arrows the system shows over a resize border are drawn by the
// window's own process. An application that resizes itself instead of asking
// the system for a move/size loop (LINE, a Qt frameless window) is not covered
// by either of them at all.
//
// This window owns those few pixels instead: it reports its whole area as
// client (so no resize cursor appears over it), keeps the plain arrow, and
// swallows presses without changing activation. It is
// layered at alpha 1 - invisible in practice, but still hit-tested, which a
// fully transparent layered window would not be.
module private TopEdgeGuardNative =
    [<System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")>]
    extern nativeint SendMessageTimeout(nativeint hwnd, uint32 message, nativeint wparam, nativeint lparam, uint32 flags, uint32 timeout, nativeint& result)

    [<System.Runtime.InteropServices.DllImport("dwmapi.dll")>]
    extern int DwmGetWindowAttribute(nativeint hwnd, uint32 attribute, RECT& value, uint32 size)

    // GetSystemMetrics answers for the primary monitor's scale whatever the
    // window's own scale is. This one answers for the scale it is given.
    [<System.Runtime.InteropServices.DllImport("user32.dll")>]
    extern int GetSystemMetricsForDpi(int index, uint32 dpi)

module TopEdgeGuardOptions =
    /// Paint the band instead of leaving it invisible, so that where it sits
    /// can be seen while it is being fitted to a window. Edit it here; there is
    /// no setting for it.
    ///
    /// true: filled blue at alpha 160, and repainted on every move - a plain
    /// move asks for no paint of its own, so without that the fill is left
    /// behind where the band used to be.
    /// false: alpha 1 - invisible to the eye, and still a window the mouse can
    /// land on, which a fully transparent layered window would not be.
    let showBand = false

type TopEdgeGuard(os: OS) =
    let mutable window : IWindow option = None
    let mutable owner = IntPtr.Zero
    let mutable shown = false
    // (window, width, height, band) -> width of the band before the buttons
    let mutable buttonScan : ((IntPtr * int * int) * int) option = None
    // (window, dpi) -> how far down its own top border reaches
    let mutable borderScan : ((IntPtr * int) * int) option = None
    let mutable eligible = false
    let mutable disposed = false
    let mutable strip = IntPtr.Zero
    let mutable keepTopmost = false
    let mutable lastOrder = None
    let mutable lastFailedOrder = None
    let traceChanges = TopEdgeGuardPolicy.ChangeTrace<obj>()

    // The strip of a window that starts a top resize: the sizing frame plus
    // the invisible padded border, at that window's own scale.
    //
    // The scale has to be the window's. GetSystemMetrics answers for the
    // primary monitor whatever the window sits on, so a window at 125% or more
    // has a taller resize border than it reports, and the band was short of
    // covering it: the rows it missed still answered "top border", and the
    // resize cursor came back over most of the width.
    //
    // Nothing is added on top of the measurement. The border is all the band is
    // meant to own; a couple of pixels of slack reach past it into the window
    // itself, which on Chrome is the top of its tabs.
    static member bandHeightForDpi (dpi: int) =
        let dpi = if dpi <= 0 then 96 else dpi
        let metric index =
            let scaled = try TopEdgeGuardNative.GetSystemMetricsForDpi(index, uint32 dpi) with _ -> 0
            if scaled > 0 then scaled
            else
                // Before Windows 10 1607, or a call that failed: scale the
                // primary monitor's metric by hand, rounding up rather than
                // down for the same reason.
                let plain = WinUserApi.GetSystemMetrics(index)
                (plain * dpi + 95) / 96
        max 4 (metric SystemMetrics.SM_CYFRAME + metric 92 (* SM_CXPADDEDBORDER *))

    member private this.wndProc (msg: Win32Message) =
        match msg.msg with
        // The whole window is client area: a border answer here would bring
        // the resize cursor back, which is the point of this window.
        | 0x0084 (* WM_NCHITTEST *) ->
            if TopEdgeGuardPlacement.safeForOwner msg.hwnd strip owner keepTopmost then 1 (* HTCLIENT *)
            else
                this.hide()
                0 (* HTNOWHERE; never use cross-thread HTTRANSPARENT as a fallback *)
        | 0x0020 (* WM_SETCURSOR *) ->
            WinUserApi.SetCursor(WinUserApi.LoadCursor(IntPtr.Zero, CursorIds.IDC_ARROW)).ignore
            1
        | 0x0021 (* WM_MOUSEACTIVATE *) -> 3 (* MA_NOACTIVATE *)
        | 0x000F (* WM_PAINT *) when TopEdgeGuardOptions.showBand ->
            let mutable ps = PAINTSTRUCT()
            let hdc = WinUserApi.BeginPaint(msg.hwnd, &ps)
            (try
                use g = Drawing.Graphics.FromHdc(hdc)
                use brush = new Drawing.SolidBrush(Drawing.Color.Blue)
                g.FillRectangle(brush, Drawing.Rectangle(0, 0,
                    ps.rcPaint.Right - ps.rcPaint.Left, ps.rcPaint.Bottom - ps.rcPaint.Top))
             with _ -> ())
            WinUserApi.EndPaint(msg.hwnd, &ps).ignore
            0
        | 0x0201 | 0x0204 | 0x0207 (* button down *) ->
            // Foreground/reorder notifications are asynchronous. A queued press
            // must never activate an owner that has since been covered.
            if not (TopEdgeGuardPlacement.safeForOwner msg.hwnd strip owner keepTopmost) then this.hide()
            0
        | _ -> msg.def()

    member private this.ensureWindow() =
        match window with
        | Some(w) -> w
        | None ->
            let w =
                os.createWindow this.wndProc
                    WindowsStyles.WS_POPUP
                    (WindowsExtendedStyles.WS_EX_LAYERED |||
                     WindowsExtendedStyles.WS_EX_TOOLWINDOW |||
                     WindowsExtendedStyles.WS_EX_NOACTIVATE)
            // Alpha 1: invisible to the eye, and still a window the mouse can
            // land on. Alpha 0 would let every press through to the border.
            let alpha = if TopEdgeGuardOptions.showBand then 160uy else 1uy
            WinUserApi.SetLayeredWindowAttributes(w.hwnd, 0, alpha, 2 (* LWA_ALPHA *)).ignore
            window <- Some(w)
            w

    // How far down the window's own top border reaches, asked of the window
    // itself rather than computed. Applications disagree: at 100% Chrome
    // answers "top border" for six pixels and Edge for eight, and the gap grows
    // with the scale. A band built from the system metrics is right for one of
    // them and wrong for the other, and being one pixel too tall puts it over
    // the top of Chrome's tabs.
    //
    // The middle of the window is asked, away from the corners and the caption
    // buttons. The answer only changes when the window moves to another display
    // or its frame changes, so it is kept per window; a window inside a
    // move/size loop is never asked (see widthBeforeButtons for why).
    member private this.topBorderHeight(ownerHwnd: IntPtr, windowBounds: Rect, dpi: int, mayScan: bool) =
        let fallback () = TopEdgeGuard.bandHeightForDpi dpi
        let key = ownerHwnd, dpi
        match borderScan with
        | Some(cached, value) when cached = key -> value
        | Some(_, value) when not mayScan -> value
        | _ when not mayScan -> fallback ()
        | _ ->
            let x = windowBounds.location.x + windowBounds.size.width / 2
            let top = windowBounds.location.y
            // Twice the tallest border any scale asks for is enough to find the
            // end of it, and short enough to stay cheap.
            let reach = min 28 (max 1 (windowBounds.size.height / 2))
            let started = Diagnostics.Stopwatch.GetTimestamp()
            let elapsedMs () =
                float (Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0
                / float Diagnostics.Stopwatch.Frequency
            let mutable last = -1
            let mutable y = 0
            let mutable asking = true
            while asking && y < reach do
                let packed = (((top + y) &&& 0xffff) <<< 16) ||| (x &&& 0xffff)
                let mutable result = IntPtr.Zero
                let answered =
                    try
                        TopEdgeGuardNative.SendMessageTimeout(ownerHwnd, 0x0084u (* WM_NCHITTEST *),
                            IntPtr.Zero, IntPtr(packed), 0x0002u (* SMTO_ABORTIFHUNG *), 60u, &result) <> IntPtr.Zero
                    with _ -> false
                // The border runs from the top edge without a break, so the
                // first row that is not it ends the measurement - there is
                // nothing below it to find. A window that did not answer will
                // not answer the next row either: asking it all twenty-eight
                // rows would hold this thread for a second and a half.
                if not answered then asking <- false
                elif result.ToInt32() = 12 (* HTTOP *) then last <- y
                else asking <- false
                // However it behaves, the whole measurement is over quickly.
                if elapsedMs () > 100.0 then asking <- false
                y <- y + 1
            // Nothing named the border - a window that resizes itself (LINE), or
            // one that was busy. The metrics are all there is to go on then.
            let value = if last >= 0 then last + 1 else fallback ()
            borderScan <- Some(key, value)
            value

    // Where the caption buttons begin, measured by asking the window what is
    // at points along the row the band covers, from the right edge inwards.
    // The answer only changes when the window is resized, so it is kept until
    // then: the scan is ~40 cross-process messages.
    member private this.widthBeforeButtons(ownerHwnd: IntPtr, bounds: Rect, bandHeight: int, mayScan: bool) =
        // The buttons sit at the right edge, so only the width can move them:
        // a resize from the top or the bottom changes the height every frame
        // and must not set off the scan again. While the window is inside a
        // move/size loop nothing is scanned at all - forty cross-process
        // questions per frame is what made a top-edge resize crawl - and the
        // last answer, or the full width, stands until the loop ends.
        let key = ownerHwnd, bounds.size.width, bandHeight
        match buttonScan with
        | Some(cached, value) when cached = key -> value
        | Some(_, value) when not mayScan -> value
        | _ when not mayScan -> bounds.size.width
        | _ ->
            let isButton code = code = 3 (* HTSYSMENU *) || code = 8 (* HTMINBUTTON *) ||
                                code = 9 (* HTMAXBUTTON *) || code = 20 (* HTCLOSE *) || code = 21 (* HTHELP *)
            // Two rows are asked. The band's own row is where a window with a
            // normal frame answers for its buttons; a UWP window (the Camera,
            // the Calculator) answers "top border" across its whole width
            // there and only names the buttons a few pixels lower, inside the
            // title bar. Both rows describe the same buttons, so the first row
            // that names any decides.
            let rows = [ bounds.location.y + bandHeight / 2; bounds.location.y + bandHeight + 6 ]
            let mutable y = List.head rows
            let right = bounds.location.x + bounds.size.width - 1
            // Far enough for three buttons at any scale, never past the middle.
            let reach = min (bounds.size.width / 2) 400
            let hitAt x =
                let packed = ((y &&& 0xffff) <<< 16) ||| (x &&& 0xffff)
                let mutable result = IntPtr.Zero
                try
                    if TopEdgeGuardNative.SendMessageTimeout(ownerHwnd, 0x0084u (* WM_NCHITTEST *),
                            IntPtr.Zero, IntPtr(packed), 0x0002u (* SMTO_ABORTIFHUNG *), 60u, &result) = IntPtr.Zero
                    then None else Some(result.ToInt32())
                with _ -> None
            let scanRow (row: int) =
                y <- row
                let mutable found = None
                let mutable x = right
                while x > right - reach do
                    match hitAt x with
                    | Some(code) when isButton code -> found <- Some(x)
                    | _ -> ()
                    x <- x - 6
                found
            let mutable leftMost = scanRow (List.head rows)
            let value =
                match leftMost with
                | Some(buttonX) ->
                    // The coarse scan leaves the edge somewhere inside the last
                    // six pixels; a few more questions find it exactly, so the
                    // band ends where the button starts instead of up to two
                    // steps short of it.
                    let mutable low = buttonX - 6   // not a button (or unasked)
                    let mutable high = buttonX      // a button
                    while high - low > 1 do
                        let middle = (low + high) / 2
                        match hitAt middle with
                        | Some(code) when isButton code -> high <- middle
                        | _ -> low <- middle
                    let cut = high - bounds.location.x
                    let cut = if cut <= 0 then bounds.size.width else min cut bounds.size.width
                    cut
                | None ->
                    // The window named nothing in the band's own row: it
                    // answers for the frame there instead (Windows Terminal,
                    // WinMerge, the file manager). DWM's caption rectangle is
                    // the next description, and it keeps the band clear of the
                    // buttons those windows draw at the very top.
                    let mutable buttons = RECT()
                    let ok =
                        try
                            TopEdgeGuardNative.DwmGetWindowAttribute(ownerHwnd, 5u (* DWMWA_CAPTION_BUTTON_BOUNDS *), &buttons, 16u) = 0
                        with _ -> false
                    if not ok || buttons.Right <= buttons.Left then
                        // No caption rectangle either: a UWP window (the
                        // Camera, the Calculator) names its buttons one row
                        // below the band, so that row is asked last.
                        match scanRow (List.item 1 rows) with
                        | Some(buttonX) ->
                            let mutable low = buttonX - 6
                            let mutable high = buttonX
                            while high - low > 1 do
                                let middle = (low + high) / 2
                                match hitAt middle with
                                | Some(code) when isButton code -> high <- middle
                                | _ -> low <- middle
                            let cut = high - bounds.location.x
                            let cut = if cut <= 0 then bounds.size.width else min cut bounds.size.width
                            cut
                        | None ->
                            bounds.size.width
                    else
                        let real = try os.windowFromHwnd(ownerHwnd).bounds with _ -> bounds
                        // DWM reports the system frame's buttons, which start
                        // further left than the ones such a window draws: the
                        // band reaches nine more pixels to the right, which
                        // measured flush against Windows Terminal's and the
                        // file manager's own buttons.
                        let scale = try Dpi.scaleForRect bounds with _ -> 1.0
                        let cut = (real.location.x + buttons.Left + Dpi.px scale 9) - bounds.location.x
                        let cut = if cut <= 0 then bounds.size.width else min cut bounds.size.width
                        cut
            buttonScan <- Some(key, value)
            value

    member this.auditDecision(phase: string) =
#if DEBUG
        let hwnd = window |> Option.map (fun w -> w.hwnd) |> Option.defaultValue IntPtr.Zero
        TopEdgeGuardPlacement.traceDecision phase hwnd strip owner None None None
#else
        ()
#endif

    member this.hide() =
        lastFailedOrder <- None
        this.auditDecision("hide-enter")
        lastOrder <- None
        match window with
        | Some w ->
            TopEdgeGuardPlacement.hideAndDemote w.hwnd
#if DEBUG
            if shown then
                Diagnostics.Trace.WriteLine(sprintf "[TopEdgeGuard] hidden owner=%X guard=%X foreground=%X"
                    (int64 owner) (int64 w.hwnd) (int64 (WinUserApi.GetForegroundWindow())))
#endif
        | None -> ()
        shown <- false
        this.auditDecision("hide-final")

    member private this.trace(key: obj, build: unit -> string) =
#if DEBUG
        traceChanges.Write(key, build, fun line ->
            Diagnostics.Trace.WriteLine(sprintf "[TopEdgeGuard] %s" line))
#else
        ()
#endif

    member private this.trace(line: string) =
        this.auditDecision(line)
        this.trace(box line, fun () -> line)

    member this.followsStrip = eligible && not disposed

    member this.invalidateOrder() = lastOrder <- None

    member private this.orderKey(stripHwnd: IntPtr, guard: IntPtr) =
        (stripHwnd, TopEdgeGuardPlacement.above owner, TopEdgeGuardPlacement.above guard,
         TopEdgeGuardPlacement.above stripHwnd, TopEdgeGuardPlacement.isTopMost owner,
         TopEdgeGuardPlacement.isTopMost guard, TopEdgeGuardPlacement.isTopMost stripHwnd,
         TopEdgeGuardPlacement.visible stripHwnd, TopEdgeGuardPlacement.ownerOf stripHwnd, keepTopmost)

    member this.stripChanged(stripHwnd: IntPtr) =
        match window with
        | Some w when this.followsStrip ->
            if not (TopEdgeGuardPlacement.foregroundOwner owner) then
                this.auditDecision("repair-behind")
                if shown then this.hide()
                false
            elif not shown then
                this.auditDecision("repair-hidden-request")
                true
            else
                let aboveOwner = TopEdgeGuardPlacement.above owner
                let key = this.orderKey(stripHwnd, w.hwnd)
                // Only direct adjacency proves no window was inserted between
                // the guard and owner. Owned-frame chains still need validation.
                if aboveOwner = w.hwnd && lastOrder = Some key then
                    this.auditDecision("repair-cached-safe")
                    PerfTrace.count "topEdgeGuard.repairUnchanged"
                    TopEdgeGuardPlacement.repairStripAboveGuard w.hwnd stripHwnd owner
                    false
                else
                    let safe = TopEdgeGuardPlacement.safeForOwner w.hwnd stripHwnd owner keepTopmost
                    this.auditDecision(if safe then "repair-safe" else "repair-unsafe")
                    if safe then
                        lastOrder <- Some key
                        lastFailedOrder <- None
                        TopEdgeGuardPlacement.repairStripAboveGuard w.hwnd stripHwnd owner
                        false
                    elif TopEdgeGuardPlacement.canKeepShown w.hwnd owner then
                        // No repeated retry for the same failed native order.
                        // A changed neighbour, foreground, or geometry permits
                        // another attempt through the existing event paths.
                        lastFailedOrder <> Some key
                    else
                        this.hide()
                        true
        | _ -> false

    /// Cover the target's top border and its contiguous owned margin frames.
    /// The strip and unrelated windows always keep their input priority.
    member this.update(wanted: bool, ownerHwnd: IntPtr, bounds: Rect option, marginTop: int, useTopmost: bool, mayScan: bool,
                       tabsInside: bool, stripHwnd: IntPtr) =
        lastOrder <- None
        keepTopmost <- useTopmost
        strip <- stripHwnd
        eligible <- wanted && not disposed && ownerHwnd <> IntPtr.Zero && bounds.IsSome
#if DEBUG
        let hwnd = window |> Option.map (fun w -> w.hwnd) |> Option.defaultValue IntPtr.Zero
        TopEdgeGuardPlacement.traceDecision
            (sprintf "update-enter(wanted=%b,disposed=%b,bounds=%b,inside=%b,topmost=%b)"
                wanted disposed bounds.IsSome tabsInside useTopmost)
            hwnd stripHwnd ownerHwnd None None None
#endif
        if disposed || not wanted || ownerHwnd = IntPtr.Zero || bounds.IsNone ||
           not (TopEdgeGuardPlacement.foregroundOwner ownerHwnd) then
            this.hide()
            this.trace("hidden: unavailable")
        else
            let target = os.windowFromHwnd(ownerHwnd)
            let stripReady = stripHwnd <> IntPtr.Zero && WinUserApi.IsWindow(stripHwnd) &&
                             TopEdgeGuardPlacement.visible stripHwnd
            if not target.isWindow || target.isMinimized || target.isMaximized then
                eligible <- false
                this.hide()
                this.trace("hidden: target unavailable")
            elif tabsInside && not stripReady then
                this.hide()
                this.trace("hidden: StripUnavailable")
            else
                let bounds = bounds.Value
                let w = this.ensureWindow()
                if owner <> ownerHwnd then
                    this.hide()
                    // Shared placement sets and verifies the native owner while hidden.
                    owner <- ownerHwnd
                // The window's own scale, not the primary monitor's: the band
                // has to be as tall as the resize border of the monitor this
                // window is on.
                let dpi = try int (WinUserApi.GetDpiForWindow(ownerHwnd)) with _ -> 96
                let band =
                    // A window with a margin (LINE) hangs its own frame above
                    // its rectangle and answers nothing useful inside it; the
                    // metrics decide there.
                    if marginTop > 0 then TopEdgeGuard.bandHeightForDpi dpi
                    else this.topBorderHeight(ownerHwnd, target.bounds, dpi, mayScan)
                let height = marginTop + band
                // The minimize / maximize / close buttons reach the top edge,
                // and they are not a resize border: the band stops short of
                // them, so their top row still takes clicks. Where they start
                // is asked of the window itself - DWM's caption-button
                // rectangle is the system frame's, and an application that
                // draws its own buttons (Chrome) or none in the top row
                // (Windows Terminal) leaves a gap next to it either way.
                // A window with a margin (LINE) has its own frame all the way
                // across the band, buttons included, and its buttons sit below
                // it: that one is covered to the right edge. Measuring it
                // instead would make things worse - it names no button in the
                // band's row, and DWM then answers with the system frame's
                // rectangle, which ends a hundred and fifty pixels short of
                // the right edge and leaves that much of the frame reachable.
                let width =
                    if marginTop > 0 then bounds.size.width
                    else this.widthBeforeButtons(ownerHwnd, bounds, height, mayScan)
                // The top-left corner stays clear: that is the corner grip, and
                // resizing a window from it is left alone. Twenty-five pixels
                // at 100% is enough to aim at, and never less than the sizing
                // frame itself.
                let leftGap =
                    let scale = try Dpi.scaleForRect bounds with _ -> 1.0
                    max (Dpi.px scale 25) band
                // `bounds` is the group's rectangle, which already includes the
                // margin: the band starts there and reaches past the window's
                // own top border.
                let rect = TopEdgeGuardPolicy.rectangle bounds.location.x bounds.location.y width marginTop band leftGap
                let rect =
                    match TopEdgeGuardPlacement.rectOf ownerHwnd with
                    | Some nativeBounds -> TopEdgeGuardPolicy.clipHorizontal nativeBounds leftGap rect
                    | None -> { rect with width = 0 }
                let repaint () =
                    if TopEdgeGuardOptions.showBand then
                        // A plain move asks for no paint of its own.
                        WinUserApi.RedrawWindow(w.hwnd, IntPtr.Zero, IntPtr.Zero,
                            RedrawWindowFlags.RDW_INVALIDATE ||| RedrawWindowFlags.RDW_ERASE |||
                            RedrawWindowFlags.RDW_UPDATENOW).ignore
                // An owned resize frame may lie above the native rectangle
                // even when no margin was configured. Extend upward before
                // z-order selection so frameInBand also admits that frame.
                shown <-
                    match TopEdgeGuardPlacement.coverOwnedTopFrames w.hwnd stripHwnd ownerHwnd rect with
                    | Some covered ->
                        TopEdgeGuardPlacement.placeForOwner w.hwnd stripHwnd ownerHwnd keepTopmost covered
                    | None ->
                        this.auditDecision("coverage-unavailable")
                        this.hide()
                        false
                if shown then
                    if TopEdgeGuardPlacement.safeForOwner w.hwnd stripHwnd owner keepTopmost then
                        lastFailedOrder <- None
                    else lastFailedOrder <- Some (this.orderKey(stripHwnd, w.hwnd))
                    repaint()
#if DEBUG
                let previous = TopEdgeGuardPlacement.above ownerHwnd
                let key = box (ownerHwnd, stripHwnd, w.hwnd, previous, shown)
                this.trace(key, fun () ->
                    sprintf "owner=%X strip=%X guard=%X aboveOwner=%X aboveGuard=%X foreground=%X shown=%b"
                        (int64 ownerHwnd) (int64 stripHwnd) (int64 w.hwnd) (int64 previous)
                        (int64 (TopEdgeGuardPlacement.above w.hwnd))
                        (int64 (WinUserApi.GetForegroundWindow())) shown)
#endif

    member this.dispose() =
        disposed <- true
        eligible <- false
        this.hide()
        window |> Option.iter (fun w -> (w :?> IDisposable).Dispose())
        window <- None
