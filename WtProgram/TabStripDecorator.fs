namespace Bemo
open System
open System.Drawing
open System.Windows.Forms
open System.Diagnostics
open Bemo.Win32.Forms

// Tab group info for cross-thread access
type TabGroupInfo = {
    hwnd: IntPtr
    tabNames: string list
    tabCount: int
    // Two pictures of the same icon, because a context menu can pop up on a
    // monitor with any scale (see TabStripDecorator.groupMenuIcon):
    //   firstTabIconSmall - the window's own 16 px ICON_SMALL artwork, byte for
    //                       byte what every release before this one showed. Used
    //                       whenever the menu is drawn at 100%.
    //   firstTabIcon      - 32 px, so a menu drawn at 125% / 150% / 200% has a
    //                       source to shrink from instead of a 16 px picture to
    //                       stretch.
    firstTabIcon: Img option
    firstTabIconSmall: Img option
    tabHwnds: IntPtr list
    tabIcons: Map<IntPtr, Img option * Img option>
}

// Tab color decoration definitions
type TabColorType = Fill | Underline | Border

type TabColorDef = {
    color: Color
    labelKey: string
    colorType: TabColorType
}

module TabColorDefs =
    // Parse RRGGBBAA hex string to Color
    let private c (rrggbbaa: string) =
        let r = System.Convert.ToInt32(rrggbbaa.Substring(0, 2), 16)
        let g = System.Convert.ToInt32(rrggbbaa.Substring(2, 2), 16)
        let b = System.Convert.ToInt32(rrggbbaa.Substring(4, 2), 16)
        let a = System.Convert.ToInt32(rrggbbaa.Substring(6, 2), 16)
        Color.FromArgb(a, r, g, b)
    // Color order: Red, Blue, Green, Yellow, Purple, Orange, Pink
    // Fill colors: RRGGBBAA format (AA=99 -> 60% opaque / 40% transparent)
    let fillDefs = [
        { color = c "D2323C99"; labelKey = "TabColorRed"; colorType = Fill }
        { color = c "2864D299"; labelKey = "TabColorBlue"; colorType = Fill }
        { color = c "13A10E99"; labelKey = "TabColorGreen"; colorType = Fill }
        { color = c "F7D30399"; labelKey = "TabColorYellow"; colorType = Fill }
        { color = c "88179899"; labelKey = "TabColorPurple"; colorType = Fill }
        { color = c "F9731699"; labelKey = "TabColorOrange"; colorType = Fill }
        { color = c "E91E8C99"; labelKey = "TabColorPink"; colorType = Fill }
    ]
    // Underline colors: RRGGBBAA format (AA=E6 -> 90% opaque / 10% transparent)
    let underlineDefs = [
        { color = c "D2323CE6"; labelKey = "TabColorRedUnderline"; colorType = Underline }
        { color = c "2864D2E6"; labelKey = "TabColorBlueUnderline"; colorType = Underline }
        { color = c "13A10EE6"; labelKey = "TabColorGreenUnderline"; colorType = Underline }
        { color = c "F7D303E6"; labelKey = "TabColorYellowUnderline"; colorType = Underline }
        { color = c "881798E6"; labelKey = "TabColorPurpleUnderline"; colorType = Underline }
        { color = c "F97316E6"; labelKey = "TabColorOrangeUnderline"; colorType = Underline }
        { color = c "E91E8CE6"; labelKey = "TabColorPinkUnderline"; colorType = Underline }
    ]
    // Border colors: RRGGBBAA format (AA=E6 -> 90% opaque / 10% transparent)
    let borderDefs = [
        { color = c "D2323CE6"; labelKey = "TabColorRedBorder"; colorType = Border }
        { color = c "2864D2E6"; labelKey = "TabColorBlueBorder"; colorType = Border }
        { color = c "13A10EE6"; labelKey = "TabColorGreenBorder"; colorType = Border }
        { color = c "F7D303E6"; labelKey = "TabColorYellowBorder"; colorType = Border }
        { color = c "881798E6"; labelKey = "TabColorPurpleBorder"; colorType = Border }
        { color = c "F97316E6"; labelKey = "TabColorOrangeBorder"; colorType = Border }
        { color = c "E91E8CE6"; labelKey = "TabColorPinkBorder"; colorType = Border }
    ]
    let allDefs = fillDefs @ underlineDefs @ borderDefs

module TabNameHelper =
    // Truncate tab name for menu display (max 12 half-width chars, full-width counts as 2)
    let truncate (text: string) =
        let mutable width = 0
        let mutable endIdx = text.Length
        let mutable found = false
        for i in 0 .. text.Length - 1 do
            if not found then
                let c = text.[i]
                let charWidth = if int c > 0x7F then 2 else 1
                if width + charWidth > 12 then
                    endIdx <- i
                    found <- true
                else
                    width <- width + charWidth
        if endIdx < text.Length then text.Substring(0, endIdx) + "..."
        else text

    // Build a menu label from a localization template. Translations may embed
    // the truncated tab name with {TabName} or {0} (e.g. "Close tab : {0}");
    // the default strings omit the placeholder, so the name is not shown.
    let format (template: string) (name: string) =
        template.Replace("{TabName}", name).Replace("{0}", name)

    // Drop the tab-name placeholder (and its " : " separator) from a template.
    // Used when the item is disabled and the name would be meaningless.
    let stripName (template: string) =
        template.Replace(" : {TabName}", "").Replace(" : {0}", "")
                .Replace("{TabName}", "").Replace("{0}", "")

// ScreenCache lives in Shared/Dpi.fs now; nothing in this file needs it any
// more (see MonitorScreen below), and the DPI-unaware settings dialog, which
// does, is compiled after that file.

/// A monitor, for the snap / detach / "move to display" code paths.
///
/// This exists so System.Windows.Forms.Screen cannot be used here at all. That
/// type is backed by a process-wide array WinForms enumerates once and caches
/// forever, in the coordinate space of whichever thread happened to fill it -
/// and WindowTabs deliberately runs one DPI-UNAWARE thread (the settings
/// dialog), so the cache can hold virtualized rectangles while everything else
/// works in device pixels. It also goes stale across a display configuration
/// change.
///
/// The second generation contained that risk by convention: keep the Screen
/// type, but funnel every instance through two helpers that re-enumerate
/// first. Convention does not survive the next edit - a Screen.AllScreens
/// written by someone unaware of the rule compiles and is silently wrong (one
/// such reference did survive, in the snap-desktop menu item). Here `Screen`
/// simply does not typecheck in these signatures, and every rectangle comes
/// from a live MonitorFromPoint / EnumDisplayMonitors query instead.
///
/// Bounds / WorkingArea are exposed as System.Drawing.Rectangle so the ~30
/// interior call sites that pass work areas around keep working unchanged.
type MonitorScreen(mon: Mon) =
    member this.Mon = mon
    member this.Bounds = mon.displayRect.Rectangle
    member this.WorkingArea = mon.workRect.Rectangle
    /// Identity is the monitor handle, so "is this the display the window is
    /// already on?" is a handle comparison rather than a rectangle comparison.
    override this.Equals(value) =
        match value with
        | :? MonitorScreen as other -> mon.hMonitor = other.Mon.hMonitor
        | _ -> false
    override this.GetHashCode() = mon.hMonitor.GetHashCode()

type TabStripDecorator(group:WindowGroup, notifyDetached: IntPtr -> unit) as this =
    // Static registry for all TabStripDecorator instances
    static let mutable decorators = System.Collections.Generic.Dictionary<IntPtr, TabStripDecorator>()
    // Roughly when WindowTabs started: the first group is decorated within a
    // moment of startup. A restored session brings its windows back over
    // several seconds, on whichever desktops Windows picks, so nothing is
    // judged to straddle until that has settled.
    static let startedAt = DateTime.UtcNow
    static let restoreSettles = TimeSpan.FromSeconds(20.0)
    // A straddle has to hold still for this many ticks (one a second) before
    // windows are taken out of a group over it. Two, because each window is
    // read once more immediately before it goes: waiting longer only makes a
    // move that was meant look as though nothing noticed it.
    static let straddleTicksNeeded = 2
    // Which groups are currently reporting that they straddle, and when they
    // last said so. A person moves one window at a time, so one group at a
    // time is the only picture that is believed.
    static let straddlingGroups = System.Collections.Generic.Dictionary<IntPtr, DateTime>()
    static let straddleGate = obj()
    // Task view (Win+Tab) and the Alt+Tab switcher, which the shell shows as a
    // window of its own. While one is open the person is in the middle of
    // arranging windows - dragging them from desktop to desktop one at a time -
    // and nothing they do there is finished until it closes.
    static let shellSwitcherClasses =
        set ["XamlExplorerHostIslandWindow"; "ForegroundStaging"; "MultitaskingViewFrame"; "TaskSwitcherWnd"]
    static let mutable groupInfos = System.Collections.Generic.Dictionary<IntPtr, TabGroupInfo>()

    let os = OS()
    let Cell = CellScope(false, true)
    let readHideSettings () =
        let root = Services.settings.root
        TabEdges.priority |> List.map (fun edge -> edge, TabEdgeSettings.hideSettings root edge) |> Map.ofList
    let edgeHideSettings = Cell.create(readHideSettings ())
    let isDraggingCell = Cell.create(false)
    let dragInfoCell = Cell.create(None)
    let dragPtCell = Cell.create(Pt.empty)
    let dropTarget = Cell.create(None)
    let mouseEvent = Event<IntPtr * MouseButton * TabPart * MouseAction * Pt>()
    let _ts = TabStrip(this :> ITabStripMonitor)
    // State for suppressing hover-driven reveal after an icon click.
    let edgeIconProtection = System.Collections.Generic.Dictionary<TabEdges.Edge, System.DateTime>()
    let hiddenByIconClick = ref false
    let iconClickProtectUntil = ref System.DateTime.MinValue
    let iconClickHideProtection = System.TimeSpan.FromSeconds(1.0)
    let firstClickTab = ref None  // Track the tab that was clicked first in potential double-click
    // What the last groupInfos entry was built from, so an unchanged periodic
    // refresh does not republish the snapshot.
    let mutable groupInfoSource : (IntPtr list * string list * (obj * obj) list) option = None
    // Pictures belong to each tab's icon objects, not its changing title.
    // Only this decorator's owning thread reads or updates the cache.
    let mutable tabIconCache : Map<IntPtr, obj * obj * (Img option * Img option)> = Map.empty
    // What the virtual desktops of this group's windows looked like on the
    // previous tick, and which windows they were. A straddle is acted on only
    // when the same one is seen twice, and never right after the group gained
    // or lost a window (see reconcileVirtualDesktop).
    let mutable lastDesktopDecision = VirtualDesktopIntegrity.Settled
    let mutable lastDesktopMembers : IntPtr list = []
    // The last reading written to the trace, so a group that sees the same
    // thing second after second writes one line rather than thousands.
    let mutable lastDesktopReading = ""
    let mutable lastDesktopGeneration = 0L
    let mutable lastDesktopCurrent : Guid option = None
    let mutable pendingStraddleGeneration : int64 option = None
    // How many ticks running this group has looked the same way about the
    // desktops, and when it last took a window out over it.
    let mutable straddleTicks = 0
    // The desktop each window was on when this group last looked, which is
    // what tells a window that moved from a window that did not.
    let mutable lastDesktopReads : Map<IntPtr, Guid> = Map.empty
    let mutable lastStraddleAction = DateTime.MinValue
    let topEdgeGuard = TopEdgeGuard(os)
    let guardUpdates = TopEdgeGuardPolicy.UpdateQueue()
    let guardRepairTimer = new System.Windows.Forms.Timer(Interval = 50)
    let mutable menuLinkGeneration = 0
    // The settings file's own values for each allowed edge other than the
    // top (TabEdgeSettings), read with the allowed edges: when the group
    // starts and whenever the dialog changes them.
    let mutable edgeValues : Map<TabEdges.Edge, TabEdgeSettings.EdgeValues> = Map.empty
    // The windows of the other edges as registered for drops and files,
    // taken off again as the strip destroys them.
    let mutable edgeRegistrations : (IntPtr * OleDropTarget option) list = []

    // Explorer-like selection: was the MouseDown'd tab already part of the
    // selection (or the active tab)? If yes, MouseUp / dragEnd without drag
    // reduces the selection to just the clicked tab (clears the multi-select).
    let mouseDownOnSelected : bool ref = ref false
    // Drag origin tab — set on MouseDown so dragBegin can compute the
    // contiguous selected range and set ts.dragGroup before slide rendering
    // starts. Cleared in dragEnd.
    // Only the source session owns these; foreign groups keep transferOrder.
    let mutable foreignDragFront : (IntPtr * IntPtr list) option = None
    let mutable pendingHoverRestore : (unit -> unit) option = None
    let mutable ownDragBlock : Tab list option = None
    let mutable restoreOwnRemainder : unit -> unit = ignore
    let mouseDownTab : Tab option ref = ref None

    do this.init()

    member this.ts = _ts
    member this.group = group
    member private this.mouse = mouseEvent.Publish

    member private this.updateGroupInfo() =
        try
            // Create a snapshot of current tab information
            let tabs = this.ts.visualOrder

            // If no tabs remain, remove from groupInfos
            if tabs.count = 0 then
                tabIconCache <- Map.empty
                groupInfoSource <- None
                lock groupInfos (fun () -> groupInfos.Remove(group.hwnd) |> ignore)
            else
                let tabNames =
                    tabs.list |> List.map (fun tab ->
                        let info = this.ts.tabInfo(tab)
                        info.text
                    )
                let tabHwnds =
                    tabs.list |> List.map (fun (Tab(hwnd)) -> hwnd)
                let tabInfos = tabs.list |> List.map this.ts.tabInfo
                let source = tabHwnds, tabNames, (tabInfos |> List.map (fun info -> box info.iconSmall, box info.iconBig))
                let unchanged =
                    match groupInfoSource with
                    | Some(h, n, icons) ->
                        let (_, _, currentIcons) = source
                        h = tabHwnds && n = tabNames && icons.Length = currentIcons.Length &&
                        List.forall2 (fun (s, b) (s2, b2) -> Object.ReferenceEquals(s, s2) && Object.ReferenceEquals(b, b2)) icons currentIcons &&
                        lock groupInfos (fun () -> groupInfos.ContainsKey(group.hwnd))
                    | None -> false
                if not unchanged then
                    // Rebuilding the map drops departed tabs while preserving pictures
                    // for unchanged icon objects, even when names or order changed.
                    let cache =
                        List.zip tabHwnds tabInfos |> List.map (fun (hwnd, info) ->
                            let smallSource, bigSource = box info.iconSmall, box info.iconBig
                            let pictures =
                                match Map.tryFind hwnd tabIconCache with
                                | Some(small, big, pictures) when
                                    Object.ReferenceEquals(small, smallSource) && Object.ReferenceEquals(big, bigSource) -> pictures
                                | _ ->
                                    // Menus above 100% shrink a 32 px picture to the
                                    // monitor's menu size instead of stretching 16 px artwork.
                                    let big =
                                        try
                                            let source = if Object.ReferenceEquals(info.iconBig, SystemIcons.Application) then info.iconSmall else info.iconBig
                                            Some(ScaledIcon.at source 32 |> fun i -> i.ToBitmap().img.resize(Sz(32,32)))
                                        with _ -> None
                                    // At 100%, use the application's own 16 px artwork.
                                    // Applications may simplify this icon rather than scale
                                    // it down, so it must not be made from the 32 px picture.
                                    let small =
                                        try Some(info.iconSmall.ToBitmap().img.resize(Sz(16,16)))
                                        with _ -> None
                                    big, small
                            hwnd, (smallSource, bigSource, pictures)) |> Map.ofList
                    // An open menu may still hold a replaced picture; as before,
                    // release our references without disposing those pictures here.
                    tabIconCache <- cache
                    let icons = cache |> Map.map (fun _ (_, _, pictures) -> pictures)
                    let big, small = icons.[tabHwnds.Head]
                    let info = {
                        hwnd = group.hwnd
                        tabNames = tabNames
                        tabCount = tabs.count
                        firstTabIcon = big
                        firstTabIconSmall = small
                        tabHwnds = tabHwnds
                        tabIcons = icons
                    }
                    groupInfoSource <- Some source
                    lock groupInfos (fun () -> groupInfos.[group.hwnd] <- info)
        with _ -> ()

    /// Whether every window of a group is on the desktop being looked at now.
    /// A window Windows will not answer for counts as being here: an unreadable
    /// window must not quietly remove a group from a menu, the same way it must
    /// not make a group look as if it straddles.
    /// A group of another desktop holding only windows shown on all desktops
    /// has every window here, and is still not this desktop's group.
    member private this.isOnCurrentDesktop(state: VirtualDesktopGroups.Live.Snapshot, usable: bool) (info: TabGroupInfo) =
        let shown =
            lock decorators (fun () ->
                match decorators.TryGetValue(info.hwnd) with
                | true, d -> d.group.isDesktopShownThreadSafe
                | _ -> true)
        // Drawn here with some of its windows elsewhere, it is still this
        // desktop's group.
        if not usable then true
        elif not shown then false
        elif VirtualDesktopGroups.keepAway then
            info.tabHwnds |> List.exists (fun hwnd ->
                let r = VirtualDesktopGroups.Live.read state hwnd
                r.presence <> VirtualDesktopGroups.Away || r.desktop.IsNone)
        else
            info.tabHwnds |> List.forall (fun hwnd ->
                (VirtualDesktopGroups.Live.read state hwnd).presence = VirtualDesktopGroups.Here)

    /// The local view used to decide visibility and which tabs lead a menu entry.
    /// The entry itself uses the full group for its total count and remaining names.
    member private this.hereInfo(state: VirtualDesktopGroups.Live.Snapshot, usable: bool) (info: TabGroupInfo) =
        if not VirtualDesktopGroups.keepAway || not usable ||
           info.tabNames.Length <> info.tabHwnds.Length then info
        else
            let here =
                List.zip info.tabHwnds info.tabNames
                |> List.filter (fun (hwnd, _) ->
                    let r = VirtualDesktopGroups.Live.read state hwnd
                    r.presence <> VirtualDesktopGroups.Away || r.desktop.IsNone)
            let big, small =
                here |> List.tryHead |> Option.bind (fun (h, _) -> Map.tryFind h info.tabIcons)
                |> Option.defaultValue (info.firstTabIcon, info.firstTabIconSmall)
            { info with
                firstTabIcon = big
                firstTabIconSmall = small
                tabHwnds = here |> List.map fst
                tabNames = here |> List.map snd
                tabCount = here.Length }

    /// A tab group is one window to the person using it, so it belongs on one
    /// virtual desktop. Nothing stops a tabbed window from being sent to
    /// another desktop (in task view, by dragging it or with "Move to" on its
    /// context menu - Windows has no key for it - or by an application moving
    /// its own window) and no event
    /// says that it happened, so each group looks at its own windows once a
    /// second. A window that has left is detached into a group of its own: it
    /// stays where it was put, and neither group straddles.
    ///
    /// Moving the rest of the group after it - which is what one would rather
    /// do, since the tab group is the thing being moved - is not open to us.
    /// IVirtualDesktopManager.MoveWindowToDesktop refuses every window of
    /// another process with E_ACCESSDENIED, and the interface that does not is
    /// undocumented and changes shape with each Windows build.
    /// Whether the shell is showing task view or the Alt+Tab switcher. Windows
    /// gives each of them a window of its own, and it is the foreground window
    /// while it is up.
    member private this.shellSwitcherIsOpen = TabStripDecorator.isShellSwitcherOpen(os)

    // The same, for the main thread's desktop pass (Program.fs).
    static member isShellSwitcherOpen(os: OS) =
        try
            let fg = WinUserApi.GetForegroundWindow()
            fg <> IntPtr.Zero && shellSwitcherClasses.Contains(os.windowFromHwnd(fg).className)
        with _ -> false

    member private this.reconcileVirtualDesktop() =
        try
            // Nothing is decided while task view is open. Dragging a group's
            // windows to another desktop is done there, one window at a time,
            // and a window that has been dragged over is not a window the
            // person has finished with - it is part of a move that ends when
            // task view closes. Acting per drag took the windows out of the
            // group one by one, so each arrived on the other desktop alone;
            // waiting means the whole picture is read at once, and they arrive
            // together, as the group they still are.
            //
            // The reading from before task view opened is kept, so the pass
            // after it closes compares against it and sees every window that
            // moved.
            let state = VirtualDesktopGroups.Live.snapshot()
            if this.shellSwitcherIsOpen || not (VirtualDesktopGroups.Live.isFresh state) then
                // Keep the pre-switch comparison, but do not reuse this
                // publication after task view closes.
                lastDesktopGeneration <- state.generation
                pendingStraddleGeneration <- None
            else
            if state.current <> lastDesktopCurrent then
                lastDesktopCurrent <- state.current
                pendingStraddleGeneration <- None
                straddleTicks <- 0
            let members = group.windows.items.list
            let membershipChanged = members <> lastDesktopMembers
            lastDesktopMembers <- members
            let wait =
                membershipChanged
                || not (VirtualDesktopGroups.Live.canDecide state members)
                || members.Length <= 1
                || DateTime.UtcNow - startedAt < restoreSettles
                // Mid-drag the windows are being moved about by us; a desktop
                // read then says nothing about what the person wants.
                || group.isInMoveSizeThreadSafe
            if wait then
                lastDesktopGeneration <- state.generation
                pendingStraddleGeneration <- None
                lastDesktopDecision <- VirtualDesktopIntegrity.Settled
                straddleTicks <- 0
                // The next reading has nothing to compare against: whatever a
                // window did while this group was not looking was not done to
                // this group.
                lastDesktopReads <- Map.empty
            elif VirtualDesktopGroups.Live.isNewer lastDesktopGeneration state then
                // A timer tick is not a new observation if the main pass has
                // not published again. Only independent readings count.
                lastDesktopGeneration <- state.generation
                let home = group.desktopHome
                let windows = VirtualDesktopGroups.Live.view state home members
                let previousReads = lastDesktopReads
                let decision =
                    VirtualDesktopIntegrity.decide previousReads group.topWindow windows
                    |> VirtualDesktopGroups.rebase home windows
                lastDesktopReads <- VirtualDesktopIntegrity.reading windows
                // Trace the main pass's actual HRESULTs, not new COM calls
                // made by the group thread. A missing entry is not a failure.
                let reading =
                    members |> List.map (fun hwnd ->
                        let result =
                            match state.desktopResults |> Map.tryFind hwnd with
                            | Some(0, id) -> (string id).Substring(0, 8)
                            | Some(hr, _) -> sprintf "hr=%08X" hr
                            | None -> "unread"
                        sprintf "%X:%s" (hwnd.ToInt64()) result)
                    |> String.concat " "
                if reading <> lastDesktopReading then
                    lastDesktopReading <- reading
                    let frontIsHere =
                        VirtualDesktopGroups.Live.isHere state group.topWindow
                    VirtualDesktopTrace.log (fun () ->
                        sprintf "group=%X thread=%d top=%X%s %s -> %A sample=%d ageMs=%.0f"
                            (group.hwnd.ToInt64())
                            System.Threading.Thread.CurrentThread.ManagedThreadId
                            (group.topWindow.ToInt64())
                            (if frontIsHere then "" else "(group is on another desktop)")
                            reading decision state.generation (DateTime.UtcNow - state.readAt).TotalMilliseconds)
                // The count is of one unchanging picture. Any change at all -
                // a different base, a different set of windows, or the group
                // settling - starts it again, so a picture that keeps moving
                // never reaches the number that lets a window be taken out.
                if decision <> lastDesktopDecision then
                    straddleTicks <- 0
                    pendingStraddleGeneration <- None
                lastDesktopDecision <- decision
                match decision with
                | VirtualDesktopIntegrity.Settled ->
                    lock straddleGate (fun () -> straddlingGroups.Remove(group.hwnd) |> ignore)
                | VirtualDesktopIntegrity.Straddling(baseDesktop, strays) ->
                    straddleTicks <- straddleTicks + 1
                    if not (VirtualDesktopIntegrity.plausible previousReads windows decision) then
                        if straddleTicks = 1 then
                            VirtualDesktopTrace.log (fun () ->
                                sprintf "group=%X straddle IMPLAUSIBLE base=%s strays=%d of %d - left alone"
                                    (group.hwnd.ToInt64()) ((string baseDesktop).Substring(0, 8))
                                    strays.Length windows.Length)
                    elif straddleTicks >= straddleTicksNeeded then
                        // The old final COM recheck was independent of the
                        // confirming read. Wait for another publication instead
                        // of using the same cached answer as its own recheck.
                        if VirtualDesktopGroups.Live.confirmationReady pendingStraddleGeneration state then
                            this.actOnStraddle(baseDesktop, strays, state)
                        else
                            pendingStraddleGeneration <- Some state.generation
                    else
                        VirtualDesktopTrace.log (fun () ->
                            sprintf "group=%X straddle %d/%d base=%s strays=%s"
                                (group.hwnd.ToInt64()) straddleTicks straddleTicksNeeded
                                ((string baseDesktop).Substring(0, 8))
                                (strays |> List.map (fun h -> sprintf "%X" (h.ToInt64())) |> String.concat ","))
        with _ -> ()

    /// Takes the windows that left out of the group, once the reading has held
    /// still for a few seconds and a second, fresh look at each of them agrees.
    ///
    /// They go together rather than one a second: three windows sent to another
    /// desktop used to take twenty-five seconds to leave, which reads as the
    /// feature being unsure of itself. Each one is still checked on its own
    /// immediately before it is taken out, and the set they come from can never
    /// be more than half the group (VirtualDesktopIntegrity.plausible).
    ///
    /// The guards are what this feature earns: the first version of it took the
    /// front window's desktop for the group's, and one second in 2026-09-20 it
    /// believed every group at once - 7 groups became 21 in half a minute. So
    /// nothing acts while another group is also reporting a straddle, since a
    /// person moves windows out of one group at a time, and a group waits a few
    /// seconds after acting before it acts again.
    member private this.actOnStraddle(baseDesktop: Guid, strays: IntPtr list, state: VirtualDesktopGroups.Live.Snapshot) =
        let now = DateTime.UtcNow
        let elsewhere =
            lock straddleGate (fun () ->
                straddlingGroups
                |> Seq.filter (fun kv -> kv.Key <> group.hwnd && now - kv.Value < TimeSpan.FromSeconds(5.0))
                |> Seq.length)
        lock straddleGate (fun () -> straddlingGroups.[group.hwnd] <- now)
        let tooSoon = now - lastStraddleAction < TimeSpan.FromSeconds(3.0)
        // This is a later publication than the one that confirmed the
        // straddle. Use its entire picture, including the windows that stayed.
        let shared = state.shared
        let presenceOf hwnd = (VirtualDesktopGroups.Live.read state hwnd).presence
        let desktopOf hwnd = (VirtualDesktopGroups.Live.read state hwnd).desktop |> Option.defaultValue Guid.Empty
        let fresh = VirtualDesktopGroups.Live.view state group.desktopHome group.windows.items.list
        let readsAs desktop hwnd =
            fresh |> List.exists (fun w -> w.hwnd = hwnd && (match w.desktop with Some id -> desktop id | None -> false))
        let stayedHere =
            group.windows.items.list
            |> List.exists (fun hwnd ->
                not (List.contains hwnd strays) && readsAs (fun id -> id = baseDesktop) hwnd)
        // Never a window that is on the desktop being looked at: one shown on
        // all desktops always is. An ordinary moved window leaves this group
        // once its absence is confirmed while this desktop is looked at
        // (VirtualDesktopGroups.mayLeave).
        let away =
            strays |> List.filter (fun hwnd ->
                readsAs (fun id -> id <> baseDesktop) hwnd &&
                VirtualDesktopGroups.mayLeave (shared.Contains hwnd) (presenceOf hwnd))
        let stillAway = stayedHere && not away.IsEmpty && away.Length = strays.Length
        let act =
            VirtualDesktopGroups.Live.canDecide state group.windows.items.list &&
            VirtualDesktopIntegrity.actOnStraddle && not VirtualDesktopGroups.keepAway &&
            stillAway && not tooSoon && elsewhere = 0
        VirtualDesktopTrace.log (fun () ->
            sprintf "group=%X straddle CONFIRMED base=%s strays=%s stillAway=%b tooSoon=%b otherGroups=%d -> %s"
                (group.hwnd.ToInt64()) ((string baseDesktop).Substring(0, 8))
                (strays |> List.map (fun h -> sprintf "%X" (h.ToInt64())) |> String.concat ",")
                stillAway tooSoon elsewhere
                (if act then
                    sprintf "detach %s"
                        (away |> List.map (fun h -> sprintf "%X" (h.ToInt64())) |> String.concat ",")
                 else "left alone"))
        if act then
            lastStraddleAction <- now
            straddleTicks <- 0
            pendingStraddleGeneration <- None
            // Windows that went to the same desktop went there together, so
            // they arrive as one tab group rather than as a row of single
            // tabs. Moving a group across desktops means moving its windows
            // one by one - Windows offers nothing else - and this is what
            // makes that add up to the group arriving.
            let order = this.ts.visualOrder.list |> List.map (fun (Tab(hwnd)) -> hwnd)
            let inOrder hwnds =
                (order |> List.filter (fun hwnd -> List.contains hwnd hwnds))
                @ (hwnds |> List.filter (fun hwnd -> not (List.contains hwnd order)))
            // A window that is in a group of another desktop as well (it was
            // shown on all desktops, or moved and followed) already has a
            // group where it went: it only leaves this one.
            let several = state.inSeveral
            let elsewhere, alone = away |> List.partition several.Contains
            elsewhere |> List.iter (fun hwnd ->
                if group.windows.items.count > 1 && group.windows.contains(hwnd) then
                    this.ts.removeTab(Tab(hwnd))
                    group.removeWindow(hwnd))
            alone
            |> List.groupBy desktopOf
            |> List.iter (fun (_, together) ->
                match inOrder together with
                | [] -> ()
                | [alone] -> this.detachToOwnGroup(alone)
                | ordered ->
                    // The seed keeps the place it is in: it is on another
                    // desktop, where the person put it, so it is not moved or
                    // snapped the way a detach from the menu would be.
                    this.detachTabsCommon(ordered, fun _ bounds place -> place bounds))

    /// Detach without placing: the window is on another virtual desktop, where
    /// the person put it, so nothing here moves or resizes it. The other detach
    /// paths position the window because the person asked for a position.
    member private this.detachToOwnGroup(hwnd: IntPtr) =
        try
            if group.windows.items.count > 1 && group.windows.contains(hwnd) then
                Services.program.suspendTabMonitoring()
                try
                    this.ts.removeTab(Tab(hwnd))
                    group.removeWindow(hwnd)
                    notifyDetached(hwnd)
                finally
                    Services.program.resumeTabMonitoringAfter(200)
        with _ -> ()

    member private this.init() =
        Services.registerLocal(_ts)
        // The edges tabs may use, from the settings file. Read before the
        // group can take a window: a joining tab's edge depends on them.
        let settingsRoot = try Services.settings.root with _ -> Newtonsoft.Json.Linq.JObject()
        let allowed = try TabEdgeSettings.allowedOf settingsRoot with _ -> TabEdges.topOnly
        this.ts.setAllowedEdges(allowed)
        this.readEdgeValues settingsRoot allowed
        group.init(this.ts)
        // Register this decorator in the global registry
        lock decorators (fun () -> decorators.[group.hwnd] <- this)

        Services.dragDrop.registerTarget(this.ts.hwnd, this:>IDragDropTarget)

        // A window for each other allowed edge, registered as a place to drop
        // on from the start: a drag takes its list of targets when it begins,
        // so a strip made later could not take part in it. Nothing is made
        // while the top edge is the only one.
        this.addEdgeWindows()
        // Taken off again while the windows still exist: the strip forgets
        // them as it destroys them, and the group's exit comes after that.
        // (Nothing to take off while the top edge is the only one.)
        this.ts.destroying.Add <| fun () ->
            for (hwnd, ole) in edgeRegistrations do
                (try Services.dragDrop.unregisterTarget(hwnd) with _ -> ())
                ole |> Option.iter (fun o -> try o.revoke() with _ -> ())
            edgeRegistrations <- []
        // The dialog's changes to the edges reach the group here, as its
        // changes to the top edge's appearance do through "tabAppearance".
        let edgeSettingsChanged =
            TabEdgeSettings.changed.Publish.Subscribe(fun () ->
                this.invokeAsync (fun () -> this.applyEdgeSettings()))
        group.exited.Add <| fun () -> edgeSettingsChanged.Dispose()

        dropTarget.set(Some(OleDropTarget(this.ts)))
        
        this.initAutoHide()

        // Other groups' tab menus list this group by the names and icon in
        // groupInfos. Refreshing the entry here, on this group's own thread,
        // lets a menu read it at once; the menu used to wait on every other
        // group's thread while it was being built. Up to a second stale is
        // fine for a menu label.
        this.updateGroupInfo()
        let groupInfoTimer = new System.Windows.Forms.Timer(Interval = 1000)
        let repairGuardOrder () = PerfTrace.time "topEdgeGuard.repair" <| fun () ->
            if topEdgeGuard.followsStrip && topEdgeGuard.stripChanged(this.ts.hwnd) then
                this.queueTopEdgeGuard()
        // A sibling can pass the strip without sending it WINDOWPOSCHANGED.
        // Reuse the bounded, coalesced repair path; the timer also covers apps
        // that do not publish an accessibility reorder event.
        let guardReorder = os.setSingleWinEvent WinEvent.EVENT_OBJECT_REORDER (fun _ ->
            // A frame can pass other siblings without changing owner adjacency.
            topEdgeGuard.invalidateOrder(false)
            repairGuardOrder())
        let guardForeground = os.setSingleWinEvent WinEvent.EVENT_SYSTEM_FOREGROUND (fun _ ->
            topEdgeGuard.invalidateOrder(true)
            repairGuardOrder())
        groupInfoTimer.Tick.Add(fun _ ->
            this.updateGroupInfo()
            group.refreshWindowMargins()
            // Child-only frame changes need an update even when placement is stable.
            if topEdgeGuard.followsStrip then
                topEdgeGuard.refreshChildren()
                this.updateTopEdgeGuard()
            repairGuardOrder()
            // Same tick, same thread as the rest of this group's upkeep.
            this.reconcileVirtualDesktop()
            PerfTrace.time "title.refresh" group.refreshTitles)
        groupInfoTimer.Start()
        // One repair per 50ms event batch, including notifications produced by
        // our own SetWindowPos. Never recursively chase a frame that re-raises.
        guardRepairTimer.Tick.Add(fun _ ->
            topEdgeGuard.auditDecision("repair-timer-fired")
            guardRepairTimer.Stop()
            guardUpdates.Request((fun f -> this.invokeAsync f), this.updateTopEdgeGuard))
        // Stop and release the timer with the group: it owns a window of this
        // group's thread, which goes away with the group.
        group.exited.Add <| fun() ->
            groupInfoTimer.Stop()
            groupInfoTimer.Dispose()
            guardReorder.Dispose()
            guardForeground.Dispose()

        let capturedHwnd = ref None

        this.mouse.Add <| fun(hwnd, btn, part, action, pt) ->
            match action, btn with
            | MouseDblClick, MouseLeft ->
                // Double-click always opens rename UI for the active tab
                if hwnd = group.topWindow && !firstClickTab = Some(hwnd) then
                    this.beginRename(hwnd)

                // Clear first click tracking after double-click
                firstClickTab := None
            | MouseDown, MouseLeft ->
                // This event runs before tab activation. Hide immediately only
                // when the pressed icon belongs to the already-active tab.
                let autoHideIconClick =
                    if TabEdges.isTopOnly this.ts.allowedEdges then group.bb.read("autoHideDoubleClick", false)
                    else fst edgeHideSettings.value.[this.ts.edgeOf (Tab(hwnd))] = "doubleclick"
                if TabBehaviorPolicy.hideFromIconMouseDown
                       autoHideIconClick (hwnd = group.topWindow) (part = TabIcon)
                   && (TabEdges.isTopOnly this.ts.allowedEdges
                       || this.ts.isInsideOn (this.ts.edgeOf (Tab(hwnd)))) then
                    if TabEdges.isTopOnly this.ts.allowedEdges then
                        hiddenByIconClick := true
                        // Ignore hover-driven reveal immediately after hiding.
                        iconClickProtectUntil := System.DateTime.Now.Add(iconClickHideProtection)
                        group.invokeAsync <| fun() -> this.ts.isShrunk <- true
                    else
                        let edge = this.ts.edgeOf (Tab(hwnd))
                        edgeIconProtection.[edge] <- System.DateTime.Now.Add(iconClickHideProtection)
                        group.invokeAsync <| fun() ->
                            if not (TabEdges.isTopOnly this.ts.allowedEdges) then
                                this.ts.setShrunkOn(edge, true)
                capturedHwnd := Some(hwnd)
                // Track the first click for double-click detection
                // Only set if it's the active tab to ensure double-click only works on already active tabs
                if hwnd = group.topWindow then
                    firstClickTab := Some(hwnd)
                else
                    firstClickTab := None
            | MouseUp, MouseRight ->
                // From the strip the tab is on (the top strip's is group.hwnd).
                let ptScreen = this.ts.screenPointOf(Tab(hwnd), pt)
                // ONE scale for the whole menu, decided from the point the menu
                // is anchored at and handed to both the item bitmaps (which
                // Win32Menu resizes) and the swatches this decorator paints.
                // Right-clicking near the right edge of a strip can open the
                // menu on the NEXT monitor; deriving the swatch size from the
                // strip's scale and the bitmap size from the menu's scale then
                // meant painting a 24 px swatch only to have it reduced to 16.
                let menuScale = Dpi.scaleForPoint ptScreen
                group.bb.write("contextMenuVisible", true)
                let darkModeEnabled =
                    try
                        let json = Services.settings.root
                        match json.getBool("EnableDarkMode") with
                        | Some(value) -> value
                        | None -> false
                    with | _ -> false
                Win32Menu.show group.hwnd ptScreen menuScale (this.contextMenu(hwnd, menuScale)) darkModeEnabled
                group.bb.write("contextMenuVisible", false)
            | MouseDown, _ ->
                capturedHwnd := Some(hwnd)
            | MouseUp, MouseMiddle ->
                capturedHwnd.Value.iter <| fun capturedHwnd ->
                    if hwnd = capturedHwnd then
                        this.onCloseWindow hwnd
            | _ -> ()
        
        // WindowGroup's throttled, per-window LOCATIONCHANGE path saves these
        // bounds. Reuse it instead of installing a global location hook.
        group.bounds.changed.Add <| fun() ->
            this.updateTsPlacement()

        // With tabs on several edges a tab coming or going can empty or fill
        // the top strip, which moves the corner a side strip starts at and
        // changes what the top edge guard stands on. (The edges can be
        // changed in the dialog while the group lives, so asked each time.)
        group.added.Add <| fun _ ->
            if not (TabEdges.isTopOnly this.ts.allowedEdges) then this.invokeAsync (fun () -> this.updateTsPlacement())
        group.removed.Add <| fun _ ->
            if not (TabEdges.isTopOnly this.ts.allowedEdges) then this.invokeAsync (fun () -> this.updateTsPlacement())

        // Update placement when tab appearance changes (for height and indent values)
        Services.settings.notifyValue "tabAppearance" <| fun(_) ->
            this.invokeAsync <| fun() ->
                this.updateTsPlacement()

        Services.settings.notifyValue "tabVerticalDirection" <| fun(_) ->
            this.invokeAsync <| fun() ->
                this.updateTsPlacement()

        // The group carries the lock now; the dialog's value reaches it
        // through the group and comes back here as this event.
        group.lockChanged.Add <| fun() ->
            this.invokeAsync <| fun() ->
                this.updateTopEdgeGuard()

        // Raised on this group's thread by the desktop pass.
        group.desktopChanged.Add <| fun() ->
            this.updateTopEdgeGuard()

        // Observe the final layer after WindowGroup's foreground handler.
        group.foregroundChanged.Add <| fun() ->
            if group.lockWindowPosition then this.queueTopEdgeGuard()

        // Without tabs on the top edge the band is kept off a fullscreen
        // window as well (TopEdgeGuardPolicy.wantedFor).
        group.isFullscreen.changed.Add <| fun() ->
            if group.lockWindowPosition then this.queueTopEdgeGuard()

        group.exited.Add <| fun() ->
            Services.dragDrop.unregisterTarget(this.ts.hwnd)
            // The other edges' strips were taken off as they were destroyed
            // (ts.destroying, see init).
            guardRepairTimer.Stop()
            guardRepairTimer.Dispose()
            guardUpdates.Dispose()
            topEdgeGuard.dispose()


    // The strip of another edge as a place to drop on. Its points come in
    // its own window's coordinates and go on as the strip lays its tabs out
    // (along it, then across). The start and end of a drag are the group's
    // and reach it once, through the top strip's registration.
    member private this.edgeDropTarget (edge: TabEdges.Edge) : IDragDropTarget =
        let logical (pt: Pt) =
            let bounds = os.windowFromHwnd(this.ts.hwndOf edge).bounds
            if TabEdges.runsHorizontally edge then Pt(max 0 (min (max 0 (bounds.width - 1)) pt.x), pt.y)
            else Pt(max 0 (min (max 0 (bounds.height - 1)) pt.y), pt.x)
        { new IDragDropTarget with
            member x.dragBegin() = ()
            member x.dragEnd() = ()
            member x.dragEnter data pt =
                let logicalPt = group.invokeSync (fun () -> logical pt)
                this.dragEnterOn(edge, data, logicalPt)
            member x.dragMove pt =
                group.invokeSync (fun () ->
                    // Body routing supplies a real off-strip point. Never turn
                    // it into a synthetic hover over a scroll button.
                    let strip = os.windowFromHwnd(this.ts.hwndOf edge)
                    if strip.bounds.containsPoint(strip.ptToScreen pt) then
                        this.ts.dragScrollAcross(edge, pt, System.Diagnostics.Stopwatch.GetTimestamp() * 1000L / System.Diagnostics.Stopwatch.Frequency)
                    else this.ts.stopDragScroll()
                    this.dragMoveOn(logical pt))
            member x.dragExit() =
                group.invokeSync(fun () -> this.ts.stopDragScroll())
                this.dragExitOn()
          interface IGroupDragTarget with
            member x.group = box this
            member x.runsAcross = TabEdges.runsHorizontally edge
          }

    // The values of the allowed edges other than the top, and the top edge's
    // own [right] distances, as the settings file has them now.
    member private this.readEdgeValues (root: Newtonsoft.Json.Linq.JObject) (allowed: TabEdges.Allowed) =
        edgeValues <-
            TabEdges.edgesOf allowed
            |> List.filter ((<>) TabEdges.Edge.Top)
            |> List.map (fun edge -> edge, TabEdgeSettings.valuesOf root edge)
            |> Map.ofList
        let defaults =
            [ TabEdges.Edge.Left; TabEdges.Edge.Right; TabEdges.Edge.Bottom ]
            |> List.map (fun edge ->
                let layout = TabEdgeSettings.edgeLayout root edge
                edge, (if layout.defaultAlign = TabEdges.AlignStart then TopLeft else TopRight))
            |> Map.ofList
        this.ts.setEdgeDefaultAlignments(defaults)

    // A window for each allowed edge other than the top that has none yet,
    // registered for drops and for files dropped on its tabs.
    member private this.addEdgeWindows() =
        let existing = this.ts.edgeHwnds
        for edge in edgeValues |> Map.toList |> List.map fst do
            let hwnd = this.ts.addEdge(edge)
            if not (List.contains hwnd existing) then
                Services.dragDrop.registerTarget(hwnd, this.edgeDropTarget edge)
                // Files dropped on a tab of this strip, as on the top strip's.
                let ole = try Some(OleDropTarget(this.ts, hwnd, fun pt -> this.ts.tryHitOn(edge, pt))) with _ -> None
                edgeRegistrations <- edgeRegistrations @ [ hwnd, ole ]

    // The dialog changed the edges (TabEdgeSettings.changed); on this
    // group's thread. A newly checked edge gets its strip; the tabs of an
    // edge no longer checked go to the first checked edge in priority order,
    // behind the tabs already there, in their order and with their pin,
    // using the destination default, one unchecked edge at a time;
    // the strips are placed again with the edges' new values. The strip of
    // an unchecked edge stays, empty and hidden: the drag of a group that is
    // under way holds the targets it began with.
    member private this.applyEdgeSettings() =
        try
            let root = Services.settings.root
            let before = this.ts.allowedEdges
            let allowed = try TabEdgeSettings.allowedOf root with _ -> TabEdges.topOnly
            this.ts.setAllowedEdges(allowed)
            this.readEdgeValues root allowed
            this.addEdgeWindows()
            if allowed <> before then this.ts.moveOffUnchecked(allowed)
            this.updateTsPlacement()
        with ex ->
