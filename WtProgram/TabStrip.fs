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

module private StripComposition =
    [<System.Runtime.InteropServices.DllImport("dwmapi.dll")>]
    extern int DwmFlush()

// Keep tooltip display and mouse interaction from activating the window.
type TabTooltipForm() =
    inherit Form()
    override this.ShowWithoutActivation = true
    override this.CreateParams =
        let parameters = base.CreateParams
        parameters.ExStyle <- parameters.ExStyle ||| WindowsExtendedStyles.WS_EX_NOACTIVATE
        parameters

type ITabStripMonitor =
    abstract member tabClick : (MouseButton * Tab * TabPart * MouseAction * Pt) -> unit
    abstract member tabActivate : (Tab) -> unit
    abstract member tabClose : Tab -> unit
    abstract member tabPin : Tab -> unit
    abstract member tabMoved : Tab * int -> unit
    abstract member windowMsg : Win32Message -> unit

// Owns only the final strip bitmap. Window.update borrows it synchronously;
// its temporary composite and native HBITMAP are released before returning.
// A failed render leaves the previous entry intact so the next update retries.
type StripRenderCache() =
    let mutable cached : ((string * obj) list * Img) option = None
#if DEBUG
    let mutable changedKeys : string list = []
    member this.ChangedKeys = changedKeys
#endif
    member this.Get(key, render: unit -> Img) =
        match cached with
        | Some(previous, image) when previous = key -> image, false
        | previous ->
            let image = PerfTrace.time "stripRender" render
#if DEBUG
            changedKeys <- key |> List.choose (fun (name, value) ->
                if previous |> Option.forall (fun (old, _) -> List.tryFind (fst >> (=) name) old <> Some(name, value)) then Some name else None)
            for name, value in key do
                if previous |> Option.forall (fun (old, _) -> List.tryFind (fst >> (=) name) old <> Some(name, value)) then
                    PerfTrace.count ("stripCause." + name)
#endif
            cached <- Some(key, image)
            previous |> Option.iter (fun (_, old) -> old.bitmap.Dispose())
            image, true
    member this.Clear() =
        let old = cached
        cached <- None
        old |> Option.iter (fun (_, image) -> image.bitmap.Dispose())
    interface IDisposable with
        member this.Dispose() = this.Clear()

/// How the strip of an edge other than the top is drawn where it has been
/// placed (TabEdgeLayout.place): inside the window or outside it, and, on a
/// left or right edge, as rows written across or as tabs written upwards.
type EdgeDraw = {
    inside: bool
    across: bool
}

/// The rows of a left or right strip written across as they are drawn now:
/// the view they are shown in along the strip (between the scroll buttons,
/// when there are any), how far they are scrolled, and each row with the
/// column it starts at, where it starts along the strip in the strip
/// window's coordinates, and the sprite that draws it.
type AcrossLayout = {
    rows: TabEdgeLayout.Rows
    viewStart: int
    viewLength: int
    scroll: int
    button: int
    count: int
    stripWidth: int
    stripLength: int
    layoutLength: int
    // In the order they are drawn, the topmost first.
    placed: (Tab * int * int * AcrossTabSprite<Tab>) list
    hiddenBefore: int
    hiddenAfter: int
    } with
    member this.rowOf (tab: Tab) =
        this.placed |> List.tryPick (fun (t, x, y, s) -> if t = tab then Some(x, y, s) else None)

type TabStrip(monitor:ITabStripMonitor) as this =
    let Cell = CellScope(false, true)
    let renderCache = new StripRenderCache()
    let mutable destroyed = false
#if DEBUG
    let mutable contentSource = "cells"
    let mutable lastContentSource = "initial"
    let mutable paintReason = "initial"
#endif
    let mutable contentHold : (Tab option * List2<Tab> * Tab list) option = None
#if DEBUG
    let mutable paintSequence = 0L
#endif
    let mutable heldDesktopShared : Set<IntPtr> = Set.empty
#if DEBUG
    let mutable paintedDimmed : Tab list = []
    let mutable paintedShared : IntPtr list = []
#endif
    let systemAppearanceCell = Cell.create(0)
    let mutable lastPresentation : (Img * Pt * byte) option = None
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
#if DEBUG
    let mutable desktopNoticeTickValue : uint32 option = None
