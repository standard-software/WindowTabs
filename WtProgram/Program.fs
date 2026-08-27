//namespace Bemo
open Bemo
open System
open System.Collections.Generic
open System.Drawing
open System.Diagnostics
open System.IO
open System.Text
open System.Reflection
open System.Runtime.InteropServices
open System.Threading
open System.Windows.Forms
open Microsoft.FSharp.Reflection
open Bemo.Win32
//open Bemo.Licensing
open Newtonsoft.Json
open Newtonsoft.Json.Linq
open Microsoft.Win32
open System.Globalization

type ProgramInput =
    | WinEvent of (IntPtr * WinEvent)
    | ShellEvent of (IntPtr * ShellEvent)
    | Timer

type ProgramVersion(parts:List2<int>)=
    new(versionString:string) =
        ProgramVersion(List2(versionString.Split([|'.'|])).map(Int32.Parse))

    member this.parts = parts

    member this.compare(v2:ProgramVersion) =
        let maxLen = max this.parts.length v2.parts.length
        let zeroPad (parts:List2<_>) =
            if parts.length < maxLen then parts.appendList(List2(Seq.init (maxLen - parts.length) (fun _ -> 0)))
            else parts
        let v1 = zeroPad this.parts
        let v2 = zeroPad v2.parts
        v1.zip(v2).tryPick(fun(v1,v2) -> 
            if v1 > v2 then Some(1)
            elif v2 > v1 then Some(-1)
            else None).def(0)

    member this.isNewerThan(v2:ProgramVersion) =
        this.compare(v2) > 0

// Parse RRGGBBAA hex string to Color
let parseColorRRGGBBAA (s: string) : Color option =
    if s.Length = 8 then
        try
            let r = Convert.ToInt32(s.Substring(0, 2), 16)
            let g = Convert.ToInt32(s.Substring(2, 2), 16)
            let b = Convert.ToInt32(s.Substring(4, 2), 16)
            let a = Convert.ToInt32(s.Substring(6, 2), 16)
            Some(Color.FromArgb(a, r, g, b))
        with _ -> None
    else None

// Convert Color to RRGGBBAA hex string
let colorToRRGGBBAA (c: Color) : string =
    sprintf "%02X%02X%02X%02X" (int c.R) (int c.G) (int c.B) (int c.A)

// State snapshot of a tab whose window was closed while WindowTabs runs,
// used to restore the tab (state + group + position) when the same app
// window (same exe path + window title) reappears.
type ClosedTabInfo = {
    exePath: string
    windowTitle: string
    renamedTabName: string option
    isPinned: bool
    fillColor: Color option
    underlineColor: Color option
    borderColor: Color option
    tabAlign: TabAlign option
    groupHwnd: IntPtr
    tabIndex: int
    closedHwnd: IntPtr
    closedAt: DateTime
    // True for an entry SEEDED by the startup restore (a saved window whose
    // application has not started yet), false for a tab the user closed by
    // hand. Only seeds are written back to the settings file, and only seeds
    // may be matched by exe path alone: resurrecting a hand-closed tab, or
    // handing an unrelated window to one, would both be wrong.
    isRestoreSeed: bool
    // Saved rectangle (x, y, width, height) of a seeded window. Kept so the
    // twin disambiguation and the title-less fallback still have a position
    // to compare against.
    savedRect: (int * int * int * int) option
    // Full tab order (hwnds) of the group at close time. Restore placement is
    // computed RELATIVE to this snapshot (count of current tabs that came
    // before this one), which keeps surviving tabs in their original relative
    // position instead of being displaced by absolute-index insertion.
    orderSnapshot: IntPtr list
}

// Exe paths come from the same API on both sides, but the file system does
// not distinguish case and neither should the comparison.
let sameExePath (a: string) (b: string) =
    String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

// The same entry without its per-tab state. Used when a saved entry is
// claimed by application alone and more than one entry could have been the
// right one: the window still rejoins its group at its saved position, but
// wearing a name or a colour that may belong to its twin would be worse than
// wearing none.
let withoutTabState (info: ClosedTabInfo) =
    { info with
        renamedTabName = None
        isPinned = false
        fillColor = None
        underlineColor = None
        borderColor = None
        tabAlign = None }

// Titles are compared after removing VSCode's unsaved-changes marker
// ("● "), so a document that closed clean and reopens dirty (or vice
// versa) still matches.
let normalizeClosedTabTitle (t: string) = t.Replace("● ", "")

// Debug-only trace of the session restore: which saved entry each window
// claimed, by which route, and where it was placed. Truncated at each start.
module RestoreTrace =
    let private path =
        IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WindowTabs", "restore_trace.log")
    let mutable private started = false
    let log (s: string) =
#if DEBUG
        try
            if not started then
                started <- true
                try IO.File.WriteAllText(path, "") with _ -> ()
            IO.File.AppendAllText(path,
                sprintf "%s %s\r\n" (DateTime.Now.ToString("HH:mm:ss.fff")) s)
        with _ -> ()
#else
        ignore s
#endif