#if DEBUG
            System.Diagnostics.Debug.WriteLine(sprintf "Applying edge settings failed: %s" (ex.ToString()))
#endif
            ()

    // Along a strip written across, a tab grabbed on a longer tab is slid
    // by the middle of its row (TabStrip.alongOffset); on every other strip
    // by the offset it was grabbed at, as always.
    member private this.tabSlide =
        dragInfoCell.value.map <| fun dragInfo ->
                this.ts.slideFromPointer(dragInfo.tab, dragPtCell.value, dragInfo.tabOffset)

    member private this.updateTsSlide() =
        this.ts.slide <- this.tabSlide

    // The invisible sliver over the window's top border (see TopEdgeGuard).
    // It follows the same events as the strip, so it is updated from the same
    // place. Downward tabs keep input priority over the band.
    member private this.queueTopEdgeGuard() =
        topEdgeGuard.auditDecision(if guardRepairTimer.Enabled then "repair-request-coalesced" else "repair-timer-start")
        if not guardRepairTimer.Enabled then guardRepairTimer.Start()

    member private this.updateTopEdgeGuard() =
        try
            // A group whose tabs are all on other edges has no top strip to
            // show, and the band still guards the top border (7-3).
            let view =
                if this.ts.topShows then TopEdgeGuardPolicy.TopStrip(this.ts.showInside,
                    this.ts.visible || (group.bounds.value.IsSome && os.foreground.hwnd <> group.topWindow))
                else TopEdgeGuardPolicy.NoTopTabs
            let wanted =
                TopEdgeGuardPolicy.wantedFor group.lockWindowPosition group.bounds.value.IsSome
                    group.isInMoveSizeThreadSafe group.isFullscreen.value view
                // A group of another virtual desktop guards nothing: a window
                // shown on all desktops is guarded by the lock of the group of
                // the desktop being looked at, and only by that one.
                && group.isDesktopShownThreadSafe
            let ownerHwnd = if wanted then group.topWindow else IntPtr.Zero
            // Only the window in front decides the band: its own margin, not
            // the group's. A group holding LINE and Chrome guards LINE's outer
            // frame while LINE shows, and just Chrome's top border while
            // Chrome does - Chrome is above LINE's frame windows then, so they
            // cannot be reached anyway.
            let marginTop =
                match group.bounds.value with
                | Some(b) when wanted ->
                    (try
                        let (top, _, _, _) = group.getWindowMargin(ownerHwnd, b)
                        max 0 top
                     with _ -> 0)
                | _ -> 0
            topEdgeGuard.update(wanted, ownerHwnd, group.bounds.value,
                                marginTop, (ownerHwnd <> IntPtr.Zero && os.windowFromHwnd(ownerHwnd).isTopMost), not group.isInMoveSizeThreadSafe,
                                TopEdgeGuardPolicy.behindStrip view, this.ts.hwnd,
                                ?ownerBounds=(if wanted then group.visibleBodyBounds(ownerHwnd) else None))
        with _ -> topEdgeGuard.hide()

    member private this.updateTsPlacement() =
        this.ts.batch (fun () ->
            if group.bounds.value.IsNone then
                this.ts.visible <- false
            else
                // Appearance and placement are pushed together, from ONE scale
                // query, so the strip box and the metrics inside it can never
                // belong to different monitors. Pushing the appearance on every
                // update (not only when the user edits the settings) is what makes
                // dragging a window to a monitor with a different scale re-lay-out
                // the tabs instead of just resizing the box around stale metrics.
                let placement = this.placement
                this.ts.setTabAppearance(group.tabAppearanceAt placement.scale, placement.scale)
                this.ts.setPlacement(placement)
                // WindowGroup is the single owner of visibility policy. Keeping
                // placement updates on the same decision prevents move-state races.
                this.ts.visible <- group.shouldShowTabs
                if TabEdges.isTopOnly this.ts.allowedEdges then group.repairStripZorder("placement")

                // The strips of the other edges (none while the top edge is the
                // only one): placed from the same window rectangle, and kept in
                // the top strip's layer.
                if not (List.isEmpty this.ts.edgeHwnds) then
                    this.updateSidePlacements(placement)

            this.updateTopEdgeGuard()
        )
        // Rendering and side placement must finish before repairing the stack.
        if not (TabEdges.isTopOnly this.ts.allowedEdges) then group.repairStripZorder("placement")

    // The visible frame excludes invisible resize borders.
    member private this.visibleFrame (hwnd: IntPtr) (windowBounds: Rect) : TabEdgeLayout.Box =
        match group.visibleBodyBounds(hwnd) with
        | Some body -> (body.x, body.y, body.width, body.height)
        | None ->
            let (insetLeft, insetTop, insetRight, insetBottom) = this.frameInsets
            (windowBounds.location.x + insetLeft,
             windowBounds.location.y + insetTop,
             max 1 (windowBounds.size.width - insetLeft - insetRight),
             max 1 (windowBounds.size.height - insetTop - insetBottom))

    // The scale of the display an edge's side of the frame is on.
    static member private sideScale (edge: TabEdges.Edge) ((fx, fy, fw, fh): TabEdgeLayout.Box) (fallback: float) =
        let side =
            match edge with
            | TabEdges.Edge.Left -> Rect(Pt(fx, fy), Sz(1, fh))
            | TabEdges.Edge.Right -> Rect(Pt(fx + fw - 1, fy), Sz(1, fh))
            | TabEdges.Edge.Bottom -> Rect(Pt(fx, fy + fh - 1), Sz(fw, 1))
            | TabEdges.Edge.Top -> Rect(Pt(fx, fy), Sz(fw, 1))
        try Dpi.scaleForRect(side) with _ -> fallback

    /// One edge's strip as TabEdgeLayout places it, and the appearance it is
    /// drawn with, at `scale`: the edge's own values from the settings file
    /// over `current` (the colours, common to all edges) and `defaults` (9:
    /// the other edges start from the program's values; a left or right edge
    /// written across, from the rounded line - TabEdgeSettings.edgeShape).
    /// The heights, widths, overlap and distances all go through the edge's
    /// own scale (8, 10).
    static member edgeSpecOf(edge: TabEdges.Edge, values: TabEdgeSettings.EdgeValues, current: TabAppearanceInfo, defaults: TabAppearanceInfo, scale: float) =
        let appearance = TabStrip.edgeAppearance(TabEdgeSettings.withShape edge defaults.tabShape values, current, defaults, scale)
        let layout = TabEdgeSettings.layoutOf values (TabEdges.newEdgeLayout edge)
        let outside, inside = TabEdgeSettings.scaledDistances scale layout
        let across = layout.text = Some(TabEdges.SideText.Horizontal)
        let px = Dpi.px scale
        let textFont = Dpi.scaledFont SystemFonts.MenuFont scale
        let rows = TabEdgeLayout.rowsOf px appearance.tabHeight appearance.tabOverlap textFont.Height
        let iconOnly = TabEdgeLayout.iconOnlyRow px rows
        let thickness = if across then appearance.tabMaxWidth else appearance.tabHeight
        let spec : TabEdgeLayout.EdgeSpec =
            { edge = edge
              direction = layout.direction
              across = across
              thickness = thickness
              minThickness = (if across then iconOnly else thickness)
              outsideDistance = outside
              insideDistance = inside
              heightOffset = appearance.tabHeightOffset }
        spec, appearance

    /// The same destination-scale input builds every edge's snap metrics.
    static member snapReserveAt(edges: TabEdges.Edge list, values: Map<TabEdges.Edge, TabEdgeSettings.EdgeValues>,
                                current: TabAppearanceInfo, defaults: TabAppearanceInfo, scale: float, enabled: bool, topDirection: string) =
        let top = if List.contains TabEdges.Edge.Top edges && TabBehaviorPolicy.reserveTopMargin enabled topDirection then Some((Dpi.scaleAppearance scale current).tabHeight) else None
        let specs = edges |> List.filter ((<>) TabEdges.Edge.Top) |> List.map (fun edge ->
            fst (TabStripDecorator.edgeSpecOf(edge, defaultArg (values.TryFind edge) TabEdgeSettings.noValues, current, defaults, scale)))
        TabEdgeLayout.snapReserve enabled top specs

    member private this.edgeSpec (edge: TabEdges.Edge) (values: TabEdgeSettings.EdgeValues) (scale: float) =
        TabStripDecorator.edgeSpecOf(edge, values, group.tabAppearanceRaw, Services.program.defaultTabAppearanceInfo, scale)

    // The strips of the other edges, each where TabEdgeLayout.place puts it
    // for this frame and these displays: the bottom strip first, then the
    // left and right strips, which give the corners to the top and the
    // bottom strip (7-2). Each is drawn with its edge's appearance at the
    // scale of its side, inside or outside as its direction and the screen
    // decide (5).
    member private this.updateSidePlacements(top: TabStripPlacment) =
        match group.bounds.value, group.tryTopWindow with
        | Some(windowBounds), Some(hwnd) ->
            let frame = this.visibleFrame hwnd windowBounds
            let box (r: Rect) = r.x, r.y, r.width, r.height
            let monitors =
                TabEdgeLayout.placementMonitors false
                    (Mon.fromHwnd(hwnd) |> Option.map (fun m -> box m.workRect))
                    (Mon.all.list |> List.map (fun m -> box m.workRect))
            let topCorner =
                if this.ts.topShows then
                    let b = top.bounds
                    [ b.location.x, b.location.y, b.size.width, b.size.height ]
                else []
            let mutable bottomCorner = []
            for edge in [ TabEdges.Edge.Bottom; TabEdges.Edge.Left; TabEdges.Edge.Right ] do
                match edgeValues.TryFind(edge) with
                | Some(values) ->
                    let scale = TabStripDecorator.sideScale edge frame top.scale
                    let spec, appearance = this.edgeSpec edge values scale
                    let placed = TabEdgeLayout.place spec frame monitors (topCorner @ bottomCorner)
                    let (x, y, w, h) = placed.rect
                    this.ts.setTextBottomInside(edge, defaultArg values.textBottomInside true)
                    this.ts.setEdgePlacement(edge, Rect(Pt(x, y), Sz(w, h)), appearance, scale,
                                             { inside = placed.inside; across = spec.across })
                    if edge = TabEdges.Edge.Bottom && not (this.ts.bandOf edge).isEmpty then
                        bottomCorner <- [ placed.rect ]
                | None -> ()
        | _ -> ()

    // An edge's tabs look like the top edge's, with that edge's own size,
    // overlap and shape (edgeSpecOf).
    member private this.edgeAppearance (edge: TabEdges.Edge) (values: TabEdgeSettings.EdgeValues) (scale: float) =
        snd (this.edgeSpec edge values scale)

    // The invisible resize borders of the window in front: (left, top, right,
    // bottom) between GetWindowRect and the DWM extended frame. A side strip
    // stands against the visible frame, or it would float a few pixels away
    // from the window. Zero when DWM cannot say, and capped so that a bogus
    // answer cannot fling the strip across the screen.
    member private this.frameInsets : int * int * int * int =
        try
            let hwnd = group.topWindow
            let windowRect = os.windowFromHwnd(hwnd).bounds
            let mutable frame = RECT()
            let hr =
                DwmApi.DwmGetWindowAttribute(
                    hwnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &frame,
                    System.Runtime.InteropServices.Marshal.SizeOf(typeof<RECT>))
            if hr <> 0 then (0, 0, 0, 0)
            else
                let cap v = max 0 (min 32 v)
                (cap (frame.Left - windowRect.left), cap (frame.Top - windowRect.top),
                 cap (windowRect.right - frame.Right), cap (windowRect.bottom - frame.Bottom))
        with _ -> (0, 0, 0, 0)

    member private this.invokeAsync f = group.invokeAsync f
    member private this.invokeSync f = group.invokeSync f

    // Pure: reading the placement must not mutate the strip. (The scale and
    // appearance are pushed by updateTsPlacement, the single writer.)
    // Return type stated explicitly: TabStripSprite also has a `scale` field,
    // and F# would otherwise infer the most recently declared matching record.
    member this.placement : TabStripPlacment =
        let scale = group.dpiScale
        // One appearance read, so height / offset / indent and the scale handed
        // to the strip all describe the same monitor.
        let appearance = group.tabAppearanceAt scale
        let decorator =  {
            windowBounds = group.bounds.value.def(Rect())
            monitorBounds =
                List2(TabEdgeLayout.placementMonitors (TabEdges.isTopOnly this.ts.allowedEdges)
                    (group.tryTopWindow |> Option.bind Mon.fromHwnd |> Option.map (fun m -> m.workRect))
                    (Mon.all.list |> List.map (fun m -> m.workRect)))
            decoratorHeight = appearance.tabHeight
            decoratorHeightOffset = appearance.tabHeightOffset
            decoratorIndentFlipped = appearance.tabIndentFlipped
            decoratorIndentFlippedRight = appearance.tabIndentFlippedRight
            decoratorIndentNormal = appearance.tabIndentNormal
            decoratorIndentNormalRight = appearance.tabIndentNormalRight
        }
        let verticalDirection =
            try Services.settings.getValue("tabVerticalDirection") :?> string
            with _ -> TabBehaviorPolicy.verticalAuto
        let inside, bounds = decorator.placementFor(verticalDirection, not (TabEdges.isTopOnly this.ts.allowedEdges))
        {
            showInside = inside
            bounds = bounds
            scale = scale
        }

    member this.beginRename(hwnd) =
        let tab = Tab(hwnd)
        // A tab of a left strip is written upwards; its box is put beside it
        // (TabStrip.sideRenameRect) instead of over its text.
        let sideBox = this.ts.sideRenameRect tab
        let textBounds =
            match sideBox with
            | Some(box) -> box
            | None ->
                this.ts.sprite.children.pick <| fun (tabOffset, tabSprite) ->
                    let tabSprite = tabSprite :?> TabSprite<Tab>
                    if tabSprite.id = tab then
                        Some(Rect(tabSprite.textLocation.add(tabOffset), tabSprite.textSize))
                    else None
        let scale = this.ts.scale
        let verticalMargin = Dpi.px scale 2

        let form = new FloatingTextBox()

        // The rename box overlays the tab's own text, so it has to match the
        // strip exactly. Both are in device pixels for the strip's monitor now:
        // textBounds comes from the DPI-scaled sprite layout, and the font is
        // the same pixel-sized menu font the strip draws with (a point-sized
        // font would follow the system DPI here, because this form renders
        // through a screen DC rather than into an Img).
        //
        // AutoScaleMode is forced off because the designer sets
        // AutoScaleMode.Font (FloatingTextBox.Designer.cs); WinForms would
        // otherwise apply its own font-ratio scaling on top of the values
        // below. The historical bug this replaces was the same double scaling
        // seen from the other side: the old code multiplied by the DPI scale
        // and the OS scaled the unaware window again, landing at pt * 2.25.
        form.AutoScaleMode <- AutoScaleMode.None
        form.textBox.Font <- Dpi.scaledFont SystemFonts.MenuFont scale

        // Convert the tab's text client position to screen coordinates.
        let mutable pt = POINT(X = textBounds.location.x, Y = textBounds.location.y + verticalMargin)
        if sideBox.IsNone then
            WinUserApi.ClientToScreen(this.ts.hwnd, &pt) |> ignore

        form.Location <- Point(pt.X, pt.Y)
        form.SetSize(Size(textBounds.size.width, textBounds.size.height - 2 * verticalMargin))
        let tabText = this.ts.tabInfo(Tab(hwnd)).text
        // Confirming the unchanged name is treated as cancel: it must not
        // register a rename override for a name the tab already shows
        let commit (newName: string) =
            if newName <> tabText then
                group.setTabName(hwnd, if newName.Length = 0 then None else Some(newName))
        form.textBox.KeyPress.Add <| fun e ->
            if e.KeyChar = char(Keys.Enter) then
                commit form.textBox.Text
                form.Close()
            elif e.KeyChar = char(Keys.Escape) then
                form.Close()
        form.textBox.Text <- tabText
        form.textBox.SelectionStart <- 0
        form.textBox.SelectionLength <- tabText.Length
        form.textBox.LostFocus.Add <| fun _ ->
            commit form.textBox.Text
            form.Close()
        group.bb.write("renamingTab", true)
        form.Closed.Add <| fun _ ->
            group.bb.write("renamingTab", false)
        form.Show()

    member private this.onCloseWindow hwnd =
        os.windowFromHwnd(hwnd).close()

    // Compute the set of hwnds an "operate on this tab" command should
    // target. When no multi-select is active the behaviour is unchanged
    // (just the right-clicked hwnd). When a multi-select is active the
    // command propagates to "active tab + selected tabs", and the
    // right-clicked hwnd is unioned in so the menu's named target is
    // always included.
    member private this.actionTargets(hwnd: IntPtr) : List<IntPtr> =
        let selected = group.selectedTabs
        if selected.count = 0 then [hwnd]
        else
            let targets = group.actionTargetTabs()
            if targets |> List.contains hwnd then targets
            else hwnd :: targets

    // Label for a multi-select menu item. When the right-clicked tab is part
    // of the selection (or is the active tab) the plain "Selected {N} tabs"
    // format already covers it; when the menu added it on top of the
    // selection, use the "...PlusTargetFormat" variant where {N} counts only
    // the selection and the target tab is named separately.
    member private this.formatSelectedTabs(hwnd: IntPtr, targets: List<IntPtr>, formatKey: string) =
        if group.actionTargetTabs() |> List.contains hwnd then
            String.Format(Localization.getString(formatKey), targets.Length)
        else
            let plusKey = formatKey.Substring(0, formatKey.Length - "Format".Length) + "PlusTargetFormat"
            String.Format(Localization.getString(plusKey), targets.Length - 1)

    // Multi-select drag-reorder: from the drag-origin tab, expand left and
    // right through visualOrder as long as the next tab is selected or
    // active, AND shares the same pin state AND same alignment. Used by
    // dragBegin to set the visual drag group and by dragEnd to splice-move
    // the whole range.
    // Variant A (-4): pin + alignment boundary, with synchronized visual
    // slide of all group members.
    member private this.contiguousSelectedFromTab(origin: Tab) : Tab list =
        match group.tryTopWindow with
        | None -> []
        | Some activeHwnd ->
            let inSelection (Tab h) =
                h = activeHwnd || group.selectedTabs.contains h
            if not (inSelection origin) then [origin]
            else
                // ... and the same edge: a block is dragged along one strip.
                let samePinAlign (a: Tab) (b: Tab) =
                    this.ts.isPinned a = this.ts.isPinned b &&
                    this.ts.getTabAlign a = this.ts.getTabAlign b &&
                    this.ts.edgeOf a = this.ts.edgeOf b
                let vo = this.ts.visualOrder.list
                let n = vo.Length
                match vo |> List.tryFindIndex ((=) origin) with
                | None -> [origin]
                | Some originIdx ->
                    let mutable s = originIdx
                    while s > 0 && inSelection vo.[s-1] && samePinAlign vo.[s-1] origin do
                        s <- s - 1
                    let mutable e = originIdx
                    while e < n - 1 && inSelection vo.[e+1] && samePinAlign vo.[e+1] origin do
                        e <- e + 1
                    vo |> List.skip s |> List.take (e - s + 1)

    member private this.onCloseOtherWindows hwnd =
        let edge = this.ts.edgeOf(Tab hwnd)
        (this.ts.bandOf edge).list
        |> List.iter (fun (Tab h) -> if h <> hwnd then this.onCloseWindow h)

    // "To the right / left" is along the tab's own strip, never across
    // edges (3-3); the whole order while the top edge is the only one.
    member private this.onCloseRightTabWindows hwnd =
        let currentTab = Tab(hwnd)
        let vo = this.ts.bandOf (this.ts.edgeOf currentTab)
        let tabIndex = vo.tryFindIndex((=) currentTab)
        tabIndex.iter <| fun index ->
            let rightTabs = vo.skip(index + 1)
            rightTabs.iter <| fun tab ->
                let (Tab(tabHwnd)) = tab
                this.onCloseWindow tabHwnd

    member private this.onCloseLeftTabWindows hwnd =
        let currentTab = Tab(hwnd)
        let vo = this.ts.bandOf (this.ts.edgeOf currentTab)
        let tabIndex = vo.tryFindIndex((=) currentTab)
        tabIndex.iter <| fun index ->
            let leftTabs = vo.take(index)
            leftTabs.iter <| fun tab ->
                let (Tab(tabHwnd)) = tab
                this.onCloseWindow tabHwnd

    member private this.onCloseAllWindows(?edge: TabEdges.Edge) =
        group.windows.items.iter (fun hwnd ->
            if edge |> Option.forall (fun e -> this.ts.edgeOf(Tab hwnd) = e) then
                this.onCloseWindow hwnd)

    // Helper methods for multi-display detach support.
    //
    // Live EnumDisplayMonitors, not the WinForms Screen cache: the answer has
    // to be current (a monitor may have just been switched on or off) and in
    // this thread's coordinate space (see MonitorScreen).
    member private this.getAllScreensSorted() : MonitorScreen[] =
        Mon.all.list
        |> List.map MonitorScreen
        |> List.sortBy (fun screen -> (screen.Bounds.X, screen.Bounds.Y))
        |> Array.ofList

    member private this.getScreenName(screen: MonitorScreen) =
        let centerX = screen.Bounds.Left + screen.Bounds.Width / 2
        let centerY = screen.Bounds.Top + screen.Bounds.Height / 2

        let directions =
            [
                if centerX < 0 then Localization.getString("Left")
                if centerX > screen.Bounds.Width then Localization.getString("Right")
                if centerY < 0 then Localization.getString("Up")
                if centerY > screen.Bounds.Height then Localization.getString("Down")
            ]

        let display = Localization.getString("Display")

        if screen.Bounds.Top = 0 && screen.Bounds.Left = 0 || directions.IsEmpty then
            let main = Localization.getString("Main")
            match Localization.currentLanguage with
            | "Japanese" -> main + display
            | _ -> display + " " + main
        else
            let directionStr =
                match Localization.currentLanguage with
                | "Japanese" -> String.concat "" directions
                | _ -> String.concat " " directions
            match Localization.currentLanguage with
            | "Japanese" -> directionStr + display
            | _ -> display + " " + directionStr

    member private this.getCurrentScreenForWindow(hwnd: IntPtr) : MonitorScreen =
        let window = os.windowFromHwnd(hwnd)
        let bounds = window.bounds
        let centerX = bounds.location.x + bounds.size.width / 2
        let centerY = bounds.location.y + bounds.size.height / 2
        // MONITOR_DEFAULTTONEAREST: a live query that always answers, so there
        // is no fallback branch to get wrong. (Screen.FromPoint would consult
        // the cached, possibly virtualized WinForms array - see MonitorScreen.)
        match Mon.nearestFromPoint(Pt(centerX, centerY)) with
        | Some(mon) -> MonitorScreen(mon)
        | None ->
            // Unreachable while at least one display is attached. Fall back to
            // the first enumerated monitor rather than throwing out of a menu
            // build; the previous Screen.FromPoint could not fail either, and
            // an indexed array access would have been a worse replacement.
            match Mon.all.list with
            | mon :: _ -> MonitorScreen(mon)
            | [] -> MonitorScreen(Mon(IntPtr.Zero))

    /// Calculate new position based on position option and work area
    /// Returns (x, y) tuple
    member private this.calculatePositionInWorkArea(
        position: Option<string>,
        workArea: System.Drawing.Rectangle,
        currentX: int,
        currentY: int,
        width: int,
        height: int,
        ?ofGroup: bool) : (int * int) =

        let topOnly = this.useTopOnlySnap(defaultArg ofGroup false)
        let originalArea = workArea
        let workArea =
            if topOnly then workArea
            else this.adjustWorkAreaForTabHeight(workArea, ofGroup = defaultArg ofGroup false)
        let tabOffset = if topOnly then this.getTabHeightForSnap() else 0
        let minY = workArea.Top + tabOffset
        // Preserve leading tab margins when the group is too large, without resizing.
        let bottomY =
            if tabOffset > 0 || workArea.Top > originalArea.Top then max minY (workArea.Bottom - height)
            else workArea.Bottom - height
        let rightX =
            if workArea.Left > originalArea.Left then max workArea.Left (workArea.Right - width)
            else workArea.Right - width
        match position with
        | Some "right" ->
            let x = rightX
            let y = max minY (min currentY (workArea.Bottom - height))
            (x, y)
        | Some "left" ->
            let x = workArea.Left
            let y = max minY (min currentY (workArea.Bottom - height))
            (x, y)
        | Some "top" ->
            let x = max workArea.Left (min currentX (workArea.Right - width))
            let y = minY
            (x, y)
        | Some "bottom" ->
            let x = max workArea.Left (min currentX (workArea.Right - width))
            let y = bottomY
            (x, y)
        // Corner positions
        | Some "topright" ->
            let x = rightX
            let y = minY
            (x, y)
        | Some "topleft" ->
            let x = workArea.Left
            let y = minY
            (x, y)
        | Some "bottomright" ->
            let x = rightX
            let y = bottomY
            (x, y)
        | Some "bottomleft" ->
            let x = workArea.Left
            let y = bottomY
            (x, y)
        | _ ->
            (currentX, currentY)

    // A cross-display move must keep the same reserved area during its final clamp.
    // Same-position moves keep the original, unreserved work-area clamp.
    member private this.clampPositionInWorkArea(position: string option, workArea: System.Drawing.Rectangle,
                                                x: int, y: int, width: int, height: int, ?ofGroup: bool) =
        let area =
            if position.IsNone then workArea
            elif this.useTopOnlySnap(defaultArg ofGroup false) then
                let offset = this.getTabHeightForSnap()
                System.Drawing.Rectangle(workArea.X, workArea.Y + offset, workArea.Width, workArea.Height - offset)
            else this.adjustWorkAreaForTabHeight(workArea, ofGroup = defaultArg ofGroup false)
        max area.Left (min x (area.Right - width)), max area.Top (min y (area.Bottom - height))

    member private this.detachTabToScreen(hwnd: IntPtr, targetScreen: MonitorScreen, position: Option<string>) =
        // This method is only called when group has multiple tabs (menu is disabled for single tab)
        if group.windows.items.count <= 1 then
            raise (System.InvalidOperationException("detachTabToScreen should not be called when there is only one tab"))

        let window = os.windowFromHwnd(hwnd)
        let margin = group.capturePlacementMargin(hwnd)
        let bounds = FramePlacement.read hwnd margin (FramePlacement.restoredBounds window)
        let sourceScreen = this.getCurrentScreenForWindow(hwnd)
        let sourceWorkArea = sourceScreen.WorkingArea
        let widthPercent = float(bounds.size.width) / float(sourceWorkArea.Width)
        let heightPercent = float(bounds.size.height) / float(sourceWorkArea.Height)
        let topPercent = float(bounds.location.y - sourceWorkArea.Top) / float(sourceWorkArea.Height)

        let tab = Tab(hwnd)
        Services.program.suspendTabMonitoring()

        try
            // Remove tab from current group
            this.ts.removeTab(tab)
            group.removeWindow(hwnd)

            window.hideOffScreen(None)

            if window.isMinimized || window.isMaximized then
                window.showWindow(ShowWindowCommands.SW_RESTORE)

            // Calculate new size based on target screen
            let targetWorkArea = targetScreen.WorkingArea
            let newWidth = int(float(targetWorkArea.Width) * widthPercent)
            let newHeight = int(float(targetWorkArea.Height) * heightPercent)

            // Calculate current position using percentages
            let leftPercent = float(bounds.location.x - sourceWorkArea.Left) / float(sourceWorkArea.Width)
            let currentX = targetWorkArea.Left + int(float(targetWorkArea.Width) * leftPercent)
            let currentY = targetWorkArea.Top + int(float(targetWorkArea.Height) * topPercent)

            // Calculate new position based on position option
            let newLeft, newTop = this.calculatePositionInWorkArea(
                position,
                targetWorkArea,
                currentX,
                currentY,
                newWidth,
                newHeight)

            // Ensure position is within bounds
            let finalX, finalY = this.clampPositionInWorkArea(
                position, targetWorkArea, newLeft, newTop, newWidth, newHeight)

            // DPI-aware window placement: move position first, wait for DPI change, then set size
            let initialDpi = WinUserApi.GetDpiForWindow(hwnd)
            window.setPositionOnly finalX finalY

            // Wait for DPI change (max 200ms)
            let mutable currentDpi = initialDpi
            let mutable elapsed = 0
            while elapsed < 200 && currentDpi = initialDpi do
                System.Threading.Thread.Sleep(10)
                elapsed <- elapsed + 10
                currentDpi <- WinUserApi.GetDpiForWindow(hwnd)

            if currentDpi <> initialDpi then
                System.Threading.Thread.Sleep(20)

            FramePlacement.moveDetached window margin (Rect(Pt(finalX, finalY), Sz(newWidth, newHeight)))
            notifyDetached(hwnd)

        finally
            Services.program.resumeTabMonitoringAfter(200)

    // Resume on the source thread only after the destination has finished restoring.
    member private this.linkFromMenu(targetGroup: WindowGroup, targets: IntPtr list, action: unit -> unit) =
        menuLinkGeneration <- menuLinkGeneration + 1
        let generation = menuLinkGeneration
        let enabled() = not Services.program.isDisabled && not Services.program.isShuttingDown
        let alive() = WinUserApi.IsWindow(group.hwnd) && WinUserApi.IsWindow(targetGroup.hwnd)
        let valid() =
            alive() && not targets.IsEmpty &&
            (targets |> List.forall (fun h -> WinUserApi.IsWindow(h) && group.windows.contains h)) &&
            (targetGroup.zorder.value.tryHead |> Option.exists WinUserApi.IsWindow)
        let linked() =
            let state = VirtualDesktopGroups.Live.snapshot()
            not targetGroup.isDesktopShownThreadSafe ||
            (VirtualDesktopGroups.keepAway && VirtualDesktopGroups.Live.isFresh state &&
             (targets |> List.forall (fun h -> (VirtualDesktopGroups.Live.read state h).presence = VirtualDesktopGroups.Away)))
        let minimized() = targetGroup.zorder.value.tryHead |> Option.exists (fun h -> (os.windowFromHwnd(h)).isMinimized)
        let abort reason = DragTrace.log (fun () -> "menu-link-cancel reason=" + reason)
        if not (enabled()) then abort "disabled-or-shutting-down"
        elif not (valid()) then abort "invalid-targets"
        elif linked() || not (minimized()) then action()
        else
            try
                targetGroup.restoreForMenuLinkAsync(group.topWindow,
                    (fun () -> alive() && targetGroup.isDesktopShownThreadSafe),
                    (fun success ->
                        group.invokeAsync(fun () ->
                            if generation <> menuLinkGeneration then abort "superseded"
                            elif not (enabled()) then abort "disabled-or-shutting-down"
                            elif not success then abort "restore-not-completed"
                            elif not (valid()) then abort "invalid-targets"
                            elif linked() || minimized() then abort "destination-changed"
                            else action())))
            with _ -> abort "post-exception"

    member private this.moveTabToGroup(hwnd: IntPtr, targetGroup: WindowGroup) =
        // Move tab to another group if it's a different group
        if targetGroup.hwnd <> group.hwnd then
            let tab = Tab(hwnd)
            let window = os.windowFromHwnd(hwnd)

            let state = VirtualDesktopGroups.Live.snapshot()
            let away = VirtualDesktopGroups.keepAway && VirtualDesktopGroups.Live.isFresh state &&
                       (VirtualDesktopGroups.Live.read state hwnd).presence = VirtualDesktopGroups.Away
            let linked = away || not targetGroup.isDesktopShownThreadSafe

            try
                // Suspend tab monitoring to prevent auto-grouping during the move
                Services.program.suspendTabMonitoring()

                try
                    // First ensure the window is not in the target group already
                    if targetGroup.windows.contains hwnd then
                        ()
                    else
                        // Store original window state
                        let wasMinimized = window.isMinimized
                        let wasMaximized = window.isMaximized

                        // Remove tab from current group first (ensure it's actually removed)
                        if this.ts.tabs.contains(tab) then
                            this.ts.removeTab(tab)
                        if group.windows.contains hwnd then
                            group.removeWindow(hwnd)

                        // Wait for removal to complete
                        System.Threading.Thread.Sleep(50)

                        // Hide window temporarily to prevent flashing
                        if not linked then window.hideOffScreen(None)

                        // Restore window state if necessary
                        if not linked && (wasMinimized || wasMaximized) then
                            window.showWindow(ShowWindowCommands.SW_RESTORE)

                        // Use synchronous invoke to ensure completion
                        let moveCompleted = ref false
                        let moveException = ref None

                        targetGroup.invokeSync(fun() ->
                            try
                                // Double-check window is not already in target group
                                if not (targetGroup.windows.contains hwnd) then
                                    let edge = targetGroup.joinEdge
                                    let alignment =
                                        targetGroup.ts.visualOrder.list
                                        |> List.filter (fun t -> targetGroup.ts.edgeOf t = edge)
                                        |> List.tryLast
                                        |> Option.map targetGroup.ts.getTabAlign
                                    if linked then
                                        targetGroup.addWindowLinked(hwnd, false, ?alignment=alignment, edge=edge)
                                    else
                                        targetGroup.addWindow(hwnd, false, ?alignment=alignment, edge=edge)
                                    // Show window again (target group will handle positioning)
                                    if not away then window.showWindow(ShowWindowCommands.SW_SHOW)
                                    moveCompleted := true
                            with ex ->
                                moveException := Some ex
                        )

                        // Check if move failed
                        match !moveException with
                        | Some ex ->
                            // Restore to original group on failure
                            System.Diagnostics.Debug.WriteLine(sprintf "Move failed, restoring to original group: %s" ex.Message)
                            group.addWindow(hwnd, false)
                            if not away then window.showWindow(ShowWindowCommands.SW_SHOW)
                            raise ex
                        | None when not !moveCompleted ->
                            // Move didn't complete, restore
                            group.addWindow(hwnd, false)
                            if not away then window.showWindow(ShowWindowCommands.SW_SHOW)
                        | None ->
                            // Move successful - wait a bit for UI to update
                            System.Threading.Thread.Sleep(100)

                            // Move successful - no need to update group info here
                            // as it will be updated when menu is opened

                finally
                    // Resume tab monitoring
                    Services.program.resumeTabMonitoring()
            with ex ->
                System.Diagnostics.Debug.WriteLine(sprintf "Error moving tab: %s" ex.Message)
                // Resume monitoring even on error
                try Services.program.resumeTabMonitoring() with _ -> ()

    member private this.moveTabGroupToGroup(targetGroup: WindowGroup, tabsToMove: IntPtr list) =
        // Move all tabs from current group to target group
        if targetGroup.hwnd <> group.hwnd then
            try
                // Suspend tab monitoring to prevent auto-grouping during the move
                Services.program.suspendTabMonitoring()

                try
                    // Use the visual-order snapshot validated by the menu entry.
                    // Move each tab to target group
                    tabsToMove |> List.iter (fun hwnd ->
                        this.moveTabToGroup(hwnd, targetGroup)
                    )
                finally
                    // Resume tab monitoring
                    Services.program.resumeTabMonitoring()
            with ex ->
                System.Diagnostics.Debug.WriteLine(sprintf "Error moving tab group: %s" ex.Message)
                // Resume monitoring even on error
                try Services.program.resumeTabMonitoring() with _ -> ()

    member private this.detachTabToPosition(hwnd: IntPtr, position: Option<string>) =
        // This method is only called when group has multiple tabs (menu is disabled for single tab)
        if group.windows.items.count <= 1 then
            raise (System.InvalidOperationException("detachTabToPosition should not be called when there is only one tab"))

        let window = os.windowFromHwnd(hwnd)
        let margin = group.capturePlacementMargin(hwnd)
        let bounds = FramePlacement.read hwnd margin (FramePlacement.restoredBounds window)
        let screen = this.getCurrentScreenForWindow(hwnd)

        let tab = Tab(hwnd)
        Services.program.suspendTabMonitoring()

        try
            this.ts.removeTab(tab)
            group.removeWindow(hwnd)

            window.hideOffScreen(None)

            if window.isMinimized || window.isMaximized then
                window.showWindow(ShowWindowCommands.SW_RESTORE)

            FramePlacement.moveDetached window margin bounds

            let (newX, newY) = this.calculatePositionInWorkArea(
                position,
                screen.WorkingArea,
                bounds.location.x,
                bounds.location.y,
                bounds.size.width,
                bounds.size.height)

            if newX <> bounds.location.x || newY <> bounds.location.y then
                FramePlacement.moveDetached window margin (Rect(Pt(newX,newY),bounds.size))

            notifyDetached(hwnd)

        finally
            Services.program.resumeTabMonitoringAfter(200)

    // ----- Multi-tab detach: shared core -----
    // Generic multi-tab detach. Takes an explicit list of hwnds (in visual
    // order) and detaches them all into a single new tab group; the seed
    // window's final placement (position / snap / move) is decided by the
    // caller via `applyToSeedWindow`. Detaches the first tab to seed a new
    // group, then moves the remaining tabs into that group once it has
    // been created.
    // Caller must guarantee tabsInOrder has at least 2 entries (single-tab
    // callers go through the original single-tab detach helpers).
    member private this.detachTabsCommon(
            tabsInOrder: List<IntPtr>,
            applyToSeedWindow: MonitorScreen -> Rect -> (Rect -> unit) -> unit
        ) =
        match tabsInOrder with
        | [] | [_] -> ()
        | firstHwnd :: remainingHwnds ->
            let window = os.windowFromHwnd(firstHwnd)
            let margins = tabsInOrder |> List.map (fun h -> h, group.capturePlacementMargin(h)) |> Map.ofList
            let margin = margins.[firstHwnd]
            let bounds = FramePlacement.read firstHwnd margin (FramePlacement.restoredBounds window)
            let screen = this.getCurrentScreenForWindow(firstHwnd)

            // Step 1: detach seed tab from this group; place / snap window.
            Services.program.suspendTabMonitoring()
            try
                this.ts.removeTab(Tab(firstHwnd))
                group.removeWindow(firstHwnd)

                window.hideOffScreen(None)

                if window.isMinimized || window.isMaximized then
                    window.showWindow(ShowWindowCommands.SW_RESTORE)

                applyToSeedWindow screen bounds (FramePlacement.moveDetached window margin)

                notifyDetached(firstHwnd)
            finally
                Services.program.resumeTabMonitoring()

            // Step 2: wait for the new group to be created.
            let mutable newGroupFound = None
            let mutable attempts = 0
            let maxAttempts = 50
            while newGroupFound.IsNone && attempts < maxAttempts do
                System.Threading.Thread.Sleep(20)
                attempts <- attempts + 1
                newGroupFound <- lock decorators (fun () ->
                    decorators.Values
                    |> Seq.tryFind (fun d ->
                        d.group.windows.contains firstHwnd && d.group.hwnd <> group.hwnd))

            // Step 3: move the remaining tabs into the freshly created group.
            match newGroupFound with
            | Some targetDecorator ->
                Services.program.suspendTabMonitoring()
                try
                    remainingHwnds |> List.iter (fun tabHwnd ->
                        let tab = Tab(tabHwnd)
                        let tabWindow = os.windowFromHwnd(tabHwnd)

                        if this.ts.tabs.contains(tab) then
                            this.ts.removeTab(tab)
                        if group.windows.contains tabHwnd then
                            group.removeWindow(tabHwnd)

                        System.Threading.Thread.Sleep(50)

                        tabWindow.hideOffScreen(None)

                        targetDecorator.group.invokeSync(fun() ->
                            if not (targetDecorator.group.windows.contains tabHwnd) then
                                FramePlacement.prepare tabHwnd margins.[tabHwnd] targetDecorator.group.placementBounds
                                targetDecorator.group.addWindow(tabHwnd, false)
                                tabWindow.showWindow(ShowWindowCommands.SW_SHOW)))
                finally
                    Services.program.resumeTabMonitoring()
            | None -> ()

    // Specific multi-tab detach variants — each just defines the
    // applyToSeedWindow callback for detachTabsCommon.

    member private this.detachTabsToPosition(tabsInOrder: List<IntPtr>, position: Option<string>) =
        match tabsInOrder with
        | [] -> ()
        | [singleHwnd] -> this.detachTabToPosition(singleHwnd, position)
        | _ ->
            this.detachTabsCommon(tabsInOrder, fun screen bounds place ->
                place bounds
                let (newX, newY) = this.calculatePositionInWorkArea(
                    position, screen.WorkingArea,
                    bounds.location.x, bounds.location.y,
                    bounds.size.width, bounds.size.height)
                if newX <> bounds.location.x || newY <> bounds.location.y then
                    place (Rect(Pt(newX,newY),bounds.size)))

    member private this.detachTabsToSnap(tabsInOrder: List<IntPtr>, snapDirection: string) =
        match tabsInOrder with
        | [] -> ()
        | [singleHwnd] -> this.detachTabToSnap(singleHwnd, snapDirection)
        | _ ->
            this.detachTabsCommon(tabsInOrder, fun screen bounds place ->
                let (newX, newY, newWidth, newHeight) = this.calculateSnapBounds(
                    snapDirection, screen.WorkingArea,
                    bounds.size.width, bounds.size.height)
                place (Rect(Pt(newX, newY), Sz(newWidth, newHeight))))

    member private this.detachTabsToSnapWithPercent(tabsInOrder: List<IntPtr>, snapDirection: string, percent: int) =
        match tabsInOrder with
        | [] -> ()
        | [singleHwnd] -> this.detachTabToSnapWithPercent(singleHwnd, snapDirection, percent)
        | _ ->
            this.detachTabsCommon(tabsInOrder, fun screen _bounds place ->
                let (newX, newY, newWidth, newHeight) = this.calculateSnapBoundsWithPercent(
                    snapDirection, percent, screen.WorkingArea)
                place (Rect(Pt(newX, newY), Sz(newWidth, newHeight))))

    member private this.detachTabsToScreen(tabsInOrder: List<IntPtr>, targetScreen: MonitorScreen, position: Option<string>) =
        match tabsInOrder with
        | [] -> ()
        | [singleHwnd] -> this.detachTabToScreen(singleHwnd, targetScreen, position)
        | _ ->
            this.detachTabsCommon(tabsInOrder, fun sourceScreen bounds place ->
                // Mirror detachTabToScreen: maintain size %, move to target screen.
                let sourceWorkArea = sourceScreen.WorkingArea
                let widthPercent = float(bounds.size.width) / float(sourceWorkArea.Width)
                let heightPercent = float(bounds.size.height) / float(sourceWorkArea.Height)
                let targetWorkArea = targetScreen.WorkingArea
                let newWidth = int(float(targetWorkArea.Width) * widthPercent)
                let newHeight = int(float(targetWorkArea.Height) * heightPercent)
                // Preserve relative position within the work area before
                // applying the requested position adjustment.
                let xOffsetPercent = float(bounds.location.x - sourceWorkArea.X) / float(sourceWorkArea.Width)
                let yOffsetPercent = float(bounds.location.y - sourceWorkArea.Y) / float(sourceWorkArea.Height)
                let initialX = targetWorkArea.X + int(float(targetWorkArea.Width) * xOffsetPercent)
                let initialY = targetWorkArea.Y + int(float(targetWorkArea.Height) * yOffsetPercent)
                place (Rect(Pt(initialX, initialY), Sz(newWidth, newHeight)))
                let (newX, newY) = this.calculatePositionInWorkArea(
                    position, targetWorkArea, initialX, initialY, newWidth, newHeight)
                if newX <> initialX || newY <> initialY then
                    place (Rect(Pt(newX,newY),Sz(newWidth,newHeight))))

    member private this.detachTabsToScreenSnap(tabsInOrder: List<IntPtr>, targetScreen: MonitorScreen, snapDirection: string) =
        match tabsInOrder with
        | [] -> ()
        | [singleHwnd] -> this.detachTabToScreenSnap(singleHwnd, targetScreen, snapDirection)
        | _ ->
            this.detachTabsCommon(tabsInOrder, fun sourceScreen bounds place ->
                let sourceWorkArea = sourceScreen.WorkingArea
                let widthPercent = float(bounds.size.width) / float(sourceWorkArea.Width)
                let heightPercent = float(bounds.size.height) / float(sourceWorkArea.Height)
                let targetWorkArea = targetScreen.WorkingArea
                let newWidth = int(float(targetWorkArea.Width) * widthPercent)
                let newHeight = int(float(targetWorkArea.Height) * heightPercent)
                let (newX, newY, finalWidth, finalHeight) = this.calculateSnapBounds(
                    snapDirection, targetWorkArea, newWidth, newHeight)
                place (Rect(Pt(newX, newY), Sz(finalWidth, finalHeight))))

    member private this.detachTabsToScreenSnapWithPercent(tabsInOrder: List<IntPtr>, targetScreen: MonitorScreen, snapDirection: string, percent: int) =
        match tabsInOrder with
        | [] -> ()
        | [singleHwnd] -> this.detachTabToScreenSnapWithPercent(singleHwnd, targetScreen, snapDirection, percent)
        | _ ->
            this.detachTabsCommon(tabsInOrder, fun _src _bounds place ->
                let (newX, newY, newWidth, newHeight) = this.calculateSnapBoundsWithPercent(
                    snapDirection, percent, targetScreen.WorkingArea)
                place (Rect(Pt(newX, newY), Sz(newWidth, newHeight))))

    // Resolve the action-target list for the detach menu and order it
    // by visualOrder so that the resulting tab strip matches the source
    // ordering. Used when a multi-select is active; otherwise the menu
    // dispatches to the single-tab detach path.
    member private this.actionTargetsInVisualOrder(hwnd: IntPtr) : List<IntPtr> =
        let raw = this.actionTargets(hwnd) |> Set.ofList
        this.ts.visualOrder.list
        |> List.choose (fun (Tab(h)) -> if raw.Contains(h) then Some h else None)

    // ----- Detach menu dispatchers (single vs multi-tab) -----
    member private this.detachOrSplitToPosition(hwnd: IntPtr, position: Option<string>) =
        let targets = this.actionTargetsInVisualOrder(hwnd)
        position |> Option.iter (fun direction ->
            this.applySnapRealign(targets, direction, fun hs a -> hs |> List.iter (fun h -> Services.program.setWindowAlignment(h, Some a))))
        if List.length targets <= 1 then this.detachTabToPosition(hwnd, position)
        else this.detachTabsToPosition(targets, position)
    member private this.detachOrSplitToSnap(hwnd: IntPtr, snapDirection: string) =
        let targets = this.actionTargetsInVisualOrder(hwnd)
        // Persist the realigned position before detaching so the freshly formed
        // group inherits it (WindowGroup.addWindow reads the per-window alignment)
        this.applySnapRealign(targets, snapDirection, fun hs a -> hs |> List.iter (fun h -> Services.program.setWindowAlignment(h, Some a)))
        if List.length targets <= 1 then this.detachTabToSnap(hwnd, snapDirection)
        else this.detachTabsToSnap(targets, snapDirection)
    member private this.detachOrSplitToSnapWithPercent(hwnd: IntPtr, snapDirection: string, percent: int) =
        let targets = this.actionTargetsInVisualOrder(hwnd)
        this.applySnapRealign(targets, snapDirection, fun hs a -> hs |> List.iter (fun h -> Services.program.setWindowAlignment(h, Some a)))
        if List.length targets <= 1 then this.detachTabToSnapWithPercent(hwnd, snapDirection, percent)
        else this.detachTabsToSnapWithPercent(targets, snapDirection, percent)
    member private this.detachOrSplitToScreen(hwnd: IntPtr, targetScreen: MonitorScreen, position: Option<string>) =
        let targets = this.actionTargetsInVisualOrder(hwnd)
        position |> Option.iter (fun direction ->
            this.applySnapRealign(targets, direction, fun hs a -> hs |> List.iter (fun h -> Services.program.setWindowAlignment(h, Some a))))
        if List.length targets <= 1 then this.detachTabToScreen(hwnd, targetScreen, position)
        else this.detachTabsToScreen(targets, targetScreen, position)
    member private this.detachOrSplitToScreenSnap(hwnd: IntPtr, targetScreen: MonitorScreen, snapDirection: string) =
        let targets = this.actionTargetsInVisualOrder(hwnd)
        this.applySnapRealign(targets, snapDirection, fun hs a -> hs |> List.iter (fun h -> Services.program.setWindowAlignment(h, Some a)))
        if List.length targets <= 1 then this.detachTabToScreenSnap(hwnd, targetScreen, snapDirection)
        else this.detachTabsToScreenSnap(targets, targetScreen, snapDirection)
    member private this.detachOrSplitToScreenSnapWithPercent(hwnd: IntPtr, targetScreen: MonitorScreen, snapDirection: string, percent: int) =
        let targets = this.actionTargetsInVisualOrder(hwnd)
        this.applySnapRealign(targets, snapDirection, fun hs a -> hs |> List.iter (fun h -> Services.program.setWindowAlignment(h, Some a)))
        if List.length targets <= 1 then this.detachTabToScreenSnapWithPercent(hwnd, targetScreen, snapDirection, percent)
        else this.detachTabsToScreenSnapWithPercent(targets, targetScreen, snapDirection, percent)

    // Device pixels: the margin a snap leaves for the strip has to match the
    // strip's REAL height (38 px at 150%, not 25).
    //
    // The scale comes from `this.ts.scale`, i.e. the scale that is currently
    // committed to the strip, not from a fresh group.dpiScale query. Those two
    // disagree for exactly as long as a window is straddling a monitor
    // boundary, and a snap performed in that window would then reserve a
    // margin for a strip height the strip is not drawing - the one remaining
    // exception to "decide the scale once and hand it down" that the second
    // generation left behind.
    /// Multi-edge keep-size snaps use the restored frame; top-only keeps its legacy input.
    static member snapSourceBounds(topOnly: bool, before: Rect, restored: Rect) =
        if topOnly then before else restored

    member private this.getTabHeightForSnap() =
        try
            if TabBehaviorPolicy.reserveTopMargin group.snapTabHeightMargin (Services.settings.getValue("tabVerticalDirection") :?> string) then
                (group.tabAppearanceAt this.ts.scale).tabHeight - 1
            else 0
        with | _ -> 0

    // The room a snap leaves for the strips (7-1): on each of `edges` that
    // stands outside, as thick as its strip at the scale of the destination
    // (TabEdgeLayout.snapReserve); always-inside edges get no room. With the
    // top edge the only one this is getTabHeightForSnap's room above and
    // nothing else.
    member private this.snapReserveFor (edges: TabEdges.Edge list) (workArea: System.Drawing.Rectangle) =
        try
            if not group.snapTabHeightMargin then TabEdgeLayout.noReserve
            else
                let scale =
                    try Dpi.scaleForRect(Rect(Pt(workArea.X, workArea.Y), Sz(max 1 workArea.Width, max 1 workArea.Height)))
                    with _ -> this.ts.scale
                TabStripDecorator.snapReserveAt(edges, edgeValues, group.tabAppearanceRaw,
                                                Services.program.defaultTabAppearanceInfo, scale, true,
                                                Services.settings.getValue("tabVerticalDirection") :?> string)
        with _ -> TabEdgeLayout.noReserve

    // The edges whose room a snap leaves: the group's own for a snap of the
    // group; for tabs detached into a new group, the edge they start on
    // there (the first allowed one). The top edge while it is the only one.
    member private this.snapEdges (ofGroup: bool) =
        let allowed = this.ts.allowedEdges
        if TabEdges.isTopOnly allowed then [ TabEdges.Edge.Top ]
        elif ofGroup then this.ts.edgesInUse
        else [ TabEdges.firstAllowed allowed ]

    member private this.useTopOnlySnap(ofGroup: bool) =
        TabEdges.useTopOnlySnap this.ts.allowedEdges this.ts.edgesInUse ofGroup

    member private this.adjustWorkAreaForTabHeight(workArea: System.Drawing.Rectangle, ?ofGroup: bool) =
        let (x, y, width, height) =
            if this.useTopOnlySnap(defaultArg ofGroup false) then
                let tabHeight = this.getTabHeightForSnap()
                let result = SnapGeometry.reserveTabHeight tabHeight (workArea.X, workArea.Y, workArea.Width, workArea.Height)