#endif
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
    // Tabs of windows that are on another virtual desktop.
    let dimmedTabsCell = Cell.create(Set2<Tab>())
    let tabAlignmentCell = Cell.create(Map2<Tab, TabAlign>())
    [<VolatileField>]
    let mutable tabAlignmentSnapshot = Map2<Tab, TabAlign>()
    let defaultAlignmentCell = Cell.create(TopRight)
    let alignment = Cell.create(TopRight)
    let buttonModesCell = Cell.create(TabButtonPolicy.activeOnly, TabButtonPolicy.activeOnly)
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
    // ----- Tabs on the other edges (four-edge tabs, TabEdges) -----
    // This strip holds the whole group: every tab, its order, pin and
    // alignment, and here its edge. Its own window draws the top edge's
    // tabs, and its handle stays the group's; each other edge has a window of
    // its own (edgeWindows) that draws that edge's tabs from the same state.
    //
    // Only a tab NOT on the top edge has an entry. While the top edge is the
    // only one the map stays empty, and every path that tests it runs as it
    // did before there were edges.
    let tabEdgeCell = Cell.create(Map2<Tab, TabEdges.Edge>())
    [<VolatileField>]
    let mutable tabEdgeSnapshot = Map2<Tab, TabEdges.Edge>()
    // The edge a tab was on when it left the strip, for a close that is
    // recorded from another thread after the tab has gone.
    [<VolatileField>]
    let mutable departedEdgeSnapshot = Map2<Tab, TabEdges.Edge>()
    let allowedEdgesCell = Cell.create(TabEdges.topOnly)
    let edgeWindows = System.Collections.Generic.Dictionary<TabEdges.Edge, IWindow>()
    let edgeHwndsSnapshot = PublishedSnapshot<IntPtr list>([])
    // Where each other edge's strip goes on screen, and the appearance and
    // scale it is drawn with there (setEdgePlacement).
    let edgePlacementCell = Cell.create(Map.empty<TabEdges.Edge, Rect * TabAppearanceInfo * float>)
    // How each other edge's strip is drawn there; an edge placed without it
    // is drawn outside, written upwards.
    let edgeDrawCell = Cell.create(Map.empty<TabEdges.Edge, EdgeDraw>)
    let textBottomInsideCell = Cell.create(Map.empty<TabEdges.Edge, bool>)
    let edgeDefaultAlignments = Cell.create(Map.empty<TabEdges.Edge, TabAlign>)
    // How far the rows of a strip written across are scrolled, in device
    // pixels along it (clamped where it is used).
    let edgeScrollCell = Cell.create(Map.empty<TabEdges.Edge, int>)
    // The edge whose window the mouse is in: the point in ptCell is that
    // window's.
    let mouseEdgeCell = Cell.create(TabEdges.Edge.Top)
    // Empty allowed edges keep an invisible hit surface during a drag.
    let receivingCell = Cell.create(false)
    let destroyingEvent = Event<unit>()
    let isShrunkCell = Cell.create(false)
    let shrunkEdgesCell = Cell.create(Set.empty<TabEdges.Edge>)
    let dragScrollInterval = 180
    let mutable lastDragScroll = -int64 dragScrollInterval
    let dragScrollTimer = new Timer(Interval = dragScrollInterval)
    let mutable wheelRemainders = Map.empty<TabEdges.Edge, int>
    let mutable dragScrollTarget : (TabEdges.Edge * Pt) option = None
    // Tooltip implementation
    let tooltipForm = new TabTooltipForm()
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

    let hoveredEdgeExport = Cell.export <| fun() ->
        if hoverCell.value.IsSome then Some mouseEdgeCell.value else None

    let insideHideEdgesExport = Cell.export <| fun() ->
        if TabEdges.isTopOnly allowedEdgesCell.value then Set.empty
        else
            TabEdges.priority
            |> List.filter (fun edge ->
                TabEdges.isAllowed allowedEdgesCell.value edge &&
                this.isInsideOn edge && not (this.bandOf edge).isEmpty)
            |> Set.ofList

    let addEvent(evt,handler) =
        eventHandlersCell.map(fun s -> s.add(_os.setSingleWinEvent evt handler))
    do
        dragScrollTimer.Tick.Add(fun _ ->
            match dragScrollTarget with
            | Some(edge, pt) when (Control.MouseButtons &&& MouseButtons.Left) <> MouseButtons.None ->
                let now = System.Diagnostics.Stopwatch.GetTimestamp() * 1000L / System.Diagnostics.Stopwatch.Frequency
                this.dragScrollAcross(edge, pt, now)
            | _ -> this.stopDragScroll())
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
        // Form.TopMost makes Framework SetVisibleCore explicitly focus the form.
        // Raise it through native makeTopMost instead, which uses SWP_NOACTIVATE.
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
            PerfTrace.time "tooltipHideTick" (fun () ->
                this.tryHideTooltipIfCursorLeft()
                this.validateHoverAgainstCursor()))
        tooltipHideTimer.Start()
        
        layeredWindowCell.value <-
            let style = WindowsStyles.WS_POPUP
            let styleExe =
                WindowsExtendedStyles.WS_EX_LAYERED ||| 
                WindowsExtendedStyles.WS_EX_TOOLWINDOW
            Some(_os.createWindow this.wndProc style styleExe)
        hwndRef := layeredWindowCell.value.Value.hwnd
        
        isMouseOverExport.init()
        hoveredEdgeExport.init()
        insideHideEdgesExport.init()

        // Sync pinned tabs snapshot for thread-safe cross-thread reads
        Cell.listen <| fun() ->
            pinnedTabsSnapshot <- pinnedTabsCell.value
        Cell.listen <| fun() ->
            tabAlignmentSnapshot <- tabAlignmentCell.value
        Cell.listen <| fun() ->
            visualOrderSnapshot <- visualOrderCell.value
        Cell.listen <| fun() ->
            tabEdgeSnapshot <- tabEdgeCell.value

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
    member private this.tabIcon (ti: TabInfo) = this.tabIconAt this.scale ti

    member private this.tabIconAt (scale: float) (ti: TabInfo) =
        let isGeneric (icon: Icon) = Object.ReferenceEquals(icon, SystemIcons.Application)
        if isGeneric ti.iconBig then ti.iconSmall
        elif isGeneric ti.iconSmall then ti.iconBig
        elif scale > 1.0 then ti.iconBig
        else ti.iconSmall

    // ----- edges -----

    /// The edge a tab is on; the top edge unless the map says otherwise.
    member this.edgeOf (tab: Tab) =
        match tabEdgeCell.value.tryFind(tab) with
        | Some(edge) -> edge
        | None -> TabEdges.Edge.Top

    /// The same for another thread (a save, a closed tab being recorded),
    /// and for a tab that has just left: the edge it was on. A tab that is
    /// in the strip is answered from where it is now and nothing else - a
    /// record of an earlier departure must not outlive its return.
    member this.edgeOfThreadSafe (tab: Tab) =
        match tabEdgeSnapshot.tryFind(tab) with
        | Some(edge) -> edge
        | None when visualOrderSnapshot.any((=) tab) -> TabEdges.Edge.Top
        | None ->
            match departedEdgeSnapshot.tryFind(tab) with
            | Some(edge) -> edge
            | None -> TabEdges.Edge.Top

    /// Whether any tab is on an edge other than the top. False all the time
    /// while the top edge is the only one.
    member private this.hasOtherEdges = not tabEdgeCell.value.items.isEmpty

    /// One edge's tabs in their order. The top edge with no tab anywhere else
    /// is the whole list, the very value it always was.
    member this.bandOf (edge: TabEdges.Edge) : List2<Tab> =
        if this.hasOtherEdges then visualOrderCell.value.where(fun t -> this.edgeOf t = edge)
        elif edge = TabEdges.Edge.Top then visualOrderCell.value
        else List2()

    member this.edgesInUse =
        TabEdges.priority |> List.filter (fun e -> not (this.bandOf e).isEmpty)

    member this.allowedEdges = allowedEdgesCell.value
    member this.allowedEdgesOutput = allowedEdgesCell :> ICellOutput<_>
    member this.hoveredEdge = hoveredEdgeExport :> ICellOutput<_>
    member this.insideHideEdges = insideHideEdgesExport :> ICellOutput<_>

    /// The edges tabs may use, as the settings file has them
    /// (TabEdgeSettings.allowedOf). Set before the other edges' windows are
    /// made, and again when the settings dialog changes them (then
    /// moveOffUnchecked takes the tabs off the edges no longer allowed).
    member this.setAllowedEdges(allowed: TabEdges.Allowed) =
        if allowed <> allowedEdgesCell.value then allowedEdgesCell.set(allowed)

    /// The edges that show a thin strip to drop on now: only while a tab is
    /// dragged, never while the top edge is the only one.
    member private this.receiverEdges =
        if receivingCell.value then
            TabEdges.receiverEdges allowedEdgesCell.value this.edgesInUse
        else []

    member this.isReceiving
        with get() = receivingCell.value
        and set(value) =
            if receivingCell.value <> value then receivingCell.set(value)

    member private this.setEdgeEntry (tab: Tab) (edge: TabEdges.Edge) =
        let (Tab hwnd) = tab
        Services.program.setWindowEdge(hwnd, edge)
        match edge, tabEdgeCell.value.tryFind(tab) with
        | TabEdges.Edge.Top, Some(_) -> tabEdgeCell.map(fun m -> m.remove tab)
        | TabEdges.Edge.Top, None -> ()
        | _, Some(current) when current = edge -> ()
        | _ -> tabEdgeCell.map(fun m -> m.add tab edge)

    /// Put tabs on an edge. They keep their pin and alignment, and go to the
    /// end of their zone on that edge in the order they are in, behind the
    /// tabs already there - as tabs moved off an unchecked edge do (1-5) and
    /// as a window joining a group does. Sorted by the zones alone they
    /// would land in front of that edge's tabs whenever they came from an
    /// edge before it: a group put back after a restart had its left tabs
    /// reversed. A move that means a position uses moveTabs or dropOnEdge.
    member this.setTabsEdge(tabs: Tab list, edge: TabEdges.Edge) =
        Cell.beginUpdate()
        let moving =
            tabs |> List.filter (fun t -> visualOrderCell.value.any((=) t) && this.edgeOf t <> edge)
        for (Tab hwnd as t) in moving do
            let alignment = tabAlignmentCell.value.tryFind(t) |> Option.defaultValue (this.defaultAlignOn (this.edgeOf t))
            this.setEdgeEntry t edge
            tabAlignmentCell.map(fun m -> m.add t alignment)
            Services.program.setWindowAlignment(hwnd, Some alignment)
            Services.program.setWindowAlignmentEdge(hwnd, edge)
        if not (List.isEmpty moving) then
            let movingSet = Set.ofList moving
            let order = visualOrderCell.value.list
            visualOrderCell.set(List2((order |> List.filter (movingSet.Contains >> not)) @ (order |> List.filter movingSet.Contains)))
        this.normalizeVisualOrder()
        Cell.endUpdate()

    member this.setTabEdge(tab: Tab, edge: TabEdges.Edge) = this.setTabsEdge([ tab ], edge)

    member this.moveTabsToEdgeFromMenu(tabs: Tab list, edge: TabEdges.Edge) =
        if not (TabEdges.isTopOnly allowedEdgesCell.value) && TabEdges.isAllowed allowedEdgesCell.value edge then
            // Preserve the stored alignment even when across-strip pins display at the start.
            let before =
                this.placedTabs |> List.map (fun (p: TabEdges.Placed<Tab>) ->
                    let alignment = tabAlignmentCell.value.tryFind(p.id) |> Option.defaultValue (this.defaultAlignOn p.edge)
                    { p with align = if alignment = TopLeft then TabEdges.AlignStart else TabEdges.AlignEnd })
            let after = TabEdges.moveFromMenu allowedEdgesCell.value edge tabs before
            let order = visualOrderCell.value.list
            let moving = after |> List.filter (fun p -> this.edgeOf p.id <> p.edge) |> List.map (fun p -> p.id) |> Set.ofList
            Cell.beginUpdate()
            try
                for p in after do
                    if this.edgeOf p.id <> p.edge then
                        this.setEdgeEntry p.id p.edge
                        let alignment = if p.align = TabEdges.AlignStart then TopLeft else TopRight
                        tabAlignmentCell.map(fun m -> m.add p.id alignment)
                        let (Tab hwnd) = p.id
                        Services.program.setWindowAlignment(hwnd, Some alignment)
                        Services.program.setWindowAlignmentEdge(hwnd, p.edge)
                // Preserve displayed order when stored alignment zones share one pin row.
                visualOrderCell.set(List2((order |> List.filter (moving.Contains >> not)) @ (order |> List.filter moving.Contains)))
                this.normalizeVisualOrder()
            finally Cell.endUpdate()

    /// Edges unchecked while the strip holds tabs (the settings dialog):
    /// the tabs of each edge no longer allowed go to the first allowed edge,
    /// behind the tabs there, preserving their order, pin and alignment,
    /// one edge after the other in priority order.
    /// Nothing moves while every
    /// edge in use is allowed.
    member this.moveOffUnchecked(allowed: TabEdges.Allowed) =
        Cell.beginUpdate()
        for edge in TabEdges.priority do
            if not (TabEdges.isAllowed allowed edge) then
                let tabs = (this.bandOf edge).list
                if not (List.isEmpty tabs) then
                    this.setTabsEdge(tabs, TabEdges.fallback allowed edge)
        Cell.endUpdate()

    // The window of an edge; the top edge's is this strip's own.
    member private this.windowOf (edge: TabEdges.Edge) : Window option =
        if edge = TabEdges.Edge.Top then Some(this.window)
        else
            match edgeWindows.TryGetValue(edge) with
            | true, w -> Some(_os.windowFromHwnd(w.hwnd))
            | _ -> None

    /// Where the rename box goes for a tab of a strip other than the top, on
    /// the screen: over the tab's own text for a bottom tab or a row written
    /// across, and beside a tab written upwards, on the window's side. None
    /// for a tab of the top strip, whose box is found as it always was.
    member this.sideRenameRect (tab: Tab) : Rect option =
        let edge = this.edgeOf tab
        if edge = TabEdges.Edge.Top then None
        else
            match edgePlacementCell.value.TryFind(edge) with
            | Some((bounds, appearance, scale)) ->
                let origin = bounds.location
                if this.isAcross edge then
                    this.acrossOf edge |> Option.bind (fun layout ->
                        layout.rowOf tab |> Option.map (fun (x, along, sprite) ->
                            Rect(Pt(origin.x + x + sprite.textLocation.x, origin.y + along),
                                 Sz(max (Dpi.px scale 60) sprite.textSize.width, layout.rows.length))))
                elif TabEdges.runsHorizontally edge then
                    let ts = this.tsOn edge
                    ts.sprite.children.tryPick (fun (at, sprite) ->
                        match sprite with
                        | :? TabSprite<Tab> as s when s.id = tab ->
                            Some(Rect(Pt(origin.x + at.x + s.textLocation.x, origin.y + at.y + s.textLocation.y), s.textSize))
                        | _ -> None)
                else
                    let ts = this.tsOn edge
                    let along = (ts.tabLocation tab).x
                    let length = int (ts.lengthOf tab)
                    let width = max (Dpi.px scale 160) length
                    let x =
                        if edge = TabEdges.Edge.Left then origin.x + bounds.size.width
                        else origin.x - width
                    Some(Rect(Pt(x, origin.y + along),
                              Sz(width, max 1 (appearance.tabHeight - Dpi.px scale 4))))
            | None -> None

    /// The handle of an edge's window: this strip's own for the top edge.
    member this.hwndOf (edge: TabEdges.Edge) =
        match this.windowOf edge with
        | Some(w) -> w.hwnd
        | None -> this.hwnd

    /// The windows of the other edges, made so far.
    member this.edgeHwnds = edgeWindows.Values |> Seq.map (fun w -> w.hwnd) |> List.ofSeq
    member this.edgeHwndsThreadSafe = edgeHwndsSnapshot.value

    member this.edgeOfHwnd (hwnd: IntPtr) =
        if hwnd = this.hwnd then Some(TabEdges.Edge.Top)
        else edgeWindows |> Seq.tryFind (fun kv -> kv.Value.hwnd = hwnd) |> Option.map (fun kv -> kv.Key)

    /// Whether `hwnd` is one of this group's strips, of any edge.
    member this.ownsHwnd (hwnd: IntPtr) = (this.edgeOfHwnd hwnd).IsSome

    /// Make the window of an edge other than the top (once). Nothing is made
    /// for an edge while the top edge is the only one, so the group has the
    /// one window it always had.
    member this.addEdge(edge: TabEdges.Edge) : IntPtr =
        if edge = TabEdges.Edge.Top then this.hwnd
        else
            match edgeWindows.TryGetValue(edge) with
            | true, w -> w.hwnd
            | _ ->
                let style = WindowsStyles.WS_POPUP
                let styleExe = WindowsExtendedStyles.WS_EX_LAYERED ||| WindowsExtendedStyles.WS_EX_TOOLWINDOW
                let w = _os.createWindow (this.edgeWndProc edge) style styleExe
                edgeWindows.[edge] <- w
                edgeHwndsSnapshot.publish(this.edgeHwnds)
                w.hwnd

    /// Where an edge's strip goes and what it is drawn with there: `bounds`
    /// on screen, the edge's appearance at `scale`.
    member this.setEdgePlacement(edge: TabEdges.Edge, bounds: Rect, appearance: TabAppearanceInfo, scale: float) =
        let value = (bounds, appearance, scale)
        let same =
            match edgePlacementCell.value.TryFind(edge) with
            | Some((b, a, s)) -> b.record = bounds.record && a = appearance && s = scale
            | None -> false
        if not same then edgePlacementCell.set(edgePlacementCell.value.Add(edge, value))

    /// The same, with how the strip is drawn there (TabEdgeLayout.place):
    /// inside or outside, and for a left or right strip whether its rows are
    /// written across.
    member this.setEdgePlacement(edge: TabEdges.Edge, bounds: Rect, appearance: TabAppearanceInfo, scale: float, draw: EdgeDraw) =
        Cell.beginUpdate()
        let wasAcross = this.isAcross edge
        if edgeDrawCell.value.TryFind(edge) <> Some(draw) then edgeDrawCell.set(edgeDrawCell.value.Add(edge, draw))
        if wasAcross <> this.isAcross edge then this.normalizeVisualOrder()
        this.setEdgePlacement(edge, bounds, appearance, scale)
        Cell.endUpdate()

    member this.setTextBottomInside(edge: TabEdges.Edge, inside: bool) =
        if textBottomInsideCell.value.TryFind(edge) <> Some inside then
            textBottomInsideCell.set(textBottomInsideCell.value.Add(edge, inside))

    member private this.sideTextClockwise edge =
        let inside = textBottomInsideCell.value.TryFind(edge) |> Option.defaultValue true
        TabEdges.sideTextClockwise inside edge

    member private this.edgeDraw (edge: TabEdges.Edge) =
        match edgeDrawCell.value.TryFind(edge) with
        | Some(draw) -> draw
        | None -> { inside = false; across = false }

    member this.isInsideOn edge =
        if edge = TabEdges.Edge.Top then this.showInside else (this.edgeDraw edge).inside

    /// A left or right strip written across (rows of text, 6-1).
    member this.isAcross (edge: TabEdges.Edge) =
        not (TabEdges.runsHorizontally edge) && (this.edgeDraw edge).across

    /// Whether any strip with tabs is inside the window: the top strip's
    /// direction alone while the top edge is the only one. The strips inside
    /// are the ones "hide inside tabs" shrinks (5-3).
    member this.anyInside =
        this.showInside
        || (edgeWindows.Count > 0
            && edgeWindows.Keys |> Seq.exists (fun edge -> (this.edgeDraw edge).inside && not (this.bandOf edge).isEmpty))

    // The way an edge's tabs point: away from the window outside it, into
    // it inside (a top strip's TabUp and TabDown). Left and right strips are
    // laid out as top strips. Their text rotation determines the unrotated
    // direction; their physical baseline always faces the window.
    member private this.directionOn (edge: TabEdges.Edge) =
        let inside = (this.edgeDraw edge).inside
        match edge with
        | TabEdges.Edge.Top -> this.direction
        | TabEdges.Edge.Left | TabEdges.Edge.Right ->
            let baselineInside = (this.sideTextClockwise edge) = (edge = TabEdges.Edge.Right)
            if inside = baselineInside then TabDown else TabUp
        | TabEdges.Edge.Bottom -> if inside then TabUp else TabDown

    // A left or right strip lays its tabs out along its height: the sprite
    // is built as a top strip as long as the column is high and as thick as
    // it is wide, and each tab is drawn turned (see renderSide).
    member private this.logicalSizeOf (edge: TabEdges.Edge) =
        if edge = TabEdges.Edge.Top then this.size
        else
            match edgePlacementCell.value.TryFind(edge) with
            | Some((bounds, _, _)) ->
                if TabEdges.runsHorizontally edge then bounds.size
                else Sz(bounds.size.height, bounds.size.width)
            | None -> Sz.empty

    /// A point in an edge window, as that strip lays its tabs out: along the
    /// strip, then across it. The same point for the top strip.
    member private this.logicalPt (edge: TabEdges.Edge) (pt: Pt) =
        if TabEdges.runsHorizontally edge then pt else Pt(pt.y, pt.x)

    /// A point of the layout of a tab's strip, on the screen.
    member this.screenPointOf (tab: Tab, pt: Pt) =
        let edge = this.edgeOf tab
        match this.windowOf edge with
        | Some(w) -> w.ptToScreen(this.logicalPt edge pt)
        | None -> this.window.ptToScreen(pt)

    member private this.tsBaseOnWithShared (edge: TabEdges.Edge) direction shared : TabStripSprite<Tab> =
        let isTop = edge = TabEdges.Edge.Top
        let others = this.hasOtherEdges
        let appearance, scale =
            if isTop then this.appearance, this.scale
            else
                match edgePlacementCell.value.TryFind(edge) with
                | Some((_, a, s)) -> a, s
                | None -> this.appearance, this.scale
        let onEdge (tabs: List2<Tab>) = if others then tabs.where(fun t -> this.edgeOf t = edge) else tabs
        // One font instance for the whole sprite tree (pixel-sized, see Dpi).
        let textFont = Dpi.scaledFont SystemFonts.MenuFont scale
        {
            tabs = Map2(this.tabs.items.map <| fun tab ->
                let ti = this.tabInfo(tab)
                let tabInfo = {
                    bgColor = tabBgColor.value.tryFind(tab)
                    fillColor = tabFillColor.value.tryFind(tab)
                    underlineColor = tabUnderlineColor.value.tryFind(tab)
                    borderColor = tabBorderColor.value.tryFind(tab)
                    TabDisplayInfo.text = ti.text
                    icon = this.tabIconAt scale ti
                    textFont = textFont
                    textBrush = SystemBrushes.MenuText
                }
                tab,tabInfo
            )
            hover = hoverCell.value
            captured = capturedCell.value
            visualOrder = this.bandOf edge
            zorder = onEdge zorderCell.value
            activeTabOverride = if others then Some zorderCell.value.tryHead else None
            size = this.logicalSizeOf edge
            slide =
                match this.slide with
                | Some(tab, _) when others && this.edgeOf tab <> edge -> None
                | slide -> slide
            forcedSlideAlignment = None
            direction = direction
            tabAlignments =
                if isTop then tabAlignmentCell.value
                else
                    (this.bandOf edge).list
                    |> List.fold (fun (values: Map2<Tab, TabAlign>) tab ->
                        values.add tab (this.getTabAlign tab)) tabAlignmentCell.value
            pinnedTabs = pinnedTabsCell.value
            selectedTabs = selectedTabsCell.value
            dimmedTabs = this.dimmedTabsFor shared
            transparent = this.transparent
            appearance = appearance
            buttonModes = buttonModesCell.value
            dragGroup =
                if others then dragGroupCell.value |> List.filter (fun t -> this.edgeOf t = edge)
                else dragGroupCell.value
            scale = scale
        }
    member private this.tsBaseWithShared direction shared = this.tsBaseOnWithShared TabEdges.Edge.Top direction shared
    member private this.tsBaseOn edge direction = this.tsBaseOnWithShared edge direction this.desktopShared
    member private this.tsBase direction = this.tsBaseWithShared direction this.desktopShared
    member private this.ts = this.tsBase this.direction

    // The sprite of an edge's strip, laid out along the strip. Left and
    // right strips are laid out as top strips and drawn turned; a strip
    // written across is not a sprite of this kind (acrossOf).
    member private this.tsOn (edge: TabEdges.Edge) : TabStripSprite<Tab> =
        if edge = TabEdges.Edge.Top then this.ts else this.tsBaseOn edge (this.directionOn edge)

    // Drawing and hit testing share tabSpriteRec's group-active decision.
    // Keep each band's drawing order; top-only rendering uses the original sprite.
    member private this.bandSprites (ts: TabStripSprite<Tab>) : (Pt * TabSprite<Tab>) list =
        let order =
            if List.isEmpty ts.dragGroup then ts.zorder.list
            else
                (ts.zorder.list |> List.filter (fun t -> not (List.contains t ts.dragGroup)))
                @ (ts.visualOrder.list |> List.filter (fun t -> List.contains t ts.dragGroup))
        order |> List.map (fun t ->
            let sprite = ts.tabSpriteRec(t)
            ts.tabLocation t, sprite)

    // One strip's sprite drawn with bandSprites.
    member private this.renderBand (ts: TabStripSprite<Tab>) : Img =
        let img = Img(if ts.size.isEmptyArea then Sz(1, 1) else ts.size)
        let g = img.graphics
        let bg = if ts.transparent then Color.FromArgb(0, 0, 0, 0) else Color.FromArgb(1, 1, 1, 1)
        use brush = new SolidBrush(bg)
        g.FillRectangle(brush, Rect(Pt(), img.size).Rectangle)
        for (at, sprite) in List.rev (this.bandSprites ts) do
            g.DrawImageUnscaled((sprite :> ISprite).render.bitmap, at.Point)
        img

    // ----- a left or right strip written across -----

    // What a row shows of its tab: the top strip's display values, in the
    // strip's own font.
    member private this.acrossInfo (scale: float) (tab: Tab) (textFont: Font) : TabDisplayInfo =
        let ti = this.tabInfo(tab)
        { bgColor = tabBgColor.value.tryFind(tab)
          fillColor = tabFillColor.value.tryFind(tab)
          underlineColor = tabUnderlineColor.value.tryFind(tab)
          borderColor = tabBorderColor.value.tryFind(tab)
          TabDisplayInfo.text = ti.text
          icon = this.tabIconAt scale ti
          textFont = textFont
          textBrush = SystemBrushes.MenuText }

    /// The rows of a strip written across as they are drawn now: laid out
    /// along it as a top strip lays out tabs of one length
    /// (TabEdges.tabStartsAt), scrolled when they do not fit
    /// (TabEdgeLayout), and while a tab is being slid along it, the rest
    /// where the drop would leave them (TabEdges.dropPositionAt).
    member this.acrossOf (edge: TabEdges.Edge) : AcrossLayout option =
        match edgePlacementCell.value.TryFind(edge) with
        | Some((bounds, a, scale)) when this.isAcross edge ->
            let px = Dpi.px scale
            let textFont = Dpi.scaledFont SystemFonts.MenuFont scale
            let rows = TabEdgeLayout.rowsOf px a.tabHeight a.tabOverlap textFont.Height
            let band = (this.bandOf edge).list
            let count = band.Length
            let button = px 14
            let stripLength = bounds.size.height
            let viewStart, viewLength = TabEdgeLayout.viewport rows count stripLength button
            let stored = defaultArg (edgeScrollCell.value.TryFind(edge)) 0
            let scroll = TabEdgeLayout.clampScroll rows count viewLength stored
            let layoutLength = TabEdgeLayout.layoutLength rows count viewLength
            let lengths : TabEdges.StripLengths =
                { stripLength = float layoutLength; tabOverlap = float rows.overlap
                  pinnedLength = float rows.length; unpinnedLength = float rows.length }
            let pinned t = pinnedTabsCell.value.contains(t)
            let stripTab t : TabEdges.StripTab<Tab> =
                { tab = t; tabAlign = (if this.getTabAlign(t) = TopLeft then TabEdges.AlignStart else TabEdges.AlignEnd); tabPinned = pinned t }
            let strip = band |> List.map stripTab
            let dragGroup = dragGroupCell.value |> List.filter (fun t -> List.contains t band)
            let starts =
                match this.slide with
                | Some(pivot, at) when List.contains pivot band ->
                    let moving = if List.isEmpty dragGroup then [ pivot ] else dragGroup
                    let content = at - viewStart + scroll
                    let index, align = TabEdges.dropPositionAt lengths strip dragGroup pivot content
                    let without = strip |> List.filter (fun s -> not (List.contains s.tab moving))
                    let index = max 0 (min without.Length index)
                    let arriving = moving |> List.map (fun t -> { stripTab t with tabAlign = align })
                    let resting =
                        TabEdges.tabStartsAt lengths (List.truncate index without @ arriving @ List.skip index without)
                        |> List.filter (fun (t, _) -> not (List.contains t moving))
                    let pitch = rows.length - rows.overlap
                    let pivotIndex = moving |> List.findIndex ((=) pivot)
                    let pivotStart = max 0 (min (layoutLength - rows.length) content)
                    resting @ (moving |> List.mapi (fun i t -> t, pivotStart + (i - pivotIndex) * pitch))
                | _ -> TabEdges.tabStartsAt lengths strip
            let standsOnRight = (edge = TabEdges.Edge.Left) <> (this.edgeDraw edge).inside
            let column = max 1 (bounds.size.width - 1)
            let iconOnly = TabEdgeLayout.iconOnlyRow px rows
            let widthOf t =
                if pinned t then
                    if a.tabPinnedTabWidthIcon then min column iconOnly
                    else min column (max iconOnly a.tabPinnedTabWidth)
                else column
            let xOf t = if standsOnRight then column - widthOf t else 1
            let top = zorderCell.value.tryHead
            let order = TabEdgeLayout.rowOrder top band
            let startOf t = starts |> List.tryPick (fun (u, s) -> if u = t then Some(s) else None)
            let placed =
                order |> List.choose (fun t ->
                    startOf t |> Option.map (fun start ->
                        let sprite : AcrossTabSprite<Tab> =
                            { id = t
                              isTop = (top = Some(t))
                              isSelected = selectedTabsCell.value.contains(t)
                              appearance = a
                              buttonModes = buttonModesCell.value
                              displayInfo = this.acrossInfo scale t textFont
                              size = Sz(widthOf t, rows.length)
                              onlyIcon = pinned t && a.tabPinnedTabWidthIcon
                              isPinned = pinned t
                              standsOnRight = standsOnRight
                              band = rows.band
                              iconSide = rows.icon
                              hover = (match hoverCell.value with Some(h, part) when h = t -> Some(part) | _ -> None)
                              captured = (match capturedCell.value with Some(h, part) when h = t -> Some(part) | _ -> None)
                              scale = scale }
                        t, xOf t, viewStart + start - scroll, sprite))
            let before, after = TabEdgeLayout.hiddenRows rows count viewLength scroll
            Some({ rows = rows; viewStart = viewStart; viewLength = viewLength; scroll = scroll; button = button
                   count = count; stripWidth = bounds.size.width; stripLength = stripLength; layoutLength = layoutLength
                   placed = placed; hiddenBefore = before; hiddenAfter = after })
        | _ -> None

    // The row under a point of a strip written across (the strip window's
    // coordinates): rows only inside the view, and only across their own
    // width.
    member private this.acrossHit (layout: AcrossLayout) (pt: Pt) : (Tab * int * int * AcrossTabSprite<Tab>) option =
        if pt.y < layout.viewStart || pt.y >= layout.viewStart + layout.viewLength then None
        else
            let starts = layout.placed |> List.map (fun (t, _, y, _) -> t, y)
            TabEdgeLayout.rowAt layout.rows starts pt.y
            |> Option.bind (fun tab ->
                layout.placed |> List.tryFind (fun (t, x, _, s) -> t = tab && pt.x >= x && pt.x < x + s.size.width))

    /// A scroll button under a point of a strip written across: Some(true)
    /// for the one towards the first row.
    member private this.acrossButtonAt (edge: TabEdges.Edge) (pt: Pt) : bool option =
        match this.acrossOf edge with
        | Some(layout) when layout.viewStart > 0 && pt.x >= 0 && pt.x < layout.stripWidth ->
            if pt.y >= 0 && pt.y < layout.viewStart then Some(true)
            elif pt.y >= layout.viewStart + layout.viewLength && pt.y < layout.stripLength then Some(false)
            else None
        | _ -> None

    member private this.setScroll (edge: TabEdges.Edge) (scroll: int) =
        if edgeScrollCell.value.TryFind(edge) <> Some(scroll) then
            edgeScrollCell.set(edgeScrollCell.value.Add(edge, scroll))

    /// Scroll a strip written across by a turn of the wheel (WM_MOUSEWHEEL's
    /// delta) or by a page (a scroll button). Nothing on any other strip.
    member this.scrollAcross(edge: TabEdges.Edge, wheelDelta: int option, pageTowardsFirst: bool option) =
        match this.acrossOf edge with
        | Some(layout) ->
            let scroll =
                match wheelDelta, pageTowardsFirst with
                | Some(delta), _ ->
                    let remainder = defaultArg (wheelRemainders.TryFind edge) 0
                    let next, remaining = TabEdgeLayout.accumulateWheel layout.rows layout.count layout.viewLength layout.scroll remainder delta
                    wheelRemainders <- wheelRemainders.Add(edge, remaining)
                    next
                | None, Some(towardsFirst) -> TabEdgeLayout.pageScroll layout.rows layout.count layout.viewLength layout.scroll (not towardsFirst)
                | None, None -> layout.scroll
            this.setScroll edge scroll
        | None -> ()

    // Throttle drag scrolling independently of mouse-event frequency.
    member this.dragScrollAcross(edge, pt: Pt, elapsedMilliseconds: int64) =
        dragScrollTarget <- Some(edge, pt)
        match this.acrossButtonAt edge pt with
        | Some(first) when elapsedMilliseconds - lastDragScroll >= int64 dragScrollInterval ->
            dragScrollTimer.Start()
            lastDragScroll <- elapsedMilliseconds
            match this.acrossOf edge with
            | Some(layout) -> this.setScroll edge (TabEdgeLayout.rowScroll layout.rows layout.count layout.viewLength layout.scroll (not first))
            | None -> ()
        | None -> this.stopDragScroll(); lastDragScroll <- elapsedMilliseconds - int64 dragScrollInterval
        | _ -> ()

    member this.stopDragScroll() =
        dragScrollTimer.Stop()
        dragScrollTarget <- None

    // The active tab of a strip written across is scrolled into sight.
    member private this.scrollToShow (tab: Tab) =
        let edge = this.edgeOf tab
        if this.isAcross edge then
            match this.acrossOf edge with
            | Some(layout) ->
                match layout.rowOf tab with
                | Some(_, y, _) ->
                    let start = y - layout.viewStart + layout.scroll
                    this.setScroll edge (TabEdgeLayout.scrollToShow layout.rows layout.count layout.viewLength layout.scroll start)
                | None -> ()
            | None -> ()

    /// How far the dragged tab's point is from the start of its row along a
    /// strip written across: the offset it was grabbed at when that is inside
    /// a row, the row's middle when it came from a longer tab. Every other
    /// strip slides the tab by the offset it was grabbed at, as always.
    member this.alongOffset(tab: Tab, offset: int) =
        let edge = this.edgeOf tab
        if this.isAcross edge then
            match this.acrossOf edge with
            | Some(layout) when offset < 0 || offset >= layout.rows.length -> layout.rows.length / 2
            | _ -> offset
        else offset

    member this.slideFromPointer(tab: Tab, point: Pt, offset: Pt) =
        tab, point.x - this.alongOffset(tab, offset.x)

    // Visible geometry of an additional strip: crop and screen position.
    member this.edgePresentation(edge: TabEdges.Edge, bounds: Rect) =
        let w, h = bounds.size.width, bounds.size.height
        let whole = Rect(Pt(), bounds.size)
        if not (this.isShrunkOn edge) || not (this.edgeDraw edge).inside then whole, bounds.location
        else
            let sliver = Dpi.px (this.scaleOn edge) 7
            match edge with
            | TabEdges.Edge.Left ->
                let s = min sliver w
                Rect(Pt(w-s,0), Sz(s,h)), bounds.location
            | TabEdges.Edge.Right ->
                let s = min sliver w
                Rect(Pt(), Sz(s,h)), Pt(bounds.right-s,bounds.top)
            | _ ->
                let s = min sliver h
                Rect(Pt(), Sz(w,s)), Pt(bounds.left,bounds.bottom-s)

    member private this.presentedPoint edge (pt: Pt) =
        match edgePlacementCell.value.TryFind edge with
        | Some(bounds,_,_) when this.isShrunkOn edge && (this.edgeDraw edge).inside ->
            let crop,_ = this.edgePresentation(edge,bounds)
            pt.add(crop.location)
        | _ -> pt

    // A strip written across, drawn: the rows in the view, and the scroll
    // buttons at its ends when they do not all fit.
    member private this.renderAcross (edge: TabEdges.Edge) : Img =
        match this.acrossOf edge with
        | Some(layout) ->
            let img = Img(Sz(max 1 layout.stripWidth, max 1 layout.stripLength))
            let g = img.graphics
            let bg = if this.transparent then Color.FromArgb(0, 0, 0, 0) else Color.FromArgb(1, 1, 1, 1)
            use brush = new SolidBrush(bg)
            g.FillRectangle(brush, Rect(Pt(), img.size).Rectangle)
            let state = g.Save()
            g.SetClip(Rectangle(0, layout.viewStart, layout.stripWidth, layout.viewLength))
            let dimmed: Set2<Tab> = this.effectiveDimmedTabs
            for (tab, x, y, sprite) in List.rev layout.placed do
                let row = sprite.render
                try
                    // Fade the complete row, including its icon and buttons,
                    // without modifying any shared icon surface.
                    if dimmed.contains(tab) then
                        let bitmap = row.bitmap
                        let data = bitmap.LockBits(Rectangle(0, 0, bitmap.Width, bitmap.Height),
                                                   ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb)
                        try
                            let bytes : byte[] = Array.zeroCreate (abs data.Stride * data.Height)
                            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length)
                            for i in 3 .. 4 .. bytes.Length - 1 do
                                let alpha = int bytes.[i]
                                if alpha > 0 then bytes.[i] <- byte (max 1 (alpha * VirtualDesktopGroups.dimmedAlpha / 255))
                            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, data.Scan0, bytes.Length)
                        finally bitmap.UnlockBits(data)
                    g.DrawImageUnscaled(row.bitmap, Point(x, y))
                finally row.bitmap.Dispose()
            g.Restore(state)
            if layout.viewStart > 0 then
                let a = match edgePlacementCell.value.TryFind(edge) with Some((_, a, _)) -> a | None -> this.appearance
                let scale = match edgePlacementCell.value.TryFind(edge) with Some((_, _, s)) -> s | None -> this.scale
                let column = max 1 (layout.stripWidth - 1)
                let x = if (edge = TabEdges.Edge.Left) <> (this.edgeDraw edge).inside then 0 else 1
                let drawButton (y: int) (towardsFirst: bool) (hidden: int) =
                    let rect = Rectangle(x, y, column - 1, max 1 (layout.button - 1))
                    use fill = new SolidBrush(a.tabInactiveTabColor)
                    g.FillRectangle(fill, rect)
                    use pen = new Pen(a.tabInactiveBorderColor, 1.0f)
                    g.DrawRectangle(pen, rect)
                    let cx = float32 rect.X + float32 rect.Width / 2.0f
                    let cy = float32 rect.Y + float32 rect.Height / 2.0f
                    let half = float32 (Dpi.px scale 4)
                    let rise = if towardsFirst then -half / 2.0f else half / 2.0f
                    let arrow = [| PointF(cx - half, cy - rise); PointF(cx + half, cy - rise); PointF(cx, cy + rise) |]
                    let c = a.tabInactiveTextColor
                    use arrowBrush = new SolidBrush(if hidden > 0 then c else Color.FromArgb(0x60, int c.R, int c.G, int c.B))
                    g.SmoothingMode <- Drawing2D.SmoothingMode.AntiAlias
                    g.FillPolygon(arrowBrush, arrow)
                    g.SmoothingMode <- Drawing2D.SmoothingMode.Default
                drawButton 0 true layout.hiddenBefore
                drawButton (layout.viewStart + layout.viewLength) false layout.hiddenAfter
            img
        | None -> Img(Sz(1, 1))

    member private this.onMouse(down, pt:Pt, btn, (tab:Tab, part)) =
        monitor.tabClick(btn, tab, part, down, pt)

    // What is under a point of an edge's window. For a turned strip the tab
    // is found along the strip, and the point is turned back into that tab's
    // own coordinates to find the part (the close and pin buttons).
    member private this.hitOn (edge: TabEdges.Edge) (pt: Pt) : Option<Tab*TabPart> =
        let pt = this.presentedPoint edge pt
        if TabEdges.runsHorizontally edge then (this.tsOn edge).tryHit(pt)
        elif this.isAcross edge then
            this.acrossOf edge
            |> Option.bind (fun layout -> this.acrossHit layout pt)
            |> Option.map (fun (tab, x, y, sprite) -> tab, sprite.partAt(Pt(pt.x - x, pt.y - y)))
        else
            let ts = this.tsOn edge
            let clockwise = this.sideTextClockwise edge
            let x, y = TabEdges.sideLayoutPoint clockwise ts.size.height (pt.x, pt.y)
            let logical = Pt(x, y)
            ts.tryHitForTooltip(logical) |> Option.map (fun tab ->
                let loc = ts.tabLocation tab
                let length = int (ts.lengthOf tab)
                let along = logical.x - loc.x
                let local = Pt((if clockwise then along else length - 1 - along), logical.y - loc.y)
                tab, ts.partOf tab local)

    /// What is under a point of an edge's window, in that window's own
    /// coordinates (a file dropped on a left strip's tab).
    member this.tryHitOn(edge: TabEdges.Edge, pt: Pt) = this.hitOn edge pt

    member private this.tooltipHitOn (edge: TabEdges.Edge) (pt: Pt) : Option<Tab> =
        let pt = this.presentedPoint edge pt
        if this.isAcross edge then
            this.acrossOf edge |> Option.bind (fun layout -> this.acrossHit layout pt) |> Option.map (fun (tab, _, _, _) -> tab)
        else
            let ts = this.tsOn edge
            let logical =
                if TabEdges.runsHorizontally edge then pt
                else
                    let clockwise = this.sideTextClockwise edge
                    let x, y = TabEdges.sideLayoutPoint clockwise ts.size.height (pt.x, pt.y)
                    Pt(x, y)
            ts.tryHitForTooltip(logical)

    member private this.hit : Option<Tab*TabPart> = maybe {
        let! pt = ptCell.value
        let! hit = this.hitOn mouseEdgeCell.value pt
        return hit
        }

    // The screen rectangles of the strips being shown, the top one first.
    // Only the top strip's while the top edge is the only one.
    member private this.shownStripRects : (TabEdges.Edge * Pt * Sz) list =
        let top = [ TabEdges.Edge.Top, this.location, this.size ]
        if edgeWindows.Count = 0 then top
        else
            let others =
                edgeWindows.Keys
                |> Seq.choose (fun edge ->
                    match edgePlacementCell.value.TryFind(edge) with
                    | Some((bounds, _, _)) when not (this.bandOf edge).isEmpty ->
                        let crop, at = this.edgePresentation(edge, bounds)
                        Some(edge, at, crop.size)
                    | _ -> None)
                |> List.ofSeq
            (if this.topShows then top else []) @ others

    member private this.cursorOutsideStrips (c: System.Drawing.Point) =
        this.shownStripRects |> List.forall (fun (_, loc, sz) ->
            let clientX = c.X - loc.x
            let clientY = c.Y - loc.y
            clientX < 0 || clientX >= sz.width || clientY < 0 || clientY >= sz.height)

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
                let outsideStrip = this.cursorOutsideStrips c
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
                let outsideStrip = this.cursorOutsideStrips c
                if outsideStrip then
                    hoverCell.set(None)
            with _ -> ()

    // Tooltip hit test: boundary at midpoint of tab overlap area, ignoring z-order
    member private this.hitForTooltipTab : Option<Tab> = maybe {
        let! pt = ptCell.value
        let! tab = this.tooltipHitOn mouseEdgeCell.value pt
        return tab
        }

    member this.scaleOn edge =
        match edgePlacementCell.value.TryFind(edge) with
        | Some(_, _, scale) when edge <> TabEdges.Edge.Top -> scale
        | _ -> this.scale

    member private this.updateTooltipForTab(tab: Tab) =
        let tabInfo = this.tabInfo(tab)
        if tabInfo.text <> "" then
            // Everything below is device pixels for the strip's OWN monitor.
            // The font is a pixel-sized copy of the menu font, so MeasureString
            // returns device pixels whatever DPI this control's DC reports.
            let scale = this.scaleOn (this.edgeOf tab)
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
            let edge = this.edgeOf tab
            let otherStrip =
                if edge = TabEdges.Edge.Top then None
                else edgePlacementCell.value.TryFind(edge) |> Option.map (fun (bounds, _, _) -> bounds)
            let tabScreenX, tabScreenY =
                match otherStrip with
                // A bottom strip: above the tab, on the window's side.
                | Some(bounds) when TabEdges.runsHorizontally edge -> bounds.location.x + tabLoc.x, bounds.location.y
                // A left or right strip: beside the tab's top end, on the
                // window's side of the strip - where "below the strip" is
                // for a top strip.
                | Some(bounds) -> bounds.location.x, bounds.location.y + tabLoc.x
                | None ->
                    let stripLoc : Pt = this.location
                    stripLoc.x + tabLoc.x, stripLoc.y
            match otherStrip with
            | Some(bounds) when TabEdges.runsHorizontally edge ->
                tooltipForm.Location <- new Point(tabScreenX, tabScreenY - tooltipForm.Height - scaled 2)
            | Some(bounds) when edge = TabEdges.Edge.Right ->
                tooltipForm.Location <- new Point(tabScreenX - tooltipForm.Width - scaled 2, tabScreenY)
            | Some(bounds) ->
                tooltipForm.Location <- new Point(tabScreenX + bounds.size.width + scaled 2, tabScreenY)
            | None ->
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
                    tooltipForm.Location <- new Point(tooltipForm.Location.X, tabScreenY - tooltipForm.Height - scaled 5)
                if edge <> TabEdges.Edge.Top then
                    let x, y, w, h = TabEdgeLayout.fitPopup
                                        (work.location.x, work.location.y, work.size.width, work.size.height)
                                        (tooltipForm.Left, tooltipForm.Top, tooltipForm.Width, tooltipForm.Height)
                    tooltipForm.Bounds <- Rectangle(x, y, w, h))

            // BringToFront activates top-level forms; preserve the application's focus.
            _os.windowFromHwnd(tooltipForm.Handle).makeTopMost()
        else
            tooltipForm.Visible <- false

    member private this.processMouse(mouse) = this.processMouseOn TabEdges.Edge.Top mouse

    // The mouse in the window of `edge`. Points arrive in that window's
    // coordinates; the monitor is told them as the strip lays its tabs out
    // (logicalPt), which is the same point for the top strip.
    member private this.processMouseOn (edge: TabEdges.Edge) mouse =
        let window = defaultArg (this.windowOf edge) this.window
        let isTop = edge = TabEdges.Edge.Top
        // A leave from one strip that arrives after the mouse has gone on to
        // another must not undo the other's hover.
        let staleLeave =
            match mouse with
            | MouseLeave -> not isTop && mouseEdgeCell.value <> edge
            | _ -> false
        if not staleLeave then
            match mouse with
            | MouseLeave -> ()
            | _ -> if mouseEdgeCell.value <> edge then mouseEdgeCell.set(edge)
        match mouse with
        | _ when staleLeave -> ()
        | MouseMove(pt) ->
            this.setPt(Some(pt))
            if window.hasCapture.not then
                window.trackMouseLeave()
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
                    if dimmedTabsCell.value.contains(hitTab) then
                        VirtualDesktopTrace.noticeSwitch()
                        VirtualDesktopTrace.handover (fun () ->
                            let (Tab target) = hitTab
                            sprintf "dimmed-click strip=%X target=%X button=%A part=%A"
                                (this.hwnd.ToInt64()) (target.ToInt64()) btn hitPart)
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
                this.onMouse(action, this.logicalPt edge pt, btn, (hitTab, hitPart))
            hoverCell.set(this.hit)
        | MouseLeave ->
            // WM_MOUSELEAVE can fire while the cursor is still on a tab: a
            // tooltip/overlay pops up over a stationary cursor, or the layered
            // window repaints at a sub-pixel edge. Clearing the hover then makes
            // the highlight and Close/Pin button vanish until the next move,
            // which reads as "hovering but no hover state". Re-check the REAL
            // cursor position; only clear when it has genuinely left the strip.
            let c = System.Windows.Forms.Cursor.Position
            let loc =
                if isTop then this.location
                else
                    match edgePlacementCell.value.TryFind(edge) with
                    | Some((bounds, _, _)) -> snd (this.edgePresentation(edge, bounds))
                    | None -> this.location
            let clientPt = Pt(c.X - loc.x, c.Y - loc.y)
            if window.hasCapture.not && (this.tooltipHitOn edge clientPt).IsSome then
                this.setPt(Some(clientPt))
                hoverCell.set(this.hitOn edge clientPt)
            else
                this.setPt(None)
                capturedCell.set(None)
                hoverCell.set(None)
                // Hide tooltip when mouse leaves
                lastToolTipTab := None
                pendingTooltipTab := None
                tooltipTimer.Stop()
                tooltipForm.Visible <- false

    member private this.wndProc(msg:Win32Message) =
