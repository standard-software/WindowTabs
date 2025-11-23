namespace Bemo
open System
open System.Diagnostics
open System.Drawing
open System.Drawing.Imaging
open System.IO
open System.Reflection
open System.Threading
open System.Windows.Forms
open Bemo.Win32.Forms

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
    let prevTop = Cell.create(None)
    let placement = Cell.create(None:Option<Rect * OSWindowPlacement>)
    let windowsCell = Cell.create(Set2())
    let _ts = ref None 
    let inMoveSize = Cell.create(false)
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
 
    let boundsExport = Cell.export <| fun() ->
        placement.value.bind <| fun(rect,placement) -> 
            if isVisibleCell.value then Some(rect) else None

    let isForegroundExport = Cell.export <| fun() ->
        zorderCell.value.any((=) foregroundCell.value)

    member this.isSuperBarEnabled = enableSuperBar

    member this.init(ts:TabStrip) =
        _ts := Some(ts)

        // Apply default setting for tab position
        let defaultPosition = 
            match Services.settings.getValue("tabPositionByDefault") :?> string with
            | "left" -> TabLeft
            | "center" -> TabCenter
            | _ -> TabRight
        ts.setAlignment(ts.direction, defaultPosition)
        
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
        isDraggingExport.init()
        zorderExport.init()
        boundsExport.init()
        isForegroundExport.init()

        this.ts.setTabAppearance(this.tabAppearance)
        Services.settings.notifyValue "tabAppearance" <| fun(_) ->
            this.invokeAsync <| fun() ->
                this.ts.setTabAppearance(this.tabAppearance)

        // Listen for tabPositionByDefault changes
        Services.settings.notifyValue "tabPositionByDefault" <| fun value ->
            this.invokeAsync <| fun() ->
                let position = unbox<string>(value)
                let alignment =
                    match position with
                    | "left" -> TabLeft
                    | "center" -> TabCenter
                    | _ -> TabRight
                ts.setAlignment(ts.direction, alignment)

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
            this.ts.visible <- isVisibleCell.value

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

    
    member this.tabAppearance = Services.settings.getValue("tabAppearance").cast<TabAppearanceInfo>()

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
        isVisibleCell.value <-
            this.isEmpty.not &&
            zorderCell.value.where(isMinimized >> not).tryHead.IsSome &&
            inMoveSize.value.not

    member private this.adjustChildWindows = fun() ->
        zorderCell.value.tail.iter(this.adjustWindowPlacement)
        
        // After initial placement, adjust sizes again to ensure DPI is considered
        match zorderCell.value.tryHead with
        | Some(topHwnd) ->
            let topWindow = this.os.windowFromHwnd(topHwnd)
            let topBounds = topWindow.bounds
            
            // Move all background windows again with the correct size
            zorderCell.value.tail.iter(fun hwnd ->
                let window = this.os.windowFromHwnd(hwnd)
                if window.isMinimized.not then
                    let currentBounds = window.bounds
                    // Keep current position but use top window's size
                    let correctBounds = Rect(currentBounds.location, topBounds.size)
                    window.move(correctBounds)
            )
        | None -> ()
        
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
        
    member this.isIconOnly 
        with get() = this.ts.isIconOnly
        and set(value) = this.ts.isIconOnly <- value

    member this.hwnd = this.ts.hwnd

    member private this.os : OS = _os
       
    member private this.windowCount = this.windows.count

    member this.placementBounds : Rect = placement.value.map(fst).def(Rect())

    member private this.isTop(hwnd) = zorderCell.value.where(isMinimized >> not).tryHead = Some(hwnd)

    member private this.saveTopWindowPlacement() =
        let window = this.os.windowFromHwnd(zorderCell.value.head)
        if  window.isMinimized.not &&
            this.os.isOnScreen(window.bounds)
            then
            let bounds = 
                if window.isMaximized then
                    //windows are placed slightly off screen when maximized, get the bounds of the monitor instead
                    match Mon.fromHwnd(window.hwnd) with
                    | Some(mon) -> mon.workRect.move(-1,-1)
                    | None -> window.bounds
                else window.bounds
            placement.set(Some(bounds, window.placement))
           
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

    member private this.adjustWindowPlacement(hwnd) =
        let window = this.os.windowFromHwnd(hwnd)
        if placement.value.IsSome then
            let bounds,wp = placement.value.Value
            //if you remove this check, then when you drag a window into an Aero Snapp'ed window
            //the dragged in window will be placed at the restore location for the target, instead of
            //at its snapped location - this is because GetWindowPlacement rcNormal is the restore
            //location for snapped windows
            if  wp.showCmd = ShowWindowCommands.SW_SHOWNORMAL &&
                window.placement.showCmd = ShowWindowCommands.SW_SHOWNORMAL
                then
                this.applyWindowBoundsWithDpiHandling(hwnd, bounds)
            else
                // Apply DPI-aware handling when target is maximized (regardless of source state)
                if wp.showCmd = ShowWindowCommands.SW_SHOWMAXIMIZED then
                    //maximized windows won't move from one monitor to another by setting placement alone,
                    //need to first move to the new bounds, then set placement
                    this.applyWindowBoundsWithDpiHandling(hwnd, bounds)
                window.setPlacement(wp)

            // Note: Cases not covered above (e.g., maximized -> normal) do not require DPI handling
            // because setPlacement correctly handles the transition without DPI-related issues.
            // This has been verified through testing across different DPI displays.

    member this.setTabName(hwnd,name) =
        Services.program.setWindowNameOverride(hwnd, name)
        this.setTabInfo(hwnd)

    member this.isMaximized = isMaximizedExport :> ICellOutput<bool>

    member this.isMouseOver = this.ts.isMouseOver

    member this.isDragging = isDraggingExport :> ICellOutput<bool>

    member this.flashTab(tab, flash) =
        flashEvent.Trigger(tab, flash)
        this.ts.setTabBgColor(tab, if flash then Some(this.tabAppearance.tabFlashBgColor) else None)
        
    member this.shellEvents(hwnd, evt) = this.invokeAsync <| fun() ->
        Cell.beginUpdate()
        match evt with
        | ShellEvent.HSHELL_FLASH ->
            //don't flash if its only a single window in the group
            if this.windows.contains(hwnd) &&
               this.windows.count > 1 then
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
        this.hideChildWindows()
        this.updateIsVisible()

    member this.onExitMoveSize() =
        inMoveSize.set(false)
        this.saveTopWindowPlacement()
        this.adjustChildWindows()
        this.makeTopWindowForeground()
        this.updateIsVisible()

    member this.main(hwnd, evt) = this.invokeAsync <| fun() -> this.withUpdate <| fun() ->
        match evt with
        | WinEvent.EVENT_SYSTEM_MINIMIZESTART -> 
            if this.windows.contains(hwnd) then
                let needsMinimized = zorderCell.value.any <| fun hwnd -> 
                    this.os.windowFromHwnd(hwnd).isMinimized.not
                if needsMinimized then  
                    this.minimizeAll()
                    this.os.setZorder(zorderCell.value.moveToEnd((=)hwnd))
                this.updateIsVisible()
        //this happens when a window is restored from minimize
        | WinEvent.EVENT_SYSTEM_MINIMIZEEND ->
            if this.windows.contains(hwnd) then
                let needsRestore = zorderCell.value.any <| fun hwnd -> 
                    this.os.windowFromHwnd(hwnd).isMinimized
                if needsRestore then  
                    this.restoreAll()
                    this.os.setZorder(zorderCell.value.moveToEnd((=)hwnd))
                this.updateIsVisible()      
                //foreground status may have changed
                this.foreground <- this.os.foreground.hwnd
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
            if this.isTop(hwnd) && inMoveSize.value.not then
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
                    isMaximizedExport.update()
        | WinEvent.EVENT_SYSTEM_FOREGROUND ->
            this.foreground <- hwnd
            this.saveZorder()
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
        | _ -> ()
      
    member this.addWindow(hwnd, withDelay) = this.withUpdate <| fun() ->
       if this.windows.contains(hwnd).not then
            if withDelay then System.Threading.Thread.Sleep(250)
            let window = this.os.windowFromHwnd(hwnd)                
            let conflateEvents = Set2(List2([WinEvent.EVENT_SYSTEM_MINIMIZESTART; WinEvent.EVENT_SYSTEM_MINIMIZEEND]))
            let window = this.os.windowFromHwnd(hwnd)
            this.setWindows(this.windows.add hwnd)
            if prevTop.value.IsNone then
                prevTop.set(Some(hwnd))
                this.saveTopWindowPlacement()
            let registerEvent evt = 
                let handler = fun() -> this.main(hwnd, evt)
                let handler =
                    if conflateEvents.contains(evt) then Helper.conflate (TimeSpan(0,0,1)) handler
                    else handler
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
            let allTabs = this.ts.lorder
            let closingTab = Tab(hwnd)
            let closingIndex = allTabs.tryFindIndex((=) closingTab)
            
            // Determine which tab to activate if this was the active window
            let tabToActivate = 
                if wasActiveWindow && allTabs.count > 1 then
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
            
            let window = this.os.windowFromHwnd(hwnd)
            this.ts.removeTab(Tab(hwnd))
            this.setWindows(this.windows.remove hwnd)
            hookCleanup.value.find(hwnd).Dispose()
            hookCleanup.map(fun hooks -> hooks.remove(hwnd))
            removedEvent.Trigger(hwnd)
    
    member this.activateIndex(index, force) =
        let nextTab = this.ts.lorder.tryAt(index)
        nextTab.iter <| fun(nextTab) ->
            this.tabActivate(nextTab, force)

    member this.switchWindow(next,force) = 
        if this.windowCount > 1 then
            let lorder = this.ts.lorder
            let max = lorder.count - 1
            let top = zorderCell.value.tryHead
            top.iter <| fun top ->
                (lorder.tryFindIndex((=)(Tab(top)))).iter <| fun index ->
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

   

    member private this.suppressAnimation f = fun() ->
        let hasAnimation = Win32Helper.GetMinMaxAnimation()
        if hasAnimation then
            Win32Helper.SetMinMaxAnimation(false)
        f()
        if hasAnimation then
            Win32Helper.SetMinMaxAnimation(true)

    member this.minimizeAll = this.suppressAnimation <| fun() ->
        zorderCell.value.reverse.iter <| fun hwnd ->
            let window = this.os.windowFromHwnd(hwnd)
            if window.isMinimized.not then
                window.showWindow(ShowWindowCommands.SW_SHOWMINNOACTIVE)
        
    member this.restoreAll = this.suppressAnimation <| fun() ->
        zorderCell.value.iter <| fun hwnd ->
            let window = this.os.windowFromHwnd(hwnd)
            if window.isMinimized then
                window.showWindow(ShowWindowCommands.SW_SHOWNOACTIVATE)
        
    member this.tabActivate(Tab(hwnd), force) = 
        let window = this.os.windowFromHwnd(hwnd)
        let tsWindow = this.os.windowFromHwnd(this.ts.hwnd)
        
        // Check if we need to prevent flashing when tabs are inside
        let isTabInside = this.ts.showInside
        let isUWP = window.className = "ApplicationFrameWindow"
        
        // Temporarily set TOPMOST for non-UWP windows when tabs are inside to prevent flashing
        if isTabInside && not isUWP then
            tsWindow.makeTopMost()
        
        window.setForegroundOrRestore(force)
        window.bringToTop()
        
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

    member this.onTabMoved(hwnd, index) = movedEvent.Trigger(hwnd, index)

    member x.exited = exitedEvent.Publish
    member this.bounds = boundsExport :> ICellOutput<_>
    member this.isForeground = isForegroundExport :> ICellOutput<_>
    member this.zorder = zorderExport :> ICellOutput<_>
    member this.added = addedEvent.Publish
    member this.moved = movedEvent.Publish
    member this.foregroundChanged = foregroundEvent.Publish
    member this.flash = flashEvent.Publish
    member this.removed = removedEvent.Publish
    member this.lorder = this.ts.lorder.map(fun(Tab(hwnd)) -> hwnd)

    