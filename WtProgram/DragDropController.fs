namespace Bemo
open System
open System.Windows.Forms

// Debug-only trace of the tab drag pipeline (capture window, detection,
// enter/float, drop) to %APPDATA%\WindowTabs\drag_trace.log. Compiled out
// of Release builds.
module DragTrace =
#if DEBUG
    let private maxBytes = 2L * 1024L * 1024L
    let mutable private writes = 0
#endif

    // Takes a thunk, not a string. An argument is evaluated before the call,
    // so taking the message itself left every sprintf at every call site
    // running in release builds with only the file write compiled out.
    let log (f: unit -> string) =
#if DEBUG
        try
            let dir = IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowTabs")
            let path = IO.Path.Combine(dir, "drag_trace.log")
            writes <- writes + 1
            if writes % 200 = 1 then
                let info = IO.FileInfo(path)
                if info.Exists && info.Length > maxBytes then
                    let previous = path + ".1"
                    try IO.File.Delete(previous) with _ -> ()
                    try IO.File.Move(path, previous) with _ -> ()
            let line = sprintf "%s [t%d] %s\r\n" (DateTime.Now.ToString("HH:mm:ss.fff")) Threading.Thread.CurrentThread.ManagedThreadId (f())
            IO.File.AppendAllText(path, line)
        with _ -> ()
#else
        ignore f
#endif

    // Short caller list for pinpointing which of the many suspend/resume call
    // sites leaked a suspension. Debug builds only.
    let callers (depth: int) =
#if DEBUG
        try
            let frames = Diagnostics.StackTrace(2, false).GetFrames()
            if isNull frames then "" else
            frames
            |> Seq.truncate depth
            |> Seq.map (fun f ->
                let m = f.GetMethod()
                if isNull m then "?" else sprintf "    %s.%s" m.DeclaringType.Name m.Name)
            |> String.concat "\r\n"
        with _ -> ""
#else
        ignore depth
        ""
#endif

type IDragState =
    abstract member mouseMove : Pt -> unit
    abstract member dispose : unit -> unit

type DragDetectingStateInfo = {
    initialPt : Pt
    onBegin : unit -> unit
    }

type DragDetectingState(info:DragDetectingStateInfo) as this =
    // Screen distances are device pixels now that the process is DPI aware, so
    // the drag thresholds are scaled to keep the same physical feel: a fixed
    // 5 device px would fire after two thirds of the former mouse travel on a
    // 150% monitor, turning careful clicks into accidental drags.
    let dragStartDistance = 5.0 * Dpi.scaleForPoint info.initialPt
    interface IDragState with
        member this.mouseMove(ptScreen) =
            if ptScreen.distance(info.initialPt) > dragStartDistance then
                DragTrace.log (fun () -> sprintf "detect: threshold passed at %A" ptScreen)
                info.onBegin()
        member this.dispose() = ()

type DragCapturedStateInfo = {
    target : IDragDropTarget
    targetHwnd : IntPtr
    targetWindow : Window
    onDragOut : Pt -> unit
    }

type DragCapturedState(info:DragCapturedStateInfo) as this =
    // Device pixels for the strip's monitor (see DragDetectingState).
    let dragOutDistance = Dpi.px (Dpi.scaleForRect info.targetWindow.bounds) 20
    interface IDragState with
        member this.mouseMove(ptScreen) =
            let dragBounds = info.targetWindow.bounds.inflate(0, dragOutDistance)
            if dragBounds.containsPoint(ptScreen) then
                info.target.dragMove(info.targetWindow.ptToClient(ptScreen))
            else
                info.target.dragExit()
                info.onDragOut(ptScreen)
        member this.dispose() = ()

type DragFloatingStateInfo = {
    imageOffset : Pt
    targets: Map2<IntPtr, IDragDropTarget>
    animationWindow: AnimationWindow
    onDragIn: (IntPtr * Pt) -> unit
    }

type DragFloatingState(info:DragFloatingStateInfo) =
    let os = OS()
    let animationWindow = info.animationWindow
    interface IDragState with
        member this.mouseMove(ptScreen) =
            let targetHwnd = os.windowAtPt(ptScreen).hwnd
            if info.targets.tryFind(targetHwnd).IsSome then
                animationWindow.setIsVisible(false)
                info.onDragIn(targetHwnd, ptScreen)
            else
                animationWindow.setLocation(ptScreen.sub(info.imageOffset))
                animationWindow.setIsVisible(true)
        member this.dispose() = animationWindow.setIsVisible(false)