#if DEBUG
                WindowFrameMargin.trace (sprintf "geometry-compensation name=snap-reserve-tab-height group=%X amount=%d before=%A after=%A"
                                            (int64 group.hwnd) tabHeight workArea result)
#endif
                result
            else
                let reserve = this.snapReserveFor (this.snapEdges (defaultArg ofGroup false)) workArea
                let result = TabEdgeLayout.reserveArea reserve (workArea.X, workArea.Y, workArea.Width, workArea.Height)
#if DEBUG
                WindowFrameMargin.trace (sprintf "geometry-compensation name=snap-reserve-edges group=%X amount=%A before=%A after=%A"
                                            (int64 group.hwnd) reserve workArea result)
#endif
                result
        System.Drawing.Rectangle(x, y, width, height)

    member private this.calculateSnapBounds(
        snapDirection: string,
        workArea: System.Drawing.Rectangle,
        currentWidth: int,
        currentHeight: int,
        ?ofGroup: bool) : (int * int * int * int) =
        // Keep-size menu geometry, also used for the drag staging position.
        // Variant B uses the percentage helper below for its final rectangle.
        let workArea = this.adjustWorkAreaForTabHeight(workArea, defaultArg ofGroup false)
        let result = SnapGeometry.snapBounds snapDirection (workArea.X, workArea.Y, workArea.Width, workArea.Height) currentWidth currentHeight
