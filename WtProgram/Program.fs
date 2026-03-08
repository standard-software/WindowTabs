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

type Program() as this =
    let version = "ss_jp_2026.03.07_next1"
    let isStandAlone = System.Diagnostics.Debugger.IsAttached

    let mutex = new Mutex(false, "BemoSoftware.WindowTabs")
    let Cell = CellScope()
    let os = OS()
    let invoker = InvokerService.invoker
    let isTabMonitoringSuspendedCell = Cell.create(false)
    let isDisabledCell = Cell.create(false)
    let isRestoringTabGroups = Cell.create(false)
    let needsRestoreOnStartup = Cell.create(false)
    let llMouseEvent = Event<_>()

    // case 727 outlook calendar items appear behind outlook main window
    let delayTabExeNames = Set2(List2(["outlook.exe"]))

    let settingsManager = Settings(isStandAlone)

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
    let isSubscribed = Cell.create(Map2<IntPtr,IDisposable>())
    let isDroppedAndAwaitingGrouping = Cell.create(Set2())
    // Track pending new window launches: process path -> (target group hwnd, invoker tab hwnd, timestamp)
    let pendingNewWindowLaunches = Cell.create(Map2<string, IntPtr * IntPtr * DateTime>())
    // Store the invoker tab hwnd consumed by tryNewWindowLaunch, for use by addWindowToGroup
    let lastNewTabInvokerHwnd = Cell.create(IntPtr.Zero)
    // Temporary storage for tab group configuration (used during disable/enable)
    let savedTabGroups = Cell.create<List2<List2<IntPtr> * string * bool * List2<IntPtr>>>(List2())
    let windowNameOverride = Cell.create(Map2())
    // Global per-HWND storage for fill color, underline color and pinned state (persists across group transfers)
    let windowFillColor = Cell.create(Map2() : Map2<IntPtr, Color>)
    let windowUnderlineColor = Cell.create(Map2() : Map2<IntPtr, Color>)
    let windowBorderColor = Cell.create(Map2() : Map2<IntPtr, Color>)
    let windowPinned = Cell.create(Set2<IntPtr>())
    let notifyNewVersionEvt = Event<_>()
    let launcher = Launcher()
   
    let isFirstRun = settingsManager.fileExists.not

    let originalVersion = 
        let original = settingsManager.settings.version
        settingsManager.update <| fun s -> { s with version = version }
        original 

    let registerShellHooks =
        os.registerShellHooks <| fun (hwnd, shellEvent) ->
            match shellEvent with
            | ShellEvent.HSHELL_WINDOWCREATED -> this.receive(ShellEvent(hwnd, shellEvent))
            | ShellEvent.HSHELL_WINDOWDESTROYED -> this.receive(ShellEvent(hwnd, shellEvent))
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
    
    member this.desktop = Services.desktop
    member this.isTabMonitoringSuspended
        with get() = isTabMonitoringSuspendedCell.value
        and set(value) = isTabMonitoringSuspendedCell.set(value)

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

    member this.updateAppWindows() =
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
            this.destroyEmptyGroups()
            this.removeUntabableWindows()

        this.exitIfNeeded()

    member this.ensureWindowIsSubscribed(window:Window) =
        let hwnd = window.hwnd
        if  isSubscribed.value.contains(hwnd).not &&
            window.pid.isCurrentProcess.not &&
            this.isAppWindowStyle(window)
            then
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
            this.addWindowToGroup(window)

    member this.destroyEmptyGroups() =
        this.desktop.groups.iter <| fun gi ->
        if gi.windows.isEmpty && launcher.isLaunching(gi).not then
            gi.destroy()

    member this.removeUntabableWindows() =
        this.desktop.groups.iter <| fun gi ->
            gi.windows.iter <| fun hwnd ->
                let window = os.windowFromHwnd(hwnd)
                // Only remove windows that are on the current virtual desktop and not tabbable
                // Don't remove windows on other virtual desktops to preserve tab groups during desktop switch
                if window.isOnCurrentVirtualDesktop && this.isTabbableWindow(window).not then
                    gi.removeWindow hwnd

    member this.findGroupForWindow(window:Window) =
        let handlers = List2([
            this.tryDropped
            launcher.findGroup
            this.tryNewWindowLaunch
            this.tryAutoGroup
            ])
        handlers.tryPick(fun f -> f(window)).def(None)

    member this.addWindowToGroup(window:Window) =
        let hwnd = window.hwnd
        let group,isNewGroup =
            match this.findGroupForWindow(window) with
            | Some(group) -> (group, false)
            | None -> (Services.desktop.createGroup(false), true)
        let isDropped = isDroppedAndAwaitingGrouping.value.contains(hwnd)
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
                    let tabs = wg.ts.lorder
                    let newTab = Tab(hwnd)
                    let invokerTab = Tab(invokerHwnd)
                    match tabs.tryFindIndex((=) invokerTab) with
                    | Some(invokerIdx) ->
                        match tabs.tryFindIndex((=) newTab) with
                        | Some(curIdx) when curIdx <> invokerIdx + 1 ->
                            wg.ts.moveTab(newTab, invokerIdx + 1)
                        | _ -> ()
                    | None -> ()
            | _ -> ()
        // For auto-grouping, position new tab next to same-exe tabs
        elif not isNewGroup && not isDropped then
            let procPath = window.pid.processPath
            match group :> obj with
            | :? GroupInfo as gi ->
                gi.invokeGroup <| fun() ->
                    let wg = gi.group
                    let tabs = wg.ts.lorder
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
                    g.removeWindow(hwnd)
                this.destroyEmptyGroups()
                this.exitIfNeeded()
                skipFullUpdate <- true
            | _ ->()
        | Timer -> ()

        if not skipFullUpdate then
            this.updateAppWindows()

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
    member this.saveTabGroupsToSettings() =
        try
            let json = settingsManager.settingsJson
            let groupsArray = JArray()
            this.desktop.groups.iter <| fun gi ->
                let windowsArray = JArray()
                gi.lorder.iter <| fun hwnd ->
                    let window = os.windowFromHwnd(hwnd)
                    if window.isWindow then
                        let windowObj = JObject()
                        // Save hwnd for group restoration
                        windowObj.setInt64("hwnd", hwnd.ToInt64())
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
                        windowsArray.Add(windowObj)
                if windowsArray.Count > 0 then
                    let groupObj = JObject()
                    groupObj.addOrUpdate("windows", windowsArray)
                    // Save per-group tab position
                    groupObj.setString("tabPosition", gi.perGroupTabPositionValue)
                    // Save per-group snap tab height margin
                    groupObj.setBool("snapTabHeightMargin", gi.snapTabHeightMargin)
                    groupsArray.Add(groupObj)
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
                                        (hwnd, obj.getString("renamedTabName"), isPinned, fillColor, underlineColor, borderColor))
                                | _ -> None)
                            |> List.ofSeq

                        // Filter to only windows that still exist
                        let matchedWindows =
                            savedWindowsList
                            |> List.filter (fun (hwnd, _, _, _, _, _) -> currentHwnds.Contains(hwnd.ToInt64()))

                        // Create group with matched windows (preserving saved order)
                        if matchedWindows.Length >= 1 then
                            let group = Services.desktop.createGroup(false)
                            matchedWindows |> List.iter (fun (hwnd, renamedTabName, isPinned, fillColor, underlineColor, borderColor) ->
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
            this.isTabMonitoringSuspended <- true

        member x.resumeTabMonitoring() = 
            this.isTabMonitoringSuspended <- false
            this.refresh()

        member x.shutdown() =
            // Save tab group configuration before shutdown
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
        member x.saveTabGroupsBeforeExit() = this.saveTabGroupsToSettings()
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
                    let pinnedHwnds = gi.lorder.where(fun hwnd -> gi.isPinned(hwnd))
                    (gi.lorder, gi.perGroupTabPositionValue, gi.snapTabHeightMargin, pinnedHwnds)
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

    interface IDesktopNotification with
        member x.dragDrop(hwnd) =
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
        
        if System.Diagnostics.Debugger.IsAttached.not then
            if mutex.WaitOne(TimeSpan.FromSeconds(0.5), false).not then
                MessageBox.Show("Another instance of WindowTabs is running, please close it before running this instance.", "WindowTabs is already running.").ignore
                exit(0)

        Application.EnableVisualStyles()

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
    let program = Program()
    program.run(List2<obj>([
        InputManagerPlugin(Set2(List2([WindowMessages.WM_MOUSEWHEEL])))
        NotifyIconPlugin()
        ExceptionHandlerPlugin()
    ]).map(fun o -> o.cast<IPlugin>()))
    0