#if DEBUG
        if contentHold.IsNone then paintReason <- "window-message"
#endif
        // Monitor callbacks and mouse state changes share one strip transaction.
        DragTrace.windowMessage (fun () -> "strip") msg
#if DEBUG
        if msg.msg = WindowMessages.WM_LBUTTONDOWN then
            let foreground = WinUserApi.GetForegroundWindow()
            let tracePress phase =
                DragTrace.activation (fun () -> sprintf "press.%s strip=%X" phase (msg.hwnd.ToInt64())) foreground
            tracePress "before"
            try this.batch (fun () -> this.wndProcCore(msg))
            finally tracePress "after"
        else this.batch (fun () -> this.wndProcCore(msg))
#else
        this.batch (fun () -> this.wndProcCore(msg))
#endif

    member private this.wndProcCore(msg:Win32Message) =
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
        | WindowMessages.WM_SETTINGCHANGE
        | WindowMessages.WM_SYSCOLORCHANGE
        | WindowMessages.WM_THEMECHANGED ->
            systemAppearanceCell.set(systemAppearanceCell.value + 1)
            msg.def()
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

    // The window of another edge. The mouse is handled as on the top strip.
    // Of its other messages only a change of scale goes to the monitor: the
    // rest (WINDOWPOSCHANGED above all) are read there as the top strip's,
    // which the top edge guard follows, and this is not that strip.
    member private this.edgeWndProc (edge: TabEdges.Edge) (msg: Win32Message) =
        let mousePt() = msg.lParam.location
        let mouse m =
            this.processMouseOn edge m
            msg.def()
        if msg.msg = WindowMessages.WM_DPICHANGED && layeredWindowCell.value.IsSome then
            monitor.windowMsg(msg)
        // A strip written across scrolls by the wheel and by its buttons;
        // a click on a button is not a click on a tab.
        let button() = if this.isAcross edge then this.acrossButtonAt edge (mousePt()) else None
        match msg.msg with
        | WindowMessages.WM_MOUSEACTIVATE -> MouseActivateReturnCodes.MA_NOACTIVATE
        | WindowMessages.WM_MOUSEWHEEL when this.isAcross edge ->
            let delta = int (int16 ((int64 msg.wParam >>> 16) &&& 0xFFFFL))
            this.scrollAcross(edge, Some(delta), None)
            0
        | WindowMessages.WM_LBUTTONDOWN | WindowMessages.WM_LBUTTONDBLCLK when (button()).IsSome ->
            this.scrollAcross(edge, None, button())
            msg.def()
        | WindowMessages.WM_LBUTTONUP when (button()).IsSome -> msg.def()
        | WindowMessages.WM_MOUSEMOVE -> mouse (MouseMove(mousePt()))
        | WindowMessages.WM_LBUTTONDOWN -> mouse (MouseClick(mousePt(), MouseLeft, MouseDown))
        | WindowMessages.WM_LBUTTONUP -> mouse (MouseClick(mousePt(), MouseLeft, MouseUp))
        | WindowMessages.WM_LBUTTONDBLCLK -> mouse (MouseClick(mousePt(), MouseLeft, MouseDblClick))
        | WindowMessages.WM_RBUTTONDOWN -> mouse (MouseClick(mousePt(), MouseRight, MouseDown))
        | WindowMessages.WM_RBUTTONUP -> mouse (MouseClick(mousePt(), MouseRight, MouseUp))
        | WindowMessages.WM_MBUTTONDOWN -> mouse (MouseClick(mousePt(), MouseMiddle, MouseDown))
        | WindowMessages.WM_MBUTTONUP -> mouse (MouseClick(mousePt(), MouseMiddle, MouseUp))
        | WindowMessages.WM_MOUSELEAVE -> mouse MouseLeave
        | _ -> msg.def()

    member private this.appearance = appearanceCell.value.Value
    member private this.top = zorderCell.value.head
    member private this.isEmpty = this.visualOrder.isEmpty
    member private this.contentOffset = this.appearance.tabHeightOffset
    member private this.location = locationCell.value 
    
    member this.withContentSource<'a>(source: string, f: unit -> 'a) =
