namespace Bemo
open System
open System.Drawing
open System.Runtime.InteropServices
open System.Windows.Forms

// HWNDs and the fallback paint stay on the caller's UI thread. The shared
// host owns only Composition objects and accepts asynchronous size/color updates.
// No "as this" or Form.TopMost: constructor callbacks and focus must stay safe.
type SnapPreview() =
    inherit Form()
    let mutable configured = false
    let mutable shownBounds : Rect option = None
    let mutable drawBounds : Rect option = None
    let mutable displayedBounds : Rect option = None
    let mutable insertAfter = IntPtr.Zero
    let mutable anchors : IntPtr list = []
    let mutable darkStyle = false
    let mutable glassMode = false
    let mutable nativeGlassReady = false
    let mutable failed = false
    let mutable closing = false
    let mutable surface : TabGlassHost.PreviewSurface option = None
    let mutable timer : Timer option = None
    let mutable animationTimer : Timer option = None
    let mutable motion : (Rect * Rect * float) option = None
    let animationClock = Diagnostics.Stopwatch()
    let box (r: Rect) = r.x, r.y, r.width, r.height
    let rect (x, y, width, height) = Rect(Pt(x, y), Sz(width, height))
    let pending = Diagnostics.Stopwatch()

    member private this.ReleaseSurface() =
        let previous = surface
        surface <- None
        previous |> Option.iter (fun value -> try (value :> IDisposable).Dispose() with _ -> ())

    member private this.Configure() =
        if not configured then
            configured <- true
            this.FormBorderStyle <- FormBorderStyle.None
            this.StartPosition <- FormStartPosition.Manual
            this.AutoScaleMode <- AutoScaleMode.None
            this.ShowInTaskbar <- false
            this.BackColor <- Color.FromArgb(85, 85, 85)
            this.Opacity <- 0.67
            let tick = new Timer(Interval = 50)
            tick.Tick.Add(fun _ -> this.RefreshPreview(true))
            timer <- Some tick
            let animationTick = new Timer(Interval = 16)
            animationTick.Tick.Add(fun _ -> this.RefreshPreview(false))
            animationTimer <- Some animationTick

    member private this.SetGlassMode(value) =
        if value <> glassMode then
            this.Hide()
            animationTimer |> Option.iter (fun value -> value.Stop())
            animationClock.Stop()
            this.ReleaseSurface()
            glassMode <- value
            displayedBounds <- None
            this.Opacity <- if value then 1.0 else 0.67
            if this.IsHandleCreated then this.RecreateHandle()

    member private this.ApplyFrame() =
        try
            let mutable rounded = 2 // DWMWCP_ROUND; unsupported on Windows 10.
            DwmApi.DwmSetWindowAttribute(this.Handle, 33, &rounded, 4) |> ignore
            let mutable border = if darkStyle then 0x363636 else 0xB8B8B8
            DwmApi.DwmSetWindowAttribute(this.Handle, 34, &border, 4) |> ignore
        with _ -> ()

    override this.OnHandleCreated(e) =
        base.OnHandleCreated(e)
        if configured then
            this.ApplyFrame()
            nativeGlassReady <- false
            if glassMode then
                try
                    let mutable enabled = 1
                    nativeGlassReady <- DwmApi.DwmSetWindowAttribute(this.Handle, 17, &enabled, 4) >= 0 &&
                                        WinUserApi.SetLayeredWindowAttributes(this.Handle, 0, 255uy, 2)
                with _ -> ()

    override this.OnHandleDestroyed(e) =
        this.ReleaseSurface()
        base.OnHandleDestroyed(e)

    override this.OnPaintBackground(e) =
        if not glassMode then base.OnPaintBackground(e)

    override this.Dispose(disposing) =
        closing <- true
        if disposing then
            timer |> Option.iter (fun value -> value.Stop(); value.Dispose())
            timer <- None
            animationTimer |> Option.iter (fun value -> value.Stop(); value.Dispose())
            animationTimer <- None
            animationClock.Reset()
            motion <- None
            try this.Hide() with _ -> ()
            this.ReleaseSurface()
        base.Dispose(disposing)

    override this.ShowWithoutActivation = true
    override this.CreateParams =
        let parameters = base.CreateParams
        // NOACTIVATE, TOOLWINDOW, TRANSPARENT, TOPMOST, LAYERED. The full-alpha
        // layered style retains mouse pass-through while Composition draws.
        parameters.ExStyle <- parameters.ExStyle ||| 0x08000000 ||| 0x80 ||| 0x20 ||| 0x8 ||| 0x80000
        if glassMode then parameters.ExStyle <- parameters.ExStyle ||| 0x00200000 // NOREDIRECTIONBITMAP
        parameters

    override this.WndProc(message: Message byref) =
        match message.Msg with
        | 0x0046 when insertAfter <> IntPtr.Zero && WinUserApi.IsWindow(insertAfter) ->
            let position = Marshal.PtrToStructure(message.LParam, typeof<WINDOWPOS>) :?> WINDOWPOS
            if position.flags &&& SetWindowPosFlags.SWP_NOZORDER = 0 then
                position.hWndInsertAfter <- insertAfter
                Marshal.StructureToPtr(position, message.LParam, false)
            base.WndProc(&message)
        | 0x0021 -> message.Result <- IntPtr(3) // MA_NOACTIVATE
        | 0x0084 -> message.Result <- IntPtr(-1) // HTTRANSPARENT
        | _ -> base.WndProc(&message)

    member this.KeepBehind(windows: IntPtr list) =
        try
            this.Configure()
            let anchors = windows |> List.filter WinUserApi.IsWindowVisible |> Set.ofList
            let lowest =
                if anchors.Count = 1 then Set.minElement anchors
                else
                    let mutable remaining = anchors
                    let mutable current = WinUserApi.GetTopWindow(IntPtr.Zero)
                    let mutable last = IntPtr.Zero
                    let mutable count = 0
                    while not remaining.IsEmpty && current <> IntPtr.Zero && count < 4096 do
                        if remaining.Contains current then
                            last <- current
                            remaining <- remaining.Remove current
                        current <- WinUserApi.GetWindow(current, 2) // GW_HWNDNEXT
                        count <- count + 1
                    if remaining.IsEmpty then last else IntPtr.Zero
            insertAfter <- lowest
            if this.Visible then
                if lowest = IntPtr.Zero then
                    this.HideImmediately()
                elif WinUserApi.GetWindow(this.Handle, 3) <> lowest then // GW_HWNDPREV
                    WinUserApi.SetWindowPos(this.Handle, lowest, 0, 0, 0, 0,
                        SetWindowPosFlags.SWP_NOACTIVATE ||| SetWindowPosFlags.SWP_NOMOVE ||| SetWindowPosFlags.SWP_NOSIZE) |> ignore
        with _ -> this.HideImmediately()


    member private this.ShowRectangle(r: Rect) =
        if not this.Visible then
            this.Bounds <- Rectangle(r.x, r.y, r.width, r.height)
            this.Show()
            displayedBounds <- None
        if displayedBounds <> Some r then
            WinUserApi.SetWindowPos(this.Handle, IntPtr.Zero, r.x, r.y, r.width, r.height,
                SetWindowPosFlags.SWP_NOACTIVATE ||| SetWindowPosFlags.SWP_NOZORDER) |> ignore
            displayedBounds <- Some r

    member this.HideImmediately() =
        shownBounds <- None
        drawBounds <- None
        displayedBounds <- None
        motion <- None
        animationClock.Reset()
        try timer |> Option.iter (fun value -> value.Stop()) with _ -> ()
        try animationTimer |> Option.iter (fun value -> value.Stop()) with _ -> ()
        try this.Hide() with _ -> ()

    member private this.AnimateTo(start: Rect, target: Rect, duration: float) =
        drawBounds <- Some start
        motion <- Some(start, target, duration)
        animationClock.Reset()
        if this.Visible then
            animationClock.Start()
            animationTimer |> Option.iter (fun value -> value.Start())

    member private this.RefreshPreview(maintainOrder: bool) =
        if not closing then
            try
                if maintainOrder then this.KeepBehind(anchors)
                match motion with
                | Some(start, target, duration) when animationClock.IsRunning ->
                    let progress = animationClock.Elapsed.TotalMilliseconds / duration
                    drawBounds <- Some(rect (SnapGeometry.previewInterpolate (box start) (box target) progress))
                    if progress >= 1.0 then
                        motion <- None
                        animationClock.Reset()
                        animationTimer |> Option.iter (fun value -> value.Stop())
                        if shownBounds.IsNone then this.HideImmediately()
                | _ -> ()
                match drawBounds with
                | Some r when insertAfter <> IntPtr.Zero ->
                    let available = TabGlassHost.previewAvailable()
                    this.SetGlassMode(available && not failed)
                    let mutable ready = false
                    if glassMode then
                        // Creating the HWND and DWM attributes stays on this thread.
                        let hwnd = this.Handle
                        if not nativeGlassReady then failed <- true
                        else
                            if surface.IsNone then
                                surface <- Some(new TabGlassHost.PreviewSurface(hwnd))
                                pending.Restart()
                            let value = surface.Value
                            // Windows.UI.Color bytes are A,R,G,B (little endian).
                            // Dark: 67% neutral tint over 33% blurred backdrop.
                            let color = if darkStyle then 0x555555ABu else 0xF3F3F3ABu
                            value.Update(Size(r.width, r.height), color)
                            ready <- value.Status = 1
                            if value.Status < 0 || (not ready && pending.ElapsedMilliseconds > 1500L) then failed <- true
                        if failed then this.SetGlassMode(false)
                    if not glassMode || ready then
                        this.ShowRectangle(r)
                        if motion.IsSome then
                            if not animationClock.IsRunning then animationClock.Start()
                            animationTimer |> Option.iter (fun value -> value.Start())
                    else
                        this.Hide()
                        animationTimer |> Option.iter (fun value -> value.Stop())
                        animationClock.Stop()
                | _ -> this.HideImmediately()
            with _ ->
                failed <- true
                this.HideImmediately()

    member this.UpdateBounds(bounds: Rect option, dark: bool, windows: IntPtr list, ?startBounds: Rect) =
        try
            this.Configure()
            anchors <- windows
            let changedColor = dark <> darkStyle
            darkStyle <- dark
            this.BackColor <- if dark then Color.FromArgb(85, 85, 85) else Color.FromArgb(243, 243, 243)
            if changedColor && this.IsHandleCreated then this.ApplyFrame()
            if bounds <> shownBounds then
                shownBounds <- bounds
                let destination = bounds |> Option.bind (fun r ->
                    if r.width <= 0 || r.height <= 0 then None else
                    let point = Pt(r.x + r.width / 2, r.y + r.height / 2)
                    let result =
                        match Mon.nearestFromPoint point with
                        | Some mon ->
                            let work = mon.workRect
                            let gap = Dpi.px (Dpi.scaleForMonitorHandle mon.hMonitor) 8
                            let x, y, width, height = SnapGeometry.previewInset gap
                                                        (work.x, work.y, work.width, work.height)
                                                        (r.x, r.y, r.width, r.height)
                            Rect(Pt(x, y), Sz(width, height))
                        | None -> r
                    Some result)
                match destination with
                | Some target ->
                    let start =
                        displayedBounds |> Option.orElse drawBounds
                        |> Option.orElse (startBounds |> Option.filter (fun r -> r.width > 0 && r.height > 0))
                        |> Option.defaultWith (fun () -> rect (SnapGeometry.previewCentered 0.5 (box target)))
                    this.AnimateTo(start, target, 300.0)
                | None ->
                    shownBounds <- None
                    match displayedBounds with
                    | Some start -> this.AnimateTo(start, rect (SnapGeometry.previewCentered 0.0 (box start)), 100.0)
                    | None -> this.HideImmediately()
            this.RefreshPreview(true)
            if drawBounds.IsSome then timer |> Option.iter (fun value -> value.Start())
        with _ ->
            this.HideImmediately()
