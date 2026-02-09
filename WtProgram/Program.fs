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

type Program() as this =
    let version = "ss_jp_2026.02.10_next_1"
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
                settingsManager.settingsJson.getBool("isDisabled").def(false)
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
    // Track pending new window launches: process path -> (target group hwnd, timestamp)
    let pendingNewWindowLaunches = Cell.create(Map2<string, IntPtr * DateTime>())
    // Temporary storage for tab group configuration (used during disable/enable)
    let savedTabGroups = Cell.create<List2<List2<IntPtr>>>(List2())
    let windowNameOverride = Cell.create(Map2())
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
        | Some((groupHwnd, timestamp)) ->
            // Remove the pending launch (only match once)
            pendingNewWindowLaunches.map(fun m -> m.remove processPath)
            // Check if the launch is still recent (within 30 seconds)
            if (DateTime.Now - timestamp).TotalSeconds < 30.0 then
                // Find the target group
                match this.desktop.groups.tryFind(fun g -> g.hwnd = groupHwnd) with
                | Some(group) -> Some(Some(group))
                | None -> None
            else
                None
        | None -> None

    member this.tryAutoGroup(window:Window) =
        if (this :> IProgram).getAutoGroupingEnabled(window.pid.processPath) then
            let hwndZorders = this.hwndZorders()
            let groups = this.desktop.groups
            let groups = 
                match this.cast<IProgram>().tabLimit with
                | Some(tabLimit) -> groups.where(fun g -> g.windows.count < tabLimit)
                | None -> groups
            let groups = groups.where(fun g-> g.windows.count > 0).sortBy(fun g -> g.windows.map(fun hwnd -> hwndZorders.tryFind(hwnd).def(Int32.MaxValue)).minBy(id))
            let group = groups.tryFind(fun g -> g.windows.map(fun hwnd -> os.windowFromHwnd(hwnd).pid.processPath).contains((=) window.pid.processPath))
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

    member this.receive message =
        match message with
        | WinEvent(hwnd, evt) -> ()
        | ShellEvent(hwnd, evt) ->
            match evt with
            | ShellEvent.HSHELL_WINDOWDESTROYED ->
                isSubscribed.value.tryFind(hwnd).iter <| fun dispose -> dispose.Dispose()
            | _ ->()
        | Timer -> ()
                  
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
                        windowsArray.Add(windowObj)
                if windowsArray.Count > 0 then
                    groupsArray.Add(windowsArray)
            json.["savedTabGroupsForRestart"] <- groupsArray
            settingsManager.settingsJson <- json
        with
        | _ -> ()

    // Restore tab groups from settings file on startup
    // Uses hwnd only for matching - simpler and more reliable
    // Includes windows on other virtual desktops (cloaked windows) for full restoration
    member this.restoreTabGroupsFromSettings() =
        try
            let json = settingsManager.settingsJson
            match json.["savedTabGroupsForRestart"] with
            | :? JArray as groupsArray when groupsArray.Count > 0 ->
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
                for groupToken in groupsArray do
                    match groupToken with
                    | :? JArray as windowsArray ->
                        // Collect saved window info (hwnd and optional renamedTabName)
                        let savedWindowsList =
                            windowsArray
                            |> Seq.choose (fun t ->
                                match t with
                                | :? JObject as obj ->
                                    obj.getIntPtr("hwnd")
                                    |> Option.map (fun hwnd -> (hwnd, obj.getString("renamedTabName")))
                                | _ -> None)
                            |> List.ofSeq

                        // Filter to only windows that still exist
                        let matchedWindows =
                            savedWindowsList
                            |> List.filter (fun (hwnd, _) -> currentHwnds.Contains(hwnd.ToInt64()))

                        // Create group with matched windows (preserving saved order)
                        if matchedWindows.Length >= 1 then
                            let group = Services.desktop.createGroup(false)
                            matchedWindows |> List.iter (fun (hwnd, renamedTabName) ->
                                group.addWindow(hwnd, false)
                                // Restore renamed tab name if exists
                                match renamedTabName with
                                | Some(name) ->
                                    windowNameOverride.set(windowNameOverride.value.add hwnd (Some(name)))
                                | None -> ()
                            )
                    | _ -> ()

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
            let hotKeys = settingsManager.settingsJson.getObject("hotKeys").def(JObject())
            match hotKeys.getInt32(key) with
            | Some(value) -> value
            | None -> 
                let shortcut, _ = hotKeyInfo.find(key)
                int(shortcut)

        member x.setHotKey key value = 
            let settings = settingsManager.settingsJson
            let hotKeys = settings.getObject("hotKeys").def(JObject())
            hotKeys.setInt32(key, value)
            settings.setObject("hotKeys", hotKeys)
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
                json.setBool("isDisabled", value)
                settingsManager.settingsJson <- json
            with
            | ex -> ()  // Ignore errors when saving settings

            if value then
                // When disabling, save current tab group configuration first
                let groupConfigs = this.desktop.groups.map <| fun gi -> gi.lorder
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
                savedTabGroups.value.iter <| fun hwnds ->
                    // Filter out windows that no longer exist or are not visible
                    let validHwnds = hwnds.where <| fun hwnd ->
                        let window = os.windowFromHwnd(hwnd)
                        window.isWindow && window.isVisibleOnScreen

                    if validHwnds.count > 0 then
                        let group = Services.desktop.createGroup(false)
                        validHwnds.iter <| fun hwnd ->
                            group.addWindow(hwnd, false)

                // Clear saved configuration
                savedTabGroups.set(List2())

                // Resume tab monitoring
                this.isTabMonitoringSuspended <- false

            this.refresh()

        member x.launchNewWindow(groupHwnd)(processPath) =
            // Register the pending launch: new window with this process path should dock to this group
            pendingNewWindowLaunches.map(fun m -> m.add processPath (groupHwnd, DateTime.Now))
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
                match json.["savedTabGroupsForRestart"] with
                | null -> false
                | :? JArray as arr -> arr.Count > 0
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