#if DEBUG
        let previous = contentSource
        contentSource <- source
        lastContentSource <- source
        try this.batch f
        finally contentSource <- previous
#else
        this.batch f
#endif

    member private this.desktopShared =
        Set.union heldDesktopShared (VirtualDesktopGroups.Live.snapshot()).stripShared

    member private this.dimmedTabsFor shared =
        dimmedTabsCell.value.items.list |> List.map (fun (Tab h) -> h)
        |> VirtualDesktopGroups.excludeSharedDimmed shared
        |> List.map Tab |> fun tabs -> Set2(List2(tabs))

    member private this.effectiveDimmedTabs = this.dimmedTabsFor this.desktopShared

    member private this.traceContent(kind: string) =
#if DEBUG
        let source = if contentSource = "cells" then lastContentSource else contentSource
        paintReason <- source + "/" + kind
        VirtualDesktopTrace.handover (fun () ->
            let handle (Tab h) = h.ToInt64()
            sprintf "strip-content strip=%X source=%s kind=%s front=%A foreground=%A dimmed=%s desktopShared=%s effectiveDimmed=%s"
                (this.hwnd.ToInt64()) source kind
                (zorderCell.value.tryHead |> Option.map handle)
                (foregroundCell.value |> Option.map handle)
                (dimmedTabsCell.value.items.list |> List.map handle |> List.sort |> List.map (sprintf "%X") |> String.concat ",")
                (this.desktopShared |> Set.toList |> List.filter (fun h -> this.tabs.contains(Tab h)) |> List.map (fun h -> sprintf "%X" (h.ToInt64())) |> String.concat ",")
                (this.effectiveDimmedTabs.items.list |> List.map handle |> List.sort |> List.map (sprintf "%X") |> String.concat ","))