type Program() as this =
    let version = "ss_2026.08.28_next1"
    let isStandAlone = System.Diagnostics.Debugger.IsAttached

    let Cell = CellScope()
    let os = OS()
    let invoker = InvokerService.invoker
    let isTabMonitoringSuspendedCell = Cell.create(false)
    // A tab-monitoring suspension is always meant to be paired with a resume,
    // but the pairing is not always safe: several call sites hand the resume to
    // a 200 ms WinForms timer created on the group's own UI thread, and that
    // timer dies silently if the group (hence the thread) goes away first -
    // exactly what a detach of the second-to-last tab does. A lost resume used
    // to be permanent and invisible: for the rest of the session no window was
    // ever auto-grouped again, so newly opened applications got no tab and a
    // tab dragged out of a group never became a group of its own. The
    // suspension is therefore given an expiry, well beyond the longest
    // legitimate operation (a multi-tab detach waits up to about two seconds).
    // Stopwatch rather than DateTime: the expiry must not be thrown off by a
    // clock adjustment (a resume from sleep, a time sync, DST).
    let monotonic = Diagnostics.Stopwatch.StartNew()
    let mutable tabMonitoringSuspendedMs = 0L
    // Overlapping operations each suspend; a delayed resume must only lift the
    // suspension it belongs to, never one a later operation has since taken.
    let mutable tabMonitoringSuspendGeneration = 0
    let tabMonitoringSuspendMaxMs = 10000L
    let mutable updateTraceCount = 0
    let mutable shellTraceCount = 0
    let mutable groupTraceCount = 0
    let isDisabledCell = Cell.create(false)
    let isRestoringTabGroups = Cell.create(false)
    let needsRestoreOnStartup = Cell.create(false)
    let llMouseEvent = Event<_>()

    // case 727 outlook calendar items appear behind outlook main window
    let delayTabExeNames = Set2(List2(["outlook.exe"]))

    let settingsManager = Settings(isStandAlone)

    // One-time migration: legacy dark-mode keys have been replaced by a
    // single "EnableDarkMode" key. Carry the old "EnableMenuDarkMode" value
    // forward (so users who had menu dark mode on keep dark mode after the
    // upgrade) and discard the old "EnableSettingsDialogDarkMode" key
    // entirely. Old keys are removed from settings.json so they don't sit
    // around as orphan data.
    do
        try
            let json = settingsManager.settingsJson
            match json.getBool("EnableDarkMode") with
            | Some _ -> ()  // already on the new key — no migration needed
            | None ->
                match json.getBool("EnableMenuDarkMode") with
                | Some(v) -> json.setBool("EnableDarkMode", v)
                | None -> ()
            // Always purge legacy keys regardless of migration outcome.
            json.update("EnableMenuDarkMode", None)
            json.update("EnableSettingsDialogDarkMode", None)
            settingsManager.settingsJson <- json
        with _ -> ()

    // Load disabled state from settings
    do
        let savedDisabledState =
            try
                settingsManager.settingsJson.getBool("IsDisabled").def(false)
            with
            | _ -> false
        isDisabledCell.set(savedDisabledState)

    let keepAliveCell = Cell.create(List2())
    let keepAlive (obj:obj) =
        keepAliveCell.map(fun l -> l.append(obj))
    let lastPing = Cell.create(DateTime.MinValue)
    let notifiedOfUpgrade = Cell.create(false)
    let inShutdown = Cell.create(false)
    // Windows logoff / restart in progress (WM_QUERYENDSESSION, surfaced as
    // SystemEvents.SessionEnding). From that moment other applications close
    // one after another, each HSHELL_WINDOWDESTROYED shrinks the groups, and a
    // periodic-save tick that fires during the teardown would overwrite
    // SavedTabGroupsForRestart with the half-emptied state - which is exactly
    // the state the next boot would then "restore". The last periodic snapshot
    // is frozen; it is at most ten seconds old and was taken before teardown.
    let inSessionEnd = Cell.create(false)
    let isSubscribed = Cell.create(Map2<IntPtr,IDisposable>())
    let isDroppedAndAwaitingGrouping = Cell.create(Set2())
    // Case C: hwnds recently placed into a group via the multi-select
    // drag-detach path. removeUntabableWindows skips these for a short
    // grace period so the dragExit off-screen parking doesn't cause the
    // window to be stripped from the new group before it gets repositioned.
    let recentlyPlacedHwnds = Cell.create(Map2() : Map2<IntPtr, DateTime>)
    let recentlyPlacedGraceMs = 2000.0
    // Track pending new window launches: process path -> (target group hwnd, invoker tab hwnd, timestamp)
    let pendingNewWindowLaunches = Cell.create(Map2<string, IntPtr * IntPtr * DateTime>())
    // Store the invoker tab hwnd consumed by tryNewWindowLaunch, for use by addWindowToGroup
    let lastNewTabInvokerHwnd = Cell.create(IntPtr.Zero)
    // Track pending standalone launches: process path -> (postAction, timestamp)
    // postAction is invoked with the new window hwnd after it has been added to its new group.
    let pendingStandaloneLaunches = Cell.create(Map2<string, (IntPtr -> unit) * DateTime>())
    // Carry a standalone launch's postAction from tryStandaloneLaunch to addWindowToGroup, keyed by new window hwnd
    let pendingStandalonePostActions = Cell.create(Map2<IntPtr, IntPtr -> unit>())
    // Closed-tab restore: state + position of tabs whose windows were closed
    // while WindowTabs runs, matched by exe path + window title when the app
    // window reappears. In-memory only — a WindowTabs restart clears it.
    let closedTabCache = Cell.create<ClosedTabInfo list>([])
    let closedTabCacheLimit = 500
    // When each window was first seen, so a match by exe path alone can be
    // held back until the window's title has settled.
    let windowFirstSeen = Cell.create(Map2() : Map2<IntPtr, int64>)
    // An application shows a transient title for the first moments of its
    // life: Windows Terminal starts on the shell's own name before the
    // session's, VSCode on "Welcome" before the workspace. Claiming a saved
    // entry by exe path alone during that time hands it - and with it a
    // position in the group - to whichever same-exe window happened to
    // appear first. After a restart a group of four terminals came back in
    // the right slots with the wrong window in each. The fallback therefore
    // waits for the title, unless it is the only candidate and there is
    // nothing to take from anyone else.
    let seedFallbackGraceMs = 20000L
    // Windows that have already taken a saved entry, either by matching one
    // at startup or by claiming one afterwards. A window takes at most one.
    // While saved windows are still waiting to open, the title sync offers
    // every grouped window to the late restore, and a window that was put
    // back in its place at startup is as eligible as any other: one already
    // restored to slot 0 went on to claim the leftover entry of a sibling
    // that never reopened, and moved itself to that sibling's slot. The
    // group's own windows shuffled themselves out of order that way.
    let restoreClaimed = Cell.create(Set2<IntPtr>())
    // Live snapshot of (exePath, windowTitle) per grouped hwnd, so the info
    // is still available when the closed tab is recorded after its window
    // has already been destroyed
    let windowInfoCache = Cell.create(Map2() : Map2<IntPtr, string * string>)
    // Carries a matched ClosedTabInfo from tryClosedTabRestore to the
    // positioning step at the end of addWindowToGroup
    let pendingClosedTabRestores = Cell.create(Map2() : Map2<IntPtr, ClosedTabInfo>)
    // Seeded closed-tab entries reference their restored group by a sentinel
    // token (the group's first saved old hwnd), not by the group's strip
    // hwnd: at restore time the strip window is created asynchronously on
    // the group thread and reading IGroup.hwnd from the main thread right
    // after createGroup is unreliable. The token maps to the live group here.
    let seededGroupMap = Cell.create(Map2() : Map2<IntPtr, IGroup>)
    // Maps a restored window's NEW hwnd to the hwnd it had before closing, so
    // relative placement can locate already-restored siblings in an order
    // snapshot taken before they closed
    let restoredFromMap = Cell.create(Map2() : Map2<IntPtr, IntPtr>)
    // Temporary storage for tab group configuration (used during disable/enable)
    let savedTabGroups = Cell.create<List2<List2<IntPtr> * string * bool * List2<IntPtr>>>(List2())
    let windowNameOverride = Cell.create(Map2())
    // Global per-HWND storage for fill color, underline color and pinned state (persists across group transfers)
    let windowFillColor = Cell.create(Map2() : Map2<IntPtr, Color>)
    let windowUnderlineColor = Cell.create(Map2() : Map2<IntPtr, Color>)
    let windowBorderColor = Cell.create(Map2() : Map2<IntPtr, Color>)
    let windowPinned = Cell.create(Set2<IntPtr>())
    let windowAlignment = Cell.create(Map2() : Map2<IntPtr, TabAlign>)
    let notifyNewVersionEvt = Event<_>()
    let launcher = Launcher()
    // Trailing debounce for updateAppWindows: apps such as LibreOffice fire shell events
    // (HSHELL_WINDOWACTIVATED / HSHELL_WINDOWCREATED etc.) in rapid bursts and each one would
    // otherwise trigger a full window-scan. Coalesce the bursts into a single update.
    let mutable pendingUpdateAppWindowsToken : IDisposable option = None
    let updateAppWindowsDebounceMs = 50

    // Periodic tab-group state save. Without this, a force-quit (Task Manager kill,
    // crash, power loss) loses everything since the last clean shutdown. Fires on the
    // UI thread; the dirty check in Settings.fs skips the ~7 ms disk write when nothing
    // changed, so the steady-state cost is one JArray build and a string compare.
    let periodicSaveTimer = new System.Windows.Forms.Timer(Interval = 10000)
    // Title polling for the closed-tab late restore. A title change alone fires
    // no shell event, so without this the restore could wait for the next event.
    // Cheap: GetWindowText on another process's window is a cached read (no
    // message is sent), and only grouped windows are scanned.
    let titleSyncTimer = new System.Windows.Forms.Timer(Interval = 1000)
   
    let isFirstRun = settingsManager.fileExists.not

    let originalVersion = 
        let original = settingsManager.settings.version
        settingsManager.update <| fun s -> { s with version = version }
        original 

    let registerShellHooks =
        os.registerShellHooks <| fun (hwnd, shellEvent) ->
            match shellEvent with
            | ShellEvent.HSHELL_WINDOWCREATED ->
                if shellTraceCount < 60 then
                    shellTraceCount <- shellTraceCount + 1
                    DragTrace.log (sprintf "shell: WINDOWCREATED hwnd=%X" (hwnd.ToInt64()))
                this.receive(ShellEvent(hwnd, shellEvent))
            | ShellEvent.HSHELL_WINDOWDESTROYED -> this.receive(ShellEvent(hwnd, shellEvent))
            | ShellEvent.HSHELL_WINDOWACTIVATED
            | ShellEvent.HSHELL_RUDEAPPACTIVATED -> this.receive(ShellEvent(hwnd, shellEvent))
            | _ -> ()


    let hotKeyInfo = Map2(List2([
        ("prevTab", (3621, fun (g:IGroup) -> g.switchWindow(false, false)))
        ("nextTab", (3623, fun g -> g.switchWindow(true, false)))
        ]))
        
    let hotKeyManager = HotKeyManager()

    do
        Desktop(this :> IDesktopNotification).ignore
        this.registerHotKeys()
        Services.settings.notifyValue "runAtStartup" this.updateRunAtStartup
        Services.desktop.groupExited.Add <| fun _ -> invoker.asyncInvoke(fun() -> this.updateAppWindows())
        Services.desktop.groupRemoved.Add <| fun _ -> invoker.asyncInvoke(fun() -> this.updateAppWindows())
        // Start the periodic state-save timer. First tick fires 10s after Start(),
        // by which time the desktop and groups are fully initialized.
        periodicSaveTimer.Tick.Add <| fun _ ->
            try
                if inShutdown.value.not && inSessionEnd.value.not then
                    this.saveTabGroupsToSettings()
            with _ -> ()
        periodicSaveTimer.Start()
        // See inSessionEnd. On the shutdown / logoff notification the saved
        // tab groups are FROZEN, not saved: the periodic snapshot is at most
        // 10 s old and was taken in normal operation, which makes it safe by
        // construction - while a save taken here could capture a teardown
        // already in progress (an application that dismantles itself the
        // moment it is queried, or a forced shutdown that skips the query
        // phase). Losing the last few seconds of changes is the accepted
        // price. WindowTabs' own exit (tray menu) still saves normally.
        Microsoft.Win32.SystemEvents.SessionEnding.Add <| fun _ ->
            try
                if inSessionEnd.value.not then
                    inSessionEnd.set(true)
                    periodicSaveTimer.Stop()
            with _ -> ()
        titleSyncTimer.Tick.Add <| fun _ ->
            try
                if inShutdown.value.not && isDisabledCell.value.not then
                    this.syncWindowTitles()
            with _ -> ()
        titleSyncTimer.Start()
    
    member this.desktop = Services.desktop
    member this.isTabMonitoringSuspended
        with get() = isTabMonitoringSuspendedCell.value
        and set(value) =
            if value then
                tabMonitoringSuspendGeneration <- tabMonitoringSuspendGeneration + 1
                tabMonitoringSuspendedMs <- monotonic.ElapsedMilliseconds
            isTabMonitoringSuspendedCell.set(value)

    // Backstop for a suspension whose resume was lost. Run from the main
    // thread's window pass rather than from the property getter: a read must
    // not change state, or the moment auto-grouping comes back would depend on
    // who happened to read the flag.
    member private this.expireStaleTabMonitoringSuspension() =
        if isTabMonitoringSuspendedCell.value &&
           monotonic.ElapsedMilliseconds - tabMonitoringSuspendedMs > tabMonitoringSuspendMaxMs then
            DragTrace.log "tabMonitoring: suspension expired, auto-resuming"
            isTabMonitoringSuspendedCell.set(false)

    member this.updateRunAtStartup(value)=
        let runAtStartup = value.cast<bool>()
        let key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true)
        let keyName = "WindowTabs"
        if runAtStartup then
            let entryAssembly = System.Reflection.Assembly.GetEntryAssembly()
            let exeUri = Uri(entryAssembly.CodeBase)
            key.SetValue(keyName, sprintf "\"%s\"" exeUri.LocalPath)
        else
            key.DeleteValue(keyName, false)

    member this.isAppWindow(window:Window) =
        Services.filter.isAppWindow(window.hwnd)

    member this.isTabbableWindow(window:Window) = 
        Services.filter.isTabbableWindow(window.hwnd)

    member this.isAppWindowStyle(window:Window) =
        Services.filter.isAppWindowStyle(window.hwnd)

    member this.tryDropped(window:Window) =
        if isDroppedAndAwaitingGrouping.value.contains(window.hwnd) then Some(None) else None

    member this.tryNewWindowLaunch(window:Window) =
        let processPath = window.pid.processPath
        match pendingNewWindowLaunches.value.tryFind(processPath) with
        | Some((groupHwnd, invokerHwnd, timestamp)) ->
            // Remove the pending launch (only match once)
            pendingNewWindowLaunches.map(fun m -> m.remove processPath)
            // Store invoker hwnd for addWindowToGroup to use for positioning
            lastNewTabInvokerHwnd.set(invokerHwnd)
            // Check if the launch is still recent (within 30 seconds)
            if (DateTime.Now - timestamp).TotalSeconds < 30.0 then
                // Find the target group
                match this.desktop.groups.tryFind(fun g -> g.hwnd = groupHwnd) with
                | Some(group) -> Some(Some(group))
                | None -> None
            else
                None
        | None -> None

    // Handler for standalone-launched windows: force a new tab group (bypass auto-grouping)
    // and schedule the registered postAction to run after the window is added to that new group.
    member this.tryStandaloneLaunch(window:Window) =
        let processPath = window.pid.processPath
        match pendingStandaloneLaunches.value.tryFind(processPath) with
        | Some((postAction, timestamp)) ->
            pendingStandaloneLaunches.map(fun m -> m.remove processPath)
            if (DateTime.Now - timestamp).TotalSeconds < 30.0 then
                // Remember the action so addWindowToGroup can invoke it after the window is grouped
                pendingStandalonePostActions.map(fun m -> m.add window.hwnd postAction)
                Some(None)  // None = do not attach to any existing group; a fresh group will be created
            else
                None
        | None -> None

    // Get the category number (1-10) for a given process path, or 0 if no category is set
    member private this.getCategoryForProcess(procPath: string) =
        let program = this :> IProgram
        let rec check i =
            if i > 10 then 0
            elif program.getCategoryEnabled(procPath, i) then i
            else check (i + 1)
        check 1

    member this.tryAutoGroup(window:Window) =
        if (this :> IProgram).getAutoGroupingEnabled(window.pid.processPath) then
            let hwndZorders = this.hwndZorders()
            let groups = this.desktop.groups
            let groups =
                match this.cast<IProgram>().tabLimit with
                | Some(tabLimit) -> groups.where(fun g -> g.windows.count < tabLimit)
                | None -> groups
            let groups = groups.where(fun g-> g.windows.count > 0).sortBy(fun g -> g.windows.map(fun hwnd -> hwndZorders.tryFind(hwnd).def(Int32.MaxValue)).minBy(id))

            // Get the category for the current window
            let windowCategory = this.getCategoryForProcess(window.pid.processPath)

            let group =
                if windowCategory > 0 then
                    // Category-based grouping: find a group with any window in the same category
                    groups.tryFind(fun g ->
                        g.windows.tryFind(fun hwnd ->
                            let otherProcPath = os.windowFromHwnd(hwnd).pid.processPath
                            this.getCategoryForProcess(otherProcPath) = windowCategory).IsSome)
                else
                    // No category: use traditional same-process grouping
                    groups.tryFind(fun g -> g.windows.map(fun hwnd -> os.windowFromHwnd(hwnd).pid.processPath).contains((=) window.pid.processPath))
            Some(group)
        else None

    // Record a closed window's tab state + position so the tab can be
    // restored if the same app window (same exe path + window title)
    // reappears while WindowTabs is still running. Called just before the
    // window is removed from its group; the window itself is already
    // destroyed, so the identity comes from the windowInfoCache snapshot.
    member this.recordClosedTab(hwnd: IntPtr, group: IGroup) =
        try
            // The two removal paths (HSHELL_WINDOWDESTROYED and the periodic
            // scan) can both see the window still in its group because removal
            // is async — record each closed hwnd only once.
            if closedTabCache.value |> List.exists (fun e -> e.closedHwnd = hwnd) then () else
            match windowInfoCache.value.tryFind(hwnd) with
            | Some((exePath, windowTitle)) when exePath <> "" && windowTitle <> "" ->
                let groupHwnd = (try group.hwnd with _ -> IntPtr.Zero)
                // Use the strip's real on-screen order; the visualOrder mirror
                // goes stale after pin/unpin normalization.
                let order = group.visualOrderThreadSafe
                let now = DateTime.Now
                // When several tabs of one group close in a burst (an app
                // quitting all its windows), the list shrinks unpredictably
                // between the closes (removal is async). Anchor every burst
                // member to the order snapshot taken at the FIRST close of the
                // burst — the tab's index there is its true pre-close position,
                // and all burst entries then share one consistent snapshot for
                // relative placement at restore time.
                let burstAnchor =
                    closedTabCache.value
                    |> List.filter (fun e ->
                        e.groupHwnd = groupHwnd &&
                        (now - e.closedAt).TotalSeconds < 5.0 &&
                        not (List.isEmpty e.orderSnapshot))
                    |> List.tryLast
                let orderSnapshot, tabIndex =
                    let fallback() =
                        order.list, (order.tryFindIndex((=) hwnd)
                                     |> Option.orElseWith (fun() -> group.visualOrder.tryFindIndex((=) hwnd))
                                     |> Option.defaultValue -1)
                    match burstAnchor with
                    | Some(anchor) ->
                        match anchor.orderSnapshot |> List.tryFindIndex ((=) hwnd) with
                        | Some(i) -> anchor.orderSnapshot, i
                        | None -> fallback()
                    | None -> fallback()
                let info = {
                    exePath = exePath
                    windowTitle = normalizeClosedTabTitle windowTitle
                    renamedTabName = windowNameOverride.value.tryFind(hwnd) |> Option.bind id
                    isPinned = windowPinned.value.contains(hwnd)
                    fillColor = windowFillColor.value.tryFind(hwnd)
                    underlineColor = windowUnderlineColor.value.tryFind(hwnd)
                    borderColor = windowBorderColor.value.tryFind(hwnd)
                    tabAlign = windowAlignment.value.tryFind(hwnd)
                    groupHwnd = groupHwnd
                    tabIndex = tabIndex
                    closedHwnd = hwnd
                    closedAt = now
                    isRestoreSeed = false
                    savedRect = None
                    orderSnapshot = orderSnapshot
                }
                closedTabCache.map(fun l -> info :: l |> List.truncate closedTabCacheLimit)
            | _ -> ()
        with _ -> ()

    // Resolve the group a ClosedTabInfo refers to: normally by strip hwnd,
    // and for seeded entries via the sentinel-token map (see seededGroupMap).
    // The mapped group is verified still present before it is trusted.
    member private this.findGroupForClosedInfo (info: ClosedTabInfo) =
        match this.desktop.groups.tryFind(fun g -> (try g.hwnd = info.groupHwnd with _ -> false)) with
        | Some(g) -> Some(g)
        | None ->
            seededGroupMap.value.tryFind(info.groupHwnd)
            |> Option.bind (fun g ->
                if this.desktop.groups.any(fun x -> obj.ReferenceEquals(x, g)) then Some(g) else None)

    member private this.isInfoGroup (g: IGroup) (info: ClosedTabInfo) =
        (try g.hwnd = info.groupHwnd with _ -> false) ||
        (match seededGroupMap.value.tryFind(info.groupHwnd) with
         | Some(sg) -> obj.ReferenceEquals(sg, g)
         | None -> false)

    // Take (and consume) the most recently recorded closed-tab entry that
    // matches exe path + window title exactly (after title normalization).
    // Exact match only: restoring nothing is better than restoring onto the
    // wrong window.
    member private this.takeClosedTabAt(idx: int) =
        let info = closedTabCache.value.[idx]
        closedTabCache.map(fun l ->
            l |> List.mapi (fun i v -> (i, v)) |> List.filter (fun (i, _) -> i <> idx) |> List.map snd)
        info

    member private this.takeClosedTabMatch(exePath: string, windowTitle: string) =
        if exePath = "" || windowTitle = "" then None else
        let windowTitle = normalizeClosedTabTitle windowTitle
        match closedTabCache.value |> List.tryFindIndex (fun i ->
                sameExePath i.exePath exePath && i.windowTitle = windowTitle) with
        | Some(idx) -> Some(this.takeClosedTabAt idx)
        | None -> None

    // Fallback for a window whose title does not survive a restart. A
    // browser's title is its active tab's, so Edge or Chrome comes back
    // showing a different page and the exact match above finds nothing -
    // which is how one window of a mixed group (Edge + LINE + Chrome) was
    // left behind while the others reassembled. Only restore SEEDS are
    // eligible, never a tab the user closed by hand, and only when the window
    // reopened where it was: applications restore their own geometry, so a
    // rectangle that no longer overlaps means this is a different window.
    // With several candidates the nearest saved rectangle wins.
    member private this.findSeedByExe(exePath: string, bounds: (int * int * int * int) option) =
        if exePath = "" then None else
        let centerOf (x, y, w, h) = (float x + float w / 2.0, float y + float h / 2.0)
        let contains (x, y, w, h) (px, py) =
            px >= float x && px <= float (x + w) && py >= float y && py <= float (y + h)
        let overlaps (saved: int * int * int * int) =
            match bounds with
            | None -> true
            | Some(live) -> contains saved (centerOf live) || contains live (centerOf saved)
        // A window minimized when the state was saved was recorded at the
        // off-screen position Windows parks minimized windows at (-32000).
        // That is not where the window lives, so it says nothing about which
        // live window this is: treat it as no position at all rather than let
        // it rule every candidate out.
        let usableRect (r: int * int * int * int) =
            let (x, y, _, _) = r
            x > -30000 && y > -30000
        let candidates =
            closedTabCache.value
            |> List.mapi (fun i e -> (i, e))
            |> List.filter (fun (_, e) ->
                e.isRestoreSeed &&
                sameExePath e.exePath exePath &&
                (match e.savedRect with
                 | Some(r) when usableRect r -> overlaps r
                 | _ -> true))
        match candidates with
        | [] -> None
        | _ ->
            let idx =
                match bounds with
                | Some(live) ->
                    let (lx, ly) = centerOf live
                    candidates
                    |> List.minBy (fun (_, e) ->
                        match e.savedRect with
                        | Some(r) when usableRect r ->
                            let (sx, sy) = centerOf r
                            (sx - lx) * (sx - lx) + (sy - ly) * (sy - ly)
                        | _ -> infinity)
                    |> fst
                | None -> candidates |> List.head |> fst
            Some(idx, List.length candidates)

    // Whether this window may claim a saved entry by exe path alone yet. One
    // candidate is unambiguous and needs no wait; several mean the title is
    // the only thing that can tell them apart, so the window has to have been
    // around long enough for its own title to have settled.
    member private this.seedFallbackAllowed(hwnd: IntPtr, candidateCount: int) =
        candidateCount <= 1 ||
        (match windowFirstSeen.value.tryFind(hwnd) with
         | Some(seenAt) -> monotonic.ElapsedMilliseconds - seenAt >= seedFallbackGraceMs
         | None -> false)

    // Consume one specific cache entry (the one a peek settled on).
    member private this.takeClosedTabEntry(info: ClosedTabInfo) =
        match closedTabCache.value |> List.tryFindIndex (fun e -> obj.ReferenceEquals(e, info)) with
        | Some(idx) -> Some(this.takeClosedTabAt idx)
        | None -> None

    // Relative placement for a restored tab: the target index is the number
    // of current tabs (excluding the restored one) that sat BEFORE it in the
    // close-time order snapshot — survivors matched by their own hwnd,
    // already-restored siblings via restoredFromMap. Tabs unknown to the
    // snapshot (opened after the close) are treated as coming after. Runs on
    // the group thread; reads only the arguments.
    member private this.closedTabTargetIdx(currentTabs: List2<Tab>, selfTab: Tab, info: ClosedTabInfo, restoredPairs: Map2<IntPtr, IntPtr>) =
        match info.orderSnapshot |> List.tryFindIndex ((=) info.closedHwnd) with
        | Some(rank) ->
            let originalPos (t: Tab) =
                let (Tab h) = t
                match info.orderSnapshot |> List.tryFindIndex ((=) h) with
                | Some(i) -> Some(i)
                | None ->
                    restoredPairs.tryFind(h)
                    |> Option.bind (fun oldH -> info.orderSnapshot |> List.tryFindIndex ((=) oldH))
            currentTabs.list
            |> List.filter (fun t -> t <> selfTab)
            |> List.filter (fun t -> match originalPos t with Some(p) -> p < rank | None -> false)
            |> List.length
        | None ->
            // No usable snapshot: fall back to the recorded absolute index
            let count = currentTabs.list.Length
            if info.tabIndex >= 0 && info.tabIndex < count then info.tabIndex else count

    // Closed-tab restore: when a window matching a recorded closed tab
    // appears, restore its tab state and put it back into its former group.
    // Runs before category/exe auto-grouping in findGroupForWindow.
    member this.tryClosedTabRestore(window:Window) =
        if closedTabCache.value.IsEmpty || restoreClaimed.value.contains(window.hwnd) then None else
        let exePath = try window.pid.processPath with _ -> ""
        let windowTitle = try window.text with _ -> ""
        let bounds =
            try
                let b = window.bounds
                Some(b.x, b.y, b.width, b.height)
            with _ -> None
        let matched =
            match this.takeClosedTabMatch(exePath, windowTitle) with
            | Some(info) -> Some(info)
            | None ->
                match this.findSeedByExe(exePath, bounds) with
                | Some(idx, count) when this.seedFallbackAllowed(window.hwnd, count) ->
                    let info = this.takeClosedTabAt idx
                    Some(if count > 1 then withoutTabState info else info)
                | _ -> None
        (match matched with
         | Some(i) -> RestoreTrace.log (sprintf "claim(early) hwnd=%X token=%X rank=%d align=%A pin=%b title=%s"
                                            (window.hwnd.ToInt64()) (i.groupHwnd.ToInt64()) i.tabIndex i.tabAlign i.isPinned windowTitle)
         | None ->
            if closedTabCache.value |> List.exists (fun e -> e.isRestoreSeed && sameExePath e.exePath exePath) then
                RestoreTrace.log (sprintf "claim(early) hwnd=%X NONE title=%s" (window.hwnd.ToInt64()) windowTitle))
        match matched with
        | Some(info) ->
            let hwnd = window.hwnd
            restoreClaimed.map(fun s -> s.add hwnd)
            // Restore state to the global maps before addWindow so the tab is
            // created with the saved name/colors/pin/alignment (same pattern
            // as restoreTabGroupsFromSettings)
            match info.renamedTabName with
            | Some(name) -> windowNameOverride.set(windowNameOverride.value.add hwnd (Some(name)))
            | None -> ()
            info.fillColor |> Option.iter (fun c -> windowFillColor.set(windowFillColor.value.add hwnd c))
            info.underlineColor |> Option.iter (fun c -> windowUnderlineColor.set(windowUnderlineColor.value.add hwnd c))
            info.borderColor |> Option.iter (fun c -> windowBorderColor.set(windowBorderColor.value.add hwnd c))
            if info.isPinned then windowPinned.set(windowPinned.value.add hwnd)
            info.tabAlign |> Option.iter (fun a -> windowAlignment.set(windowAlignment.value.add hwnd a))
            // Remember old->new hwnd so siblings restored later can locate this
            // tab in their close-time order snapshot
            restoredFromMap.map(fun m -> m.add hwnd info.closedHwnd)
            // Remember the entry so addWindowToGroup can restore the position
            pendingClosedTabRestores.map(fun m -> m.add hwnd info)
            match this.findGroupForClosedInfo info with
            | Some(g) -> Some(Some(g))
            | None -> None  // former group is gone: state is restored, grouping falls through
        | None -> None

    // Main-thread title sync (no cross-thread notification): compares each
    // grouped window's current title with the windowInfoCache snapshot.
    // On a change it refreshes the snapshot and runs the closed-tab late
    // restore for apps whose title settles only after grouping (e.g. VSCode
    // starting on "Welcome" before showing the workspace name). Only
    // pristine tabs (no custom state yet) are touched. Grouped windows
    // without a snapshot yet (e.g. restored at startup, which bypasses
    // addWindowToGroup) get one here, so closing them can be recorded.
    // Called from updateAppWindows and the 1s titleSyncTimer.
    member this.syncWindowTitles() =
        try
            // While saved windows are still waiting to open, every grouped
            // window is offered to the late restore on each pass, not only
            // when its title changes: a window whose title never becomes the
            // saved one has no other moment at which it could rejoin its
            // group once the grace period is over.
            let hasSeeds = closedTabCache.value |> List.exists (fun e -> e.isRestoreSeed)
            this.desktop.groups.iter <| fun gi ->
                gi.windows.iter <| fun hwnd ->
                    match windowInfoCache.value.tryFind(hwnd) with
                    | Some((exePath, oldTitle)) ->
                        let window = os.windowFromHwnd(hwnd)
                        if window.isWindow then
                            let newTitle = try window.text with _ -> oldTitle
                            if newTitle <> oldTitle then
                                windowInfoCache.map(fun m -> m.add hwnd (exePath, newTitle))
                                if newTitle <> "" then
                                    this.tryLateClosedTabRestore(hwnd, exePath, newTitle)
                            elif hasSeeds && exePath <> "" then
                                this.tryLateClosedTabRestore(hwnd, exePath, newTitle)
                    | None ->
                        let window = os.windowFromHwnd(hwnd)
                        if window.isWindow then
                            let exePath = try window.pid.processPath with _ -> ""
                            let title = try window.text with _ -> ""
                            if exePath <> "" then
                                windowInfoCache.map(fun m -> m.add hwnd (exePath, title))
        with _ -> ()

    member private this.tryLateClosedTabRestore(hwnd: IntPtr, exePath: string, windowTitle: string) =
        try
                let isPristine =
                    windowNameOverride.value.tryFind(hwnd).IsNone &&
                    windowPinned.value.contains(hwnd).not &&
                    windowFillColor.value.tryFind(hwnd).IsNone &&
                    windowUnderlineColor.value.tryFind(hwnd).IsNone &&
                    windowBorderColor.value.tryFind(hwnd).IsNone
                if closedTabCache.value.IsEmpty.not && isPristine &&
                   restoreClaimed.value.contains(hwnd).not then
                    // Peek first: the entry is only consumed when applied in place
                    let normTitle = normalizeClosedTabTitle windowTitle
                    let bounds =
                        try
                            let b = os.windowFromHwnd(hwnd).bounds
                            Some(b.x, b.y, b.width, b.height)
                        with _ -> None
                    let peeked =
                        match closedTabCache.value |> List.tryFind (fun i -> sameExePath i.exePath exePath && i.windowTitle = normTitle) with
                        | Some(info) -> Some(info, false)
                        | None ->
                            // The title never became the saved one (a browser
                            // reopened on a different page): once the grace
                            // period has passed, exe path alone is enough to
                            // put the window back with its group.
                            match this.findSeedByExe(exePath, bounds) with
                            | Some(idx, count) when this.seedFallbackAllowed(hwnd, count) ->
                                Some(closedTabCache.value.[idx], count > 1)
                            | _ -> None
                    (match peeked with
                     | Some(i, amb) -> RestoreTrace.log (sprintf "claim(late) hwnd=%X token=%X rank=%d amb=%b title=%s"
                                                            (hwnd.ToInt64()) (i.groupHwnd.ToInt64()) i.tabIndex amb windowTitle)
                     | None -> ())
                    match peeked with
                    | Some(info, ambiguous) ->
                        let currentGroup = this.desktop.groups.tryFind(fun g -> g.windows.contains((=)hwnd))
                        let savedGroup = this.findGroupForClosedInfo info
                        match currentGroup, savedGroup with
                        | Some(cur), Some(saved) when (try cur.hwnd <> saved.hwnd with _ -> false) ->
                            // The tab sits in the wrong group (e.g. VSCode's "Welcome"
                            // window was auto-grouped before the workspace title
                            // appeared). Detach it and let the normal grouping
                            // pipeline re-add it: with the title now matching,
                            // tryClosedTabRestore performs the full group +
                            // position + state restore and consumes the entry.
                            RestoreTrace.log (sprintf "  late hwnd=%X DETACH (wrong group)" (hwnd.ToInt64()))
                            cur.removeWindow(hwnd)
                            this.scheduleUpdateAppWindows()
                        | _ ->
                        match this.takeClosedTabEntry(info) with
                        | Some(entry) ->
                            restoreClaimed.map(fun s -> s.add hwnd)
                            let info = if ambiguous then withoutTabState entry else entry
                            info.fillColor |> Option.iter (fun c -> windowFillColor.set(windowFillColor.value.add hwnd c))
                            info.underlineColor |> Option.iter (fun c -> windowUnderlineColor.set(windowUnderlineColor.value.add hwnd c))
                            info.borderColor |> Option.iter (fun c -> windowBorderColor.set(windowBorderColor.value.add hwnd c))
                            if info.isPinned then windowPinned.set(windowPinned.value.add hwnd)
                            info.tabAlign |> Option.iter (fun a -> windowAlignment.set(windowAlignment.value.add hwnd a))
                            restoredFromMap.map(fun m -> m.add hwnd info.closedHwnd)
                            let restoredPairs = restoredFromMap.value
                            match this.desktop.groups.tryFind(fun g -> g.windows.contains((=)hwnd)) with
                            | Some(g) ->
                                // A window whose title had not settled when it
                                // appeared was auto-grouped before its saved
                                // entry could be claimed, so the entry's group
                                // token was never bound to a live group. Bind
                                // it now, to the group this window landed in:
                                // without it neither this tab nor any sibling
                                // that follows is ever put back in its saved
                                // place, and a group whose windows all start
                                // late comes back in the order they happened to
                                // open in.
                                let mutable isSavedGroup = this.isInfoGroup g info
                                if not isSavedGroup && info.isRestoreSeed &&
                                   info.groupHwnd <> IntPtr.Zero &&
                                   seededGroupMap.value.tryFind(info.groupHwnd).IsNone then
                                    seededGroupMap.map(fun m -> m.add info.groupHwnd g)
                                    isSavedGroup <- true
                                match g :> obj with
                                | :? GroupInfo as gi ->
                                    gi.invokeGroup <| fun() ->
                                        try
                                            let wg = gi.group
                                            let tab = Tab(hwnd)
                                            info.renamedTabName |> Option.iter (fun n -> wg.setTabName(hwnd, Some(n)))
                                            info.fillColor |> Option.iter (fun c -> wg.ts.setTabFillColor(tab, Some(c)))
                                            info.underlineColor |> Option.iter (fun c -> wg.ts.setTabUnderlineColor(tab, Some(c)))
                                            info.borderColor |> Option.iter (fun c -> wg.ts.setTabBorderColor(tab, Some(c)))
                                            // Through the group, not the strip: wg.setTabAlign
                                            // also writes the global map the settings file is
                                            // saved from. The strip-only call left that map
                                            // holding the alignment this window had inherited
                                            // when it was auto-grouped, so the tab looked right
                                            // but came back right-aligned after the NEXT restart.
                                            info.tabAlign |> Option.iter (fun a -> wg.setTabAlign(hwnd, a))
                                            if isSavedGroup then
                                                let currentTabs = wg.ts.visualOrder
                                                let targetIdx = this.closedTabTargetIdx(currentTabs, tab, info, restoredPairs)
                                                match currentTabs.tryFindIndex((=) tab) with
                                                | Some(curIdx) when curIdx <> targetIdx ->
                                                    wg.ts.moveTab(tab, targetIdx)
                                                | _ -> ()
                                                RestoreTrace.log (sprintf "  place(late) hwnd=%X rank=%d target=%d after=%s"
                                                                        (hwnd.ToInt64()) info.tabIndex targetIdx
                                                                        (wg.ts.visualOrder.list |> List.map (fun (Tab h) -> sprintf "%X" (h.ToInt64())) |> String.concat ","))
                                            else
                                                RestoreTrace.log (sprintf "  place(late) hwnd=%X SKIPPED (not saved group)" (hwnd.ToInt64()))
                                            // Enforce the saved pin state AFTER the move:
                                            // moveTab applies smart-pin, which can flip the
                                            // pin state depending on the tab's neighbors.
                                            if info.isPinned && not (wg.ts.isPinned(tab)) then
                                                wg.pinTab(hwnd)
                                            elif not info.isPinned && wg.ts.isPinned(tab) then
                                                wg.unpinTab(hwnd)
                                        with _ -> ()
                                | _ -> ()
                            | None -> ()
                        | None -> ()
                    | None -> ()
        with _ -> ()

    member this.updateAppWindows() =
        this.expireStaleTabMonitoringSuspension()
        if updateTraceCount < 60 || this.desktop.isDragging || this.isTabMonitoringSuspended then
            updateTraceCount <- updateTraceCount + 1
            if updateTraceCount <= 200 then
                DragTrace.log (sprintf "updateAppWindows #%d: isDragging=%b suspended=%b disabled=%b shutdown=%b restorePending=%b"
                                    updateTraceCount this.desktop.isDragging this.isTabMonitoringSuspended isDisabledCell.value inShutdown.value needsRestoreOnStartup.value)
        if this.desktop.isDragging.not then
            // If restoration is needed on startup, do it first before auto-grouping
            if needsRestoreOnStartup.value then
                this.restoreTabGroupsFromSettings()
                needsRestoreOnStartup.set(false)

            if inShutdown.value.not && isDisabledCell.value.not then
                os.windowsInZorder.iter <| fun window ->
                    this.ensureWindowIsSubscribed(window)
                    if this.isTabMonitoringSuspended.not then
                        this.ensureWindowIsGrouped(window)
                this.syncWindowTitles()
            this.destroyEmptyGroups()
            this.removeUntabableWindows()

        this.exitIfNeeded()

    member this.ensureWindowIsSubscribed(window:Window) =
        let hwnd = window.hwnd
        if  isSubscribed.value.contains(hwnd).not &&
            window.pid.isCurrentProcess.not &&
            this.isAppWindowStyle(window)
            then
            if windowFirstSeen.value.tryFind(hwnd).IsNone then
                windowFirstSeen.map(fun m -> m.add hwnd monotonic.ElapsedMilliseconds)
            let registerEvent evt =
                window.setWinEventHook evt (fun() -> this.receive(WinEvent(hwnd, evt)))
            let hooks = List2([WinEvent.EVENT_OBJECT_SHOW;WinEvent.EVENT_OBJECT_HIDE]).map(registerEvent)
            let dispose = {
                new IDisposable with
                    member this.Dispose() =
                        hooks.iter(fun h -> h.Dispose())
                }
            isSubscribed.map(fun s -> s.add hwnd dispose)

    member this.ensureWindowIsGrouped(window:Window) =
        // Skip windows not on the current virtual desktop to prevent regrouping during desktop switch
        if window.isOnCurrentVirtualDesktop && this.isTabbableWindow(window) && this.isInGroup(window.hwnd).not then
            if groupTraceCount < 60 then
                groupTraceCount <- groupTraceCount + 1
                DragTrace.log (sprintf "ensureWindowIsGrouped: hwnd=%X exe=%s" (window.hwnd.ToInt64()) (try window.pid.exeName with _ -> "?"))
            this.addWindowToGroup(window)

    member this.destroyEmptyGroups() =
        this.desktop.groups.iter <| fun gi ->
        if gi.windows.isEmpty && launcher.isLaunching(gi).not then
            gi.destroy()

    member this.removeUntabableWindows() =
        // Case C: prune expired entries from the recently-placed grace map
        // first so it doesn't grow unboundedly.
        let now = DateTime.Now
        let expired =
            recentlyPlacedHwnds.value.items.list
            |> List.filter (fun (_, ts) -> (now - ts).TotalMilliseconds > recentlyPlacedGraceMs)
            |> List.map fst
        if not (List.isEmpty expired) then
            recentlyPlacedHwnds.map(fun m ->
                expired |> List.fold (fun acc h -> acc.remove h) m)
        let isRecentlyPlaced(hwnd) =
            match recentlyPlacedHwnds.value.tryFind(hwnd) with
            | Some _ -> true
            | None -> false
        this.desktop.groups.iter <| fun gi ->
            // Skip groups whose top window is in a native move/size loop: the
            // other windows are parked off-screen for the duration
            // (hideChildWindows) and would wrongly fail the tabbable check,
            // getting kicked out of the group (issue #12 — a click on the
            // window edge could dissolve the group under high load).
            if (try gi.isInMoveSizeThreadSafe with _ -> false) then () else
            gi.windows.iter <| fun hwnd ->
                let window = os.windowFromHwnd(hwnd)
                // Only remove windows that are on the current virtual desktop and not tabbable
                // Don't remove windows on other virtual desktops to preserve tab groups during desktop switch
                // Case C: also skip windows that were just placed into a
                // group via the multi-select drag-detach path — they may
                // still be at the dragExit off-screen parking location
                // while their adjustChildWindows hasn't run yet.
                if window.isOnCurrentVirtualDesktop &&
                   this.isTabbableWindow(window).not &&
                   not (isRecentlyPlaced(hwnd))
                then
                    // Record only genuinely destroyed windows; a window that is
                    // merely hidden (e.g. minimized to tray) keeps its hwnd and
                    // its state in the global maps
                    if window.isWindow.not then
                        this.recordClosedTab(hwnd, gi)
                    gi.removeWindow hwnd

    member this.findGroupForWindow(window:Window) =
        let handlers = List2([
            this.tryDropped
            launcher.findGroup
            this.tryStandaloneLaunch
            this.tryNewWindowLaunch
            this.tryClosedTabRestore
            this.tryAutoGroup
            ])
        handlers.tryPick(fun f -> f(window)).def(None)

    member this.addWindowToGroup(window:Window) =
        let hwnd = window.hwnd
        // Snapshot exe path + title now; recordClosedTab needs them after the
        // window has been destroyed and can no longer be queried
        windowInfoCache.map(fun m ->
            m.add hwnd ((try window.pid.processPath with _ -> ""), (try window.text with _ -> "")))
        let group,isNewGroup =
            match this.findGroupForWindow(window) with
            | Some(group) -> (group, false)
            | None -> (Services.desktop.createGroup(false), true)
        let isDropped = isDroppedAndAwaitingGrouping.value.contains(hwnd)
        DragTrace.log (sprintf "addWindowToGroup: hwnd=%X dropped=%b newGroup=%b" (hwnd.ToInt64()) isDropped isNewGroup)
        //need to add this now so we don't end up creating another group for it while waiting for the WgnWindowAdded notification
        isDroppedAndAwaitingGrouping.map(fun s -> s.remove hwnd)
        let withDelay = not isDropped && isNewGroup && delayTabExeNames.contains(window.pid.exeName)
        group.addWindow(hwnd, withDelay)

        // Check if this is a "New Tab" launch - position after the invoking tab
        let invokerHwnd = lastNewTabInvokerHwnd.value
        if invokerHwnd <> IntPtr.Zero then
            lastNewTabInvokerHwnd.set(IntPtr.Zero)
            match group :> obj with
            | :? GroupInfo as gi ->
                gi.invokeGroup <| fun() ->
                    let wg = gi.group
                    let newTab = Tab(hwnd)
                    let invokerTab = Tab(invokerHwnd)
                    // Set new tab's alignment to match the invoker tab
                    let invokerAlign = wg.ts.getTabAlign(invokerTab)
                    wg.ts.setTabAlign(newTab, invokerAlign)
                    Services.program.setWindowAlignment(hwnd, Some(invokerAlign))
                    // Position new tab after the invoker tab in visual order
                    let tabs = wg.ts.visualOrder
                    match tabs.tryFindIndex((=) invokerTab) with
                    | Some(invokerIdx) ->
                        match tabs.tryFindIndex((=) newTab) with
                        | Some(curIdx) when curIdx <> invokerIdx + 1 ->
                            wg.ts.moveTab(newTab, invokerIdx + 1)
                        | _ -> ()
                    | None -> ()
                    // If invoker tab is pinned, pin the new tab too
                    // This handles the case where the invoker is the rightmost pinned tab
                    // in its group - without this, the new tab would end up unpinned
                    if wg.ts.isPinned(invokerTab) && not (wg.ts.isPinned(newTab)) then
                        wg.ts.pinTab(newTab)
                        Services.program.setWindowPinned(hwnd, true)
            | _ -> ()
        else
            // Joining an existing group: inherit the alignment of the group's
            // last tab so a fully left-aligned group stays all-left and a group
            // with any right-aligned tab puts the joiner on the right, regardless
            // of the joining window's previous per-tab alignment. Then splice to
            // the rightmost slot — normalize is a stable sort, so when the joiner
            // ends up in the same zone as the existing tabs it would otherwise
            // stay at its original index inside that zone instead of landing at
            // the visual end.
            if not isNewGroup then
                match group :> obj with
                | :? GroupInfo as gi ->
                    gi.invokeGroup <| fun() ->
                        let wg = gi.group
                        let newTab = Tab(hwnd)
                        let others = wg.ts.visualOrder.where(fun t -> t <> newTab)
                        match others.list |> List.tryLast with
                        | Some(lastTab) ->
                            let lastAlign = wg.ts.getTabAlign(lastTab)
                            wg.setTabAlign(hwnd, lastAlign)
                            let endIndex = wg.ts.visualOrder.list.Length
                            wg.ts.moveTab(newTab, endIndex)
                        | None -> ()
                | _ -> ()
        // For auto-grouping, position new tab next to same-exe tabs
        if invokerHwnd = IntPtr.Zero && not isNewGroup && not isDropped then
            let procPath = window.pid.processPath
            match group :> obj with
            | :? GroupInfo as gi ->
                gi.invokeGroup <| fun() ->
                    let wg = gi.group
                    let tabs = wg.ts.visualOrder
                    let newTab = Tab(hwnd)
                    // Find the rightmost tab of the same exe (excluding the new tab)
                    let mutable lastSameExeIdx = -1
                    tabs.iteri(fun i t ->
                        if t <> newTab then
                            try
                                let (Tab thwnd) = t
                                let otherProcPath = os.windowFromHwnd(thwnd).pid.processPath
                                if otherProcPath = procPath then
                                    lastSameExeIdx <- i
                            with _ -> ()
                    )
                    if lastSameExeIdx >= 0 then
                        match tabs.tryFindIndex((=) newTab) with
                        | Some(curIdx) when curIdx <> lastSameExeIdx + 1 ->
                            wg.ts.moveTab(newTab, lastSameExeIdx + 1)
                        | _ -> ()
            | _ -> ()

        // Closed-tab restore: reapply saved alignment and visual position after
        // the generic join/positioning blocks above so the restored values win.
        // The position is only meaningful inside the tab's former group.
        match pendingClosedTabRestores.value.tryFind(hwnd) with
        | Some(info) ->
            pendingClosedTabRestores.map(fun m -> m.remove hwnd)
            let isSavedGroup = this.isInfoGroup group info
            // The former group no longer exists (e.g. the whole group was
            // absent at startup and its members are reopening one by one, so
            // the cache entries still carry the dead sentinel). Bind the
            // sentinel to the group this window actually landed in, so the
            // siblings reassemble there instead of each spawning a group of
            // its own.
            //
            // What is registered is the group OBJECT, not its strip hwnd:
            // TabStripDecorator is constructed asynchronously on the group
            // thread, so a group created moments ago has no strip hwnd yet
            // and reading it here throws. Keying on the hwnd therefore did
            // nothing in exactly the case this exists for - a whole saved
            // group absent at boot - and Edge, LINE and Chrome each ended up
            // in a group of their own.
            if not isSavedGroup && info.groupHwnd <> IntPtr.Zero then
                seededGroupMap.map(fun m -> m.add info.groupHwnd group)
            let restoredPairs = restoredFromMap.value
            match group :> obj with
            | :? GroupInfo as gi ->
                gi.invokeGroup <| fun() ->
                    try
                        let wg = gi.group
                        let newTab = Tab(hwnd)
                        info.tabAlign |> Option.iter (fun a -> wg.setTabAlign(hwnd, a))
                        if isSavedGroup then
                            let currentTabs = wg.ts.visualOrder
                            let targetIdx = this.closedTabTargetIdx(currentTabs, newTab, info, restoredPairs)
                            match currentTabs.tryFindIndex((=) newTab) with
                            | Some(curIdx) when curIdx <> targetIdx ->
                                wg.ts.moveTab(newTab, targetIdx)
                            | _ -> ()
                            RestoreTrace.log (sprintf "  place(early) hwnd=%X rank=%d target=%d after=%s"
                                                    (hwnd.ToInt64()) info.tabIndex targetIdx
                                                    (wg.ts.visualOrder.list |> List.map (fun (Tab h) -> sprintf "%X" (h.ToInt64())) |> String.concat ","))
                        else
                            RestoreTrace.log (sprintf "  place(early) hwnd=%X SKIPPED (not saved group)" (hwnd.ToInt64()))
                        // Enforce the saved pin state AFTER the move: moveTab
                        // applies smart-pin, which can flip the pin state
                        // depending on the restored tab's neighbors.
                        if info.isPinned && not (wg.ts.isPinned(newTab)) then
                            wg.pinTab(hwnd)
                        elif not info.isPinned && wg.ts.isPinned(newTab) then
                            wg.unpinTab(hwnd)
                    with _ -> ()
            | _ -> ()
        | None -> ()

        // Run any post-action registered for a standalone launch (position, etc.)
        match pendingStandalonePostActions.value.tryFind(hwnd) with
        | Some(action) ->
            pendingStandalonePostActions.map(fun m -> m.remove hwnd)
            invoker.asyncInvoke(fun () -> action hwnd)
        | None -> ()

    member this.scheduleUpdateAppWindows() =
        pendingUpdateAppWindowsToken |> Option.iter (fun t -> t.Dispose())
        pendingUpdateAppWindowsToken <-
            Some(ThreadHelper.cancelablePostBack updateAppWindowsDebounceMs (fun () ->
                pendingUpdateAppWindowsToken <- None
                this.updateAppWindows()))

    member this.receive message =
        let mutable skipFullUpdate = false
        match message with
        | WinEvent(hwnd, evt) -> ()
        | ShellEvent(hwnd, evt) ->
            match evt with
            | ShellEvent.HSHELL_WINDOWDESTROYED ->
                isSubscribed.value.tryFind(hwnd).iter <| fun dispose -> dispose.Dispose()
                // Direct removal + lightweight cleanup instead of expensive full window scan.
                // EVENT_OBJECT_HIDE already handles tab removal via updateAppWindows(),
                // so HSHELL_WINDOWDESTROYED only needs cleanup for any remaining cases.
                this.desktop.groups.tryFind(fun g -> g.windows.contains((=)hwnd)).iter <| fun g ->
                    this.recordClosedTab(hwnd, g)
                    g.removeWindow(hwnd)
                windowInfoCache.map(fun m -> m.remove hwnd)
                windowFirstSeen.map(fun m -> m.remove hwnd)
                restoreClaimed.map(fun s -> s.remove hwnd)
                this.destroyEmptyGroups()
                this.exitIfNeeded()
                skipFullUpdate <- true
            | _ ->()
        | Timer -> ()

        if not skipFullUpdate then
            this.scheduleUpdateAppWindows()

    member this.exitIfNeeded() =
        if inShutdown.value then
            if this.desktop.isEmpty then Application.ExitThread()

    member this.saveSettingsAndUpdateAppWindows(f) =
        settingsManager.update f
        this.updateAppWindows()


    //needed to keep hook alive
    member this.keepAliveReference = keepAliveCell.value

    member this.foregroundGroup = this.desktop.foregroundGroup

    member this.registerHotKeys() =
        hotKeyInfo.items.iter <| fun(key,(_,f)) ->
            let f() =
                this.foregroundGroup.iter <| fun group -> 
                    f(group)
            let shortcut = this.cast<IProgram>().getHotKey(key)
            let shortcut = HotKeyShortcut(HotKeyControlCode=int16(shortcut))
            hotKeyManager.register key (shortcut.RegisterHotKeyModifierFlags, shortcut.RegisterHotKeyVirtualKeyCode) f |> ignore

   
    member this.hwndZorders() : Map2<IntPtr, int>= Map2(os.windowsInZorder.enumerate.map(fun(i,w) -> w.hwnd,i))
    
    member this.isInGroup hwnd : bool =
        this.desktop.groups.any(fun group -> group.windows.contains((=)hwnd))

    member this.notifyNewVersion = notifyNewVersionEvt.Publish

    member this.refresh() = this.receive(Timer)

    // Save tab group configuration to settings file for restoration on next startup
    // Uses hwnd only for matching - simpler and more reliable
    // One saved window that has not opened yet, as it goes back into the
    // settings file.
    member private this.seedToJson(e: ClosedTabInfo) =
        let o = JObject()
        o.setInt64("hwnd", e.closedHwnd.ToInt64())
        o.setString("exePath", e.exePath)
        o.setString("windowTitle", e.windowTitle)
        (match e.savedRect with
         | Some(x, y, w, h) -> o.setString("rect", sprintf "%d,%d,%d,%d" x y w h)
         | None -> ())
        (match e.renamedTabName with
         | Some(name) -> o.setString("renamedTabName", name)
         | None -> ())
        if e.isPinned then o.setBool("isPinned", true)
        (match e.fillColor with
         | Some(c) -> o.setString("tabFillColor", colorToRRGGBBAA c)
         | None -> ())
        (match e.underlineColor with
         | Some(c) -> o.setString("tabUnderlineColor", colorToRRGGBBAA c)
         | None -> ())
        (match e.borderColor with
         | Some(c) -> o.setString("tabBorderColor", colorToRRGGBBAA c)
         | None -> ())
        (match e.tabAlign with
         | Some(a) ->
            o.setString("tabAlignment", (match a with TopLeft -> "TopLeft" | TopRight -> "TopRight"))
         | None -> ())
        o

    member this.saveTabGroupsToSettings() =
        try
            let json = settingsManager.settingsJson
            let groupsArray = JArray()
            // Saved windows whose application has not started yet are held in
            // memory as restore seeds, and nothing else remembers them. The
            // periodic save fires 10 s after boot - long before applications
            // that are not in Startup come up - so without writing the seeds
            // back it would replace the file's record of those groups with
            // whatever happens to be running, which right after a restart is
            // nothing. A WindowTabs restart, or a second reboot, would then
            // have lost the groups for good.
            let pendingSeeds =
                closedTabCache.value
                |> List.filter (fun e -> e.isRestoreSeed)
                |> List.sortBy (fun e -> e.tabIndex)
            let claimedSeeds = System.Collections.Generic.HashSet<IntPtr>()
            this.desktop.groups.iter <| fun gi ->
                let windowsArray = JArray()
                // Save in the strip's real on-screen order (the visualOrder
                // mirror goes stale after pin/unpin normalization). Windows
                // not yet present in the strip snapshot (added moments ago,
                // strip add still pending) are appended so none are lost.
                let snapshotOrder = gi.visualOrderThreadSafe
                let order = snapshotOrder.appendList(gi.visualOrder.where(fun h -> snapshotOrder.contains((=) h).not))
                order.iter <| fun hwnd ->
                    let window = os.windowFromHwnd(hwnd)
                    if window.isWindow then
                        let windowObj = JObject()
                        // Save hwnd for group restoration
                        windowObj.setInt64("hwnd", hwnd.ToInt64())
                        // Also save the window's identity. An hwnd is only
                        // valid within one OS session, so after a Windows
                        // restart the hwnd matches nothing (and can even
                        // collide with an unrelated new window that reuses
                        // the value) - the exe path + normalized title are
                        // what the restore falls back to, and what it uses
                        // to distrust a reused hwnd.
                        let exePath = (try window.pid.processPath with _ -> "")
                        if exePath <> "" then
                            windowObj.setString("exePath", exePath)
                        let title = (try window.text with _ -> "")
                        if title <> "" then
                            windowObj.setString("windowTitle", normalizeClosedTabTitle title)
                        // The rectangle disambiguates two windows of the same
                        // application with the same title (two terminals both
                        // called "Claude1"): exe + title alone assigned their
                        // per-window state first-come-first-served, which put
                        // one window's rename onto the other. Most windows
                        // reopen where they were, so at restore the nearest
                        // saved rectangle picks the right twin.
                        try
                            let b = window.bounds
                            windowObj.setString("rect", sprintf "%d,%d,%d,%d" b.x b.y b.width b.height)
                        with _ -> ()
                        // Save renamed tab name if exists
                        match windowNameOverride.value.tryFind(hwnd) with
                        | Some(Some(name)) ->
                            windowObj.setString("renamedTabName", name)
                        | _ -> ()
                        // Save pinned state from global
                        if windowPinned.value.contains(hwnd) then
                            windowObj.setBool("isPinned", true)
                        // Save tab fill color from global
                        match windowFillColor.value.tryFind(hwnd) with
                        | Some(c) -> windowObj.setString("tabFillColor", colorToRRGGBBAA c)
                        | None -> ()
                        // Save tab underline color from global
                        match windowUnderlineColor.value.tryFind(hwnd) with
                        | Some(c) -> windowObj.setString("tabUnderlineColor", colorToRRGGBBAA c)
                        | None -> ()
                        // Save tab border color from global
                        match windowBorderColor.value.tryFind(hwnd) with
                        | Some(c) -> windowObj.setString("tabBorderColor", colorToRRGGBBAA c)
                        | None -> ()
                        // Save per-tab alignment from global
                        match windowAlignment.value.tryFind(hwnd) with
                        | Some(a) ->
                            let alignStr = match a with TopLeft -> "TopLeft" | TopRight -> "TopRight"
                            windowObj.setString("tabAlignment", alignStr)
                        | None -> ()
                        windowsArray.Add(windowObj)
                // Seeds of a group that has partly reassembled here are
                // saved with it, at the index they held in the saved order.
                pendingSeeds |> List.iter (fun e ->
                    if claimedSeeds.Contains(e.closedHwnd).not then
                        match this.findGroupForClosedInfo e with
                        | Some(g) when obj.ReferenceEquals(g, gi) ->
                            claimedSeeds.Add(e.closedHwnd) |> ignore
                            let at =
                                if e.tabIndex >= 0 && e.tabIndex < windowsArray.Count
                                then e.tabIndex else windowsArray.Count
                            windowsArray.Insert(at, this.seedToJson e)
                        | _ -> ())
                if windowsArray.Count > 0 then
                    let groupObj = JObject()
                    groupObj.addOrUpdate("windows", windowsArray)
                    // Save per-group tab position
                    groupObj.setString("tabPosition", gi.perGroupTabPositionValue)
                    // Save per-group snap tab height margin
                    groupObj.setBool("snapTabHeightMargin", gi.snapTabHeightMargin)
                    groupsArray.Add(groupObj)
            // A group where nothing has opened yet keeps a group of its own,
            // held together by the sentinel token it was seeded under.
            pendingSeeds
            |> List.filter (fun e -> claimedSeeds.Contains(e.closedHwnd).not)
            |> List.groupBy (fun e -> e.groupHwnd)
            |> List.iter (fun (_, entries) ->
                let windowsArray = JArray()
                entries |> List.iter (fun e -> windowsArray.Add(this.seedToJson e))
                if windowsArray.Count > 0 then
                    let groupObj = JObject()
                    groupObj.addOrUpdate("windows", windowsArray)
                    groupsArray.Add(groupObj))
            json.addOrUpdate("SavedTabGroupsForRestart", groupsArray)
            settingsManager.settingsJson <- json
        with
        | _ -> ()

    // Restore tab groups from settings file on startup
    // Uses hwnd only for matching - simpler and more reliable
    // Includes windows on other virtual desktops (cloaked windows) for full restoration
    member this.restoreTabGroupsFromSettings() =
        try
            let json = settingsManager.settingsJson
            match json.getValueCI("SavedTabGroupsForRestart") with
            | Some(:? JArray as groupsArray) when groupsArray.Count > 0 ->
                isRestoringTabGroups.set(true)

                // Get all current windows including those on other virtual desktops (cloaked)
                // We use isAppWindowStyle instead of isTabbableWindow to include cloaked windows
                let currentWindows = os.windowsInZorder.where(fun w ->
                    w.pid.isCurrentProcess.not &&
                    w.isWindow &&
                    this.isAppWindowStyle(w) &&
                    Services.filter.getIsTabbingEnabledForProcess(w.pid.processPath))

                // Build a set of current window hwnds for fast lookup
                let currentHwnds = currentWindows.map(fun w -> w.hwnd.ToInt64()).list |> Set.ofList

                // Identity of every live window, for the two things an hwnd
                // alone cannot do: reject a REUSED hwnd (after a reboot the
                // OS hands the same values to unrelated windows) and find a
                // window again when every hwnd has changed (the reboot case).
                let currentIdent = System.Collections.Generic.Dictionary<int64, string * string * (float * float) option>()
                currentWindows.iter <| fun w ->
                    let exe = (try w.pid.processPath with _ -> "")
                    let title = (try normalizeClosedTabTitle w.text with _ -> "")
                    let center =
                        try
                            let b = w.bounds
                            Some(float b.x + float b.width / 2.0, float b.y + float b.height / 2.0)
                        with _ -> None
                    currentIdent.[w.hwnd.ToInt64()] <- (exe, title, center)
                let currentIdentList =
                    [ for kv in currentIdent ->
                        let (exe, title, center) = kv.Value
                        kv.Key, exe, title, center ]

                // An hwnd match is trusted only if the process still agrees
                // (entries saved before exePath existed are trusted as
                // before). Reserve every trusted hwnd up front, so the
                // identity fallback below can never steal a window that a
                // later group still matches by hwnd.
                let savedHwndTrusted (h: int64) (exePath: string option) =
                    currentHwnds.Contains(h) &&
                    (match exePath with
                     | Some(exe) when exe <> "" ->
                         (match currentIdent.TryGetValue(h) with
                          | true, (curExe, _, _) -> String.Equals(exe, curExe, StringComparison.OrdinalIgnoreCase)
                          | _ -> false)
                     | _ -> true)
                let hwndReserved = System.Collections.Generic.HashSet<int64>()
                for groupToken in groupsArray do
                    let arr =
                        match groupToken with
                        | :? JObject as g ->
                            (match g.getValueCI("windows") with
                             | Some(:? JArray as a) -> a
                             | _ -> JArray())
                        | :? JArray as a -> a
                        | _ -> JArray()
                    for t in arr do
                        match t with
                        | :? JObject as o ->
                            match o.getIntPtr("hwnd") with
                            | Some(h) when savedHwndTrusted (h.ToInt64()) (o.getString("exePath")) ->
                                hwndReserved.Add(h.ToInt64()) |> ignore
                            | _ -> ()
                        | _ -> ()

                // Identity matching after a Windows restart. Once every hwnd
                // is new, exe path + title is all that is left, and it does not
                // always identify one window: two terminals can both be titled
                // "Claude1". The two ambiguous cases are not equally bad.
                //   - The same identity in two DIFFERENT saved groups is not
                //     restored at all. Picking one of them arbitrarily would
                //     not be an uncertain restore, it would be a certain change
                //     to the group structure.
                //   - The same identity more than once inside ONE saved group
                //     is restored. Whichever live window each entry ends up
                //     matching, the resulting group membership is identical, so
                //     it is right either way.
                // Per-window state (rename, pin, colors, alignment) is applied
                // only when the identity is unique on both sides: a rename
                // landing on the wrong twin is worse than no rename at all.
                // Same-session hwnd matches above remain authoritative and do
                // not use this fallback.
                let identityKey (exe: string) (title: string) =
                    exe.ToLowerInvariant() + "\u001f" + title
                let savedIdentityCounts = System.Collections.Generic.Dictionary<string, int>()
                let savedIdentityGroup = System.Collections.Generic.Dictionary<string, int>()
                let crossGroupIdentities = System.Collections.Generic.HashSet<string>()
                let mutable savedGroupIndex = -1
                for groupToken in groupsArray do
                    savedGroupIndex <- savedGroupIndex + 1
                    let arr =
                        match groupToken with
                        | :? JObject as g ->
                            (match g.getValueCI("windows") with
                             | Some(:? JArray as a) -> a
                             | _ -> JArray())
                        | :? JArray as a -> a
                        | _ -> JArray()
                    for token in arr do
                        match token with
                        | :? JObject as entry ->
                            match entry.getIntPtr("hwnd"), entry.getString("exePath"), entry.getString("windowTitle") with
                            | Some(h), Some(exe), Some(title)
                                when exe <> "" && title <> "" && hwndReserved.Contains(h.ToInt64()).not ->
                                let key = identityKey exe title
                                savedIdentityCounts.[key] <-
                                    (match savedIdentityCounts.TryGetValue(key) with
                                     | true, count -> count + 1
                                     | _ -> 1)
                                match savedIdentityGroup.TryGetValue(key) with
                                | true, g when g <> savedGroupIndex -> crossGroupIdentities.Add(key) |> ignore
                                | true, _ -> ()
                                | _ -> savedIdentityGroup.[key] <- savedGroupIndex
                            | _ -> ()
                        | _ -> ()
                let liveIdentityCounts = System.Collections.Generic.Dictionary<string, int>()
                for (_, exe, title, _) in currentIdentList do
                    if exe <> "" && title <> "" then
                        let key = identityKey exe title
                        liveIdentityCounts.[key] <-
                            (match liveIdentityCounts.TryGetValue(key) with
                             | true, count -> count + 1
                             | _ -> 1)
                let isUniqueIdentity (exe: string) (title: string) =
                    let key = identityKey exe title
                    (match savedIdentityCounts.TryGetValue(key) with
                     | true, 1 -> true
                     | _ -> false) &&
                    (match liveIdentityCounts.TryGetValue(key) with
                     | true, 1 -> true
                     | _ -> false)
                // The identity appears in more than one saved group: refuse it.
                let isCrossGroupIdentity (exe: string) (title: string) =
                    crossGroupIdentities.Contains(identityKey exe title)
                // Uniqueness on the saved side alone. A seed is created for a
                // window whose application has not started yet, so there is
                // nothing of it to count on the live side: after an OS restart
                // - the very case seeds exist for - the live half of
                // isUniqueIdentity is empty for every one of them, and asking
                // for it made every window come back stripped of its name, its
                // pin, its colours and its left/right alignment. What can be
                // known now is that no other saved window shares the identity;
                // the live side is answered later, when the seed is claimed:
                // an exact title match is definitive, and a claim by
                // application alone drops the state unless it was the only
                // candidate.
                let isUniqueSavedIdentity (exe: string) (title: string) =
                    match savedIdentityCounts.TryGetValue(identityKey exe title) with
                    | true, 1 -> true
                    | _ -> false

                // Live windows already handed to a restored group, by either
                // path. Each one is consumed at most once.
                let taken = System.Collections.Generic.HashSet<int64>()

                // Resolve one saved window to a live hwnd. First choice: the
                // saved hwnd itself (WindowTabs restarted within the same OS
                // session - the watchdog and manual restarts). Fallback: exe
                // path + normalized title, for the Windows-restart case where
                // every hwnd is new. Exact match only - restoring nothing is
                // better than restoring onto the wrong window.
                let resolveWindow (savedHwnd: IntPtr) (exePath: string option) (title: string option) (savedRect: (int * int * int * int) option) =
                    let h = savedHwnd.ToInt64()
                    if hwndReserved.Contains(h) && taken.Add(h) then Some(savedHwnd)
                    else
                        match exePath, title with
                        | Some(exe), Some(t) when exe <> "" && t <> "" && not (isCrossGroupIdentity exe t) ->
                            let candidates =
                                currentIdentList
                                |> List.filter (fun (ch, cExe, cTitle, _) ->
                                    hwndReserved.Contains(ch).not &&
                                    taken.Contains(ch).not &&
                                    String.Equals(cExe, exe, StringComparison.OrdinalIgnoreCase) &&
                                    cTitle = t)
                            let savedCenter =
                                savedRect
                                |> Option.map (fun (x, y, w, h) ->
                                    (float x + float w / 2.0, float y + float h / 2.0))
                            let pick =
                                match candidates, savedCenter with
                                | [], _ -> None
                                // Two or more windows share exe AND title (two
                                // terminals both called "Claude1"): the one
                                // nearest the saved rectangle is the right
                                // twin, since windows reopen where they were.
                                | (_ :: _ :: _), Some(sx, sy) ->
                                    candidates
                                    |> List.minBy (fun (_, _, _, c) ->
                                        match c with
                                        | Some(cx, cy) -> (cx - sx) * (cx - sx) + (cy - sy) * (cy - sy)
                                        | None -> infinity)
                                    |> Some
                                | (first :: _), _ -> Some(first)
                            pick
                            |> Option.map (fun (ch, _, _, _) ->
                                taken.Add(ch) |> ignore
                                IntPtr(ch))
                        | _ -> None

                // For each saved group, find windows by hwnd and recreate the group
                // Supports both old format (JArray of windows) and new format (JObject with windows + tabPosition)
                for groupToken in groupsArray do
                    let windowsArray, savedTabPosition, savedSnapMargin =
                        match groupToken with
                        | :? JObject as groupObj ->
                            // New format: { windows: [...], tabPosition: "TopLeft", snapTabHeightMargin: true }
                            let windows =
                                match groupObj.getValueCI("windows") with
                                | Some(:? JArray as arr) -> arr
                                | _ -> JArray()
                            let tabPos = groupObj.getString("tabPosition")
                            let snapMargin = groupObj.getBool("snapTabHeightMargin")
                            windows, tabPos, snapMargin
                        | :? JArray as arr ->
                            // Old format: direct array of window objects
                            arr, None, None
                        | _ -> JArray(), None, None

                    if windowsArray.Count > 0 then
                        // Collect saved window info (hwnd, optional renamedTabName, isPinned, fillColor)
                        let savedWindowsList =
                            windowsArray
                            |> Seq.choose (fun t ->
                                match t with
                                | :? JObject as obj ->
                                    obj.getIntPtr("hwnd")
                                    |> Option.map (fun hwnd ->
                                        let isPinned = obj.getBool("isPinned") |> Option.defaultValue false
                                        let fillColor = obj.getString("tabFillColor") |> Option.bind parseColorRRGGBBAA
                                        let underlineColor = obj.getString("tabUnderlineColor") |> Option.bind parseColorRRGGBBAA
                                        let borderColor = obj.getString("tabBorderColor") |> Option.bind parseColorRRGGBBAA
                                        let tabAlign =
                                            match obj.getString("tabAlignment") with
                                            | Some("TopLeft") | Some("Left") -> Some(TopLeft)
                                            | Some("TopRight") | Some("Right") -> Some(TopRight)
                                            | _ -> None
                                        let savedRect =
                                            obj.getString("rect")
                                            |> Option.bind (fun s ->
                                                match s.Split(',') with
                                                | [| xs; ys; ws; hs |] ->
                                                    match Int32.TryParse xs, Int32.TryParse ys, Int32.TryParse ws, Int32.TryParse hs with
                                                    | (true, x), (true, y), (true, w), (true, h) -> Some(x, y, w, h)
                                                    | _ -> None
                                                | _ -> None)
                                        (hwnd, obj.getString("renamedTabName"), isPinned, fillColor, underlineColor, borderColor, tabAlign,
                                         obj.getString("exePath"), obj.getString("windowTitle"), savedRect))
                                | _ -> None)
                            |> List.ofSeq

                        // Saved rectangle per old hwnd, for the seeds below:
                        // a seed that outlives a WindowTabs restart has to
                        // carry its position, or the twin disambiguation and
                        // the title-less fallback lose their reference point.
                        let savedRectByHwnd = System.Collections.Generic.Dictionary<IntPtr, (int * int * int * int) option>()
                        savedWindowsList |> List.iter (fun (h, _, _, _, _, _, _, _, _, r) -> savedRectByHwnd.[h] <- r)

                        // Resolve each saved window to a live one (saved order
                        // kept). The resolved hwnd - not the saved one - is
                        // what the group and the global per-window maps get.
                        let resolvedList =
                            savedWindowsList
                            |> List.map (fun (hwnd, renamed, isPinned, fill, under, border, align, exePath, title, rect) ->
                                (hwnd, renamed, isPinned, fill, under, border, align, exePath, title,
                                 resolveWindow hwnd exePath title rect))
                        let matchedWindows =
                            resolvedList
                            |> List.choose (fun (savedHwnd, renamed, isPinned, fill, under, border, align, exePath, title, live) ->
                                live |> Option.map (fun l ->
                                    // A window matched by its own hwnd is the
                                    // window that was saved - it keeps
                                    // everything. A window matched by identity
                                    // keeps its state only when that identity
                                    // picks out exactly one saved and one live
                                    // window; otherwise it is in the right
                                    // group but may be the wrong twin.
                                    let keepState =
                                        l = savedHwnd ||
                                        (match exePath, title with
                                         | Some(exe), Some(t) -> isUniqueIdentity exe t
                                         | _ -> false)
                                    if keepState then (l, savedHwnd, renamed, isPinned, fill, under, border, align)
                                    else (l, savedHwnd, None, false, None, None, None, None)))

                        // Create group with matched windows (preserving saved order)
                        matchedWindows |> List.iteri (fun i (h, sh, _, _, _, _, _, _) ->
                            RestoreTrace.log (sprintf "  matched[%d] hwnd=%X saved=%X" i (h.ToInt64()) (sh.ToInt64())))
                        let createdGroup =
                          if matchedWindows.Length >= 1 then
                            let group = Services.desktop.createGroup(false)
                            matchedWindows |> List.iter (fun (hwnd, savedHwnd, renamedTabName, isPinned, fillColor, underlineColor, borderColor, tabAlign) ->
                                // Restore to global maps BEFORE addWindow to avoid race condition
                                // (addWindow is async on group thread, which reads from globals)
                                match renamedTabName with
                                | Some(name) ->
                                    windowNameOverride.set(windowNameOverride.value.add hwnd (Some(name)))
                                | None -> ()
                                // Restore fill color to global
                                match fillColor with
                                | Some(c) ->
                                    windowFillColor.set(windowFillColor.value.add hwnd c)
                                | None -> ()
                                // Restore underline color to global
                                match underlineColor with
                                | Some(c) ->
                                    windowUnderlineColor.set(windowUnderlineColor.value.add hwnd c)
                                | None -> ()
                                // Restore border color to global
                                match borderColor with
                                | Some(c) ->
                                    windowBorderColor.set(windowBorderColor.value.add hwnd c)
                                | None -> ()
                                // Restore pinned state to global
                                if isPinned then
                                    windowPinned.set(windowPinned.value.add hwnd)
                                // Restore per-tab alignment to global
                                match tabAlign with
                                | Some(a) ->
                                    windowAlignment.set(windowAlignment.value.add hwnd a)
                                | None -> ()
                                // Identity-matched window: remember new -> old
                                // hwnd, so a sibling restored later can find
                                // this tab in its close-time order snapshot
                                // (closedTabTargetIdx resolves through this map).
                                if hwnd <> savedHwnd then
                                    restoredFromMap.map(fun m -> m.add hwnd savedHwnd)
                                restoreClaimed.map(fun s -> s.add hwnd)
                                group.addWindow(hwnd, false)
                            )
                            // Restore per-group tab position if saved
                            match savedTabPosition with
                            | Some(pos) ->
                                group.perGroupTabPositionValue <- pos
                            | None -> ()  // Use global default (already applied during group creation)
                            // Restore per-group snap tab height margin if saved
                            match savedSnapMargin with
                            | Some(v) ->
                                group.snapTabHeightMargin <- v
                            | None -> ()  // Use global default (already applied during group creation)
                            Some(group)
                          else None

                        // Boot-order independence: windows of this group that
                        // are not open yet (their application starts after
                        // WindowTabs) are seeded into the closed-tab cache, so
                        // the moment such a window appears the closed-tab
                        // restore machinery puts it into its group - whichever
                        // of WindowTabs and the application won the boot race.
                        // closedHwnd carries the old (dead) hwnd purely as a
                        // unique token; the order snapshot is the saved order
                        // in old hwnds, which closedTabTargetIdx resolves
                        // through restoredFromMap.
                        let savedOrderOldHwnds =
                            resolvedList |> List.map (fun (savedHwnd, _, _, _, _, _, _, _, _, _) -> savedHwnd)
                        // The token is always the first saved old hwnd - a
                        // sentinel, never a live strip hwnd, because the strip
                        // of a group created moments ago does not reliably
                        // have one yet. When part of the group was restored,
                        // the token maps to the live group object; when the
                        // whole group was absent, the first sibling to land
                        // repoints the remaining entries (addWindowToGroup).
                        let seedGroupHwnd =
                            match savedOrderOldHwnds with h :: _ -> h | [] -> IntPtr.Zero
                        RestoreTrace.log (sprintf "group token=%X created=%b order=%s"
                                                (seedGroupHwnd.ToInt64())
                                                (createdGroup.IsSome)
                                                (savedOrderOldHwnds |> List.map (fun h -> sprintf "%X" (h.ToInt64())) |> String.concat ","))
                        (match createdGroup with
                         | Some(g) when seedGroupHwnd <> IntPtr.Zero ->
                            seededGroupMap.map(fun m -> m.add seedGroupHwnd g)
                         | _ -> ())
                        if seedGroupHwnd <> IntPtr.Zero then
                            resolvedList
                            |> List.iteri (fun idx entry ->
                                match entry with
                                | (savedHwnd, renamed, isPinned, fill, under, border, align, Some(exe), Some(title), None)
                                    when exe <> "" && title <> "" && not (isCrossGroupIdentity exe title) ->
                                    // Same rule as the direct match above,
                                    // but answered on the saved side only: a
                                    // seed for an ambiguous identity rejoins
                                    // the right group but carries no state.
                                    let unique = isUniqueSavedIdentity exe title
                                    if closedTabCache.value |> List.exists (fun e -> e.closedHwnd = savedHwnd) |> not then
                                        let info = {
                                            exePath = exe
                                            windowTitle = title
                                            renamedTabName = (if unique then renamed else None)
                                            isPinned = (unique && isPinned)
                                            fillColor = (if unique then fill else None)
                                            underlineColor = (if unique then under else None)
                                            borderColor = (if unique then border else None)
                                            tabAlign = (if unique then align else None)
                                            groupHwnd = seedGroupHwnd
                                            tabIndex = idx
                                            closedHwnd = savedHwnd
                                            closedAt = DateTime.Now
                                            isRestoreSeed = true
                                            savedRect =
                                                (match savedRectByHwnd.TryGetValue(savedHwnd) with
                                                 | true, r -> r
                                                 | _ -> None)
                                            orderSnapshot = savedOrderOldHwnds
                                        }
                                        RestoreTrace.log (sprintf "  seed rank=%d token=%X unique=%b align=%A pin=%b title=%s"
                                                                idx (savedHwnd.ToInt64()) unique align isPinned title)
                                        closedTabCache.map(fun l -> info :: l |> List.truncate closedTabCacheLimit)
                                | _ -> ())

                isRestoringTabGroups.set(false)
                // Do NOT clear saved data here - keep it for watchdog restart scenarios
                // Data will be overwritten on normal shutdown/restart
            | _ -> ()
        with
        | _ -> isRestoringTabGroups.set(false)

    interface IProgram with
        member x.version = version
        member x.isUpgrade = version <> originalVersion
        member x.isFirstRun = isFirstRun
        member x.refresh() = this.refresh()
        member x.suspendTabMonitoring() = 
            DragTrace.log (sprintf "suspendTabMonitoring (already=%b)\r\n%s" isTabMonitoringSuspendedCell.value (DragTrace.callers 6))
            this.isTabMonitoringSuspended <- true

        member x.resumeTabMonitoringAfter(delayMs) =
            // The resume must not be owned by the calling group's UI thread:
            // that thread can exit before the delay elapses (detaching the
            // second-to-last tab empties and destroys the group), taking the
            // pending WinForms timer - and the resume - with it. The main
            // thread outlives every group.
            let generation = tabMonitoringSuspendGeneration
            invoker.asyncInvoke <| fun() ->
                (ThreadHelper.cancelablePostBack delayMs <| fun() ->
                    // Only this call's own suspension may be lifted: another
                    // operation may have suspended during the delay, and its
                    // own resume is the one that has to end it.
                    if tabMonitoringSuspendGeneration = generation then
                        this.isTabMonitoringSuspended <- false
                        DragTrace.log "resumeTabMonitoring (delayed)"
                        this.refresh()
                    else
                        DragTrace.log "resumeTabMonitoring (delayed, superseded)") |> ignore

        member x.resumeTabMonitoring() = 
            DragTrace.log "resumeTabMonitoring"
            this.isTabMonitoringSuspended <- false
            this.refresh()

        member x.shutdown() =
            // Stop the periodic save before the explicit final save, so we don't
            // race against an in-flight Tick during shutdown teardown.
            periodicSaveTimer.Stop()
            // A normal WindowTabs exit gets one final snapshot. During Windows
            // logoff/restart, SessionEnding already froze the last known-good
            // periodic snapshot; saving here could overwrite it after other
            // applications have started tearing down their windows.
            if inSessionEnd.value.not then
                this.saveTabGroupsToSettings()
            inShutdown.set(true)
            this.desktop.groups.iter <| fun gi ->
                gi.windows.iter <| fun window ->
                    gi.removeWindow window
            this.updateAppWindows()
                   
        member x.tabLimit = None
     
        member x.setWindowNameOverride((hwnd, name)) = 
            windowNameOverride.set(windowNameOverride.value.add hwnd name)

        member x.getWindowNameOverride(hwnd) =
            windowNameOverride.value.tryFind(hwnd).bind(id)

        member x.setWindowFillColor(hwnd, color : Color option) =
            match color with
            | Some(c) ->
                windowFillColor.set(windowFillColor.value.add hwnd c)
                windowUnderlineColor.set(windowUnderlineColor.value.remove hwnd)
                windowBorderColor.set(windowBorderColor.value.remove hwnd)
            | None -> windowFillColor.set(windowFillColor.value.remove hwnd)

        member x.getWindowFillColor(hwnd) =
            windowFillColor.value.tryFind(hwnd)

        member x.setWindowUnderlineColor(hwnd, color : Color option) =
            match color with
            | Some(c) ->
                windowUnderlineColor.set(windowUnderlineColor.value.add hwnd c)
                windowFillColor.set(windowFillColor.value.remove hwnd)
                windowBorderColor.set(windowBorderColor.value.remove hwnd)
            | None -> windowUnderlineColor.set(windowUnderlineColor.value.remove hwnd)

        member x.getWindowUnderlineColor(hwnd) =
            windowUnderlineColor.value.tryFind(hwnd)

        member x.setWindowBorderColor(hwnd, color : Color option) =
            match color with
            | Some(c) ->
                windowBorderColor.set(windowBorderColor.value.add hwnd c)
                windowFillColor.set(windowFillColor.value.remove hwnd)
                windowUnderlineColor.set(windowUnderlineColor.value.remove hwnd)
            | None -> windowBorderColor.set(windowBorderColor.value.remove hwnd)

        member x.getWindowBorderColor(hwnd) =
            windowBorderColor.value.tryFind(hwnd)

        member x.setWindowPinned(hwnd, pinned : bool) =
            if pinned then windowPinned.set(windowPinned.value.add hwnd)
            else windowPinned.set(windowPinned.value.remove hwnd)

        member x.isWindowPinned(hwnd) =
            windowPinned.value.contains(hwnd)

        member x.setWindowAlignment(hwnd, alignment : TabAlign option) =
            match alignment with
            | Some(a) -> windowAlignment.set(windowAlignment.value.add hwnd a)
            | None -> windowAlignment.set(windowAlignment.value.remove hwnd)

        member x.getWindowAlignment(hwnd) =
            windowAlignment.value.tryFind(hwnd)

        member x.appWindows =
            os.windowsInZorder.where(this.isAppWindow).map(fun w -> w.hwnd)

        member x.getAutoGroupingEnabled procPath =
            settingsManager.settings.autoGroupingPaths.contains(procPath)

        member x.setAutoGroupingEnabled procPath enabled =
            if enabled then
                this.saveSettingsAndUpdateAppWindows <| fun s -> { s with autoGroupingPaths = s.autoGroupingPaths.add procPath }
               //toggle tabbing for the process to force regrouping
                Services.filter.setIsTabbingEnabledForProcess procPath false
                this.refresh()
                Services.filter.setIsTabbingEnabledForProcess procPath true
                this.refresh()
            else
                this.saveSettingsAndUpdateAppWindows <| fun s -> { s with autoGroupingPaths = s.autoGroupingPaths.remove procPath }

        member x.getCategoryEnabled (procPath, categoryNum) =
            let settingsJson = settingsManager.settingsJson
            let categoryKey = sprintf "Category%dPaths" categoryNum
            let paths = settingsJson.getStringArray(categoryKey).def(List2())
            paths.contains((=) procPath)

        member x.setCategoryEnabled procPath categoryNum enabled =
            let settingsJson = settingsManager.settingsJson
            let categoryKey = sprintf "Category%dPaths" categoryNum
            let paths = Set2(settingsJson.getStringArray(categoryKey).def(List2()))
            let newPaths =
                if enabled then paths.add procPath
                else paths.remove procPath
            settingsJson.setStringArray(categoryKey, newPaths.items)
            settingsManager.settingsJson <- settingsJson

        member x.tabAppearanceInfo = 
            settingsManager.settings.tabAppearance

        member x.defaultTabAppearanceInfo = settingsManager.defaultTabAppearance

        member x.darkModeTabAppearanceInfo = 
            settingsManager.darkModeTabAppearance

        member x.darkModeBlueTabAppearanceInfo =
            settingsManager.darkModeBlueTabAppearance

        member x.lightMonoTabAppearanceInfo =
            settingsManager.lightMonoTabAppearance

        member x.darkMonoTabAppearanceInfo =
            settingsManager.darkMonoTabAppearance

        member x.darkMono2TabAppearanceInfo =
            settingsManager.darkMono2TabAppearance

        member x.darkRedFrameTabAppearanceInfo =
            settingsManager.darkRedFrameTabAppearance
            
        member x.getHotKey key = 
            let hotKeys = settingsManager.settingsJson.getObject("HotKeys").def(JObject())
            match hotKeys.getInt32(key) with
            | Some(value) -> value
            | None -> 
                let shortcut, _ = hotKeyInfo.find(key)
                int(shortcut)

        member x.setHotKey key value = 
            let settings = settingsManager.settingsJson
            let hotKeys = settings.getObject("HotKeys").def(JObject())
            hotKeys.setInt32(key, value)
            settings.setObject("HotKeys", hotKeys)
            settingsManager.settingsJson <- settings
            this.registerHotKeys()

        member x.ping() = 
            ()

        member x.notifyNewVersion() = notifyNewVersionEvt.Trigger()
        member x.newVersion = notifyNewVersionEvt.Publish
        member x.llMouse = llMouseEvent.Publish
        member x.isDisabled = isDisabledCell.value
        member x.isShuttingDown = inShutdown.value
        member x.saveTabGroupsBeforeExit() =
            if inSessionEnd.value.not then
                this.saveTabGroupsToSettings()
        member x.setDisabled(value) =
            // Save disabled state to settings
            try
                let json = settingsManager.settingsJson
                json.setBool("IsDisabled", value)
                settingsManager.settingsJson <- json
            with
            | ex -> ()  // Ignore errors when saving settings

            if value then
                // When disabling, save current tab group configuration first (with per-group tab position)
                let groupConfigs = this.desktop.groups.map <| fun gi ->
                    let pinnedHwnds = gi.visualOrder.where(fun hwnd -> gi.isPinned(hwnd))
                    (gi.visualOrder, gi.perGroupTabPositionValue, gi.snapTabHeightMargin, pinnedHwnds)
                savedTabGroups.set(groupConfigs)

                // Set disabled state before destroying groups
                isDisabledCell.set(true)

                // Destroy all tab groups to hide them
                this.desktop.groups.iter <| fun gi ->
                    gi.windows.iter <| fun window ->
                        gi.removeWindow window
                    gi.destroy()
            else
                // When enabling, restore saved tab group configuration
                isDisabledCell.set(false)

                // Suspend tab monitoring to prevent auto-grouping during restore
                this.isTabMonitoringSuspended <- true

                // Restore saved tab groups
                savedTabGroups.value.iter <| fun (hwnds, savedTabPos, savedSnapMargin, pinnedHwnds) ->
                    // Filter out windows that no longer exist or are not visible
                    let validHwnds = hwnds.where <| fun hwnd ->
                        let window = os.windowFromHwnd(hwnd)
                        window.isWindow && window.isVisibleOnScreen

                    if validHwnds.count > 0 then
                        let group = Services.desktop.createGroup(false)
                        validHwnds.iter <| fun hwnd ->
                            group.addWindow(hwnd, false)
                        // Restore per-group tab position
                        group.perGroupTabPositionValue <- savedTabPos
                        // Restore per-group snap tab height margin
                        group.snapTabHeightMargin <- savedSnapMargin
                        // Restore pinned tabs
                        pinnedHwnds.iter <| fun hwnd ->
                            if validHwnds.contains((=) hwnd) then
                                group.pinTab(hwnd)

                // Clear saved configuration
                savedTabGroups.set(List2())

                // Resume tab monitoring
                this.isTabMonitoringSuspended <- false

            this.refresh()

        member x.launchNewWindow(groupHwnd)(invokerHwnd)(processPath) =
            // Register the pending launch: new window with this process path should dock to this group
            pendingNewWindowLaunches.map(fun m -> m.add processPath (groupHwnd, invokerHwnd, DateTime.Now))
            // Start the process
            try
                let psi = ProcessStartInfo()
                psi.UseShellExecute <- true
                psi.FileName <- processPath
                Process.Start(psi) |> ignore
            with
            | _ ->
                // If launch fails, remove the pending entry
                pendingNewWindowLaunches.map(fun m -> m.remove processPath)

        member x.launchStandaloneWindow(processPath)(postAction) =
            // Register the pending launch: the new window must land in its own fresh group
            // regardless of auto-grouping settings, and postAction runs afterwards.
            pendingStandaloneLaunches.map(fun m -> m.add processPath (postAction, DateTime.Now))
            try
                let psi = ProcessStartInfo()
                psi.UseShellExecute <- true
                psi.FileName <- processPath
                Process.Start(psi) |> ignore
            with
            | _ ->
                pendingStandaloneLaunches.map(fun m -> m.remove processPath)

        member x.getAllConfiguredProcessPaths() =
            let paths = System.Collections.Generic.HashSet<string>()
            // Collect from includedPaths and excludedPaths
            let settings = settingsManager.settings
            settings.includedPaths.items.iter(fun p -> paths.Add(p) |> ignore)
            settings.excludedPaths.items.iter(fun p -> paths.Add(p) |> ignore)
            // Collect from autoGroupingPaths
            settings.autoGroupingPaths.items.iter(fun p -> paths.Add(p) |> ignore)
            // Collect from Category1Paths through Category10Paths
            let settingsJson = settingsManager.settingsJson
            for i in 1..10 do
                let categoryKey = sprintf "Category%dPaths" i
                let categoryPaths = settingsJson.getStringArray(categoryKey).def(List2())
                categoryPaths.iter(fun p -> paths.Add(p) |> ignore)
            List2(paths |> Seq.toList)

        member x.removeProcessSettings(procPath) =
            // Remove from includedPaths, excludedPaths, autoGroupingPaths
            this.saveSettingsAndUpdateAppWindows <| fun s ->
                { s with
                    includedPaths = s.includedPaths.remove procPath
                    excludedPaths = s.excludedPaths.remove procPath
                    autoGroupingPaths = s.autoGroupingPaths.remove procPath }
            // Remove from Category1-10Paths
            let settingsJson = settingsManager.settingsJson
            for i in 1..10 do
                let categoryKey = sprintf "Category%dPaths" i
                let paths = Set2(settingsJson.getStringArray(categoryKey).def(List2()))
                let newPaths = paths.remove procPath
                settingsJson.setStringArray(categoryKey, newPaths.items)
            settingsManager.settingsJson <- settingsJson

        member x.markRecentlyPlaced(hwnds) =
            let now = DateTime.Now
            recentlyPlacedHwnds.map(fun m ->
                hwnds |> List.fold (fun acc h -> acc.add h now) m)

    interface IDesktopNotification with
        member x.dragDrop(hwnd) =
            DragTrace.log (sprintf "Program.dragDrop: hwnd=%X" (hwnd.ToInt64()))
            isDroppedAndAwaitingGrouping.map <| fun s -> s.add hwnd

        member x.dragEnd() = 
            this.updateAppWindows()
            

    member this.run(plugins:List2<IPlugin>) =

        // Initialize localization with language setting
        let language =
            try
                let value = settingsManager.settingsJson.["language"]
                if value = null then "English" else value.ToString()
            with
            | _ -> "English"
        Localization.initLanguage(language)

        // Check if there are saved tab groups to restore
        let hasSavedTabGroups =
            try
                let json = settingsManager.settingsJson
                match json.getValueCI("SavedTabGroupsForRestart") with
                | Some(:? JArray as arr) when arr.Count > 0 -> true
                | _ -> false
            with _ -> false

        // If saved tab groups exist, set flag to restore before auto-grouping
        if hasSavedTabGroups then
            needsRestoreOnStartup.set(true)

        Services.register(this :> IProgram)
        Services.register(FilterService() :> IFilterService)
        Services.register(ManagerViewService() :> IManagerView)
        Services.program.refresh()

        plugins.iter <| fun p -> p.init()

        Application.Run()

        plugins.iter <| fun p ->
            match p with
            | :? IDisposable as d -> d.Dispose()
            | _ -> ()

