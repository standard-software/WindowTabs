namespace Bemo
open System

type FilterService() as this =
    let os = OS()
    let blackListedExeNames = Set2(List2(["taskmgr.exe"]))

    member this.includedPaths 
        with get() = Services.settings.getValue("includedPaths").cast<Set2<string>>()
        and set(value) = Services.settings.setValue("includedPaths", box(value))

     member this.excludedPaths 
        with get() = Services.settings.getValue("excludedPaths").cast<Set2<string>>()
        and set(value) = Services.settings.setValue("excludedPaths", box(value))
           
    member this.isTabbingEnabledForAllProcessesByDefault 
        with get() = Services.settings.getValue("enableTabbingByDefault").cast<bool>()
        and set(value) = 
            Services.settings.setValue("enableTabbingByDefault", box(value))
            Services.program.refresh().ignore
            // The set of tabbed applications has changed wholesale; whatever
            // has dropped out of it leaves no closed tabs behind.
            Services.program.forgetClosedTabsOfUntabbedApps()

    member this.isBanned (window:Window) =
        blackListedExeNames.contains(window.pid.exeName)

    member this.isAppWindowStyle (window:Window) = 
        let style = IntPtr( 
            WindowsStyles.WS_OVERLAPPEDWINDOW &&&
            ~~~WindowsStyles.WS_CAPTION &&&
            ~~~WindowsStyles.WS_THICKFRAME &&&
            ~~~WindowsStyles.WS_SYSMENU
        )
        Win32Helper.IntPtrAnd(window.styleEx, IntPtr(WindowsExtendedStyles.WS_EX_TOOLWINDOW)) = IntPtr.Zero &&
        Win32Helper.IntPtrAnd(window.style, style) = style
    
    member this.isAppWindow (window:Window) =
        let tests = List2([
            fun() -> window.pid.canQueryProcess
            fun() -> this.isAppWindowStyle(window)
            fun() -> window.pid.isCurrentProcess.not
            fun() -> window.isWindow
            fun() -> window.isVisibleOnScreen
            fun() -> this.isValidOwner(window)
            //Win32 Dialogue class
            fun() -> window.className <> "#32770"
            //ApplicationFrameWindow check for modern Windows apps/Explorer
            fun() -> window.className <> "ApplicationFrameWindow" || not(String.IsNullOrEmpty window.text)
            fun() -> this.isBanned(window).not
            fun() -> this.isOnScreenOrMinimized(window)
            ])
        tests.all(fun pred -> pred()) 

    member this.isValidOwner (window:Window) =
        let owner = window.parent
        if owner.hwnd = IntPtr.Zero then true
        // Delphi and MFC have owner windows w/ zero size.
        else owner.bounds.width = 0

    member this.isOnScreenOrMinimized(window:Window) =
        // Rectangle arithmetic rather than a GDI region: this is asked for
        // every window on every pass, and the regions were freed only by the
        // garbage collector.
        window.isMinimized ||
        (let bounds = window.bounds
         Mon.all.any(fun mon ->
            let screen = mon.displayRect
            min screen.right bounds.right > max screen.left bounds.left &&
            min screen.bottom bounds.bottom > max screen.top bounds.top))
    
    // Asked by application, not by path: a Store application's path carries
    // its version and changes under us on every update. See AppPath.
    member this.getIsTabbingEnabledForProcess(processPath) =
        if this.isTabbingEnabledForAllProcessesByDefault then
            (AppPath.containsApp this.excludedPaths.items.list processPath).not
        else
            AppPath.containsApp this.includedPaths.items.list processPath

    member this.isTabbableWindow(window:Window) = 
        this.getIsTabbingEnabledForProcess(window.pid.processPath) && this.isAppWindow(window)

    interface IFilterService with
        
        member x.isAppWindow(hwnd) =
            let window = os.windowFromHwnd(hwnd)
            this.isAppWindow(window)

        member x.isAppWindowStyle(hwnd) =
            let window = os.windowFromHwnd(hwnd)
            this.isAppWindowStyle(window)

        member x.isTabbableWindow(hwnd) =
            let window = os.windowFromHwnd(hwnd)
            this.isTabbableWindow(window)

        member x.isTabbingEnabledForAllProcessesByDefault
            with get() = this.isTabbingEnabledForAllProcessesByDefault
            and set(value) = this.isTabbingEnabledForAllProcessesByDefault <- value

        member x.setIsTabbingEnabledForProcess processPath enabled = 
            // add/remove work on the application, so setting a Store app
            // replaces the entry left by its previous version instead of
            // adding a second one beside it.
            let setTo (paths: Set2<string>) enable =
                Set2(List2(
                    if enable then AppPath.addApp paths.items.list processPath
                    else AppPath.removeApp paths.items.list processPath))
            if this.isTabbingEnabledForAllProcessesByDefault then
                this.excludedPaths <- setTo this.excludedPaths (not enabled)
            else
                this.includedPaths <- setTo this.includedPaths enabled
                
            Services.program.refresh().ignore
            // Switching an application off means it has no tabs, not that its
            // tabs were closed: its closed-tab records go with it.
            Services.program.forgetClosedTabsOfUntabbedApps()

        member x.getIsTabbingEnabledForProcess(processPath) = 
            this.getIsTabbingEnabledForProcess(processPath)