#else
        ()
#endif

    member private this.tracePaint(kind: string) =
#if DEBUG
        paintSequence <- paintSequence + 1L
        VirtualDesktopTrace.handover (fun () ->
            let handle (Tab h) = h.ToInt64()
            sprintf "strip-paint strip=%X sequence=%d kind=%s reason=%s front=%A foreground=%A dimmed=%s desktopShared=%s keys=%s noticeTick=%A"
                (this.hwnd.ToInt64()) paintSequence kind paintReason
                (zorderCell.value.tryHead |> Option.map handle)
                (foregroundCell.value |> Option.map handle)
                (paintedDimmed |> List.map handle |> List.sort |> List.map (sprintf "%X") |> String.concat ",")
                (paintedShared |> List.map (fun h -> sprintf "%X" (h.ToInt64())) |> String.concat ",") (String.concat "," renderCache.ChangedKeys) desktopNoticeTickValue)
#else
        ()
#endif

    // The top strip draws its tabs as it always has. A group whose tabs are
    // all on other edges keeps the strip - its handle is the group's - and
    // shows nothing there, or the thin strip to drop on while a tab is
    // dragged.
    member private this.topBandEmpty = this.hasOtherEdges && (this.bandOf TabEdges.Edge.Top).isEmpty

    member this.topShows = not this.topBandEmpty

    member private this.update() =
        if destroyed then ()
        elif this.visible && not this.topBandEmpty then
            // Read the complete sprite input on every listener pass, even on a
            // cache hit, so Cell retains all dependencies after move-only work.
            let rendered =
                try
                    let shared = this.desktopShared
                    let ts = this.tsBaseWithShared this.direction shared
#if DEBUG
                    paintedDimmed <- ts.dimmedTabs.items.list
                    paintedShared <- shared |> Set.toList |> List.filter (fun h -> this.tabs.contains(Tab h))
#endif
                    let shrunk = this.isShrunkOn TabEdges.Edge.Top && ts.direction = TabDirection.TabDown
                    // The top band's local Z-order can stay unchanged when
                    // activation moves to another edge. Read this on cache
                    // hits too so Cell keeps the dependency and invalidates it.
                    let otherEdges = this.hasOtherEdges
                    let edgeActive = if otherEdges then zorderCell.value.tryHead else None
                    let key = ts.renderKey @ [ "sliver", box shrunk; "system", box systemAppearanceCell.value;
                                              "edgeActive", box (otherEdges, edgeActive) ]
                    Some(renderCache.Get(key, fun () -> this.render(ts, shrunk)))
                with _ ->
                    PerfTrace.count "stripRenderFailed"
                    None
            let location, alpha = this.location, this.alpha
            match rendered with
            | Some(image, redrawn) ->
                let unchanged =
                    match lastPresentation with
                    | Some(old, point, opacity) ->
                        Object.ReferenceEquals(old, image) && point.record = location.record && opacity = alpha
                    | None -> false
                if not unchanged then
