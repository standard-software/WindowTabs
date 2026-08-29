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
    // DPI scale of the monitor the strip is currently placed on (1.0 = 100%),
    // refreshed from every setPlacement. The process is Per-Monitor-V2 aware,
    // so the strip's bitmap is presented 1:1 on that monitor and every
    // self-drawn size has to be multiplied by this factor to keep its former
    // apparent size. It replaces the old non-integer-DPI draw correction: with
    // no OS bitmap stretch left there is no drift to compensate for.
    let scaleCell = Cell.create(1.0)
    let visualOrderCell = Cell.create(List2())
    let zorderCell = Cell.create(List2())
    let visibleCell = Cell.create(false)
    let transparentCell = Cell.create(true)
    let showInsideCell = Cell.create(false)
    let isInAltTabCell = Cell.create(false)
    let pinnedTabsCell = Cell.create(Set2<Tab>())
    // Thread-safe snapshot of pinned tabs for cross-thread reads (e.g., save from main thread)
    [<VolatileField>]
    let mutable pinnedTabsSnapshot = Set2<Tab>()
    // Thread-safe snapshot of the on-screen visual order. GroupInfo's
    // windowsCell mirror misses reorders done by normalizeVisualOrder
    // (pin/unpin/align), so cross-thread readers that need the REAL order
    // (save, closed-tab recording) must use this instead.
    [<VolatileField>]
    let mutable visualOrderSnapshot = List2<Tab>()
    // Selected tabs (multi-select via Shift/Ctrl click). The active tab is
    // never part of this set — it is the implicit primary action target.
    let selectedTabsCell = Cell.create(Set2<Tab>())
    let tabAlignmentCell = Cell.create(Map2<Tab, TabAlign>())
    [<VolatileField>]
    let mutable tabAlignmentSnapshot = Map2<Tab, TabAlign>()
    let defaultAlignmentCell = Cell.create(TopRight)
    let alignment = Cell.create(TopRight)
    let capturedCell = Cell.create(None : Option<Tab*TabPart>)
    let hoverCell = Cell.create(None : Option<Tab*TabPart>)
    let slideCell = Cell.create(None)
    // Multi-tab drag group: set by the decorator on drag begin to the
    // contiguous selected range (in visualOrder). The first element is the
    // pivot (= slide.tab). Cleared on drag end.
    let dragGroupCell = Cell.create<Tab list>([])
    let ptCell = Cell.create(None)
    let tabInfoCell = Cell.create(Map2():Map2<Tab,TabInfo>)
    let layeredWindowCell = Cell.create(None)
    let eventHandlersCell = Cell.create(Set2())
    let tabBgColor = Cell.create(Map2())
    let tabFillColor = Cell.create(Map2() : Map2<Tab, Color>)
    let tabUnderlineColor = Cell.create(Map2() : Map2<Tab, Color>)
    let tabBorderColor = Cell.create(Map2() : Map2<Tab, Color>)
    // Thread-safe snapshots for cross-thread reads
    [<VolatileField>]
    let mutable tabFillColorSnapshot = Map2<Tab, Color>()
    [<VolatileField>]
    let mutable tabUnderlineColorSnapshot = Map2<Tab, Color>()
    [<VolatileField>]
    let mutable tabBorderColorSnapshot = Map2<Tab, Color>()
    let hwndRef = ref IntPtr.Zero
    let isShrunkCell = Cell.create(false)
    // Tooltip implementation
    let tooltipForm = new Form()
    let tooltipLabel = new Label()
    let tooltipTimer = new Timer(Interval = 500)
    // Polling fallback for stuck tooltips: WM_MOUSELEAVE does not always fire
    // (e.g. when the cursor glides off the leftmost tab along a screen edge
    // and then moves further), which would otherwise leave the tooltip
    // permanently on screen. While the tooltip is visible, this re-checks the
    // real cursor position on a short interval and force-hides the tooltip
    // once the cursor has actually left the strip's tooltip hit area.
    let tooltipHideTimer = new Timer(Interval = 200)
    let lastToolTipTab = ref None
    let pendingTooltipTab = ref None
    // Tooltip metrics are scaled per strip (see this.scale), not from the
    // system DPI as before: on a desktop whose primary monitor is scaled, the
    // old Graphics.FromHwnd(IntPtr.Zero) reading made tooltips on the 100%
    // monitors 1.5x too wide as well.
    let tooltipBaseMaxWidth = 500

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
        tooltipForm.Padding <- new Padding(8, 8, 8, 8) // Equal padding on all sides (rescaled per monitor on show)
        // Size and font are computed in device pixels for the strip's monitor;
        // WinForms must not apply its own scaling on top of them.
        tooltipForm.AutoScaleMode <- AutoScaleMode.None
        tooltipForm.TopMost <- true
        // Set form opacity for modern look
        tooltipForm.Opacity <- 0.95
        
        tooltipLabel.ForeColor <- Color.White
        tooltipLabel.BackColor <- Color.FromArgb(40, 40, 40)
        tooltipLabel.Font <- SystemFonts.MenuFont // replaced with the DPI-scaled font on show
        tooltipLabel.TextAlign <- ContentAlignment.TopLeft  // Top-left aligned for proper wrapping
        tooltipLabel.AutoSize <- false  // Keep false for proper width control
        tooltipLabel.MaximumSize <- new Size(tooltipBaseMaxWidth, 0)
        tooltipLabel.Dock <- DockStyle.Fill  // Fill the parent container
        tooltipLabel.Parent <- tooltipForm
        
        // Custom paint for rounded corners
        tooltipForm.Paint.Add(fun e ->
            let g = e.Graphics
            g.SmoothingMode <- SmoothingMode.AntiAlias
            let rect = new Rectangle(0, 0, tooltipForm.Width - 1, tooltipForm.Height - 1)
            use path = new GraphicsPath()
            let radius = Dpi.px scaleCell.value 4
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

        tooltipHideTimer.Tick.Add(fun _ ->
            this.tryHideTooltipIfCursorLeft()
            this.validateHoverAgainstCursor())
        tooltipHideTimer.Start()
        
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
        Cell.listen <| fun() ->
            tabAlignmentSnapshot <- tabAlignmentCell.value
        Cell.listen <| fun() ->
            visualOrderSnapshot <- visualOrderCell.value

        // Sync tab color snapshots for thread-safe cross-thread reads
        Cell.listen <| fun() ->
            tabFillColorSnapshot <- tabFillColor.value
        Cell.listen <| fun() ->
            tabUnderlineColorSnapshot <- tabUnderlineColor.value
        Cell.listen <| fun() ->
            tabBorderColorSnapshot <- tabBorderColor.value

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

    // DPI scale of the monitor the strip is placed on. Every hand-drawn size
    // in the strip is written as a 96-dpi design unit and multiplied by this.
    member this.scale = scaleCell.value

    // Which of the window's two published icons to use as the drawing source.
    //
    // Above 100% the icon box is bigger than 16 px, so the small icon would
    // have to be enlarged and would stay soft while everything around it is
    // finally crisp; the big icon (usually 32 px) is the better source, and
    // ScaledIcon then asks Windows for the exact box size from the underlying
    // resource. A window that publishes no big icon gets the shared
    // SystemIcons.Application instance from Window.icon - detected by
    // reference, so such windows keep their real small icon instead of being
    // silently replaced by the generic one.
    member private this.tabIcon (ti: TabInfo) =
        let isGeneric (icon: Icon) = Object.ReferenceEquals(icon, SystemIcons.Application)
        if isGeneric ti.iconBig then ti.iconSmall
        elif isGeneric ti.iconSmall then ti.iconBig
        elif this.scale > 1.0 then ti.iconBig
        else ti.iconSmall

    member private this.tsBase direction =
        // One font instance for the whole sprite tree (pixel-sized, see Dpi).
        let textFont = Dpi.scaledFont SystemFonts.MenuFont this.scale
        {
            tabs = Map2(this.tabs.items.map <| fun tab ->
                let ti = this.tabInfo(tab)
                let tabInfo = {
                    bgColor = tabBgColor.value.tryFind(tab)
                    fillColor = tabFillColor.value.tryFind(tab)
                    underlineColor = tabUnderlineColor.value.tryFind(tab)
                    borderColor = tabBorderColor.value.tryFind(tab)
                    TabDisplayInfo.text = ti.text
                    icon = this.tabIcon ti
                    textFont = textFont
                    textBrush = SystemBrushes.MenuText
                }
                tab,tabInfo
            )
            hover = hoverCell.value
            captured = capturedCell.value
            visualOrder = visualOrderCell.value
            zorder = zorderCell.value
            size = this.size
            slide = this.slide
            direction = direction
            tabAlignments = tabAlignmentCell.value
            pinnedTabs = pinnedTabsCell.value
            selectedTabs = selectedTabsCell.value
            transparent = this.transparent
            appearance = this.appearance
            dragGroup = dragGroupCell.value
            scale = this.scale
        }
    member private this.ts = this.tsBase this.direction
        
    member private this.onMouse(down, pt:Pt, btn, (tab:Tab, part)) =
        monitor.tabClick(btn, tab, part, down, pt)
        
    member private this.hit : Option<Tab*TabPart> = maybe {
        let! pt = ptCell.value
        let! hit = this.ts.tryHit(pt)
        return hit
        }

    // Polling fallback for stuck tooltips. Called from tooltipHideTimer; the
    // body lives on a member (not inline in the do block) so this.ts and
    // this.location resolve through normal forward-reference handling.
    //
    // Strict-bounds test instead of tryHitForTooltip: hitInGroup returns
    // Some(leftmost tab) for any clientX < 0 (cursor is to the left of the
    // strip on another monitor), so a tryHitForTooltip-based check misses
    // the case where the cursor moves to a display that sits to the left of
    // the strip's own display. Rectangle-only containment catches that.
    member private this.tryHideTooltipIfCursorLeft() =
        if tooltipForm.Visible then
            try
                let c = System.Windows.Forms.Cursor.Position
                let loc : Pt = this.location
                let sz : Sz = this.size
                let clientX = c.X - loc.x
                let clientY = c.Y - loc.y
                let outsideStrip =
                    clientX < 0 || clientX >= sz.width
                    || clientY < 0 || clientY >= sz.height
                if outsideStrip then
                    lastToolTipTab := None
                    pendingTooltipTab := None
                    tooltipTimer.Stop()
                    tooltipForm.Visible <- false
            with _ -> ()

    // Hover is otherwise driven purely by mouse Enter/Move/Leave events, so a
    // tab redrawn while the cursor is elsewhere (e.g. the strip reappearing
    // after a group restore) can keep a stale hover color forever. Reconcile
    // the hover state with the real cursor position: if the cursor is not
    // over the strip at all, there can be no hovered tab.
    member private this.validateHoverAgainstCursor() =
        if hoverCell.value.IsSome then
            try
                let c = System.Windows.Forms.Cursor.Position
                let loc : Pt = this.location
                let sz : Sz = this.size
                let clientX = c.X - loc.x
                let clientY = c.Y - loc.y
                let outsideStrip =
                    clientX < 0 || clientX >= sz.width
                    || clientY < 0 || clientY >= sz.height
                if outsideStrip then
                    hoverCell.set(None)
            with _ -> ()

    // Tooltip hit test: boundary at midpoint of tab overlap area, ignoring z-order
    member private this.hitForTooltipTab : Option<Tab> = maybe {
        let! pt = ptCell.value
        let! tab = this.ts.tryHitForTooltip(pt)
        return tab
        }

    member private this.updateTooltipForTab(tab: Tab) =
        let tabInfo = this.tabInfo(tab)
        if tabInfo.text <> "" then
            // Everything below is device pixels for the strip's OWN monitor.
            // The font is a pixel-sized copy of the menu font, so MeasureString
            // returns device pixels whatever DPI this control's DC reports.
            let scale = this.scale
            let scaled = Dpi.px scale
            let tooltipMaxWidth = scaled tooltipBaseMaxWidth
            let padding = scaled 8
            tooltipForm.Padding <- new Padding(padding, padding, padding, padding)
            tooltipLabel.Font <- Dpi.scaledFont SystemFonts.MenuFont scale
            tooltipLabel.MaximumSize <- new Size(tooltipMaxWidth, 0)
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
            let labelWidth = min tooltipMaxWidth (int(textSize.Width + charWidth) + scaled 16)
            let labelHeight = int(textSize.Height) + scaled 16
            tooltipForm.Size <- new Size(labelWidth, labelHeight)

            // Force synchronous repaint with new content while off-screen
            tooltipForm.Update()

            // Calculate final position based on tab's left edge, always below tab strip
            let tabLoc : Pt = this.tabLocation tab
            let stripLoc : Pt = this.location
            let tabScreenX = stripLoc.x + tabLoc.x
            let tabScreenY = stripLoc.y
            let tabStripHeight = this.ts.size.height
            tooltipForm.Location <- new Point(tabScreenX, tabScreenY + tabStripHeight + scaled 2)

            // Ensure tooltip is within screen bounds. Mon (a live
            // MonitorFromPoint query) instead of Screen.FromPoint: the WinForms
            // screen cache is process-wide and may hold the virtualized
            // rectangles of the DPI-unaware settings dialog.
            Mon.nearestFromPoint(Pt(tabScreenX, tabScreenY)) |> Option.iter (fun mon ->
                let work = mon.workRect
                let formRight = tooltipForm.Location.X + tooltipForm.Width
                let formBottom = tooltipForm.Location.Y + tooltipForm.Height
                if formRight > work.right then
                    tooltipForm.Location <- new Point(work.right - tooltipForm.Width, tooltipForm.Location.Y)
                if formBottom > work.bottom then
                    tooltipForm.Location <- new Point(tooltipForm.Location.X, tabScreenY - tooltipForm.Height - scaled 5))

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
            // WM_MOUSELEAVE can fire while the cursor is still on a tab: a
            // tooltip/overlay pops up over a stationary cursor, or the layered
            // window repaints at a sub-pixel edge. Clearing the hover then makes
            // the highlight and Close/Pin button vanish until the next move,
            // which reads as "hovering but no hover state". Re-check the REAL
            // cursor position; only clear when it has genuinely left the strip.
            let c = System.Windows.Forms.Cursor.Position
            let loc = this.location
            let clientPt = Pt(c.X - loc.x, c.Y - loc.y)
            let ts = this.ts
            if this.window.hasCapture.not && (ts.tryHitForTooltip(clientPt)).IsSome then
                this.setPt(Some(clientPt))
                hoverCell.set(ts.tryHit(clientPt))
            else
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
        // The mouse point is used as-is. The strip window is per-monitor DPI
        // aware, so the client point Windows reports is in the same device
        // pixels the sprite tree was laid out in: hit test == what is drawn, at
        // any DPI, with no coordinate conversion in between.
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
    member private this.isEmpty = this.visualOrder.isEmpty
    member private this.contentOffset = this.appearance.tabHeightOffset
    member private this.location = locationCell.value 
    
    member private this.update() =
        if this.visible then
            this.window.update(this.render, this.location, this.alpha)
        else this.window.hide()
    
    // No draw correction any more. The former applyDrawCorrection pre-compressed
    // the rendered strip horizontally to cancel the drift between the OS bitmap
    // stretch and the mouse virtualization of a DPI-UNAWARE layered window. A
    // per-monitor aware window is not stretched at all, so that compression
    // would now BE the distortion it used to remove - and it re-sampled the
    // whole strip with HighQualityBicubic on every frame, which fought the very
    // sharpness this change is about. (It would also be dead code: it derived
    // its rate from GetDeviceCaps HORZRES vs DESKTOPHORZRES, and an aware
    // process gets the same physical value from both, so the rate is always 0.)
    member private this.render : Img =
        try
            let img = this.ts.render
            if this.isShrunk && this.direction = TabDirection.TabDown then
                // Sliver of strip left visible when auto-hidden.
                let sliver = Dpi.px this.scale 7
                img.clip(Rect(Pt(0, img.height - sliver), Sz(img.width, sliver)))
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
        addToEnd(visualOrderCell)
        addToEnd(zorderCell)
        // Set default alignment for new tab
        if tabAlignmentCell.value.tryFind(tab).IsNone then
            tabAlignmentCell.map(fun m -> m.add tab defaultAlignmentCell.value)
        // Ensure the newly added tab sits in the correct visual zone
        this.normalizeVisualOrder()
        slide.iter <| fun slide ->
            this.slide <- Some(slide)
        Cell.endUpdate()
    
    member this.addTab tab = this.addTabSlide tab None

    member this.removeTab tab =
        // The scaled-icon cache is keyed by source HICON, and Windows recycles
        // icon handles once the window that owned them is gone. Dropping the
        // cache whenever a tab leaves a strip is the cheap, coarse way to make
        // sure a recycled handle can never resurrect the previous window's
        // picture; the cache refills with one CopyImage per visible tab.
        ScaledIcon.invalidate()
        Cell.beginUpdate()
        if pinnedTabsCell.value.contains(tab) then
            pinnedTabsCell.set(pinnedTabsCell.value.remove(tab))
        tabAlignmentCell.map(fun m -> m.remove tab)
        tabFillColor.map(fun m -> m.remove tab)
        tabUnderlineColor.map(fun m -> m.remove tab)
        tabBorderColor.map(fun m -> m.remove tab)
        visualOrderCell.map(fun l -> l.where((<>) tab))
        zorderCell.map(fun z -> z.where((<>) tab))
        tabInfoCell.map(fun m -> m.remove tab)
        Cell.endUpdate()

    member this.tabs : Set2<Tab> = Set2(visualOrderCell.value)

    member this.visualOrder
        with get() : List2<_> = visualOrderCell.value


    member this.movedTab = this.ts.movedTab

    // Single-tab move is just a special case of moveTabs (the group is a
    // 1-element list). Sharing the implementation ensures smart-pin uses
    // the same right-neighbor / left-neighbor rule for both single and
    // multi-tab drags.
    member this.moveTab(tab, index, ?newAlignment: TabAlign) =
        match newAlignment with
        | Some a -> this.moveTabs([tab], index, a)
        | None -> this.moveTabs([tab], index)

    // Multi-tab move: splice `tabs` (in the given order) into visualOrder at
    // `targetIndex`. Smart-pin: the whole group adopts the pin state of
    // the tab immediately to the RIGHT of the spliced group in the same
    // alignment zone (matches single-tab moveTab's intuition extended to
    // a group). If there is no right neighbor in the same alignment, the
    // left neighbor decides instead. If the group has no neighbor at all
    // in its alignment, the existing pin state is preserved.
    member this.moveTabs(tabs: Tab list, targetIndex: int, ?newAlignment: TabAlign) =
        if List.isEmpty tabs then () else
        Cell.beginUpdate()
        match newAlignment with
        | Some(a) ->
            for t in tabs do
                tabAlignmentCell.map(fun m -> m.add t a)
        | None -> ()
        let tabSet = Set.ofList tabs
        let cur = visualOrderCell.value.list
        let withoutTabs = cur |> List.filter (fun t -> not (Set.contains t tabSet))
        let safeIndex = max 0 (min withoutTabs.Length targetIndex)
        let newOrder =
            (withoutTabs |> List.truncate safeIndex)
            @ tabs
            @ (withoutTabs |> List.skip safeIndex)
        visualOrderCell.set(List2(newOrder))
        // Smart-pin decision: look at the immediate right neighbor of the
        // spliced group in the same alignment zone. If absent, fall back
        // to the left neighbor.
        let pivot = List.head tabs
        let pivotAlign = this.getTabAlign pivot
        let postOrder = visualOrderCell.value.list
        let groupLastIdx =
            postOrder
            |> List.mapi (fun i t -> i, t)
            |> List.filter (fun (_, t) -> Set.contains t tabSet)
            |> List.tryLast
            |> Option.map fst
        let groupFirstIdx =
            postOrder
            |> List.mapi (fun i t -> i, t)
            |> List.tryFind (fun (_, t) -> Set.contains t tabSet)
            |> Option.map fst
        let rightNeighbor =
            match groupLastIdx with
            | Some idx ->
                postOrder
                |> List.skip (idx + 1)
                |> List.tryFind (fun t ->
                    not (Set.contains t tabSet) && this.getTabAlign(t) = pivotAlign)
            | None -> None
        let leftNeighbor =
            match groupFirstIdx with
            | Some idx when idx > 0 ->
                postOrder
                |> List.truncate idx
                |> List.filter (fun t ->
                    not (Set.contains t tabSet) && this.getTabAlign(t) = pivotAlign)
                |> List.tryLast
            | _ -> None
        // Smart-pin: only flip pin state when the group ENTERS the other
        // zone. Concretely:
        //   - All-unpinned group whose RIGHT neighbor is pinned -> pin
        //     (the group has slid into the pinned zone from the right edge
        //     of unpinned).
        //   - All-pinned group whose LEFT neighbor is unpinned -> unpin
        //     (the group has slid out of the pinned zone past its right
        //     edge).
        // Other configurations leave the pin state alone — in particular,
        // an all-pinned group whose right neighbor is unpinned (= dropped
        // at the right end of its own zone) keeps its pin state.
        let groupIsPinned = pinnedTabsCell.value.contains(pivot)
        let shouldBePinned =
            if groupIsPinned then
                match leftNeighbor with
                | Some l when not (pinnedTabsCell.value.contains(l)) -> Some false
                | _ -> None
            else
                match rightNeighbor with
                | Some r when pinnedTabsCell.value.contains(r) -> Some true
                | _ -> None
        match shouldBePinned with
        | Some pin ->
            for t in tabs do
                if pin && not (pinnedTabsCell.value.contains(t)) then
                    pinnedTabsCell.set(pinnedTabsCell.value.add(t))
                elif not pin && pinnedTabsCell.value.contains(t) then
                    pinnedTabsCell.set(pinnedTabsCell.value.remove(t))
        | None -> ()
        this.normalizeVisualOrder()
        Cell.endUpdate()
        for t in tabs do
            let finalIdx =
                visualOrderCell.value.tryFindIndex((=) t) |> Option.defaultValue 0
            monitor.tabMoved(t, finalIdx)
            tabMovedEvent.Trigger(t, finalIdx)

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

    // ----- Multi-tab selection passthrough -----
    // WindowGroup owns the canonical selection set (per-hwnd). TabStrip
    // mirrors it in Tab form so the per-tab sprite construction can read it
    // without an extra hwnd→Tab translation per draw. WindowGroup pushes
    // updates here whenever its selectedTabsCell changes.
    member this.selectedTabs = selectedTabsCell.value
    member this.setSelectedTabs(newSet: Set2<Tab>) =
        if selectedTabsCell.value <> newSet then
            selectedTabsCell.set(newSet)
            this.update()
    member this.isTabSelected(tab) = selectedTabsCell.value.contains(tab)

    // Thread-safe version for cross-thread reads (reads from volatile snapshot)
    member this.isPinnedThreadSafe(tab) = pinnedTabsSnapshot.contains(tab)

    // Thread-safe visual order for cross-thread reads (reads from volatile snapshot)
    member this.visualOrderThreadSafe = visualOrderSnapshot

    // Canonical visual zone for a tab: left-pinned (0), left-unpinned (1),
    // right-pinned (2), right-unpinned (3). The stored visualOrder list is kept
    // sorted by this zone so that it exactly matches the on-screen left-to-right order.
    // Public, and deferring to TabOrder.zoneOf, because the session restore has
    // to compute an order that already agrees with the zones (see
    // setVisualOrder): a caller that guessed at them would have its list
    // quietly re-sorted by the normalize below, and a check run against a
    // second copy of the rule would not be checking this one.
    member this.visualZoneOf(tab) =
        TabOrder.zoneOf (this.getTabAlign(tab) = TopLeft) (pinnedTabsCell.value.contains(tab))

    // Re-sort the stored list into canonical visual order while preserving the
    // relative order within each zone (stable sort).
    member private this.normalizeVisualOrder() =
        let current = visualOrderCell.value.list
        let sorted = current |> List.sortBy this.visualZoneOf
        if sorted <> current then
            visualOrderCell.set(List2(sorted))

    // Put the whole strip into the given left-to-right order in one step.
    //
    // moveTab cannot express this. It takes ONE tab and an index into the
    // whole list, and normalizeVisualOrder then re-sorts by zone, so an index
    // that would carry the tab across a zone boundary is silently undone -
    // which is why reordering a group a tab at a time did nothing whatever
    // when the group's tabs were not all aligned the same way. Here the caller
    // has already worked out the complete order zone by zone, so the normalize
    // only confirms it.
    //
    // Alignment and pin state are left alone: this is a reorder, not a move.
    // In particular the smart-pin rule of moveTabs must NOT run - a restore is
    // putting the saved pin state back, not deriving a new one from whichever
    // neighbours the restore happens to have produced so far.
    member this.setVisualOrder(desired: Tab list) =
        let before = visualOrderCell.value.list
        let present = Set.ofList before
        let kept = desired |> List.filter present.Contains |> List.distinct
        // A tab the caller left out keeps its place, at the end, rather than
        // disappearing from the strip's own list.
        let keptSet = Set.ofList kept
        let missing = before |> List.filter (fun t -> not (keptSet.Contains t))
        Cell.beginUpdate()
        visualOrderCell.set(List2(kept @ missing))
        this.normalizeVisualOrder()
        Cell.endUpdate()
        let after = visualOrderCell.value.list
        if after <> before then
            // The same notification a move sends: the taskbar plugin and the
            // group's own mirror of the order both listen for it. Only the
            // positions that actually changed are announced, each with its
            // final index and in ascending order, which is what the mirror's
            // one-at-a-time move needs to arrive at the same list.
            after |> List.iteri (fun i t ->
                if (before |> List.tryItem i) <> Some(t) then
                    monitor.tabMoved(t, i)
                    tabMovedEvent.Trigger(t, i))

    member this.pinTab(tab) =
        if not (pinnedTabsCell.value.contains(tab)) then
            pinnedTabsCell.set(pinnedTabsCell.value.add(tab))
            this.normalizeVisualOrder()

    member this.unpinTab(tab) =
        if pinnedTabsCell.value.contains(tab) then
            pinnedTabsCell.set(pinnedTabsCell.value.remove(tab))
            this.normalizeVisualOrder()

    member this.pinAll() =
        let allTabs = visualOrderCell.value
        pinnedTabsCell.set(allTabs.fold (Set2<Tab>()) (fun s t -> s.add(t)))
        this.normalizeVisualOrder()

    member this.unpinAll() =
        pinnedTabsCell.set(Set2<Tab>())
        this.normalizeVisualOrder()

    // Get tabs in same alignment group as the given tab, in visual order
    member private this.sameAlignGroup(tab) =
        let tabAlign = this.getTabAlign(tab)
        visualOrderCell.value.list |> List.filter (fun t -> this.getTabAlign(t) = tabAlign)

    // Tabs to the left of (and including) the given tab in visual order, regardless of alignment or pin state
    member private this.visualLeftTabs(tab) =
        let order = this.visualOrder.list
        match order |> List.tryFindIndex ((=) tab) with
        | Some idx -> order |> List.take (idx + 1)
        | None -> []

    // Tabs to the right of (and including) the given tab in visual order, regardless of alignment or pin state
    member private this.visualRightTabs(tab) =
        let order = this.visualOrder.list
        match order |> List.tryFindIndex ((=) tab) with
        | Some idx -> order |> List.skip idx
        | None -> []

    // Count of tabs to the left (including the tab itself) in visual order, regardless of alignment or pin state
    member this.countToLeft(tab) =
        this.visualLeftTabs(tab) |> List.length

    // Count of tabs to the right (including the tab itself) in visual order, regardless of alignment or pin state
    member this.countToRight(tab) =
        this.visualRightTabs(tab) |> List.length

    // Pin all visual-left tabs of the given tab (including the tab itself), regardless of alignment
    member this.pinLeftTabs(tab) =
        Cell.beginUpdate()
        this.visualLeftTabs(tab)
        |> List.filter (fun t -> not (pinnedTabsCell.value.contains(t)))
        |> List.iter (fun t ->
            pinnedTabsCell.set(pinnedTabsCell.value.add(t)))
        this.normalizeVisualOrder()
        Cell.endUpdate()

    // Pin all visual-right tabs of the given tab (including the tab itself), regardless of alignment
    member this.pinRightTabs(tab) =
        Cell.beginUpdate()
        this.visualRightTabs(tab)
        |> List.filter (fun t -> not (pinnedTabsCell.value.contains(t)))
        |> List.iter (fun t ->
            pinnedTabsCell.set(pinnedTabsCell.value.add(t)))
        this.normalizeVisualOrder()
        Cell.endUpdate()

    // Unpin all visual-left tabs of the given tab (including the tab itself), regardless of alignment
    member this.unpinLeftTabs(tab) =
        Cell.beginUpdate()
        this.visualLeftTabs(tab)
        |> List.filter (fun t -> pinnedTabsCell.value.contains(t))
        |> List.iter (fun t ->
            pinnedTabsCell.set(pinnedTabsCell.value.remove(t)))
        this.normalizeVisualOrder()
        Cell.endUpdate()

    // Unpin all visual-right tabs of the given tab (including the tab itself), regardless of alignment
    member this.unpinRightTabs(tab) =
        Cell.beginUpdate()
        this.visualRightTabs(tab)
        |> List.filter (fun t -> pinnedTabsCell.value.contains(t))
        |> List.iter (fun t ->
            pinnedTabsCell.set(pinnedTabsCell.value.remove(t)))
        this.normalizeVisualOrder()
        Cell.endUpdate()

    // Count of same-alignment tabs to the left (including the tab itself), regardless of pin state
    member this.alignCountToLeft(tab) =
        let group = this.sameAlignGroup(tab)
        match group |> List.tryFindIndex ((=) tab) with
        | Some idx -> idx + 1
        | None -> 0

    // Count of same-alignment tabs to the right (including the tab itself), regardless of pin state
    member this.alignCountToRight(tab) =
        let group = this.sameAlignGroup(tab)
        match group |> List.tryFindIndex ((=) tab) with
        | Some idx -> group.Length - idx
        | None -> 0

    // Change alignment of same-alignment tabs to the left (including the tab itself)
    member this.alignLeftTabs(tab, newAlignment) =
        let group = this.sameAlignGroup(tab)
        match group |> List.tryFindIndex ((=) tab) with
        | Some idx ->
            let tabsToAlign = group |> List.take (idx + 1)
            tabsToAlign |> List.iter (fun t ->
                tabAlignmentCell.map(fun m -> m.add t newAlignment))
            this.normalizeVisualOrder()
        | None -> ()

    // Change alignment of same-alignment tabs to the right (including the tab itself)
    member this.alignRightTabs(tab, newAlignment) =
        let group = this.sameAlignGroup(tab)
        match group |> List.tryFindIndex ((=) tab) with
        | Some idx ->
            let tabsToAlign = group |> List.skip idx
            tabsToAlign |> List.iter (fun t ->
                tabAlignmentCell.map(fun m -> m.add t newAlignment))
            this.normalizeVisualOrder()
        | None -> ()

    member this.isMouseOver = isMouseOverExport :> ICellOutput<_>

    member this.getAlignment direction = alignment.value

    member this.setAlignment((direction, newAlignment)) =
        alignment.set(newAlignment)
        defaultAlignmentCell.set(newAlignment)

    member this.setTabAlign(tab, newAlignment) =
        tabAlignmentCell.map(fun m -> m.add tab newAlignment)
        // Restore canonical visual ordering after changing alignment
        this.normalizeVisualOrder()

    // Change several tabs' alignment as ONE operation (single map update +
    // single normalize). Aligning them one at a time sends each converted tab
    // to the far end of the destination zone in turn, which reverses the
    // group's relative order on an all-left <-> all-right switch.
    member this.setTabsAlign(tabs: Tab list, newAlignment) =
        tabAlignmentCell.map(fun m ->
            tabs |> List.fold (fun (acc: Map2<Tab, TabAlign>) t -> acc.add t newAlignment) m)
        this.normalizeVisualOrder()

    member this.getTabAlign(tab) =
        match tabAlignmentCell.value.tryFind(tab) with
        | Some(a) -> a
        | None -> defaultAlignmentCell.value

    member this.getTabAlignThreadSafe(tab) =
        match tabAlignmentSnapshot.tryFind(tab) with
        | Some(a) -> a
        | None -> defaultAlignmentCell.value
            
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

        // Create a TabStrip with scaled size and single tab, keeping the
        // dragged tab's alignment so a right-aligned tab stays at the right
        // end of the preview
        let baseTabStrip = this.tsBase(TabUp)
        let singleTabAligns = Map2<Tab, TabAlign>().add tab (this.getTabAlign(tab))
        let scaledTabStrip = {
            baseTabStrip with
                size = Sz(previewWidth, baseTabStrip.size.height)
                tabAlignments = singleTabAligns
                visualOrder = List2([tab])  // Only the dragged tab
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

        // Create a TabStrip with scaled size and single tab, keeping the
        // dragged tab's alignment (see dragTabLocation)
        let baseTabStrip = this.tsBase(TabUp)
        let singleTabAligns = Map2<Tab, TabAlign>().add tab (this.getTabAlign(tab))
        let scaledTabStrip = {
            baseTabStrip with
                size = Sz(previewWidth, baseTabStrip.size.height)
                tabAlignments = singleTabAligns
                visualOrder = List2([tab])  // Only the dragged tab
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
        | Some(c) ->
            tabFillColor.map(fun m -> m.add tab c)
            // Mutually exclusive: clear underline and border when setting fill
            tabUnderlineColor.map(fun m -> m.remove tab)
            tabBorderColor.map(fun m -> m.remove tab)
        | None -> tabFillColor.map(fun m -> m.remove tab)

    member this.getTabFillColor(tab) = tabFillColor.value.tryFind(tab)

    // Thread-safe versions for cross-thread reads
    member this.getTabFillColorThreadSafe(tab) = tabFillColorSnapshot.tryFind(tab)

    member this.setTabUnderlineColor(tab, color : Color option) =
        match color with
        | Some(c) ->
            tabUnderlineColor.map(fun m -> m.add tab c)
            // Mutually exclusive: clear fill and border when setting underline
            tabFillColor.map(fun m -> m.remove tab)
            tabBorderColor.map(fun m -> m.remove tab)
        | None -> tabUnderlineColor.map(fun m -> m.remove tab)

    member this.getTabUnderlineColor(tab) = tabUnderlineColor.value.tryFind(tab)

    member this.getTabUnderlineColorThreadSafe(tab) = tabUnderlineColorSnapshot.tryFind(tab)

    member this.setTabBorderColor(tab, color : Color option) =
        match color with
        | Some(c) ->
            tabBorderColor.map(fun m -> m.add tab c)
            // Mutually exclusive: clear fill and underline when setting border
            tabFillColor.map(fun m -> m.remove tab)
            tabUnderlineColor.map(fun m -> m.remove tab)
        | None -> tabBorderColor.map(fun m -> m.remove tab)

    member this.getTabBorderColor(tab) = tabBorderColor.value.tryFind(tab)

    member this.getTabBorderColorThreadSafe(tab) = tabBorderColorSnapshot.tryFind(tab)

    // The appearance and the monitor scale it was computed at are ONE value:
    // the appearance carries the already-scaled heights and indents, `scale`
    // covers the constants the sprite tree writes itself. Taking them as a
    // pair means no caller can push a 150% appearance while the sprites still
    // draw at 100%, which is the state a window crossing a monitor boundary
    // would otherwise be in for a frame. setPlacement pushes the same scale
    // again, with the bounds it produced.
    member this.setTabAppearance(appearance, scale: float) =
        Cell.beginUpdate()
        appearanceCell.set(Some(appearance))
        scaleCell.set(scale)
        Cell.endUpdate()
            
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
        // The scale that produced these bounds. It travels with the placement
        // instead of being re-derived here so the decorator and the strip can
        // never disagree about which monitor the strip is on.
        scaleCell.set(placement.scale)

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

    member this.dragGroup
        with get() : Tab list = dragGroupCell.value
        and set(value: Tab list) = dragGroupCell.set(value)

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
        tooltipHideTimer.Dispose()
        tooltipForm.Dispose()
        this.window.destroy()
            
    member this.tryHit(pt) : Option<_> = this.ts.tryHit(pt)
