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

/// Debug-only trace of window titles and tab texts: every name-change event a
/// group receives, every tab text it sets, and every tab whose text differs
/// from its window's title. Truncated at each start; Release builds drop it.
module TitleTrace =
#if DEBUG
    let private path =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "WindowTabs", "title_trace.log")
    let mutable private started = false
    let private gate = obj()
#endif
    let log (f: unit -> string) =
#if DEBUG
        try
            lock gate (fun () ->
                if not started then
                    started <- true
                    try File.WriteAllText(path, "") with _ -> ()
                File.AppendAllText(path,
                    sprintf "%s [t%d] %s\r\n" (DateTime.Now.ToString("HH:mm:ss.fff"))
                        Thread.CurrentThread.ManagedThreadId (f())))
        with _ -> ()
#else
        ignore f
#endif

type WindowGroup(plugins:List2<IPlugin>) as this =
    let Cell = CellScope(true)
    let _bb = Blackboard()
    let invoker = InvokerService.invoker
    let _os = OS()
    let frameMargins = WindowFrameMargin.Cache()
    let mutable marginPollIndex = 0
    let addedEvent = Event<_>()
    let movedEvent = Event<IntPtr*int>()
    let removedEvent = Event<_>()
    let exitedEvent = Event<_>()
    // Raised when this group's lock changes, from its own menu or from the
    // dialog: the top edge band follows it (see TabStripDecorator).
    let lockChangedEvent = Event<unit>()
    let flashEvent = Event<_>()
    let foregroundEvent = Event<_>()

    let isDestroyed = Cell.create(false)
    let mutable tabDragOwners = 0
    let mutable destroyAfterTabDrag = false
    let zorderCell = Cell.create(List2<IntPtr>())
    // Set of inactive tabs that the user has "selected" via Shift/Ctrl click.
    // The active tab (zorder.head) is NEVER part of this set; treat it
    // implicitly as another action target alongside the selected ones. The
    // set is cleared by a plain left click on any tab and by adding/removing
    // windows from the group.
    let selectedTabsCell = Cell.create(Set2<IntPtr>())
    let prevTop = Cell.create(None)
    let placement = Cell.create(None:Option<Rect * OSWindowPlacement>)
    // Actual outer bounds include the maximized frame; placement uses work-area bounds.
    let mutable maximizedFrameBounds : Rect option = None
    let mutable childPlacementPending = false
    let followerPlacements = FollowerPlacementQueue()
    let synchronousFollowers = System.Collections.Generic.HashSet<IntPtr>()
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
    let captionDragOwner = obj()
    // Whether this group's windows are locked in place. The dialog's setting
    // is the value a new group starts from, and changing it there applies to
    // every group, exactly as the snap margin behaves; the tab menu then
    // overrides it for one group. While it is on, the group's windows are
    // registered in CaptionDragTargets, which is what puts the input hook up
    // (see CaptionDragPlugin) and what the hook filters presses by.
    //
    // It also arms the fallback for drags the hook could not stop (see
    // CaptionDragFallback); that session belongs to one window and to the
    // press (sequence number) that armed it. (group-thread only)
    let mutable captionDragEnabled = false
    let mutable fallbackSession : CaptionDragFallback.Session option = None
    let mutable fallbackHwnd = IntPtr.Zero
    let mutable fallbackPressSequence = 0L
    let mutable fallbackCorrections = 0
    // One queued correction at a time; it reads the latest rectangle anyway.
    let mutable fallbackCorrectionQueued = false
    let shellHookWindow = Cell.create(None)
    let winEventHandler = Cell.create(None)
    let isDraggingCell = Cell.create(false)
    let isDraggingExport = Cell.export <| fun() -> isDraggingCell.value
    let zorderExport = Cell.export <| fun() -> zorderCell.value
    let isVisibleCell = Cell.create(false)
    // Tab groups per virtual desktop (Shared/VirtualDesktopGroups.fs). A window
    // shown on all desktops is a member of one group per desktop; this is
    // whether this group is the one of the desktop being looked at, as the
    // main thread's pass last decided, and which desktop it belongs to. Both
    // are written from the main thread and read from this group's thread and
    // its decorator's.
    [<VolatileField>]
    let mutable desktopShown = true
    [<VolatileField>]
    let mutable desktopHomeValue : Guid option = None
    // Whether WindowTabs itself hides the strip because the group is not the
    // one of the desktop being looked at. Most of the time the shell has
    // already hidden it (its owner is on the other desktop), and this stays
    // false; see VirtualDesktopGroups.hideStrip.
    let desktopHiddenCell = Cell.create(false)
    let desktopChangedEvent = Event<unit>()
    // Who each member was when it joined (VirtualDesktopGroups.Identity), so
    // that a handle Windows has given to another window since is not moved
    // along with a window shown on all desktops. This group's thread only.
    let memberIdentities = Dictionary<IntPtr, VirtualDesktopGroups.Identity option>()

    let isMaximizedExport = Cell.export <| fun() ->
        zorderCell.value.tryHead.exists(fun hwnd -> this.os.windowFromHwnd(hwnd).isMaximized)

    let isFullscreenExport = Cell.export <| fun() ->
        zorderCell.value.tryHead.exists(fun hwnd -> this.os.windowFromHwnd(hwnd).isFullscreen)

    let boundsExport = Cell.export <| fun() ->
        placement.value.bind <| fun(rect,placement) -> 
            if isVisibleCell.value then Some(rect) else None

    let isForegroundExport = Cell.export <| fun() ->
        zorderCell.value.any((=) foregroundCell.value)

    let shouldHideTabs () =
        // Read reactive state before settings. Even if a settings read fails,
        // Cell.listen must retain its fullscreen dependency.
        let isFullscreen = isFullscreenExport.value
        let isMoving = inMoveSizeSnapshot
        try
            TabBehaviorPolicy.hideTabs
                (Services.settings.getValue("hideTabsOnFullscreen") :?> bool)
                isFullscreen
                (Services.settings.getValue("hideTabsWhileMoving") :?> bool)
                isMoving
        with _ -> false

    let shouldShowTabStrip () =
        isVisibleCell.value && not (shouldHideTabs()) && not desktopHiddenCell.value

    let updateTabVisibility () =
        match !_ts with
        | Some(ts: TabStrip) -> ts.visible <- shouldShowTabStrip()
        | None -> ()

    // Per-group tab position: always has a concrete value (TopLeft/TopRight)
    let mutable perGroupTabPosition : string = "TopRight"
    // Per-group snap tab height margin: always has a concrete value
    let mutable perGroupSnapTabHeightMargin : bool = false

    // Track the margin-shrunk size for each hwnd, so we know when to compensate on read
    // Key: hwnd, Value: (shrunkWidth, shrunkHeight) that was last applied
    let marginShrunkSizes = Cell.create(Map.empty<IntPtr, (int * int)>)
    // Where each window was before hideChildWindows parked it off screen.
    let mutable parkedBounds : Map<IntPtr, Rect> = Map.empty
    let pendingBackgroundMoves = System.Collections.Generic.Dictionary<IntPtr, int64>()
    let mutable backgroundMoveGeneration = 0L

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

        captionDragEnabled <-
            try Services.settings.getValue("lockWindowPosition") :?> bool
            with _ -> false

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

        // Turning "lock window position" off drops an armed fallback at once,
        // even in the middle of a drag.
        // Changing it in the dialog applies to all groups, as the snap margin
        // does, and overwrites whatever a group was set to from its own menu.
        Services.settings.notifyValue "lockWindowPosition" <| fun value ->
            this.invokeAsync <| fun() ->
                this.applyLockWindowPosition(unbox<bool>(value))
                lockChangedEvent.Trigger()

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
            this.updateStripOwner()

        Cell.listen updateTabVisibility

        // Listen for hideTabsOnFullscreen setting changes
        Services.settings.notifyValue "hideTabsOnFullscreen" <| fun _ ->
            this.invokeAsync updateTabVisibility

        Services.settings.notifyValue "hideTabsWhileMoving" <| fun _ ->
            this.invokeAsync updateTabVisibility

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
        let run () =
            Cell.beginUpdate()
            try f()
            finally Cell.endUpdate()
        // The strip keeps its own cell scope; hold its renders back as well,
        // so everything this update pushes into it is drawn once at the end.
        match !_ts with
        | Some(ts: TabStrip) -> ts.batch run
        | None -> run()

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

    // Let the event's group AND strip batches finish before touching followers.
    // Keep only one queued pass; no HWND, bounds or showCmd is captured here.
    member private this.queueChildPlacement() =
        if not childPlacementPending then
            childPlacementPending <- true
            // Deliberately use the raw invoker: wrapping both phases in
            // this.invokeAsync would hold the strip batch across the slow work.
            invoker.asyncInvoke <| fun () ->
                childPlacementPending <- false
                if isDestroyed.value.not && desktopShown && inMoveSize.value.not then
                    match zorderCell.value.tryHead with
                    | Some(hwnd) ->
                        let window = this.os.windowFromHwnd(hwnd)
                        let bounds = window.bounds
                        if window.isWindow && window.isMinimized.not && window.isInMoveSize.not &&
                           bounds.width > 0 && bounds.height > 0 then
                            // The native window may have changed again before its
                            // throttled event reaches us. Publish that latest state
                            // and flush the strip before applying it to followers.
                            this.withUpdate <| fun () ->
                                this.saveTopWindowPlacement()
                                isMaximizedExport.update()
                                isFullscreenExport.update()
                                updateTabVisibility()
                            this.withUpdate <| fun () ->
                                let isForeground = this.os.foreground.hwnd = hwnd
                                this.adjustChildWindows()
                                if isForeground then this.makeTopWindowForeground()
                                this.foreground <- this.os.foreground.hwnd
                                this.reassertRestoreFront()
                    | None -> ()

    member private this.adjustChildWindows = fun() -> PerfTrace.time "group.adjustChildren" <| fun () ->
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
            let minimizedAtEntry = zorderCell.value.tail.where(isMinimized)
            let siblingsWereMinimized = minimizedAtEntry.isEmpty.not
            // Publish the restore-front target BEFORE un-minimizing the
            // siblings, so adjustWindowPlacement can insert each surfacing
            // sibling directly behind it (no flicker on top).
            if siblingsWereMinimized then
                restoredHwnd |> Option.iter (fun front ->
                    restoreFrontHwnd <- front
                    restoreFrontUntil <- DateTime.Now.AddMilliseconds(1500.0))
            // Capture the final group rectangle BEFORE posting any moves.
            // The second pass must not keep a sibling's still-old position.
            let topWindow = this.os.windowFromHwnd(zorderCell.value.head)
            let liveBounds = topWindow.bounds
            let backgroundBounds =
                if topWindow.isMaximized || liveBounds.width <= 0 || liveBounds.height <= 0 then None
                elif this.hasWindowMargin(topWindow.hwnd) then Some(this.removeWindowMarginForRead(topWindow.hwnd, liveBounds))
                else Some liveBounds
            let queuedAsync = System.Collections.Generic.HashSet<IntPtr>()
            zorderCell.value.tail.iter(fun hwnd ->
                if this.adjustWindowPlacementCore(hwnd, backgroundBounds) then queuedAsync.Add(hwnd) |> ignore)

            // After initial placement, adjust sizes again to ensure DPI is considered
            match zorderCell.value.tryHead with
            | Some(topHwnd) ->
                let topWindow = this.os.windowFromHwnd(topHwnd)
                let topBounds = topWindow.bounds
                // When the top window is maximized, its bounds already match the
                // monitor work rect — skip detected frame margin on both sides.
                let topMaximized = topWindow.isMaximized

                // If the top window has a margin, always expand to get group bounds
                let groupBounds =
                    if this.hasWindowMargin(topHwnd) && not topMaximized then
                        this.removeWindowMarginForRead(topHwnd, topBounds)
                    else topBounds

                // Move all background windows again with the correct size
                zorderCell.value.tail.iter(fun hwnd ->
                    let window = this.os.windowFromHwnd(hwnd)
                    // A sibling that was minimized when this pass began is
                    // being un-minimized and moved ASYNCHRONOUSLY by the first
                    // pass. Read now, it may already be un-minimized but still
                    // at the iconic position (-32000,-32000); keeping "its"
                    // position with the corrected size then pinned it there
                    // for good, and the periodic scan later threw it out of
                    // the group as being on no screen - a tab and its window
                    // gone together. Its move is already queued with the right
                    // size: leave it alone.
                    if BackgroundPlacementPolicy.needsSecondPass
                        (queuedAsync.Contains hwnd) (minimizedAtEntry.contains((=) hwnd)) window.isMinimized then
                        // Apply detected frame margin for this background window
                        let targetBounds =
                            if topMaximized then groupBounds
                            else this.applyWindowMarginForWrite(hwnd, groupBounds)
                        let currentBounds = window.bounds
                        // Keep current position but use correct size - unless
                        // the position is the iconic one, which is nowhere.
                        let atIconicPosition = currentBounds.x <= -30000 || currentBounds.y <= -30000
                        let correctBounds =
                            if atIconicPosition then targetBounds
                            else Rect(currentBounds.location, targetBounds.size)
                        // Skip the move if size already matches - SetWindowPos is expensive and apps that
                        // fire EVENT_OBJECT_LOCATIONCHANGE without actually moving (e.g. LibreOffice) would
                        // otherwise trigger redundant work and follow-up events on every spurious change.
                        if currentBounds.size <> correctBounds.size || atIconicPosition then
                            // Async (SWP_ASYNCWINDOWPOS) so a busy just-restored
                            // app can't stall the strip thread here; z-order is
                            // untouched so this can't disturb the fronting done
                            // afterwards.
                            if siblingsWereMinimized then window.moveAsync(correctBounds)
                            else PerfTrace.time "group.follower.secondBounds" <| fun () -> window.move(correctBounds)
                        // Track the margin-shrunk size for this window
                        if this.hasWindowMargin(hwnd) && not topMaximized then
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

    // While the top window is in a move/size loop the other windows of the
    // group are parked outside every monitor, and adjustChildWindows brings
    // them back with it. That path does nothing when the top window has no
    // usable rectangle just then (minimized, or being torn down), and windows
    // parked in that moment were left outside the desktop with no way back -
    // which is how a whole group of windows disappeared. Where each of them
    // came from is remembered here so they can be put back regardless.
    member private this.hideChildWindows() =
        zorderCell.value.tail.where(isMinimized >> not).iter(fun hwnd ->
            followerPlacements.Synchronous(hwnd, ignore)
            let window = this.os.windowFromHwnd(hwnd)
            (try
                let bounds = window.bounds
                if bounds.width > 0 && bounds.height > 0 then
                    parkedBounds <- parkedBounds.Add(hwnd, bounds)
             with _ -> ())
            // Parking restores maximized followers before moving them away.
            this.withoutTransitions(hwnd, fun() ->
                // hideOffScreen uses SW_RESTORE for maximized windows, which
                // would activate a follower before it is parked.
                if window.isMaximized then
                    Win32Helper.RestoreWindowNoActivate(hwnd) |> ignore
                // If the style change is refused, retain the native restore/park path.
                window.hideOffScreen(None)))

    /// Puts back anything hideChildWindows parked that is still outside every
    /// monitor. Called after the move/size loop, whatever adjustChildWindows
    /// decided to do.
    member private this.restoreParkedWindows() =
        for hwnd in parkedBounds |> Map.toList |> List.map fst do
            this.restoreParkedWindow hwnd

    /// Puts one window back from where hideChildWindows parked it, if it is
    /// still outside every monitor, and forgets the park.
    member private this.restoreParkedWindow(hwnd: IntPtr) =
        match parkedBounds.TryFind hwnd with
        | None -> ()
        | Some(home) ->
            parkedBounds <- parkedBounds.Remove hwnd
            try
                let window = this.os.windowFromHwnd(hwnd)
                if window.isWindow && not window.isMinimized then
                    let bounds = window.bounds
                    let onScreen =
                        Mon.all.any(fun mon ->
                            let r = mon.displayRect
                            bounds.x < r.x + r.width && bounds.x + bounds.width > r.x &&
                            bounds.y < r.y + r.height && bounds.y + bounds.height > r.y)
                    if not onScreen then window.move(home)
            with _ -> ()

    member private this.inZorder(windows:List2<IntPtr>) = this.windows.items.sortBy(fun hwnd -> this.os.windowFromHwnd(hwnd).zorder)

    member private this.setZorder(newZorder:List2<_>) =
        if zorderCell.value.list <> newZorder.list then
            prevTop.set(zorderCell.value.tryHead)
            zorderCell.set(newZorder)

    member private this.saveZorder() =
        this.setZorder(this.inZorder(this.windows.items))

    member private this.setWindows(newWindows: Set2<IntPtr>) =
        // Publish membership without cross-thread service calls in the input
        // hook. A group that is not locked registers nothing: the hook is
        // installed only while some window is registered. Nor does a group of
        // another virtual desktop: a window shown on all desktops is in it as
        // well as in the group of the desktop being looked at, and only that
        // group's lock is the one the person can see.
        let register = captionDragEnabled && desktopShown
        this.windows.items.iter (fun hwnd ->
            if not (newWindows.contains hwnd) || not register then
                CaptionDragTargets.remove captionDragOwner hwnd)
        if register then newWindows.items.iter (CaptionDragTargets.add captionDragOwner)
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
            previewSize = fun() ->
                let size = if window.isMinimized then this.placementBounds.size else window.bounds.size
                if size.isEmptyArea then Sz(1, 1) else size
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
                        DragTrace.activation (fun () -> "preview.beforePrintWindow") hwnd
                        try Img(Win32Helper.PrintWindow(hwnd))
                        finally DragTrace.activation (fun () -> "preview.afterPrintWindow") hwnd
                with ex -> Img(Sz(1, 1))
        }
    
    member private this.setTabInfo(hwnd) =
        let info = this.getTabInfo(hwnd)
        TitleTrace.log (fun () -> sprintf "setTabInfo hwnd=%X text=[%s]" (hwnd.ToInt64()) info.text)
        this.ts.setTabInfo(Tab(hwnd), info)

    // Once a second from the group's timer. A tab follows its window's title
    // on EVENT_OBJECT_NAMECHANGE, but not every application raises it:
    // PowerPoint renames its window on "Save As" without one, and the tab kept
    // the old file name. Reading a title is cheap - GetWindowText answers from
    // the cached text for another process's window, without asking it.
    member this.refreshTitles() =
        for hwnd in this.windows.items.list do
            let shown = try this.ts.tabInfo(Tab(hwnd)).text with _ -> null
            let actual = try this.hwndText hwnd with _ -> null
            if not (isNull shown) && not (isNull actual) && shown <> actual then
                TitleTrace.log (fun () ->
                    let window = this.os.windowFromHwnd(hwnd)
                    sprintf "MISSED hwnd=%X class=%s tab=[%s] window=[%s]"
                        (hwnd.ToInt64()) (try window.className with _ -> "?") shown actual)
                PerfTrace.count "title.missed"
                this.setTabInfo hwnd

    member private this.setTsParent(parentHwnd) =
        this.os.windowFromHwnd(this.ts.hwnd).setParent(this.os.windowFromHwnd(parentHwnd))

    // The window that owns the strip. The shell shows and hides an owned tool
    // window with its owner, so the strip of a group holding a window shown
    // on all desktops is owned by one of its windows that is not: it then
    // disappears in the same frame as its desktop does, instead of staying on
    // screen with the pinned window until WindowTabs notices the switch. With
    // no such window about, it is the front window, as it always was.
    member private this.stripOwnerHwnd =
        if this.isEmpty then IntPtr.Zero
        else
            let state = VirtualDesktopGroups.Live.snapshot()
            VirtualDesktopGroups.stripOwnerHere zorderCell.value.list (VirtualDesktopGroups.Live.shared())
                (VirtualDesktopGroups.Live.isHere state)
            |> Option.defaultValue zorderCell.value.head

    member private this.updateStripOwner() =
        let owner = this.stripOwnerHwnd
        this.setTsParent(owner)
        this.keepStripAboveTop(owner)

    // Owned by a window that is not the front one, the strip sits just above
    // its owner - behind the front window. It is put back just above the front
    // window, where an owned strip would have been.
    member private this.keepStripAboveTop(owner: IntPtr) =
        if owner <> IntPtr.Zero && not this.isEmpty then
            let top = zorderCell.value.head
            if top <> owner then
                try
                    let above = this.os.windowFromHwnd(top).prevZorder
                    if above.hwnd <> this.ts.hwnd then
                        this.os.windowFromHwnd(this.ts.hwnd).insertAfter(above)
                with _ -> ()

    // The tabs of the windows that are on another desktop are drawn dimmed.
    // The front tab is never one of them: when the front window is elsewhere,
    // the frontmost window that is here takes its place in the group's own
    // order. No window is touched - raising one of another desktop would
    // switch to it.
    member private this.updateDimmedTabs(shown: bool) =
        let state = VirtualDesktopGroups.Live.snapshot()
        let away =
            if this.isEmpty || not (VirtualDesktopGroups.Live.isFresh state) then []
            else
                VirtualDesktopGroups.dimmed shown
                    (fun h -> (VirtualDesktopGroups.Live.read state h).presence) zorderCell.value.list
        if not away.IsEmpty && List.contains zorderCell.value.head away then
            zorderCell.value.list
            |> List.tryFind (VirtualDesktopGroups.Live.isHere state)
            |> Option.iter this.bringToTop
        this.ts.setDimmedTabs(away |> List.map Tab)

    /// Called by the main thread's desktop pass (through GroupInfo): whether
    /// this group is the one of the desktop being looked at. Re-chooses the
    /// strip's owner (the set of windows shown everywhere may have changed),
    /// and hides the strip only where the shell has not already done so.
    member this.applyDesktopState(shown: bool) =
        desktopShown <- shown
        this.updateDimmedTabs(shown)
        this.updateStripOwner()
        let owner = this.stripOwnerHwnd
        let ownerPresence =
            if owner = IntPtr.Zero then VirtualDesktopGroups.Unsure
            else (VirtualDesktopGroups.Live.read (VirtualDesktopGroups.Live.snapshot()) owner).presence
        let hide =
            VirtualDesktopGroups.hideStrip
                (if shown then VirtualDesktopGroups.Shown else VirtualDesktopGroups.Hidden) ownerPresence
        if desktopHiddenCell.value <> hide then desktopHiddenCell.set(hide)
        this.syncCaptionDragTargets()
        if shown then
            // What a group of another desktop did not keep up with.
            this.updateIsVisible()
            this.foreground <- this.os.foreground.hwnd
        desktopChangedEvent.Trigger()

    member this.isDesktopShownThreadSafe = desktopShown
    // Set at once from the main thread, ahead of applyDesktopState, so that
    // nothing asking in between is told the old answer.
    member this.markDesktopShown(shown: bool) = desktopShown <- shown
    member this.desktopHome
        with get() = desktopHomeValue
        and set(value: Guid option) = desktopHomeValue <- value
    member this.desktopChanged = desktopChangedEvent.Publish

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

    member this.lockWindowPosition
        with get() = captionDragEnabled
        and set(value) =
            this.applyLockWindowPosition(value)
            lockChangedEvent.Trigger()

    // Registering the windows is what turns the hook on for them, so the
    // registration has to follow the setting both ways. An armed fallback is
    // dropped at once when the lock goes off, even in the middle of a drag.
    member private this.applyLockWindowPosition(value: bool) =
        captionDragEnabled <- value
        this.syncCaptionDragTargets()
        if not value then this.endCaptionDragFallback "lock turned off"

    member private this.syncCaptionDragTargets() =
        if captionDragEnabled && desktopShown then this.windows.items.iter (CaptionDragTargets.add captionDragOwner)
        else this.windows.items.iter (CaptionDragTargets.remove captionDragOwner)

    member this.hwnd = this.ts.hwnd

    member private this.os : OS = _os
       
    member private this.windowCount = this.windows.count

    member this.placementBounds : Rect = placement.value.map(fst).def(Rect())

    member private this.isTop(hwnd) = zorderCell.value.where(isMinimized >> not).tryHead = Some(hwnd)

    member private this.saveTopWindowPlacement() = PerfTrace.time "group.saveTopPlacement" <| fun () ->
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
            // Owned resize frames lie outside the native rectangle.
            // Skip when maximized: bounds already match the work rect.
            let adjustedBounds =
                if this.hasWindowMargin(window.hwnd) && not window.isMaximized then
                    this.removeWindowMarginForRead(window.hwnd, bounds)
                else bounds
            maximizedFrameBounds <- if window.isMaximized then Some liveBounds else None
            placement.set(Some(adjustedBounds, window.placement))
           
    member private this.waitForDpiChange(hwnd: IntPtr, initialDpi: uint32, maxWaitMs: int) = PerfTrace.time "group.follower.waitDpi" <| fun () ->
        let mutable currentDpi = initialDpi
        let mutable elapsed = 0
        let checkInterval = 10 // Check every 10ms
        
        while elapsed < maxWaitMs && currentDpi = initialDpi do
            System.Threading.Thread.Sleep(checkInterval)
            elapsed <- elapsed + checkInterval
            currentDpi <- WinUserApi.GetDpiForWindow(hwnd)
            
        currentDpi <> initialDpi // Return true if DPI changed

    // Common method to apply window bounds with DPI-aware logic
    member private this.applyWindowBoundsWithDpiHandling(hwnd:IntPtr, bounds:Rect) = PerfTrace.time "group.follower.dpiBounds" <| fun () ->
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
            PerfTrace.time "group.follower.position" <| fun () -> window.setPositionOnly bounds.x bounds.y

            // Wait for DPI change (max 200ms)
            if this.waitForDpiChange(hwnd, currentDpi, 200) then
                // DPI changed, wait a bit more for stabilization
                System.Threading.Thread.Sleep(20)

            // Apply final position with size
            PerfTrace.time "group.follower.move" <| fun () -> window.move(bounds)
        else
            // Same DPI: move with position and size at once for better performance
            PerfTrace.time "group.follower.move" <| fun () -> window.move(bounds)

    // The position-first DPI protocol is retained, but its wait is a timer,
    // not a sleep on the group thread. Tickets prevent an old correction from
    // moving a detached member or overwriting a newer placement.
    member private this.applyBackgroundWindowBounds(hwnd: IntPtr, bounds: Rect) =
        let window = this.os.windowFromHwnd(hwnd)
        backgroundMoveGeneration <- backgroundMoveGeneration + 1L
        let ticket = backgroundMoveGeneration
        pendingBackgroundMoves.[hwnd] <- ticket
        // restoreParkedWindows runs immediately after this pass. The queued
        // target replaces the parked location; do not restore the old home.
        parkedBounds <- parkedBounds.Remove hwnd
        let isCurrent() =
            match pendingBackgroundMoves.TryGetValue hwnd with
            | true, current when current = ticket -> true
            | _ -> false
        let valid() =
            isCurrent() && this.windows.contains(hwnd) && this.isSameWindow(hwnd) &&
            desktopShown && not inMoveSize.value && not window.isMinimized && not window.isMaximized && not window.isInMoveSize &&
            not ((VirtualDesktopGroups.Live.shared()).Contains hwnd)
        let completed() =
            if isCurrent() then pendingBackgroundMoves.Remove(hwnd) |> ignore