#if DEBUG
                    if not redrawn then
                        match lastPresentation with
                        | Some _ -> PerfTrace.count "stripMoveOnly"
                        | None -> PerfTrace.count "stripReuseShow"
#endif
                    this.window.update(image, location, alpha)
                    this.tracePaint("pixels")
#if DEBUG
                    VirtualDesktopTrace.handover (fun () ->
                        sprintf "strip-show strip=%X owner=%X visible=%b cloak=%A noticeTick=%A"
                            (this.hwnd.ToInt64()) (this.window.parent.hwnd.ToInt64()) this.window.isVisible this.window.cloakedValue desktopNoticeTickValue)
#endif
                    lastPresentation <- Some(image, location, alpha)
            | None ->
                VirtualDesktopTrace.handover (fun () -> sprintf "strip-paint-deferred strip=%X reason=render-failed" (this.hwnd.ToInt64()))
        elif this.visible && List.contains TabEdges.Edge.Top this.receiverEdges then
            this.updateReceiver(this.window, TabEdges.Edge.Top)
            lastPresentation <- None
        else
            let wasVisible = this.window.isVisible
            if wasVisible then this.window.hide()
#if DEBUG
            if wasVisible then
                VirtualDesktopTrace.handover (fun () ->
                    sprintf "strip-hide strip=%X owner=%X cloak=%A noticeTick=%A"
                        (this.hwnd.ToInt64()) (this.window.parent.hwnd.ToInt64()) this.window.cloakedValue desktopNoticeTickValue)
#endif
            lastPresentation <- None
        if not destroyed && edgeWindows.Count > 0 then this.updateEdgeWindows()

    member private this.updateEdgeWindows() =
        let receivers = this.receiverEdges
        for kv in edgeWindows do
            let edge = kv.Key
            let window = _os.windowFromHwnd(kv.Value.hwnd)
            match edgePlacementCell.value.TryFind(edge) with
            | Some((bounds, _, _)) when this.visible && not bounds.size.isEmptyArea ->
                if not (this.bandOf edge).isEmpty then
                    PerfTrace.time "stripRender" (fun () ->
                        let img, location = this.presentEdge edge bounds
                        window.update(img, location, this.alpha))
                elif List.contains edge receivers then
                    this.updateReceiver(window, edge)
                else window.hide()
            | _ -> window.hide()

    // The thin strip to drop on (2-5): along the side of the strip's place
    // that touches the window, eight pixels at 100%.
    member private this.receiverRect (edge: TabEdges.Edge) : Rect =
        let bounds, scale =
            if edge = TabEdges.Edge.Top then Rect(this.location, this.size), this.scale
            else
                match edgePlacementCell.value.TryFind(edge) with
                | Some((b, _, s)) -> b, s
                | None -> Rect(), 1.0
        let across = if TabEdges.runsHorizontally edge then bounds.size.height else bounds.size.width
        let thin = max 1 (min (Dpi.px scale 8) across)
        let x, y, w, h = bounds.location.x, bounds.location.y, bounds.size.width, bounds.size.height
        // Inside the window the side that touches it is the window's border.
        let inside = (this.edgeDraw edge).inside
        match edge with
        | TabEdges.Edge.Top ->
            if this.showInside then Rect(Pt(x, y), Sz(w, thin))
            else Rect(Pt(x, y + h - thin), Sz(w, thin))
        | TabEdges.Edge.Left ->
            if inside then Rect(Pt(x, y), Sz(thin, h)) else Rect(Pt(x + w - thin, y), Sz(thin, h))
        | TabEdges.Edge.Right ->
            if inside then Rect(Pt(x + w - thin, y), Sz(thin, h)) else Rect(Pt(x, y), Sz(thin, h))
        | TabEdges.Edge.Bottom ->
            if inside then Rect(Pt(x, y + h - thin), Sz(w, thin)) else Rect(Pt(x, y), Sz(w, thin))

    member private this.receiverGeometry edge =
        let thin = this.receiverRect edge
        let appearance =
            if edge = TabEdges.Edge.Top then this.appearance
            else
                match edgePlacementCell.value.TryFind(edge) with
                | Some(_, a, _) -> a
                | None -> this.appearance
        let x, y, w, h =
            TabEdges.receiverHitRect edge appearance.tabHeight
                (thin.location.x, thin.location.y, thin.size.width, thin.size.height)
        Rect(Pt(x, y), Sz(w, h)), thin, appearance.tabActiveTabColor

    member private this.updateReceiver(window: Window, edge) =
        let hit, _, _ = this.receiverGeometry edge
        let img = Img(if hit.size.isEmptyArea then Sz(1, 1) else hit.size)
        let g = img.graphics
        // Keep the expanded area hit-testable after applying window opacity.
        let alpha = this.alpha
        let hitAlpha = TabEdges.receiverPixelAlpha alpha
        // Layered windows need nonzero alpha for hit testing. No visible border.
        g.Clear(Color.FromArgb(hitAlpha, 0, 0, 0))
        window.update(img, hit.location, alpha)

    /// An edge's strip as it is drawn now, of an edge other than the top
    /// (for the strip window, and for pictures made without one).
    member this.renderEdge (edge: TabEdges.Edge) : Img =
        if TabEdges.runsHorizontally edge then
            try this.renderBand (this.tsOn edge) with _ -> Img(Sz(1, 1))
        elif this.isAcross edge then
            try this.renderAcross edge with _ -> Img(Sz(1, 1))
        else this.renderSide edge

    // The strip of an edge other than the top as its window shows it, and
    // where: the whole strip, or, while "hide inside tabs" has shrunk the
    // group, the sliver of a strip inside the window - its far side, against
    // the window's border, as the top strip's is (render).
    member this.presentEdge (edge: TabEdges.Edge) (bounds: Rect) : Img * Pt =
        let img = this.renderEdge edge
        let crop, at = this.edgePresentation(edge, bounds)
        if crop.size = img.size then img, at
        elif crop.size.isEmptyArea then Img(Sz(1,1)), at
        else Img(img.bitmap.Clone(crop.Rectangle, PixelFormat.Format32bppArgb)), at

    // Rotate each tab independently, preserving top-to-bottom strip order.
    // The right side can read downwards or retain its original upwards text.
    // Mirror the cross-axis offset with clockwise rotation to keep the baseline.
    member private this.renderSide (edge: TabEdges.Edge) : Img =
        try
            let ts = this.tsOn edge
            let logical = ts.size
            let img = Img(Sz(max 1 logical.height, max 1 logical.width))
            let g = img.graphics
            let bg = if this.transparent then Color.FromArgb(0, 0, 0, 0) else Color.FromArgb(1, 1, 1, 1)
            use brush = new SolidBrush(bg)
            g.FillRectangle(brush, Rect(Pt(), img.size).Rectangle)
            for (pt, sprite) in List.rev (this.bandSprites ts) do
                let tabImg = (sprite :> ISprite).render
                let clockwise = this.sideTextClockwise edge
                let column = TabEdges.sideTabColumn clockwise logical.height pt.y tabImg.height
                tabImg.bitmap.RotateFlip(if clockwise then RotateFlipType.Rotate90FlipNone else RotateFlipType.Rotate270FlipNone)
                g.DrawImageUnscaled(tabImg.bitmap, Point(column, pt.x))
            img
        with _ ->
            Img(Sz(1, 1))

    // No draw correction any more. The former applyDrawCorrection pre-compressed
    // the rendered strip horizontally to cancel the drift between the OS bitmap
    // stretch and the mouse virtualization of a DPI-UNAWARE layered window. A
    // per-monitor aware window is not stretched at all, so that compression
    // would now BE the distortion it used to remove - and it re-sampled the
    // whole strip with HighQualityBicubic on every frame, which fought the very
    // sharpness this change is about. (It would also be dead code: it derived
    // its rate from GetDeviceCaps HORZRES vs DESKTOPHORZRES, and an aware
    // process gets the same physical value from both, so the rate is always 0.)
    member private this.render(ts: TabStripSprite<Tab>, shrunk: bool) : Img =
        // Only the group's active tab is active across all edge bands.
        let img = if this.hasOtherEdges then this.renderBand ts else ts.render
        if shrunk then
            // The clipped image is independent; release the full-size source.
            try img.clip(Rect(Pt(0, img.height - Dpi.px ts.scale 7), Sz(img.width, Dpi.px ts.scale 7)))
            finally img.bitmap.Dispose()
        else img

    member private this.withUpdate<'a> (f: unit -> 'a) : 'a =
        this.batch f

    member this.hwnd = hwndRef.Value
    
    member this.addTabWithState(tab, alignment, pinned, after, ?restoreOrder: TabOrder.Placed list -> IntPtr list, ?edge: TabEdges.Edge, ?alignmentEdge: TabEdges.Edge) =
        this.batch <| fun () ->
            // Returning through any insertion path invalidates the departed edge.
            if departedEdgeSnapshot.tryFind(tab).IsSome then
                departedEdgeSnapshot <- departedEdgeSnapshot.remove tab
            if not (visualOrderCell.value.contains((=) tab)) then
                let (Tab hwnd) = tab
                let targetEdge = TabEdges.fallback this.allowedEdges (defaultArg edge (this.edgeOf tab))
                let sourceEdge = alignmentEdge |> Option.orElseWith (fun () -> Services.program.getWindowAlignmentEdge(hwnd))
                this.setEdgeEntry tab targetEdge
                match TabEdges.alignmentOnArrival sourceEdge targetEdge (Some alignment) with
                | Some value -> tabAlignmentCell.map(fun m -> m.add tab value)
                | None ->
                    tabAlignmentCell.map(fun m -> m.remove tab)
                    Services.program.setWindowAlignment(hwnd, None)
                Services.program.setWindowAlignmentEdge(hwnd, targetEdge)
                if pinned then pinnedTabsCell.set(pinnedTabsCell.value.add(tab))
                else pinnedTabsCell.set(pinnedTabsCell.value.remove(tab))
                let zone = this.visualZoneOf tab
                let added = TabOrder.addTab tab zone after this.visualZoneOf visualOrderCell.value.list
                let ordered =
                    match restoreOrder with
                    | Some order ->
                        added |> List.map (fun (Tab h as t) -> TabOrder.placed h (this.visualZoneOf t))
                        |> order |> List.map Tab
                    | None -> added
                visualOrderCell.set(List2(ordered))
                this.zorder <- zorderCell.value.append(tab)
#if DEBUG
                this.withContentSource("tab-added", fun () -> this.traceContent("front-order"))
#endif

    member this.addTabSlide tab (slide:Option<_>) =
        this.batch <| fun () ->
            this.addTabWithState(tab, this.getTabAlign(tab), this.isPinned(tab), None)
            slide.iter <| fun slide -> this.slide <- Some(slide)

    member this.addTab tab = this.addTabSlide tab None

    member this.removeTab tab =
        // The scaled-icon cache is keyed by source HICON, and Windows recycles
        // icon handles once the window that owned them is gone. Dropping the
        // cache whenever a tab leaves a strip is the cheap, coarse way to make
        // sure a recycled handle can never resurrect the previous window's
        // picture; the cache refills with one CopyImage per visible tab.
        ScaledIcon.invalidate()
        IconBitmapCache.clear()
        // A removed window can recycle its icon handle before the next draw.
        renderCache.Clear()
        Cell.beginUpdate()
        match tabEdgeCell.value.tryFind(tab) with
        | Some(edge) ->
            departedEdgeSnapshot <- departedEdgeSnapshot.add tab edge
            tabEdgeCell.map(fun m -> m.remove tab)
        | None ->
            if departedEdgeSnapshot.tryFind(tab).IsSome then
                departedEdgeSnapshot <- departedEdgeSnapshot.remove tab
        if pinnedTabsCell.value.contains(tab) then
            pinnedTabsCell.set(pinnedTabsCell.value.remove(tab))
        tabAlignmentCell.map(fun m -> m.remove tab)
        tabFillColor.map(fun m -> m.remove tab)
        tabUnderlineColor.map(fun m -> m.remove tab)
        tabBorderColor.map(fun m -> m.remove tab)
        visualOrderCell.map(fun l -> l.where((<>) tab))
        this.zorder <- zorderCell.value.where((<>) tab)
        tabInfoCell.map(fun m -> m.remove tab)
#if DEBUG
        this.withContentSource("tab-removed", fun () -> this.traceContent("front-order"))
