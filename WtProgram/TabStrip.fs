namespace Bemo
open System
open System.Collections
open System.Drawing
open System.Drawing.Drawing2D
open System.Drawing.Imaging
open System.Reflection
open System.IO
open System.Windows.Forms
open Bemo.Win32.Forms

type ITabStripMonitor =
    abstract member tabClick : (MouseButton * Tab * TabPart * MouseAction * Pt) -> unit
    abstract member tabActivate : (Tab) -> unit
    abstract member tabClose : Tab -> unit
    abstract member tabPin : Tab -> unit
    abstract member tabMoved : Tab * int -> unit
    abstract member windowMsg : Win32Message -> unit

type TabStrip(monitor:ITabStripMonitor) as this =
    // Static set to track pinned state across tab transfers between groups
    static let recentlyPinnedTabs = System.Collections.Generic.HashSet<IntPtr>()
    let Cell = CellScope(false, true)
    let _os = OS()
    let taskbar = _os.getTaskbar()
    let tabMovedEvent = Event<_>()
    let contentBoundsCell = Cell.create(Rect())
    let appearanceCell = Cell.create(None)
    let foregroundCell = Cell.create(None:Tab option)
    let prevForegroundCell = Cell.create(None)
    let sizeCell = Cell.create(Sz.empty)
    let alphaCell = Cell.create(byte(0xFF))
    let locationCell = Cell.create(Pt.empty)
    let lorderCell = Cell.create(List2())
    let zorderCell = Cell.create(List2())
    let visibleCell = Cell.create(false)
    let transparentCell = Cell.create(true)
    let showInsideCell = Cell.create(false)
    let isInAltTabCell = Cell.create(false)
    let pinnedTabsCell = Cell.create(Set2<Tab>())
    // Thread-safe snapshot of pinned tabs for cross-thread reads (e.g., save from main thread)
    [<VolatileField>]
    let mutable pinnedTabsSnapshot = Set2<Tab>()
    let alignment = Cell.create(TabRight)
    let capturedCell = Cell.create(None : Option<Tab*TabPart>)
    let hoverCell = Cell.create(None : Option<Tab*TabPart>)
    let slideCell = Cell.create(None)
    let ptCell = Cell.create(None)
    let tabInfoCell = Cell.create(Map2():Map2<Tab,TabInfo>)
    let layeredWindowCell = Cell.create(None)
    let eventHandlersCell = Cell.create(Set2())
    let tabBgColor = Cell.create(Map2())
    let tabFillColor = Cell.create(Map2() : Map2<Tab, Color>)
    // Thread-safe snapshots for cross-thread reads
    [<VolatileField>]
    let mutable tabFillColorSnapshot = Map2<Tab, Color>()
    let hwndRef = ref IntPtr.Zero
    let isShrunkCell = Cell.create(false)
    // Tooltip implementation
    let tooltipForm = new Form()
    let tooltipLabel = new Label()
    let tooltipTimer = new Timer(Interval = 500)
    let lastToolTipTab = ref None
    let pendingTooltipTab = ref None
    let tooltipMaxWidth =
        use g = Graphics.FromHwnd(IntPtr.Zero)
        let dpiScale = g.DpiX / 96.0f
        int(500.0f * dpiScale)

    let isMouseOverExport = Cell.export <| fun() ->
        hoverCell.value.IsSome

    let addEvent(evt,handler) =
        eventHandlersCell.map(fun s -> s.add(_os.setSingleWinEvent evt handler))
    do  
        addEvent(WinEvent.EVENT_SYSTEM_SWITCHSTART, fun(hwnd) -> isInAltTabCell.set(true))
        addEvent(WinEvent.EVENT_SYSTEM_SWITCHEND, fun(hwnd) -> isInAltTabCell.set(false))
        
        // Initialize tooltip with Windows 10/11 dark theme style
        tooltipForm.FormBorderStyle <- FormBorderStyle.None
        tooltipForm.ShowInTaskbar <- false
        tooltipForm.StartPosition <- FormStartPosition.Manual
        tooltipForm.BackColor <- Color.FromArgb(40, 40, 40) // Dark gray background
        tooltipForm.AutoSize <- false  // Manual size control
        tooltipForm.Padding <- new Padding(8, 8, 8, 8) // Equal padding on all sides
        tooltipForm.TopMost <- true
        // Set form opacity for modern look
        tooltipForm.Opacity <- 0.95
        
        tooltipLabel.ForeColor <- Color.White
        tooltipLabel.BackColor <- Color.FromArgb(40, 40, 40)
        tooltipLabel.Font <- SystemFonts.MenuFont
        tooltipLabel.TextAlign <- ContentAlignment.TopLeft  // Top-left aligned for proper wrapping
        tooltipLabel.AutoSize <- false  // Keep false for proper width control
        tooltipLabel.MaximumSize <- new Size(tooltipMaxWidth, 0)
        tooltipLabel.Dock <- DockStyle.Fill  // Fill the parent container
        tooltipLabel.Parent <- tooltipForm
        
        // Custom paint for rounded corners
        tooltipForm.Paint.Add(fun e ->
            let g = e.Graphics
            g.SmoothingMode <- SmoothingMode.AntiAlias
            let rect = new Rectangle(0, 0, tooltipForm.Width - 1, tooltipForm.Height - 1)
            use path = new GraphicsPath()
            let radius = 4
            path.AddArc(rect.X, rect.Y, radius * 2, radius * 2, 180.0f, 90.0f)
            path.AddArc(rect.Right - radius * 2, rect.Y, radius * 2, radius * 2, 270.0f, 90.0f)
            path.AddArc(rect.Right - radius * 2, rect.Bottom - radius * 2, radius * 2, radius * 2, 0.0f, 90.0f)
            path.AddArc(rect.X, rect.Bottom - radius * 2, radius * 2, radius * 2, 90.0f, 90.0f)
            path.CloseFigure()
            tooltipForm.Region <- new Region(path)
        )
        
        tooltipTimer.Tick.Add(fun _ ->
            tooltipTimer.Stop()
            match !pendingTooltipTab with
            | Some(tab) ->
                this.updateTooltipForTab tab
                pendingTooltipTab := None
            | None -> ()
        )
        
        layeredWindowCell.value <-
            let style = WindowsStyles.WS_POPUP
            let styleExe =
                WindowsExtendedStyles.WS_EX_LAYERED ||| 
                WindowsExtendedStyles.WS_EX_TOOLWINDOW
            Some(_os.createWindow this.wndProc style styleExe)
        hwndRef := layeredWindowCell.value.Value.hwnd
        
        isMouseOverExport.init()

        // Sync pinned tabs snapshot for thread-safe cross-thread reads
        Cell.listen <| fun() ->
            pinnedTabsSnapshot <- pinnedTabsCell.value

        // Sync tab color snapshots for thread-safe cross-thread reads
        Cell.listen <| fun() ->
            tabFillColorSnapshot <- tabFillColor.value

        Cell.listen <| fun() ->
            this.update()
       
    member private this.inAltSwitch = isInAltTabCell.value

    member private this.layeredWindow = layeredWindowCell.value.Value
    member private this.os = _os
    member private this.window : Window = this.os.windowFromHwnd(this.hwnd)
    member private this.pt = ptCell.value.Value
    member private this.ptScreen = this.window.ptToScreen(this.pt)
    member private this.setPt = ptCell.set

    member private this.size = sizeCell.value

    member this.showInside = showInsideCell.value

    member private this.tsBase direction =
        {
            tabs = Map2(this.tabs.items.map <| fun tab ->
                let ti = this.tabInfo(tab)
                let tabInfo = {
                    bgColor = tabBgColor.value.tryFind(tab)
                    fillColor = tabFillColor.value.tryFind(tab)
                    TabDisplayInfo.text = ti.text
                    icon = ti.iconSmall
                    textFont = SystemFonts.MenuFont
                    textBrush = SystemBrushes.MenuText
                }
                tab,tabInfo
            )
            hover = hoverCell.value
            captured = capturedCell.value
            lorder = lorderCell.value
            zorder = zorderCell.value
            size = this.size
            slide = this.slide
            direction = direction
            alignment = alignment.value
            pinnedTabs = pinnedTabsCell.value
            transparent = this.transparent
            appearance = this.appearance
        }
    member private this.ts = this.tsBase this.direction
        
    member private this.onMouse(down, pt:Pt, btn, (tab:Tab, part)) =
        monitor.tabClick(btn, tab, part, down, pt)
        
    member private this.hit : Option<Tab*TabPart> = maybe {
        let! pt = ptCell.value
        let! hit = this.ts.tryHit(pt)
        return hit
        }

    // Tooltip hit test: boundary at midpoint of tab overlap area, ignoring z-order
    member private this.hitForTooltipTab : Option<Tab> = maybe {
        let! pt = ptCell.value
        let! tab = this.ts.tryHitForTooltip(pt)
        return tab
        }

    member private this.updateTooltipForTab(tab: Tab) =
        let tabInfo = this.tabInfo(tab)
        if tabInfo.text <> "" then
            // Move off-screen and ensure visible so rendering can occur
            tooltipForm.Location <- new Point(-10000, -10000)
            if not tooltipForm.Visible then
                tooltipForm.Visible <- true

            // Update text and size while off-screen
            tooltipLabel.Text <- tabInfo.text

            // Calculate proper size based on text
            use g = tooltipLabel.CreateGraphics()
            let textSize = g.MeasureString(tabInfo.text, tooltipLabel.Font, tooltipMaxWidth)
            let charWidth = g.MeasureString("W", tooltipLabel.Font).Width
            let labelWidth = min tooltipMaxWidth (int(textSize.Width + charWidth) + 16)
            let labelHeight = int(textSize.Height) + 16
            tooltipForm.Size <- new Size(labelWidth, labelHeight)

            // Force synchronous repaint with new content while off-screen
            tooltipForm.Update()

            // Calculate final position based on tab's left edge, always below tab strip
            let tabLoc : Pt = this.tabLocation tab
            let stripLoc : Pt = this.location
            let tabScreenX = stripLoc.x + tabLoc.x
            let tabScreenY = stripLoc.y
            let tabStripHeight = this.ts.size.height
            tooltipForm.Location <- new Point(tabScreenX, tabScreenY + tabStripHeight + 2)

            // Ensure tooltip is within screen bounds
            let screen = Screen.FromPoint(new Point(tabScreenX, tabScreenY))
            let formRight = tooltipForm.Location.X + tooltipForm.Width
            let formBottom = tooltipForm.Location.Y + tooltipForm.Height
            if formRight > screen.WorkingArea.Right then
                tooltipForm.Location <- new Point(screen.WorkingArea.Right - tooltipForm.Width, tooltipForm.Location.Y)
            if formBottom > screen.WorkingArea.Bottom then
                tooltipForm.Location <- new Point(tooltipForm.Location.X, tabScreenY - tooltipForm.Height - 5)

            tooltipForm.BringToFront()

    member private this.processMouse(mouse) =
        match mouse with
        | MouseMove(pt) ->
            this.setPt(Some(pt))
            if this.window.hasCapture.not then 
                this.window.trackMouseLeave()
            let currentHit = this.hit
            hoverCell.set(currentHit)
            // Update tooltip using overlap-midpoint hit test (ignores tab curves and z-order)
            let tooltipTab = this.hitForTooltipTab
            match tooltipTab with
            | Some(tab) ->
                if !lastToolTipTab <> Some(tab) then
                    let wasVisible = tooltipForm.Visible
                    tooltipTimer.Stop()
                    lastToolTipTab := Some(tab)
                    pendingTooltipTab := Some(tab)
                    if wasVisible then
                        // Tooltip was showing for previous tab - update immediately
                        // updateTooltipForTab moves off-screen, updates content, repaints, then moves to position
                        this.updateTooltipForTab tab
                        pendingTooltipTab := None
                    else
                        // No tooltip visible - show with delay
                        tooltipTimer.Start()
            | None ->
                if !lastToolTipTab <> None then
                    lastToolTipTab := None
                    pendingTooltipTab := None
                    tooltipTimer.Stop()
                    tooltipForm.Visible <- false
            // Mouse hover to activate tab
            let enableHoverActivate = Services.settings.getValue("enableHoverActivate").cast<bool>()
            if enableHoverActivate then 
                currentHit.iter <| fun(hitTab, hitPart) ->
                    monitor.tabActivate(hitTab)
        | MouseClick(pt, btn, action) ->
            this.setPt(Some(pt))
            // Hide tooltip when right-click to prevent conflict with context menu
            if btn = MouseRight then
                lastToolTipTab := None
                pendingTooltipTab := None
                tooltipTimer.Stop()
                tooltipForm.Visible <- false
            this.hit.iter <| fun(hitTab, hitPart) ->
                match action with
                | MouseDown ->
                    capturedCell.set(Some(hitTab, hitPart))
                | MouseUp ->
                    capturedCell.value.iter <| fun(capturedTab, capturedPart) ->
                    if  btn = MouseLeft &&
                        hitTab = capturedTab &&
                        hitPart = capturedPart then
                        match hitPart with
                        | TabClose -> monitor.tabClose(hitTab)
                        | TabPin -> monitor.tabPin(hitTab)
                        | _ -> ()
                    capturedCell.set(None)
                | MouseDblClick ->
                    ()
                this.onMouse(action, pt, btn, (hitTab, hitPart))
            hoverCell.set(this.hit)
        | MouseLeave ->
            this.setPt(None)
            capturedCell.set(None)
            hoverCell.set(None)
            // Hide tooltip when mouse leaves
            lastToolTipTab := None
            pendingTooltipTab := None
            tooltipTimer.Stop()
            tooltipForm.Visible <- false
        this.update()

    member private this.wndProc(msg:Win32Message) =
        let mousePt() = msg.lParam.location
        let mouseDown btn =
            this.processMouse(MouseClick(mousePt(), btn, MouseDown))
            msg.def()
        let mouseUp btn = 
            this.processMouse(MouseClick(mousePt(), btn, MouseUp))
            msg.def()
        let mouseDblClick btn =
            this.processMouse(MouseClick(mousePt(), btn, MouseDblClick))
            msg.def()

        //don't callback until the window has been created
        if layeredWindowCell.value.IsSome then
            monitor.windowMsg(msg)

        match msg.msg with
        | WindowMessages.WM_MOUSEACTIVATE ->
            MouseActivateReturnCodes.MA_NOACTIVATE
        | WindowMessages.WM_MOUSEMOVE ->
            this.processMouse(MouseMove(mousePt()))
            msg.def()
        | WindowMessages.WM_LBUTTONDOWN -> mouseDown MouseLeft
        | WindowMessages.WM_LBUTTONUP -> mouseUp MouseLeft
        | WindowMessages.WM_LBUTTONDBLCLK -> mouseDblClick(MouseLeft)
        | WindowMessages.WM_RBUTTONDOWN -> mouseDown MouseRight
        | WindowMessages.WM_RBUTTONUP -> mouseUp MouseRight
        | WindowMessages.WM_MBUTTONDOWN -> mouseDown MouseMiddle
        | WindowMessages.WM_MBUTTONUP -> mouseUp MouseMiddle
        | WindowMessages.WM_MOUSELEAVE ->
            this.processMouse(MouseLeave)
            msg.def()
        | _ ->
            msg.def()

    member private this.appearance = appearanceCell.value.Value
    member private this.top = zorderCell.value.head
    member private this.isEmpty = this.lorder.isEmpty
    member private this.contentOffset = this.appearance.tabHeightOffset
    member private this.location = locationCell.value 
    
    member private this.update() = 
        if this.visible then 
            this.window.update(this.render, this.location, this.alpha)
            GC.Collect()
        else this.window.hide()
    
    member private this.render : Img = 
        try
            let img = this.ts.render
            if this.isShrunk && this.direction = TabDirection.TabDown then
                img.clip(Rect(Pt(0, img.height - 7), Sz(img.width, 7)))
            else
                img
        with ex -> 
            Img(Sz(1,1))

    
    member private this.withUpdate f =
        Cell.beginUpdate()
        let result = f()
        Cell.endUpdate()
        result

    member this.hwnd = hwndRef.Value
    
    member this.addTabSlide tab (slide:Option<_>) =
        Cell.beginUpdate()
        let addToEnd(l:Cell<List2<_>>)=
            if l.value.any((=) tab).not then
                l.map(fun l -> l.append(tab))
        addToEnd(lorderCell)
        addToEnd(zorderCell)
        // Restore pinned state from cross-group transfer
        let (Tab h) = tab
        if recentlyPinnedTabs.Remove(h) then
            pinnedTabsCell.set(pinnedTabsCell.value.add(tab))
            // Move pinned tab to the end of pinned zone in lorder
            let pinnedCount = lorderCell.value.where(fun t -> pinnedTabsCell.value.contains(t)).length
            lorderCell.set(lorderCell.value.move((=) tab, pinnedCount - 1))
        slide.iter <| fun slide ->
            this.slide <- Some(slide)
        Cell.endUpdate()
    
    member this.addTab tab = this.addTabSlide tab None

    member this.removeTab tab =
        Cell.beginUpdate()
        // Remember pinned state for cross-group transfer
        let (Tab h) = tab
        if pinnedTabsCell.value.contains(tab) then
            recentlyPinnedTabs.Add(h) |> ignore
            pinnedTabsCell.set(pinnedTabsCell.value.remove(tab))
        else
            recentlyPinnedTabs.Remove(h) |> ignore
        lorderCell.map(fun l -> l.where((<>) tab))
        zorderCell.map(fun z -> z.where((<>) tab))
        tabInfoCell.map(fun m -> m.remove tab)
        Cell.endUpdate()

    member this.tabs : Set2<Tab> = Set2(lorderCell.value)

    member this.lorder 
        with get() : List2<_> = lorderCell.value


    member this.movedTab = this.ts.movedTab

    member this.moveTab(tab, index) =
        Cell.beginUpdate()
        lorderCell.set(lorderCell.value.move((=) tab, index))
        // Auto-pin/unpin based on drop position (VSCode-style cross-zone drag)
        let newLorder = lorderCell.value
        let tabIndex = newLorder.tryFindIndex((=) tab)
        match tabIndex with
        | Some idx ->
            let currentPinnedCount =
                newLorder.where(fun t -> pinnedTabsCell.value.contains(t)).length
            if idx < currentPinnedCount then
                // Dropped in pinned zone -> pin the tab
                if not (pinnedTabsCell.value.contains(tab)) then
                    pinnedTabsCell.set(pinnedTabsCell.value.add(tab))
            else
                // Dropped in unpinned zone -> unpin the tab
                if pinnedTabsCell.value.contains(tab) then
                    pinnedTabsCell.set(pinnedTabsCell.value.remove(tab))
        | None -> ()
        Cell.endUpdate()
        monitor.tabMoved(tab, index)
        tabMovedEvent.Trigger(tab, index)

    member this.tabMoved = tabMovedEvent.Publish

    member this.zorder
        with get() = zorderCell.value
        and set(zorder:List2<Tab>) =
            zorderCell.set(zorder.where(this.tabs.contains))

    member this.sprite = this.ts.sprite
            
    member this.isShrunk
        with get() = isShrunkCell.value
        and set(newValue) = isShrunkCell.set(newValue)

    member this.pinnedTabs = pinnedTabsCell.value

    member this.isPinned(tab) = pinnedTabsCell.value.contains(tab)

    // Thread-safe version for cross-thread reads (reads from volatile snapshot)
    member this.isPinnedThreadSafe(tab) = pinnedTabsSnapshot.contains(tab)

    member this.pinTab(tab) =
        if not (pinnedTabsCell.value.contains(tab)) then
            pinnedTabsCell.set(pinnedTabsCell.value.add(tab))
            // Move pinned tab to the end of pinned zone in lorder
            let pinnedCount = lorderCell.value.where(fun t -> pinnedTabsCell.value.contains(t)).length
            lorderCell.set(lorderCell.value.move((=) tab, pinnedCount - 1))

    member this.unpinTab(tab) =
        if pinnedTabsCell.value.contains(tab) then
            pinnedTabsCell.set(pinnedTabsCell.value.remove(tab))
            // Move unpinned tab to the start of unpinned zone (= end of pinned zone)
            let pinnedCount = lorderCell.value.where(fun t -> pinnedTabsCell.value.contains(t)).length
            lorderCell.set(lorderCell.value.move((=) tab, pinnedCount))

    member this.pinAll() =
        let allTabs = lorderCell.value
        pinnedTabsCell.set(allTabs.fold (Set2<Tab>()) (fun s t -> s.add(t)))

    member this.unpinAll() =
        pinnedTabsCell.set(Set2<Tab>())

    // Count of unpinned tabs to the left of the given tab (in lorder)
    member this.unpinnedCountToLeft(tab) =
        match lorderCell.value.tryFindIndex((=) tab) with
        | Some idx ->
            lorderCell.value.list
            |> Seq.take idx
            |> Seq.filter (fun t -> not (pinnedTabsCell.value.contains(t)))
            |> Seq.length
        | None -> 0

    // Count of pinned tabs to the right of the given tab (in lorder)
    member this.pinnedCountToRight(tab) =
        match lorderCell.value.tryFindIndex((=) tab) with
        | Some idx ->
            lorderCell.value.list
            |> Seq.skip (idx + 1)
            |> Seq.filter (fun t -> pinnedTabsCell.value.contains(t))
            |> Seq.length
        | None -> 0

    // Pin all unpinned tabs to the left of the given tab
    member this.pinLeftTabs(tab) =
        Cell.beginUpdate()
        match lorderCell.value.tryFindIndex((=) tab) with
        | Some idx ->
            let mutable newPinned = pinnedTabsCell.value
            lorderCell.value.list |> List.iteri (fun i t ->
                if i <= idx && not (newPinned.contains(t)) then
                    newPinned <- newPinned.add(t)
            )
            pinnedTabsCell.set(newPinned)
        | None -> ()
        Cell.endUpdate()

    // Unpin all pinned tabs to the right of the given tab (including the tab itself)
    member this.unpinRightTabs(tab) =
        Cell.beginUpdate()
        match lorderCell.value.tryFindIndex((=) tab) with
        | Some idx ->
            let mutable newPinned = pinnedTabsCell.value
            lorderCell.value.list |> List.iteri (fun i t ->
                if i >= idx && newPinned.contains(t) then
                    newPinned <- newPinned.remove(t)
            )
            pinnedTabsCell.set(newPinned)
        | None -> ()
        Cell.endUpdate()

    member this.isMouseOver = isMouseOverExport :> ICellOutput<_>

    member this.getAlignment direction = alignment.value

    member this.setAlignment((direction, newAlignment)) = alignment.set(newAlignment)
            
    member this.direction = if showInsideCell.value then TabDown else TabUp
    
    member this.tabInfo tab : TabInfo = 
        tabInfoCell.value.tryFind(tab).def({
            text = ""
            isRenamed = false
            iconSmall = System.Drawing.SystemIcons.Application
            iconBig = System.Drawing.SystemIcons.Application
            preview = fun() -> Img(Sz(1,1))
        })

    member this.setTabInfo((tab, tabInfo)) = 
        tabInfoCell.map(fun m -> m.add tab tabInfo)
            
    member this.tabLocation = this.ts.tabLocation

    member this.dragTabLocation (tab:Tab) : Pt=
        // Calculate tab location for the drag preview
        let bmpHwnd : Img = this.tabInfo(tab).preview()
        let previewWidth = bmpHwnd.width

        // Create a TabStrip with scaled size and single tab
        let baseTabStrip = this.tsBase(TabUp)
        let scaledTabStrip = {
            baseTabStrip with
                size = Sz(previewWidth, baseTabStrip.size.height)
                alignment = alignment.value  // Use actual alignment setting
                lorder = List2([tab])  // Only the dragged tab
                zorder = List2([tab])
        }

        // Return the tab location in the scaled strip
        scaledTabStrip.tabLocation(tab)

    member this.dragImage (tab:Tab) : Img=
        // Get the window preview to determine the target size
        let bmpHwnd : Img = this.tabInfo(tab).preview()
        let previewWidth = bmpHwnd.width

        // Calculate the scale ratio
        let scaleRatio = float(previewWidth) / float(this.size.width)

        // Create a TabStrip with scaled size and single tab
        let baseTabStrip = this.tsBase(TabUp)
        let scaledTabStrip = {
            baseTabStrip with
                size = Sz(previewWidth, baseTabStrip.size.height)
                alignment = alignment.value  // Use actual alignment setting
                lorder = List2([tab])  // Only the dragged tab
                zorder = List2([tab])
        }

        // Render the scaled tab strip
        let fullStripImg = scaledTabStrip.render

        // Combine the tab strip with the window preview
        let bmpOverlay = Img(Sz(previewWidth, bmpHwnd.height + fullStripImg.height - this.contentOffset))
        let gCapture = bmpOverlay.graphics
        gCapture.DrawImage(fullStripImg.bitmap, Point.Empty)
        gCapture.DrawImage(bmpHwnd.bitmap, new Point(0, this.size.height - this.contentOffset))
        gCapture.Dispose()
        bmpOverlay

    member this.setTabBgColor((tab, color)) =
        match color with
        | Some(color) ->
            tabBgColor.map(fun m -> m.add tab color)
        | None ->
            tabBgColor.map(fun m -> m.remove tab)

    member this.setTabFillColor(tab, color : Color option) =
        match color with
        | Some(c) -> tabFillColor.map(fun m -> m.add tab c)
        | None -> tabFillColor.map(fun m -> m.remove tab)

    member this.getTabFillColor(tab) = tabFillColor.value.tryFind(tab)

    // Thread-safe versions for cross-thread reads
    member this.getTabFillColorThreadSafe(tab) = tabFillColorSnapshot.tryFind(tab)

    member this.setTabAppearance(appearance) = appearanceCell.set(Some(appearance))
            
    member this.contentBounds 
        with get() = contentBoundsCell.value
        and set(value) = contentBoundsCell.set(value)
            
    member this.foreground 
        with get() = foregroundCell.value
        and set(value) =
            prevForegroundCell.set(this.foreground)
            foregroundCell.set(value) 
            
    member this.bounds = this.window.bounds

    member this.setPlacement(placement) =
        showInsideCell.set(placement.showInside)
        sizeCell.set(placement.bounds.size)
        locationCell.set(placement.bounds.location)   
     
    member this.alpha
        with get() = alphaCell.value
        and set(value) = alphaCell.set(value)

    member this.visible 
        with get() = visibleCell.value
        and set(value) = visibleCell.set(value)
            
    member this.transparent 
        with get() = transparentCell.value
        and set(value) = transparentCell.set(value)

    member this.slide 
        with get() : (Tab * int) option = slideCell.value
        and set(value) = slideCell.set(value)

    member this.renderTs(top) =
        let ts = this.ts
        let ts = 
            { ts with
                zorder = 
                    match top with
                    | Some(top) -> ts.zorder.moveToEnd((=)top)
                    | None -> ts.zorder }
        ts.render

    member this.destroy() = 
        eventHandlersCell.value.items.iter(fun d -> d.Dispose())
        layeredWindowCell.value.iter <| fun w -> (w :?> IDisposable).Dispose()
        tooltipTimer.Dispose()
        tooltipForm.Dispose()
        this.window.destroy()
            
    member this.tryHit(pt) : Option<_> = this.ts.tryHit(pt)