#if DEBUG
        let mutable counted = false
        let count() =
            if not counted then
                counted <- true
                PerfTrace.count "group.adjustAsync"
#else
        let count() = ()
#endif
        let targetDpi = WinUserApi.GetDpiForWindow(zorderCell.value.head)
        let watch = System.Diagnostics.Stopwatch.StartNew()
        let later delay f =
            ThreadHelper.cancelablePostBack delay (fun () -> this.invokeAsync f) |> ignore
        BackgroundPlacementPolicy.move
            (fun () -> WinUserApi.GetDpiForWindow(hwnd)) targetDpi
            (fun () -> watch.ElapsedMilliseconds) later valid
            (fun () -> count(); window.setPositionOnlyAsync bounds.x bounds.y)
            (fun () -> count(); window.moveAsync(bounds)) completed

    // Cache-only reads: geometry, hotkeys and strip placement never scan windows.
    member this.getWindowMargin(hwnd:IntPtr, bounds:Rect) =
        let dpi = try int (Math.Round(Dpi.scaleForRect bounds * 96.0)) with _ -> 96
        frameMargins.Get(hwnd, dpi)

    member this.hasWindowMargin(hwnd:IntPtr) =
        let (top, left, right, bottom) = frameMargins.Get(hwnd, 96)
        top <> 0 || left <> 0 || right <> 0 || bottom <> 0

    member private this.refreshWindowMargin(hwnd) =
        frameMargins.Refresh(hwnd, fun candidate ->
            // Our strip and guard are owned popups too. Neither is an app frame.
            candidate = this.ts.hwnd || TopEdgeGuardPlacement.isGuard candidate)

    // The front member plus one rotating follower per existing upkeep tick
    // discovers late frames without global hooks or scans in painting.
    member this.refreshWindowMargins() = this.withUpdate <| fun () ->
        if not inMoveSize.value && not this.isEmpty then
            let members = this.windows.items.list
            let hwnd = members.[marginPollIndex % members.Length]
            marginPollIndex <- (marginPollIndex + 1) % members.Length
            let targets = if hwnd = this.topWindow then [hwnd] else [this.topWindow; hwnd]
            for target in targets do
                if this.refreshWindowMargin(target) then
                    if target = this.topWindow then this.saveTopWindowPlacement()
                    else this.adjustWindowPlacement(target)

    // Record that margin was applied to a window (for tracking shrunk state)
    member this.recordMarginApplied(hwnd:IntPtr, width:int, height:int) =
        marginShrunkSizes.set(marginShrunkSizes.value.Add(hwnd, (width, height)))

    // Apply margin when writing bounds to a window (shrink by margin)
    // Left/Top: +margin (move inward), Width/Height: -(left+right)/(top+bottom)
    // Result: window becomes smaller by margin on each side
    member this.applyWindowMarginForWrite(hwnd:IntPtr, bounds:Rect) : Rect =
        if this.hasWindowMargin(hwnd) then
            let (top, left, right, bottom) = this.getWindowMargin(hwnd, bounds)
            let result = Rect(Pt(bounds.x + left, bounds.y + top),
                              Sz(bounds.width - left - right, bounds.height - top - bottom))
            result
        else bounds

    // Apply reverse margin when reading bounds from a foreground window (expand by margin)
    // Left/Top: -margin (move outward), Width/Height: +(left+right)/(top+bottom)
    // Result: reported bounds become larger by margin on each side
    member private this.removeWindowMarginForRead(hwnd:IntPtr, bounds:Rect) : Rect =
        if this.hasWindowMargin(hwnd) then
            let (top, left, right, bottom) = this.getWindowMargin(hwnd, bounds)
            let result = Rect(Pt(bounds.x - left, bounds.y - top),
                              Sz(bounds.width + left + right, bounds.height + top + bottom))
            result
        else bounds

    member private this.adjustWindowPlacement(hwnd) =
        this.adjustWindowPlacementCore(hwnd, None) |> ignore

    member private this.adjustWindowPlacementCore(hwnd, backgroundBounds: Rect option) = PerfTrace.time "group.adjustOne" <| fun () ->
        pendingBackgroundMoves.Remove(hwnd) |> ignore
        let mutable queuedAsync = false
        let window = this.os.windowFromHwnd(hwnd)
        if placement.value.IsSome then
            let bounds,wp = placement.value.Value