#if DEBUG
        group.traceSnapGeometry(sprintf "direction=%s mode=keep-size inputSize=(%d,%d) reservedArea=%A base=%A"
                                    snapDirection currentWidth currentHeight workArea result)
#endif
        result

    member private this.calculateSnapBoundsWithPercent(
        snapDirection: string,
        percent: int,
        workArea: System.Drawing.Rectangle,
        ?ofGroup: bool) : (int * int * int * int) =
        // Returns (x, y, width, height) for snap position with percentage
        // Snap right/left: width = workArea.Width * percent, height = full
        // Snap top/bottom: width = full, height = workArea.Height * percent
        let ofGroup = defaultArg ofGroup false
        let workArea =
            if snapDirection = "snapmaximizedesktop" then
                this.adjustWorkAreaForTabHeight(System.Windows.Forms.SystemInformation.VirtualScreen, ofGroup)
            else
                this.adjustWorkAreaForTabHeight(workArea, ofGroup)
        let result = SnapGeometry.calculateSnapBoundsWithPercent snapDirection percent
                         (workArea.X, workArea.Y, workArea.Width, workArea.Height)
#if DEBUG
        group.traceSnapGeometry(sprintf "direction=%s percent=%d reservedArea=%A base=%A" snapDirection percent workArea result)
#endif
        result

    member private this.detachTabToSnap(hwnd: IntPtr, snapDirection: string) =
        // This method is only called when group has multiple tabs (menu is disabled for single tab)
        if group.windows.items.count <= 1 then
            raise (System.InvalidOperationException("detachTabToSnap should not be called when there is only one tab"))

        let window = os.windowFromHwnd(hwnd)
        let margin = group.capturePlacementMargin(hwnd)
        let bounds = FramePlacement.read hwnd margin (FramePlacement.restoredBounds window)
        let screen = this.getCurrentScreenForWindow(hwnd)

        let tab = Tab(hwnd)
        Services.program.suspendTabMonitoring()

        try
            this.ts.removeTab(tab)
            group.removeWindow(hwnd)

            window.hideOffScreen(None)

            if window.isMinimized || window.isMaximized then
                window.showWindow(ShowWindowCommands.SW_RESTORE)

            let (newX, newY, newWidth, newHeight) = this.calculateSnapBounds(
                snapDirection,
                screen.WorkingArea,
                bounds.size.width,
                bounds.size.height)

            FramePlacement.moveDetached window margin (Rect(Pt(newX, newY), Sz(newWidth, newHeight)))

            notifyDetached(hwnd)

        finally
            Services.program.resumeTabMonitoringAfter(200)

    member private this.detachTabToScreenSnap(hwnd: IntPtr, targetScreen: MonitorScreen, snapDirection: string) =
        // This method is only called when group has multiple tabs (menu is disabled for single tab)
        if group.windows.items.count <= 1 then
            raise (System.InvalidOperationException("detachTabToScreenSnap should not be called when there is only one tab"))

        let window = os.windowFromHwnd(hwnd)
        let margin = group.capturePlacementMargin(hwnd)
        let bounds = FramePlacement.read hwnd margin (FramePlacement.restoredBounds window)
        let sourceScreen = this.getCurrentScreenForWindow(hwnd)
        let sourceWorkArea = sourceScreen.WorkingArea
        let widthPercent = float(bounds.size.width) / float(sourceWorkArea.Width)
        let heightPercent = float(bounds.size.height) / float(sourceWorkArea.Height)

        let tab = Tab(hwnd)
        Services.program.suspendTabMonitoring()

        try
            // Remove tab from current group
            this.ts.removeTab(tab)
            group.removeWindow(hwnd)

            window.hideOffScreen(None)

            if window.isMinimized || window.isMaximized then
                window.showWindow(ShowWindowCommands.SW_RESTORE)

            // Calculate new size based on target screen (using percentage for DPI-awareness)
            let targetWorkArea = targetScreen.WorkingArea
            let newWidth = int(float(targetWorkArea.Width) * widthPercent)
            let newHeight = int(float(targetWorkArea.Height) * heightPercent)

            let (newX, newY, finalWidth, finalHeight) = this.calculateSnapBounds(
                snapDirection,
                targetWorkArea,
                newWidth,
                newHeight)

            // DPI-aware window placement: move position first, wait for DPI change, then set size
            let initialDpi = WinUserApi.GetDpiForWindow(hwnd)
            window.setPositionOnly newX newY

            // Wait for DPI change (max 200ms)
            let mutable currentDpi = initialDpi
            let mutable elapsed = 0
            while elapsed < 200 && currentDpi = initialDpi do
                System.Threading.Thread.Sleep(10)
                elapsed <- elapsed + 10
                currentDpi <- WinUserApi.GetDpiForWindow(hwnd)

            if currentDpi <> initialDpi then
                System.Threading.Thread.Sleep(20)

            FramePlacement.moveDetached window margin (Rect(Pt(newX, newY), Sz(finalWidth, finalHeight)))

            notifyDetached(hwnd)

        finally
            Services.program.resumeTabMonitoringAfter(200)

    member private this.detachTabToSnapWithPercent(hwnd: IntPtr, snapDirection: string, percent: int) =
        // Snap with percentage: width/height based on display percentage
        if group.windows.items.count <= 1 then
            raise (System.InvalidOperationException("detachTabToSnapWithPercent should not be called when there is only one tab"))

        let window = os.windowFromHwnd(hwnd)
        let margin = group.capturePlacementMargin(hwnd)
        let screen = this.getCurrentScreenForWindow(hwnd)

        let tab = Tab(hwnd)
        Services.program.suspendTabMonitoring()

        try
            this.ts.removeTab(tab)
            group.removeWindow(hwnd)

            window.hideOffScreen(None)

            if window.isMinimized || window.isMaximized then
                window.showWindow(ShowWindowCommands.SW_RESTORE)

            let (newX, newY, newWidth, newHeight) = this.calculateSnapBoundsWithPercent(
                snapDirection,
                percent,
                screen.WorkingArea)

            FramePlacement.moveDetached window margin (Rect(Pt(newX, newY), Sz(newWidth, newHeight)))

            notifyDetached(hwnd)

        finally
            Services.program.resumeTabMonitoringAfter(200)

    member private this.detachTabToScreenSnapWithPercent(hwnd: IntPtr, targetScreen: MonitorScreen, snapDirection: string, percent: int) =
        // Snap with percentage to another screen
        if group.windows.items.count <= 1 then
            raise (System.InvalidOperationException("detachTabToScreenSnapWithPercent should not be called when there is only one tab"))

        let window = os.windowFromHwnd(hwnd)
        let margin = group.capturePlacementMargin(hwnd)

        let tab = Tab(hwnd)
        Services.program.suspendTabMonitoring()

        try
            this.ts.removeTab(tab)
            group.removeWindow(hwnd)

            window.hideOffScreen(None)

            if window.isMinimized || window.isMaximized then
                window.showWindow(ShowWindowCommands.SW_RESTORE)

            let targetWorkArea = targetScreen.WorkingArea
            let (newX, newY, newWidth, newHeight) = this.calculateSnapBoundsWithPercent(
                snapDirection,
                percent,
                targetWorkArea)

            let initialDpi = WinUserApi.GetDpiForWindow(hwnd)
            window.setPositionOnly newX newY

            let mutable currentDpi = initialDpi
            let mutable elapsed = 0
            while elapsed < 200 && currentDpi = initialDpi do
                System.Threading.Thread.Sleep(10)
                elapsed <- elapsed + 10
                currentDpi <- WinUserApi.GetDpiForWindow(hwnd)

            if currentDpi <> initialDpi then
                System.Threading.Thread.Sleep(20)

            FramePlacement.moveDetached window margin (Rect(Pt(newX, newY), Sz(newWidth, newHeight)))

            notifyDetached(hwnd)

        finally
            Services.program.resumeTabMonitoringAfter(200)

    member this.moveTabGroupToPosition(hwnd: IntPtr, position: Option<string>) =
        // Move the entire tab group to the specified position
        // Always use the active window (topWindow) as the reference point
        let activeHwnd = group.topWindow
        let window = os.windowFromHwnd(activeHwnd)
        let margin = group.capturePlacementMargin(activeHwnd)
        let bounds = FramePlacement.read activeHwnd margin (FramePlacement.restoredBounds window)
        let screen = this.getCurrentScreenForWindow(activeHwnd)

        // Restore window if minimized or maximized
        if window.isMinimized || window.isMaximized then
            window.showWindow(ShowWindowCommands.SW_RESTORE)

        // Calculate new position
        let (newX, newY) = this.calculatePositionInWorkArea(
            position,
            screen.WorkingArea,
            bounds.location.x,
            bounds.location.y,
            bounds.size.width,
            bounds.size.height, ofGroup = true)

        // Move the active window
        if newX <> bounds.location.x || newY <> bounds.location.y then
            let baseBounds = Rect(Pt(newX,newY),bounds.size)
            window.move(FramePlacement.target activeHwnd margin baseBounds)

        position |> Option.iter this.applyGroupSnapRealign

    member this.moveTabGroupToScreen(hwnd: IntPtr, targetScreen: MonitorScreen, position: Option<string>) =
        // Move the entire tab group to the specified screen and position
        // Always use the active window (topWindow) as the reference point
        let activeHwnd = group.topWindow
        let window = os.windowFromHwnd(activeHwnd)
        let bounds = window.bounds
        let sourceScreen = this.getCurrentScreenForWindow(activeHwnd)
        let sourceWorkArea = sourceScreen.WorkingArea

        // Use group bounds (expanded by margin) for percentage calculation
        let (mTop, mLeft, mRight, mBottom) = group.getWindowMargin(activeHwnd, bounds)
        let groupWidth = bounds.size.width + mLeft + mRight
        let groupHeight = bounds.size.height + mTop + mBottom
        let groupX = bounds.location.x - mLeft
        let groupY = bounds.location.y - mTop
#if DEBUG
        WindowFrameMargin.trace (sprintf "geometry-compensation name=snap-read-frame-margin hwnd=%X before=%A amountTLRB=%A base=%A"
                                    (int64 activeHwnd) bounds (mTop,mLeft,mRight,mBottom) (groupX,groupY,groupWidth,groupHeight))
#endif

        // Calculate size percentages for DPI-aware placement
        let widthPercent = float(groupWidth) / float(sourceWorkArea.Width)
        let heightPercent = float(groupHeight) / float(sourceWorkArea.Height)
        let topPercent = float(groupY - sourceWorkArea.Top) / float(sourceWorkArea.Height)

        // Restore window if minimized or maximized
        if window.isMinimized || window.isMaximized then
            window.showWindow(ShowWindowCommands.SW_RESTORE)

        // Calculate new size based on target screen
        let targetWorkArea = targetScreen.WorkingArea
        let newWidth = int(float(targetWorkArea.Width) * widthPercent)
        let newHeight = int(float(targetWorkArea.Height) * heightPercent)

        // Calculate current position using percentages (use group position, not margined position)
        let leftPercent = float(groupX - sourceWorkArea.Left) / float(sourceWorkArea.Width)
        let currentX = targetWorkArea.Left + int(float(targetWorkArea.Width) * leftPercent)
        let currentY = targetWorkArea.Top + int(float(targetWorkArea.Height) * topPercent)

        // Calculate new position based on position option
        let newLeft, newTop = this.calculatePositionInWorkArea(
            position,
            targetWorkArea,
            currentX,
            currentY,
            newWidth,
            newHeight, ofGroup = true)

        // Ensure position is within bounds
        let finalX, finalY = this.clampPositionInWorkArea(
            position, targetWorkArea, newLeft, newTop, newWidth, newHeight, ofGroup = true)

        // DPI-aware window placement: move position first, wait for DPI change, then set size
        let initialDpi = WinUserApi.GetDpiForWindow(hwnd)
        window.setPositionOnly finalX finalY

        // Wait for DPI change (max 200ms)
        let mutable currentDpi = initialDpi
        let mutable elapsed = 0
        while elapsed < 200 && currentDpi = initialDpi do
            System.Threading.Thread.Sleep(10)
            elapsed <- elapsed + 10
            currentDpi <- WinUserApi.GetDpiForWindow(hwnd)

        if currentDpi <> initialDpi then
            System.Threading.Thread.Sleep(20)

        let moveBounds = Rect(Pt(finalX, finalY), Sz(newWidth, newHeight))
        let finalBounds = group.applyWindowMarginForWrite(activeHwnd, moveBounds)
#if DEBUG
        WindowFrameMargin.trace (sprintf "geometry-compensation name=snap-write-frame-margin hwnd=%X base=%A target=%A"
                                    (int64 activeHwnd) moveBounds finalBounds)
#endif
        window.move(finalBounds)
        if group.hasWindowMargin(activeHwnd) then
            group.recordMarginApplied(activeHwnd, finalBounds.width, finalBounds.height)

        position |> Option.iter this.applyGroupSnapRealign

    // Whether the "Tab alignment on snap and move" setting is enabled.
    member private this.snapRealignEnabled() =
        try (Services.settings.getValue("changeTabPositionOnSnap") :?> string) = "change"
        with _ -> false

    // Realign each uniformly aligned edge when the setting is enabled.
    // Horizontal strips follow the horizontal snap component, vertical strips
    // the vertical component; corner snaps affect both axes. setAlignAll updates
    // visible tabs for a group snap or persists alignment before detaching.
    // Read the current group before detaching for the uniformity check.
    // TabEdges.snapRealignTabs uses the edge supplied by edgeOf, sharing the
    // alignment rule with Desktop.dragDrop and the top-only path.
    member private this.applySnapRealignOn(tabHwnds: IntPtr list, edgeOf: IntPtr -> TabEdges.Edge, snapDirection: string, setAlignAll: IntPtr list -> TabAlign -> unit) =
        if this.snapRealignEnabled() && not tabHwnds.IsEmpty then
            let toAlign a = if a = TopLeft then TabEdges.AlignStart else TabEdges.AlignEnd
            let tabs = tabHwnds |> List.map (fun h -> h, edgeOf h, toAlign (group.getTabAlign h))
            for (hwnds, desired) in TabEdges.snapRealignTabs true snapDirection tabs do
                setAlignAll hwnds (if desired = TabEdges.AlignStart then TopLeft else TopRight)

    // Tabs detached into a group of their own: they start on the new
    // group's first edge (the top edge unless it is not allowed), and that
    // edge decides, as Desktop.dragDrop has it.
    member private this.applySnapRealign(tabHwnds: IntPtr list, snapDirection: string, setAlignAll: IntPtr list -> TabAlign -> unit) =
        let first = TabEdges.firstAllowed this.ts.allowedEdges
        this.applySnapRealignOn(tabHwnds, (fun _ -> first), snapDirection, fun hwnds alignment ->
            setAlignAll hwnds alignment
            // Persist the edge that owns the newly computed alignment before arrival.
            hwnds |> List.iter (fun h -> Services.program.setWindowAlignmentEdge(h, first)))

    // Realign the whole current group after an in-place snap, each edge's
    // tabs by themselves; a left strip's tabs answer a top or bottom snap
    // and are left alone by a left or right one.
    // Bulk-align so the tabs keep their relative order.
    member private this.applyGroupSnapRealign(snapDirection: string) =
        this.applySnapRealignOn(group.visualOrder.list, (fun h -> this.ts.edgeOf(Tab h)), snapDirection, fun hs a -> group.setTabsAlign(hs, a))

    member private this.sizeWorkArea(sourceScreen: MonitorScreen, targetScreen: MonitorScreen) =
        let area = targetScreen.WorkingArea
        let sameScreen = targetScreen.Equals(sourceScreen)
        let reserved =
            try
                let targetScale =
                    if sameScreen then this.ts.scale
                    else Dpi.scaleForMonitorHandle targetScreen.Mon.hMonitor
                SnapGeometry.sizeWorkArea sameScreen this.ts.scale targetScale
                    (fun scale -> (group.tabAppearanceAt scale).tabHeight)
                    group.snapTabHeightMargin (area.X, area.Y, area.Width, area.Height)
            with | _ -> (area.X, area.Y, area.Width, area.Height)
        let x, y, width, height = reserved
        System.Drawing.Rectangle(x, y, width, height)

    member private this.resizeTabGroup(targetScreen: MonitorScreen, width: int, height: int) =
        let activeHwnd = group.topWindow
        let window = os.windowFromHwnd(activeHwnd)
        let margin = group.capturePlacementMargin(activeHwnd)
        let bounds = FramePlacement.read activeHwnd margin (FramePlacement.restoredBounds window)
        let sourceScreen = this.getCurrentScreenForWindow(activeHwnd)
        let sameScreen = targetScreen.Equals(sourceScreen)
        let sourceArea = sourceScreen.WorkingArea
        let targetArea = targetScreen.WorkingArea
        let position = SnapGeometry.sizePosition sameScreen
                           (sourceArea.X, sourceArea.Y, sourceArea.Width, sourceArea.Height)
                           (targetArea.X, targetArea.Y, targetArea.Width, targetArea.Height)
                           (bounds.x, bounds.y)
        let area = this.sizeWorkArea(sourceScreen, targetScreen)
        match SnapGeometry.sizeBounds (area.X, area.Y, area.Width, area.Height)
                  position (width, height) with
        | None -> ()
        | Some(x, y, w, h) ->
            if window.isMinimized || window.isMaximized then
                window.showWindow(ShowWindowCommands.SW_RESTORE)
            if not sameScreen then
                // Follow screen moves: settle the DPI change before writing the pixel size.
                let initialDpi = WinUserApi.GetDpiForWindow(activeHwnd)
                window.setPositionOnly x y
                let mutable elapsed = 0
                while elapsed < 200 && WinUserApi.GetDpiForWindow(activeHwnd) = initialDpi do
                    System.Threading.Thread.Sleep(10)
                    elapsed <- elapsed + 10
                if WinUserApi.GetDpiForWindow(activeHwnd) <> initialDpi then
                    System.Threading.Thread.Sleep(20)
            let finalBounds = FramePlacement.target activeHwnd margin (Rect(Pt(x, y), Sz(w, h)))
            // Moving the front member uses the same group propagation as snapping.
            window.move(finalBounds)
            if group.hasWindowMargin(activeHwnd) then
                group.recordMarginApplied(activeHwnd, finalBounds.width, finalBounds.height)

    member this.moveTabGroupToSnap(hwnd: IntPtr, snapDirection: string) =
        // Move the entire tab group to snap position (resize and position)
        let activeHwnd = group.topWindow
        let window = os.windowFromHwnd(activeHwnd)
        let bounds = window.bounds
        let screen = this.getCurrentScreenForWindow(activeHwnd)

        // Read shadow state together with the native rectangle, before restore.
        let (mTop, mLeft, mRight, mBottom) = group.getWindowMargin(activeHwnd, bounds)

        // Restore window if minimized or maximized
        if window.isMinimized || window.isMaximized then
            window.showWindow(ShowWindowCommands.SW_RESTORE)

        let bounds = TabStripDecorator.snapSourceBounds(this.useTopOnlySnap(true), bounds, window.bounds)

        // Use group bounds (expanded by margin) for snap calculation so margin is not applied twice
#if DEBUG
        WindowFrameMargin.trace (sprintf "geometry-compensation name=snap-read-frame-margin group=%X hwnd=%X before=%A amountTLRB=%A expandedSize=%A"
                                    (int64 group.hwnd) (int64 activeHwnd) bounds (mTop,mLeft,mRight,mBottom)
                                    (bounds.width+mLeft+mRight, bounds.height+mTop+mBottom))
#endif
        let snapWidth = bounds.size.width + mLeft + mRight
        let snapHeight = bounds.size.height + mTop + mBottom

        let (newX, newY, newWidth, newHeight) = this.calculateSnapBounds(
            snapDirection,
            screen.WorkingArea,
            snapWidth,
            snapHeight,
            true)

        let snapBounds = Rect(Pt(newX, newY), Sz(newWidth, newHeight))
        // Apply detected frame margin
        let finalBounds = group.applyWindowMarginForWrite(activeHwnd, snapBounds)