#endif
        Cell.endUpdate()

    member this.tabs : Set2<Tab> = Set2(visualOrderCell.value)

    member this.visualOrder
        with get() : List2<_> = visualOrderCell.value


    // Where the tab being slid would land: the answer of the strip it is
    // being slid along. The index counts that strip's tabs without the
    // dragged ones; for the top strip, which comes first in the order, that
    // is the index into the whole list as well.
    member this.movedTab =
        match this.slide with
        | Some(tab, _) when this.hasOtherEdges -> (this.tsOn (this.edgeOf tab)).movedTab
        | _ -> this.ts.movedTab

    /// The edge of the strip the dragged tab is being slid along.
    member this.slideEdge = this.slide |> Option.map (fun (tab, _) -> this.edgeOf tab)

    /// Keep the transferred tabs in their source order before computing the
    /// destination drop. The pivot need not have been the first selected tab.
    member this.prepareEdgeDragBlock(tabs: Tab list) =
        let moving = tabs |> List.distinct |> List.filter (fun t -> this.tabs.contains t)
        let mutable pending = moving
        let order =
            this.visualOrder.list |> List.map (fun t ->
                if List.contains t moving then
                    let next = List.head pending
                    pending <- List.tail pending
                    next
                else t)
        this.setVisualOrder(order)
        this.dragGroup <- moving

    static member edgeAppearance(values: TabEdgeSettings.EdgeValues, current: TabAppearanceInfo, defaults: TabAppearanceInfo, scale: float) =
        let raw =
            { current with
                tabHeight = defaultArg values.tabHeight defaults.tabHeight
                tabMaxWidth = defaultArg values.tabMaxWidth defaults.tabMaxWidth
                tabPinnedTabWidth = defaultArg values.tabPinnedTabWidth defaults.tabPinnedTabWidth
                tabPinnedTabWidthIcon = defaultArg values.tabPinnedTabWidthIcon defaults.tabPinnedTabWidthIcon
                tabOverlap = defaultArg values.tabOverlap defaults.tabOverlap
                tabShape = values.tabShape |> Option.map TabShape.normalize |> Option.defaultValue defaults.tabShape }
        Dpi.scaleAppearance scale raw

    // The group's tabs as TabEdges sees them.
    member private this.placedTabs =
        visualOrderCell.value.list |> List.map (fun t ->
            TabEdges.placed t (this.edgeOf t)
                (if this.getTabAlign(t) = TopLeft then TabEdges.AlignStart else TabEdges.AlignEnd)
                (pinnedTabsCell.value.contains(t)))

    /// A drop on the strip of an edge other than the top (2-3, 2-4): where
    /// the strip reports the dragged tab or block landing, judged by the
    /// lengths it draws them with (TabEdges.dropOnStripAt), and put there
    /// with the strip's own smart pin (TabEdges.moveIntoStrip). Returns the
    /// moved tabs and their alignment, or None when the slide is not on that
    /// edge.
    member this.dropOnEdge(edge: TabEdges.Edge) : (Tab list * TabAlign) option =
        match this.slide with
        | Some(pivot, slideAt) when edge <> TabEdges.Edge.Top && this.edgeOf pivot = edge ->
            // A strip written across judges the drop by its rows, all one
            // length, along the rows' own coordinates (acrossOf).
            let lengths, at =
                match this.acrossOf edge with
                | Some(layout) ->
                    ({ stripLength = float layout.layoutLength
                       tabOverlap = float layout.rows.overlap
                       pinnedLength = float layout.rows.length
                       unpinnedLength = float layout.rows.length } : TabEdges.StripLengths),
                    slideAt - layout.viewStart + layout.scroll
                | None ->
                    let ts = this.tsOn edge
                    let pinnedLength, unpinnedLength = ts.tabLengths
                    let overlap =
                        match edgePlacementCell.value.TryFind(edge) with
                        | Some((_, a, _)) -> float a.tabOverlap
                        | None -> float this.appearance.tabOverlap
                    ({ stripLength = float ts.size.width
                       tabOverlap = overlap
                       pinnedLength = pinnedLength
                       unpinnedLength = unpinnedLength } : TabEdges.StripLengths),
                    slideAt
            let dragGroup = dragGroupCell.value |> List.filter (fun t -> this.edgeOf t = edge)
            let moving = if List.isEmpty dragGroup then [ pivot ] else dragGroup
            let before = this.placedTabs
            let index, targetAlign = TabEdges.dropPositionAt lengths (TabEdges.stripTabsOf edge before) dragGroup pivot at
            let after = TabEdges.moveIntoStrip edge moving index (Some targetAlign) before
            let align =
                match after |> List.tryFind (fun p -> p.id = pivot) with
                | Some(p) when p.align = TabEdges.AlignStart -> TopLeft
                | _ -> TopRight
            Cell.beginUpdate()
            for p in after do
                if List.contains p.id moving then
                    tabAlignmentCell.map(fun m -> m.add p.id (if p.align = TabEdges.AlignStart then TopLeft else TopRight))
                    this.setEdgeEntry p.id p.edge
                    if p.pinned && not (pinnedTabsCell.value.contains(p.id)) then
                        pinnedTabsCell.set(pinnedTabsCell.value.add(p.id))
                    elif not p.pinned && pinnedTabsCell.value.contains(p.id) then
                        pinnedTabsCell.set(pinnedTabsCell.value.remove(p.id))
            visualOrderCell.set(List2(after |> List.map (fun p -> p.id)))
            this.normalizeVisualOrder()
            Cell.endUpdate()
            for t in moving do
                let finalIdx = visualOrderCell.value.tryFindIndex((=) t) |> Option.defaultValue 0
                monitor.tabMoved(t, finalIdx)
                tabMovedEvent.Trigger(t, finalIdx)
            Some(moving, align)
        | _ -> None

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
        // A neighbour is a tab of the same strip: a tab of another edge that
        // happens to come next in the group's order is not beside it.
        let pivotEdge = this.edgeOf pivot
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
                    not (Set.contains t tabSet) && this.getTabAlign(t) = pivotAlign && this.edgeOf t = pivotEdge)
            | None -> None
        let leftNeighbor =
            match groupFirstIdx with
            | Some idx when idx > 0 ->
                postOrder
                |> List.truncate idx
                |> List.filter (fun t ->
                    not (Set.contains t tabSet) && this.getTabAlign(t) = pivotAlign && this.edgeOf t = pivotEdge)
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

    member private this.frontOrder(order: List2<Tab>) =
        let handles = order.list |> List.map (fun (Tab h) -> h)
        let dimmed =
            (match contentHold with Some(_, _, prepared) -> prepared | None -> this.effectiveDimmedTabs.items.list)
            |> List.map (fun (Tab h) -> h)
            |> VirtualDesktopGroups.excludeSharedDimmed this.desktopShared |> Set.ofList
        VirtualDesktopGroups.stripFrontOrder (Set.ofList handles) dimmed None handles

    member this.zorder
        with get() = zorderCell.value
        and set(zorder:List2<Tab>) =
            let before = zorderCell.value.tryHead
            let order =
                match contentHold with
                | Some(_, prepared, _) -> prepared.where(this.tabs.contains)
                | None -> zorder.where(this.tabs.contains)
            let _, ordered = this.frontOrder(order)
            let order = List2(ordered |> List.map Tab)
            if zorderCell.value.list <> order.list then
#if DEBUG
                if before <> order.tryHead then
                    DragTrace.log (fun () ->
                        let handle = Option.map (fun (Tab h) -> h.ToInt64()) >> Option.defaultValue 0L
                        sprintf "[dockDrag] selection reason=strip-zorder strip=%X before=%X after=%X"
                            (this.hwnd.ToInt64()) (handle before) (handle order.tryHead))