#if DEBUG
            let sourceState = window.placement.showCmd
            if sourceState <> wp.showCmd then
                InputStallTrace.context
                    (if wp.showCmd = ShowWindowCommands.SW_SHOWMAXIMIZED then InputStallTrace.Kind.Maximize
                     elif wp.showCmd = ShowWindowCommands.SW_SHOWMINIMIZED ||
                          wp.showCmd = ShowWindowCommands.SW_MINIMIZE ||
                          wp.showCmd = ShowWindowCommands.SW_SHOWMINNOACTIVE then InputStallTrace.Kind.Minimize
                     else InputStallTrace.Kind.Restore) hwnd 0.0
#endif
            let sourceNormal = window.placement.showCmd = ShowWindowCommands.SW_SHOWNORMAL
            let useAsync = backgroundBounds.IsSome && desktopShown &&
                            BackgroundPlacementPolicy.useAsync true
                                (hwnd <> this.os.foreground.hwnd && not (this.isTop hwnd))
                                (window.tid <> WinBaseApi.GetCurrentThreadId()) sourceNormal
                                (wp.showCmd = ShowWindowCommands.SW_SHOWNORMAL)
                                ((VirtualDesktopGroups.Live.shared()).Contains hwnd)
            let bounds = if useAsync then backgroundBounds.Value else bounds
            // Skip detected frame margin when target is maximized: bounds already match the work rect.
            let targetMaximized = wp.showCmd = ShowWindowCommands.SW_SHOWMAXIMIZED
            let adjustedBounds =
                if targetMaximized then bounds
                else this.applyWindowMarginForWrite(hwnd, bounds)
            let followerBounds =
                if targetMaximized then maximizedFrameBounds |> Option.defaultValue adjustedBounds
                else adjustedBounds
            let mutable nativeBounds = followerBounds.RECT
            let source = zorderCell.value.tryHead
            let canPost =