#if DEBUG
        WindowFrameMargin.trace (sprintf "geometry-compensation name=snap-write-frame-margin group=%X hwnd=%X margin=%A base=%A target=%A"
                                    (int64 group.hwnd) (int64 activeHwnd) (group.getWindowMargin(activeHwnd, snapBounds)) snapBounds finalBounds)
        group.traceMarginGeometry("snap-before-write", None)
#endif
        window.move(finalBounds)
#if DEBUG
        group.traceMarginGeometry("snap-after-write", None)
#endif
        // Record shrunk size for tracking
        if group.hasWindowMargin(activeHwnd) then
            group.recordMarginApplied(activeHwnd, finalBounds.width, finalBounds.height)
        this.applyGroupSnapRealign(snapDirection)

    member this.moveTabGroupToScreenSnap(hwnd: IntPtr, targetScreen: MonitorScreen, snapDirection: string) =
        // Move the entire tab group to snap position on target screen (resize and position)
        let activeHwnd = group.topWindow
        let window = os.windowFromHwnd(activeHwnd)
        let bounds = window.bounds
        let sourceScreen = this.getCurrentScreenForWindow(activeHwnd)
        let sourceWorkArea = sourceScreen.WorkingArea

        if not (this.useTopOnlySnap(true)) && (window.isMinimized || window.isMaximized) then
            window.showWindow(ShowWindowCommands.SW_RESTORE)
        let bounds = TabStripDecorator.snapSourceBounds(this.useTopOnlySnap(true), bounds, window.bounds)

        // Use group bounds (expanded by margin) for percentage calculation
        let (mTop, mLeft, mRight, mBottom) = group.getWindowMargin(activeHwnd, bounds)
#if DEBUG
        WindowFrameMargin.trace (sprintf "geometry-compensation name=snap-read-frame-margin group=%X hwnd=%X before=%A amountTLRB=%A expandedSize=%A"
                                    (int64 group.hwnd) (int64 activeHwnd) bounds (mTop,mLeft,mRight,mBottom)
                                    (bounds.width+mLeft+mRight, bounds.height+mTop+mBottom))
#endif
        let groupWidth = bounds.size.width + mLeft + mRight
        let groupHeight = bounds.size.height + mTop + mBottom

        // Calculate size percentages for DPI-aware placement
        let widthPercent = float(groupWidth) / float(sourceWorkArea.Width)
        let heightPercent = float(groupHeight) / float(sourceWorkArea.Height)

        // Restore window if minimized or maximized
        if window.isMinimized || window.isMaximized then
            window.showWindow(ShowWindowCommands.SW_RESTORE)

        // Calculate new size based on target screen (using percentage for DPI-awareness)
        let targetWorkArea = targetScreen.WorkingArea
        let newWidth = int(float(targetWorkArea.Width) * widthPercent)
        let newHeight = int(float(targetWorkArea.Height) * heightPercent)
#if DEBUG
        WindowFrameMargin.trace (sprintf "geometry-compensation name=snap-scale-work-area group=%X sourceArea=%A targetArea=%A sizeRatio=%A outputSize=%A"
                                    (int64 group.hwnd) sourceWorkArea targetWorkArea (widthPercent,heightPercent) (newWidth,newHeight))
#endif

        let (newX, newY, finalWidth, finalHeight) = this.calculateSnapBounds(
            snapDirection,
            targetWorkArea,
            newWidth,
            newHeight,
            true)

        // DPI-aware window placement: move position first, wait for DPI change, then set size
        let initialDpi = WinUserApi.GetDpiForWindow(hwnd)
        window.setPositionOnly newX newY

        // Wait for DPI change (max 200ms)
        let mutable currentDpi = initialDpi
        let mutable elapsed = 0
        while elapsed < 200 && currentDpi = initialDpi do
            System.Threading.Thread.Sleep(10)
            elapsed <- elapsed + 10
            currentDpi <- WinUserApi.GetDpiForWindow(hwnd)

        if currentDpi <> initialDpi then
            System.Threading.Thread.Sleep(20)

        let snapBounds = Rect(Pt(newX, newY), Sz(finalWidth, finalHeight))
        // Apply detected frame margin
        let finalBounds2 = group.applyWindowMarginForWrite(activeHwnd, snapBounds)
#if DEBUG
        WindowFrameMargin.trace (sprintf "geometry-compensation name=snap-write-frame-margin group=%X hwnd=%X margin=%A base=%A target=%A"
                                    (int64 group.hwnd) (int64 activeHwnd) (group.getWindowMargin(activeHwnd, snapBounds)) snapBounds finalBounds2)
        group.traceMarginGeometry("snap-before-write", None)
#endif
        window.move(finalBounds2)
#if DEBUG
        group.traceMarginGeometry("snap-after-write", None)
#endif
        if group.hasWindowMargin(activeHwnd) then
            group.recordMarginApplied(activeHwnd, finalBounds2.width, finalBounds2.height)
        this.applyGroupSnapRealign(snapDirection)

    member this.moveTabGroupToSnapWithPercent(hwnd: IntPtr, snapDirection: string, percent: int) =
        // Move the entire tab group to snap position with percentage
        let activeHwnd = group.topWindow
        let window = os.windowFromHwnd(activeHwnd)
        let screen = this.getCurrentScreenForWindow(activeHwnd)

        if window.isMinimized || window.isMaximized then
            window.showWindow(ShowWindowCommands.SW_RESTORE)

        let (newX, newY, newWidth, newHeight) = this.calculateSnapBoundsWithPercent(
            snapDirection,
            percent,
            screen.WorkingArea,
            true)

        let snapBounds = Rect(Pt(newX, newY), Sz(newWidth, newHeight))
        let finalBounds = group.applyWindowMarginForWrite(activeHwnd, snapBounds)
#if DEBUG
        WindowFrameMargin.trace (sprintf "geometry-compensation name=snap-write-frame-margin group=%X hwnd=%X margin=%A base=%A target=%A"
                                    (int64 group.hwnd) (int64 activeHwnd) (group.getWindowMargin(activeHwnd, snapBounds)) snapBounds finalBounds)
        group.traceMarginGeometry("snap-before-write", None)
#endif
        window.move(finalBounds)
#if DEBUG
        group.traceMarginGeometry("snap-after-write", None)
#endif
        if group.hasWindowMargin(activeHwnd) then
            group.recordMarginApplied(activeHwnd, finalBounds.width, finalBounds.height)
        this.applyGroupSnapRealign(snapDirection)

    member this.moveTabGroupToScreenSnapWithPercent(hwnd: IntPtr, targetScreen: MonitorScreen, snapDirection: string, percent: int) =
        // Move the entire tab group to snap position on target screen with percentage
        let activeHwnd = group.topWindow
        let window = os.windowFromHwnd(activeHwnd)

        if window.isMinimized || window.isMaximized then
            window.showWindow(ShowWindowCommands.SW_RESTORE)

        let targetWorkArea = targetScreen.WorkingArea
        let (newX, newY, newWidth, newHeight) = this.calculateSnapBoundsWithPercent(
            snapDirection,
            percent,
            targetWorkArea,
            true)

        let initialDpi = WinUserApi.GetDpiForWindow(hwnd)
        window.setPositionOnly newX newY

        let mutable currentDpi = initialDpi
        let mutable elapsed = 0
        while elapsed < 200 && currentDpi = initialDpi do
            System.Threading.Thread.Sleep(10)
            elapsed <- elapsed + 10
            currentDpi <- WinUserApi.GetDpiForWindow(hwnd)

        if currentDpi <> initialDpi then
            System.Threading.Thread.Sleep(20)

        let snapBounds = Rect(Pt(newX, newY), Sz(newWidth, newHeight))
        let finalBounds2 = group.applyWindowMarginForWrite(activeHwnd, snapBounds)
#if DEBUG
        WindowFrameMargin.trace (sprintf "geometry-compensation name=snap-write-frame-margin group=%X hwnd=%X margin=%A base=%A target=%A"
                                    (int64 group.hwnd) (int64 activeHwnd) (group.getWindowMargin(activeHwnd, snapBounds)) snapBounds finalBounds2)
        group.traceMarginGeometry("snap-before-write", None)
#endif
        window.move(finalBounds2)
#if DEBUG
        group.traceMarginGeometry("snap-after-write", None)
#endif
        if group.hasWindowMargin(activeHwnd) then
            group.recordMarginApplied(activeHwnd, finalBounds2.width, finalBounds2.height)
        this.applyGroupSnapRealign(snapDirection)

    // `menuScale` is the DPI scale of the monitor the menu is anchored on,
    // decided once by the caller and used for every picture in the menu:
    // the per-group application icons and the hand-painted colour swatches.
    // Win32Menu.show is given the same number for the bitmap size it resizes
    // to, so nothing in the menu is drawn at one scale and shown at another.
    member private  this.contextMenu(hwnd, menuScale: float) =
        let checked(isChecked) = if isChecked then List2([MenuFlags.MF_CHECKED]) else List2()
        let grayed(isGrayed) = if isGrayed then List2([MenuFlags.MF_GRAYED]) else List2()

        // A group's application icon for this menu. At 100% the item bitmap is
        // 16 px, so hand over the window's own 16 px artwork and the menu looks
        // exactly as it did before any of this DPI work - no 32 px picture
        // reduced to 16, no change of drawing. Above 100% the bitmap is bigger
        // than 16 px, so the 32 px capture is the right source to shrink from.
        let groupMenuIcon (info: TabGroupInfo) =
            if menuScale = 1.0 then info.firstTabIconSmall else info.firstTabIcon

        // A cloak notification may invalidate readings without changing desktops.
        // Share one registry read and one usability decision across all three menus.
        let state = VirtualDesktopGroups.Live.snapshot()
        let fresh = VirtualDesktopGroups.Live.isFresh state
        let explorerDesktop =
            let mutable id = Guid.Empty
            if VirtualDesktopHelper.TryReadCurrentDesktopId(&id) then Some id else None
        let usable = VirtualDesktopGroups.Live.usable fresh explorerDesktop state
#if DEBUG
        let mutable menuReadingLogged = false
#endif

        // Read native geometry once per menu, without invoking another group thread.
        // Use the same member regardless of activation or visual tab order, so frame
        // margins cannot make otherwise stationary groups exchange places.
        let linkScreens = this.getAllScreensSorted()
        let layoutBounds (rect: System.Drawing.Rectangle) : LinkGroupLayout.Bounds =
            { x = rect.X; y = rect.Y; width = rect.Width; height = rect.Height }
        let linkDisplays = linkScreens |> Array.map (fun screen -> layoutBounds screen.Bounds) |> Array.toList
        let linkBounds =
            lock groupInfos (fun () -> groupInfos.Values |> List.ofSeq)
            |> List.map (fun info ->
                let bounds =
                    info.tabHwnds |> List.sort |> List.tryFind WinUserApi.IsWindow
                    |> Option.map (fun memberHwnd ->
                        let memberWindow = os.windowFromHwnd(memberHwnd)
                        let rect =
                            if not memberWindow.isMinimized then memberWindow.bounds else
                            let normal = memberWindow.placement.rcNormalPosition
                            if int(memberWindow.styleEx) &&& WindowsExtendedStyles.WS_EX_TOOLWINDOW <> 0 then normal
                            else
                                match Mon.fromHwnd(memberHwnd) with
                                | Some mon -> normal.move(mon.workRect.x - mon.displayRect.x,
                                                          mon.workRect.y - mon.displayRect.y)
                                | None -> normal
                        ({ x = rect.x; y = rect.y
                           width = rect.size.width; height = rect.size.height } : LinkGroupLayout.Bounds))
                    |> Option.defaultValue { x = 0; y = 0; width = 0; height = 0 }
                info.hwnd, bounds)
            |> Map.ofList

        // All link menus use one immutable reading and the same presentation.
        let linkGroupItems exclude click =
            this.updateGroupInfo()
#if DEBUG
            let mutable listedGroups = Set.empty<IntPtr>
#endif
            let extended = VirtualDesktopGroups.keepAway && usable &&
                           (state.listed |> Option.exists (fun ds -> ds.Length > 1 && state.current |> Option.exists (fun c -> List.contains c ds)))
            let infos = lock groupInfos (fun () -> groupInfos.Values |> List.ofSeq)
                        |> List.filter (fun info -> info.hwnd <> group.hwnd && info.tabCount > 0 && not (exclude info))
                        |> List.distinctBy (fun info -> info.hwnd)
                        |> List.filter (fun info -> Map.containsKey info.hwnd linkBounds)
                        |> List.sortBy (fun info -> LinkGroupLayout.orderKey linkBounds.[info.hwnd] (info.hwnd.ToInt64()))
            let entry (info: TabGroupInfo) preferred =
                try
                    let target = lock decorators (fun () ->
                        match decorators.TryGetValue(info.hwnd) with
                        | true, d when WinUserApi.IsWindow(d.group.hwnd) && WinUserApi.IsWindow(d.ts.hwnd) -> Some d
                        | _ -> None)
                    target |> Option.map (fun decorator ->
                        let members, count = LinkGroupTabs.ordered preferred (List.zip info.tabHwnds info.tabNames)
                        let limit = if count = 1 then 22 elif count = 2 then 9 else 5
                        let names = members |> List.truncate 3 |> List.map (fun (_, name: string) ->
                            if name.Length > limit then name.Substring(0, limit) + "..." else name)
                        let names = String.Join(" ", names) + (if count > 3 then "..." else "")
                        let word = Localization.getString(if count = 1 then "TabSingular" else "TabPlural")
                        let text = String.Format(Localization.getString("MoveTabGroupFormat"), count, word, names)
                        let text =
                            match LinkGroupLayout.displayIndex linkBounds.[info.hwnd] linkDisplays with
                            | LinkGroupLayout.OnDisplay index -> String.Format(Localization.getString("LinkGroupDisplayFormat"),
                                                                                this.getScreenName(linkScreens.[index]), text)
                            | LinkGroupLayout.OnNoDisplay -> String.Format(Localization.getString("LinkGroupDisplayFormat"), "-", text)
                            | LinkGroupLayout.NoPrefix -> text
                        let big, small = info.tabIcons |> Map.tryFind (fst members.Head) |> Option.defaultValue (None, None)
#if DEBUG
                        listedGroups <- listedGroups.Add info.hwnd
#endif
                        CmiRegular({ text = text; image = groupMenuIcon { info with firstTabIcon = big; firstTabIconSmall = small }
                                     click = (fun () -> click decorator); flags = List2() }))
                with _ -> None
            let views = infos |> List.map (fun info ->
                let tabs = List.zip info.tabHwnds info.tabNames
                let here, others = VirtualDesktopGroups.menuTabs state.current state.listed state.reads tabs
                info, here, others)
            let hereItems =
                if extended then
                    views |> List.choose (fun (info, here, _) ->
                        if here.IsEmpty then None else entry info here)
                else
                    infos |> List.choose (fun info ->
                        let here = this.hereInfo(state, usable) info
                        if here.tabCount = 0 || not (this.isOnCurrentDesktop(state, usable) here) then None
                        else entry info (List.zip here.tabHwnds here.tabNames))
            let parents =
                if not extended then [] else
                state.listed.Value |> List.mapi (fun i desktop ->
                    let children = views |> List.choose (fun (info, here, others) ->
                        if not here.IsEmpty then None else
                        others |> List.tryFind (fun (_, d, _) -> d = desktop)
                        |> Option.bind (fun (_, _, tabs) -> entry info tabs))
                    let text = String.Format(Localization.getString("OtherDesktopGroupsMenu"), i + 1)
                    if Some desktop = state.current then None
                    elif children.IsEmpty then
                        // Keep empty desktops visible without offering a submenu.
                        Some(CmiRegular({ text = text; image = None; click = (fun () -> ())
                                          flags = List2([MenuFlags.MF_GRAYED]) }))
                    else
                        Some(CmiPopUp({ text = text; image = None
                                        items = List2(children); flags = List2() }))) |> List.choose id
#if DEBUG
            // The first link menu lists all destinations; log it once per context
            // menu, not once for each of the three presentations of that list.
            if not fresh && not menuReadingLogged then
                menuReadingLogged <- true
                VirtualDesktopTrace.log (fun () ->
                    let prefix (desktop: Guid option) =
                        desktop |> Option.map (fun id -> id.ToString("N").Substring(0, 8)) |> Option.defaultValue "none"
                    let age = if state.readAt = DateTime.MinValue then -1.0 else (DateTime.UtcNow - state.readAt).TotalMilliseconds
                    sprintf "link-menu case=%s ageMs=%.0f currentKnown=%b listedKnown=%b current=%s explorer=%s groups=%d"
                        (if usable then "stale-but-same-desktop" else "fallback") age
                        state.current.IsSome state.listed.IsSome (prefix state.current) (prefix explorerDesktop) listedGroups.Count)