type DragActionInfo = {
    targets : Map2<IntPtr, IDragDropTarget>
    notifications: Set2<IDragDropNotification>
    initialHwnd : IntPtr
    image : unit -> Img
    imageOffset : Pt
    initialPt : Pt
    data: obj
    onCancel : unit -> unit
    onBegin : unit -> unit
    onDrop : Pt -> unit
    onEnd : unit -> unit
    }

type DragAction(info:DragActionInfo, dragId:int) as this =
    let os = OS()
    let dragScale = 0.5
    let Cell = CellScope(true, false)
    let ptScreenCell = Cell.create(info.initialPt)
    let dragStateCell = Cell.create(None:Option<IDragState>)
    let captureWindowCell = Cell.create(None:Option<IWindow>)
    let timer = new Timer()
    let animationWindowCell = Cell.create(None:Option<AnimationWindow>)

    // Check if left mouse button is physically pressed using GetAsyncKeyState
    let isLeftMouseButtonDown() =
        Win32Helper.IsKeyPressed(VirtualKeyCodes.VK_LBUTTON)
    let mutable moveCount = 0
    let mutable hasEnded = false

    member this.setNextState(newState:obj) =
        let newState = unbox<IDragState>(newState)
        dragStateCell.value.iter <| fun state -> state.dispose()
        dragStateCell.set(Some(newState))

    member this.captureWindow : Window = os.windowFromHwnd(captureWindowCell.value.Value.hwnd)

    // Ending a drag must be all-or-nothing in one direction only: whatever
    // fails while tearing down the capture window, the timer, the drag state
    // or the animation window, the terminal callback (onCancel / onEnd) still
    // has to run. It is what releases Desktop.isDragging and the controller's
    // single-drag slot; skipping it leaves the whole application in a state
    // where no further drag can start and updateAppWindows - hence all
    // auto-grouping - never runs again. Each step is therefore isolated, and
    // the whole thing runs at most once per drag.
    // Every way a drag can end funnels through here, so the terminal callback
    // - the one that releases Desktop.isDragging and the controller's
    // single-drag slot - is issued in exactly one place, exactly once.
    // dropAllowed is false when the drag is being abandoned rather than
    // completed: the teardown still runs, but no window is dropped.
    member this.captureEnded(ptScreen) = this.finish(ptScreen, true)

    // Abandon a drag that was left behind (see beginDrag). Releases the mouse
    // capture, the watchdog timer, the capture window and the animation window
    // before handing the slot back.
    member this.abort() =
        DragTrace.log (fun () -> sprintf "[d%d] abort: abandoning drag" dragId)
        this.finish(ptScreenCell.value, false)

    member private this.finish(ptScreen, dropAllowed) =
        if hasEnded.not then
            hasEnded <- true
            DragTrace.log (fun () -> sprintf "[d%d] captureEnded: state=%s pt=%A drop=%b" dragId (match dragStateCell.value with Some s -> s.GetType().Name | None -> "none") ptScreen dropAllowed)
            let step name f =
                try f() with ex -> DragTrace.log (fun () -> sprintf "[d%d] captureEnded: %s FAILED %s" dragId name (ex.ToString()))
            let state = dragStateCell.value
            step "releaseCapture" <| fun() -> this.captureWindow.releaseCapture()
            step "disposeCaptureWindow" <| fun() -> (captureWindowCell.value.Value :?> IDisposable).Dispose()
            step "disposeTimer" timer.Dispose
            step "disposeState" <| fun() -> state.iter(fun s -> s.dispose())
            step "disposeAnimation" <| fun() -> animationWindowCell.value.iter(fun window -> window.Dispose())
            let notifyTargets() =
                info.targets.values.iter <| fun target ->
                    step "target.dragEnd" <| fun() -> target.dragEnd()
            let notifyNotifications() =
                info.notifications.items.iter <| fun n ->
                    step "notification.dragEnd" <| fun() -> n.dragEnd()
            match state with
            | Some(:? DragDetectingState) ->
                // Call target.dragEnd on cancel too so the source decorator can
                // finalize state (e.g. reduce multi-select after a click that
                // never produced a drag past the 5px threshold). target.dragEnd
                // is idempotent — it just resets transient drag-state flags.
                // notifications.dragEnd is NOT called here because it pairs with
                // notifications.dragBegin (only emitted from dragDetect.onBegin).
                notifyTargets()
                step "onCancel" info.onCancel
            | Some(:? DragCapturedState) ->
                notifyTargets()
                notifyNotifications()
                step "onEnd" info.onEnd
            | Some(:? DragFloatingState) ->
                notifyTargets()
                notifyNotifications()
                if dropAllowed then step "onDrop" <| fun() -> info.onDrop(ptScreen)
                step "onEnd" info.onEnd
            | _ ->
                // No state yet (the watchdog timer can tick before dragDetect
                // has set one): still hand the slot back.
                notifyTargets()
                step "onCancel" info.onCancel
            DragTrace.log (fun () -> sprintf "[d%d] captureEnded: done" dragId)

    member this.wndProc (msg:Win32Message) =
        let ptScreen() =
            let pt = msg.lParam.location
            let ptScreen = this.captureWindow.ptToScreen(pt)
            ptScreenCell.set(ptScreen)
            ptScreen
        match msg.msg with
        | WindowMessages.WM_MOUSEMOVE ->
            if moveCount < 3 then
                moveCount <- moveCount + 1
                DragTrace.log (fun () -> sprintf "[d%d] wndProc: WM_MOUSEMOVE #%d at %A" dragId moveCount (ptScreen()))
            // Also check physical mouse button state during mouse move
            // This catches the case where mouse was released but no up event was received
            if not (isLeftMouseButtonDown()) then
                DragTrace.log (fun () -> sprintf "[d%d] wndProc: button up during move" dragId)
                this.captureEnded(ptScreen())
            else
                dragStateCell.value.Value.mouseMove(ptScreen())
        | WindowMessages.WM_MOUSELEAVE
        | WindowMessages.WM_LBUTTONUP ->
            DragTrace.log (fun () -> sprintf "[d%d] wndProc: msg %d" dragId msg.msg)
            this.captureEnded(ptScreen())
        | _ -> ()
        msg.def()

    member this.dragFloat() =
        DragTrace.log (fun () -> sprintf "[d%d] dragFloat" dragId)
        this.setNextState <| DragFloatingState({
            targets = info.targets
            imageOffset = info.imageOffset.mulf(dragScale, dragScale)
            animationWindow = animationWindowCell.value.Value
            onDragIn = fun (targetHwnd, ptScreen) ->
                this.dragEnter(targetHwnd, ptScreen, false)
        })

    member this.dragEnter(targetHwnd, ptScreen, isInitial) =
        let target = info.targets.find(targetHwnd)
        let targetWindow = os.windowFromHwnd(targetHwnd)
        let ptTarget = targetWindow.ptToClient(ptScreen)
        let accepted = target.dragEnter info.data ptTarget
        DragTrace.log (fun () -> sprintf "[d%d] dragEnter: target=%X initial=%b accepted=%b" dragId (targetHwnd.ToInt64()) isInitial accepted)
        if accepted then
            this.setNextState <| DragCapturedState({
                target = target
                targetHwnd = targetHwnd
                targetWindow = targetWindow
                onDragOut = fun(ptScreen) -> 
                    let targetHwnd = os.windowAtPt(ptScreen).hwnd
                    match info.targets.tryFind(targetHwnd) with
                    | Some(target) -> this.dragEnter(targetHwnd, ptScreen, false)
                    | None -> this.dragFloat()
            })
        else 
            if isInitial then target.dragExit()
            this.dragFloat()

    member this.dragDetect() =
        this.setNextState <| DragDetectingState({
            initialPt = info.initialPt
            onBegin = fun() ->  
                animationWindowCell.value <-
                    let animationWindow = AnimationWindow(os)
                    animationWindow.setAlpha(byte(0xAA))
                    try
                        //this may fail if the image coming back is too small
                        animationWindow.setImage(info.image().scale(dragScale))
                    with _ -> ()
                    Some(animationWindow)
                info.targets.values.iter <| fun target -> target.dragBegin()
                info.notifications.items.iter <| fun n -> n.dragBegin()
                info.onBegin()
                this.dragEnter(info.initialHwnd, info.initialPt, true)
        })

    member this.start() =
        if captureWindowCell.value.IsSome then failwith "already started"
        captureWindowCell.set(Some(os.createWindow this.wndProc 0 0))
        this.captureWindow.setCapture()
        DragTrace.log (fun () -> sprintf "[d%d] start: captureHwnd=%X hasCapture=%b lbuttonDown=%b fgHwnd=%X fgThread=%d myThread=%d" dragId
                                      (this.captureWindow.hwnd.ToInt64()) this.captureWindow.hasCapture (isLeftMouseButtonDown())
                                      (WinUserApi.GetForegroundWindow().ToInt64())
                                      (Win32Helper.GetWindowThreadId(WinUserApi.GetForegroundWindow()))
                                      (Win32Helper.GetWindowThreadId(this.captureWindow.hwnd)))
        // Use shorter interval (50ms) for more responsive mouse button state detection
        timer.Interval <- 50
        timer.Tick.Add <| fun _ ->
            // Check if capture is lost OR if mouse button is physically released
            // This handles the case where mouse up event is missed (e.g., released outside tab area)
            if this.captureWindow.hasCapture.not || not (isLeftMouseButtonDown()) then
                DragTrace.log (fun () -> sprintf "[d%d] timer: hasCapture=%b lbuttonDown=%b" dragId this.captureWindow.hasCapture (isLeftMouseButtonDown()))
                this.captureEnded(ptScreenCell.value)
        timer.Start()
        this.dragDetect()