#if DEBUG
                not StallExperiment.syncFollowers &&
#endif
                not (synchronousFollowers.Contains hwnd) && desktopShown && source.IsSome &&
                source.Value <> hwnd && hwnd <> this.os.foreground.hwnd && window.isVisible &&
                not window.isMinimized &&
                (targetMaximized || wp.showCmd = ShowWindowCommands.SW_SHOWNORMAL) &&
                (targetMaximized || window.isMaximized || followerPlacements.HasPending(hwnd)) &&
                not ((VirtualDesktopGroups.Live.shared()).Contains hwnd) &&
                WinUserApi.GetDpiForWindow(hwnd) <> 0u &&
                WinUserApi.GetDpiForWindow(hwnd) = WinUserApi.GetDpiForWindow(source.Value) &&
                WinUserApi.MonitorFromWindow(hwnd, MonitorFlags.MONITOR_DEFAULTTONEAREST) =
                    WinUserApi.MonitorFromRect(&nativeBounds, MonitorFlags.MONITOR_DEFAULTTONEAREST)
            if canPost then
                queuedAsync <- true
                followerPlacements.Post(hwnd, source.Value, this.os.windowFromHwnd(source.Value).bounds,
                    targetMaximized, followerBounds, fun revision ->
                        // Do not hold a strip batch across the fallback. As in
                        // queueChildPlacement, publish/flush the live top state
                        // before touching any follower.
                        invoker.asyncInvoke <| fun () ->
                            if isDestroyed.value.not && desktopShown && inMoveSize.value.not && this.windows.contains(hwnd) &&
                               not (this.isTop hwnd) && hwnd <> this.os.foreground.hwnd &&
                               followerPlacements.IsCurrent(hwnd, revision) then
                                match zorderCell.value.tryHead with
                                | Some top ->
                                    let sourceWindow = this.os.windowFromHwnd(top)
                                    let liveBounds = sourceWindow.bounds
                                    if sourceWindow.isWindow && sourceWindow.isMinimized.not && sourceWindow.isInMoveSize.not &&
                                       liveBounds.width > 0 && liveBounds.height > 0 then
                                        this.withUpdate <| fun () ->
                                            this.saveTopWindowPlacement()
                                            isMaximizedExport.update()
                                            isFullscreenExport.update()
                                            updateTabVisibility()
                                        // One correction per current lane revision.
                                        // Pending posts coalesce in the lane; a newer
                                        // revision invalidates this callback. Forcing
                                        // synchronous placement prevents a retry loop
                                        // even if the source keeps changing.
                                        this.withUpdate <| fun () ->
                                            synchronousFollowers.Add(hwnd) |> ignore
                                            try this.adjustWindowPlacementCore(hwnd, None) |> ignore
                                            finally synchronousFollowers.Remove(hwnd) |> ignore
                                | None -> ()) |> ignore
            else followerPlacements.Synchronous(hwnd, fun () ->
                //if you remove this check, then when you drag a window into an Aero Snapp'ed window
                //the dragged in window will be placed at the restore location for the target, instead of
                //at its snapped location - this is because GetWindowPlacement rcNormal is the restore
                //location for snapped windows
                if  wp.showCmd = ShowWindowCommands.SW_SHOWNORMAL &&
                    window.placement.showCmd = ShowWindowCommands.SW_SHOWNORMAL
                    then
                    if useAsync then
                        queuedAsync <- true
                        this.applyBackgroundWindowBounds(hwnd, adjustedBounds)
                    else this.applyWindowBoundsWithDpiHandling(hwnd, adjustedBounds)
                else
                    if window.isMinimized && not targetMaximized then
                        // Un-minimize and reposition asynchronously so busy apps do
                        // not stall the strip. Keep transitions disabled until the
                        // queued operations have had time to run.
                        this.disableTransitions(hwnd)
                        try
                            window.showWindowAsync(ShowWindowCommands.SW_SHOWNOACTIVATE)
                            window.moveAsync(adjustedBounds)
                            // Post the sibling directly behind the restore-front window
                            // so it surfaces already below it instead of flickering on
                            // top. Ordered after the show on the target's queue.
                            if restoreFrontHwnd <> IntPtr.Zero && restoreFrontHwnd <> hwnd then
                                window.insertAfterAsync(restoreFrontHwnd)
                            ThreadHelper.cancelablePostBack 500 (fun() -> this.enableTransitions(hwnd)) |> ignore
                        with _ ->
                            // No delayed cleanup is guaranteed if posting fails.
                            this.enableTransitions(hwnd)
                            reraise()
                    elif not window.isMinimized &&
                         (targetMaximized || wp.showCmd = ShowWindowCommands.SW_SHOWNORMAL) then
                        // Native show commands raise followers even with transitions
                        // disabled. Apply WS_MAXIMIZE and the final frame/bounds as
                        // one position change, retaining z-order and activation.
                        this.withoutTransitions(hwnd, fun() ->
                            let followerBounds =
                                if targetMaximized then maximizedFrameBounds |> Option.defaultValue adjustedBounds
                                else adjustedBounds
                            if Win32Helper.SetWindowMaximizedNoActivate(hwnd, targetMaximized, followerBounds.RECT,
                                    Win32Helper.PlacementTiming(fun name ms -> PerfTrace.recordTime name ms), null) then
                                // Correct any app/DPI adjustment, or move an already
                                // matching state. This is a no-op when bounds match.
                                this.applyWindowBoundsWithDpiHandling(hwnd, followerBounds)
                            else
                                // A blocked or refused style change must not stop
                                // the follower. Retain the native placement fallback.
                                if targetMaximized then
                                    this.applyWindowBoundsWithDpiHandling(hwnd, adjustedBounds)
                                PerfTrace.time "group.follower.setPlacement" <| fun () -> window.setPlacement(wp))
                    else
                        // Keep native placement for minimized followers; their
                        // restore bookkeeping must still be performed by Windows.
                        this.withoutTransitions(hwnd, fun() ->
                            if targetMaximized then
                                // Placement alone cannot move a maximized window
                                // between monitors. Suppress the preparatory move too.
                                this.applyWindowBoundsWithDpiHandling(hwnd, adjustedBounds)
                            PerfTrace.time "group.follower.setPlacement" <| fun () -> window.setPlacement(wp))

            )
            // Track the margin-shrunk size for this window
            if this.hasWindowMargin(hwnd) && not targetMaximized then
                marginShrunkSizes.set(marginShrunkSizes.value.Add(hwnd, (adjustedBounds.width, adjustedBounds.height)))

        queuedAsync

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
        
    member this.shellEvents(hwnd, evt) = this.invokeAsync <| fun() -> PerfTrace.time (sprintf "group.shell.%O" evt) <| fun () ->
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
        

    // ----- Lock window position: MOVESIZESTART fallback -----
    //
    // CaptionDragPlugin swallows a press on a managed window's caption, so no
    // move loop starts (level 1). A loop that starts anyway came from a press
    // the hook let through - a custom title bar answering HTCLIENT, a child
    // window, a hit test that timed out, an app starting the loop itself - and
    // here the window is put back on every rectangle change for as long as the
    // loop runs (level 2). The decisions are in CaptionDragFallback.
    //
    // Scope:
    //  - Only a loop the hook saw the press for. Keyboard move/size
    //    (Alt+Space) and touch/pen drags that bypass the mouse hook are not
    //    locked; neither is a window that enters a loop before WindowTabs
    //    watches it (a Chrome tab torn off into a new window).
    //  - A maximized window's drag-to-restore is not undone here (undoing it
    //    means maximizing again, not moving back). The hook blocks it for
    //    standard captions; for custom ones it passes.
    //  - Moves that never enter a loop - the tab strip's drag and drop, its
    //    Move/Snap menu commands, workspace restore, Win+arrow, siblings
    //    following the top window - never arm anything.

    member private this.fallbackNowMs = Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency

    member private this.fallbackBoxOf (r: Rect) = CaptionDragFallback.box r.x r.y r.width r.height

    // Called from the WinEventProc of EVENT_SYSTEM_MOVESIZESTART, before the
    // event is queued: the held-button test must see the button as it is
    // when the loop starts. It sends no message to the window, so the
    // callback cannot be re-entered while it waits. The result travels with
    // the queued event. Costs one field read while the setting is off.
    member private this.captureCaptionDragFallback(hwnd: IntPtr) =
        if not captionDragEnabled || not (this.isTop hwnd) then None
        else
            match CaptionDragTargets.currentPress() with
            | Some press when press.hwnd = hwnd ->
                let window = this.os.windowFromHwnd(hwnd)
                if not window.isWindow || window.isMinimized || window.isMaximized then None
                else
                    let held = Win32Helper.IsKeyPressed(VirtualKeyCodes.VK_LBUTTON)
                    let current = this.fallbackBoxOf window.bounds
                    if not (CaptionDragFallback.pressApplies held (Environment.TickCount - press.tick) press.bounds current) then None
                    else
                        let band = CaptionDragFallback.edgeBand (int (WinUserApi.GetDpiForWindow(hwnd)))
                        Some(press, CaptionDragFallback.grabOfPress press.rootHit press.bounds press.x press.y band)
            | _ -> None

    member private this.endCaptionDragFallback(reason: string) =
        if fallbackSession.IsSome && fallbackCorrections > 0 then
            Debug.WriteLine(sprintf "[CaptionDrag] fallback ended hwnd=%X corrections=%d (%s)" (int64 fallbackHwnd) fallbackCorrections reason)
        fallbackSession <- None
        fallbackCorrections <- 0

    // Queued START handler. A new loop always replaces a session that is
    // still settling from the previous one.
    member private this.beginCaptionDragFallback(hwnd: IntPtr, armed: (CaptionDragTargets.Press * CaptionDragFallback.Grab) option) =
        this.endCaptionDragFallback "new loop"
        match armed with
        | Some(press, grab) when captionDragEnabled ->
            match CaptionDragFallback.start grab press.bounds with
            | Some(session) ->
                fallbackSession <- Some(session)
                fallbackHwnd <- hwnd
                fallbackPressSequence <- press.sequence
                Debug.WriteLine(sprintf "[CaptionDrag] fallback armed hwnd=%X grab=%A rootHit=%A" (int64 hwnd) grab press.rootHit)
            | None -> ()
        | _ -> ()

    member private this.finishCaptionDragFallback() =
        match fallbackSession with
        | Some(session) ->
            match CaptionDragFallback.finish this.fallbackNowMs session with
            | Some(settling) -> fallbackSession <- Some(settling)
            | None -> this.endCaptionDragFallback "loop ended"
        | None -> ()

    member private this.isCaptionDragFallbackArmedFor(hwnd: IntPtr) =
        fallbackSession.IsSome && fallbackHwnd = hwnd

    // Observe the armed window once and put it back if the drag moved it.
    // Runs for its LOCATIONCHANGEs (see addWindow) and once more at
    // MOVESIZEEND, always before the placement the tab strip and the sibling
    // windows follow is saved.
    member private this.enforceCaptionDragFallback(hwnd: IntPtr) =
        match fallbackSession with
        | Some(session) when fallbackHwnd = hwnd ->
            if not captionDragEnabled || not (this.windows.contains hwnd) || not (this.isTop hwnd) then
                this.endCaptionDragFallback "window left the top of the group"
            else
                let window = this.os.windowFromHwnd(hwnd)
                let isNormal = window.isWindow && not window.isMinimized && not window.isMaximized
                let newerPress = CaptionDragTargets.pressSequence() <> fallbackPressSequence
                let next, correction =
                    CaptionDragFallback.observe this.fallbackNowMs isNormal newerPress session (this.fallbackBoxOf window.bounds)
                if correction <> CaptionDragFallback.NoCorrection then
                    if fallbackCorrections = 0 then
                        let count = CaptionDragTargets.noteFallback()
                        Debug.WriteLine(sprintf "[CaptionDrag] fallback engaged #%d hwnd=%X grab=%A" count (int64 hwnd) session.grab)
                    fallbackCorrections <- fallbackCorrections + 1
                match next with
                | Some(s) -> fallbackSession <- Some(s)
                | None -> this.endCaptionDragFallback "observed the end"
                // Synchronous, like every other placement call here: the
                // window's thread is pumping inside its move loop. The move it
                // causes comes back as a LOCATIONCHANGE that matches the anchor.
                match correction with
                | CaptionDragFallback.NoCorrection -> ()
                | CaptionDragFallback.RestorePosition(x, y) -> window.setPositionOnly x y
                | CaptionDragFallback.RestoreBounds(b) -> window.move(Rect(Pt(b.x, b.y), Sz(b.width, b.height)))
        | Some(_) ->
            // Another window of the group changed. If the armed one is no
            // longer on top (a tab switch mid-drag), nothing is left to hold.
            if not (this.isTop fallbackHwnd) then this.endCaptionDragFallback "tab switched"
        | None -> ()

    member this.onEnterMoveSize() =
        inMoveSizeSnapshot <- true
        inMoveSize.set(true)
        updateTabVisibility()
        this.hideChildWindows()
        this.saveTopWindowPlacement()
        this.updateIsVisible()

    member this.onExitMoveSize() =
        // However the loop ended - MOVESIZEEND, or CASE 777 where the window
        // leaves the group inside it - the fallback settles or goes.
        this.finishCaptionDragFallback()
        inMoveSizeSnapshot <- false
        inMoveSize.set(false)
        this.saveTopWindowPlacement()
        this.adjustChildWindows()
        this.restoreParkedWindows()
        this.makeTopWindowForeground()
        this.updateIsVisible()
        updateTabVisibility()

    // Thread-safe version for cross-thread reads (reads from volatile snapshot)
    member this.isInMoveSizeThreadSafe = inMoveSizeSnapshot

    member this.shouldShowTabs = shouldShowTabStrip()

    // ----- A group of another virtual desktop -----
    //
    // Its windows that live on that desktop are not in anyone's hands right
    // now, and nothing they do may reach the desktop being looked at. Its
    // windows shown on all desktops are, and what is done to them here is
    // done to them in this group as well - a move, a maximize, a minimize or
    // a restore - so that the group is found as it should be when its desktop
    // is looked at again, and switching desktops never has to move anything.
    // Nothing here activates a window or touches the z-order: that would
    // switch the desktop.
    member private this.backgroundEvent(hwnd, evt) =
        let isSharedMember =
            this.windows.contains(hwnd) &&
            VirtualDesktopGroups.adoptsMoveOf false ((VirtualDesktopGroups.Live.shared()).Contains hwnd) false
        match evt with
        | WinEvent.EVENT_OBJECT_NAMECHANGE ->
            TitleTrace.log (fun () -> sprintf "namechange(background) hwnd=%X member=%b" (hwnd.ToInt64()) (this.windows.contains(hwnd)))
            if this.windows.contains(hwnd) then this.setTabInfo hwnd
        | WinEvent.EVENT_SYSTEM_FOREGROUND ->
            this.foreground <- hwnd
        | WinEvent.EVENT_OBJECT_LOCATIONCHANGE
        | WinEvent.EVENT_SYSTEM_MOVESIZEEND when isSharedMember ->
            this.followSharedWindow(hwnd)
        | WinEvent.EVENT_SYSTEM_MINIMIZESTART when isSharedMember ->
            if this.consumeMinMaxEcho(hwnd, evt) then this.updateIsVisible()
            else this.followSharedMinimize(hwnd)
        | WinEvent.EVENT_SYSTEM_MINIMIZEEND when isSharedMember ->
            this.followSharedRestore(hwnd)
        | _ -> ()

    /// Takes the rectangle a member was given somewhere else as the group's,
    /// and puts the other members there - as the group would if the member
    /// had been dragged while in front. Not while it is being dragged (the
    /// end of the drag brings it here once), not minimized, not parked off
    /// every monitor (a group parks its other windows there while its front
    /// window is dragged).
    member private this.followSharedWindow(hwnd) =
        let window = this.os.windowFromHwnd(hwnd)
        if window.isWindow && not window.isMinimized && not window.isInMoveSize && this.isSameWindow(hwnd) then
            let live = window.bounds
            if live.width > 0 && live.height > 0 && this.os.isOnScreen(live) then
                let bounds =
                    if window.isMaximized then
                        match Mon.fromHwnd(hwnd) with
                        | Some(mon) -> mon.workRect.move(-1,-1)
                        | None -> live
                    else live
                let adjusted =
                    if this.hasWindowMargin(hwnd) && not window.isMaximized then this.removeWindowMarginForRead(hwnd, bounds)
                    else bounds
                let wp = window.placement
                let unchanged =
                    match placement.value with
                    | Some(r, p) -> r = adjusted && p.showCmd = wp.showCmd
                    | None -> false
                if not unchanged then
                    VirtualDesktopTrace.log (fun () ->
                        sprintf "group=%X follows %X to %d,%d %dx%d (shown=%b)"
                            (this.hwnd.ToInt64()) (hwnd.ToInt64())
                            adjusted.x adjusted.y adjusted.width adjusted.height desktopShown)
                    maximizedFrameBounds <- if window.isMaximized then Some live else None
                    placement.set(Some(adjusted, wp))
                    // Checked just before each is moved: a handle Windows has
                    // given to another window since it joined is left alone.
                    zorderCell.value
                        .where((<>) hwnd)
                        .where(isMinimized >> not)
                        .iter(fun other ->
                            if this.isSameWindow(other) then this.adjustWindowPlacement(other)
                            else
                                VirtualDesktopTrace.log (fun () ->
                                    sprintf "group=%X does not move %X: no longer the window that joined"
                                        (this.hwnd.ToInt64()) (other.ToInt64())))

    member private this.identityOf(hwnd: IntPtr) : VirtualDesktopGroups.Identity option =
        try
            let tid = Win32Helper.GetWindowThreadId(hwnd)
            let pid = Win32Helper.GetWindowProcessId(hwnd)
            if tid = 0 || pid = 0 then None
            else
                let exe = try this.os.windowFromHwnd(hwnd).pid.processPath with _ -> ""
                Some { VirtualDesktopGroups.Identity.pid = pid
                       VirtualDesktopGroups.Identity.tid = tid
                       VirtualDesktopGroups.Identity.exe = exe }
        with _ -> None

    member private this.isSameWindow(hwnd: IntPtr) =
        let joined =
            match memberIdentities.TryGetValue(hwnd) with
            | true, identity -> identity
            | _ -> None
        joined.IsNone || VirtualDesktopGroups.sameWindow joined (this.identityOf hwnd)

    member private this.followSharedMinimize(hwnd) =
        suppressFlashUntil <- DateTime.Now.AddSeconds(3.0)
        zorderCell.value.where((<>) hwnd).reverse.iter <| fun other ->
            if this.os.windowFromHwnd(other).isMinimized.not && this.isSameWindow(other) then
                pendingMinMaxEchoes.[(other, WinEvent.EVENT_SYSTEM_MINIMIZESTART)] <- DateTime.Now
                this.showWindowAsyncNoAnimation(other, ShowWindowCommands.SW_SHOWMINNOACTIVE)
        this.updateIsVisible()

    member private this.followSharedRestore(hwnd) =
        suppressFlashUntil <- DateTime.Now.AddSeconds(3.0)
        zorderCell.value.where((<>) hwnd).iter <| fun other ->
            if this.os.windowFromHwnd(other).isMinimized && this.isSameWindow(other) then
                pendingMinMaxEchoes.[(other, WinEvent.EVENT_SYSTEM_MINIMIZEEND)] <- DateTime.Now
                this.showWindowNoAnimation(other, ShowWindowCommands.SW_SHOWNOACTIVATE)
        this.updateIsVisible()
        this.followSharedWindow(hwnd)

    member this.main(hwnd, evt) =
        let fallbackPress =
            if evt = WinEvent.EVENT_SYSTEM_MOVESIZESTART then this.captureCaptionDragFallback(hwnd) else None
        this.dispatchEvent(hwnd, evt, fallbackPress)

    member private this.dispatchEvent(hwnd, evt, fallbackPress) = this.invokeAsync <| fun() -> this.withUpdate <| fun() -> PerfTrace.time (sprintf "group.%O" evt) <| fun () ->
        if this.windows.contains(hwnd) &&
           (evt = WinEvent.EVENT_OBJECT_LOCATIONCHANGE || evt = WinEvent.EVENT_OBJECT_SHOW ||
            evt = WinEvent.EVENT_SYSTEM_MOVESIZEEND || evt = WinEvent.EVENT_SYSTEM_MINIMIZEEND ||
            evt = WinEvent.EVENT_SYSTEM_FOREGROUND) &&
           (not inMoveSize.value || evt = WinEvent.EVENT_SYSTEM_MOVESIZEEND) then
            if this.refreshWindowMargin(hwnd) then
                // Foreground processing may still change the front member in
                // this batch. Publish the new outer bounds after it settles.
                this.invokeAsync <| fun () ->
                    if this.windows.contains(hwnd) && hwnd = this.topWindow then
                        this.saveTopWindowPlacement()
        if not desktopShown then this.backgroundEvent(hwnd, evt) else
        match evt with
        | WinEvent.EVENT_SYSTEM_MINIMIZESTART ->