#endif
            hereItems @ (if parents.IsEmpty then [] else CmiSeparator :: parents)

        // Build the "Snap Percent" submenu (e.g. "Snap 90%") containing Left/Right/Top/Bottom,
        // corner variants, and Center options at the given percent.
        let buildSnapPercentSubMenu (snapPercentFn: string -> int -> unit) (pct: int) =
            CmiPopUp({
                text = String.Format(Localization.getString("SnapPercent"), pct)
                image = None
                items = List2([
                    CmiRegular({ text = Localization.getString("SnapLeftPercent"); image = None; click = (fun() -> snapPercentFn "snapleft" pct); flags = List2() })
                    CmiRegular({ text = Localization.getString("SnapRightPercent"); image = None; click = (fun() -> snapPercentFn "snapright" pct); flags = List2() })
                    CmiRegular({ text = Localization.getString("SnapTopPercent"); image = None; click = (fun() -> snapPercentFn "snaptop" pct); flags = List2() })
                    CmiRegular({ text = Localization.getString("SnapBottomPercent"); image = None; click = (fun() -> snapPercentFn "snapbottom" pct); flags = List2() })
                    CmiSeparator
                    CmiRegular({ text = Localization.getString("SnapTopLeftPercent"); image = None; click = (fun() -> snapPercentFn "snaptopleft" pct); flags = List2() })
                    CmiRegular({ text = Localization.getString("SnapTopRightPercent"); image = None; click = (fun() -> snapPercentFn "snaptopright" pct); flags = List2() })
                    CmiRegular({ text = Localization.getString("SnapBottomLeftPercent"); image = None; click = (fun() -> snapPercentFn "snapbottomleft" pct); flags = List2() })
                    CmiRegular({ text = Localization.getString("SnapBottomRightPercent"); image = None; click = (fun() -> snapPercentFn "snapbottomright" pct); flags = List2() })
                    CmiSeparator
                    CmiRegular({ text = Localization.getString("SnapCenter"); image = None; click = (fun() -> snapPercentFn "snapcenter" pct); flags = List2() })
                    CmiRegular({ text = Localization.getString("SnapCenterHorizontal"); image = None; click = (fun() -> snapPercentFn "snapcenterhorizontal" pct); flags = List2() })
                    CmiRegular({ text = Localization.getString("SnapCenterVertical"); image = None; click = (fun() -> snapPercentFn "snapcentervertical" pct); flags = List2() })
                ])
                flags = List2()
            })

        // Build the "Move" submenu (edge + corner moves that keep the current size).
        let buildMoveEdgeSubMenu (moveFn: string option -> unit) =
            CmiPopUp({
                text = Localization.getString("MoveEdgeMenu")
                image = None
                items = List2([
                    CmiRegular({ text = Localization.getString("MoveEdgeLeft"); image = None; click = (fun() -> moveFn(Some "left")); flags = List2() })
                    CmiRegular({ text = Localization.getString("MoveEdgeRight"); image = None; click = (fun() -> moveFn(Some "right")); flags = List2() })
                    CmiRegular({ text = Localization.getString("MoveEdgeTop"); image = None; click = (fun() -> moveFn(Some "top")); flags = List2() })
                    CmiRegular({ text = Localization.getString("MoveEdgeBottom"); image = None; click = (fun() -> moveFn(Some "bottom")); flags = List2() })
                    CmiSeparator
                    CmiRegular({ text = Localization.getString("MoveEdgeTopLeft"); image = None; click = (fun() -> moveFn(Some "topleft")); flags = List2() })
                    CmiRegular({ text = Localization.getString("MoveEdgeTopRight"); image = None; click = (fun() -> moveFn(Some "topright")); flags = List2() })
                    CmiRegular({ text = Localization.getString("MoveEdgeBottomLeft"); image = None; click = (fun() -> moveFn(Some "bottomleft")); flags = List2() })
                    CmiRegular({ text = Localization.getString("MoveEdgeBottomRight"); image = None; click = (fun() -> moveFn(Some "bottomright")); flags = List2() })
                ])
                flags = List2()
            })

        let buildSizeSubMenu (screen: MonitorScreen) =
            let area = this.sizeWorkArea(this.getCurrentScreenForWindow(group.topWindow), screen)
            CmiPopUp({
                text = Localization.getString("SizeMenu")
                image = None
                items = List2(SnapGeometry.sizePresets |> List.map (fun (label, width, height) ->
                    let fits = SnapGeometry.sizeBounds (area.X, area.Y, area.Width, area.Height)
                                   (area.X, area.Y) (width, height) |> Option.isSome
                    CmiRegular({ text = label; image = None
                                 click = (fun () -> this.resizeTabGroup(screen, width, height))
                                 flags = List2(if fits then [] else [MenuFlags.MF_GRAYED]) })))
                flags = List2()
            })

        // Inner items for a "Position Move" submenu, flat (the old "Snap Other"
        // submenu is expanded in place): Snap Left / Right / Top / Bottom, the
        // percent-variant snap submenus, Snap Display (and Snap Desktop on
        // multi-monitor when includeSnapDesktop), then the Move edge submenu,
        // each block separated by a divider.
        // Display submenus are appended separately by the caller when needed.
        let buildPositionMoveInnerItems (includeSnapDesktop: bool) (moveFn: string option -> unit) (snapFn: string -> unit) (snapPercentFn: string -> int -> unit) =
            let desktopSnap =
                if includeSnapDesktop && Mon.all.length > 1 then
                    [CmiRegular({ text = Localization.getString("SnapMaximizeDesktop"); image = None; click = (fun() -> snapPercentFn "snapmaximizedesktop" 100); flags = List2() })]
                else []
            [
                CmiRegular({ text = Localization.getString("SnapLeft");   image = None; click = (fun() -> snapFn("snapleft"));   flags = List2() })
                CmiRegular({ text = Localization.getString("SnapRight");  image = None; click = (fun() -> snapFn("snapright"));  flags = List2() })
                CmiSeparator
                CmiRegular({ text = Localization.getString("SnapTop");    image = None; click = (fun() -> snapFn("snaptop"));    flags = List2() })
                CmiRegular({ text = Localization.getString("SnapBottom"); image = None; click = (fun() -> snapFn("snapbottom")); flags = List2() })
                CmiSeparator
                buildSnapPercentSubMenu snapPercentFn 90
                buildSnapPercentSubMenu snapPercentFn 70
                buildSnapPercentSubMenu snapPercentFn 50
                buildSnapPercentSubMenu snapPercentFn 30
                CmiSeparator
                CmiRegular({ text = Localization.getString("SnapMaximizeDisplay"); image = None; click = (fun() -> snapPercentFn "snapmaximizedisplay" 100); flags = List2() })
            ]
            @ desktopSnap
            @ [
                CmiSeparator
                buildMoveEdgeSubMenu moveFn
            ]

        // One "<menuTextPrefix> <display>" submenu per attached monitor, used
        // only when multiple monitors are present. The monitor the window
        // currently sits on is marked with a localized "(here)" suffix and
        // drives the plain
        // (current-screen) move/snap functions, so its behavior matches the
        // previous single menu; other monitors use the screen-targeted
        // functions and get a leading "Same position on this display" item.
        // currentScreenExtraItems is prepended to the current monitor's submenu
        // (keeps the "Same position" entry that the new-window and detach menus
        // show). The submenus are meant to be spliced into the parent menu in
        // place of the old single "<menuTextPrefix>" item.
        let buildPerDisplayPositionItems
                (includeSize: bool)
                (contextHwnd: IntPtr)
                (menuTextPrefix: string)
                (currentScreenExtraItems: ContextMenuItem list)
                (moveFn: string option -> unit)
                (snapFn: string -> unit)
                (snapPercentFn: string -> int -> unit)
                (screenMoveFn: MonitorScreen -> string option -> unit)
                (screenSnapFn: MonitorScreen -> string -> unit)
                (screenSnapPercentFn: MonitorScreen -> string -> int -> unit) : ContextMenuItem list =
            let currentScreen = this.getCurrentScreenForWindow(contextHwnd)
            this.getAllScreensSorted()
            |> Array.map (fun screen ->
                let isCurrentScreen = screen.Equals(currentScreen)
                let menuText =
                    menuTextPrefix + " " + this.getScreenName(screen)
                    + (if isCurrentScreen then " " + Localization.getString("CurrentDisplaySuffix") else "")
                let items =
                    if isCurrentScreen then
                        currentScreenExtraItems @
                        buildPositionMoveInnerItems true moveFn snapFn snapPercentFn
                    else
                        [
                            CmiRegular({ text = Localization.getString("SamePositionThisDisplay"); image = None; click = (fun() -> screenMoveFn screen None); flags = List2() })
                            CmiSeparator
                        ] @
                        buildPositionMoveInnerItems false
                            (fun pos -> screenMoveFn screen pos)
                            (fun dir -> screenSnapFn screen dir)
                            (fun dir pct -> screenSnapPercentFn screen dir pct)
                CmiPopUp({
                    text = menuText
                    image = None
                    items = List2(items @ (if includeSize then [buildSizeSubMenu screen] else []))
                    flags = List2()
                }))
            |> Array.toList

        let window = os.windowFromHwnd(hwnd)
        let pid = window.pid
        let exeName = pid.exeName
        let processPath = pid.processPath

        // Start another window of this tab's program and report what cannot
        // be started (a Store application, a failed start). All three
        // new-launch menu items go through here, and so does the "add a tab
        // to the right of the active tab" hot key (Program.runHotKey) - the
        // same NewWindowLaunch.start, so the menu and the key behave alike.
        let handleLaunchError (launchCall: string -> unit) =
            NewWindowLaunch.start hwnd processPath launchCall

        // Find the decorator whose group contains the given window hwnd.
        // Used by the "new window + position" callback to reach the newly created group.
        let findDecoratorForWindow (winHwnd: IntPtr) =
            lock decorators (fun () ->
                decorators.Values
                |> Seq.tryFind (fun d ->
                    try
                        WinUserApi.IsWindow(d.group.hwnd) && d.group.windows.contains(winHwnd)
                    with _ -> false))

        let menuKey horizontal vertical = TabEdges.menuKey (this.ts.edgeOf (Tab(hwnd))) horizontal vertical
        let currentTabName = TabNameHelper.truncate (this.ts.tabInfo(Tab(hwnd)).text)

        // Disabled header at the top of the menu showing which tab it targets
        let targetTabHeaderItem =
            let caption = TabNameHelper.format (Localization.getString("TargetTabHeader")) currentTabName
            let count = this.ts.visualOrder.count
            let size = String.Format(Localization.getString(TabGroupHeader.formatKey count), count)
            CmiRegular({
                text = TabGroupHeader.append caption size
                image = None
                flags = List2([MenuFlags.MF_GRAYED])
                click = fun() -> ()
            })

        // Item 1: "New tab : right of this tab" — launch and dock into the current tab group
        let newTabInGroupItem =
            CmiRegular({
                text = TabNameHelper.format (Localization.getString(menuKey "NewTabInGroup" "NewTabBelowInGroup")) currentTabName
                flags = List2()
                image = None
                click = fun() ->
                    handleLaunchError (fun path -> Services.program.launchNewWindow group.hwnd hwnd processPath)
            })

        // Item 2: "New window (position)" — launch as standalone then apply a position to the new group.
        // Single monitor: one "New window (position)" submenu. Multiple monitors:
        // one flattened "New window (position) <display>" item per display,
        // spliced directly into the "New launch" menu.
        let newWindowPositionItems =
            let launchStandaloneThen (postAction: TabStripDecorator -> IntPtr -> unit) =
                handleLaunchError (fun path ->
                    Services.program.launchStandaloneWindow processPath (fun newHwnd ->
                        // The decorator for the new group is registered asynchronously on the group's thread,
                        // and the new window needs a moment to settle before it can be repositioned.
                        // Poll a few times until the decorator is available.
                        async {
                            do! Async.Sleep 200
                            let mutable attempts = 20
                            let mutable applied = false
                            while not applied && attempts > 0 do
                                match findDecoratorForWindow newHwnd with
                                | Some(newDec) ->
                                    applied <- true
                                    try
                                        newDec.group.invokeSync(fun () -> postAction newDec newHwnd)
                                    with _ -> ()
                                | None ->
                                    do! Async.Sleep 100
                                    attempts <- attempts - 1
                        } |> Async.Start))
            let samePositionItem =
                CmiRegular({
                    text = Localization.getString("DetachTabSamePosition")
                    image = None
                    click = (fun() -> launchStandaloneThen (fun _ _ -> ()))
                    flags = List2()
                })
            let currentScreenExtraItems = [samePositionItem; CmiSeparator]
            let moveFn = (fun pos -> launchStandaloneThen (fun d h -> d.moveTabGroupToPosition(h, pos)))
            let snapFn = (fun dir -> launchStandaloneThen (fun d h -> d.moveTabGroupToSnap(h, dir)))
            let snapPercentFn = (fun dir pct -> launchStandaloneThen (fun d h -> d.moveTabGroupToSnapWithPercent(h, dir, pct)))
            if this.getAllScreensSorted().Length > 1 then
                // Multi-monitor: flattened "New window (position) <display>" items
                buildPerDisplayPositionItems false hwnd (Localization.getString("NewWindowPositionMenu")) currentScreenExtraItems
                    moveFn snapFn snapPercentFn
                    (fun s pos -> launchStandaloneThen (fun d h -> d.moveTabGroupToScreen(h, s, pos)))
                    (fun s dir -> launchStandaloneThen (fun d h -> d.moveTabGroupToScreenSnap(h, s, dir)))
                    (fun s dir pct -> launchStandaloneThen (fun d h -> d.moveTabGroupToScreenSnapWithPercent(h, s, dir, pct)))
            else
                // Single monitor: one "New window (position)" submenu (unchanged)
                [ CmiPopUp({
                    text = Localization.getString("NewWindowPositionMenu")
                    image = None
                    items = List2(currentScreenExtraItems @ buildPositionMoveInnerItems true moveFn snapFn snapPercentFn)
                    flags = List2()
                }) ]

        // Item 3: "New window (link to group)" submenu — launch as a new tab docked into an existing other group
        let newWindowLinkGroupItem =
            let groupItems = linkGroupItems (fun _ -> false) (fun decorator ->
                handleLaunchError (fun path -> Services.program.launchNewWindow decorator.group.hwnd hwnd path))
            CmiPopUp({
                text = Localization.getString("NewWindowLinkGroupMenu")
                image = None
                items = List2(groupItems)
                flags = if List.isEmpty groupItems then List2([MenuFlags.MF_GRAYED]) else List2()
            })

        // Top-level wrapper: "New launch : execute <exe>" containing the variants
        // (the "New window (position)" part expands to one item per display).
        let newLaunchMenu =
            let exeName = System.IO.Path.GetFileName(processPath)
            CmiPopUp({
                text = String.Format(Localization.getString("NewLaunchMenu"), exeName)
                image = None
                items = List2([newTabInGroupItem; CmiSeparator] @ newWindowPositionItems
                              @ [CmiSeparator; newWindowLinkGroupItem])
                flags = List2()
            })

        
        let allTabsEdge = this.ts.edgeOf (Tab(hwnd))
        let allTabsTargets = (this.ts.bandOf allTabsEdge).list |> List.map (fun (Tab h) -> h)
        let allTabsEdgeName = Localization.getString(string allTabsEdge + "TabsInMenu")
        let showEdgeActions = this.ts.visualOrder.list |> List.exists (fun tab -> this.ts.edgeOf tab <> allTabsEdge)

        let tabDirectionSubMenu =
            let buildTargets text targets enabled =
                CmiPopUp({
                    text = text
                    image = None
                    flags = if enabled then List2() else List2([MenuFlags.MF_GRAYED])
                    items = List2(TabEdges.priority |> List.map (fun edge ->
                        let current = targets |> List.forall (fun h -> this.ts.edgeOf(Tab h) = edge)
                        CmiRegular({
                            text = Localization.getString(string edge + "Tabs")
                            image = None
                            flags = if current || not (TabEdges.isAllowed this.ts.allowedEdges edge) then List2([MenuFlags.MF_GRAYED]) else List2()
                            click = fun () ->
                                let moving = targets |> List.filter (fun h -> this.ts.edgeOf(Tab h) <> edge)
                                this.ts.moveTabsToEdgeFromMenu(moving |> List.map Tab, edge)
                                this.updateTsPlacement()
                        })))
                })
            let targets = this.actionTargets(hwnd)
            let targetText =
                if targets.Length > 1 then
                    this.formatSelectedTabs(hwnd, targets, "MoveTabEdgeSelectedFormat")
                else Localization.getString("MoveTabEdgeThis")
            CmiPopUp({
                text = Localization.getString("TabDirectionMenu")
                image = None
                flags = List2()
                items = List2([
                    buildTargets targetText targets true
                    buildTargets (String.Format(Localization.getString("MoveTabEdgeCurrentFormat"), allTabsEdgeName)) allTabsTargets true
                    buildTargets (Localization.getString("MoveTabEdgeAll")) (this.ts.visualOrder.list |> List.map (fun (Tab h) -> h)) showEdgeActions
                ])
            })

        let tabPositionSubMenu =
            let currentAlignment = group.getTabAlign(hwnd)
            let allTopLeft = allTabsTargets |> List.forall (fun h -> group.getTabAlign(h) = TopLeft)
            let allTopRight = allTabsTargets |> List.forall (fun h -> group.getTabAlign(h) = TopRight)
            let shortTabName = TabNameHelper.truncate (this.ts.tabInfo(Tab(hwnd)).text)
            let alignTargets = this.actionTargets(hwnd)
            let isMultiSelect = alignTargets.Length > 1
            // Per-tab item gating: union of targets in multi-select,
            // just the right-clicked tab otherwise.
            let allTargetsLeft = alignTargets |> List.forall (fun h -> group.getTabAlign(h) = TopLeft)
            let allTargetsRight = alignTargets |> List.forall (fun h -> group.getTabAlign(h) = TopRight)

            let leftItemText =
                if isMultiSelect then
                    this.formatSelectedTabs(hwnd, alignTargets, (menuKey "AlignSelectedTabsLeftFormat" "AlignSelectedTabsTopFormat"))
                else
                    TabNameHelper.format (Localization.getString(menuKey "AlignThisTabLeft" "AlignThisTabTop")) shortTabName
            let leftItemFlags =
                let disable = if isMultiSelect then allTargetsLeft else currentAlignment = TopLeft
                if disable then List2([MenuFlags.MF_GRAYED]) else List2()
            let leftItem =
                CmiRegular({
                    text = leftItemText
                    image = None
                    flags = leftItemFlags
                    click = fun() -> group.setTabsAlign(alignTargets, TopLeft)
                })

            let rightItemText =
                if isMultiSelect then
                    this.formatSelectedTabs(hwnd, alignTargets, (menuKey "AlignSelectedTabsRightFormat" "AlignSelectedTabsBottomFormat"))
                else
                    TabNameHelper.format (Localization.getString(menuKey "AlignThisTabRight" "AlignThisTabBottom")) shortTabName
            let rightItemFlags =
                let disable = if isMultiSelect then allTargetsRight else currentAlignment = TopRight
                if disable then List2([MenuFlags.MF_GRAYED]) else List2()
            let rightItem =
                CmiRegular({
                    text = rightItemText
                    image = None
                    flags = rightItemFlags
                    click = fun() -> group.setTabsAlign(alignTargets, TopRight)
                })

            CmiPopUp({
                text = Localization.getString("TabAlignMenu")
                image = None
                items = List2([
                    leftItem
                    rightItem
                    CmiSeparator
                    CmiRegular({
                        text = String.Format(Localization.getString(menuKey "AlignAllTopLeftFormat" "AlignAllToTopFormat"), allTabsEdgeName)
                        image = None
                        flags = if allTopLeft then List2([MenuFlags.MF_GRAYED]) else List2()
                        click = fun() ->
                            group.setTabsAlign(allTabsTargets, TopLeft)
                    })
                    CmiRegular({
                        text = String.Format(Localization.getString(menuKey "AlignAllTopRightFormat" "AlignAllToBottomFormat"), allTabsEdgeName)
                        image = None
                        flags = if allTopRight then List2([MenuFlags.MF_GRAYED]) else List2()
                        click = fun() ->
                            group.setTabsAlign(allTabsTargets, TopRight)
                    })
                ])
                flags = List2()
            })

        let tabPinSubMenu =
            let tabName = TabNameHelper.truncate (this.ts.tabInfo(Tab(hwnd)).text)
            let pinTargets = this.actionTargets(hwnd)
            let isMultiSelect = pinTargets.Length > 1
            let isPinned = group.isPinned(hwnd)

            // Multi-select: gating against the union of the targets.
            // Single-select: gating against just the right-clicked tab.
            let allTargetsPinned = pinTargets |> List.forall group.isPinned
            let noneTargetsPinned = pinTargets |> List.forall (fun h -> not (group.isPinned h))

            let pinItemText =
                if isMultiSelect then
                    this.formatSelectedTabs(hwnd, pinTargets, "PinSelectedTabsFormat")
                else
                    TabNameHelper.format (Localization.getString("PinThisTab")) tabName
            let pinItemFlags =
                let disable = if isMultiSelect then allTargetsPinned else isPinned
                if disable then List2([MenuFlags.MF_GRAYED]) else List2()
            let pinItem =
                CmiRegular({
                    text = pinItemText
                    image = None
                    flags = pinItemFlags
                    click = fun() -> pinTargets |> List.iter group.pinTab
                })

            let unpinItemText =
                if isMultiSelect then
                    this.formatSelectedTabs(hwnd, pinTargets, "UnpinSelectedTabsFormat")
                else
                    TabNameHelper.format (Localization.getString("UnpinThisTab")) tabName
            let unpinItemFlags =
                let disable = if isMultiSelect then noneTargetsPinned else not isPinned
                if disable then List2([MenuFlags.MF_GRAYED]) else List2()
            let unpinItem =
                CmiRegular({
                    text = unpinItemText
                    image = None
                    flags = unpinItemFlags
                    click = fun() -> pinTargets |> List.iter group.unpinTab
                })

            // Edge actions include every tab on the clicked edge, independently of selection.
            let pinEdgeFlags = if allTabsTargets |> List.forall group.isPinned then List2([MenuFlags.MF_GRAYED]) else List2()
            let pinEdgeItem =
                CmiRegular({
                    text = String.Format(Localization.getString("PinEdgeTabsFormat"), allTabsEdgeName)
                    image = None
                    flags = pinEdgeFlags
                    click = fun() -> group.pinAll(edge=allTabsEdge)
                })
            let unpinEdgeFlags = if allTabsTargets |> List.forall (fun h -> not (group.isPinned h)) then List2([MenuFlags.MF_GRAYED]) else List2()
            let unpinEdgeItem =
                CmiRegular({
                    text = String.Format(Localization.getString("UnpinEdgeTabsFormat"), allTabsEdgeName)
                    image = None
                    flags = unpinEdgeFlags
                    click = fun() -> group.unpinAll(edge=allTabsEdge)
                })

            // Group actions include all edges, including in top-only mode.
            let groupPinTargets = this.ts.visualOrder.list |> List.map (fun (Tab h) -> h)
            let pinAllItem =
                CmiRegular({
                    text = Localization.getString("PinAllTabs")
                    image = None
                    flags = if groupPinTargets |> List.forall group.isPinned then List2([MenuFlags.MF_GRAYED]) else List2()
                    click = fun() -> group.pinAll()
                })
            let unpinAllItem =
                CmiRegular({
                    text = Localization.getString("UnpinAllTabs")
                    image = None
                    flags = if groupPinTargets |> List.forall (fun h -> not (group.isPinned h)) then List2([MenuFlags.MF_GRAYED]) else List2()
                    click = fun() -> group.unpinAll()
                })

            CmiPopUp({
                text = Localization.getString("PinTabMenu")
                image = None
                items = List2([
                    pinItem
                    unpinItem
                    CmiSeparator
                    if showEdgeActions then
                        pinEdgeItem
                        unpinEdgeItem
                        CmiSeparator
                    pinAllItem
                    unpinAllItem
                ])
                flags = List2()
            })

        let tabColorSubMenu =
            let currentFill = group.getTabFillColor(hwnd)
            let currentUnderline = group.getTabUnderlineColor(hwnd)
            let currentBorder = group.getTabBorderColor(hwnd)
            let shortTabText = TabNameHelper.truncate (this.ts.tabInfo(Tab(hwnd)).text)
            // Check if color matches by RGB (ignore alpha) and type
            let isColorMatch (current: Color option) (defColor: Color) =
                current |> Option.exists (fun c ->
                    int c.R = int defColor.R && int c.G = int defColor.G && int c.B = int defColor.B)
            // Draw checkmark overlay on icon
            let drawCheckmark (g: Graphics) =
                g.SmoothingMode <- Drawing2D.SmoothingMode.AntiAlias
                use outlinePen = new Pen(Color.FromArgb(200, 0, 0, 0), 3.0f)
                g.DrawLine(outlinePen, 3, 8, 6, 12)
                g.DrawLine(outlinePen, 6, 12, 13, 4)
                use checkPen = new Pen(Color.White, 2.0f)
                g.DrawLine(checkPen, 3, 8, 6, 12)
                g.DrawLine(checkPen, 6, 12, 13, 4)
            // Menu swatch icons are DRAWN at the menu's scale instead of being
            // drawn at 16 px and enlarged afterwards, so the borders and the
            // check mark stay crisp next to system-scaled menu text. The
            // coordinates below stay 96-dpi design units; the transform does
            // the conversion, pen widths included.
            //
            // menuScale, NOT the strip's scale: the menu is not necessarily on
            // the strip's monitor. Right-clicking near the right edge of a
            // strip on a 150% monitor opens the menu on the 100% monitor next
            // to it, and painting a 24 px swatch that Win32Menu then reduces to
            // 16 px is both wasted work and a softer picture than painting 16
            // px directly.
            let swatch (paint: Graphics -> unit) =
                let size = Dpi.px menuScale 16
                let img = Img(Sz(size, size))
                let g = img.graphics
                if menuScale <> 1.0 then
                    g.ScaleTransform(float32 menuScale, float32 menuScale)
                paint g
                img
            let createFillIcon (color: Color) (isChecked: bool) = swatch <| fun g ->
                let size = 16
                g.Clear(Color.White)
                use brush = new SolidBrush(Color.FromArgb(255, int color.R, int color.G, int color.B))
                g.FillRectangle(brush, 1, 1, size - 2, size - 2)
                use pen = new Pen(Color.FromArgb(160, 160, 160), 1.0f)
                g.DrawRectangle(pen, 0, 0, size - 1, size - 1)
                if isChecked then drawCheckmark g
            let createUnderlineIcon (color: Color) (isChecked: bool) = swatch <| fun g ->
                let size = 16
                g.Clear(Color.Transparent)
                use pen = new Pen(Color.FromArgb(160, 160, 160), 1.0f)
                g.DrawRectangle(pen, 0, 0, size - 1, size - 1)
                use underlineBrush = new SolidBrush(Color.FromArgb(255, int color.R, int color.G, int color.B))
                g.FillRectangle(underlineBrush, 1, size - 4, size - 2, 3)
                if isChecked then
                    g.SmoothingMode <- Drawing2D.SmoothingMode.AntiAlias
                    use outlinePen = new Pen(Color.FromArgb(200, 0, 0, 0), 3.0f)
                    g.DrawLine(outlinePen, 3, 7, 6, 11)
                    g.DrawLine(outlinePen, 6, 11, 13, 3)
                    use checkPen = new Pen(Color.White, 2.0f)
                    g.DrawLine(checkPen, 3, 7, 6, 11)
                    g.DrawLine(checkPen, 6, 11, 13, 3)
            let createBorderIcon (color: Color) (isChecked: bool) = swatch <| fun g ->
                let size = 16
                g.Clear(Color.Transparent)
                use borderPen = new Pen(Color.FromArgb(255, int color.R, int color.G, int color.B), 2.0f)
                g.DrawRectangle(borderPen, 1, 1, size - 3, size - 3)
                if isChecked then drawCheckmark g
            // Targets for the "this tab" submenu and its clear item.
            // Single-select: just the right-clicked hwnd.
            // Multi-select: active + selected (unioned with right-clicked).
            let colorTargets = this.actionTargets(hwnd)
            let isMultiSelect = colorTargets.Length > 1

            // For multi-select, checkmark is shown only when ALL targets
            // share the same color for that channel. The reference color
            // for the comparison is the right-clicked tab's color.
            let allSameFill =
                let xs = colorTargets |> List.map group.getTabFillColor
                match xs with
                | [] -> false
                | head :: rest -> rest |> List.forall (fun v -> v = head)
            let allSameUnderline =
                let xs = colorTargets |> List.map group.getTabUnderlineColor
                match xs with
                | [] -> false
                | head :: rest -> rest |> List.forall (fun v -> v = head)
            let allSameBorder =
                let xs = colorTargets |> List.map group.getTabBorderColor
                match xs with
                | [] -> false
                | head :: rest -> rest |> List.forall (fun v -> v = head)

            // Build color selection submenu items for a set of target hwnds.
            // In multi-select the checkmark is gated by allSame* so it only
            // appears when every target shares that exact color.
            let buildColorItems (targetHwnds: IntPtr list) =
                let fillItems =
                    TabColorDefs.fillDefs |> List.map (fun def ->
                        let baseMatch = isColorMatch currentFill def.color
                        let isChecked = if isMultiSelect then allSameFill && baseMatch else baseMatch
                        CmiRegular({
                            text = Localization.getString(def.labelKey)
                            image = Some(createFillIcon def.color isChecked)
                            flags = List2()
                            click = fun() ->
                                let color = if isChecked then None else Some(def.color)
                                targetHwnds |> List.iter (fun h -> group.setTabFillColor(h, color))
                        })
                    )
                let underlineItems =
                    TabColorDefs.underlineDefs |> List.map (fun def ->
                        let baseMatch = isColorMatch currentUnderline def.color
                        let isChecked = if isMultiSelect then allSameUnderline && baseMatch else baseMatch
                        CmiRegular({
                            text = Localization.getString(def.labelKey)
                            image = Some(createUnderlineIcon def.color isChecked)
                            flags = List2()
                            click = fun() ->
                                let color = if isChecked then None else Some(def.color)
                                targetHwnds |> List.iter (fun h -> group.setTabUnderlineColor(h, color))
                        })
                    )
                let borderItems =
                    TabColorDefs.borderDefs |> List.map (fun def ->
                        let baseMatch = isColorMatch currentBorder def.color
                        let isChecked = if isMultiSelect then allSameBorder && baseMatch else baseMatch
                        CmiRegular({
                            text = Localization.getString(def.labelKey)
                            image = Some(createBorderIcon def.color isChecked)
                            flags = List2()
                            click = fun() ->
                                let color = if isChecked then None else Some(def.color)
                                targetHwnds |> List.iter (fun h -> group.setTabBorderColor(h, color))
                        })
                    )
                fillItems @ [CmiSeparator] @ underlineItems @ [CmiSeparator] @ borderItems

            // Clear all color settings (fill, underline, border) for a list of target tabs
            let clearColorFor (targetHwnds: IntPtr list) () =
                targetHwnds |> List.iter (fun h ->
                    group.setTabFillColor(h, None)
                    group.setTabUnderlineColor(h, None)
                    group.setTabBorderColor(h, None)
                )

            let hasAnyColor (hwnds: IntPtr list) =
                hwnds |> List.exists (fun h ->
                    (group.getTabFillColor h).IsSome
                    || (group.getTabUnderlineColor h).IsSome
                    || (group.getTabBorderColor h).IsSome)

            let thisTabSubMenuText =
                if isMultiSelect then
                    this.formatSelectedTabs(hwnd, colorTargets, "TabColorSelectedTabsFormat")
                else
                    TabNameHelper.format (Localization.getString("TabColorThisTab")) shortTabText
            let thisTabSubMenu =
                CmiPopUp({
                    text = thisTabSubMenuText
                    image = None
                    items = List2(buildColorItems colorTargets)
                    flags = List2()
                })
            let clearThisTabText =
                if isMultiSelect then
                    this.formatSelectedTabs(hwnd, colorTargets, "TabColorClearSelectedTabsFormat")
                else
                    Localization.getString("TabColorClearThisTab")
            // Multi-select: gray out when none of the selected targets has any color set.
            // Single-select: keep enabled regardless (existing behavior).
            let clearThisTabFlags =
                if isMultiSelect && not (hasAnyColor colorTargets) then List2([MenuFlags.MF_GRAYED])
                else List2()
            let clearThisTabItem =
                CmiRegular({
                    text = clearThisTabText
                    image = None
                    flags = clearThisTabFlags
                    click = clearColorFor colorTargets
                })

            // "Clear all tabs" — operates on every tab in the group; gray
            // out when no tab in the group has any color set.
            let allHwnds = group.windows.items.list
            let clearAllTabsFlags =
                if hasAnyColor allHwnds then List2() else List2([MenuFlags.MF_GRAYED])
            let clearAllTabsItem =
                CmiRegular({
                    text = Localization.getString("TabColorClearAllTabs")
                    image = None
                    flags = clearAllTabsFlags
                    click = clearColorFor allHwnds
                })

            CmiPopUp({
                text = Localization.getString("TabColorMenu")
                image = None
                items = List2([
                    thisTabSubMenu
                    clearThisTabItem
                    CmiSeparator
                    clearAllTabsItem
                ])
                flags = List2()
            })

        let tabNameSubMenu =
            // Rename / reset operate on the single right-clicked tab only
            // (never on a multi-select), so show which tab that is: the
            // current displayed name for rename, and the name the tab will
            // return to (the window title) for reset
            let displayedName = TabNameHelper.truncate (this.ts.tabInfo(Tab(hwnd)).text)
            let resetName = TabNameHelper.truncate (try os.windowFromHwnd(hwnd).text with _ -> "")
            CmiPopUp({
                text = Localization.getString("TabNameEdit")
                image = None
                items = List2([
                    CmiRegular({
                        text = TabNameHelper.format (Localization.getString("RenameTabFormat")) displayedName
                        image = None
                        flags = List2()
                        click = fun() ->
                            this.beginRename(hwnd)
                    })
                    CmiRegular({
                        // When disabled (tab not renamed) the reset-to name is
                        // meaningless, so show the plain label without it
                        text =
                            if group.isRenamed(hwnd) then
                                TabNameHelper.format (Localization.getString("ResetTabNameFormat")) resetName
                            else
                                TabNameHelper.stripName (Localization.getString("ResetTabNameFormat"))
                        image = None
                        flags = if group.isRenamed(hwnd) then List2() else List2([MenuFlags.MF_GRAYED])
                        click = fun() -> group.setTabName(hwnd, None)
                    })
                ])
                flags = List2()
            })

        let systemSubMenu =
            let window = os.windowFromHwnd(hwnd)
            let exeName = try System.IO.Path.GetFileName(window.pid.processPath) with _ -> "unknown.exe"
            let exePath = try window.pid.processPath with _ -> ""
            let windowTitle = try window.text with _ -> ""
            let shortWindowTitle = TabNameHelper.truncate windowTitle
            // Use visual order (left-to-right) so multi-select clipboard
            // output matches the tab strip ordering. actionTargets alone
            // returns active-first, which is non-obvious for "Copy paths".
            let sysTargets = this.actionTargetsInVisualOrder(hwnd)
            let isMultiSelect = sysTargets.Length > 1

            // Copy items: in multi-select, join each target's path / title
            // with CR-LF for the clipboard. In single-select, behaves like before.
            let copyPathItem =
                if isMultiSelect then
                    CmiRegular({
                        text = this.formatSelectedTabs(hwnd, sysTargets, "SystemCopySelectedTabsExePathFormat")
                        image = None
                        flags = List2()
                        click = fun() ->
                            try
                                let paths =
                                    sysTargets
                                    |> List.map (fun h ->
                                        try (os.windowFromHwnd h).pid.processPath with _ -> "")
                                    |> List.filter (fun s -> s <> "")
                                System.Windows.Forms.Clipboard.SetText(String.concat "\r\n" paths)
                            with _ -> ()
                    })
                else
                    CmiRegular({
                        text = String.Format(Localization.getString("SystemCopyExePath"), exeName)
                        image = None
                        flags = List2()
                        click = fun() ->
                            try System.Windows.Forms.Clipboard.SetText(exePath) with _ -> ()
                    })

            let copyTitleItem =
                if isMultiSelect then
                    CmiRegular({
                        text = this.formatSelectedTabs(hwnd, sysTargets, "SystemCopySelectedTabsWindowTitleFormat")
                        image = None
                        flags = List2()
                        click = fun() ->
                            try
                                let titles =
                                    sysTargets
                                    |> List.map (fun h ->
                                        try (os.windowFromHwnd h).text with _ -> "")
                                    |> List.filter (fun s -> s <> "")
                                System.Windows.Forms.Clipboard.SetText(String.concat "\r\n" titles)
                            with _ -> ()
                    })
                else
                    CmiRegular({
                        text = TabNameHelper.format (Localization.getString("SystemCopyWindowTitle")) shortWindowTitle
                        image = None
                        flags = List2()
                        click = fun() ->
                            try System.Windows.Forms.Clipboard.SetText(windowTitle) with _ -> ()
                    })

            // Open folder / Kill process target a single process, so they are
            // disabled in multi-select regardless of the targets' state.
            let openFolderFlags =
                if isMultiSelect then List2([MenuFlags.MF_GRAYED])
                elif exePath <> "" then List2()
                else List2([MenuFlags.MF_GRAYED])
            let openFolderItem =
                CmiRegular({
                    text = String.Format(Localization.getString("SystemOpenExeFolder"), exeName)
                    image = None
                    flags = openFolderFlags
                    click = fun() ->
                        try
                            let folder = System.IO.Path.GetDirectoryName(exePath)
                            let psi = System.Diagnostics.ProcessStartInfo(folder)
                            psi.UseShellExecute <- true
                            System.Diagnostics.Process.Start(psi) |> ignore
                        with _ -> ()
                })

            let killProcessItem =
                CmiRegular({
                    text = Localization.getString("SystemKillProcess")
                    image = None
                    flags = if isMultiSelect then List2([MenuFlags.MF_GRAYED]) else List2()
                    click = fun() ->
                        try
                            let proc = System.Diagnostics.Process.GetProcessById(window.pid.pid)
                            proc.Kill()
                        with _ -> ()
                })

            CmiPopUp({
                text = Localization.getString("SystemMenu")
                image = None
                items = List2([
                    copyPathItem
                    copyTitleItem
                    CmiSeparator
                    openFolderItem
                    killProcessItem
                ])
                flags = List2()
            })

        let closeTabItem =
            let closeTargets = this.actionTargets(hwnd)
            let menuText =
                if closeTargets.Length > 1 then
                    this.formatSelectedTabs(hwnd, closeTargets, "CloseSelectedTabsFormat")
                else
                    let displayText = TabNameHelper.truncate (this.ts.tabInfo(Tab(hwnd)).text)
                    TabNameHelper.format (Localization.getString("CloseTab")) displayText
            CmiRegular({
                text = menuText
                image = None
                click = fun() ->
                    closeTargets |> List.iter this.onCloseWindow
                flags = List2()
            })

        // Left/Right close items reference the right-clicked tab's position
        // in visual order, which is ambiguous under multi-select (no single
        // "pivot"). They are grayed out in that case.
        let isCloseMultiSelect = (this.actionTargets(hwnd)).Length > 1

        let closeRightTabsItem =
            let currentTab = Tab(hwnd)
            let band = this.ts.bandOf (this.ts.edgeOf currentTab)
            let tabIndex = band.tryFindIndex((=) currentTab)
            let rightTabCount =
                tabIndex |> Option.map(fun index -> band.count - index - 1) |> Option.defaultValue 0
            let displayText =
                let formatKey = (menuKey "CloseTabsToTheRightFormat" "CloseTabsBelowFormat")
                let formatString = Localization.getString(formatKey)
                if formatString = null then
                    failwithf "Resource string '%s' not found" formatKey
                let tabWord =
                    if rightTabCount = 1 then
                        Localization.getString("TabSingular")
                    else
                        Localization.getString("TabPlural")
                String.Format(formatString, rightTabCount, tabWord)
            CmiRegular({
                text = displayText
                image = None
                click = fun() -> this.onCloseRightTabWindows hwnd
                flags = grayed(isCloseMultiSelect || rightTabCount = 0)
            })

        let closeLeftTabsItem =
            let currentTab = Tab(hwnd)
            let tabIndex = (this.ts.bandOf (this.ts.edgeOf currentTab)).tryFindIndex((=) currentTab)
            let leftTabCount =
                tabIndex |> Option.defaultValue 0
            let displayText =
                let formatKey = (menuKey "CloseTabsToTheLeftFormat" "CloseTabsAboveFormat")
                let formatString = Localization.getString(formatKey)
                if formatString = null then
                    failwithf "Resource string '%s' not found" formatKey
                let tabWord =
                    if leftTabCount = 1 then
                        Localization.getString("TabSingular")
                    else
                        Localization.getString("TabPlural")
                String.Format(formatString, leftTabCount, tabWord)
            CmiRegular({
                text = displayText
                image = None
                click = fun() -> this.onCloseLeftTabWindows hwnd
                flags = grayed(isCloseMultiSelect || leftTabCount = 0)
            })

        // "Close other tabs" in single-select becomes "Close N unselected
        // tabs" in multi-select, closing every tab not in (active + selected).
        let closeOtherTabsItem =
            let targets = this.actionTargets(hwnd)
            if targets.Length > 1 then
                let targetSet = Set.ofList targets
                let unselected =
                    group.windows.items.list |> List.filter (fun h -> not (targetSet.Contains h))
                CmiRegular({
                    text = String.Format(Localization.getString("CloseUnselectedTabsFormat"), unselected.Length)
                    image = None
                    click = fun() -> unselected |> List.iter this.onCloseWindow
                    flags = grayed(unselected.Length = 0)
                })
            else
                let otherCount = allTabsTargets.Length - 1
                let caption =
                    if showEdgeActions then
                        String.Format(Localization.getString("CloseOtherEdgeTabsFormat"), allTabsEdgeName, otherCount)
                    else
                        String.Format(Localization.getString("CloseOtherTabsFormat"), otherCount)
                CmiRegular({
                    text = caption
                    image = None
                    click = fun() -> this.onCloseOtherWindows hwnd
                    flags = grayed(otherCount = 0)
                })

        let closeEdgeTabsItem =
            CmiRegular({
                text = String.Format(Localization.getString("CloseEdgeTabsFormat"), allTabsEdgeName, allTabsTargets.Length)
                image = None
                click = fun() -> this.onCloseAllWindows(edge=allTabsEdge)
                flags = List2()
            })

        let closeAllTabsItem =
            CmiRegular({
                text = String.Format(Localization.getString("CloseAllTabsFormat"), group.windows.items.count)
                image = None
                click = fun() -> this.onCloseAllWindows()
                flags = List2()
            })

        let managerItem =
            CmiRegular({
                text = Localization.getString("SettingsMenu")
                image = None
                click = fun() -> Services.managerView.show()
                flags = grayed(not (DialogState.canOpenSettings Services.program.isDisabled))
            })

        // "Detach this tab and move (position)". Single monitor: one submenu.
        // Multiple monitors: one flattened "<detach position move> <display>"
        // item per display, spliced directly into the "Tab detach" menu.
        let detachTabPositionItems =
            let isEnabled = group.zorder.value.length > 1  // Only enabled when there are 2+ tabs

            // When the user has a multi-select active, "Detach this tab"
            // detaches the active + selected tabs together into ONE new tab
            // group (similar shape to Split Right/Left). detachOrSplitToPosition
            // handles both cases (single-tab fallback when no selection).
            let currentScreenExtraItems = [
                CmiRegular({ text = Localization.getString("DetachTabSamePosition"); image = None; click = (fun() -> this.detachOrSplitToPosition(hwnd, None)); flags = List2() })
                CmiSeparator
            ]
            let moveFn = (fun pos -> this.detachOrSplitToPosition(hwnd, pos))
            let snapFn = (fun dir -> this.detachOrSplitToSnap(hwnd, dir))
            let snapPercentFn = (fun dir pct -> this.detachOrSplitToSnapWithPercent(hwnd, dir, pct))

            // The tab name / selected-tab count now lives on the parent "Tab
            // detach" menu, so this submenu is just "Position" in all cases
            let detachMoveText = Localization.getString("DetachAndMovePosTab")

            if not isEnabled then
                // Disabled (single tab): a single grayed item, no per-display split
                [ CmiPopUp({
                    text = detachMoveText
                    image = None
                    items = List2(currentScreenExtraItems @ buildPositionMoveInnerItems true moveFn snapFn snapPercentFn)
                    flags = List2([MenuFlags.MF_GRAYED])
                }) ]
            elif this.getAllScreensSorted().Length > 1 then
                // Multi-monitor: flattened "<detach position move> <display>" items
                buildPerDisplayPositionItems false hwnd detachMoveText currentScreenExtraItems
                    moveFn snapFn snapPercentFn
                    (fun s pos -> this.detachOrSplitToScreen(hwnd, s, pos))
                    (fun s dir -> this.detachOrSplitToScreenSnap(hwnd, s, dir))
                    (fun s dir pct -> this.detachOrSplitToScreenSnapWithPercent(hwnd, s, dir, pct))
            else
                // Single monitor: one submenu (unchanged)
                [ CmiPopUp({
                    text = detachMoveText
                    image = None
                    items = List2(currentScreenExtraItems @ buildPositionMoveInnerItems true moveFn snapFn snapPercentFn)
                    flags = List2()
                }) ]

        // "Position Move" for the tab group: direct Snap Left/Right/Top/Bottom,
        // Move submenu, Snap Other submenu (percent variants + display/desktop max).
        // Single monitor: one "Position Move" submenu. Multiple monitors: one
        // top-level "Position Move <display>" submenu per display (the display
        // the window is on is marked with a localized "(here)" suffix).
        let positionMoveMenuItems =
            let moveFn = (fun pos -> this.moveTabGroupToPosition(hwnd, pos))
            let snapFn = (fun dir -> this.moveTabGroupToSnap(hwnd, dir))
            let snapPercentFn = (fun dir pct -> this.moveTabGroupToSnapWithPercent(hwnd, dir, pct))
            if this.getAllScreensSorted().Length > 1 then
                buildPerDisplayPositionItems true hwnd (Localization.getString("PositionMoveMenu")) []
                    moveFn snapFn snapPercentFn
                    (fun s pos -> this.moveTabGroupToScreen(hwnd, s, pos))
                    (fun s dir -> this.moveTabGroupToScreenSnap(hwnd, s, dir))
                    (fun s dir pct -> this.moveTabGroupToScreenSnapWithPercent(hwnd, s, dir, pct))
            else
                [ CmiPopUp({
                    text = Localization.getString("PositionMoveMenu")
                    image = None
                    items = List2(buildPositionMoveInnerItems true moveFn snapFn snapPercentFn
                                  @ [buildSizeSubMenu (this.getCurrentScreenForWindow(group.topWindow))])
                    flags = List2()
                }) ]

        let moveTabGroupToGroupMenu =
            let items = linkGroupItems (fun _ -> false) (fun decorator ->
                let targets = group.visualOrder.list
                this.linkFromMenu(decorator.group, targets,
                    fun () -> this.moveTabGroupToGroup(decorator.group, targets)))
            CmiPopUp({
                text = Localization.getString("DockingTabGroupToGroup")
                image = None
                items = List2(items)
                flags = if items.IsEmpty then List2([MenuFlags.MF_GRAYED]) else List2()
            })

        List2(
            [
            Some(targetTabHeaderItem)
            Some(CmiSeparator)
            Some(newLaunchMenu)
            Some(CmiSeparator)
            ]
            @ (positionMoveMenuItems |> List.map Some)
            @ [
            Some(moveTabGroupToGroupMenu)
            Some(CmiSeparator)
            // Tab Detach and Split submenu containing both detach and link menus
            (
                let items = linkGroupItems (fun info -> List.contains hwnd info.tabHwnds) (fun decorator ->
                    let targets = this.actionTargetsInVisualOrder(hwnd)
                    this.linkFromMenu(decorator.group, targets,
                        fun () -> targets |> List.iter (fun h -> this.moveTabToGroup(h, decorator.group))))
                let moveTabMenu =
                    if items.IsEmpty then None else
                    Some(CmiPopUp({
                        text = Localization.getString("DetachAndDockingTabToGroup")
                        image = None
                        items = List2(items)
                        flags = if group.zorder.value.length <= 1 then List2([MenuFlags.MF_GRAYED]) else List2()
                    }))

                // Wrap detach menus in parent submenu (the detach-position part
                // expands to one item per display on multi-monitor setups)
                // Inside a submenu the link entry is set apart from the
                // position entries above it.
                let subMenuItems =
                    (detachTabPositionItems |> List.map Some)
                    @ (if moveTabMenu.IsSome && not detachTabPositionItems.IsEmpty then [ Some CmiSeparator ] else [])
                    @ [ moveTabMenu ]
                    |> List.choose id

                if subMenuItems.IsEmpty then None
                else
                    // "Detach this tab : <name>", or "Detach <n> selected tabs"
                    // when a multi-select is active
                    let detachParentTargets = this.actionTargets(hwnd)
                    let detachParentText =
                        if detachParentTargets.Length > 1 then
                            this.formatSelectedTabs(hwnd, detachParentTargets, "TabDetachSelectedFormat")
                        else
                            TabNameHelper.format (Localization.getString("TabDetachThisFormat")) (TabNameHelper.truncate (this.ts.tabInfo(Tab(hwnd)).text))
                    Some(CmiPopUp({
                        text = detachParentText
                        image = None
                        items = List2(subMenuItems)
                        flags = List2()
                    }))
            )
            Some(CmiSeparator)
            Some(CmiPopUp({
                text = Localization.getString("CloseTabMenu")
                image = None
                items = List2([
                    closeTabItem
                    CmiSeparator
                    closeLeftTabsItem
                    closeRightTabsItem
                    CmiSeparator
                    closeOtherTabsItem
                    if showEdgeActions then closeEdgeTabsItem
                    closeAllTabsItem
                ])
                flags = List2()
            }))
            Some(CmiSeparator)
            Some(CmiPopUp({
                // Both of these are about how the window behaves rather than
                // how the tabs look, which is what separates them from the
                // rest of the menu. Each one starts from the dialog's value
                // and is overridden here for this group alone.
                text = Localization.getString("WindowBehaviorMenu")
                image = None
                items = List2([
                    CmiRegular({
                        text = Localization.getString("LockWindowPositionMenu")
                        image = None
                        flags = if group.lockWindowPosition then List2([MenuFlags.MF_CHECKED]) else List2()
                        click = fun() ->
                            group.lockWindowPosition <- not group.lockWindowPosition
                    })
                    CmiRegular({
                        text = Localization.getString("SnapTabMarginTop")
                        image = None
                        flags = if group.snapTabHeightMargin then List2([MenuFlags.MF_CHECKED]) else List2()
                        click = fun() ->
                            group.snapTabHeightMargin <- not group.snapTabHeightMargin
                    })
                ])
                flags = List2()
            }))
            Some(tabPositionSubMenu)
            if TabEdges.isSingleEdge this.ts.allowedEdges then None else Some(tabDirectionSubMenu)
            Some(tabPinSubMenu)
            Some(tabColorSubMenu)
            Some(tabNameSubMenu)
            Some(CmiSeparator)
            Some(systemSubMenu)
            Some(managerItem)
        ]).choose(id)

    member private this.initAutoHide() =
        let callbackRef = ref None
        // Create a cell that tracks whether tabs are shown inside
        let isWindowInside = Cell.create(false)
        let isMouseOver = Cell.import(group.isMouseOver)
        let allowedEdges = Cell.import(this.ts.allowedEdgesOutput)
        let hoveredEdge = Cell.import(this.ts.hoveredEdge)
        let insideHideEdges = Cell.import(this.ts.insideHideEdges)
        let propCell(key,def) =
            let cell = Cell.create(group.bb.read(key, def))
            let update() = cell.value <- group.bb.read(key, def)
            group.bb.subscribe key update
            cell
        let autoHideCell = propCell("autoHide", false)
        let autoHideMaximizedCell = propCell("autoHideMaximized", false)
        let autoHideDoubleClickCell = propCell("autoHideDoubleClick", false)
        let contextMenuVisibleCell = propCell("contextMenuVisible", false)
        let renamingTabCell = propCell("renamingTab", false)
        // Create a cell that tracks the hide delay setting
        let hideDelayCell = Cell.create(
            try
                Services.settings.getValue("hideTabsDelayMilliseconds") :?> int
            with
            | _ -> 3000
        )
        let recentEdges = Cell.create(Set.empty<TabEdges.Edge>)
        let recentCallbacks = System.Collections.Generic.Dictionary<TabEdges.Edge, IDisposable>()
        group.zorder.changed.Add <| fun () ->
            for callback in recentCallbacks.Values do callback.Dispose()
            recentCallbacks.Clear()
            let edges = TabEdgeSettings.recentHideEdges allowedEdges.value edgeHideSettings.value
            recentEdges.value <- Set.ofList edges
            for edge in edges do
                let delay = snd edgeHideSettings.value.[edge]
                recentCallbacks.[edge] <- ThreadHelper.cancelablePostBack (TabBehaviorPolicy.hideTimerInterval delay) (fun () ->
                    recentCallbacks.Remove edge |> ignore
                    recentEdges.value <- Set.remove edge recentEdges.value)
        group.exited.Add <| fun () ->
            for callback in recentCallbacks.Values do callback.Dispose()
            recentCallbacks.Clear()
        let isRecentlyChangedZorderCell =
            let cell = Cell.create(false)
            let cbRef = ref None
            group.zorder.changed.Add <| fun() ->
                cell.value <- true
                cbRef.Value.iter <| fun(d:IDisposable) -> d.Dispose()
                cbRef := Some(ThreadHelper.cancelablePostBack (TabBehaviorPolicy.hideTimerInterval hideDelayCell.value) <| fun() ->
                    cell.value <- false
                )
            cell
        // Each edge owns its pending delay. Changes on other edges must not
        // restart it. Global interaction guards retain their existing scope.
        let edgeCallbacks = System.Collections.Generic.Dictionary<TabEdges.Edge, int * IDisposable>()
        let cancelEdge edge =
            match edgeCallbacks.TryGetValue edge with
            | true, (_, callback) ->
                callback.Dispose()
                edgeCallbacks.Remove edge |> ignore
            | _ -> ()
        group.exited.Add <| fun() ->
            for edge in TabEdges.priority do cancelEdge edge
        let updateEdges() =
            for edge in TabEdges.priority do
                let eligible = Set.contains edge insideHideEdges.value
                let over = hoveredEdge.value = Some edge
                let blocked = isDraggingCell.value || contextMenuVisibleCell.value || renamingTabCell.value
                let recent = Set.contains edge recentEdges.value
                let mode, delay = edgeHideSettings.value.[edge]
                let shrunk = this.ts.isShrunkOn edge
                if not eligible then
                    cancelEdge edge
                    edgeIconProtection.Remove edge |> ignore
                    this.ts.setShrunkOn(edge, false)
                elif mode = "doubleclick" then
                    cancelEdge edge
                    let protectedNow =
                        match edgeIconProtection.TryGetValue edge with
                        | true, until -> System.DateTime.Now <= until
                        | _ -> false
                    if shrunk && over && not isDraggingCell.value && not protectedNow then
                        edgeIconProtection.Remove edge |> ignore
                        this.ts.setShrunkOn(edge, false)
                else
                    edgeIconProtection.Remove edge |> ignore
                    let shrink = (mode = "down")
                                 && not over && not blocked && not recent
                    if not shrink then
                        cancelEdge edge
                        this.ts.setShrunkOn(edge, false)
                    elif not shrunk then
                        let pendingDelay =
                            match edgeCallbacks.TryGetValue edge with
                            | true, (value, _) -> Some value
                            | _ -> None
                        if TabBehaviorPolicy.restartHideDelay shrink shrunk delay pendingDelay then
                            cancelEdge edge
                            let callback = ThreadHelper.cancelablePostBack (TabBehaviorPolicy.hideTimerInterval delay) <| fun() ->
                                edgeCallbacks.Remove edge |> ignore
                                if not (TabEdges.isTopOnly allowedEdges.value)
                                   && Set.contains edge insideHideEdges.value
                                   && hoveredEdge.value <> Some edge
                                   && fst edgeHideSettings.value.[edge] = "down"
                                   && not (isDraggingCell.value || contextMenuVisibleCell.value || renamingTabCell.value
                                           || Set.contains edge recentEdges.value) then
                                    this.ts.setShrunkOn(edge, true)
                            edgeCallbacks.[edge] <- delay, callback
        // Create a function to handle the auto-hide logic
        let updateAutoHide() =
            if not (TabEdges.isTopOnly allowedEdges.value) then
                callbackRef.Value.iter <| fun(d:IDisposable) -> d.Dispose()
                callbackRef := None
                if this.ts.isShrunk then this.ts.isShrunk <- false
                updateEdges()
            else
                for edge in TabEdges.priority do
                    cancelEdge edge
                    this.ts.setShrunkOn(edge, false)
                edgeIconProtection.Clear()
                // Update isWindowInside based on current tab position: any strip
                // inside the window, on any edge ("hide inside tabs", 5-3) - the
                // top strip's direction alone while the top edge is the only one.
                isWindowInside.value <- this.ts.anyInside

                // Handle icon-click-to-hide mode separately
                if autoHideDoubleClickCell.value then

                    // Check if protection period has expired
                    let protectionExpired = System.DateTime.Now > !iconClickProtectUntil

                    // In icon-click mode, only show tabs when mouse is over and hidden
                    if this.ts.isShrunk && isMouseOver.value && not isDraggingCell.value then
                        // Check if we should show tabs (protection period expired OR mouse left and returned)
                        if not !hiddenByIconClick || protectionExpired then
                            if protectionExpired && !hiddenByIconClick then
                                hiddenByIconClick := false
                            if not !hiddenByIconClick then
                                this.ts.isShrunk <- false
                    // Clear the flag when mouse leaves
                    elif not isMouseOver.value then
                        if !hiddenByIconClick && protectionExpired then
                            hiddenByIconClick := false
                else
                    // Normal auto-hide logic for other modes
                    let shrink =
                        ((isWindowInside.value && autoHideCell.value) ||
                         (group.isMaximized.value && autoHideMaximizedCell.value)) &&
                        isMouseOver.value.not &&
                        isDraggingCell.value.not &&
                        contextMenuVisibleCell.value.not &&
                        renamingTabCell.value.not &&
                        isRecentlyChangedZorderCell.value.not
                    callbackRef.Value.iter <| fun(d:IDisposable) -> d.Dispose()
                    callbackRef := None
                    if shrink then
                        callbackRef := Some(ThreadHelper.cancelablePostBack (TabBehaviorPolicy.hideTimerInterval hideDelayCell.value) <| fun() ->
                            this.ts.isShrunk <- true
                        )
                    else
                        this.ts.isShrunk <- false
        
        // Listen for changes to trigger auto-hide update
        Cell.listen updateAutoHide
        
        let refreshEdgeHide () =
            group.invokeAsync <| fun () -> edgeHideSettings.value <- readHideSettings ()
        let hideSubscription = TabEdgeSettings.changed.Publish.Subscribe(fun () -> refreshEdgeHide ())
        group.exited.Add <| fun () -> hideSubscription.Dispose()
        // WindowGroup's existing mode notification always writes this key.
        // This group-owned subscription is released on exit.
        let modeSubscription = group.bb.disposableSubscribe "autoHideDoubleClick" refreshEdgeHide
        group.exited.Add <| fun () -> modeSubscription.Dispose()

        // When hideDelayCell value changes via notifyValue, restart timer if running
        Services.settings.notifyValue "hideTabsDelayMilliseconds" <| fun value ->
            group.invokeAsync <| fun() ->
                edgeHideSettings.value <- readHideSettings ()
                hideDelayCell.value <-
                    try
                        value :?> int
                    with
                    | _ -> 3000
                // If a timer is running, restart it with the new delay
                if callbackRef.Value.IsSome then
                    updateAutoHide()

    // A dragged tab enters the strip of `edge` (the top strip's through this
    // decorator, the others' through edgeDropTarget), `pt` along the strip.
    member private this.dragEnterOn(edge: TabEdges.Edge, data: obj, pt: Pt) =
        let accepted = this.invokeSync <| fun() ->
            let dragInfo = data :?> TabDragInfo
            let (Tab(hwnd)) = dragInfo.tab