#endif
                zorderCell.set(order)
                this.traceContent("front-order")
            // A tab that becomes the active one on a strip written across is
            // scrolled into sight. (No other strip scrolls.)
            if edgeWindows.Count > 0 && zorderCell.value.tryHead <> before then
                zorderCell.value.tryHead |> Option.iter this.scrollToShow

    member this.sprite = this.ts.sprite
            
    member this.isShrunk
        with get() = isShrunkCell.value
        and set(newValue) = isShrunkCell.set(newValue)

    member this.isShrunkOn edge =
        if TabEdges.isTopOnly this.allowedEdges then this.isShrunk
        else Set.contains edge shrunkEdgesCell.value

    member this.setShrunkOn(edge, value) =
        let current = shrunkEdgesCell.value
        let next = if value then Set.add edge current else Set.remove edge current
        if next <> current then shrunkEdgesCell.set(next)

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
    // With tabs on several edges the zones are TabEdges': the edge first,
    // then these four. A top tab's zone is the same number either way.
    member this.visualZoneOf(tab) =
        TabEdges.rank (this.edgeOf tab) * 4
        + TabOrder.zoneOf (this.getTabAlign(tab) = TopLeft) (pinnedTabsCell.value.contains(tab))

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

    member this.pinAll(?edge: TabEdges.Edge) =
        let targets = edge |> Option.map this.bandOf |> Option.defaultValue visualOrderCell.value
        let initial = if edge.IsSome then pinnedTabsCell.value else Set2<Tab>()
        pinnedTabsCell.set(targets.fold initial (fun s t -> s.add(t)))
        this.normalizeVisualOrder()

    member this.unpinAll(?edge: TabEdges.Edge) =
        let remaining =
            match edge with
            | Some e -> (this.bandOf e).fold pinnedTabsCell.value (fun s t -> s.remove(t))
            | None -> Set2<Tab>()
        pinnedTabsCell.set(remaining)
        this.normalizeVisualOrder()

    // Get tabs in same alignment group as the given tab, in visual order.
    // Of its own strip: "to the left / right" and the alignment groups are
    // counted on one edge, never across (3-3). The whole list while the top
    // edge is the only one.
    member private this.sameAlignGroup(tab) =
        let tabAlign = this.getTabAlign(tab)
        (this.bandOf (this.edgeOf tab)).list |> List.filter (fun t -> this.getTabAlign(t) = tabAlign)

    // Tabs to the left of (and including) the given tab in visual order, regardless of alignment or pin state
    member private this.visualLeftTabs(tab) =
        let order = (this.bandOf (this.edgeOf tab)).list
        match order |> List.tryFindIndex ((=) tab) with
        | Some idx -> order |> List.take (idx + 1)
        | None -> []

    // Tabs to the right of (and including) the given tab in visual order, regardless of alignment or pin state
    member private this.visualRightTabs(tab) =
        let order = (this.bandOf (this.edgeOf tab)).list
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

    member this.clearTabsAlign(tabs: Tab list) =
        tabAlignmentCell.map(fun m ->
            tabs |> List.fold (fun (acc: Map2<Tab, TabAlign>) tab -> acc.remove tab) m)
        this.normalizeVisualOrder()

    member this.clearTabAlign(tab) = this.clearTabsAlign([tab])

    member this.setTabAlign(tab, newAlignment) =
        let (Tab hwnd) = tab
        Services.program.setWindowAlignmentEdge(hwnd, this.edgeOf tab)
        tabAlignmentCell.map(fun m -> m.add tab newAlignment)
        // Restore canonical visual ordering after changing alignment
        this.normalizeVisualOrder()

    // Change several tabs' alignment as ONE operation (single map update +
    // single normalize). Aligning them one at a time sends each converted tab
    // to the far end of the destination zone in turn, which reverses the
    // group's relative order on an all-left <-> all-right switch.
    member this.setTabsAlign(tabs: Tab list, newAlignment) =
        for (Tab hwnd as tab) in tabs do
            Services.program.setWindowAlignmentEdge(hwnd, this.edgeOf tab)
        tabAlignmentCell.map(fun m ->
            tabs |> List.fold (fun (acc: Map2<Tab, TabAlign>) t -> acc.add t newAlignment) m)
        this.normalizeVisualOrder()

    member this.setEdgeDefaultAlignments(values) =
        if edgeDefaultAlignments.value <> values then
            edgeDefaultAlignments.set(values)
            if this.hasOtherEdges then this.normalizeVisualOrder()

    member private this.defaultAlignOn edge =
        TabEdges.defaultAlignment edge defaultAlignmentCell.value edgeDefaultAlignments.value

    member this.getTabAlign(tab) =
        let alignment =
            match tabAlignmentCell.value.tryFind(tab) with
            | Some(a) -> a
            | None -> this.defaultAlignOn (this.edgeOf tab)
        TabEdges.pinnedRowAlignment (this.isAcross (this.edgeOf tab))
            (pinnedTabsCell.value.contains(tab)) TopLeft alignment

    // The tab's own alignment, None when it simply follows the group's side:
    // the distinction the global per-window map keeps, for another group of
    // the same window to take over as it is.
    member this.explicitTabAlignThreadSafe(tab) = tabAlignmentSnapshot.tryFind(tab)
            
    member this.direction = if showInsideCell.value then TabDown else TabUp
    
    member this.tabInfo tab : TabInfo = 
        tabInfoCell.value.tryFind(tab).def({
            text = ""
            isRenamed = false
            iconSmall = System.Drawing.SystemIcons.Application
            iconBig = System.Drawing.SystemIcons.Application
            previewSize = fun() -> Sz(1,1)
            preview = fun() -> Img(Sz(1,1))
        })

    member this.setTabInfo((tab, tabInfo)) = 
        tabInfoCell.map(fun m -> m.add tab tabInfo)
            
    // Where a tab is in its own strip's layout (along the strip for a left
    // strip, see logicalPt).
    // A row written across: where it starts along the strip, then across.
    member this.tabLocation (tab: Tab) =
        let edge = this.edgeOf tab
        if this.isAcross edge then
            match this.acrossOf edge |> Option.bind (fun layout -> layout.rowOf tab) with
            | Some(x, y, _) -> Pt(y, x)
            | None -> Pt()
        else (this.tsOn edge).tabLocation tab

    member this.dragTabLocation (tab:Tab, previewWidth:int) : Pt=
        // MouseDown needs only geometry. Rendering here would send PrintWindow
        // to the application even when the press never becomes a drag.

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

    // Share the source sprite and physical geometry between the pointer anchor
    // and painting. Constructing the sprite does not capture the window.
    member private this.dragEdgeSprite(tab: Tab) =
        let edge = this.edgeOf tab
        match this.acrossOf edge |> Option.bind (fun layout -> layout.rowOf tab) with
        | Some(_, _, sprite) -> sprite.size, (fun () -> sprite.render)
        | None ->
            let sprite = (this.tsOn edge).tabSpriteRec(tab)
            let sideways = not (TabEdges.runsHorizontally edge)
            let size = if sideways then Sz(sprite.size.height, sprite.size.width) else sprite.size
            size, (fun () ->
                let img = (sprite :> ISprite).render
                if sideways then
                    let clockwise = this.sideTextClockwise edge
                    img.bitmap.RotateFlip(if clockwise then RotateFlipType.Rotate90FlipNone else RotateFlipType.Rotate270FlipNone)
                img)

    member private this.dragEdgePlacement(tab: Tab, previewSize: Sz, tabSize: Sz) =
        let align = if this.getTabAlign(tab) = TopLeft then TabEdges.AlignStart else TabEdges.AlignEnd
        let (w,h), (tx,ty), (wx,wy) =
            TabEdges.dragPreviewPlacement (this.edgeOf tab) align
                (previewSize.width, previewSize.height) (tabSize.width, tabSize.height)
        Sz(w,h), Pt(tx,ty), Pt(wx,wy)

    member this.dragImageOffset(tab: Tab, previewSize: Sz, clickOffset: Pt) =
        if this.edgeOf tab = TabEdges.Edge.Top then
            let at = this.dragTabLocation(tab, previewSize.width)
            let ratio = float previewSize.width / float (this.bounds: Rect).width
            at.add(Pt(int(float clickOffset.x * ratio), clickOffset.y))
        else
            let size, _ = this.dragEdgeSprite(tab)
            let _, at, _ = this.dragEdgePlacement(tab, previewSize, size)
            at.add(this.logicalPt (this.edgeOf tab) clickOffset)

    member this.dragPreviewWindowOffset(tab: Tab, previewSize: Sz) =
        if this.edgeOf tab = TabEdges.Edge.Top then None
        else
            let size, _ = this.dragEdgeSprite(tab)
            let _, _, windowAt = this.dragEdgePlacement(tab, previewSize, size)
            Some windowAt

    member this.dragImage (tab:Tab) : Img=
        if this.edgeOf tab = TabEdges.Edge.Top then this.dragTopImage(tab)
        else
            let preview: Img = this.tabInfo(tab).preview()
            try
                let tabSize, render = this.dragEdgeSprite(tab)
                let size, tabAt, windowAt = this.dragEdgePlacement(tab, preview.size, tabSize)
                let tabImg = render()
                try
                    let img = Img(size)
                    use g = img.graphics
                    g.DrawImageUnscaled(preview.bitmap, windowAt.Point)
                    g.DrawImageUnscaled(tabImg.bitmap, tabAt.Point)
                    img
                finally tabImg.bitmap.Dispose()
            finally preview.bitmap.Dispose()

    member private this.dragTopImage (tab:Tab) : Img=
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

    member this.setButtonModes(closeMode, pinMode) =
        let modes = TabButtonPolicy.normalize closeMode, TabButtonPolicy.normalize pinMode
        if buttonModesCell.value <> modes then buttonModesCell.set modes

    // The appearance and the monitor scale it was computed at are ONE value:
    // the appearance carries the already-scaled heights and indents, `scale`
    // covers the constants the sprite tree writes itself. Taking them as a
    // pair means no caller can push a 150% appearance while the sprites still
    // draw at 100%, which is the state a window crossing a monitor boundary
    // would otherwise be in for a frame. setPlacement pushes the same scale
    // again, with the bounds it produced.
    member this.setTabAppearance(appearance, scale: float) =
        Cell.beginUpdate()
        if appearanceCell.value <> Some(appearance) then appearanceCell.set(Some(appearance))
        if scaleCell.value <> scale then scaleCell.set(scale)
        Cell.endUpdate()

    // Runs f with this strip's cell listeners held back, so the strip renders
    // once for everything f changes. The strip has its own cell scope, so a
    // WindowGroup update does not batch it: one foreground change in the
    // system rendered every group's strip about eight times in a row (zorder,
    // foreground, appearance, four placement cells, visibility).
    member this.batch<'a> (f: unit -> 'a) : 'a =
        Cell.beginUpdate()
        try f()
        finally Cell.endUpdate()
            
    member this.contentBounds 
        with get() = contentBoundsCell.value
        and set(value) = contentBoundsCell.set(value)
            
    member this.foreground 
        with get() = foregroundCell.value
        and set(value) =
            let value = match contentHold with Some(tab, _, _) -> tab | None -> value
            let value = value |> Option.bind (fun tab ->
                let preferred = zorderCell.value.moveToEnd((=) tab)
                this.frontOrder(preferred) |> fst |> Option.map Tab)
            // Pushed on every foreground change in the system; setting the
            // same tab again would only render the strip again.
            if value <> this.foreground then
#if DEBUG
                DragTrace.log (fun () ->
                    let handle = Option.map (fun (Tab h) -> h.ToInt64()) >> Option.defaultValue 0L
                    sprintf "[dockDrag] selection reason=strip-foreground strip=%X before=%X after=%X"
                        (this.hwnd.ToInt64()) (handle this.foreground) (handle value))
#endif
                prevForegroundCell.set(this.foreground)
                foregroundCell.set(value)
                this.traceContent("foreground")
            
    member this.bounds = this.window.bounds

    member this.setPlacement(placement) =
        // The placement is pushed again on every foreground change, mostly
        // unchanged: set only what differs, and render once for all of it.
        this.batch <| fun () ->
            if showInsideCell.value <> placement.showInside then showInsideCell.set(placement.showInside)
            if sizeCell.value.record <> placement.bounds.size.record then sizeCell.set(placement.bounds.size)
            if locationCell.value.record <> placement.bounds.location.record then locationCell.set(placement.bounds.location)
            // The scale that produced these bounds. It travels with the placement
            // instead of being re-derived here so the decorator and the strip can
            // never disagree about which monitor the strip is on.
            if scaleCell.value <> placement.scale then scaleCell.set(placement.scale)

    member this.setDimmedTabs(tabs: Tab list) = this.batch (fun () ->
        let requested =
            (match contentHold with Some(_, _, prepared) -> prepared | None -> tabs)
            |> List.filter this.tabs.contains |> List.map (fun (Tab h) -> h)
        let shared = this.desktopShared
        let accepted = VirtualDesktopGroups.excludeSharedDimmed shared requested
#if DEBUG
        if accepted <> requested then
            VirtualDesktopTrace.handover (fun () ->
                let handles xs = xs |> List.sort |> List.map (fun (h: IntPtr) -> sprintf "%X" (h.ToInt64())) |> String.concat ","
                sprintf "strip-dimmed-filter strip=%X source=%s requested=%s excluded=%s held=%b"
                    (this.hwnd.ToInt64()) lastContentSource (handles requested)
                    (requested |> List.filter shared.Contains |> handles) contentHold.IsSome)
#endif
        let tabs = accepted |> List.map Tab |> List.sort
        if dimmedTabsCell.value.items.list <> (Set2(List2(tabs))).items.list then
            dimmedTabsCell.set(Set2(List2(tabs)))
            this.traceContent("dimmed")
        // The order and dimming must settle in the same transaction.
        this.zorder <- zorderCell.value
        this.foreground <- foregroundCell.value)

    // Directly submit the prepared pixels even inside the outer mouse batch.
    // Its eventual listener pass will see the same image and skip submission.
    member this.prepareDesktopSwitch(tab, order, dimmed, ?source: string) =
        heldDesktopShared <- (VirtualDesktopGroups.Live.snapshot()).stripShared
        contentHold <- Some(Some tab, order, dimmed)
        this.withContentSource(defaultArg source "click-preparation", fun () ->
            this.zorder <- order
            this.foreground <- Some tab
            this.setDimmedTabs(dimmed))

    member this.presentDesktopSwitch() =
#if DEBUG
        paintReason <- "click-preparation"
#endif
        this.update()
#if DEBUG
        let clock = System.Diagnostics.Stopwatch.StartNew()
#endif
        let result = StripComposition.DwmFlush()
#if DEBUG
        VirtualDesktopTrace.handover (fun () ->
            sprintf "strip-flush strip=%X result=%d elapsedMs=%.3f sequence=%d"
                (this.hwnd.ToInt64()) result clock.Elapsed.TotalMilliseconds paintSequence)
#endif
        ()

    member this.holdDesktopContent() =
        heldDesktopShared <- (VirtualDesktopGroups.Live.snapshot()).stripShared
        match zorderCell.value.tryHead with
        | Some tab -> contentHold <- Some(foregroundCell.value, zorderCell.value, dimmedTabsCell.value.items.list)
        | None -> ()

    member this.releaseDesktopSwitch() =
        contentHold <- None
        heldDesktopShared <- Set.empty

    member this.alpha
        with get() = alphaCell.value
        and set(value) = alphaCell.set(value)

    member this.desktopNoticeTick with set(value: uint32 option) =
#if DEBUG
        desktopNoticeTickValue <- value
#else
        ()
#endif

    member this.visible 
        with get() = visibleCell.value
        and set(value) = if visibleCell.value <> value then visibleCell.set(value)
            
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

    /// Match the top strip's owner (currently none). Visibility is controlled
    /// explicitly for every strip by the group's desktop state.
    member this.setEdgeOwners(owner: Window) =
        for w in edgeWindows.Values do
            _os.windowFromHwnd(w.hwnd).setParent(owner)

    /// Keep the other edges' windows in the top strip's layer, below it and
    /// above the window in front (`front`). Only a window out of that range
    /// is moved - directly below the top strip - so that whatever the top
    /// edge guard keeps directly below the top strip stays where it is.
    member this.followTopStrip(front: IntPtr) =
        if edgeWindows.Count > 0 then
            let top = this.hwnd
            let topIsTopMost = this.window.isTopMost
            for w in edgeWindows.Values do
                try
                    let window = _os.windowFromHwnd(w.hwnd)
                    if topIsTopMost && not window.isTopMost then window.makeTopMost()
                    elif not topIsTopMost && window.isTopMost then window.makeNotTopMost()
                    let inPlace =
                        TopEdgeGuardPlacement.isAbove top w.hwnd &&
                        (front = IntPtr.Zero || TopEdgeGuardPlacement.isAbove w.hwnd front)
                    if not inPlace then window.insertAfter(top)
                with _ -> ()

    /// Raised by destroy while the other edges' windows still exist, for
    /// whoever registered them somewhere (as places to drop on) to take them
    /// off again: once they are gone, edgeHwnds is empty.
    member this.destroying = destroyingEvent.Publish

    member this.destroy() = 
        IconBitmapCache.clear()
        ScaledIcon.invalidate()
        destroyed <- true
        renderCache.Clear()
        lastPresentation <- None
        (try destroyingEvent.Trigger() with _ -> ())
        eventHandlersCell.value.items.iter(fun d -> d.Dispose())
        // Every edge's window, whatever happens to any one of them.
        for w in List.ofSeq edgeWindows.Values do
            try (w :?> IDisposable).Dispose() with _ -> ()
        edgeWindows.Clear()
        edgeHwndsSnapshot.publish([])
        layeredWindowCell.value.iter <| fun w -> (w :?> IDisposable).Dispose()
        dragScrollTimer.Dispose()
        tooltipTimer.Dispose()
        tooltipHideTimer.Dispose()
        tooltipForm.Dispose()
        this.window.destroy()
            
    member this.tryHit(pt) : Option<_> = this.ts.tryHit(pt)
