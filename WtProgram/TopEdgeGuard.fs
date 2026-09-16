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
// swallows presses after bringing the window it belongs to to the front. It is
// layered at alpha 1 - invisible in practice, but still hit-tested, which a
// fully transparent layered window would not be.
module private TopEdgeGuardNative =
    [<System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")>]
    extern nativeint SendMessageTimeout(nativeint hwnd, uint32 message, nativeint wparam, nativeint lparam, uint32 flags, uint32 timeout, nativeint& result)

    [<System.Runtime.InteropServices.DllImport("dwmapi.dll")>]
    extern int DwmGetWindowAttribute(nativeint hwnd, uint32 attribute, RECT& value, uint32 size)

type TopEdgeGuard(os: OS) =
    let mutable window : IWindow option = None
    let mutable owner = IntPtr.Zero
    let mutable shown = false
    // (window, width, height, band) -> width of the band before the buttons
    let mutable buttonScan : ((IntPtr * int * int) * int) option = None

    // The strip of a window that starts a top resize: the sizing frame plus
    // the invisible padded border, as the system reports them.
    static member bandHeight =
        let frame = WinUserApi.GetSystemMetrics(SystemMetrics.SM_CYFRAME)
        let padded = WinUserApi.GetSystemMetrics(92 (* SM_CXPADDEDBORDER *))
        max 4 (frame + padded)

    member private this.wndProc (msg: Win32Message) =
        match msg.msg with
        // The whole window is client area: a border answer here would bring
        // the resize cursor back, which is the point of this window.
        | 0x0084 (* WM_NCHITTEST *) -> 1 (* HTCLIENT *)
        | 0x0020 (* WM_SETCURSOR *) ->
            WinUserApi.SetCursor(WinUserApi.LoadCursor(IntPtr.Zero, CursorIds.IDC_ARROW)).ignore
            1
        | 0x0021 (* WM_MOUSEACTIVATE *) -> 3 (* MA_NOACTIVATE *)
        | 0x0201 | 0x0204 | 0x0207 (* button down *) ->
            // The press is swallowed, but the window below it still comes to
            // the front, which is what clicking a title bar would have done.
            if owner <> IntPtr.Zero && WinUserApi.IsWindow(owner) then
                os.windowFromHwnd(owner).setForeground(false)
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
            WinUserApi.SetLayeredWindowAttributes(w.hwnd, 0, 1uy, 2 (* LWA_ALPHA *)).ignore
            window <- Some(w)
            w

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

    member private this.hide() =
        if shown then
            match window with
            | Some(w) -> WinUserApi.ShowWindow(w.hwnd, ShowWindowCommands.SW_HIDE).ignore
            | None -> ()
            shown <- false

    /// Put the guard over the top edge of `ownerHwnd`, or take it away when
    /// the setting is off, the window is gone, maximized (no top border to
    /// grab) or the tabs are drawn inside the window, where the strip itself
    /// covers this band.
    /// `marginTop` is the per-exe margin of the window in front (0 for most).
    /// An application with one - LINE - hangs its own frame windows in that
    /// band, above its main window, and resizes from them; the guard then
    /// starts at the outer edge of that frame and is raised above it. Every
    /// other window keeps the plain band over its own top border, owned by it,
    /// so nothing of another application is ever covered.
    member this.update(wanted: bool, ownerHwnd: IntPtr, bounds: Rect option, marginTop: int, keepOnTop: bool, mayScan: bool) =
        if not wanted || ownerHwnd = IntPtr.Zero || bounds.IsNone then this.hide()
        else
            let target = os.windowFromHwnd(ownerHwnd)
            if not target.isWindow || target.isMinimized || target.isMaximized then this.hide()
            else
                let bounds = bounds.Value
                let w = this.ensureWindow()
                if owner <> ownerHwnd then
                    owner <- ownerHwnd
                    // Owned by the window it guards, so it follows it in the
                    // z-order and never covers anything in front of it.
                    os.windowFromHwnd(w.hwnd).setParent(target)
                let height = marginTop + TopEdgeGuard.bandHeight
                // The minimize / maximize / close buttons reach the top edge,
                // and they are not a resize border: the band stops short of
                // them, so their top row still takes clicks. Where they start
                // is asked of the window itself - DWM's caption-button
                // rectangle is the system frame's, and an application that
                // draws its own buttons (Chrome) or none in the top row
                // (Windows Terminal) leaves a gap next to it either way.
                // A window with a margin (LINE) has its own frame all the way
                // across the band, buttons included, and its buttons sit below
                // it: that one is covered to the right edge.
                let width =
                    if marginTop > 0 then
                        (try
                            let exe = try target.pid.exeName with _ -> "?"
                            let path = IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowTabs", "guard_width.log")
                            IO.File.AppendAllText(path,
                                sprintf "%s %-24s %-16s cut=%d of %d (margin %d)\r\n"
                                    (DateTime.Now.ToString("HH:mm:ss.fff")) exe "margin override"
                                    bounds.size.width bounds.size.width marginTop)
                         with _ -> ())
                        bounds.size.width
                    else this.widthBeforeButtons(ownerHwnd, bounds, height, mayScan)
                // With a margin the band has to start above the window's own
                // rectangle, where that application's frame windows are, and
                // be raised over them. They are ordinary windows, not topmost,
                // so this only ever reaches over the application's own frame.
                // A window with a margin keeps raising its own frame windows
                // above everything of its own, so being merely at the top of
                // the ordinary order is not enough while it is the tab in
                // front: the band is topmost for as long as it shows, and goes
                // back to ordinary the moment another tab does.
                let insertAfter =
                    if keepOnTop || marginTop > 0 then WindowHandleTypes.HWND_TOPMOST
                    else IntPtr.Zero
                // Leaving topmost behind needs saying so explicitly, or the
                // band stays above everything once a UWP window has been in
                // front.
                let insertAfter =
                    if insertAfter = IntPtr.Zero && os.windowFromHwnd(w.hwnd).isTopMost
                    then WindowHandleTypes.HWND_NOTOPMOST
                    else insertAfter
                let flags =
                    if insertAfter = IntPtr.Zero then
                        SetWindowPosFlags.SWP_NOACTIVATE ||| SetWindowPosFlags.SWP_NOZORDER
                    else SetWindowPosFlags.SWP_NOACTIVATE
                // The top-left corner stays clear: that is the corner grip, and
                // resizing a window from it is left alone. Twenty-five pixels
                // at 100% is enough to aim at, and never less than the sizing
                // frame itself.
                let leftGap =
                    let scale = try Dpi.scaleForRect bounds with _ -> 1.0
                    max (Dpi.px scale 25) TopEdgeGuard.bandHeight
                // `bounds` is the group's rectangle, which already includes the
                // margin: the band starts there and reaches past the window's
                // own top border.
                WinUserApi.SetWindowPos(w.hwnd, insertAfter,
                    bounds.location.x + leftGap, bounds.location.y, max 1 (width - leftGap), height,
                    flags).ignore
                if not shown then
                    WinUserApi.ShowWindow(w.hwnd, ShowWindowCommands.SW_SHOWNOACTIVATE).ignore
                    shown <- true

    member this.dispose() =
        this.hide()
        window |> Option.iter (fun w -> (w :?> IDisposable).Dispose())
        window <- None