#if DEBUG
            DragTrace.log (fun () -> sprintf "[dockDrag] enter edge=%A continuing=%b" edge dragInfoCell.value.IsSome)
            group.traceDockDrag("enter-before", edge, hwnd)
#endif
            if dragInfoCell.value.IsSome && this.ts.tabs.contains(dragInfo.tab) then
                // From another strip of this group: the tab - with the block
                // dragged along with it - changes edge and stays in the
                // group. Its place and alignment on the new strip are decided
                // by the slide, as on any strip, and only when it is dropped.
                let moving = dragInfo.tab :: (this.ts.dragGroup |> List.filter ((<>) dragInfo.tab))
                this.ts.setTabsEdge(moving, edge)
                dragPtCell.set(pt)
                this.updateTsSlide()
                // The top strip may just have emptied or filled: its corner
                // and the top edge guard follow.
                this.updateTsPlacement()
#if DEBUG
                group.traceDockDrag("enter-after", edge, hwnd)
#endif
                true
            else
                let returning = not (this.ts.tabs.contains dragInfo.tab)
                let accepted = this.enterFromOutside(edge, dragInfo, pt)
                if accepted then
                    match ownDragBlock with
                    | Some block ->
                        if returning then restoreOwnRemainder()
                        let moving = TabEdges.ownWindowBlock block this.ts.tabs.contains
                        this.ts.dragGroup <- []
                        if moving.Length > 1 then this.ts.prepareEdgeDragBlock(moving)
                        this.ts.setTabsEdge(moving, edge)
                        this.updateTsSlide()
                        this.updateTsPlacement()
                    | None -> ()
#if DEBUG
                group.traceDockDrag("enter-after", edge, hwnd)
#endif
                accepted
        // The entry batch has now rendered the arriving tabs and all strips.
        // Repair against their final native windows, only for a foreign visit.
        if accepted then
            this.invokeSync (fun () ->
                if foreignDragFront.IsSome then group.repairStripZorder("group-preview-presented"))
        accepted

    // A dragged tab entering this group's strip from anywhere else - or the
    // drag starting here. What dragEnter always did, and then the arriving
    // tabs are put on the edge of the strip they arrive on.
#if DEBUG
    member private this.traceDragGroups(phase: string, info: TabDragInfo) =
        DragTrace.log (fun () ->
            let source = lock decorators (fun () ->
                match decorators.TryGetValue info.sourceGroupHwnd with
                | true, value -> Some value
                | _ -> None)
            let order = Win32Helper.GetWindowsInZOrder() |> Array.toList
            let front members = order |> List.tryFind (fun h -> List.contains h members) |> Option.defaultValue IntPtr.Zero
            let sourceFront = source |> Option.map (fun d -> front d.group.windowsThreadSafe.items.list) |> Option.defaultValue IntPtr.Zero
            let targetFront = front group.windows.items.list
            let above upper lower =
                match List.tryFindIndex ((=) upper) order, List.tryFindIndex ((=) lower) order with
                | Some a, Some b -> a < b
                | _ -> false
            let sourceStrips = source |> Option.map (fun d -> d.ts.hwnd :: d.ts.edgeHwndsThreadSafe) |> Option.defaultValue []
            let targetStrips = this.ts.hwnd :: this.ts.edgeHwnds
            let (Tab pivot) = info.tab
            sprintf "[dockDrag] groups phase=%s source=%X target=%X drag=%X sourceFront=%X targetFront=%X targetAboveSource=%b sourceStripsAboveTarget=%A targetStripsAboveSource=%A"
                phase (info.sourceGroupHwnd.ToInt64()) (this.ts.hwnd.ToInt64()) (pivot.ToInt64())
                (sourceFront.ToInt64()) (targetFront.ToInt64()) (above targetFront sourceFront)
                (sourceStrips |> List.map (fun h -> h, above h targetFront))
                (targetStrips |> List.map (fun h -> h, above h sourceFront)))
#endif

    member private this.enterFromOutside(edge: TabEdges.Edge, dragInfo: TabDragInfo, pt: Pt) =
            let (Tab(hwnd)) = dragInfo.tab
            if dragInfo.sourceGroupHwnd <> this.ts.hwnd && not (List.isEmpty dragInfo.transferOrder) && foreignDragFront.IsNone then
                foreignDragFront <- group.captureDragGroupFront(hwnd :: dragInfo.selectedHwnds)
                pendingHoverRestore <- foreignDragFront |> Option.map (fun (front, predecessors) ->
                    fun () -> this.invokeSync (fun () -> group.restoreDragGroupFront(front, predecessors)))
            // Which of the tabs arrive in this group now, as opposed to being
            // in it already (the drag starting here): only those are put on
            // the strip they arrive on.
            let arriving =
                (if this.ts.tabs.contains(dragInfo.tab) then [] else [ dragInfo.tab ])
                @ (dragInfo.selectedHwnds |> List.filter (fun h -> not (group.windows.contains h)) |> List.map Tab)
            // No special case for a single-tab group: it used to return
            // false here (= reject the strip as a drop target), which sent
            // the drag straight into floating-window mode. Sliding the
            // lone tab inside the strip is still useful — dropping it on
            // the other half of the strip switches its left/right
            // alignment — and dragging beyond the strip bounds still
            // exits into floating-window mode as before.
            dragPtCell.set(pt)
            dragInfoCell.set(Some(dragInfo))
            let alignment =
                dragInfo.sourceTabAligns |> List.tryHead
                |> Option.defaultWith (fun () -> this.ts.getTabAlign(dragInfo.tab))
            this.ts.addTabWithState(dragInfo.tab, alignment,
                Services.program.isWindowPinned(hwnd), None, edge=edge,
                ?alignmentEdge=(dragInfo.sourceTabEdges |> Map.tryFind hwnd))
            this.updateTsSlide()
            this.ts.setTabInfo(dragInfo.tab, dragInfo.tabInfo)
            if TabEdges.isTopOnly this.ts.allowedEdges then group.addWindow(hwnd, false)
            else group.addWindow(hwnd, false, edge=edge)

            // Multi-select continuation: the source's dragExit
            // already removed the selected tabs and hid them
            // off-screen, so we just need to add + show them in
            // this group. This single branch handles both
            // drag-link (target group is different from source)
            // and drag-cancel (target group IS the source).
            if not (List.isEmpty dragInfo.selectedHwnds) then
                // Drop the target's pre-existing multi-select —
                // the dragged set wins.
                group.clearSelected()
                Services.program.suspendTabMonitoring()
                try
                    for i, selHwnd in List.indexed dragInfo.selectedHwnds do
                        if not (group.windows.contains selHwnd) then
                            let sourceEdge = dragInfo.sourceTabEdges |> Map.tryFind selHwnd
                            if sourceEdge = Some edge then
                                Services.program.setWindowAlignmentEdge(selHwnd, edge)
                                group.addWindow(selHwnd, false, ?alignment=(dragInfo.sourceTabAligns |> List.tryItem (i + 1)), edge=edge)
                            else
                                let alignment = dragInfo.sourceTabAligns |> List.tryItem (i + 1) |> Option.defaultValue (this.ts.getTabAlign(Tab selHwnd))
                                this.ts.addTabWithState(Tab selHwnd, alignment, Services.program.isWindowPinned(selHwnd), None,
                                                       edge=edge, ?alignmentEdge=sourceEdge)
                                group.addWindow(selHwnd, false, edge=edge)
                        let selWindow = os.windowFromHwnd(selHwnd)
                        selWindow.showWindow(if List.isEmpty dragInfo.transferOrder then ShowWindowCommands.SW_SHOW else ShowWindowCommands.SW_SHOWNOACTIVATE)
                finally
                    Services.program.resumeTabMonitoring()
                // Re-establish the active + selected state at the
                // destination: dragged is the active, prior
                // selection becomes the new selected set.
                if List.isEmpty dragInfo.transferOrder then group.tabActivate(dragInfo.tab, false, drag=true)
                for selHwnd in dragInfo.selectedHwnds do
                    group.setSelected(selHwnd, true)

            // A tab arriving on a strip is on that strip's edge (1-4-2), not
            // on the edge a joining window takes. Nothing to do while the top
            // edge is the only one.
            if not (TabEdges.isTopOnly this.ts.allowedEdges) && not (List.isEmpty arriving) then
                this.ts.setTabsEdge(arriving, edge)
                // Several tabs arriving from another group slide along the
                // strip and land together, at the point they are dropped at,
                // as a block dragged within its own group does. (The group
                // the drag started in has its block from dragBegin.) With the
                // top edge alone, today's drop, which places the dragged tab
                // only, is left as it is.
                if (!mouseDownTab).IsNone && arriving.Length > 1 then
                    this.ts.prepareEdgeDragBlock(dragInfo.transferOrder |> List.filter (fun t -> List.contains t arriving))
                this.updateTsPlacement()

            this.updateTsSlide()
            foreignDragFront |> Option.iter (fun (front, _) ->
#if DEBUG
                this.traceDragGroups("preview-before", dragInfo)
#endif
                group.previewDragGroupFront(hwnd, front)
#if DEBUG
                this.traceDragGroups("preview-after", dragInfo)
#endif
                )
            true

    // A drop on the strip of an edge other than the top: that strip's
    // position and smart pin (TabStrip.dropOnEdge). None when the tab is
    // not being slid along such a strip - always, while the top edge is the
    // only one - and the top strip's drop runs as it always has.
    member private this.dropOnSideStrip() : bool option =
        match this.ts.slideEdge with
        | Some(edge) when edge <> TabEdges.Edge.Top ->
            match this.ts.dropOnEdge(edge) with
            | Some(moved, newAlignment) ->
                for (Tab h) in moved do
                    Services.program.setWindowAlignment(h, Some(newAlignment))
                Some(true)
            | None -> Some(false)
        | _ -> None

    member private this.dragMoveOn(pt: Pt) = PerfTrace.time "drag.move" <| fun () ->
        this.invokeSync <| fun() ->
            dragPtCell.set(pt)
            this.updateTsSlide()

    member private this.finishDragHover(commit) =
        match foreignDragFront with
        | Some(front, _) ->
            if not commit then group.keepDragGroupFront(front)
#if DEBUG
            DragTrace.log (fun () ->
                sprintf "[dockDrag] group-%s group=%X front=%X"
                    (if commit then "commit" else "keep") (this.ts.hwnd.ToInt64()) (front.ToInt64()))
#endif
        // Source hover raises only the dragged block. Its original front is
        // restored by the source session on cancellation, never on edge exit.
        | None -> ()
        foreignDragFront <- None

    member private this.dragExitOn() =
        this.invokeSync <| fun() ->
#if DEBUG
            DragTrace.log (fun () -> sprintf "[dockDrag] exit edge=%A" this.ts.slideEdge)
