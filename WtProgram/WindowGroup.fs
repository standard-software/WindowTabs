namespace Bemo
open System
open System.Collections.Generic
open System.Diagnostics
open System.Drawing
open System.Drawing.Imaging
open System.IO
open System.Reflection
open System.Threading
open System.Windows.Forms
open Bemo.Win32.Forms
open Newtonsoft.Json.Linq

// Per-exe margin settings loaded from Settings/Window_Margin.json
module WindowMarginSettings =
    // Cache: exe name (lowercase) -> (top, left, right, bottom)
    let mutable private marginCache : Dictionary<string, (int * int * int * int)> option = None

    let private loadSettings() =
        let dict = Dictionary<string, (int * int * int * int)>(StringComparer.OrdinalIgnoreCase)
        try
            let exePath = Assembly.GetExecutingAssembly().Location
            let exeDir = Path.GetDirectoryName(exePath)
            let jsonPath = Path.Combine(exeDir, "Settings", "Window_Margin.json")
            if File.Exists(jsonPath) then
                let json = File.ReadAllText(jsonPath)
                let jobj = JObject.Parse(json)
                for prop in jobj.Properties() do
                    let exeName = prop.Name
                    match prop.Value with
                    | :? JObject as marginObj ->
                        let getInt (key:string) =
                            match marginObj.getInt32(key) with
                            | Some(v) -> v
                            | None -> 0
                        let top = getInt "top"
                        let left = getInt "left"
                        let right = getInt "right"
                        let bottom = getInt "bottom"
                        dict.[exeName] <- (top, left, right, bottom)
                        System.Diagnostics.Debug.WriteLine(sprintf "[WindowMargin] Loaded: %s -> (%d,%d,%d,%d)" exeName top left right bottom)
                    | _ -> ()
            else
                System.Diagnostics.Debug.WriteLine(sprintf "[WindowMargin] Settings file not found: %s" jsonPath)
        with ex ->
            System.Diagnostics.Debug.WriteLine(sprintf "[WindowMargin] Error loading settings: %s" ex.Message)
        dict

    let getMargin(exeName: string) =
        if marginCache.IsNone then
            marginCache <- Some(loadSettings())
        match marginCache.Value.TryGetValue(exeName) with
        | true, margin -> margin
        | false, _ -> (0, 0, 0, 0)

    let reload() =
        marginCache <- Some(loadSettings())

