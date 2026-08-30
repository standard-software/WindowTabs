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

// Parse RRGGBBAA hex string to Color. The text half of the conversion is in
// SavedTabState.Rgba, next to the record that carries it, so that the byte
// order the file is written in and the byte order it is read in are one piece
// of code and can be checked without starting WindowTabs.
let parseColorRRGGBBAA (s: string) : Color option =
    SavedTabState.Rgba.parse s
    |> Option.map (fun (r, g, b, a) -> Color.FromArgb(int a, int r, int g, int b))

// Convert Color to RRGGBBAA hex string
let colorToRRGGBBAA (c: Color) : string =
    SavedTabState.Rgba.format (c.R, c.G, c.B, c.A)

// TabAlign as the settings file knows it, and back. Two cases each way, so
// that SavedTabState can stay free of System.Drawing and be loadable by a
// script (see the note at the head of that file).
let savedAlignOfTabAlign =
    function TopLeft -> SavedTabState.AlignLeft | TopRight -> SavedTabState.AlignRight

let tabAlignOfSavedAlign =
    function SavedTabState.AlignLeft -> TopLeft | SavedTabState.AlignRight -> TopRight

// Which group a closed-tab entry belongs to. Both cases carry a window
// handle and neither can be told from the other by value, which is the whole
// reason they are separate cases: a saved token is the first window handle of
// a group as it was written to the settings file, and Windows is free to hand
// that same number to a live tab strip days later. Matching on the case makes
// the two impossible to compare by mistake - it had been got wrong twice.
type GroupRef =
    // The real handle of a tab strip that exists now.
    | LiveStrip of IntPtr
    // A token standing for a saved group, resolved only through seededGroupMap.
    | SavedToken of IntPtr

// The handle either case carries, for keying and for tracing. Never for
// deciding which group an entry belongs to - match on the case for that.
let groupRefHandle = function LiveStrip(h) | SavedToken(h) -> h

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
    groupRef: GroupRef
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
    // When this entry began waiting: the startup restore found the window
    // closed, or the user closed the tab. Entries outlive restarts by being
    // written back to the settings file, so without a date of their own their
    // age would reset at every start and one whose window is never opened
    // again would hold its place for ever.
    seedSince: DateTime option
    // Full tab order (hwnds) of the group at close time. Restore placement is
    // computed RELATIVE to this snapshot (count of current tabs that came
    // before this one), which keeps surviving tabs in their original relative
    // position instead of being displaced by absolute-index insertion.
    orderSnapshot: IntPtr list
    // Whether the name and colours on this entry are known to belong to
    // whichever window claims it. False for an entry read back from the
    // settings file whose application and title are shared with another saved
    // window: the two cannot be told apart, so neither may wear the other's
    // name. The state is still CARRIED - it is written back to the file
    // unchanged, and an entry that is never claimed loses nothing - it is only
    // held back from being put on a live window (see applicableState).
    stateIsCertain: bool
}

// Exe paths come from the same API on both sides, but the file system does
// not distinguish case and neither should the comparison.
let sameExePath (a: string) (b: string) =
    String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

// The same entry without the part of its state that names a particular
// window. Used when more than one saved entry could have been the right one:
// the window still rejoins its group at its saved position, but wearing a name
// or a colour that may belong to its twin would be worse than wearing none.
//
// Alignment and pin are NOT dropped, although they were, and dropping them is
// what made a jumbled group get worse at every restart instead of better.
// They are not decoration, they are the position: TabStrip.visualZoneOf reads
// the pair to decide which of the strip's four bands the tab is drawn in, and
// a saved order can only be honoured inside a band. Keeping the position is
// the one thing this function already promised to do, so keeping these two is
// what that promise meant.
//
// It costs nothing to keep them, either. An identity that appears in more than
// one saved group is refused outright (isCrossGroupIdentity), so the entries a
// claim cannot choose between all belong to ONE group, and each of them
// carries that group with it. Whichever way round the twins end up, the group
// gets back exactly the set of (place, alignment, pin) it was saved with -
// only possibly with two indistinguishable windows exchanged, which is the
// trade already accepted for the place.
//
// Dropping them, by contrast, is not neutral: nothing else ever restores an
// alignment. The tab falls back on whatever the group it was auto-grouped into
// happened to be using, that inherited value is written to the global map, the
// next save records it in place of the real one, and the value the user chose
// is gone from the settings file for good. That is the difference between the
// two halves of the state here - a name that is not applied is merely missing
// for now, an alignment that is not applied is destroyed.
let withoutIdentityState (info: ClosedTabInfo) =
    { info with
        renamedTabName = None
        fillColor = None
        underlineColor = None
        borderColor = None }

// The entry as it may be APPLIED to the window that has claimed it. The entry
// itself always keeps everything that was saved, so that one which is never
// claimed is written back to the settings file whole; only what is put on a
// live window is held back. `claimIsCertain` is what the claim itself could
// tell (an exact title match against a single candidate), `info.stateIsCertain`
// what was already known when the entry was created (no other saved window
// shares its application and title).
let applicableState (claimIsCertain: bool) (info: ClosedTabInfo) =
    if claimIsCertain && info.stateIsCertain then info else withoutIdentityState info

// Titles are compared after removing VSCode's unsaved-changes marker
// ("● "), so a document that closed clean and reopens dirty (or vice
// versa) still matches.
let normalizeClosedTabTitle (t: string) = t.Replace("● ", "")

// Debug-only trace of the session restore: which saved entry each window
// claimed, by which route, and where it was placed. Truncated at each start.
module RestoreTrace =
#if DEBUG
    let private path =
        IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WindowTabs", "restore_trace.log")
    let mutable private started = false
