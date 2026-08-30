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
    let enableCtrlNumberHotKey = Services.settings.getValue("enableCtrlNumberHotKey").cast<bool>()
    let (_group, invoker) = ThreadHelper.startOnThreadAndWait <| fun() ->
        let plugins = List2<_>([
            None // MouseScrollPlugin disabled - mouse wheel tab switching removed
            (if enableCtrlNumberHotKey then Some(NumericTabHotKeyPlugin().cast<IPlugin>()) else None)
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
        member x.perGroupTabPositionValue
            with get() : string = _group.perGroupTabPositionValue
            and set(value:string) = this.invokeGroup <| fun() -> _group.perGroupTabPositionValue <- value
        member x.snapTabHeightMargin
            with get() = _group.snapTabHeightMargin
            and set(value) = this.invokeGroup <| fun() -> _group.snapTabHeightMargin <- value
        member x.isPinned(hwnd) = _group.isPinnedThreadSafe(hwnd)
        member x.pinTab(hwnd) = this.invokeGroup <| fun() -> _group.pinTab(hwnd)
        member x.isPinnedThreadSafe(hwnd) = _group.isPinnedThreadSafe(hwnd)
        member x.isInMoveSizeThreadSafe = _group.isInMoveSizeThreadSafe
        member x.setTabFillColor(hwnd, color) = this.invokeGroup <| fun() -> _group.setTabFillColor(hwnd, color)
        member x.getTabFillColorThreadSafe(hwnd) = _group.getTabFillColorThreadSafe(hwnd)
        member x.setTabUnderlineColor(hwnd, color) = this.invokeGroup <| fun() -> _group.setTabUnderlineColor(hwnd, color)
        member x.getTabUnderlineColorThreadSafe(hwnd) = _group.getTabUnderlineColorThreadSafe(hwnd)
        member x.setTabBorderColor(hwnd, color) = this.invokeGroup <| fun() -> _group.setTabBorderColor(hwnd, color)
        member x.getTabBorderColorThreadSafe(hwnd) = _group.getTabBorderColorThreadSafe(hwnd)

type IDesktopNotification =
    abstract member dragDrop : IntPtr -> unit
    abstract member dragEnd : unit -> unit

type Desktop(notify:IDesktopNotification) as this =
    let os = OS()
    let Cell = CellScope()
    let groupCell = Cell.create(Set2<GroupInfo>())
    let isDraggingCell = Cell.create(false)
    let invoker = InvokerService.invoker
    let exitedEvent = Event<_>()
    let removedEvent = Event<_>()
    let _dd = DragDropController(this :> IDragDropParent) :> IDragDrop
    
    do 
        Services.register(_dd, false)
        Services.register(this.cast<IDesktop>())

    member private this.groups : List2<GroupInfo> = groupCell.value.items
    member private this.isEmpty = this.groups.all(fun g -> g.isExited)
    member private this.isDragging = isDraggingCell.value
    member private this.createGroup(enableSuperBar) =
        let group = GroupInfo(enableSuperBar)
        groupCell.map(fun g -> g.add(group))
        group.invokeGroup <| fun() -> 
            let ig = group.cast<IGroup>()
            group.exited.Add <| fun _ -> exitedEvent.Trigger ig
            group.removed.Add <| fun _ -> removedEvent.Trigger ig
            TabStripDecorator(group.group, fun hwnd ->
                invoker.asyncInvoke <| fun() ->
                    notify.dragDrop(hwnd)
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
                notify.dragDrop(hwnd)

        member x.dragEnd() = invoker.asyncInvoke <| fun() ->
            isDraggingCell.set(false)
            notify.dragEnd()

    