type DragDropController(parent:IDragDropParent) =
    let lockObj = obj()
    let withLock = lock lockObj
    let Cell = CellScope(true, false)
    let targetsCell = Cell.create(Map2())
    let notificationsCell = Cell.create(Set2())
    let dragActionCell = Cell.create(None : DragAction option)
    let mutable nextDragId = 0

    interface IDragDrop with
        member x.registerNotification(notify) = withLock <| fun() ->
            notificationsCell.map(fun l -> l.add notify)
        member x.unregisterNotification(notify) = withLock <| fun() ->
            notificationsCell.map(fun l -> l.remove notify)
        member x.registerTarget((hwnd, target)) = withLock <| fun() ->
            targetsCell.map(fun targets -> targets.add hwnd target)
        member x.unregisterTarget(hwnd) = withLock <| fun() ->
            targetsCell.map(fun targets -> targets.remove hwnd)
        member x.beginDrag((initialHwnd, image, imageOffset, initialPt, data)) = withLock <| fun() ->
            nextDragId <- nextDragId + 1
            let dragId = nextDragId
            DragTrace.log (fun () -> sprintf "[d%d] beginDrag: strip=%X pt=%A busy=%b" dragId (initialHwnd.ToInt64()) initialPt dragActionCell.value.IsSome)
            // A new mouse-down on a tab means the previous drag is over,
            // whatever state its own teardown left behind. Abandon it properly
            // - mouse capture, watchdog timer, capture window and animation
            // window all have to go, and its terminal callback has to run -
            // rather than only dropping the reference, which would leave those
            // alive and a later tick of its timer free to disturb this drag.
            // Without this, one stuck slot silently swallowed every later drag
            // for the rest of the session.
            match dragActionCell.value with
            | Some(stale) ->
                (try stale.abort() with _ -> ())
                dragActionCell.set(None)
            | None -> ()
            if dragActionCell.value.IsNone then 
                let dragAction = DragAction({
                    targets = targetsCell.value
                    notifications = notificationsCell.value
                    initialHwnd = initialHwnd
                    image = image
                    imageOffset = imageOffset
                    initialPt = initialPt
                    data = data
                    onCancel = fun() -> 
                        dragActionCell.set(None)
                    onBegin = fun() -> 
                        parent.dragBegin()
                    onDrop = fun pt ->
                        parent.dragDrop(pt, data)
                    onEnd = fun() ->    
                        parent.dragEnd()
                        dragActionCell.set(None)
                }, dragId)
                dragActionCell.set(Some(dragAction))
                dragAction.start()
            ()