#endif
    // Takes a thunk, not a string: an argument is evaluated before the call,
    // so taking the message itself would leave every sprintf at every call
    // site running in Release - including two that walk the whole tab strip -
    // with only the file write compiled out.
    let log (f: unit -> string) =
#if DEBUG
        try
            if not started then
                started <- true
                try IO.File.WriteAllText(path, "") with _ -> ()
            IO.File.AppendAllText(path,
                sprintf "%s %s\r\n" (DateTime.Now.ToString("HH:mm:ss.fff")) (f()))
        with _ -> ()
#else
        ignore f
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
    // How long a saved window that has not reopened keeps its place in its
    // group. It has to outlast the way applications are actually used - a
    // machine left over a holiday, a project not opened for a fortnight -
    // because dropping an entry too early costs the user a group they have
    // to rebuild by hand, while keeping one too long costs an unseen line in
    // the settings file. The asymmetry is why this is generous.
    let seedMaxAgeDays = 30.0
    // The same for a tab the user closed by hand, which is kept as well so
    // that closing a tab and opening the window again days later still puts
    // it back where it was. Shorter, because the two say different things: an
    // unopened window is "not yet", a closed tab is "not now". Eight days
    // carries a Sunday's work through to the next Sunday.
    let closedTabMaxAgeDays = 8.0
    // How many closed tabs are written out. The cache holds hundreds and the
    // file is rewritten every ten seconds, so only the newest few go in.
    let closedTabSaveLimit = 50
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
    // Tab position and snap margin of a saved group, by the same token. A
    // group whose windows all start late is put together by ordinary
    // auto-grouping, which knows nothing of what was saved, so without this
    // the group came back on the default side of its windows and lost the
    // setting again at the next save.
    let seededGroupSettings = Cell.create(Map2() : Map2<IntPtr, string option * bool option>)
    // Maps a restored window's NEW hwnd to the hwnd it had before closing, so
    // relative placement can locate already-restored siblings in an order
    // snapshot taken before they closed
    // New hwnd -> the old one it was restored from. Not a Cell: a Cell may
    // only be read from the thread that owns it, so the placement running on a
    // group thread had to be handed a copy taken when the tab was claimed.
    // Claiming and placing are seconds apart, and every sibling claimed in
    // between was missing from that copy - so a tab counted fewer tabs before
    // it than there were and landed too far left, displacing the one already
    // in that place. A concurrent dictionary can be read where it is needed,
    // as it stands at that moment.
    let restoredFromMap = System.Collections.Concurrent.ConcurrentDictionary<IntPtr, IntPtr>()
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
                    DragTrace.log (fun () -> sprintf "shell: WINDOWCREATED hwnd=%X" (hwnd.ToInt64()))
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
            DragTrace.log (fun () -> "tabMonitoring: suspension expired, auto-resuming")
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
                        // A burst is made of tabs closed just now, all of
                        // them holding their strip's real handle.
                        (match e.groupRef with
                         | LiveStrip(h) -> h = groupHwnd
                         | SavedToken(_) -> false) &&
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
                    groupRef = LiveStrip(groupHwnd)
                    tabIndex = tabIndex
                    closedHwnd = hwnd
                    closedAt = now
                    isRestoreSeed = false
                    savedRect = None
                    seedSince = Some(now)
                    orderSnapshot = orderSnapshot
                    // Taken from the window itself as it closed, so it is that
                    // window's state and nobody else's.
                    stateIsCertain = true
                }
                closedTabCache.map(fun l -> info :: l |> List.truncate closedTabCacheLimit)
            | _ -> ()
        with _ -> ()

    // Resolve the group a ClosedTabInfo refers to: normally by strip hwnd,
    // and for seeded entries via the sentinel-token map (see seededGroupMap).
    // The mapped group is verified still present before it is trusted.
    member private this.findGroupForClosedInfo (info: ClosedTabInfo) =
        match info.groupRef with
        | SavedToken(token) ->
            seededGroupMap.value.tryFind(token)
            |> Option.bind (fun g ->
                if this.desktop.groups.any(fun x -> obj.ReferenceEquals(x, g)) then Some(g) else None)
        | LiveStrip(strip) ->
            this.desktop.groups.tryFind(fun g -> (try g.hwnd = strip with _ -> false))

    // Hand a live group the settings of the saved group whose token has just
    // been bound to it.
    member private this.applySeededGroupSettings(token: IntPtr, g: IGroup) =
        match seededGroupSettings.value.tryFind(token) with
        | Some(pos, margin) ->
            pos |> Option.iter (fun p -> g.perGroupTabPositionValue <- p)
            margin |> Option.iter (fun m -> g.snapTabHeightMargin <- m)
        | None -> ()

    member private this.isInfoGroup (g: IGroup) (info: ClosedTabInfo) =
        match info.groupRef with
        | SavedToken(token) ->
            (match seededGroupMap.value.tryFind(token) with
             | Some(sg) -> obj.ReferenceEquals(sg, g)
             | None -> false)
        | LiveStrip(strip) -> (try g.hwnd = strip with _ -> false)

    // Take (and consume) the most recently recorded closed-tab entry that
    // matches exe path + window title exactly (after title normalization).
    // Exact match only: restoring nothing is better than restoring onto the
    // wrong window.
    member private this.takeClosedTabAt(idx: int) =
        let info = closedTabCache.value.[idx]
        closedTabCache.map(fun l ->
            l |> List.mapi (fun i v -> (i, v)) |> List.filter (fun (i, _) -> i <> idx) |> List.map snd)
        // Nothing left to claim, so nothing left to guard. Otherwise the
        // record of which windows have claimed grows for the life of the
        // session - it is only pruned when a window is destroyed, and a
        // destroy notification that never arrives would leave a handle in it
        // for Windows to hand to another window, which would then be refused
        // a restore it was entitled to.
        if closedTabCache.value.IsEmpty then
            restoreClaimed.set(Set2<IntPtr>())
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
    // candidate is the only one it could be. Where several entries share the
    // application there is nothing to choose between them, and waiting for the
    // title to settle is not a way of choosing: an application that starts on
    // a name of its own - Excel before a workbook is loaded, VSCode showing
    // the container rather than the folder - still has that name when the wait
    // is over. Claiming then took an entry at random, put the window in its
    // place, and consumed the record, so the window that really belonged there
    // had nothing left to come back to. Two VSCode windows lost their
    // underline that way, and an Excel window took the other workbook's slot.
    //
    // So an entry is claimed by application alone only when it cannot be
    // mistaken, and the window waits until then. Nothing is lost by waiting:
    // the entry stays, the late pass runs while entries remain, and the moment
    // the real title appears the exact match restores the place, the colours
    // and the name together.
    member private this.seedFallbackAllowed(hwnd: IntPtr, candidateCount: int) =
        candidateCount <= 1 &&
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
    member private this.closedTabTargetIdx(currentTabs: List2<Tab>, selfTab: Tab, info: ClosedTabInfo, restoredPairs: System.Collections.Concurrent.ConcurrentDictionary<IntPtr, IntPtr>) =
        match info.orderSnapshot |> List.tryFindIndex ((=) info.closedHwnd) with
        | Some(rank) ->
            let originalPos (t: Tab) =
                let (Tab h) = t
                match info.orderSnapshot |> List.tryFindIndex ((=) h) with
                | Some(i) -> Some(i)
                | None ->
                    (match restoredPairs.TryGetValue(h) with
                     | true, oldH -> Some(oldH)
                     | _ -> None)
                    |> Option.bind (fun oldH -> info.orderSnapshot |> List.tryFindIndex ((=) oldH))
            currentTabs.list
            |> List.filter (fun t -> t <> selfTab)
            |> List.filter (fun t -> match originalPos t with Some(p) -> p < rank | None -> false)
            |> List.length
        | None ->
            // No usable snapshot: fall back to the recorded absolute index
            let count = currentTabs.list.Length
            if info.tabIndex >= 0 && info.tabIndex < count then info.tabIndex else count

    // Put the saved pin state back on a restored tab. Runs on the group thread.
    //
    // Pin is half of the tab's band on the strip and the ordering below sorts
    // within bands, so this has to have happened before any order is worked
    // out - a tab still carrying the pin state that auto-grouping happened to
    // give it would be sorted into the wrong half of the strip and the saved
    // order would come out wrong for every tab in that band, not just this one.
    // It has to happen again after anything that runs the strip's smart-pin
    // rule, which is moveTab and nothing else; setVisualOrder deliberately
    // leaves pin alone.
    member private this.enforcePin(wg: WindowGroup, hwnd: IntPtr, info: ClosedTabInfo) =
        let tab = Tab(hwnd)
        if info.isPinned && not (wg.ts.isPinned(tab)) then wg.pinTab(hwnd)
        elif not info.isPinned && wg.ts.isPinned(tab) then wg.unpinTab(hwnd)

    // Put a whole group back into the order one of its members was saved in.
    // Runs on the group thread; the only shared state it reads is
    // restoredFromMap, which is a concurrent dictionary for that reason.
    //
    // The whole group, not only the tab that has just arrived, and that is the
    // point of it. The tab that CREATES a group is never placed by itself: at
    // that moment it is the only tab there is, the group is not yet known to be
    // the saved one, and until now nothing ever came back to it - so a group
    // whose windows all start after WindowTabs came back with its first window
    // wherever it happened to land and the rest arranged around it, which is
    // exactly the reported fault. Recomputing the entire order at every arrival
    // also makes the result independent of the sequence the windows start in,
    // which is what matters when an application starts half an hour late.
    //
    // Nothing puts this on a timer: it runs only when a tab that belongs to the
    // saved order arrives. Once the group's saved windows have all arrived (or
    // their entries have expired) there is no arrival left to trigger it, so it
    // cannot come back later and undo what the user has done since.
    //
    // The arithmetic itself is in TabOrder, where it can be run without
    // starting WindowTabs; here it is only fed the strip's live values.
    member private this.applySavedOrder(wg: WindowGroup, savedOrder: IntPtr list) =
        if List.isEmpty savedOrder then () else
        let current = wg.ts.visualOrder.list
        let placed =
            current |> List.map (fun (Tab(h) as t) -> TabOrder.placed h (wg.ts.visualZoneOf(t)))
        let oldHandleOf (h: IntPtr) =
            match restoredFromMap.TryGetValue(h) with
            | true, oldHwnd -> Some(oldHwnd)
            | _ -> None
        let desired = TabOrder.restoreOrder savedOrder oldHandleOf placed
        if desired <> (current |> List.map (fun (Tab(h)) -> h)) then
            wg.ts.setVisualOrder(desired |> List.map Tab)

    // The placement half of a closed-tab restore, shared by the early claim
    // (addWindowToGroup) and the late one (tryLateClosedTabRestore) so that the
    // two can never drift apart. Runs on the group thread, after the caller has
    // applied the saved alignment and pin.
    member private this.restorePlacement(wg: WindowGroup, hwnd: IntPtr, info: ClosedTabInfo) =
        let tab = Tab(hwnd)
        // An entry whose own handle is missing from its order snapshot - a tab
        // closed at a moment when its group's order could not be read - has
        // nothing to go on but the absolute index it was recorded at. Use that
        // first; the group pass below then leaves the tab exactly where this
        // put it, because a tab the snapshot does not know keeps the neighbour
        // it currently follows.
        if (info.orderSnapshot |> List.tryFindIndex ((=) info.closedHwnd)).IsNone then
            let currentTabs = wg.ts.visualOrder
            let targetIdx = this.closedTabTargetIdx(currentTabs, tab, info, restoredFromMap)
            match currentTabs.tryFindIndex((=) tab) with
            | Some(curIdx) when curIdx <> targetIdx -> wg.ts.moveTab(tab, targetIdx)
            | _ -> ()
            // moveTab is the one call here that applies smart-pin.
            this.enforcePin(wg, hwnd, info)
        this.applySavedOrder(wg, info.orderSnapshot)

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
        // An entry is claimed on an exact application and title, and on
        // nothing else. Claiming by application alone was meant for a window
        // whose title does not survive a restart - a browser reopening on
        // another page - but there is no way to tell that window from any
        // other of the same application, and being the last candidate left is
        // not the same as being the right one. A second terminal called
        // "PowerShell ver 7 pwsh.exe" took the last free entry in its group,
        // which belonged to a window called "WindowTabs1"; the entry was
        // consumed, and when that window did appear there was nothing left for
        // it. Restoring nothing is better: entries are kept for thirty days,
        // so a window whose title comes back gets its place back with it.
        let matched =
            match this.takeClosedTabMatch(exePath, windowTitle) with
            | Some(info) -> Some(applicableState true info)
            | None -> None
        (match matched with
         | Some(i) -> RestoreTrace.log (fun () -> sprintf "claim(early) hwnd=%X token=%X rank=%d align=%A pin=%b title=%s"
                                                      (window.hwnd.ToInt64()) ((groupRefHandle i.groupRef).ToInt64()) i.tabIndex i.tabAlign i.isPinned windowTitle)
         | None ->
            if closedTabCache.value |> List.exists (fun e -> e.isRestoreSeed && sameExePath e.exePath exePath) then
                RestoreTrace.log (fun () -> sprintf "claim(early) hwnd=%X NONE title=%s" (window.hwnd.ToInt64()) windowTitle))
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
            restoredFromMap.[hwnd] <- info.closedHwnd
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
                        // Exact title only here as well - see tryClosedTabRestore.
                        | None -> None
                    (match peeked with
                     | Some(i, amb) -> RestoreTrace.log (fun () -> sprintf "claim(late) hwnd=%X token=%X rank=%d amb=%b title=%s"
                                                                      (hwnd.ToInt64()) ((groupRefHandle i.groupRef).ToInt64()) i.tabIndex amb windowTitle)
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
                            RestoreTrace.log (fun () -> sprintf "  late hwnd=%X DETACH (wrong group)" (hwnd.ToInt64()))
                            cur.removeWindow(hwnd)
                            this.scheduleUpdateAppWindows()
                        | _ ->
                        match this.takeClosedTabEntry(info) with
                        | Some(entry) ->
                            restoreClaimed.map(fun s -> s.add hwnd)
                            let info = applicableState (not ambiguous) entry
                            info.fillColor |> Option.iter (fun c -> windowFillColor.set(windowFillColor.value.add hwnd c))
                            info.underlineColor |> Option.iter (fun c -> windowUnderlineColor.set(windowUnderlineColor.value.add hwnd c))
                            info.borderColor |> Option.iter (fun c -> windowBorderColor.set(windowBorderColor.value.add hwnd c))
                            if info.isPinned then windowPinned.set(windowPinned.value.add hwnd)
                            info.tabAlign |> Option.iter (fun a -> windowAlignment.set(windowAlignment.value.add hwnd a))
                            restoredFromMap.[hwnd] <- info.closedHwnd
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
                                match info.groupRef with
                                | SavedToken(token) when
                                        not isSavedGroup && info.isRestoreSeed &&
                                        token <> IntPtr.Zero &&
                                        (this.findGroupForClosedInfo info).IsNone ->
                                    seededGroupMap.map(fun m -> m.add token g)
                                    // As in the early pass, only onto a group
                                    // that is this window's alone. A window
                                    // auto-grouped on its own before its title
                                    // settled reaches its saved group only
                                    // here, and refusing every group outright
                                    // left it on the default side for good -
                                    // siblings arriving later find the group
                                    // no longer new and cannot mend it either.
                                    if g.windows.count = 1 then
                                        this.applySeededGroupSettings(token, g)
                                    isSavedGroup <- true
                                | _ -> ()
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
                                            // Alignment and pin first, always,
                                            // whether or not this is the saved
                                            // group: together they are the
                                            // tab's band, the ordering below
                                            // sorts within bands, and a tab
                                            // that landed outside its saved
                                            // group is not ordered at all but
                                            // still keeps what it was saved
                                            // with.
                                            this.enforcePin(wg, hwnd, info)
                                            if isSavedGroup then
                                                this.restorePlacement(wg, hwnd, info)
                                                RestoreTrace.log (fun () -> sprintf "  place(late) hwnd=%X rank=%d order=%s"
                                                                                  (hwnd.ToInt64()) info.tabIndex
                                                                                  (wg.ts.visualOrder.list |> List.map (fun (Tab h) -> sprintf "%X" (h.ToInt64())) |> String.concat ","))
                                            else
                                                RestoreTrace.log (fun () -> sprintf "  place(late) hwnd=%X SKIPPED (not saved group)" (hwnd.ToInt64()))
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
                DragTrace.log (fun () -> sprintf "updateAppWindows #%d: isDragging=%b suspended=%b disabled=%b shutdown=%b restorePending=%b"
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
                DragTrace.log (fun () -> sprintf "ensureWindowIsGrouped: hwnd=%X exe=%s" (window.hwnd.ToInt64()) (try window.pid.exeName with _ -> "?"))
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

    // The alignment the saved entries still waiting for this application all
    // agree on. At startup a group is put together by ordinary grouping
    // seconds before the restore reaches it: the first window in takes the
    // global default, every joiner copies the tab before it, and the restore
    // then corrects only the tabs it can identify by title - leaving any
    // window still showing a generic name (Excel before a workbook loads,
    // VSCode showing the container rather than the folder) on the wrong side
    // of the strip, where it is conspicuous. The entries know which side the
    // group was on before anything opened, so a window that cannot be
    // identified yet starts there instead of on the default side.
    member private this.savedAlignFor(exePath: string) =
        if exePath = "" then None else
        match closedTabCache.value
              |> List.filter (fun e -> e.isRestoreSeed && sameExePath e.exePath exePath)
              |> List.map (fun e -> e.tabAlign) with
        | [] -> None
        | first :: rest when rest |> List.forall ((=) first) -> first
        | _ -> None

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
        DragTrace.log (fun () -> sprintf "addWindowToGroup: hwnd=%X dropped=%b newGroup=%b" (hwnd.ToInt64()) isDropped isNewGroup)
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
            // While saved entries for this application are still waiting, they
            // decide the side instead: see savedAlignFor.
            let savedAlign = this.savedAlignFor(try window.pid.processPath with _ -> "")
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
                            wg.setTabAlign(hwnd, defaultArg savedAlign lastAlign)
                            let endIndex = wg.ts.visualOrder.list.Length
                            wg.ts.moveTab(newTab, endIndex)
                        | None -> ()
                | _ -> ()
            else
                // The window that forms the group has no tab to copy from, and
                // the global default is what put a whole group on the wrong
                // side at startup.
                match savedAlign, (group :> obj) with
                | Some(a), (:? GroupInfo as gi) ->
                    gi.invokeGroup <| fun() -> gi.group.setTabAlign(hwnd, a)
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
            let mutable isSavedGroup = this.isInfoGroup group info
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
            // Only when the token has no LIVE group, as in the late pass. A
            // token pointing at a group that still exists means this window
            // landed somewhere else, and repointing it would send every
            // sibling still to come after it. A token pointing at a group that
            // has since been destroyed - the first window of a saved group
            // opened and was closed again before its siblings started - has to
            // be repointed, or the rest of the group would never reassemble:
            // findGroupForClosedInfo refuses the dead group, and a bare lookup
            // would still find the key and refuse to bind a live one.
            (match info.groupRef with
             | SavedToken(token) when
                    not isSavedGroup && info.isRestoreSeed &&
                    token <> IntPtr.Zero &&
                    (this.findGroupForClosedInfo info).IsNone ->
                seededGroupMap.map(fun m -> m.add token group)
                // Only onto a group made for this window. Where the window
                // joined a group that was already there, that group is some
                // other windows' and its tab position is theirs to keep.
                if isNewGroup then
                    this.applySeededGroupSettings(token, group)
                // This IS now the saved group, as it is on the late path. It
                // was read before the binding above, so it said false, and the
                // window that created its group was left unplaced for ever
                // after - the reported fault. Ordering it is safe even when it
                // has joined some other windows' group: TabOrder moves nothing
                // it does not recognise from the saved order, so tabs that were
                // already there keep the neighbours they have.
                isSavedGroup <- true
             | _ -> ())
            match group :> obj with
            | :? GroupInfo as gi ->
                gi.invokeGroup <| fun() ->
                    try
                        let wg = gi.group
                        let newTab = Tab(hwnd)
                        info.tabAlign |> Option.iter (fun a -> wg.setTabAlign(hwnd, a))
                        // Alignment and pin first, always, whether or not this
                        // is the saved group: together they are the tab's band,
                        // the ordering below sorts within bands, and a tab that
                        // landed outside its saved group is not ordered at all
                        // but still keeps what it was saved with.
                        this.enforcePin(wg, hwnd, info)
                        if isSavedGroup then
                            this.restorePlacement(wg, hwnd, info)
                            RestoreTrace.log (fun () -> sprintf "  place(early) hwnd=%X rank=%d order=%s"
                                                              (hwnd.ToInt64()) info.tabIndex
                                                              (wg.ts.visualOrder.list |> List.map (fun (Tab h) -> sprintf "%X" (h.ToInt64())) |> String.concat ","))
                        else
                            RestoreTrace.log (fun () -> sprintf "  place(early) hwnd=%X SKIPPED (not saved group)" (hwnd.ToInt64()))
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

    // Save tab group configuration to settings file for restoration on next
    // startup.
    //
    // WHAT goes into the file - which groups, in which order, with the windows
    // that have not opened yet spliced back in at the index they held - is
    // decided in SavedSession, and this reads the values out of the desktop
    // and the global per-window maps to hand over. The split is not tidiness.
    // That composition is where a saved order can be taken from the wrong list
    // or an entry aged out by the wrong rule, and while it stood in the middle
    // of this method it could not be run at all without a screen full of
    // windows; over there a script drives it and reads back what was written.

    // One saved window that has not opened yet, as it goes back into the
    // settings file.
    member private this.savedTabOfSeed(e: ClosedTabInfo) =
        { SavedTabState.ofHwnd e.closedHwnd with
            exePath = Some(e.exePath)
            windowTitle = Some(e.windowTitle)
            rect = e.savedRect
            renamedTabName = e.renamedTabName
            isPinned = e.isPinned
            fillColor = e.fillColor |> Option.map colorToRRGGBBAA
            underlineColor = e.underlineColor |> Option.map colorToRRGGBBAA
            borderColor = e.borderColor |> Option.map colorToRRGGBBAA
            align = e.tabAlign |> Option.map savedAlignOfTabAlign
            seedSince = Some(e.seedSince |> Option.defaultValue DateTime.Now)
            // Read back as a tab the user closed, not as a window still
            // waiting to start: only an exact title match may claim one of
            // these. Without the mark they would return as seeds and become
            // eligible for the match by application alone, and a newly
            // opened browser window could be pulled into a group the user
            // had closed a week earlier.
            closedByUser = e.isRestoreSeed.not }

    // One live window as the file records it, and None for a handle that is no
    // longer a window - the one thing here that cannot be decided without
    // asking Windows, which is why the save takes this as a function.
    member private this.savedTabOfWindow(hwnd: IntPtr) =
        let window = os.windowFromHwnd(hwnd)
        if window.isWindow.not then None else
        // The window's identity goes in as well as its handle. A handle is
        // only valid within one OS session, so after a Windows restart it
        // matches nothing (and can even collide with an unrelated new window
        // that reuses the value) - the exe path + normalized title are what
        // the restore falls back to, and what it uses to distrust a reused
        // handle.
        let exePath = (try window.pid.processPath with _ -> "")
        let title = (try window.text with _ -> "")
        // The rectangle tells apart two windows of the same application with
        // the same title (two terminals both called "Claude1"): exe + title
        // alone assigned their per-window state first-come-first-served, which
        // put one window's rename onto the other. Most windows reopen where
        // they were, so at restore the saved rectangles pick the right twins.
        let rect =
            try
                let b = window.bounds
                Some(b.x, b.y, b.width, b.height)
            with _ -> None
        // Written through the same record a not-yet-started window is written
        // through (savedTabOfSeed), so the two cannot disagree about a key or
        // a format.
        Some { SavedTabState.ofHwnd hwnd with
                 exePath = (if exePath <> "" then Some(exePath) else None)
                 windowTitle = (if title <> "" then Some(normalizeClosedTabTitle title) else None)
                 rect = rect
                 renamedTabName = windowNameOverride.value.tryFind(hwnd) |> Option.bind id
                 isPinned = windowPinned.value.contains(hwnd)
                 fillColor = windowFillColor.value.tryFind(hwnd) |> Option.map colorToRRGGBBAA
                 underlineColor = windowUnderlineColor.value.tryFind(hwnd) |> Option.map colorToRRGGBBAA
                 borderColor = windowBorderColor.value.tryFind(hwnd) |> Option.map colorToRRGGBBAA
                 // From the global map, not from the strip. The absence of a
                 // value is meaningful: it says "whichever side this group is
                 // set to", so writing the strip's effective alignment instead
                 // would nail every tab to a side and the group's own
                 // tabPosition setting would stop moving them. What must not
                 // happen is a value going missing from the map, and that is
                 // dealt with where it was lost - at the restore
                 // (SavedSession.plan) - not by papering over it here.
                 align = windowAlignment.value.tryFind(hwnd) |> Option.map savedAlignOfTabAlign }

    member this.saveTabGroupsToSettings() =
        try
            let json = settingsManager.settingsJson
            let saveNow = DateTime.Now
            // Saved windows whose application has not started yet are held in
            // memory as restore seeds, and nothing else remembers them. The
            // periodic save fires 10 s after boot - long before applications
            // that are not in Startup come up - so without writing the seeds
            // back it would replace the file's record of those groups with
            // whatever happens to be running, which right after a restart is
            // nothing. A WindowTabs restart, or a second reboot, would then
            // have lost the groups for good.
            let pending =
                closedTabCache.value
                |> List.map (fun e ->
                    e, { SavedSession.tab = this.savedTabOfSeed e
                         SavedSession.rank = e.tabIndex
                         SavedSession.isRestoreSeed = e.isRestoreSeed })
            let infoOf = System.Collections.Generic.Dictionary<IntPtr, ClosedTabInfo>()
            pending |> List.iter (fun (info, p) -> infoOf.[p.tab.hwnd] <- info)
            let pendingSeeds =
                SavedSession.seedsToSave saveNow seedMaxAgeDays closedTabMaxAgeDays
                                         closedTabSaveLimit (pending |> List.map snd)
            let claimedSeeds = System.Collections.Generic.HashSet<IntPtr>()
            let liveGroups =
                this.desktop.groups.list
                |> List.map (fun gi ->
                    // Seeds of a group that has partly reassembled here are
                    // saved with it. Which group an entry belongs to is a
                    // question about the live desktop, so it is answered here
                    // and the answer handed over.
                    let seeds =
                        pendingSeeds
                        |> List.filter (fun p ->
                            claimedSeeds.Contains(p.tab.hwnd).not &&
                            (match this.findGroupForClosedInfo infoOf.[p.tab.hwnd] with
                             | Some(g) when obj.ReferenceEquals(g, gi) ->
                                claimedSeeds.Add(p.tab.hwnd) |> ignore
                                true
                             | _ -> false))
                    // Both orders, because neither is complete on its own: the
                    // strip snapshot is the real on-screen order but does not
                    // yet hold a window added moments ago, and the mirror goes
                    // stale after a pin/unpin normalization.
                    let group : SavedSession.GroupToSave =
                        { stripOrder = gi.visualOrderThreadSafe.list
                          mirrorOrder = gi.visualOrder.list
                          seeds = seeds
                          tabPosition = Some(gi.perGroupTabPositionValue)
                          snapMargin = Some(gi.snapTabHeightMargin) }
                    group)
            // A group where nothing has opened yet keeps a group of its own,
            // held together by the sentinel token it was seeded under. Tabs
            // the user closed are not kept this way: emptying a group is the
            // plainest way of saying it is finished with, so once its last tab
            // is closed the group goes rather than waiting to be resurrected
            // by whichever of its windows is opened next.
            let waitingGroups =
                pendingSeeds
                |> List.filter (fun p -> claimedSeeds.Contains(p.tab.hwnd).not && p.isRestoreSeed)
                // By the reference itself, not the handle inside it: grouping
                // on the bare number would put a token and a strip handle that
                // happened to share it into one group. The filter above admits
                // only saved entries today, so this changes nothing - it keeps
                // the guarantee in the type rather than in the filter above it.
                |> List.groupBy (fun p -> infoOf.[p.tab.hwnd].groupRef)
                |> List.choose (fun (gref, entries) ->
                    // Only a saved group is written this way, and only a saved
                    // group has settings held against its token.
                    match gref with
                    | LiveStrip(_) -> None
                    | SavedToken(token) ->
                        // The group's own settings go with it. Read from the
                        // live group above, they have no live group to be read
                        // from here, and a second restart would have lost them.
                        let pos, margin =
                            match seededGroupSettings.value.tryFind(token) with
                            | Some(p, m) -> p, m
                            | None -> None, None
                        let group : SavedSession.GroupToSave =
                            { stripOrder = []
                              mirrorOrder = []
                              seeds = entries
                              tabPosition = pos
                              snapMargin = margin }
                        Some(group))
            json.addOrUpdate("SavedTabGroupsForRestart",
                             SavedSession.write this.savedTabOfWindow (liveGroups @ waitingGroups))
            settingsManager.settingsJson <- json
        with
        | _ -> ()

    // Restore tab groups from settings file on startup.
    //
    // WHICH saved tab comes back as which live window, and what may be put on
    // it once it is there, is decided in SavedSession.plan. This gathers the
    // windows that are on screen and then carries the plan out. Same reason as
    // the save: the matching is the part that goes wrong invisibly - a window
    // takes its twin's name, or comes back without the alignment it was saved
    // with and is drawn at the wrong end of the strip - and while it was
    // written in the middle of this method there was no way to run it at all
    // without a desktop full of windows.
    //
    // Includes windows on other virtual desktops (cloaked windows) for full
    // restoration.
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

                // The identity of every live window, for the two things a
                // handle alone cannot do: reject a REUSED handle (after a
                // reboot the OS hands the same values to unrelated windows)
                // and find a window again when every handle has changed (the
                // reboot case). The centre of the rectangle is what tells two
                // windows apart when even the identity cannot.
                let live =
                    currentWindows.list
                    |> List.map (fun w ->
                        { SavedSession.handle = w.hwnd
                          SavedSession.exePath = (try w.pid.processPath with _ -> "")
                          SavedSession.title = (try normalizeClosedTabTitle w.text with _ -> "")
                          SavedSession.center =
                            (try
                                let b = w.bounds
                                Some(float b.x + float b.width / 2.0, float b.y + float b.height / 2.0)
                             with _ -> None) })

                let planned =
                    SavedSession.plan DateTime.Now seedMaxAgeDays closedTabMaxAgeDays
                                      (SavedSession.read groupsArray) live

                for g in planned do
                    let matched = g.tabs |> List.filter (fun t -> t.live.IsSome)
                    matched |> List.iteri (fun i t ->
                        RestoreTrace.log (fun () -> sprintf "  matched[%d] hwnd=%X saved=%X"
                                                            i (t.live.Value.ToInt64()) (t.saved.hwnd.ToInt64())))
                    // Create the group with the matched windows, in saved order.
                    let createdGroup =
                        if matched.IsEmpty then None else
                        let group = Services.desktop.createGroup(false)
                        matched |> List.iter (fun t ->
                            let hwnd = t.live.Value
                            // What the plan allows onto this window: always
                            // the alignment and the pin, the name and the
                            // colours only when they cannot have belonged to
                            // another window.
                            let e = t.applied
                            // Restore to global maps BEFORE addWindow to avoid race condition
                            // (addWindow is async on group thread, which reads from globals)
                            e.renamedTabName |> Option.iter (fun name ->
                                windowNameOverride.set(windowNameOverride.value.add hwnd (Some(name))))
                            e.fillColor |> Option.bind parseColorRRGGBBAA
                            |> Option.iter (fun c -> windowFillColor.set(windowFillColor.value.add hwnd c))
                            e.underlineColor |> Option.bind parseColorRRGGBBAA
                            |> Option.iter (fun c -> windowUnderlineColor.set(windowUnderlineColor.value.add hwnd c))
                            e.borderColor |> Option.bind parseColorRRGGBBAA
                            |> Option.iter (fun c -> windowBorderColor.set(windowBorderColor.value.add hwnd c))
                            if e.isPinned then
                                windowPinned.set(windowPinned.value.add hwnd)
                            e.align |> Option.iter (fun a ->
                                windowAlignment.set(windowAlignment.value.add hwnd (tabAlignOfSavedAlign a)))
                            // Identity-matched window: remember new -> old
                            // handle, so a sibling restored later can find
                            // this tab in its close-time order snapshot
                            // (the order arithmetic resolves through this map).
                            if hwnd <> t.saved.hwnd then
                                restoredFromMap.[hwnd] <- t.saved.hwnd
                            restoreClaimed.map(fun s -> s.add hwnd)
                            group.addWindow(hwnd, false))
                        // The group's own settings, or the global default
                        // already applied at creation when it has none.
                        g.tabPosition |> Option.iter (fun pos -> group.perGroupTabPositionValue <- pos)
                        g.snapMargin |> Option.iter (fun v -> group.snapTabHeightMargin <- v)
                        Some(group)

                    RestoreTrace.log (fun () -> sprintf "group token=%X created=%b order=%s"
                                                        (g.token.ToInt64())
                                                        (createdGroup.IsSome)
                                                        (g.savedOrder |> List.map (fun h -> sprintf "%X" (h.ToInt64())) |> String.concat ","))
                    if g.token <> IntPtr.Zero then
                        seededGroupSettings.map(fun m -> m.add g.token (g.tabPosition, g.snapMargin))
                        (match createdGroup with
                         | Some(grp) -> seededGroupMap.map(fun m -> m.add g.token grp)
                         | None -> ())
                        // Boot-order independence: windows of this group that
                        // are not open yet (their application starts after
                        // WindowTabs) are seeded into the closed-tab cache, so
                        // the moment such a window appears the closed-tab
                        // restore machinery puts it into its group - whichever
                        // of WindowTabs and the application won the boot race.
                        // closedHwnd carries the old (dead) handle purely as a
                        // unique token; the order snapshot is the saved order
                        // in old handles, which the arrival resolves through
                        // restoredFromMap.
                        g.tabs |> List.iter (fun t ->
                            let e = t.saved
                            let byUser = e.closedByUser
                            let title = e.windowTitle |> Option.defaultValue ""
                            match t.outcome with
                            | SavedSession.Expired ->
                                // Not seeded and so not written back either:
                                // the entry leaves the settings file on the
                                // next save.
                                RestoreTrace.log (fun () -> sprintf "  %s rank=%d token=%X DROPPED after %.0f days title=%s"
                                                                    (if byUser then "closed" else "seed") t.rank (e.hwnd.ToInt64())
                                                                    (DateTime.Now - t.waitingSince).TotalDays title)
                            | SavedSession.Waiting ->
                                if closedTabCache.value |> List.exists (fun c -> c.closedHwnd = e.hwnd) |> not then
                                    // Through the same record the save writes
                                    // the entry back from, so that what is put
                                    // into the cache here and what leaves it at
                                    // the next save are one description and not
                                    // two that have to be kept in step by hand.
                                    let p = SavedSession.pendingOfPlanned t
                                    let info = {
                                        exePath = p.tab.exePath |> Option.defaultValue ""
                                        windowTitle = title
                                        // Everything the file held, whether or
                                        // not it may be applied. What is
                                        // dropped here is dropped for good: the
                                        // entry is written back from these
                                        // fields at the next save, so an entry
                                        // created without its alignment takes
                                        // the alignment out of the settings
                                        // file even if its window never opens
                                        // at all. Whether the name and the
                                        // colours may be put on the window that
                                        // claims it is carried separately, in
                                        // stateIsCertain.
                                        renamedTabName = p.tab.renamedTabName
                                        isPinned = p.tab.isPinned
                                        fillColor = p.tab.fillColor |> Option.bind parseColorRRGGBBAA
                                        underlineColor = p.tab.underlineColor |> Option.bind parseColorRRGGBBAA
                                        borderColor = p.tab.borderColor |> Option.bind parseColorRRGGBBAA
                                        tabAlign = p.tab.align |> Option.map tabAlignOfSavedAlign
                                        groupRef = SavedToken(g.token)
                                        tabIndex = p.rank
                                        closedHwnd = p.tab.hwnd
                                        closedAt = DateTime.Now
                                        isRestoreSeed = p.isRestoreSeed
                                        // A seed that outlives a WindowTabs
                                        // restart has to carry its position, or
                                        // the twin disambiguation and the
                                        // title-less fallback lose their
                                        // reference point.
                                        savedRect = p.tab.rect
                                        seedSince = p.tab.seedSince
                                        orderSnapshot = g.savedOrder
                                        stateIsCertain = t.stateIsCertain
                                    }
                                    RestoreTrace.log (fun () -> sprintf "  %s rank=%d token=%X unique=%b align=%A pin=%b title=%s"
                                                                        (if byUser then "closed" else "seed") t.rank (e.hwnd.ToInt64())
                                                                        t.stateIsCertain e.align e.isPinned title)
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
            DragTrace.log (fun () -> sprintf "suspendTabMonitoring (already=%b)\r\n%s" isTabMonitoringSuspendedCell.value (DragTrace.callers 6))
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
                        DragTrace.log (fun () -> "resumeTabMonitoring (delayed)")
                        this.refresh()
                    else
                        DragTrace.log (fun () -> "resumeTabMonitoring (delayed, superseded)")) |> ignore

        member x.resumeTabMonitoring() = 
            DragTrace.log (fun () -> "resumeTabMonitoring")
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
            DragTrace.log (fun () -> sprintf "Program.dragDrop: hwnd=%X" (hwnd.ToInt64()))
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