#if DEBUG
            InputStallTrace.context InputStallTrace.Kind.Minimize hwnd 0.0
#endif
            if this.windows.contains(hwnd) then
                // Queued followers may finish after the initial visibility check.
                // Consume the echo without starting another batch, but refresh visibility.
                if this.consumeMinMaxEcho(hwnd, evt) then this.updateIsVisible() else
                let needsMinimized = zorderCell.value.any <| fun hwnd ->
                    this.os.windowFromHwnd(hwnd).isMinimized.not
                suppressFlashUntil <- DateTime.Now.AddSeconds(3.0)
                if needsMinimized then
                    this.minimizeAll()
                    // Every minimize is already posted. This orders handles only;
                    // it does not require the followers to be minimized yet.
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
            TitleTrace.log (fun () -> sprintf "namechange hwnd=%X member=%b inMoveSize=%b" (hwnd.ToInt64()) (this.windows.contains(hwnd)) inMoveSize.value)
            if  this.windows.contains(hwnd) &&
                //some windows (e.g. chrome on GoogleAnalitics page) fire namechange constantly as they are resized
                inMoveSize.value.not 
                then
                this.setTabInfo hwnd
        | WinEvent.EVENT_SYSTEM_MOVESIZESTART ->
            if this.isTop(hwnd) then
                this.beginCaptionDragFallback(hwnd, fallbackPress)
                this.onEnterMoveSize()

        | WinEvent.EVENT_SYSTEM_MOVESIZEEND ->
            if this.isTop(hwnd) then
                // Last correction inside the loop, before onExitMoveSize saves
                // the placement the siblings follow.
                this.enforceCaptionDragFallback(hwnd)
                this.onExitMoveSize()

        //this is here to detect transitions between maximized and
        //restored (both directions). MOVESIZE does not get triggered in this case
        //however, we need to be careful because some apps (Skype.exe) will trigger this
        //event when they loose focus, we don't want to automatically give them focus in this case
        //so make sure that the window HAD focus before reapplying it
        | WinEvent.EVENT_OBJECT_LOCATIONCHANGE ->
            if this.isTop(hwnd) then
                // Before either branch saves the placement. Outside the loop
                // this is the settle time after MOVESIZEEND.
                this.enforceCaptionDragFallback(hwnd)
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
                        this.saveTopWindowPlacement()
                        isMaximizedExport.update()
                        isFullscreenExport.update()
                        updateTabVisibility()
                        this.queueChildPlacement()
            // A window of this group that is shown on all desktops, moved by
            // something other than this group - its group on another desktop
            // following a move made there. Same as if it had been dragged
            // here (VirtualDesktopGroups.adoptsMoveOf).
            elif this.windows.contains(hwnd) && inMoveSize.value.not &&
                 VirtualDesktopGroups.adoptsMoveOf true ((VirtualDesktopGroups.Live.shared()).Contains hwnd) false then
                this.followSharedWindow(hwnd)
        | WinEvent.EVENT_SYSTEM_FOREGROUND ->
            this.foreground <- hwnd
            this.saveZorder()
            this.keepStripAboveTop(this.stripOwnerHwnd)
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
                updateTabVisibility()
        | _ -> ()
      
    member this.addWindow(hwnd, withDelay, ?alignment: TabAlign, ?pinned: bool, ?after: IntPtr,
                          ?restoreOrder: TabOrder.Placed list -> IntPtr list) =
        this.addWindowPlaced(hwnd, withDelay, true, ?alignment=alignment, ?pinned=pinned,
                             ?after=after, ?restoreOrder=restoreOrder)

    // Workspace placement precedes hooks and membership, so no sibling follows
    // an intermediate restore and no ordinary add path can activate a window.
    member this.addWindowForWorkspace(hwnd, placement: OSWindowPlacement) =
        followerPlacements.Synchronous(hwnd, fun () ->
            Dpi.withUnawareContext <| fun () ->
                if not (Win32Helper.PlaceWorkspaceWindowNoActivate(hwnd, placement.rcNormalPosition.RECT,
                            placement.showCmd = ShowWindowCommands.SW_SHOWMAXIMIZED)) then
                    invalidOp "Workspace placement was refused.")
        this.addWindowPlaced(hwnd, false, false)

    // Recover a partial add without placing or activating the native window.
    member this.recoverWorkspaceWindow(hwnd) =
        if not (this.windows.contains(hwnd) && this.ts.tabs.contains(Tab(hwnd))) then
            if this.windows.contains(hwnd) then this.removeWindow(hwnd, activate=false)
            this.addWindowPlaced(hwnd, false, false)
        else
            // A failure after tab creation may have preceded the membership notice.
            addedEvent.Trigger(hwnd)

    // Explicit links place only the joining window. Reading the destination's
    // placement must never restore or activate one of its existing members.
    member this.addWindowLinked(hwnd, withDelay, ?alignment: TabAlign, ?pinned: bool, ?after: IntPtr,
                                ?restoreOrder: TabOrder.Placed list -> IntPtr list) = this.withUpdate <| fun () ->
        if not (this.windows.contains(hwnd)) then
            let target =
                zorderCell.value.tryHead |> Option.bind (fun front ->
                    let window = this.os.windowFromHwnd(front)
                    let wp = window.placement
                    if window.isMinimized then
                        // rcNormalPosition uses workspace coordinates except for tool
                        // windows, as in Win32Helper.RestoreWindowNoActivate.
                        let normal =
                            if int(window.styleEx) &&& WindowsExtendedStyles.WS_EX_TOOLWINDOW <> 0 then wp.rcNormalPosition
                            else
                                match Mon.fromHwnd(front) with
                                | Some mon -> wp.rcNormalPosition.move(mon.workRect.x - mon.displayRect.x,
                                                                      mon.workRect.y - mon.displayRect.y)
                                | None -> wp.rcNormalPosition
                        Some(this.removeWindowMarginForRead(front, normal), false,
                             { wp with showCmd = ShowWindowCommands.SW_SHOWNORMAL; flags = 0 })
                    else
                        // Read without publishing placement: its listeners update the
                        // strip and guard. Linking needs only the destination rectangle.
                        let bounds = window.bounds
                        if window.isWindow && bounds.width > 0 && bounds.height > 0 then
                            let maximized = window.isMaximized
                            let bounds = if maximized then bounds else this.removeWindowMarginForRead(front, bounds)
                            Some(bounds, maximized, wp)
                        else
                            placement.value |> Option.map (fun (bounds, cached) ->
                                let maximized = cached.showCmd = ShowWindowCommands.SW_SHOWMAXIMIZED
                                (if maximized then maximizedFrameBounds |> Option.defaultValue bounds else bounds),
                                maximized, cached))
            // Place before registering membership and window event hooks, so
            // the joiner's restore cannot trigger a restore of existing members.
            frameMargins.Attach(hwnd)
            try
                this.refreshWindowMargin(hwnd) |> ignore
                target |> Option.iter (fun (bounds, maximized, _) ->
                    if bounds.width > 0 && bounds.height > 0 then
                        let window = this.os.windowFromHwnd(hwnd)
                        let bounds = if maximized then bounds else this.applyWindowMarginForWrite(hwnd, bounds)
                        followerPlacements.Synchronous(hwnd, fun () ->
                            this.withoutTransitions(hwnd, fun () ->
                                // Only the joiner may need restoring, including a joiner
                                // on another desktop. Its restoration must not activate it.
                                if window.isMinimized then
                                    window.showWindow(ShowWindowCommands.SW_SHOWNOACTIVATE)
                                if Win32Helper.SetWindowMaximizedNoActivate(hwnd, maximized, bounds.RECT) then
                                    this.applyWindowBoundsWithDpiHandling(hwnd, bounds)
                                else
                                    // A refused style change must not stop the link. The
                                    // bounds are still applied, to the joiner only, and
                                    // without a placement call: SetWindowPlacement activates
                                    // the window, and a joiner on another desktop would
                                    // switch to it.
                                    this.applyWindowBoundsWithDpiHandling(hwnd, bounds)))
                        if this.hasWindowMargin(hwnd) && not maximized then
                            marginShrunkSizes.set(marginShrunkSizes.value.Add(hwnd, (bounds.width, bounds.height))))
                this.addWindowPlaced(hwnd, withDelay, false, ?alignment=alignment, ?pinned=pinned,
                                     ?after=after, ?restoreOrder=restoreOrder)
            finally
                if not (this.windows.contains(hwnd)) then frameMargins.Remove(hwnd)

    // `place` false: the window joins where it is. A group made for a window
    // shown on all desktops, on another desktop, takes it as it is found - the
    // first member's own rectangle is the group's, and moving the others to it
    // would be switching desktops moving windows.
    member this.addWindowPlaced(hwnd, withDelay, place, ?alignment: TabAlign, ?pinned: bool,
                                ?after: IntPtr, ?restoreOrder: TabOrder.Placed list -> IntPtr list) = this.withUpdate <| fun() ->
       if this.windows.contains(hwnd).not then
            if withDelay then System.Threading.Thread.Sleep(250)
            frameMargins.Attach(hwnd)
            this.refreshWindowMargin(hwnd) |> ignore
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
                            let throttled = Helper.conflateWithTrailing interval handler
                            fun() ->
                                // A window held by the lock fallback is corrected
                                // on every change, not on the 50 ms throttle, or it
                                // visibly follows the cursor before jumping back.
                                // Queued, so the correction's synchronous
                                // SetWindowPos cannot re-enter this callback, and
                                // at most one is waiting. Nothing is armed while
                                // the setting is off.
                                if this.isCaptionDragFallbackArmedFor(hwnd) && not fallbackCorrectionQueued then
                                    fallbackCorrectionQueued <- true
                                    this.invokeAsync <| fun() ->
                                        fallbackCorrectionQueued <- false
                                        this.enforceCaptionDragFallback(hwnd)
                                throttled()
                        | _ -> Helper.conflate interval handler
                    | None -> handler
                if evt = WinEvent.EVENT_OBJECT_SHOW then
                    // Reuse the member's SHOW hook, restricted to its process.
                    // CREATE/SHOW for owned frames wakes a coalesced refresh;
                    // no global hook or enumeration in the callback.
                    let refreshFrames = Helper.conflateWithTrailing (TimeSpan.FromMilliseconds(50.0)) (fun () ->
                        this.invokeAsync <| fun () ->
                            if this.windows.contains(hwnd) then
                                frameMargins.Invalidate(hwnd)
                                if this.refreshWindowMargin(hwnd) then
                                    if hwnd = this.topWindow then this.saveTopWindowPlacement()
                                    else this.adjustWindowPlacement(hwnd))
                    this.os.setWinEventHook(WinEvent.EVENT_OBJECT_CREATE, WinEvent.EVENT_OBJECT_SHOW,
                        (fun _ event candidate _ _ _ _ ->
                            if event = int WinEvent.EVENT_OBJECT_CREATE || event = int WinEvent.EVENT_OBJECT_SHOW then
                                if candidate = hwnd then
                                    handler()
                                    refreshFrames()
                                elif TopEdgeGuardPlacement.ownerOf candidate = hwnd &&
                                     WindowFrameMargin.isExternalFrame candidate then
                                    refreshFrames()), window.pid.pid, 0)
                else window.setWinEventHook evt handler
            let hooks = 
                List2([
                    WinEvent.EVENT_OBJECT_NAMECHANGE
                    WinEvent.EVENT_OBJECT_SHOW
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

            let tab = Tab(hwnd)
            let knownAlignment = alignment |> Option.orElseWith (fun () -> Services.program.getWindowAlignment(hwnd))
            let initialAlignment, initialPinned =
                TabOrder.initialState (this.ts.getTabAlign(tab)) knownAlignment
                    (pinned |> Option.defaultWith (fun () -> Services.program.isWindowPinned(hwnd)))
            this.ts.addTabWithState(tab, initialAlignment, initialPinned, after |> Option.map Tab,
                                   ?restoreOrder=restoreOrder)
            alignment |> Option.iter (fun a -> Services.program.setWindowAlignment(hwnd, Some a))
            pinned |> Option.iter (fun p -> Services.program.setWindowPinned(hwnd, p))
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
            memberIdentities.[hwnd] <- this.identityOf(hwnd)
            if place then this.adjustWindowPlacement(hwnd)
            addedEvent.Trigger(hwnd)

    // Put one window of the group back where the group is. For the periodic
    // scan, which may find a live window of ours stranded at the iconic
    // position (-32000,-32000) after a restore; see the second pass of
    // adjustChildWindows for how that happened.
    member this.reseatWindow(hwnd) = this.withUpdate <| fun() ->
        if this.windows.contains(hwnd) && inMoveSize.value.not then
            this.adjustChildWindows()

    member this.removeWindow(hwnd, ?activate: bool) = this.withUpdate <| fun() ->
        followerPlacements.Synchronous(hwnd, ignore)
        pendingBackgroundMoves.Remove(hwnd) |> ignore
        frameMargins.Remove(hwnd)
        if this.windows.contains(hwnd) then    
            // A window this group parked is put back before it leaves: once it
            // is in no group, nothing would. Every way out of a group passes
            // here - a closed tab, a drag into another group, a window no
            // longer shown on all desktops (VirtualDesktopGroups.leftBehind) -
            // and the move/size loop that would have restored it may never end
            // for this group.
            this.restoreParkedWindow hwnd
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
            // Nor in a group of another desktop: a window shown on all desktops
            // closed (or unpinned) here leaves that group too, and bringing the
            // next of its tabs forward would switch to that desktop.
            let window = this.os.windowFromHwnd(hwnd)
            let skipActivation =
                not (defaultArg activate true) || Services.program.isShuttingDown || Services.program.isDisabled || window.isCloaked || not desktopShown

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
            hookCleanup.value.tryFind(hwnd) |> Option.iter(fun hooks -> hooks.Dispose())
            hookCleanup.map(fun hooks -> hooks.remove(hwnd))
            memberIdentities.Remove(hwnd) |> ignore
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
                        
    // The drag's capture window and timer belong to this thread, even after
    // its last tab has left. Release only once the terminal callback has run.
    member this.retainForTabDrag() =
        tabDragOwners <- tabDragOwners + 1
        let mutable released = false
        { new IDisposable with
            member _.Dispose() =
                this.invokeAsync <| fun () ->
                    if not released then
                        released <- true
                        tabDragOwners <- tabDragOwners - 1
                        if tabDragOwners = 0 && destroyAfterTabDrag then
                            destroyAfterTabDrag <- false
                            // It may have acquired a tab again while dragging.
                            if this.isEmpty then this.destroy() }

    member this.destroy() =
        if tabDragOwners > 0 && this.isEmpty then destroyAfterTabDrag <- true
        elif isDestroyed.value.not then
            frameMargins.Clear()
            followerPlacements.CancelAll()
            pendingBackgroundMoves.Clear()
            isDestroyed.set(true)
            this.windows.items.iter (CaptionDragTargets.remove captionDragOwner)
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
        try
            f()
        finally
            this.enableTransitions(hwnd)

    member private this.showWindowNoAnimation(hwnd, cmd) =
        this.withoutTransitions(hwnd, fun() -> this.os.windowFromHwnd(hwnd).showWindow(cmd))

    // Post without waiting for a follower's UI thread. As in async restore,
    // leave transitions disabled briefly while the target processes the show.
    member private this.showWindowAsyncNoAnimation(hwnd, cmd) =
        followerPlacements.Cancel(hwnd)
        this.disableTransitions(hwnd)
        try
            this.os.windowFromHwnd(hwnd).showWindowAsync(cmd)
            ThreadHelper.cancelablePostBack 500 (fun() -> this.enableTransitions(hwnd)) |> ignore
        with _ ->
            this.enableTransitions(hwnd)
            reraise()

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
                this.showWindowAsyncNoAnimation(hwnd, ShowWindowCommands.SW_SHOWMINNOACTIVE)

    member this.restoreAll() =
#if DEBUG
        InputStallTrace.context InputStallTrace.Kind.Restore this.ts.hwnd 0.0
#endif
        suppressFlashUntil <- DateTime.Now.AddSeconds(3.0)
        zorderCell.value.iter <| fun hwnd ->
            let window = this.os.windowFromHwnd(hwnd)
            if window.isMinimized then
                pendingMinMaxEchoes.[(hwnd, WinEvent.EVENT_SYSTEM_MINIMIZEEND)] <- DateTime.Now
                this.showWindowNoAnimation(hwnd, ShowWindowCommands.SW_SHOWNOACTIVATE)
        
    member this.tabActivate(Tab(hwnd), force) =
        followerPlacements.Synchronous(hwnd, ignore)
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
        let temporaryTopmost =
            TopEdgeGuardPolicy.temporaryStripTopmost isTabInside isUWP (this.hasWindowMargin(hwnd)) this.lockWindowPosition
        if temporaryTopmost then
            tsWindow.makeTopMost()

        window.setForegroundOrRestore(force)
        window.bringToTop()
        // Update WindowGroup's internal zorder state immediately
        this.bringToTop(hwnd)

        // Remove TOPMOST after the window switch for non-UWP windows
        if temporaryTopmost then
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
    member this.lockChanged = lockChangedEvent.Publish
    member this.bounds = boundsExport :> ICellOutput<_>
    member this.isForeground = isForegroundExport :> ICellOutput<_>
    member this.zorder = zorderExport :> ICellOutput<_>
    member this.added = addedEvent.Publish
    member this.moved = movedEvent.Publish
    member this.foregroundChanged = foregroundEvent.Publish
    member this.flash = flashEvent.Publish
    member this.removed = removedEvent.Publish
    member this.visualOrder = this.ts.visualOrder.map(fun(Tab(hwnd)) -> hwnd)