#endif
            let leavingFront =
                dragInfoCell.value |> Option.bind (fun info ->
                    let (Tab pivot) = info.tab
                    group.tryTopWindow |> Option.filter (fun h -> h = pivot || List.contains h info.selectedHwnds))
            match dragInfoCell.value with
            | Some(dragInfo) ->
                let tab = dragInfo.tab
                if this.ts.tabs.contains(tab) then
                    this.ts.removeTab(tab)
                    dragInfoCell.set(None)
                    let (Tab(hwnd)) = tab
                    // The departing body must not activate a sibling before
                    // the source remainder is positioned with its bands.
                    group.removeWindow(hwnd, activate=false)
                    dragInfo.parkWindow(hwnd)

                    // Multi-select: also detach the selected tabs from
                    // this group so the source visibly reflects the loss
                    // (= [A,B,C] when dragging E with D selected from
                    // [A,B,C,D,E]). The dragEnter on the destination
                    // (drag-link or drag-cancel) or Desktop.dragDrop
                    // (drag-detach) will add them back.
                    for selHwnd in dragInfo.selectedHwnds do
                        let selTab = Tab(selHwnd)
                        if this.ts.tabs.contains(selTab) then
                            this.ts.removeTab(selTab)
                        if group.windows.contains selHwnd then
                            group.removeWindow(selHwnd, activate=false)
                        dragInfo.parkWindow(selHwnd)
                    // Retain G's previous Z slot without stealing focus or
                    // raising it over applications that were already above it.
                    if ownDragBlock.IsSome then
                        leavingFront |> Option.iter (fun departed ->
                            group.tryTopWindow |> Option.iter (fun front ->
                                group.raiseDragWindow(front, departed, reason="source-remainder")))
            | None -> ()
            // Detach the carried block first, then bind the bands to B again.
            // The drag owner retains the rollback; leaving is not cancellation.
            this.finishDragHover(false)
            this.updateTsSlide()

    interface IDragHoverGroup with
        member x.ownsHoverWindow hwnd = group.windowsThreadSafe.contains hwnd
        member x.bodyDragTarget(pt, current) = this.invokeSync <| fun () ->
            group.tryTopWindow |> Option.bind (fun hwnd ->
                let bounds = os.windowFromHwnd(hwnd).bounds
                TabEdges.ownWindowEdgeAt (Dpi.scaleForPoint pt) this.ts.allowedEdges
                    (bounds.x, bounds.y, bounds.width, bounds.height) (pt.x, pt.y)
                    (current |> Option.bind this.ts.edgeOfHwnd)
                |> Option.map this.ts.hwndOf)
        member x.hoverFront(data, valid) = this.invokeAsync <| fun () ->
            if valid() then
                match data with
                | Some(:? TabDragInfo as info) when this.ts.tabs.contains info.tab ->
                    let (Tab hwnd) = info.tab
                    // Placement and DPI compensation have already settled.
                    // Hover changes only Z order, with bands and bodies together.
                    group.raiseDragWindow(hwnd, selected=info.selectedHwnds)
                | None -> ()
                | _ -> ()
        member x.takeHoverRestore() = this.invokeSync <| fun () ->
            let restore = pendingHoverRestore
            pendingHoverRestore <- None
            restore
        member x.endHover commit = this.invokeAsync <| fun () ->
            // Cancellation rollback has already completed on the drag thread.
            foreignDragFront <- None
            pendingHoverRestore <- None
#if DEBUG
            if commit then DragTrace.log (fun () -> sprintf "[dockDrag] group-commit group=%X" (this.ts.hwnd.ToInt64()))
#endif
        member x.completeDrop data = this.invokeAsync <| fun () ->
            match data with
            | :? TabDragInfo as info when this.ts.tabs.contains info.tab ->
                TabEdges.completeDragDrop
                    (fun tab -> group.tabActivate(tab, false, drag=true))
                    (fun hwnd -> if group.windows.contains hwnd then group.setSelected(hwnd, true))
                    info.tab info.selectedHwnds
#if DEBUG
                let (Tab hwnd) = info.tab
                group.traceDockDrag("drop-complete", this.ts.edgeOf info.tab, hwnd)
                this.traceDragGroups("drop-after", info)
#endif
            | _ -> ()

    interface IOwnWindowDragSource with
        member this.beginOwnWindowDrag(data, registered) = this.invokeSync <| fun () ->
            if TabEdges.isSingleEdge this.ts.allowedEdges || not (data :? TabDragInfo) || group.tryTopWindow.IsNone then None
            else
                let dragInfo = data :?> TabDragInfo
                let originalFront = group.topWindow
                let mutable homeBounds = os.windowFromHwnd(originalFront).bounds
                let originalOrder = this.ts.visualOrder.list
                let placements =
                    (dragInfo.tab :: (dragInfo.selectedHwnds |> List.map Tab))
                    |> List.distinct
                    |> List.map (fun (Tab h) ->
                        let window = os.windowFromHwnd(h)
                        h, window.pid.pid, window.tid, window.placement, window.bounds)
                let mutable returned = false
                let mutable leftSource = false
                let restoreSourceWindows() =
                    returned <- true
                    // Restore only parked geometry during capture. In particular,
                    // never activate a maximized app while moving across the band.
                    dragInfo.restoreParked()
                let completeSourceWindows() =
                    if returned && leftSource then
                        // Queued behind dragEnd, after capture release. A normal
                        // window needs no placement write; maximize only windows
                        // that actually returned to the source, not foreign drops.
                        this.invokeAsync (fun () ->
                            for h, pid, tid, placement, bounds in placements do
                                let window = os.windowFromHwnd(h)
                                if group.windows.contains h && window.isWindow &&
                                   window.pid.pid = pid && window.tid = tid && not window.isMinimized && not window.isMaximized &&
                                   placement.showCmd = ShowWindowCommands.SW_SHOWMAXIMIZED then
                                    // Reuse the native no-activation path. It also
                                    // establishes the saved normal restore rectangle.
                                    Win32Helper.PlaceWorkspaceWindowNoActivate(h, placement.rcNormalPosition.RECT, true) |> ignore)
                let originalState = originalOrder |> List.map (fun tab ->
                    tab, this.ts.edgeOf tab, this.ts.explicitTabAlignThreadSafe tab, this.ts.isPinned tab,
                    Services.program.getWindowAlignmentEdge(let (Tab h) = tab in h))
                let block = this.contiguousSelectedFromTab(dragInfo.tab)
                ownDragBlock <- Some block
                if block.Length > 1 then this.ts.prepareEdgeDragBlock(block)
                let restoreState predicate =
                    this.ts.batch (fun () ->
                        for (Tab h as tab), edge, alignment, pinned, alignmentEdge in originalState do
                            if predicate tab && this.ts.tabs.contains tab then
                                let changed = this.ts.edgeOf tab <> edge || this.ts.explicitTabAlignThreadSafe tab <> alignment
                                if this.ts.edgeOf tab <> edge then this.ts.setTabEdge(tab, edge)
                                if this.ts.isPinned tab <> pinned then
                                    if pinned then this.ts.pinTab(tab) else this.ts.unpinTab(tab)
                                    Services.program.setWindowPinned(h, pinned)
                                if changed then
                                    match alignment with
                                    | Some value -> this.ts.setTabAlign(tab, value)
                                    | None -> this.ts.clearTabAlign(tab)
                                    Services.program.setWindowAlignment(h, alignment)
                                    alignmentEdge |> Option.iter (fun e -> Services.program.setWindowAlignmentEdge(h, e))
                        let present = this.ts.visualOrder.list
                        let order = TabEdges.ownWindowRestoreOrder originalOrder predicate present
                        if present <> order then this.ts.setVisualOrder(order))
                // Detach still transfers all selected tabs. On return, the
                // extra selections resume their old zones; only the original
                // contiguous block follows the pointer.
                restoreOwnRemainder <- fun () ->
                    restoreState (fun (Tab h as tab) -> List.contains h dragInfo.selectedHwnds && not (List.contains tab block))
                // A hidden zero-sized native sibling keeps the home's Z slot
                // independently of parked windows visiting another group.
                // It never paints, receives input, or activates an application.
                let mutable anchor : IWindow option = None
                let mutable lastCoveredHit : (IntPtr * bool * int64) option = None
                let refreshHit() = lastCoveredHit <- None
                let leaving() =
                    leftSource <- true
                    // Snapshot geometry and Z position together on EVERY exit.
                    // Membership may have changed since the drag began.
                    let (Tab pivot) = dragInfo.tab
                    let canEmpty = group.windows.items.list |> List.forall (fun h -> h = pivot || List.contains h dragInfo.selectedHwnds)
                    match group.tryTopWindow with
                    | Some front when canEmpty ->
                        homeBounds <- os.windowFromHwnd(front).bounds
                        if anchor.IsNone then
                            anchor <- Some(os.createWindow (fun msg -> msg.def()) WindowsStyles.WS_POPUP
                                        (WindowsExtendedStyles.WS_EX_TOOLWINDOW ||| WindowsExtendedStyles.WS_EX_NOACTIVATE))
                        anchor |> Option.iter (fun window -> os.windowFromHwnd(window.hwnd).insertAfter(front))
                        refreshHit()
                    | _ -> ()
                let uncovered visible =
                    let now = System.Diagnostics.Stopwatch.GetTimestamp()
                    match lastCoveredHit with
                    | Some(h, result, at) when h = visible && now - at < System.Diagnostics.Stopwatch.Frequency / 10L -> result
                    | _ ->
                        let result = anchor |> Option.exists (fun window ->
                            TabEdges.ownWindowHomeUncovered TopEdgeGuardPlacement.above window.hwnd IntPtr.Zero visible)
                        lastCoveredHit <- Some(visible, result, now)
                        result
                Some { new IOwnWindowDragSession with
                    member x.targetAt(pt, visible, current) = this.invokeSync <| fun () ->
                        let realStrip edge =
                            let hwnd = this.ts.hwndOf edge
                            if this.ts.edgeOfHwnd hwnd = Some edge && registered hwnd then Some hwnd else None
                        let allowed = TabEdges.edgesOf this.ts.allowedEdges |> List.filter (realStrip >> Option.isSome) |> TabEdges.allowedOf
                        let guard = TopEdgeGuardPlacement.isGuard visible
                        let visible = if guard then TopEdgeGuardPlacement.ownerOf visible else visible
                        let ownGuard = guard && (this.ts.ownsHwnd visible || group.windows.contains visible)
                        let front = group.tryTopWindow
                        let bounds =
                            match front with
                            | Some h ->
                                let bounds = os.windowFromHwnd(h).bounds
                                if bounds <> homeBounds then homeBounds <- bounds
                                // The home follows a live source, but freezes
                                // while empty, including visits to other groups.
                                bounds
                            | None -> homeBounds
                        let currentEdge = current |> Option.bind this.ts.edgeOfHwnd
                        let rect = bounds.x, bounds.y, bounds.width, bounds.height
                        let scale = Dpi.scaleForPoint pt
                        let bodyEdge =
                            allowed |> Option.bind (fun edges ->
                                TabEdges.ownWindowEdgeAt scale edges rect (pt.x, pt.y) currentEdge)
                            |> Option.filter (realStrip >> Option.isSome)
                        match this.ts.edgeOfHwnd visible with
                        | Some edge when List.contains edge (TabEdges.edgesOf this.ts.allowedEdges) -> realStrip edge
                        | _ ->
                            let visibleBody =
                                match front with
                                | Some _ -> group.windows.contains visible || this.ts.ownsHwnd visible
                                | None -> bodyEdge.IsSome && uncovered visible
                            let nearCurrent() =
                                current |> Option.filter (fun hwnd ->
                                    let r = os.windowFromHwnd(hwnd).bounds
                                    TabEdges.ownWindowStripContains false
                                        (currentEdge |> Option.exists TabEdges.runsHorizontally) (Dpi.px (Dpi.scaleForRect r) 20)
                                        (r.x, r.y, r.width, r.height) (pt.x, pt.y))
                            if ownGuard then
                                let px = max bounds.x (min (bounds.right - 1) pt.x)
                                let py = max bounds.y (min (bounds.bottom - 1) pt.y)
                                allowed |> Option.bind (fun edges ->
                                    TabEdges.ownWindowEdgeAt scale edges rect (px, py) currentEdge)
                                |> Option.bind realStrip
                            elif visibleBody && bounds.containsPoint pt then
                                nearCurrent() |> Option.orElseWith (fun () -> bodyEdge |> Option.bind realStrip)
                            elif (visible = IntPtr.Zero ||
                                  List.contains (os.windowFromHwnd(visible).className) ["Progman"; "WorkerW"] ||
                                  (Mon.fromPoint(pt) |> Option.exists (fun mon -> not (mon.workRect.containsPoint pt)))) then
                                // Keep the legacy screen-edge/taskbar tolerance.
                                // Visible registered strips were already prioritized.
                                current |> Option.filter (fun hwnd ->
                                    let r = os.windowFromHwnd(hwnd).bounds
                                    let gap = Dpi.px (Dpi.scaleForRect r) 20
                                    let receiver = false
                                    let horizontal = currentEdge |> Option.exists TabEdges.runsHorizontally
                                    TabEdges.ownWindowStripContains receiver horizontal gap
                                        (r.x, r.y, r.width, r.height) (pt.x, pt.y))
                            else None
                    member x.leaving() = this.invokeSync leaving
                    member x.refreshHit() = this.invokeSync refreshHit
                    member x.complete() = completeSourceWindows()
                    member x.prepareReturn() = this.invokeSync restoreSourceWindows
                    member x.cancel() = this.invokeSync <| fun () ->
                        restoreSourceWindows()
                        if not (this.ts.tabs.contains dragInfo.tab) then
                            this.enterFromOutside(dragInfo.sourceTabEdges.[let (Tab h) = dragInfo.tab in h], dragInfo, Pt()) |> ignore
                        this.cleanupEdgeDrag()
                        restoreState (fun _ -> true)
                        // Roll back G's front even after a body hover while its
                        // original front was parked. Global application Z order
                        // and keyboard focus are not a desktop-wide snapshot.
                        group.raiseDragWindow(originalFront, reason="drag-cancel")
                        this.updateTsPlacement()
                  interface IDisposable with
                    member x.Dispose() = this.invokeSync (fun () ->
                        ownDragBlock <- None
                        restoreOwnRemainder <- ignore
                        anchor |> Option.iter (fun window -> (window :?> IDisposable).Dispose())) }


    interface IGroupDragTarget with
        member x.group = box this
        member x.runsAcross = true

    interface ITabStripMonitor with
        member x.tabClick((btn, tab, part, action, pt)) =
            let (Tab(hwnd)) = tab
            mouseEvent.Trigger(hwnd, btn, part, action, pt)
            let ptScreen = this.ts.screenPointOf(tab, pt)
            match action with
            | MouseDown ->
                group.flashTab(tab, false)
                match btn with
                | MouseRight ->
                    // A right-click only opens the menu. Bringing the group
                    // forward changed the foreground window, which every group
                    // reacts to, and the menu waited behind all of that.
                    ()
                | MouseLeft ->
                    // Read modifier keys at click time for multi-tab selection.
                    //   plain   : Explorer-like behavior (see below)
                    //   Shift   : select range from active to clicked
                    //                    (selection is updated; active does NOT move)
                    //   Ctrl    : toggle selection on the clicked inactive tab
                    //                    (active does NOT move; clicking active is a no-op)
                    let mods = System.Windows.Forms.Control.ModifierKeys
                    let shiftHeld = (mods &&& System.Windows.Forms.Keys.Shift) = System.Windows.Forms.Keys.Shift
                    let ctrlHeld = (mods &&& System.Windows.Forms.Keys.Control) = System.Windows.Forms.Keys.Control
                    if shiftHeld && part <> TabClose && part <> TabPin then
                        if this.ts.edgeOf tab <> this.ts.edgeOf (Tab(group.topWindow)) then
                            group.clearSelected()
                            group.tabActivate(tab, false)
                        else
                            group.selectRange(hwnd)
                    elif ctrlHeld && part <> TabClose && part <> TabPin then
                        // Ctrl+click on the active tab is a no-op per spec.
                        if hwnd <> group.topWindow then
                            group.toggleSelected(hwnd)
                    else
                        // Plain left click — Explorer-like selection model:
                        //   click on UNSELECTED tab : clear selection, then activate
                        //                             (MouseUp does nothing extra)
                        //   click on SELECTED tab   : keep the full selection set
                        //                             ({active} ∪ selected) unchanged —
                        //                             active moves to the clicked tab and
                        //                             the previously-active tab joins the
                        //                             selected set
                        //   click on SELECTED tab + drag : keep selection through drag
                        //                                  (drag-detach carries the selection)
                        //   MouseUp on a "selected click" without drag : reduce to single
                        // The active tab itself counts as "selected" since the
                        // selection set is the implicit set + the active tab.
                        let wasSelected = group.isSelected(hwnd) || hwnd = group.topWindow
                        mouseDownOnSelected := wasSelected
                        mouseDownTab := Some tab
                        if not wasSelected then
                            group.clearSelected()
                            group.tabActivate(tab, false)
                        else
                            // Activate first so prevActiveHwnd is no longer the
                            // implicit selection target, THEN add it to the
                            // selected set (setSelected is a no-op on the active
                            // tab). This preserves {active ∪ selected} across the
                            // activation swap.
                            let prevActiveHwnd = group.topWindow
                            group.tabActivate(tab, false)
                            if hwnd <> prevActiveHwnd && part <> TabClose && part <> TabPin then
                                group.setSelected(prevActiveHwnd, true)
                        if part <> TabClose && part <> TabPin then
                            let tabInfo = this.ts.tabInfo(tab)
                            let originalTabLocation = this.ts.tabLocation tab
                            let clickOffsetInTab = pt.sub(originalTabLocation)

                            // Calculate scaled offset for preview image (always needed when dragging to different location)
                            // Keep PrintWindow behind the drag threshold: both offset
                            // calculations use the same geometry-only width snapshot.
                            let previewSize = tabInfo.previewSize()
                            let previewWidth = previewSize.width
                            DragTrace.log (fun () -> sprintf "press.geometry: hwnd=%X previewWidth=%d" (hwnd.ToInt64()) previewWidth)
                            let imageOffset = this.ts.dragImageOffset(tab, previewSize, clickOffsetInTab)

                            // For tab reordering within same group, use unscaled click offset
                            let tabOffset = clickOffsetInTab

                            let dragImage = fun() -> this.ts.dragImage(tab)
                            // Multi-select drag continuation: snapshot the
                            // selection (excluding the dragged tab) and source
                            // group hwnd so the target group's dragEnter can
                            // carry the selected tabs across with the dragged
                            // tab. When the selection covers all of the source
                            // group's tabs, the source naturally empties and
                            // is auto-destroyed (= "group-move" with no
                            // separate detach pass needed).
                            let selectedSnapshot =
                                if TabEdges.isTopOnly this.ts.allowedEdges then
                                    group.selectedTabs.items.list |> List.filter (fun h -> h <> hwnd)
                                else
                                    this.ts.visualOrder.list
                                    |> List.choose (fun (Tab h) -> if h <> hwnd && group.selectedTabs.contains h then Some h else None)
                            let frameMargins =
                                (hwnd :: selectedSnapshot)
                                |> List.map (fun h -> h, group.capturePlacementMargin(h)) |> Map.ofList
                            let parking = DragWindowParking()
                            let dragInfo =
                                box({ tab = tab
                                      tabOffset = tabOffset
                                      imageOffset = imageOffset
                                      previewWindowOffset = this.ts.dragPreviewWindowOffset(tab, previewSize)
                                      tabInfo = tabInfo
                                      parkWindow = parking.park
                                      restoreParked = parking.restore
                                      retainSource = group.retainForTabDrag
                                      sourceGroupHwnd = group.hwnd
                                      selectedHwnds = selectedSnapshot
                                      sourceFrameMargins = frameMargins
                                      transferOrder =
                                          if TabEdges.isTopOnly this.ts.allowedEdges then []
                                          else this.ts.visualOrder.list |> List.filter (fun (Tab h) -> h = hwnd || List.contains h selectedSnapshot)
                                      sourceRestoreSize =
                                          let window = os.windowFromHwnd(hwnd)
                                          let restored = FramePlacement.restoredBounds window
                                          (FramePlacement.read hwnd frameMargins.[hwnd] restored).size
                                      sourceSnapTabHeightMargin = group.snapTabHeightMargin
                                      sourceLockWindowPosition = group.lockWindowPosition
                                      sourceTabAligns = (hwnd :: selectedSnapshot) |> List.map group.getTabAlign
                                      sourceTabEdges = (hwnd :: selectedSnapshot) |> List.map (fun h -> h, group.tabEdge h) |> Map.ofList })
                            // From the strip the tab is on.
                            Services.dragDrop.beginDrag(this.ts.hwndOf(this.ts.edgeOf tab), dragImage, imageOffset, ptScreen, dragInfo)
                | MouseMiddle ->
                    group.clearSelected()
                    group.tabActivate(tab, false)
            | MouseUp ->
                match btn with
                | MouseLeft ->
                    // Explorer-like: if MouseDown landed on an already-selected
                    // tab and no drag occurred, MouseUp reduces the selection
                    // to just that single tab (clears the multi-select).
                    let mods = System.Windows.Forms.Control.ModifierKeys
                    let shiftHeld = (mods &&& System.Windows.Forms.Keys.Shift) = System.Windows.Forms.Keys.Shift
                    let ctrlHeld = (mods &&& System.Windows.Forms.Keys.Control) = System.Windows.Forms.Keys.Control
                    if not shiftHeld && not ctrlHeld && not isDraggingCell.value then
                        if !mouseDownOnSelected then
                            group.clearSelected()
                    mouseDownOnSelected := false
                | _ -> ()
            | _ -> ()

        member x.tabActivate((tab)) =
            group.tabActivate(tab, false)

        member x.tabMoved(Tab(hwnd), index) =
            group.onTabMoved(hwnd, index)

        member x.tabPin(Tab(hwnd)) =
            group.unpinTab(hwnd)

        member x.tabClose(Tab(hwnd)) =
            // Get all tabs and current tab index before closing
            let currentTab = Tab(hwnd)
            let allTabs = this.ts.visualOrder
            let currentIndex = allTabs.tryFindIndex((=) currentTab)

            // Check if this is the active tab
            let isActiveTab = (group.topWindow = hwnd)

            // If closing the active tab and there are more tabs, activate the next one
            if isActiveTab && allTabs.count > 1 then
                currentIndex.iter <| fun index ->
                    // Determine which tab to activate
                    let nextTab =
                        if index < allTabs.count - 1 then
                            Some(allTabs.at(index + 1))  // Next tab
                        elif index > 0 then
                            Some(allTabs.at(index - 1))  // Previous tab
                        else
                            None

                    // Activate the next tab before closing
                    nextTab.iter <| fun tab ->
                        group.tabActivate(tab, true)

            // Close the window
            os.windowFromHwnd(hwnd).close()

        member x.windowMsg(msg) =
            if msg.msg = 0x0047 then group.repairStripZorder("strip-position")
            // Native layer changes can follow the group notification. Hide an
            // unsafe band synchronously, then repair after strip placement.
            let follows =
                msg.msg = 0x0047 && topEdgeGuard.followsStrip &&
                group.lockWindowPosition && group.bounds.value.IsSome &&
                not group.isInMoveSizeThreadSafe && not group.isFullscreen.value
            TopEdgeGuardPolicy.dispatchStripMessage msg.msg follows
                (fun () -> topEdgeGuard.stripChanged(msg.hwnd)) this.queueTopEdgeGuard
            // The strip window is per-monitor DPI aware, so Windows notifies it
            // whenever the scale of its monitor changes - the display setting
            // was changed while the app runs, or the strip crossed to a monitor
            // with a different scale. Recompute the placement: the bounds AND
            // the metrics derived from the same scale.
            //
            // Usually the tracked window moves too and group.bounds.changed has
            // already done this. This covers the case where the window
            // rectangle stays byte-for-byte identical - a maximized window
            // whose monitor's scale was changed - which no bounds event would
            // ever report.
            if msg.msg = WindowMessages.WM_DPICHANGED then
                this.invokeAsync <| fun() ->
                    this.updateTsPlacement()


    member private this.cleanupEdgeDrag() =
        TabEdges.cleanupDrag [
            (fun () -> this.ts.stopDragScroll(); this.ts.isReceiving <- false)
            (fun () -> isDraggingCell.value <- false)
            (fun () -> this.ts.transparent <- true)
            (fun () -> this.ts.dragGroup <- [])
            (fun () -> mouseDownTab := None; mouseDownOnSelected := false)
            (fun () -> dragInfoCell.set(None))
            (fun () -> this.ts.slide <- None)
            (fun () -> this.updateTsSlide())
            (fun () -> this.updateTsPlacement()) ]

    interface IDragDropTarget with
        member this.dragBegin() =
            let dispatch = if ownDragBlock.IsSome then this.invokeSync else this.invokeAsync
            dispatch <| fun() ->
                let beginDrag() =
                    isDraggingCell.value <- true
                    group.repairStripZorder("drag-begin")
                    this.ts.transparent <- false
                    // Every group shows a thin strip on each allowed edge it has
                    // no tab on, for as long as the drag lasts (2-5). Never while
                    // the top edge is the only one.
                    if not (TabEdges.isTopOnly this.ts.allowedEdges) then
                        this.ts.isReceiving <- true
                    // Multi-select drag-reorder: if the drag origin tab is part
                    // of the multi-select set, compute the contiguous range and
                    // hand it to the strip so the slide rendering carries all
                    // members along with the pivot.
                    // Guard: dragBegin runs async (BeginInvoke), but for a
                    // single-tab group the synchronous dragEnter->dragExit that
                    // follows can detach the only tab and empty this group
                    // BEFORE this callback executes. In that case the origin tab
                    // is gone, so there is no group to build — computing it would
                    // call group.topWindow on an empty zorder and crash
                    // ("The input list was empty.").
                    match !mouseDownTab with
                    | Some originTab when this.ts.tabs.contains(originTab) ->
                        let grp = ownDragBlock |> Option.defaultWith (fun () -> this.contiguousSelectedFromTab(originTab))
                        if grp.Length > 1 then
                            this.ts.dragGroup <- grp
                        else
                            this.ts.dragGroup <- []
                    | _ ->
                        if TabEdges.clearBlockOnBegin this.ts.allowedEdges dragInfoCell.value.IsSome then
                            this.ts.dragGroup <- []
                TabEdges.endDrag this.ts.allowedEdges beginDrag this.cleanupEdgeDrag

        member this.dragEnter dragInfo pt = this.dragEnterOn(TabEdges.Edge.Top, dragInfo, pt)

        member this.dragMove(pt) =
            if ownDragBlock.IsSome then
                let bounds = os.windowFromHwnd(this.ts.hwnd).bounds
                this.dragMoveOn(Pt(max 0 (min (max 0 (bounds.width - 1)) pt.x), max 0 (min (max 0 (bounds.height - 1)) pt.y)))
            else this.dragMoveOn(pt)

        member this.dragExit() = this.dragExitOn()

        member this.dragEnd() =
            this.invokeAsync <| fun() ->
                let finish() =
                    this.ts.stopDragScroll()
                    this.ts.isReceiving <- false
                    this.dragEndBody()
                TabEdges.endDrag this.ts.allowedEdges finish this.cleanupEdgeDrag

    // The end of a drag, on this group's thread (see dragEnd).
    member private this.dragEndBody() =
#if DEBUG
                dragInfoCell.value |> Option.iter (fun info ->
                    let (Tab hwnd) = info.tab
                    group.traceDockDrag("end-before", this.ts.edgeOf info.tab, hwnd))
#endif
                isDraggingCell.value <- false
                group.repairStripZorder("drag-end")
                this.ts.transparent <- true
                let didMove =
                    match this.dropOnSideStrip() with
                    | Some(moved) -> moved
                    | None ->
                        match this.ts.movedTab with
                        | Some(tab, index, newAlignment) ->
                            // Multi-select drag-reorder: ts.dragGroup holds the
                            // contiguous range; splice-move the whole group.
                            let grp = this.ts.dragGroup
                            if grp.Length > 1 then
                                this.ts.moveTabs(grp, index, newAlignment)
                                for movedTab in grp do
                                    let (Tab h) = movedTab
                                    Services.program.setWindowAlignment(h, Some(newAlignment))
                            else
                                this.ts.moveTab(tab, index, newAlignment)
                                let (Tab hwnd) = tab
                                Services.program.setWindowAlignment(hwnd, Some(newAlignment))
                            true
                        | None -> false
                this.ts.dragGroup <- []
                mouseDownTab := None

                // Explorer-like: if MouseDown landed on an already-selected
                // tab, reduce the multi-select to single now. This fires on
                // both the drag-cancel path (DragDetectingState onCancel —
                // see DragDropController) and the drag-completed path so the
                // reduction is reliable regardless of OS mouse capture.
                //
                // Exception: if a drag actually moved tab(s), the user
                // clearly intended the multi-select to persist (the drag
                // operated on the whole selection). Keep the selection. A
                // drag that started but ended without moving anything (no
                // movedTab) is treated as a no-op click and still clears
                // the selection — this rescues the user from accidental
                // micro-drags being mistaken for "really meant to drag".
                if !mouseDownOnSelected && not didMove then
                    group.clearSelected()
                mouseDownOnSelected := false

#if DEBUG
                dragInfoCell.value |> Option.iter (fun info ->
                    let (Tab hwnd) = info.tab
                    group.traceDockDrag("end-after", this.ts.edgeOf info.tab, hwnd))
#endif
                dragInfoCell.set(None)
                this.updateTsSlide()
                // Tabs may have changed edge, and the thin strips have gone.
                if not (TabEdges.isTopOnly this.ts.allowedEdges) then
                    this.updateTsPlacement()

    interface IDisposable with
        member this.Dispose() =
            // Unregister from the global registry
            lock decorators (fun () -> decorators.Remove(group.hwnd) |> ignore)
            lock groupInfos (fun () -> groupInfos.Remove(group.hwnd) |> ignore)
