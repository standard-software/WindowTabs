namespace Bemo

open System
open System.Diagnostics
open System.Runtime.InteropServices
open System.Threading
open System.Windows.Forms

module private CaptionDragNative =
    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type Point =
        val mutable x: int
        val mutable y: int
        new(x, y) = { x = x; y = y }
    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type Rect =
        val mutable left: int
        val mutable top: int
        val mutable right: int
        val mutable bottom: int
    type Hook = delegate of int * IntPtr * IntPtr -> IntPtr
    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type TitleBarInfo =
        val mutable size: uint32
        val mutable rect: Rect
        val mutable state: uint32
        val mutable reserved: uint32
        val mutable minimize: uint32
        val mutable maximize: uint32
        val mutable help: uint32
        val mutable close: uint32
    [<DllImport("user32.dll")>]
    extern bool GetTitleBarInfo(IntPtr hwnd, TitleBarInfo& info)
    [<DllImport("dwmapi.dll")>]
    extern int DwmGetWindowAttribute(IntPtr hwnd, uint32 attribute, Rect& rect, uint32 size)
    [<DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")>]
    extern int GetNonClientRendering(IntPtr hwnd, uint32 attribute, int& enabled, uint32 size)
    [<DllImport("user32.dll", SetLastError = true)>]
    extern IntPtr SetWindowsHookExW(int kind, Hook callback, IntPtr moduleHandle, uint32 threadId)
    [<DllImport("user32.dll", SetLastError = true)>]
    extern bool UnhookWindowsHookEx(IntPtr hook)
    [<DllImport("user32.dll")>]
    extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data)
    [<DllImport("kernel32.dll", CharSet = CharSet.Unicode)>]
    extern IntPtr GetModuleHandleW(string name)
    [<DllImport("user32.dll")>]
    extern IntPtr WindowFromPoint(Point point)
    [<DllImport("user32.dll")>]
    extern IntPtr GetAncestor(IntPtr hwnd, uint32 flags)
    [<DllImport("user32.dll")>]
    extern uint32 GetWindowThreadProcessId(IntPtr hwnd, IntPtr processId)
    [<DllImport("user32.dll")>]
    extern int GetWindowLongW(IntPtr hwnd, int index)
    [<DllImport("user32.dll")>]
    extern bool GetWindowRect(IntPtr hwnd, Rect& rect)
    [<DllImport("user32.dll")>]
    extern bool ClientToScreen(IntPtr hwnd, Point& point)
    [<DllImport("user32.dll")>]
    extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd)
    [<DllImport("user32.dll")>]
    extern int GetAwarenessFromDpiAwarenessContext(IntPtr context)
    [<DllImport("user32.dll")>]
    extern bool PhysicalToLogicalPointForPerMonitorDPI(IntPtr hwnd, Point& point)
    [<DllImport("user32.dll", SetLastError = true)>]
    extern IntPtr SendMessageTimeoutW(IntPtr hwnd, uint32 message, IntPtr wparam, IntPtr lparam, uint32 flags, uint32 timeout, UIntPtr& result)
    [<DllImport("user32.dll")>]
    extern bool PostMessageW(IntPtr hwnd, uint32 message, IntPtr wparam, IntPtr lparam)
    [<DllImport("user32.dll")>]
    extern bool SetForegroundWindow(IntPtr hwnd)
    [<DllImport("user32.dll")>]
    extern IntPtr GetForegroundWindow()
    [<DllImport("user32.dll")>]
    extern bool IsWindow(IntPtr hwnd)
    [<DllImport("user32.dll")>]
    extern bool IsZoomed(IntPtr hwnd)
    [<DllImport("user32.dll")>]
    extern bool GetCursorPos(Point& point)
    [<DllImport("user32.dll")>]
    extern uint32 GetDoubleClickTime()
    [<DllImport("user32.dll")>]
    extern int GetSystemMetrics(int index)
    [<DllImport("user32.dll")>]
    extern int16 GetAsyncKeyState(int key)