[<STAThread>]
[<EntryPoint>]
let main argv =
    // Per-Monitor-V2 DPI awareness, before anything creates a window or a DC.
    // app.manifest declares the same thing and normally wins, in which case
    // this call simply fails and changes nothing; keeping it means the tab
    // strips still render natively if a packaging step ever drops the win32
    // manifest from the executable.
    Dpi.enablePerMonitorV2()
    // The single-instance check must run before Program() is constructed.
    // The constructor registers shell hooks and starts timers, and the
    // "already running" MessageBox pumps messages, so checking later lets
    // a second instance run updateAppWindows before services are registered
    // and crash with KeyNotFoundException.
    let mutex = new Mutex(false, "BemoSoftware.WindowTabs")
    if System.Diagnostics.Debugger.IsAttached.not then
        if mutex.WaitOne(TimeSpan.FromSeconds(0.5), false).not then
            MessageBox.Show("Another instance of WindowTabs is running, please close it before running this instance.", "WindowTabs is already running.").ignore
            exit(0)

    Application.EnableVisualStyles()

    // WinForms takes ONE process-wide DPI snapshot, the first time any Control
    // is constructed, and uses it for its legacy scaling decisions. Take that
    // snapshot here, under a DPI-unaware context, so it reads 96 dpi.
    //
    // This line predates the settings dialog becoming DPI-aware and its reason
    // has changed, but its value has not, and it is kept deliberately.
    // WinForms' snapshot is a SYSTEM dpi (120 on this desktop, from the 125%
    // primary monitor) and is the same number on every monitor. Letting it be
    // anything but 96 would have WinForms silently pre-scale ToolStrip image
    // sizes, owner-drawn item heights and the like by the wrong, non-per-
    // monitor factor - on top of the explicit per-monitor scaling in
    // SettingsDpi. Pinned at 96, WinForms' own scaling is a no-op everywhere
    // and every device pixel in this process is decided by the Dpi /
    // SettingsDpi modules, from the DPI of the monitor the window is on.
    //
    // Everything WindowTabs draws itself was already scaled explicitly, so the
    // tab strips are unaffected either way.
    //
    // Placed AFTER EnableVisualStyles because that call has to happen before
    // the process creates any window handle, and this one does. Placed BEFORE
    // Program() because its constructor resolves InvokerService.invoker, and
    // Invoker() builds a Form and forces its handle (Shared/Invoker.fs:14) -
    // that Form would otherwise take the snapshot first, under the per-monitor
    // context enabled at the top of main, making this block a no-op.
    //
    // Placed AFTER the single-instance check so a losing instance, which only
    // shows a message box and exits, never touches the snapshot at all.
    Dpi.withUnawareContext <| fun() ->
        try
            use c = new Control()
            c.DeviceDpi.ignore
        with _ -> ()

    let program = Program()
    program.run(List2<obj>([
        InputManagerPlugin(Set2(List2([WindowMessages.WM_MOUSEWHEEL])))
        NotifyIconPlugin()
        ExceptionHandlerPlugin()
    ]).map(fun o -> o.cast<IPlugin>()))
    // Keep the single-instance mutex alive for the whole process lifetime;
    // if the GC collected it, its handle would close and release the mutex.
    GC.KeepAlive(mutex)
    0
