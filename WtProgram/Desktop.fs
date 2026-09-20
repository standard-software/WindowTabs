namespace Bemo
open System
open System.Drawing
open System.Threading
open System.Windows.Forms


type GroupInfo(enableSuperBar) as this =
    let Cell = CellScope(true, true)
    let windowsCell = Cell.create(List2())
    let mutable _isExited = false
    let desktopInvoker = InvokerService.invoker
    let (_group, invoker) = ThreadHelper.startOnThreadAndWait <| fun() ->
        let plugins = List2<_>([
            None // MouseScrollPlugin disabled - mouse wheel tab switching removed
            // Ctrl+1 .. Ctrl+9 used to be a low-level keyboard hook plugin
            // here (NumericTabHotKeyPlugin). The number keys are
            // RegisterHotKey hot keys now, owned by Program together with
            // Next / Previous Tab: see HotKeyPolicy and Program.syncHotKeys.
            Some(HideTabsOnInactiveGroupPlugin().cast<IPlugin>())
            (if enableSuperBar then Some(SuperBarPlugin().cast<IPlugin>()) else None)
            ])
        let plugins = plugins.choose(id)

        let _group = WindowGroup(enableSuperBar, plugins)
        _group.exited.Add <| fun _ ->
            _isExited <- true 
            Application.ExitThread()
        (_group, InvokerService.invoker)

    do
        // asyncInvoke, not invoke: these fire on the group thread, and a
        // synchronous invoke into the (possibly busy) main thread here is one
        // half of the display-change deadlock — see NonBlockingSyncContext.
        // Ordering is preserved by the main thread's message queue.
        _group.added.Add <| fun hwnd ->
            desktopInvoker.asyncInvoke <| fun() ->
                windowsCell.map <| fun l -> l.where((<>) hwnd).append hwnd

        _group.moved.Add <| fun(hwnd, index) ->
            desktopInvoker.asyncInvoke <| fun() ->
                windowsCell.map <| fun l -> l.move((=) hwnd, index)

        _group.removed.Add <| fun hwnd ->
            desktopInvoker.asyncInvoke <| fun() ->
                windowsCell.map <| fun l -> l.where((<>) hwnd)

    member this.invokeGroup = invoker.asyncInvoke
    member this.isExited = _isExited
    member this.exited = _group.exited
    member this.removed = _group.removed
    member this.group = _group
    member this.hwnd = this.group.hwnd
    member private this.windows = windowsCell.value
    member private this.addWindow(hwnd, withDelay) =  
        //add it to collection up front, can't wait for async notification of add through added event
        windowsCell.map <| fun l -> l.append hwnd
        this.invokeGroup <| fun() -> this.group.addWindow(hwnd, withDelay)
    member private this.removeWindow hwnd =
        this.invokeGroup <| fun() -> this.group.removeWindow(hwnd)
    member private this.destroy() = this.invokeGroup <| fun() -> this.group.destroy()
    member private x.switchWindow(next, force) = this.invokeGroup <| fun() -> this.group.switchWindow(next, force)
    // force = true, as the old Ctrl+number plugin did: the user named the tab
    // and expects it in front even when its window is the one already active.
    member private x.activateIndex(index) = this.invokeGroup <| fun() -> this.group.activateIndex(index, true)

    interface IGroup with
        member x.hwnd = this.hwnd
        member x.windows
            with get() = this.windows
        member x.visualOrder = this.windows  // Use windowsCell which maintains order via added/moved events
        member x.visualOrderThreadSafe = _group.visualOrderHwndsThreadSafe
        member x.destroy() = this.destroy()
        member x.addWindow(hwnd, delay) = this.addWindow(hwnd, delay)
        member x.removeWindow hwnd = this.removeWindow hwnd
        member x.switchWindow(next,force) = this.switchWindow(next, force)
        member x.activateIndex(index) = this.activateIndex(index)
        member x.perGroupTabPositionValue
            with get() : string = _group.perGroupTabPositionValue
            and set(value:string) = this.invokeGroup <| fun() -> _group.perGroupTabPositionValue <- value
        member x.snapTabHeightMargin
            with get() = _group.snapTabHeightMargin
            and set(value) = this.invokeGroup <| fun() -> _group.snapTabHeightMargin <- value
        member x.lockWindowPosition
            with get() = _group.lockWindowPosition
            and set(value) = this.invokeGroup <| fun() -> _group.lockWindowPosition <- value
        member x.isPinned(hwnd) = _group.isPinnedThreadSafe(hwnd)
        member x.pinTab(hwnd) = this.invokeGroup <| fun() -> _group.pinTab(hwnd)
        member x.isPinnedThreadSafe(hwnd) = _group.isPinnedThreadSafe(hwnd)
        member x.isInMoveSizeThreadSafe = _group.isInMoveSizeThreadSafe
        member x.reseatWindow(hwnd) = this.invokeGroup <| fun() -> _group.reseatWindow(hwnd)
        member x.setTabFillColor(hwnd, color) = this.invokeGroup <| fun() -> _group.setTabFillColor(hwnd, color)
        member x.getTabFillColorThreadSafe(hwnd) = _group.getTabFillColorThreadSafe(hwnd)
        member x.setTabUnderlineColor(hwnd, color) = this.invokeGroup <| fun() -> _group.setTabUnderlineColor(hwnd, color)
        member x.getTabUnderlineColorThreadSafe(hwnd) = _group.getTabUnderlineColorThreadSafe(hwnd)
        member x.setTabBorderColor(hwnd, color) = this.invokeGroup <| fun() -> _group.setTabBorderColor(hwnd, color)
        member x.getTabBorderColorThreadSafe(hwnd) = _group.getTabBorderColorThreadSafe(hwnd)
        member x.setTabName(hwnd, name) = this.invokeGroup <| fun() -> _group.setTabName(hwnd, name)
        member x.setTabAlign(hwnd, align) = this.invokeGroup <| fun() -> _group.setTabAlign(hwnd, align)
        member x.moveTab(hwnd, index) = this.invokeGroup <| fun() -> _group.ts.moveTab(Tab(hwnd), index)