// Level 1: consume caption button-down before the target can start moving.
// No MOVESIZESTART, saved rectangle, SetWindowPos or location-change feedback
// is involved for these presses. Snapped/maximized windows also cannot be
// dragged to restore; caption buttons, double-click, keyboard snap and
// programmatic moves remain.
//
// What this hook cannot stop - a custom title bar answering HTCLIENT, a hit
// test that fails or times out, an app that starts the move loop itself - is
// recorded as a press and handed to the MOVESIZESTART fallback in WindowGroup
// (CaptionDragFallback), which puts the window back while it is dragged.
// Hit-test failures always pass here; never turn an unresponsive app into a
// mouse trap.
//
// Swallowing the press also swallows what the real non-client click would
// have done besides activation: an open popup or menu of that app is not
// dismissed, an in-place rename is not committed, and the app's own
// WM_NCLBUTTONDOWN handling does not run. Activation and double-click are
// re-created below; nothing else is.
type CaptionDragPlugin() =
    let mutable enabled = 0
    let mutable stopping = 0
    let mutable worker: Thread option = None
    let dispatchGate = obj()
    let mutable dispatch: (unit -> unit) option = None
    let wake() = lock dispatchGate (fun () -> dispatch |> Option.iter (fun post -> post()))

    // Double-click replacement. false: post WM_NCLBUTTONDBLCLK so the window
    // makes its own maximize/restore decision. true: post SC_MAXIMIZE or
    // SC_RESTORE directly, for windows that ignore a double-click that was
    // not preceded by its button-down/up.
    let doubleClickBySystemCommand = false
    // Whole budget for the hit tests of one press, and for one window.
    // LowLevelHooksTimeout is 300 ms or more; the hook must stay far below it.
    let hitTestBudgetMs = 40
    let hitTestTimeoutMs = 25
    let healthCheckMs = 2000
    let retryMs = 1000
    let releasePollMs = 25
    let slowCallbackMs = 100.0

    let run() =
        use context = new ApplicationContext()
        use control = new Control()
        control.Handle |> ignore
        use timer = new System.Windows.Forms.Timer(Interval = healthCheckMs)
        // Work that must not run inside the hook callback: posted to this
        // thread's queue and executed after the callback has returned.
        let post (work: unit -> unit) =
            try control.BeginInvoke(Action(work)) |> ignore with _ -> ()
        let mutable hook = IntPtr.Zero
        let mutable state = CaptionDragPolicy.empty
        let mutable lastCallbackTick = Environment.TickCount
        let mutable lastHookX = 0
        let mutable lastHookY = 0
        let mutable reinstalls = 0

        let elapsedMs (started: int64) =
            float (Stopwatch.GetTimestamp() - started) * 1000.0 / float Stopwatch.Frequency

        // WM_NCHITTEST for one window, in that window's coordinate space.
        // Some(answer, x, y) with the point actually sent; None when the
        // window was not asked (unknown DPI context, failed conversion), did
        // not answer in time, or gave a caption answer that cannot be trusted.
        let hitTest (hwnd: IntPtr) (x: int) (y: int) (timeout: int) =
            let awareness =
                try CaptionDragNative.GetAwarenessFromDpiAwarenessContext(CaptionDragNative.GetWindowDpiAwarenessContext(hwnd))
                with _ -> -1
            let converted =
                if awareness = CaptionDragPolicy.awarenessPerMonitor then None
                else
                    let mutable point = CaptionDragNative.Point(x, y)
                    let ok =
                        try CaptionDragNative.PhysicalToLogicalPointForPerMonitorDPI(hwnd, &point)
                        with _ -> false
                    if ok then Some(point.x, point.y) else None
            match CaptionDragPolicy.hitTestPoint awareness (x, y) converted with
            | None -> None
            | Some(hx, hy) ->
                let mutable result = UIntPtr.Zero
                let sent =
                    CaptionDragNative.SendMessageTimeoutW(
                        hwnd, 0x84u, IntPtr.Zero, IntPtr(CaptionDragPolicy.packPoint hx hy),
                        0x23u (* SMTO_BLOCK | SMTO_ABORTIFHUNG | SMTO_ERRORONEXIT *), uint32 timeout, &result)
                if sent = IntPtr.Zero then None
                else
                    let code = int (result.ToUInt64())
                    let clientTop () =
                        let mutable origin = CaptionDragNative.Point(0, 0)
                        if CaptionDragNative.ClientToScreen(hwnd, &origin) then Some origin.y else None
                    if code = CaptionDragPolicy.hitCaption &&
                       not (CaptionDragPolicy.trustCaption awareness y
                                (if awareness = CaptionDragPolicy.awarenessPerMonitor then None else clientTop())) then None
                    else Some(code, hx, hy)

        let hasOwnCaption (hwnd: IntPtr) =
            (CaptionDragNative.GetWindowLongW(hwnd, -16 (* GWL_STYLE *)) &&& 0x00C00000) = 0x00C00000

        // A native frame can answer HTTOP on the first caption row even
        // though pressing it moves the window. Use
        // the OS caption rectangle, not a guessed pixel-width correction.
        // Require native non-client rendering and a caption ending exactly
        // at the client origin. Custom title bars keep their own hit tests.
        let isNativeCaptionBoundary hwnd x y =
            try
                if not (hasOwnCaption hwnd) || CaptionDragNative.IsZoomed(hwnd) ||
                   CaptionDragNative.GetAwarenessFromDpiAwarenessContext(CaptionDragNative.GetWindowDpiAwarenessContext(hwnd)) <> 2 then false
                else
                    let mutable title = CaptionDragNative.TitleBarInfo()
                    title.size <- uint32 (Marshal.SizeOf(typeof<CaptionDragNative.TitleBarInfo>))
                    let mutable bounds = CaptionDragNative.Rect()
                    let mutable buttons = CaptionDragNative.Rect()
                    let mutable client = CaptionDragNative.Point(0, 0)
                    let mutable rendered = 0
                    if not (CaptionDragNative.GetTitleBarInfo(hwnd, &title)) ||
                       not (CaptionDragNative.GetWindowRect(hwnd, &bounds)) ||
                       not (CaptionDragNative.ClientToScreen(hwnd, &client)) ||
                       title.rect.bottom <> client.y ||
                       CaptionDragNative.GetNonClientRendering(hwnd, 1u, &rendered, 4u) <> 0 || rendered = 0 ||
                       CaptionDragNative.DwmGetWindowAttribute(hwnd, 5u, &buttons, 16u) <> 0 then false
                    else
                        let box (r: CaptionDragNative.Rect) : CaptionDragPolicy.CaptionRect =
                            { left = r.left; top = r.top; right = r.right; bottom = r.bottom }
                        let buttonBox : CaptionDragPolicy.CaptionRect =
                            { left = bounds.left + buttons.left; top = bounds.top + buttons.top
                              right = bounds.left + buttons.right; bottom = bounds.top + buttons.bottom }
                        CaptionDragPolicy.nativeCaptionBoundary 12 true
                            ((title.state &&& 0x18001u) = 0u)
                            (box bounds) (box title.rect) client.y buttonBox x y
            with _ -> false

        // Route the press the way the system does: ask the window under the
        // cursor, and follow HTTRANSPARENT up to the top-level window.
        // Returns the caption receiver with its point when the press must be
        // swallowed, and the top-level window's own answer if it gave one.
        let inspect (leaf: IntPtr) (root: IntPtr) (x: int) (y: int) =
            let deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * int64 hitTestBudgetMs / 1000L
            let rec walk (window: IntPtr) depth =
                let remaining = int ((deadline - Stopwatch.GetTimestamp()) * 1000L / Stopwatch.Frequency)
                if window = IntPtr.Zero || depth > 8 || remaining <= 0 then None, None
                else
                    let isRoot = window = root
                    match hitTest window x y (min hitTestTimeoutMs remaining) with
                    | None -> None, None
                    | Some(code, hx, hy) ->
                        let rootHit = if isRoot then Some code else None
                        let parent = if isRoot then IntPtr.Zero else CaptionDragNative.GetAncestor(window, 1u (* GA_PARENT *))
                        let ownCaption = not isRoot && code = CaptionDragPolicy.hitCaption && hasOwnCaption window
                        let hostsControls =
                            not isRoot && code = CaptionDragPolicy.hitCaption &&
                            (try CaptionDragPolicy.captionHostsControls (Win32Helper.GetClassName(window)) with _ -> false)
                        let parentOnSameThread =
                            not isRoot && code = CaptionDragPolicy.hitTransparent && parent <> IntPtr.Zero &&
                            CaptionDragNative.GetWindowThreadProcessId(window, IntPtr.Zero) =
                                CaptionDragNative.GetWindowThreadProcessId(parent, IntPtr.Zero)
                        match CaptionDragPolicy.hitStep isRoot ownCaption hostsControls parentOnSameThread code with
                        | CaptionDragPolicy.CaptionHit -> Some(window, hx, hy), rootHit
                        | CaptionDragPolicy.Climb -> walk parent (depth + 1)
                        // The top border of the managed window is swallowed
                        // whether it is really the caption's first row
                        // (isNativeCaptionBoundary) or a genuine resize edge:
                        // with the window locked in place, a top-edge resize is
                        // the other way the tab strip gets dragged by accident.
                        | CaptionDragPolicy.OtherHit code when
                                isRoot && CaptionDragPolicy.blockTopBorderPress &&
                                CaptionDragPolicy.isTopBorder code ->
                            Some(window, hx, hy), rootHit
                        | CaptionDragPolicy.OtherHit _ -> None, rootHit
            match walk leaf 0 with
            | None, None ->
                // The walk stopped at a child that answered for itself, so the
                // top-level window was never asked - and that is where the
                // resize borders live. Office (NetUIHWND), LINE and VS Code put
                // a child over the top edge, and their windows kept resizing
                // from it. Ask the root itself before letting the press pass.
                let remaining = int ((deadline - Stopwatch.GetTimestamp()) * 1000L / Stopwatch.Frequency)
                if remaining <= 0 then None, None
                else
                    match hitTest root x y (min hitTestTimeoutMs remaining) with
                    | Some(code, hx, hy) when CaptionDragPolicy.isTopBorder code -> Some(root, hx, hy), Some code
                    | Some(code, _, _) -> None, Some code
                    | None -> None, None
            | result -> result

        // Replacement for the activation the swallowed press would have done.
        // The press never reached any queue, so the foreground lock may refuse
        // a plain SetForegroundWindow; WindowTabs' forced path (the one tab
        // clicks use) is the second attempt.
        let activate (target: int64) =
            let hwnd = IntPtr(target)
            if CaptionDragNative.IsWindow(hwnd) && CaptionDragNative.GetForegroundWindow() <> hwnd then
                if not (CaptionDragNative.SetForegroundWindow(hwnd)) then
                    OS().windowFromHwnd(hwnd).setForeground(true)
                    let granted = CaptionDragNative.GetForegroundWindow() = hwnd
                    Trace.WriteLine(sprintf "Caption drag: SetForegroundWindow refused for %X; forced path %s"
                                        target (if granted then "succeeded" else "failed"))

        let doubleClick (click: CaptionDragPolicy.Click) =
            let root = IntPtr(click.target)
            let delivered =
                if doubleClickBySystemCommand then
                    let hasMaximizeBox = (CaptionDragNative.GetWindowLongW(root, -16) &&& 0x00010000) <> 0
                    match CaptionDragPolicy.doubleClickCommand (CaptionDragNative.IsZoomed(root)) hasMaximizeBox with
                    | Some command -> CaptionDragNative.PostMessageW(root, 0x112u, IntPtr(command), IntPtr.Zero)
                    | None -> true
                else
                    CaptionDragNative.PostMessageW(
                        IntPtr(click.receiver), 0xa3u, IntPtr(CaptionDragPolicy.hitCaption),
                        IntPtr(CaptionDragPolicy.packPoint click.hitX click.hitY))
            if not delivered then
                Trace.WriteLine(sprintf "Caption drag: double-click could not be delivered to %X" click.receiver)

        let primaryButtonHeld () =
            let key = CaptionDragPolicy.primaryButtonKey (CaptionDragNative.GetSystemMetrics(23 (* SM_SWAPBUTTON *)) <> 0)
            CaptionDragNative.GetAsyncKeyState(key) < 0s

        let callback = CaptionDragNative.Hook(fun code message data ->
            let pass() = CaptionDragNative.CallNextHookEx(hook, code, message, data)
            if code < 0 || data = IntPtr.Zero then pass() else
            lastCallbackTick <- Environment.TickCount
            // MSLLHOOKSTRUCT: pt.x 0, pt.y 4, time 16.
            let x = Marshal.ReadInt32(data, 0)
            let y = Marshal.ReadInt32(data, 4)
            lastHookX <- x
            lastHookY <- y
            let msg = message.ToInt32()
            if msg <> 0x201 && msg <> 0x202 then pass() else
            let started = Stopwatch.GetTimestamp()
            try
                let mutable hitTestMs = 0.0
                let candidate =
                    if msg <> 0x201 then None
                    else
                        CaptionDragTargets.notePress() |> ignore
                        if Volatile.Read(&enabled) = 0 then None
                        else
                            let leaf = CaptionDragNative.WindowFromPoint(CaptionDragNative.Point(x, y))
                            let root = CaptionDragNative.GetAncestor(leaf, 2u (* GA_ROOT *))
                            if root = IntPtr.Zero || not (CaptionDragTargets.contains root) then
                                // A press elsewhere replaces the record: a later
                                // move loop of a managed window is not this press.
                                CaptionDragTargets.setPress None
                                None
                            else
                                let mutable rect = CaptionDragNative.Rect()
                                let hasRect = CaptionDragNative.GetWindowRect(root, &rect)
                                let hitStarted = Stopwatch.GetTimestamp()
                                let caption, rootHit = inspect leaf root x y
                                hitTestMs <- elapsedMs hitStarted
                                match caption with
                                | Some(receiver, hx, hy) when Volatile.Read(&enabled) <> 0 && CaptionDragTargets.contains root ->
                                    CaptionDragTargets.setPress None
                                    Some ({ target = root.ToInt64(); receiver = receiver.ToInt64()
                                            x = x; y = y; hitX = hx; hitY = hy
                                            time = uint32 (Marshal.ReadInt32(data, 16)) } : CaptionDragPolicy.Click)
                                | _ ->
                                    CaptionDragTargets.setPress (
                                        if not hasRect then None
                                        else
                                            Some { hwnd = root; rootHit = rootHit; x = x; y = y
                                                   bounds = CaptionDragFallback.box rect.left rect.top (rect.right - rect.left) (rect.bottom - rect.top)
                                                   tick = Environment.TickCount
                                                   sequence = CaptionDragTargets.pressSequence() })
                                    None
                let input = if msg = 0x202 then CaptionDragPolicy.Up else CaptionDragPolicy.Down candidate
                let next, action = CaptionDragPolicy.step (CaptionDragNative.GetDoubleClickTime())
                                        (CaptionDragNative.GetSystemMetrics(36) / 2)
                                        (CaptionDragNative.GetSystemMetrics(37) / 2) state input
                state <- next
                let result =
                    match action with
                    | CaptionDragPolicy.Pass -> pass()
                    | CaptionDragPolicy.Block -> IntPtr(1)
                    | CaptionDragPolicy.Activate target ->
                        post (fun () -> activate target)
                        IntPtr(1)
                    | CaptionDragPolicy.DoubleClick click ->
                        post (fun () -> doubleClick click)
                        IntPtr(1)
                let callbackMs = elapsedMs started
                let hitMs = hitTestMs
                if callbackMs >= slowCallbackMs then
                    post (fun () ->
                        Trace.WriteLine(sprintf "Caption drag: slow hook callback %.1f ms (hit test %.1f ms)" callbackMs hitMs))
#if DEBUG
                if msg = 0x201 && Volatile.Read(&enabled) <> 0 then
                    post (fun () ->
                        Debug.WriteLine(sprintf "[CaptionDrag] down %A callback=%.2fms hitTest=%.2fms" action callbackMs hitMs))
#endif
                result
            with ex ->
                state <- CaptionDragPolicy.empty
                post (fun () -> Trace.WriteLine("Caption drag input failed: " + ex.Message))
                pass())

        // Compile the hook's code paths before the hook goes in, not on the
        // first real click: a JIT pause inside the callback counts against
        // LowLevelHooksTimeout. Everything is aimed at this thread's own
        // hidden window. Runs once, at the first installation (never while
        // the setting stays off).
        let mutable warmedUp = false
        let warmUp () =
            try
                callback.Invoke(-1, IntPtr.Zero, IntPtr.Zero) |> ignore
                inspect control.Handle control.Handle 0 0 |> ignore
                isNativeCaptionBoundary control.Handle 0 0 |> ignore
                CaptionDragPolicy.step 500u 1 1 CaptionDragPolicy.empty (CaptionDragPolicy.Down None) |> ignore
                CaptionDragTargets.contains control.Handle |> ignore
                CaptionDragTargets.currentPress() |> ignore
                CaptionDragNative.WindowFromPoint(CaptionDragNative.Point(0, 0)) |> ignore
                primaryButtonHeld() |> ignore
            with ex ->
                Trace.WriteLine("Caption drag warm-up failed: " + ex.Message)

        let install () =
            let fresh = CaptionDragNative.SetWindowsHookExW(14 (* WH_MOUSE_LL *), callback, CaptionDragNative.GetModuleHandleW(null), 0u)
            if fresh = IntPtr.Zero then
                Trace.WriteLine(sprintf "Caption drag hook installation failed: %d" (Marshal.GetLastWin32Error()))
                false
            else
                // A replaced handle belongs to a hook Windows already removed;
                // unhooking it only releases the handle.
                if hook <> IntPtr.Zero then CaptionDragNative.UnhookWindowsHookEx(hook) |> ignore
                hook <- fresh
                lastCallbackTick <- Environment.TickCount
                let mutable cursor = CaptionDragNative.Point(0, 0)
                if CaptionDragNative.GetCursorPos(&cursor) then
                    lastHookX <- cursor.x
                    lastHookY <- cursor.y
                true

        let uninstall () =
            if hook <> IntPtr.Zero then
                if not (CaptionDragNative.UnhookWindowsHookEx(hook)) then
                    Trace.WriteLine(sprintf "Caption drag hook removal failed: %d" (Marshal.GetLastWin32Error()))
                hook <- IntPtr.Zero
            state <- CaptionDragPolicy.empty
            CaptionDragTargets.setPress None

        let cursorMovedSinceCallback () =
            let mutable cursor = CaptionDragNative.Point(0, 0)
            CaptionDragNative.GetCursorPos(&cursor) && (cursor.x <> lastHookX || cursor.y <> lastHookY)

        let startTimer interval =
            timer.Interval <- interval
            timer.Start()

        let synchronize() =
            let isEnabled = Volatile.Read(&enabled) <> 0
            if not isEnabled then
                state <- fst (CaptionDragPolicy.step 0u 0 0 state CaptionDragPolicy.Disable)
            let plan =
                CaptionDragPolicy.plan (Volatile.Read(&stopping) <> 0) isEnabled (hook <> IntPtr.Zero)
                    state.pendingUp (primaryButtonHeld())
            match plan with
            | CaptionDragPolicy.Exit ->
                uninstall()
                timer.Stop()
                context.ExitThread()
            | CaptionDragPolicy.Install ->
                if not warmedUp then
                    warmedUp <- true
                    warmUp()
                // Retry at a bounded rate if the desktop is unavailable.
                startTimer (if install() then healthCheckMs else retryMs)
            | CaptionDragPolicy.Keep ->
                // Windows removes a hook that overran LowLevelHooksTimeout
                // without telling anyone; the handle stays valid. Replace it
                // when the cursor moves without callbacks.
                if CaptionDragPolicy.hookLooksLost (Environment.TickCount - lastCallbackTick) (cursorMovedSinceCallback()) then
                    reinstalls <- reinstalls + 1
                    Trace.WriteLine(sprintf "Caption drag hook stopped receiving input; reinstalling (%d)" reinstalls)
                    install() |> ignore
                // Also ends a release poll left over from a quick OFF -> ON.
                startTimer healthCheckMs
            | CaptionDragPolicy.WaitForRelease ->
                // Finish an already swallowed click, even if OFF or removal
                // occurs between down/up. No new gesture is intercepted OFF.
                startTimer releasePollMs
            | CaptionDragPolicy.Remove ->
                uninstall()
                timer.Stop()

        timer.Tick.Add(fun _ -> synchronize())
        lock dispatchGate (fun () ->
            dispatch <- Some(fun () -> control.BeginInvoke(Action(synchronize)) |> ignore))
        synchronize()
        try
            if Volatile.Read(&stopping) = 0 then Application.Run(context)
        finally
            lock dispatchGate (fun () -> dispatch <- None)
            uninstall()
            GC.KeepAlive(callback)

    interface IPlugin with
        member this.init() =
            Volatile.Write(&enabled, if unbox<bool>(Services.settings.getValue("lockWindowPosition")) then 1 else 0)
            Services.settings.notifyValue "lockWindowPosition" (fun value ->
                Volatile.Write(&enabled, if unbox<bool> value then 1 else 0)
                wake())
            let thread = Thread(ThreadStart(run), IsBackground = true, Name = "Caption drag input")
            thread.SetApartmentState(ApartmentState.STA)
            worker <- Some thread
            thread.Start()

    interface IDisposable with
        member this.Dispose() =
            Volatile.Write(&enabled, 0)
            Volatile.Write(&stopping, 1)
            wake()
            worker |> Option.iter (fun thread -> thread.Join(1500) |> ignore)