type WindowGroup(enableSuperBar:bool, plugins:List2<IPlugin>) as this =
    let Cell = CellScope(true)
    let _bb = Blackboard()
    let invoker = InvokerService.invoker
    let _os = OS()
    let addedEvent = Event<_>()
    let movedEvent = Event<IntPtr*int>()
    let removedEvent = Event<_>()
    let exitedEvent = Event<_>()
    let mouseLLEvent = Event<Int32 * Pt * IntPtr>()
    let flashEvent = Event<_>()
    let keyboardLLEvent = Event<Int32 * KBDLLHOOKSTRUCT>()
    let foregroundEvent = Event<_>()

    let isDestroyed = Cell.create(false)
    let zorderCell = Cell.create(List2<IntPtr>())
    // Set of inactive tabs that the user has "selected" via Shift/Ctrl click.
    // The active tab (zorder.head) is NEVER part of this set; treat it
    // implicitly as another action target alongside the selected ones. The
    // set is cleared by a plain left click on any tab and by adding/removing
    // windows from the group.
    let selectedTabsCell = Cell.create(Set2<IntPtr>())
    let prevTop = Cell.create(None)
    let placement = Cell.create(None:Option<Rect * OSWindowPlacement>)
    let windowsCell = Cell.create(Set2())
    let _ts = ref None 
    let inMoveSize = Cell.create(false)
    // Thread-safe mirror of inMoveSize for cross-thread reads (the main
    // thread's untabbable-window scan must spare groups whose child windows
    // are parked off-screen during a move/size)
    [<VolatileField>]
    let mutable inMoveSizeSnapshot = false
    // While the group is minimizing/restoring, ignore HSHELL_FLASH:
    // background windows whose activation is denied during the state change
    // would otherwise flash their tab (group-thread only)
    let mutable suppressFlashUntil = DateTime.MinValue
    // Echoes of the group's own batch minimize/restore: every window changed
    // by minimizeAll/restoreAll fires its own MINIMIZESTART/END, and one
    // arriving late — after a newer user operation — used to flip the whole
    // group back (the minimize<->restore oscillation). Events matching a
    // recorded expectation are consumed and ignored (group-thread only).
    let pendingMinMaxEchoes = Dictionary<IntPtr * WinEvent, DateTime>()
    // After a group restore, the siblings un-minimize asynchronously and each
    // surfaces over the window the user restored. These track that window and
    // a deadline so every sibling-surfacing event can push it back to the
    // front (a single delayed timer fired too late — the group thread was
    // busy processing the surfacing events). (group-thread only)
    let mutable restoreFrontHwnd = IntPtr.Zero
    let mutable restoreFrontUntil = DateTime.MinValue
    let foregroundCell = Cell.create(_os.foreground.hwnd)
    let prevForegroundCell = ref None
    let isMinimized hwnd = this.os.windowFromHwnd(hwnd).isMinimized
    let hookCleanup = Cell.create(Map2<IntPtr, IDisposable>())
    let shellHookWindow = Cell.create(None)
    let winEventHandler = Cell.create(None)
    let isDraggingCell = Cell.create(false)
    let isDraggingExport = Cell.export <| fun() -> isDraggingCell.value
    let zorderExport = Cell.export <| fun() -> zorderCell.value
    let isVisibleCell = Cell.create(false)

    let isMaximizedExport = Cell.export <| fun() ->
        zorderCell.value.tryHead.exists(fun hwnd -> this.os.windowFromHwnd(hwnd).isMaximized)

    let isFullscreenExport = Cell.export <| fun() ->
        zorderCell.value.tryHead.exists(fun hwnd -> this.os.windowFromHwnd(hwnd).isFullscreen)

    let boundsExport = Cell.export <| fun() ->
        placement.value.bind <| fun(rect,placement) -> 
            if isVisibleCell.value then Some(rect) else None

    let isForegroundExport = Cell.export <| fun() ->
        zorderCell.value.any((=) foregroundCell.value)

    // Per-group tab position: always has a concrete value (TopLeft/TopRight)
    let mutable perGroupTabPosition : string = "TopRight"
    // Per-group snap tab height margin: always has a concrete value
    let mutable perGroupSnapTabHeightMargin : bool = false

    // Track the margin-shrunk size for each hwnd, so we know when to compensate on read
    // Key: hwnd, Value: (shrunkWidth, shrunkHeight) that was last applied
    let marginShrunkSizes = Cell.create(Map.empty<IntPtr, (int * int)>)

    member this.isSuperBarEnabled = enableSuperBar

    member this.init(ts:TabStrip) =
        _ts := Some(ts)

        // Apply default setting for tab position
        let defaultPosition = Services.settings.getValue("tabPositionByDefault") :?> string
        perGroupTabPosition <- defaultPosition
        let alignment =
            match defaultPosition with
            | "TopLeft" -> TopLeft
            | _ -> TopRight
        ts.setAlignment(ts.direction, alignment)

        // Apply default setting for snap tab height margin
        let defaultSnapMargin =
            try Services.settings.getValue("snapTabHeightMargin") :?> bool
            with _ -> false
        perGroupSnapTabHeightMargin <- defaultSnapMargin

        // Apply default setting for hiding tabs when inside
        let hideTabsMode = Services.settings.getValue("hideTabsWhenDownByDefault") :?> string
        match hideTabsMode with
        | "down" -> _bb.write("autoHide", true)
        | "doubleclick" -> _bb.write("autoHideDoubleClick", true)
        | "never" -> () // Do nothing
        | _ -> _bb.write("autoHideDoubleClick", true)  // Default to "doubleclick" for invalid/unknown values

        winEventHandler.set(Some(
            _os.setSingleWinEvent WinEvent.EVENT_SYSTEM_FOREGROUND <| fun(hwnd) -> 
                this.main(hwnd, WinEvent.EVENT_SYSTEM_FOREGROUND)))
            
        shellHookWindow.set(Some(_os.registerShellHooks this.shellEvents))
        
            
        isMaximizedExport.init()
        isFullscreenExport.init()
        isDraggingExport.init()
        zorderExport.init()
        boundsExport.init()
        isForegroundExport.init()

        // Seed the strip so it has an appearance before its first placement.
        // Appearance and scale go in together (TabStrip.setTabAppearance).
        //
        // There is deliberately NO settings listener here: the decorator owns
        // that (TabStripDecorator.init, "tabAppearance" -> updateTsPlacement),
        // and it re-pushes the appearance AND the placement bounds from one
        // scale query. A second listener that pushed only the appearance would
        // leave the strip box sized for the old settings until the next bounds
        // event.
        let seedScale = this.dpiScale
        this.ts.setTabAppearance(this.tabAppearanceAt seedScale, seedScale)

        // Listen for tabPositionByDefault changes (apply to all groups)
        Services.settings.notifyValue "tabPositionByDefault" <| fun value ->
            this.invokeAsync <| fun() ->
                let position = unbox<string>(value)
                perGroupTabPosition <- position
                let alignment =
                    match position with
                    | "TopLeft" -> TopLeft
                    | _ -> TopRight
                ts.setAlignment(ts.direction, alignment)

        // Listen for snapTabHeightMargin changes (apply to all groups)
        Services.settings.notifyValue "snapTabHeightMargin" <| fun value ->
            this.invokeAsync <| fun() ->
                perGroupSnapTabHeightMargin <- unbox<bool>(value)

        // Listen for hideTabsWhenDownByDefault changes
        Services.settings.notifyValue "hideTabsWhenDownByDefault" <| fun value ->
            this.invokeAsync <| fun() ->
                let hideMode = unbox<string>(value)
                // Clear all hide settings first
                _bb.write("autoHide", false)
                _bb.write("autoHideMaximized", false)
                _bb.write("autoHideDoubleClick", false)
                // Set new mode
                match hideMode with
                | "down" -> _bb.write("autoHide", true)
                | "doubleclick" -> _bb.write("autoHideDoubleClick", true)
                | "never" -> () // Do nothing
                | _ -> _bb.write("autoHideDoubleClick", true)  // Default to "doubleclick" for invalid/unknown values

        Cell.listen <| fun() ->
            this.ts.zorder <- zorderCell.value.map(Tab)
            
        Cell.listen <| fun() ->
            this.ts.foreground <- this.foregroundTab
        
        Cell.listen <| fun() ->
            //this is important, we dont' want to leave the parent set to the previous hwnd
            //which was removed, this can cause issues when that window gets added to another
            //group on another thread during drag / drop
            this.setTsParent(if this.isEmpty.not then zorderCell.value.head else IntPtr.Zero)

        Cell.listen <| fun() ->
            // Check if tabs should be hidden due to fullscreen window
            let hideForFullscreen =
                try
                    let hideTabsOnFullscreen = Services.settings.getValue("hideTabsOnFullscreen") :?> bool
                    hideTabsOnFullscreen && isFullscreenExport.value
                with _ -> false
            this.ts.visible <- isVisibleCell.value && not hideForFullscreen

        // Listen for hideTabsOnFullscreen setting changes
        Services.settings.notifyValue "hideTabsOnFullscreen" <| fun _ ->
            this.invokeAsync <| fun() ->
                // Trigger visibility update
                let hideForFullscreen =
                    try
                        let hideTabsOnFullscreen = Services.settings.getValue("hideTabsOnFullscreen") :?> bool
                        hideTabsOnFullscreen && isFullscreenExport.value
                    with _ -> false
                this.ts.visible <- isVisibleCell.value && not hideForFullscreen

        Services.registerLocal(this)

        plugins.iter <| fun p -> p.init()

    member this.foreground
        with get() = foregroundCell.value
        and set(value) =
            let prev = foregroundCell.value
            if prev <> value then
                foregroundCell.set(value)
                foregroundEvent.Trigger()

    member this.foregroundTab =
        if this.windows.contains(this.foreground) then
            Some(Tab(this.foreground))
        else
            None

    member this.postMouseLL(msg, pt, data) = mouseLLEvent.Trigger(msg, pt, data)
    member this.postKeyboardLL(key, data) = keyboardLLEvent.Trigger(key, data)
    member this.mouseLL = mouseLLEvent.Publish
    member this.keyboardLL = keyboardLLEvent.Publish
    member this.bb = _bb
    member this.ts : TabStrip = _ts.Value.Value
    
    member this.isPointInTs (pt:Pt) =
        let hwnd = Win32Helper.GetTopLevelWindowFromPoint(pt.Point)
        this.ts.hwnd = hwnd

    member this.isPointInGroup (pt:Pt) =
        let hwnd = Win32Helper.GetTopLevelWindowFromPoint(pt.Point)
        this.ts.hwnd = hwnd || this.windows.contains(hwnd)
    
    member this.topWindow = zorderCell.value.head
   

    member this.windows : Set2<IntPtr> = windowsCell.value

    
    // Appearance exactly as the user configured it, in 96-dpi design units.
    // The settings UI reads this one (through Services.program.tabAppearanceInfo),
    // so a scaled value can never be written back to the settings file.
    member this.tabAppearanceRaw = Services.settings.getValue("tabAppearance").cast<TabAppearanceInfo>()

    // DPI scale of the monitor this group's tab strip is displayed on.
    //
    // The strip is anchored on the top edge of the tracked window, so the
    // monitor is chosen from that edge rather than from the whole window
    // rectangle (Dpi.scaleForStripAnchor explains why, and why the strip's own
    // height is deliberately not part of the query).
    //
    // This is a LIVE query, so it is read in exactly two places: once per
    // placement update (TabStripDecorator.placement, whose result is then
    // handed to everything downstream) and once when a group seeds its strip.
    // Anything that needs "the scale the strip is currently drawn at" must use
    // TabStrip.scale instead, or it can disagree with the strip for the frames
    // during which a window straddles a monitor boundary.
    member this.dpiScale =
        try
            match placement.value with
            | Some(rect, _) when rect.width > 0 && rect.height > 0 -> Dpi.scaleForStripAnchor(rect)
            | _ -> Dpi.scaleForHwnd(this.ts.hwnd)
        with _ -> 1.0

    // Appearance in device pixels for a given monitor scale. Everything that
    // lays out or positions the strip - decorator bounds, snap margin, sprite
    // metrics - goes through here, so one conversion covers them all and the
    // strip box can never end up scaled differently from its contents.
    member this.tabAppearanceAt(scale: float) = Dpi.scaleAppearance scale this.tabAppearanceRaw

    // (There is deliberately no `tabAppearance` shorthand for
    // `tabAppearanceAt this.dpiScale`. Such a property looks like a field but
    // issues a MonitorFromRect + GetDpiForMonitor pair on every read, and two
    // reads a few microseconds apart can straddle a boundary crossing and
    // return different scales. Callers name the scale they mean.)

    member private this.withUpdate f =
        Cell.beginUpdate()
        let result = f()
        Cell.endUpdate()
        result

    member this.invokeSync f =
        invoker.invoke (fun() -> this.withUpdate f)

    member this.invokeAsync f =
        invoker.asyncInvoke <| fun() -> this.withUpdate f

    member private this.updateIsVisible() =
        // Check if all windows in the group are cloaked (on another virtual desktop)
        // This is particularly important for UWP apps which use cloaking when switching virtual desktops
        let allWindowsCloaked =
            if this.isEmpty then
                false
            else
                zorderCell.value.where(isMinimized >> not).all(fun hwnd ->
                    let window = this.os.windowFromHwnd(hwnd)
                    window.isCloaked)

        isVisibleCell.value <-
            this.isEmpty.not &&
            zorderCell.value.where(isMinimized >> not).tryHead.IsSome &&
            not allWindowsCloaked

    member private this.adjustChildWindows = fun() ->
        // Skip entirely while the top window cannot provide usable bounds:
        // - degenerate (0,0,0,0) bounds from a window being torn down under
        //   load (issue #13, closing LibreOffice), and
        // - a MINIMIZED top window, whose GetWindowRect is the tiny iconic
        //   rect (~160x30) — propagating either one shrinks every other
        //   window of the group to a minimal size.
        let topIsValid =
            match zorderCell.value.tryHead with
            | Some(topHwnd) ->
                let w = this.os.windowFromHwnd(topHwnd)
                let b = w.bounds
                w.isWindow && w.isMinimized.not && b.width > 0 && b.height > 0
            | None -> false
        if topIsValid then
            // This is the de-facto "restore follows the group" path: when the
            // user restores one window (e.g. from the taskbar), the siblings
            // are still minimized here and adjustWindowPlacement below
            // un-minimizes them via SetWindowPlacement. Remember that so the
            // restored window can be kept in front afterwards.
            let restoredHwnd = zorderCell.value.where(isMinimized >> not).tryHead
            let siblingsWereMinimized = zorderCell.value.tail.any(isMinimized)
            // Publish the restore-front target BEFORE un-minimizing the
            // siblings, so adjustWindowPlacement can insert each surfacing
            // sibling directly behind it (no flicker on top).
            if siblingsWereMinimized then
                restoredHwnd |> Option.iter (fun front ->
                    restoreFrontHwnd <- front
                    restoreFrontUntil <- DateTime.Now.AddMilliseconds(1500.0))
            zorderCell.value.tail.iter(this.adjustWindowPlacement)

            // After initial placement, adjust sizes again to ensure DPI is considered
            match zorderCell.value.tryHead with
            | Some(topHwnd) ->
                let topWindow = this.os.windowFromHwnd(topHwnd)
                let topBounds = topWindow.bounds
                // When the top window is maximized, its bounds already match the
                // monitor work rect — skip per-exe margin on both sides.
                let topMaximized = topWindow.isMaximized

                // If the top window has a margin, always expand to get group bounds
                let groupBounds =
                    if this.hasExeMargin(topHwnd) && not topMaximized then
                        this.removeExeMarginForRead(topHwnd, topBounds)
                    else topBounds

                // Move all background windows again with the correct size
                zorderCell.value.tail.iter(fun hwnd ->
                    let window = this.os.windowFromHwnd(hwnd)
                    if window.isMinimized.not then
                        // Apply per-exe margin for this background window
                        let targetBounds =
                            if topMaximized then groupBounds
                            else this.applyExeMarginForWrite(hwnd, groupBounds)
                        let currentBounds = window.bounds
                        // Keep current position but use correct size
                        let correctBounds = Rect(currentBounds.location, targetBounds.size)
                        System.Diagnostics.Debug.WriteLine(sprintf "[ExeMargin] 2nd pass: %s group=(%d,%d,%d,%d) target=(%d,%d,%d,%d) correct=(%d,%d,%d,%d)"
                            window.pid.exeName
                            groupBounds.x groupBounds.y groupBounds.width groupBounds.height
                            targetBounds.x targetBounds.y targetBounds.width targetBounds.height
                            correctBounds.x correctBounds.y correctBounds.width correctBounds.height)
                        // Skip the move if size already matches - SetWindowPos is expensive and apps that
                        // fire EVENT_OBJECT_LOCATIONCHANGE without actually moving (e.g. LibreOffice) would
                        // otherwise trigger redundant work and follow-up events on every spurious change.
                        if currentBounds.size <> correctBounds.size then
                            // Async (SWP_ASYNCWINDOWPOS) so a busy just-restored
                            // app can't stall the strip thread here; z-order is
                            // untouched so this can't disturb the fronting done
                            // afterwards.
                            if siblingsWereMinimized then window.moveAsync(correctBounds)
                            else window.move(correctBounds)
                        // Track the margin-shrunk size for this window
                        if this.hasExeMargin(hwnd) && not topMaximized then
                            marginShrunkSizes.set(marginShrunkSizes.value.Add(hwnd, (correctBounds.width, correctBounds.height)))
                )
            | None -> ()

            // If this pass just restored minimized siblings, keep the window
            // the user restored in front of them.
            if siblingsWereMinimized then
                restoredHwnd |> Option.iter this.bringRestoredToFront

    // Put the user-restored window in front of the group and keep it there:
    // siblings restored in the background may raise or activate themselves a
    // moment later (Office, Visual Studio), so one settle pass re-fronts it
    // unless focus has already moved outside the group.
    member private this.bringRestoredToFront(hwnd) =
        let frontOrder = List2([hwnd]).appendList(zorderCell.value.where((<>) hwnd))
        this.setZorder(frontOrder)
        this.os.setZorder(frontOrder)
        // Arm the reassert window: siblings un-minimize asynchronously over
        // the next ~1s and each surfaces over this window. reassertRestoreFront
        // (called from the MINIMIZEEND cascade + a backstop timer) pushes it
        // back to the front each time until the deadline.
        restoreFrontHwnd <- hwnd
        restoreFrontUntil <- DateTime.Now.AddMilliseconds(1500.0)
        ThreadHelper.cancelablePostBack 900 (fun() -> this.invokeAsync this.reassertRestoreFront) |> ignore

    // Keep the user-restored window on top while its siblings are still
    // surfacing (see restoreFrontHwnd). Reorders z-order only; re-activates
    // only if a sibling stole activation (Office/VS self-activate). Stops once
    // focus leaves the group or the deadline passes.
    member private this.reassertRestoreFront() =
        let hwnd = restoreFrontHwnd
        if hwnd <> IntPtr.Zero && DateTime.Now < restoreFrontUntil then
            try
                let fg = this.os.foreground.hwnd
                if this.windows.contains(fg) &&
                   this.windows.contains(hwnd) &&
                   this.os.windowFromHwnd(hwnd).isMinimized.not then
                    let z = this.os.windowZorders
                    let idxOf h = z.tryFind(h).def(Int32.MaxValue)
                    let coveredBySibling =
                        zorderCell.value.any(fun h -> h <> hwnd && idxOf h < idxOf hwnd)
                    if coveredBySibling then
                        this.os.setZorder(List2([hwnd]).appendList(zorderCell.value.where((<>) hwnd)))
                        if fg <> hwnd then
                            this.os.windowFromHwnd(hwnd).setForeground(false)
            with _ -> ()

    member private this.makeTopWindowForeground() =
        match zorderCell.value.where(isMinimized >> not).tryHead with
        | Some(top) -> 
            let window = this.os.windowFromHwnd(top)
            window.setForeground(false)
        | None -> ()

    member private this.hideChildWindows() =
        zorderCell.value.tail.where(isMinimized >> not).iter(fun window -> this.os.windowFromHwnd(window).hideOffScreen(None))

    member private this.inZorder(windows:List2<IntPtr>) = this.windows.items.sortBy(fun hwnd -> this.os.windowFromHwnd(hwnd).zorder)

    member private this.setZorder(newZorder:List2<_>) =
        if zorderCell.value.list <> newZorder.list then
            prevTop.set(zorderCell.value.tryHead)
            zorderCell.set(newZorder)

    member private this.saveZorder() =
        this.setZorder(this.inZorder(this.windows.items))

    member private this.setWindows(newWindows) =
        windowsCell.set(newWindows)
        this.saveZorder()
        this.updateIsVisible()

    member private this.isEmpty : bool = this.windows.items.isEmpty

    member private this.bringToTop hwnd =
        this.setZorder(zorderCell.value.moveToEnd((=)hwnd))

    member this.isRenamed hwnd = Services.program.getWindowNameOverride(hwnd).IsSome
    
    member private this.hwndText hwnd = 
        let window = this.os.windowFromHwnd(hwnd)
        let text = Services.program.getWindowNameOverride(hwnd).def(window.text)
        // DebugMode
        // if System.Diagnostics.Debugger.IsAttached then sprintf "%X - %s" hwnd text else text
        text

    member private this.getTabInfo(hwnd) =
        let window = this.os.windowFromHwnd(hwnd)
        {
            text = this.hwndText hwnd
            isRenamed = this.isRenamed hwnd
            iconSmall = window.iconSmall
            iconBig = window.iconBig
            preview = fun() ->
                try
                    if window.isMinimized then
                        let size = this.placementBounds.size
                        let icon = window.iconBig
                        let iconSize = icon.Size.Sz
                        let img = Img(size)
                        let g = img.graphics
                        g.FillRectangle(SolidBrush(Color.LightGray), Rect(Pt(), size).Rectangle)
                        g.DrawIcon(icon, ((size.width - iconSize.width).float / 2.0).Int32, ((size.height - iconSize.height).float / 2.0).Int32)
                        img
                    else
                        Img(Win32Helper.PrintWindow(hwnd))
                with ex -> Img(Sz(1, 1))
        }
    
    member private this.setTabInfo(hwnd) =
        this.ts.setTabInfo(Tab(hwnd), this.getTabInfo(hwnd))

    member private this.setTsParent(parentHwnd) =
        this.os.windowFromHwnd(this.ts.hwnd).setParent(this.os.windowFromHwnd(parentHwnd))
        
    member this.isPinned(hwnd) = this.ts.isPinned(Tab(hwnd))
    // Thread-safe version for cross-thread reads (e.g., save from main thread)
    member this.isPinnedThreadSafe(hwnd) = this.ts.isPinnedThreadSafe(Tab(hwnd))
    // Real on-screen tab order for cross-thread reads (thread-safe snapshot)
    member this.visualOrderHwndsThreadSafe = this.ts.visualOrderThreadSafe.map(fun (Tab h) -> h)
    member this.pinTab(hwnd) =
        this.ts.pinTab(Tab(hwnd))
        Services.program.setWindowPinned(hwnd, true)
    member this.unpinTab(hwnd) =
        this.ts.unpinTab(Tab(hwnd))
        Services.program.setWindowPinned(hwnd, false)
    member this.pinAll() =
        this.ts.pinAll()
        this.ts.visualOrder.iter(fun (Tab h) -> Services.program.setWindowPinned(h, true))
    member this.unpinAll() =
        this.ts.unpinAll()
        this.ts.visualOrder.iter(fun (Tab h) -> Services.program.setWindowPinned(h, false))
    member this.pinnedCount = this.ts.pinnedTabs.count
    member this.allPinned = this.ts.pinnedTabs.count = this.ts.tabs.count
    member this.nonePinned = this.ts.pinnedTabs.count = 0

    // ----- Multi-tab selection -----
    // The active tab is treated as an implicit action target alongside the
    // selected ones, so selectedTabsCell explicitly excludes it. Helpers
    // below maintain that invariant. WindowGroup is the source of truth (in
    // IntPtr form); it mirrors changes into the underlying TabStrip (in Tab
    // form) so the sprite layer can read them per-frame without translation.
    member private this.activeHwnd = zorderCell.value.tryHead
    member private this.pushSelectedToTabStrip(next: Set2<IntPtr>) =
        let asTabs = next.items.map(Tab) |> Set2
        this.ts.setSelectedTabs(asTabs)
    member private this.applySelected(next: Set2<IntPtr>) =
        let cur = selectedTabsCell.value
        if next <> cur then
            selectedTabsCell.set(next)
            this.pushSelectedToTabStrip(next)
    member this.selectedTabs = selectedTabsCell.value
    member this.isSelected(hwnd: IntPtr) = selectedTabsCell.value.contains(hwnd)
    member this.clearSelected() = this.applySelected(Set2<IntPtr>())
    // Set or clear the selected flag for a single tab. Setting it on the
    // active tab is a no-op (active is always the implicit target).
    member this.setSelected(hwnd: IntPtr, isSel: bool) =
        let isActive = this.activeHwnd.exists((=) hwnd)
        if isActive then ()
        else
            let cur = selectedTabsCell.value
            let next =
                if isSel then cur.add(hwnd)
                else cur.remove(hwnd)
            this.applySelected(next)
    member this.toggleSelected(hwnd: IntPtr) =
        this.setSelected(hwnd, this.isSelected(hwnd).not)
    // Select the inclusive range of tabs from the currently active tab to
    // `targetHwnd` in visualOrder. The active tab itself is NOT placed into
    // the selected set (it is the implicit target). Called by Shift+click.
    member this.selectRange(targetHwnd: IntPtr) =
        match this.activeHwnd with
        | None ->
            this.applySelected(Set2(List2([targetHwnd])))
        | Some active when active = targetHwnd ->
            this.clearSelected()
        | Some active ->
            let order = this.ts.visualOrder.list |> List.map (fun (Tab h) -> h)
            let idxActive = order |> List.tryFindIndex ((=) active)
            let idxTarget = order |> List.tryFindIndex ((=) targetHwnd)
            match idxActive, idxTarget with
            | Some a, Some t ->
                let lo = min a t
                let hi = max a t
                let inRange = order |> List.mapi (fun i h -> i, h) |> List.filter (fun (i, _) -> i >= lo && i <= hi) |> List.map snd
                let filtered = inRange |> List.filter (fun h -> h <> active)
                let next = Set2(List2(filtered))
                this.applySelected(next)
            | _ ->
                ()
    // Action targets for commands such as close / color / detach: the active
    // tab first (so commands that need a "primary" still get one), then the
    // selected tabs in visualOrder. Excludes the active tab from the
    // selected portion to avoid duplicates.
    member this.actionTargetTabs() =
        let selected = selectedTabsCell.value
        match this.activeHwnd with
        | None -> selected.items.list
        | Some active ->
            let order = this.ts.visualOrder.list |> List.map (fun (Tab h) -> h)
            let othersInOrder = order |> List.filter (fun h -> h <> active && selected.contains(h))
            active :: othersInOrder
    member this.countToLeft(hwnd) = this.ts.countToLeft(Tab(hwnd))
    member this.countToRight(hwnd) = this.ts.countToRight(Tab(hwnd))
    member this.pinLeftTabs(hwnd) =
        this.ts.pinLeftTabs(Tab(hwnd))
        this.ts.visualOrder.iter(fun (Tab h) -> Services.program.setWindowPinned(h, this.ts.isPinned(Tab(h))))
    member this.pinRightTabs(hwnd) =
        this.ts.pinRightTabs(Tab(hwnd))
        this.ts.visualOrder.iter(fun (Tab h) -> Services.program.setWindowPinned(h, this.ts.isPinned(Tab(h))))
    member this.unpinLeftTabs(hwnd) =
        this.ts.unpinLeftTabs(Tab(hwnd))
        this.ts.visualOrder.iter(fun (Tab h) -> Services.program.setWindowPinned(h, this.ts.isPinned(Tab(h))))
    member this.unpinRightTabs(hwnd) =
        this.ts.unpinRightTabs(Tab(hwnd))
        this.ts.visualOrder.iter(fun (Tab h) -> Services.program.setWindowPinned(h, this.ts.isPinned(Tab(h))))

    member this.setTabFillColor(hwnd, color : Color option) =
        this.ts.setTabFillColor(Tab(hwnd), color)
        Services.program.setWindowFillColor(hwnd, color)
    member this.getTabFillColor(hwnd) = this.ts.getTabFillColor(Tab(hwnd))
    // Thread-safe versions for cross-thread reads
    member this.getTabFillColorThreadSafe(hwnd) = this.ts.getTabFillColorThreadSafe(Tab(hwnd))

    member this.setTabUnderlineColor(hwnd, color : Color option) =
        this.ts.setTabUnderlineColor(Tab(hwnd), color)
        Services.program.setWindowUnderlineColor(hwnd, color)
    member this.getTabUnderlineColor(hwnd) = this.ts.getTabUnderlineColor(Tab(hwnd))
    member this.getTabUnderlineColorThreadSafe(hwnd) = this.ts.getTabUnderlineColorThreadSafe(Tab(hwnd))

    member this.setTabBorderColor(hwnd, color : Color option) =
        this.ts.setTabBorderColor(Tab(hwnd), color)
        Services.program.setWindowBorderColor(hwnd, color)
    member this.getTabBorderColor(hwnd) = this.ts.getTabBorderColor(Tab(hwnd))
    member this.getTabBorderColorThreadSafe(hwnd) = this.ts.getTabBorderColorThreadSafe(Tab(hwnd))

    member this.setTabAlign(hwnd, alignment : TabAlign) =
        this.ts.setTabAlign(Tab(hwnd), alignment)
        Services.program.setWindowAlignment(hwnd, Some(alignment))
    // Bulk variant: keeps the tabs' relative order (see TabStrip.setTabsAlign)
    member this.setTabsAlign(hwnds: IntPtr list, alignment : TabAlign) =
        this.ts.setTabsAlign(hwnds |> List.map Tab, alignment)
        hwnds |> List.iter (fun h -> Services.program.setWindowAlignment(h, Some(alignment)))
    member this.getTabAlign(hwnd) = this.ts.getTabAlign(Tab(hwnd))
    member this.alignCountToLeft(hwnd) = this.ts.alignCountToLeft(Tab(hwnd))
    member this.alignCountToRight(hwnd) = this.ts.alignCountToRight(Tab(hwnd))
    member this.alignLeftTabs(hwnd, newAlignment) =
        this.ts.alignLeftTabs(Tab(hwnd), newAlignment)
        this.ts.visualOrder.iter(fun (Tab h) -> Services.program.setWindowAlignment(h, Some(this.ts.getTabAlign(Tab(h)))))
    member this.alignRightTabs(hwnd, newAlignment) =
        this.ts.alignRightTabs(Tab(hwnd), newAlignment)
        this.ts.visualOrder.iter(fun (Tab h) -> Services.program.setWindowAlignment(h, Some(this.ts.getTabAlign(Tab(h)))))

    member this.tabPosition
        with get() = perGroupTabPosition
        and set(value) =
            perGroupTabPosition <- value
            let alignment =
                match value with
                | "TopLeft" -> TopLeft
                | _ -> TopRight
            this.ts.setAlignment(this.ts.direction, alignment)

    member this.perGroupTabPositionValue
        with get() = perGroupTabPosition
        and set(value) =
            perGroupTabPosition <- value
            let alignment =
                match value with
                | "TopLeft" -> TopLeft
                | _ -> TopRight
            this.ts.setAlignment(this.ts.direction, alignment)

    member this.snapTabHeightMargin
        with get() = perGroupSnapTabHeightMargin
        and set(value) = perGroupSnapTabHeightMargin <- value

    member this.hwnd = this.ts.hwnd

    member private this.os : OS = _os
       
    member private this.windowCount = this.windows.count

    member this.placementBounds : Rect = placement.value.map(fst).def(Rect())

    member private this.isTop(hwnd) = zorderCell.value.where(isMinimized >> not).tryHead = Some(hwnd)

    member private this.saveTopWindowPlacement() =
        let window = this.os.windowFromHwnd(zorderCell.value.head)
        // A dying window (e.g. LibreOffice tearing down under load) reports
        // degenerate bounds: GetWindowRect fails and yields (0,0,0,0). Never
        // save such a placement — it would later be applied to every other
        // window in the group.
        let liveBounds = window.bounds
        if  window.isWindow &&
            window.isMinimized.not &&
            liveBounds.width > 0 && liveBounds.height > 0 &&
            this.os.isOnScreen(liveBounds)
            then
            let bounds =
                if window.isMaximized then
                    //windows are placed slightly off screen when maximized, get the bounds of the monitor instead
                    match Mon.fromHwnd(window.hwnd) with
                    | Some(mon) -> mon.workRect.move(-1,-1)
                    | None -> window.bounds
                else window.bounds
            // If the foreground window has a margin, always compensate to get the real group bounds.
            // LINE.exe always has 30px margin, so its bounds are always smaller than the group bounds.
            // Skip when maximized: bounds already match the work rect.
            let adjustedBounds =
                if this.hasExeMargin(window.hwnd) && not window.isMaximized then
                    System.Diagnostics.Debug.WriteLine(sprintf "[ExeMargin] saveTopPlacement: compensating %s bounds=(%d,%d,%d,%d)"
                        window.pid.exeName bounds.x bounds.y bounds.width bounds.height)
                    this.removeExeMarginForRead(window.hwnd, bounds)
                else bounds
            placement.set(Some(adjustedBounds, window.placement))
           
    member private this.waitForDpiChange(hwnd: IntPtr, initialDpi: uint32, maxWaitMs: int) =
        let mutable currentDpi = initialDpi
        let mutable elapsed = 0
        let checkInterval = 10 // Check every 10ms
        
        while elapsed < maxWaitMs && currentDpi = initialDpi do
            System.Threading.Thread.Sleep(checkInterval)
            elapsed <- elapsed + checkInterval
            currentDpi <- WinUserApi.GetDpiForWindow(hwnd)
            
        currentDpi <> initialDpi // Return true if DPI changed

    // Common method to apply window bounds with DPI-aware logic
    member private this.applyWindowBoundsWithDpiHandling(hwnd:IntPtr, bounds:Rect) =
        let window = this.os.windowFromHwnd(hwnd)

        // Skip the move if bounds already match - SetWindowPos is expensive and apps that
        // fire EVENT_OBJECT_LOCATIONCHANGE without actually moving (e.g. LibreOffice) would
        // otherwise trigger redundant work and cascading follow-up events.
        if window.bounds = bounds then () else

        // Get current DPI (before move) and target DPI (after move)
        let currentDpi = WinUserApi.GetDpiForWindow(hwnd)
        let targetDpi =
            // Find other windows in the group (excluding current hwnd)
            let otherWindows = zorderCell.value.where(fun h -> h <> hwnd)
            match otherWindows.tryHead with
            | Some(otherHwnd) ->
                // Use DPI of another window in the group
                WinUserApi.GetDpiForWindow(otherHwnd)
            | None ->
                // No other windows, use current DPI
                currentDpi

        // Use different approach based on DPI change
        if currentDpi <> targetDpi then
            // Different DPI: use position-first approach to handle DPI scaling
            window.setPositionOnly bounds.x bounds.y

            // Wait for DPI change (max 200ms)
            if this.waitForDpiChange(hwnd, currentDpi, 200) then
                // DPI changed, wait a bit more for stabilization
                System.Threading.Thread.Sleep(20)

            // Apply final position with size
            window.move(bounds)
        else
            // Same DPI: move with position and size at once for better performance
            window.move(bounds)

    // Get per-exe margin as (top, left, right, bottom) from Settings/Window_Margin.json
    // Positive = shrink window, Negative = expand window
    member this.getExeMarginRaw(hwnd:IntPtr) =
        let window = this.os.windowFromHwnd(hwnd)
        let exeName = window.pid.exeName
        WindowMarginSettings.getMargin(exeName)

    // The same margin in device pixels for the monitor `bounds` lives on.
    //
    // The JSON values compensate for an app's invisible resize border. That
    // border is itself DPI-scaled by the app, and the values were authored
    // while every coordinate WindowTabs saw was virtualized to 96 dpi. Window
    // rects are real device pixels now, so the margin has to be scaled too.
    //
    // The scale comes from the RECTANGLE being adjusted, not from the group's
    // current monitor: apply-on-write and remove-on-read then use the same
    // factor for the same window on the same monitor, and a window that has
    // moved to a 100% monitor gets the 100% margin removed - which is correct,
    // because its invisible border is now 100% wide as well. Deriving the
    // factor from a single group-level value would make shrink and un-shrink
    // disagree by the ratio of the two monitors.
    member this.getExeMargin(hwnd:IntPtr, bounds:Rect) =
        let (top, left, right, bottom) = this.getExeMarginRaw(hwnd)
        let scale = try Dpi.scaleForRect bounds with _ -> 1.0
        if scale = 1.0 then (top, left, right, bottom)
        else (Dpi.px scale top, Dpi.px scale left, Dpi.px scale right, Dpi.px scale bottom)

    // Check if a window has any non-zero margin. Scale-independent: Dpi.px
    // maps 0 to 0 and never turns a non-zero value into zero.
    member this.hasExeMargin(hwnd:IntPtr) =
        let (top, left, right, bottom) = this.getExeMarginRaw(hwnd)
        top <> 0 || left <> 0 || right <> 0 || bottom <> 0

    // Record that margin was applied to a window (for tracking shrunk state)
    member this.recordMarginApplied(hwnd:IntPtr, width:int, height:int) =
        marginShrunkSizes.set(marginShrunkSizes.value.Add(hwnd, (width, height)))

    // Apply margin when writing bounds to a window (shrink by margin)
    // Left/Top: +margin (move inward), Width/Height: -(left+right)/(top+bottom)
    // Result: window becomes smaller by margin on each side
    member this.applyExeMarginForWrite(hwnd:IntPtr, bounds:Rect) : Rect =
        if this.hasExeMargin(hwnd) then
            let (top, left, right, bottom) = this.getExeMargin(hwnd, bounds)
            let result = Rect(Pt(bounds.x + left, bounds.y + top),
                              Sz(bounds.width - left - right, bounds.height - top - bottom))
            System.Diagnostics.Debug.WriteLine(sprintf "[ExeMargin] Write: %s margin=(%d,%d,%d,%d) input=(%d,%d,%d,%d) output=(%d,%d,%d,%d)"
                (this.os.windowFromHwnd(hwnd).pid.exeName) top left right bottom
                bounds.x bounds.y bounds.width bounds.height
                result.x result.y result.width result.height)
            result
        else bounds

    // Apply reverse margin when reading bounds from a foreground window (expand by margin)
    // Left/Top: -margin (move outward), Width/Height: +(left+right)/(top+bottom)
    // Result: reported bounds become larger by margin on each side
    member private this.removeExeMarginForRead(hwnd:IntPtr, bounds:Rect) : Rect =
        if this.hasExeMargin(hwnd) then
            let (top, left, right, bottom) = this.getExeMargin(hwnd, bounds)
            let result = Rect(Pt(bounds.x - left, bounds.y - top),
                              Sz(bounds.width + left + right, bounds.height + top + bottom))
            System.Diagnostics.Debug.WriteLine(sprintf "[ExeMargin] Read: %s margin=(%d,%d,%d,%d) input=(%d,%d,%d,%d) output=(%d,%d,%d,%d)"
                (this.os.windowFromHwnd(hwnd).pid.exeName) top left right bottom
                bounds.x bounds.y bounds.width bounds.height
                result.x result.y result.width result.height)
            result
        else bounds

    member private this.adjustWindowPlacement(hwnd) =
        let window = this.os.windowFromHwnd(hwnd)
        if placement.value.IsSome then
            let bounds,wp = placement.value.Value
            // Skip per-exe margin when target is maximized: bounds already match the work rect.
            let targetMaximized = wp.showCmd = ShowWindowCommands.SW_SHOWMAXIMIZED
            let adjustedBounds =
                if targetMaximized then bounds
                else this.applyExeMarginForWrite(hwnd, bounds)
            System.Diagnostics.Debug.WriteLine(sprintf "[ExeMargin] adjustWindowPlacement: %s bounds=(%d,%d,%d,%d) adjusted=(%d,%d,%d,%d) showCmd=%A windowShowCmd=%A"
                window.pid.exeName bounds.x bounds.y bounds.width bounds.height
                adjustedBounds.x adjustedBounds.y adjustedBounds.width adjustedBounds.height
                wp.showCmd window.placement.showCmd)
            //if you remove this check, then when you drag a window into an Aero Snapp'ed window
            //the dragged in window will be placed at the restore location for the target, instead of
            //at its snapped location - this is because GetWindowPlacement rcNormal is the restore
            //location for snapped windows
            if  wp.showCmd = ShowWindowCommands.SW_SHOWNORMAL &&
                window.placement.showCmd = ShowWindowCommands.SW_SHOWNORMAL
                then
                this.applyWindowBoundsWithDpiHandling(hwnd, adjustedBounds)
            else
                // Apply DPI-aware handling when target is maximized (regardless of source state)
                if targetMaximized then
                    //maximized windows won't move from one monitor to another by setting placement alone,
                    //need to first move to the new bounds, then set placement
                    this.applyWindowBoundsWithDpiHandling(hwnd, adjustedBounds)
                if window.isMinimized && not targetMaximized then
                    // Un-minimizing a group sibling (the "restore follows the
                    // group" path). setPlacement blocks on the target app's
                    // thread — with several busy apps this summed to ~600-900ms
                    // of frozen strip. Instead un-minimize and reposition
                    // ASYNCHRONOUSLY (ShowWindowAsync + SWP_ASYNCWINDOWPOS),
                    // reaching the same final bounds without stalling the strip.
                    // Transitions are disabled first (re-enabled shortly after)
                    // so only the window the user restored animates.
                    this.disableTransitions(hwnd)
                    window.showWindowAsync(ShowWindowCommands.SW_SHOWNOACTIVATE)
                    window.moveAsync(adjustedBounds)
                    // Post the sibling directly behind the restore-front window
                    // so it surfaces already below it instead of flickering on
                    // top. Ordered after the show on the target's queue.
                    if restoreFrontHwnd <> IntPtr.Zero && restoreFrontHwnd <> hwnd then
                        window.insertAfterAsync(restoreFrontHwnd)
                    ThreadHelper.cancelablePostBack 500 (fun() -> this.enableTransitions(hwnd)) |> ignore
                elif window.isMinimized then
                    this.withoutTransitions(hwnd, fun() -> window.setPlacement(wp))
                else
                    window.setPlacement(wp)

            // Track the margin-shrunk size for this window
            if this.hasExeMargin(hwnd) && not targetMaximized then
                marginShrunkSizes.set(marginShrunkSizes.value.Add(hwnd, (adjustedBounds.width, adjustedBounds.height)))

            // Note: Cases not covered above (e.g., maximized -> normal) do not require DPI handling
            // because setPlacement correctly handles the transition without DPI-related issues.
            // This has been verified through testing across different DPI displays.

    member this.setTabName(hwnd,name) =
        Services.program.setWindowNameOverride(hwnd, name)
        this.setTabInfo(hwnd)

    member this.isMaximized = isMaximizedExport :> ICellOutput<bool>

    member this.isFullscreen = isFullscreenExport :> ICellOutput<bool>

    member this.isMouseOver = this.ts.isMouseOver

    member this.isDragging = isDraggingExport :> ICellOutput<bool>

    member this.flashTab(tab, flash) =
        flashEvent.Trigger(tab, flash)
        // Raw: a colour is DPI-independent, and reading the raw appearance
        // avoids issuing a monitor DPI query for it.
        this.ts.setTabBgColor(tab, if flash then Some(this.tabAppearanceRaw.tabFlashTabColor) else None)
        
    member this.shellEvents(hwnd, evt) = this.invokeAsync <| fun() ->
        Cell.beginUpdate()
        match evt with
        | ShellEvent.HSHELL_FLASH ->
            if this.windows.contains(hwnd) then
                let suppressed = DateTime.Now < suppressFlashUntil
                if suppressed then
                    // The flash was caused by the group's own minimize/restore
                    // (activation denied for a background window). The OS keeps
                    // re-flashing until the window is activated, so cancel the
                    // flash state at the source — this also stops the taskbar
                    // button blinking.
                    Win32Helper.FlashWindow(hwnd, FlashWindowExFlags.FLASHW_STOP, 0)
                //don't flash if its only a single window in the group
                elif this.windows.count > 1 then
                    this.flashTab(Tab(hwnd), true)
        | ShellEvent.HSHELL_REDRAW ->
            if this.windows.contains(hwnd) then
                this.flashTab(Tab(hwnd), false)
        | ShellEvent.HSHELL_WINDOWACTIVATED 
        | ShellEvent.HSHELL_RUDEAPPACTIVATED ->
            if this.windows.contains(hwnd) then
                this.saveZorder()
        | _ -> ()
        Cell.endUpdate()
        

    member this.onEnterMoveSize() =
        inMoveSize.set(true)
        inMoveSizeSnapshot <- true
        this.hideChildWindows()
        this.saveTopWindowPlacement()
        this.updateIsVisible()

    member this.onExitMoveSize() =
        inMoveSize.set(false)
        inMoveSizeSnapshot <- false
        this.saveTopWindowPlacement()
        this.adjustChildWindows()
        this.makeTopWindowForeground()
        this.updateIsVisible()

    // Thread-safe version for cross-thread reads (reads from volatile snapshot)
    member this.isInMoveSizeThreadSafe = inMoveSizeSnapshot

    member this.main(hwnd, evt) = this.invokeAsync <| fun() -> this.withUpdate <| fun() ->
        match evt with
        | WinEvent.EVENT_SYSTEM_MINIMIZESTART ->
            if this.windows.contains(hwnd) then
                if this.consumeMinMaxEcho(hwnd, evt) then () else
                let needsMinimized = zorderCell.value.any <| fun hwnd ->
                    this.os.windowFromHwnd(hwnd).isMinimized.not
                suppressFlashUntil <- DateTime.Now.AddSeconds(3.0)
                if needsMinimized then
                    this.minimizeAll()
                    this.os.setZorder(zorderCell.value.moveToEnd((=)hwnd))
                this.updateIsVisible()
        //this happens when a window is restored from minimize
        | WinEvent.EVENT_SYSTEM_MINIMIZEEND ->
            if this.windows.contains(hwnd) then
                if this.consumeMinMaxEcho(hwnd, evt) then () else
                let needsRestore = zorderCell.value.any <| fun hwnd ->
                    this.os.windowFromHwnd(hwnd).isMinimized
                suppressFlashUntil <- DateTime.Now.AddSeconds(3.0)
                if needsRestore then
                    this.restoreAll()
                    // Put the window the user restored in FRONT of the group:
                    // setZorder places the list head topmost, and the siblings
                    // were restored without activation, so they must stay
                    // behind it. (moveToEnd pushed the restored window to the
                    // BOTTOM of the group instead.)
                    this.bringRestoredToFront(hwnd)
                this.updateIsVisible()
                //foreground status may have changed
                this.foreground <- this.os.foreground.hwnd
                // A sibling just surfaced from its async un-minimize — push the
                // user-restored window back to the front so it never flickers
                // behind (the surfacing cascade IS this event stream).
                this.reassertRestoreFront()
        | WinEvent.EVENT_OBJECT_REORDER ->
            this.saveZorder()
            // Update TOPMOST status for UWP apps when Z-order changes
            if (!_ts).IsSome then
                let ts = (!_ts).Value
                let tsWindow = this.os.windowFromHwnd(ts.hwnd)
                let hasActiveUWP = this.windows.items.any(fun hwnd ->
                    let window = this.os.windowFromHwnd(hwnd)
                    window.className = "ApplicationFrameWindow" && hwnd = this.os.foreground.hwnd
                )
                if hasActiveUWP then
                    tsWindow.makeTopMost()
        | WinEvent.EVENT_OBJECT_NAMECHANGE ->
            if  this.windows.contains(hwnd) &&
                //some windows (e.g. chrome on GoogleAnalitics page) fire namechange constantly as they are resized
                inMoveSize.value.not 
                then
                this.setTabInfo hwnd
        | WinEvent.EVENT_SYSTEM_MOVESIZESTART ->
            if this.isTop(hwnd) then
                this.onEnterMoveSize()

        | WinEvent.EVENT_SYSTEM_MOVESIZEEND ->
            if this.isTop(hwnd) then 
                this.onExitMoveSize()

        //this is here to detect transitions between maximized and
        //restored (both directions). MOVESIZE does not get triggered in this case
        //however, we need to be careful because some apps (Skype.exe) will trigger this
        //event when they loose focus, we don't want to automatically give them focus in this case
        //so make sure that the window HAD focus before reapplying it
        | WinEvent.EVENT_OBJECT_LOCATIONCHANGE ->
            if this.isTop(hwnd) then
                if inMoveSize.value then
                    // During move/size, update tab position to follow the window
                    this.saveTopWindowPlacement()
                else
                    //you can miss EVENT_SYSTEM_MOVESIZESTART events
                    //when a window is created and is immediatly in move size, we subscribe
                    //to the event too late (Chrome tab dragging is prime example)
                    //could be solved by subscribing only once for MOVESIZESTART gobally for all hwnds
                    //but instead, to keep it simple, we just check on all location changes if its in move size
                    let window = this.os.windowFromHwnd(hwnd)
                    if window.isInMoveSize then
                        this.onEnterMoveSize()
                    else
                        let isForeground = this.os.foreground.hwnd = hwnd
                        this.saveTopWindowPlacement()
                        this.adjustChildWindows()
                        if isForeground then
                            this.makeTopWindowForeground()
                        this.foreground <- this.os.foreground.hwnd
                        this.reassertRestoreFront()
                        isMaximizedExport.update()
                        isFullscreenExport.update()
                        // Update tab visibility for fullscreen change
                        let hideForFullscreen =
                            try
                                let hideTabsOnFullscreen = Services.settings.getValue("hideTabsOnFullscreen") :?> bool
                                hideTabsOnFullscreen && isFullscreenExport.value
                            with _ -> false
                        this.ts.visible <- isVisibleCell.value && not hideForFullscreen
        | WinEvent.EVENT_SYSTEM_FOREGROUND ->
            this.foreground <- hwnd
            this.saveZorder()
            // Update visibility for all groups when foreground changes
            // This is critical for detecting virtual desktop switches where windows become cloaked
            this.updateIsVisible()
            // Handle UWP application tab visibility
            if (!_ts).IsSome then
                let ts = (!_ts).Value
                let tsWindow = this.os.windowFromHwnd(ts.hwnd)

                // Check if the foreground window belongs to this group
                if this.windows.contains(hwnd) then
                    let window = this.os.windowFromHwnd(hwnd)
                    // Make topmost for UWP apps
                    if window.className = "ApplicationFrameWindow" then
                        tsWindow.makeTopMost()
                    else
                        tsWindow.makeNotTopMost()
                else
                    // Window outside the group is now foreground
                    // Check if group has UWP windows that need TOPMOST removal
                    let hasUWPWindow = this.windows.items.any(fun hwnd ->
                        let window = this.os.windowFromHwnd(hwnd)
                        window.className = "ApplicationFrameWindow"
                    )
                    if hasUWPWindow && tsWindow.isTopMost then
                        tsWindow.makeNotTopMost()
                        // Insert after the new foreground window to go behind it
                        let foregroundWindow = this.os.windowFromHwnd(hwnd)
                        tsWindow.insertAfter(foregroundWindow)
            // Update fullscreen state and visibility when foreground changes
            if this.windows.contains(hwnd) then
                isFullscreenExport.update()
                let hideForFullscreen =
                    try
                        let hideTabsOnFullscreen = Services.settings.getValue("hideTabsOnFullscreen") :?> bool
                        hideTabsOnFullscreen && isFullscreenExport.value
                    with _ -> false
                this.ts.visible <- isVisibleCell.value && not hideForFullscreen
        | _ -> ()
      
    member this.addWindow(hwnd, withDelay) = this.withUpdate <| fun() ->
       if this.windows.contains(hwnd).not then
            if withDelay then System.Threading.Thread.Sleep(250)
            let window = this.os.windowFromHwnd(hwnd)                
            // Per-event leading throttle intervals. LOCATIONCHANGE fires very frequently for
            // some apps (e.g. LibreOffice) even when the window has not actually moved, so throttle
            // it to at most one handler call every 50ms. MINIMIZE events need to be coalesced over
            // a full second because they can fire in rapid pairs as windows show/hide.
            let conflateIntervals =
                Map.ofList [
                    WinEvent.EVENT_OBJECT_LOCATIONCHANGE, TimeSpan.FromMilliseconds(50.0)
                    WinEvent.EVENT_SYSTEM_MINIMIZESTART,  TimeSpan.FromSeconds(1.0)
                    WinEvent.EVENT_SYSTEM_MINIMIZEEND,    TimeSpan.FromSeconds(1.0)
                ]
            let window = this.os.windowFromHwnd(hwnd)
            this.setWindows(this.windows.add hwnd)
            if prevTop.value.IsNone then
                prevTop.set(Some(hwnd))
                this.saveTopWindowPlacement()
            let registerEvent evt =
                let handler = fun() -> this.main(hwnd, evt)
                let handler =
                    match Map.tryFind evt conflateIntervals with
                    | Some(interval) ->
                        // LOCATIONCHANGE uses leading+trailing so the tab strip
                        // settles at the final position after a drag stops instead
                        // of being left ~50 ms behind. Other events (MINIMIZE pair
                        // coalescing) don't need the trailing edge.
                        match evt with
                        | WinEvent.EVENT_OBJECT_LOCATIONCHANGE ->
                            Helper.conflateWithTrailing interval handler
                        | _ -> Helper.conflate interval handler
                    | None -> handler
                window.setWinEventHook evt handler
            let hooks = 
                List2([
                    WinEvent.EVENT_OBJECT_NAMECHANGE
                    WinEvent.EVENT_OBJECT_LOCATIONCHANGE
                    WinEvent.EVENT_SYSTEM_MOVESIZESTART
                    WinEvent.EVENT_SYSTEM_MOVESIZEEND
                    WinEvent.EVENT_SYSTEM_MINIMIZESTART
                    WinEvent.EVENT_SYSTEM_MINIMIZEEND
                ]).map(registerEvent)
            let dispose = 
                {
                    new IDisposable with
                        member this.Dispose() = hooks.iter(fun h -> h.Dispose())
                }
            hookCleanup.map(fun hooks -> hooks.add hwnd dispose)
            this.setTabInfo hwnd

            this.ts.addTab(Tab(hwnd))
            // Restore fill color from global (persists across group transfers)
            match Services.program.getWindowFillColor(hwnd) with
            | Some(c) -> this.ts.setTabFillColor(Tab(hwnd), Some(c))
            | None -> ()
            // Restore underline color from global (persists across group transfers)
            match Services.program.getWindowUnderlineColor(hwnd) with
            | Some(c) -> this.ts.setTabUnderlineColor(Tab(hwnd), Some(c))
            | None -> ()
            // Restore border color from global (persists across group transfers)
            match Services.program.getWindowBorderColor(hwnd) with
            | Some(c) -> this.ts.setTabBorderColor(Tab(hwnd), Some(c))
            | None -> ()
            // Restore pinned state from global (persists across group transfers)
            if Services.program.isWindowPinned(hwnd) then
                this.ts.pinTab(Tab(hwnd))
            // Restore per-tab alignment from global (persists across group transfers)
            match Services.program.getWindowAlignment(hwnd) with
            | Some(a) -> this.ts.setTabAlign(Tab(hwnd), a)
            | None -> ()
            this.adjustWindowPlacement(hwnd)
            addedEvent.Trigger(hwnd)

    member this.removeWindow(hwnd) = this.withUpdate <| fun() ->
        if this.windows.contains(hwnd) then    
            //CASE 777 - chrome windows can close when you merge a single chrome tab
            //into another chrome group, need to exit the move/size and restore windows on screen in this case
            if inMoveSize.value then
                this.onExitMoveSize()
            
            // Check if this is the active window before removing
            let wasActiveWindow = (this.topWindow = hwnd)
            let allTabs = this.ts.visualOrder
            let closingTab = Tab(hwnd)
            let closingIndex = allTabs.tryFindIndex((=) closingTab)

            // Skip active tab switching during shutdown, restart, disable, or when window is cloaked
            // (cloaked = window moved to another virtual desktop, not actually closed)
            // to avoid excessive window switching during bulk close operations or virtual desktop switches
            let window = this.os.windowFromHwnd(hwnd)
            let skipActivation = Services.program.isShuttingDown || Services.program.isDisabled || window.isCloaked

            // Determine which tab to activate if this was the active window
            let tabToActivate =
                if wasActiveWindow && allTabs.count > 1 && not skipActivation then
                    closingIndex.bind <| fun index ->
                        // Get the next tab (or previous if it's the last tab)
                        if index < allTabs.count - 1 then
                            Some(allTabs.at(index + 1))  // Next tab
                        elif index > 0 then
                            Some(allTabs.at(index - 1))  // Previous tab
                        else
                            None
                else
                    None

            // Activate the next tab before removing the window
            tabToActivate.iter <| fun tab ->
                this.tabActivate(tab, true)

            this.ts.removeTab(Tab(hwnd))
            this.setWindows(this.windows.remove hwnd)
            // Drop the closed hwnd from the multi-select set so a stale
            // entry can never linger past the tab's life.
            if selectedTabsCell.value.contains(hwnd) then
                this.applySelected(selectedTabsCell.value.remove(hwnd))
            hookCleanup.value.find(hwnd).Dispose()
            hookCleanup.map(fun hooks -> hooks.remove(hwnd))
            removedEvent.Trigger(hwnd)
    
    member this.activateIndex(index, force) =
        let nextTab = this.ts.visualOrder.tryAt(index)
        nextTab.iter <| fun(nextTab) ->
            this.tabActivate(nextTab, force)

    member this.switchWindow(next,force) =
        if this.windowCount > 1 then
            let order = this.ts.visualOrder
            let max = order.count - 1
            let top = zorderCell.value.tryHead
            top.iter <| fun top ->
                (order.tryFindIndex((=)(Tab(top)))).iter <| fun index ->
                    let targetIndex = if next then index + 1 else index - 1
                    let targetIndex = 
                        if targetIndex > max then 0
                        elif targetIndex < 0 then max
                        else targetIndex
                    this.activateIndex(targetIndex, force)
                        
    member this.destroy() =
        if isDestroyed.value.not then
            isDestroyed.set(true)
            this.ts.destroy()
            shellHookWindow.value.iter <| fun d -> d.Dispose()
            winEventHandler.value.iter <| fun d -> d.Dispose()
            exitedEvent.Trigger()
            (invoker :> IDisposable).Dispose()

   

    // Run a window state change without the DWM transition animation. Only
    // the window the user actually operated should animate — the rest of the
    // group follows silently. DWMWA_TRANSITIONS_FORCEDISABLED is per-window
    // and is restored right after the state change. (The old approach toggled
    // the SYSTEM-wide SPI_SETANIMATION setting around the loop, which was
    // unreliable and mutated the user's setting.)
    member private this.disableTransitions(hwnd: IntPtr) =
        let mutable disabled = 1
        DwmApi.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_TRANSITIONS_FORCEDISABLED, &disabled, sizeof<int>) |> ignore

    member private this.enableTransitions(hwnd: IntPtr) =
        let mutable enabled = 0
        DwmApi.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_TRANSITIONS_FORCEDISABLED, &enabled, sizeof<int>) |> ignore

    member private this.withoutTransitions(hwnd: IntPtr, f: unit -> unit) =
        this.disableTransitions(hwnd)
        f()
        this.enableTransitions(hwnd)

    member private this.showWindowNoAnimation(hwnd, cmd) =
        this.withoutTransitions(hwnd, fun() -> this.os.windowFromHwnd(hwnd).showWindow(cmd))

    // Consume an expected echo of our own batch operation. Returns true if
    // the event was caused by minimizeAll/restoreAll and must be ignored.
    member private this.consumeMinMaxEcho(hwnd, evt) =
        let stale =
            pendingMinMaxEchoes
            |> Seq.filter (fun kv -> (DateTime.Now - kv.Value).TotalSeconds > 5.0)
            |> Seq.map (fun kv -> kv.Key)
            |> List.ofSeq
        stale |> List.iter (fun k -> pendingMinMaxEchoes.Remove(k) |> ignore)
        pendingMinMaxEchoes.Remove((hwnd, evt))

    member this.minimizeAll() =
        suppressFlashUntil <- DateTime.Now.AddSeconds(3.0)
        zorderCell.value.reverse.iter <| fun hwnd ->
            let window = this.os.windowFromHwnd(hwnd)
            if window.isMinimized.not then
                pendingMinMaxEchoes.[(hwnd, WinEvent.EVENT_SYSTEM_MINIMIZESTART)] <- DateTime.Now
                this.showWindowNoAnimation(hwnd, ShowWindowCommands.SW_SHOWMINNOACTIVE)

    member this.restoreAll() =
        suppressFlashUntil <- DateTime.Now.AddSeconds(3.0)
        zorderCell.value.iter <| fun hwnd ->
            let window = this.os.windowFromHwnd(hwnd)
            if window.isMinimized then
                pendingMinMaxEchoes.[(hwnd, WinEvent.EVENT_SYSTEM_MINIMIZEEND)] <- DateTime.Now
                this.showWindowNoAnimation(hwnd, ShowWindowCommands.SW_SHOWNOACTIVATE)
        
    member this.tabActivate(Tab(hwnd), force) =
        let window = this.os.windowFromHwnd(hwnd)
        let tsWindow = this.os.windowFromHwnd(this.ts.hwnd)

        // If the tab being activated was previously in the selection set,
        // remove it — the active tab is always the implicit primary action
        // target and is never simultaneously "selected".
        if selectedTabsCell.value.contains(hwnd) then
            this.applySelected(selectedTabsCell.value.remove(hwnd))

        // Check if we need to prevent flashing when tabs are inside
        let isTabInside = this.ts.showInside
        let isUWP = window.className = "ApplicationFrameWindow"

        // Temporarily set TOPMOST for non-UWP windows when tabs are inside to prevent flashing
        if isTabInside && not isUWP then
            tsWindow.makeTopMost()

        window.setForegroundOrRestore(force)
        window.bringToTop()
        // Update WindowGroup's internal zorder state immediately
        this.bringToTop(hwnd)

        // Remove TOPMOST after the window switch for non-UWP windows
        if isTabInside && not isUWP then
            // Use a small delay to ensure the window switch is complete
            (ThreadHelper.cancelablePostBack 50 <| fun() ->
                this.invokeAsync <| fun() ->
                    if not (this.windows.items.any(fun hwnd ->
                        let w = this.os.windowFromHwnd(hwnd)
                        w.className = "ApplicationFrameWindow"
                    )) then
                        tsWindow.makeNotTopMost()
            ).Dispose()

    member this.onTabMoved(hwnd, index) =
        // Sync pinned state to global after drag-based auto-pin/unpin
        Services.program.setWindowPinned(hwnd, this.ts.isPinned(Tab(hwnd)))
        movedEvent.Trigger(hwnd, index)

    member x.exited = exitedEvent.Publish
    member this.bounds = boundsExport :> ICellOutput<_>
    member this.isForeground = isForegroundExport :> ICellOutput<_>
    member this.zorder = zorderExport :> ICellOutput<_>
    member this.added = addedEvent.Publish
    member this.moved = movedEvent.Publish
    member this.foregroundChanged = foregroundEvent.Publish
    member this.flash = flashEvent.Publish
    member this.removed = removedEvent.Publish
    member this.visualOrder = this.ts.visualOrder.map(fun(Tab(hwnd)) -> hwnd)

    