type IDesktopNotification =
    abstract member dragDrop : IntPtr * bool option -> unit
    abstract member dragEnd : unit -> unit

type Desktop(notify:IDesktopNotification) as this =
    let os = OS()
    let Cell = CellScope()
    let groupCell = Cell.create(Set2<GroupInfo>())
    let isDraggingCell = Cell.create(false)
    let invoker = InvokerService.invoker
    let mutable pendingSnapDrops = 0
    let exitedEvent = Event<_>()
    let removedEvent = Event<_>()
    let _dd = DragDropController(this :> IDragDropParent) :> IDragDrop
    
    do 
        Services.register(_dd, false)
        Services.register(this.cast<IDesktop>())

    member private this.groups : List2<GroupInfo> = groupCell.value.items
    member private this.isEmpty = this.groups.all(fun g -> g.isExited)
    member private this.isDragging = isDraggingCell.value || pendingSnapDrops > 0
    member private this.createGroup(enableSuperBar) =
        let group = GroupInfo(enableSuperBar)
        groupCell.map(fun g -> g.add(group))
        group.invokeGroup <| fun() -> 
            let ig = group.cast<IGroup>()
            group.exited.Add <| fun _ -> exitedEvent.Trigger ig
            group.removed.Add <| fun _ -> removedEvent.Trigger ig
            TabStripDecorator(group.group, fun hwnd ->
                invoker.asyncInvoke <| fun() ->
                    notify.dragDrop(hwnd, None)
                    notify.dragEnd()
            ).ignore
        group.cast<IGroup>() 

    // (windowOffset removed: it had no callers and read the RAW, unscaled
    // appearance, so reviving it on a scaled monitor would have placed windows
    // a third of a strip height off.)
    member this.findGroupContainingHwnd hwnd : IGroup option =
        this.cast<IDesktop>().groups.tryFind(fun g -> g.windows.contains((=)hwnd))

    member this.restartGroup(groupHwnd, enableSuperBar) =
        let group = this.groups.tryFind(fun g -> g.hwnd = groupHwnd)
        group.iter <| fun g ->
            let group = g.cast<IGroup>()
            
            Services.program.suspendTabMonitoring()
            
            let newGroup = Services.desktop.createGroup(enableSuperBar)
            group.windows.iter <| fun hwnd -> 
                group.removeWindow(hwnd)
                newGroup.addWindow(hwnd, false)

            Services.program.resumeTabMonitoring()

    // Variant B uses the menu's 50-percent geometry. Group creation continues
    // only after the position-first DPI transition, without blocking the UI.
    member private this.snapDroppedWindow(window: Window, mon: Mon, pt: Pt, dragInfo: TabDragInfo, complete: unit -> unit) =
        let hwnd = window.hwnd
        let work = mon.workRect
        let workArea = (work.x, work.y, work.width, work.height)
        let snapDirection = SnapGeometry.directionForPoint workArea (pt.x, pt.y)
        let snapArea() =
            let tabHeight =
                if dragInfo.sourceSnapTabHeightMargin then
                    let scale = Dpi.scaleForMonitorHandle mon.hMonitor
                    (Dpi.scaleAppearance scale Services.program.tabAppearanceInfo).tabHeight - 1
                else 0
            SnapGeometry.reserveTabHeight tabHeight workArea

        let finish() =
            try
                if window.isWindow then
                    // Final dimensions are computed after DPI settles. Unlike A,
                    // B's 50 percent does not depend on the restored window size.
                    let (x, y, width, height) =
                        SnapGeometry.calculateSnapBoundsWithPercent snapDirection 50 (snapArea())
                    window.move (Rect(Pt(x, y), Sz(width, height)))
                    let realignEnabled =
                        try (Services.settings.getValue("changeTabPositionOnSnap") :?> string) = "change"
                        with _ -> false
                    match SnapGeometry.realignment realignEnabled snapDirection dragInfo.sourceTabAligns TopLeft TopRight with
                    | Some(alignment) ->
                        for h in hwnd :: dragInfo.selectedHwnds do
                            Services.program.setWindowAlignment(h, Some(alignment))
                    | None -> ()
                    DragTrace.log (fun () ->
                        sprintf "Desktop.snapDroppedWindow: hwnd=%X pt=%A direction=%s bounds=%A restoreSize=%A"
                            (hwnd.ToInt64()) pt snapDirection (x, y, width, height) dragInfo.sourceRestoreSize)
                    complete()
            finally
                pendingSnapDrops <- pendingSnapDrops - 1
                // dragEnd may have arrived while the timer was pending. Keep
                // auto-grouping/pruning paused until the placement is complete.
                if not this.isDragging then notify.dragEnd()

        let schedule interval callback =
            let timer = new System.Windows.Forms.Timer(Interval = interval)
            timer.Tick.Add <| fun _ ->
                timer.Stop()
                timer.Dispose()
                callback()
            timer.Start()

        let currentDpi = WinUserApi.GetDpiForWindow(hwnd)
        let targetDpi = uint32 (DpiApi.GetDpiForMonitorHandle(mon.hMonitor))
        let perMonitorAware =
            try
                WinUserApi.GetAwarenessFromDpiAwarenessContext(WinUserApi.GetWindowDpiAwarenessContext(hwnd)) = 2
            with _ -> false
        // The captured restore size is the staging geometry input, never the
        // size of the off-screen (or maximized) rectangle. B's final size is 50%.
        let size = dragInfo.sourceRestoreSize
        let (x, y, _, _) = SnapGeometry.snapBounds snapDirection (snapArea()) size.width size.height
        window.setPositionOnly x y
        pendingSnapDrops <- pendingSnapDrops + 1
        let started = System.Diagnostics.Stopwatch.StartNew()
        SnapDrop.finishAfterDpiChange currentDpi targetDpi perMonitorAware
            (fun() -> window.isWindow)
            (fun() -> WinUserApi.GetDpiForWindow(hwnd))
            (fun() -> started.ElapsedMilliseconds)
            schedule finish



    interface IDesktop with
        member x.isDragging = this.isDragging
        member x.isEmpty = this.isEmpty
        member x.createGroup(enableSuperBar) = this.createGroup(enableSuperBar)
        member x.restartGroup(hwnd, enableSuperBar) = invoker.asyncInvoke <| fun() ->
            this.restartGroup(hwnd, enableSuperBar)
        member x.groups = this.groups.where(fun(g) -> g.isExited.not).map(fun(g) -> g.cast<IGroup>())
        member x.groupExited = exitedEvent.Publish
        member x.groupRemoved = removedEvent.Publish
        member x.foregroundGroup
            with get() =
                let foregroundWindow = os.foreground
                this.findGroupContainingHwnd(foregroundWindow.hwnd)

    interface IDragDropParent with
        member x.dragBegin() = invoker.asyncInvoke <| fun() ->
            isDraggingCell.set(true)

        member x.dragDrop((pt, data)) = invoker.asyncInvoke <| fun() ->
            let dragInfo = unbox<TabDragInfo>(data)
            let (Tab(hwnd)) = dragInfo.tab
            DragTrace.log (fun () -> sprintf "Desktop.dragDrop: hwnd=%X pt=%A selected=%d" (hwnd.ToInt64()) pt dragInfo.selectedHwnds.Length)
            let window = os.windowFromHwnd(hwnd)
            // "Snap to the screen edge when dragging a tab out": the display is
            // the one containing the drop point (nearest, for a point in a gap
            // between monitors). Off, or no display answered: the placement
            // below, unchanged.
            let snapMonitor =
                let enabled =
                    try Services.settings.getValue("snapOnDragDetach") :?> bool
                    with _ -> false
                if enabled then Mon.nearestFromPoint pt else None

            let completeDrop() =
                // Multi-select drag-detach continuation. Case C:
                //   selected tabs were hideOffScreen'd by dragExit; we tell
                //   Program to spare them from removeUntabableWindows for a
                //   brief grace window (markRecentlyPlaced) so the auto-prune
                //   pass triggered from Program.dragEnd doesn't strip them
                //   from the new group before adjustChildWindows puts them
                //   back on-screen. Then we addWindow + showWindow them and
                //   re-establish active+selected on the new group's thread.
                if not (List.isEmpty dragInfo.selectedHwnds) then
                    Services.program.markRecentlyPlaced(hwnd :: dragInfo.selectedHwnds)
                    Services.program.suspendTabMonitoring()
                    try
                        let newGroup = this.createGroup(false)
                        if snapMonitor.IsSome then newGroup.snapTabHeightMargin <- dragInfo.sourceSnapTabHeightMargin
                        newGroup.lockWindowPosition <- dragInfo.sourceLockWindowPosition
                        newGroup.addWindow(hwnd, false)
                        for selHwnd in dragInfo.selectedHwnds do
                            if not (newGroup.windows.contains((=) selHwnd)) then
                                newGroup.addWindow(selHwnd, false)
                            let selWindow = os.windowFromHwnd(selHwnd)
                            selWindow.showWindow(ShowWindowCommands.SW_SHOW)
                        match newGroup with
                        | :? GroupInfo as gi ->
                            gi.invokeGroup <| fun() ->
                                let wg = gi.group
                                wg.tabActivate(Tab(hwnd), false)
                                for selHwnd in dragInfo.selectedHwnds do
                                    wg.setSelected(selHwnd, true)
                        | _ -> ()
                    finally
                        Services.program.resumeTabMonitoring()
                else
                    // The group this window lands in may not exist yet; the
                    // lock waits under its handle until it does.
                    Services.program.markDetachedLock hwnd dragInfo.sourceLockWindowPosition
                    notify.dragDrop(hwnd, snapMonitor |> Option.map (fun _ -> dragInfo.sourceSnapTabHeightMargin))
            match snapMonitor with
            | Some(mon) ->
                if window.isMinimized || window.isMaximized then
                    window.showWindow(ShowWindowCommands.SW_RESTORE)
                this.snapDroppedWindow(window, mon, pt, dragInfo, completeDrop)
            | None ->
                // Calculate window position from drop point
                // In preview image: click position is at imageOffset, window top-left is at (0, tabHeight - tabHeightOffset - 1)
                // Device pixels for the monitor the tab was dropped on: the drop
                // point and the window rectangles are physical now that the process
                // is DPI aware, so this offset has to be scaled to match or the
                // window lands about a third of a strip height too high at 150%.
                let tabAppearance = Dpi.scaleAppearance (Dpi.scaleForPoint pt) Services.program.tabAppearanceInfo
                let previewWindowOffset = Pt(0, tabAppearance.tabHeight - (tabAppearance.tabHeightOffset + 1))
                let windowPt = pt.sub(dragInfo.imageOffset).add(previewWindowOffset)
                let monitor = Mon.fromPoint windowPt
                let workspaceOffset = monitor.map(fun mon -> mon.workRect.location.sub(mon.displayRect.location)).def(Pt())
                let windowPt = windowPt.sub(workspaceOffset)

                // First restore the window if it's minimized or maximized
                if window.isMinimized || window.isMaximized then
                    window.showWindow(ShowWindowCommands.SW_RESTORE)

                // Get window size for boundary checking
                let windowSize = window.bounds.size

                // Calculate window center point to determine which screen it belongs to.
                // Mon (a live MonitorFromPoint query) instead of Screen.FromPoint:
                // the WinForms screen cache is process-wide and may have been filled
                // by the deliberately DPI-unaware settings dialog, whose rectangles
                // are virtualized rather than device pixels.
                let centerX = windowPt.x + windowSize.width / 2
                let centerY = windowPt.y + windowSize.height / 2
                let workArea =
                    match Mon.nearestFromPoint(Pt(centerX, centerY)) with
                    | Some(mon) -> mon.workRect
                    | None -> Rect(windowPt, windowSize)

                // Limit window size to screen size if it exceeds
                let maxWidth = workArea.width
                let maxHeight = workArea.height
                let finalWidth = min windowSize.width maxWidth
                let finalHeight = min windowSize.height maxHeight

                // Adjust position to keep window within screen boundaries
                let adjustedX = max workArea.left (min windowPt.x (workArea.right - finalWidth))
                let adjustedY = max workArea.top (min windowPt.y (workArea.bottom - finalHeight))

                // Resize window if it exceeds screen size, then move to position
                if windowSize.width > maxWidth || windowSize.height > maxHeight then
                    WinUserApi.SetWindowPos(
                        hwnd,
                        WindowHandleTypes.HWND_TOP,
                        adjustedX,
                        adjustedY,
                        finalWidth,
                        finalHeight,
                        SetWindowPosFlags.SWP_NOACTIVATE ||| SetWindowPosFlags.SWP_NOZORDER) |> ignore
                else
                    // Move window position only
                    window.setPositionOnly adjustedX adjustedY

                completeDrop()

        member x.dragEnd() = invoker.asyncInvoke <| fun() ->
            isDraggingCell.set(false)
            notify.dragEnd()

    
