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
    firstTabIcon: Img option
    tabHwnds: IntPtr list
}

type TabStripDecorator(group:WindowGroup, notifyDetached: IntPtr -> unit) as this =
    // Static registry for all TabStripDecorator instances
    static let mutable decorators = System.Collections.Generic.Dictionary<IntPtr, TabStripDecorator>()
    static let mutable groupInfos = System.Collections.Generic.Dictionary<IntPtr, TabGroupInfo>()

    let os = OS()
    let Cell = CellScope(false, true)
    let isDraggingCell = Cell.create(false)
    let dragInfoCell = Cell.create(None)
    let dragPtCell = Cell.create(Pt.empty)
    let dropTarget = Cell.create(None)
    let mouseEvent = Event<IntPtr * MouseButton * TabPart * MouseAction * Pt>()
    let _ts = TabStrip(this :> ITabStripMonitor)
    // Variables for double-click detection
    let lastClickTime = ref System.DateTime.MinValue
    let lastClickTab = ref None
    let doubleClickTimeoutMs = 500.0  // Windows default double-click time
    let hiddenByDoubleClick = ref false
    let doubleClickProtectUntil = ref System.DateTime.MinValue
    let firstClickTab = ref None  // Track the tab that was clicked first in potential double-click

    do this.init()

    member this.ts = _ts
    member this.group = group
    member private this.mouse = mouseEvent.Publish

    member private this.updateGroupInfo() =
        try
            // Create a snapshot of current tab information
            let tabs = this.ts.lorder

            // If no tabs remain, remove from groupInfos
            if tabs.count = 0 then
                lock groupInfos (fun () -> groupInfos.Remove(group.hwnd) |> ignore)
            else
                let tabNames =
                    tabs.list |> List.map (fun tab ->
                        let info = this.ts.tabInfo(tab)
                        info.text
                    )
                let tabHwnds =
                    tabs.list |> List.map (fun (Tab(hwnd)) -> hwnd)
                let firstTabIcon =
                    if tabs.count > 0 then
                        let firstTab = tabs.at(0)
                        let info = this.ts.tabInfo(firstTab)
                        try
                            Some(info.iconSmall.ToBitmap().img.resize(Sz(16,16)))
                        with _ ->
                            None
                    else
                        None
                let info = {
                    hwnd = group.hwnd
                    tabNames = tabNames
                    tabCount = tabs.count
                    firstTabIcon = firstTabIcon
                    tabHwnds = tabHwnds
                }
                lock groupInfos (fun () -> groupInfos.[group.hwnd] <- info)
        with _ -> ()

    member private this.init() =
        Services.registerLocal(_ts)
        group.init(this.ts)
        // Register this decorator in the global registry
        lock decorators (fun () -> decorators.[group.hwnd] <- this)

        Services.dragDrop.registerTarget(this.ts.hwnd, this:>IDragDropTarget)
    
        dropTarget.set(Some(OleDropTarget(this.ts)))
        
        this.initAutoHide()

        let capturedHwnd = ref None

        this.mouse.Add <| fun(hwnd, btn, part, action, pt) ->
            match action, btn with
            | MouseDblClick, MouseLeft ->
                // Check if tab width toggle on icon double-click is enabled
                let toggleTabWidthOnIconDoubleClick =
                    try
                        Services.settings.getValue("makeTabsNarrowerByDefault") :?> bool
                    with
                    | _ -> false

                // Check if double-click hide mode is enabled
                let autoHideDoubleClick = group.bb.read("autoHideDoubleClick", false)

                // Only process if it's the active tab
                if hwnd = group.topWindow && !firstClickTab = Some(hwnd) then
                    match part with
                    | TabIcon when toggleTabWidthOnIconDoubleClick ->
                        // Toggle tab width on icon double-click
                        group.isIconOnly <- not group.isIconOnly
                    | TabBackground when toggleTabWidthOnIconDoubleClick && group.isIconOnly ->
                        // When tabs are narrow (icon-only), allow background click to also toggle width
                        // This improves usability when tab is narrow (clicking beside icon still works)
                        group.isIconOnly <- not group.isIconOnly
                    | TabBackground when autoHideDoubleClick && this.ts.direction = TabDown && not group.isIconOnly ->
                        // Hide tabs on background double-click (only when tabs are at bottom and NOT in icon-only mode)
                        hiddenByDoubleClick := true
                        doubleClickProtectUntil := System.DateTime.Now.AddMilliseconds(300.0)
                        group.invokeAsync <| fun() ->
                            this.ts.isShrunk <- true
                    | _ when not toggleTabWidthOnIconDoubleClick && autoHideDoubleClick && this.ts.direction = TabDown && not group.isIconOnly ->
                        // When toggle feature is OFF, treat all double-clicks as background double-click
                        // Only hide if tabs are NOT in icon-only mode
                        hiddenByDoubleClick := true
                        doubleClickProtectUntil := System.DateTime.Now.AddMilliseconds(300.0)
                        group.invokeAsync <| fun() ->
                            this.ts.isShrunk <- true
                    | _ -> ()

                // Clear first click tracking after double-click
                firstClickTab := None
            | MouseUp, MouseRight ->
                let ptScreen = os.windowFromHwnd(group.hwnd).ptToScreen(pt)
                group.bb.write("contextMenuVisible", true)
                let darkModeEnabled =
                    try
                        let json = Services.settings.root
                        match json.getBool("enableMenuDarkMode") with
                        | Some(value) -> value
                        | None -> false
                    with | _ -> false
                Win32Menu.show group.hwnd ptScreen (this.contextMenu(hwnd)) darkModeEnabled
                group.bb.write("contextMenuVisible", false)
            | MouseDown, MouseLeft ->
                capturedHwnd := Some(hwnd)
                // Track the first click for double-click detection
                // Only set if it's the active tab to ensure double-click only works on already active tabs
                if hwnd = group.topWindow then
                    firstClickTab := Some(hwnd)
                else
                    firstClickTab := None
            | MouseDown, _ ->
                capturedHwnd := Some(hwnd)
            | MouseUp, MouseMiddle -> 
                capturedHwnd.Value.iter <| fun capturedHwnd ->
                    if hwnd = capturedHwnd then
                        this.onCloseWindow hwnd
            | _ -> ()
        
        group.bounds.changed.Add <| fun() ->
            this.updateTsPlacement()

        // Update placement when tab appearance changes (for height and indent values)
        Services.settings.notifyValue "tabAppearance" <| fun(_) ->
            this.invokeAsync <| fun() ->
                this.updateTsPlacement()

        group.exited.Add <| fun() ->
            Services.dragDrop.unregisterTarget(this.ts.hwnd)
    

    member private this.tabSlide =
        dragInfoCell.value.map <| fun dragInfo ->
                dragInfo.tab, dragPtCell.value.sub(dragInfo.tabOffset).x

    member private this.updateTsSlide() =
        this.ts.slide <- this.tabSlide

    member private this.updateTsPlacement() = 
        if group.bounds.value.IsNone then
            this.ts.visible <- false
        else
            this.ts.setPlacement(this.placement)
            this.ts.visible <- true
            
            // Handle UWP application tab visibility
            let hasUWPWindow = group.windows.items.any(fun hwnd ->
                let window = os.windowFromHwnd(hwnd)
                window.className = "ApplicationFrameWindow"
            )
            
            let tsWindow = os.windowFromHwnd(this.ts.hwnd)
            // Make topmost for UWP apps when a UWP app is active
            if hasUWPWindow then
                // Check if any UWP window in the group is foreground
                let isUWPForeground = group.windows.items.any(fun hwnd ->
                    let window = os.windowFromHwnd(hwnd)
                    window.className = "ApplicationFrameWindow" && hwnd = os.foreground.hwnd
                )
                if isUWPForeground then
                    tsWindow.makeTopMost()
                else
                    tsWindow.makeNotTopMost()
            else
                tsWindow.makeNotTopMost()

    member private this.invokeAsync f = group.invokeAsync f
    member private this.invokeSync f = group.invokeSync f

    member this.placement =
        let decorator =  {
            windowBounds = group.bounds.value.def(Rect())
            monitorBounds = Mon.all.map(fun m -> m.workRect)
            decoratorHeight = group.tabAppearance.tabHeight
            decoratorHeightOffset = group.tabAppearance.tabHeightOffset
            decoratorIndentFlipped = group.tabAppearance.tabIndentFlipped
            decoratorIndentNormal = group.tabAppearance.tabIndentNormal
        }
        {
            showInside = decorator.shouldShowInside
            bounds = decorator.bounds
        }

    member this.beginRename(hwnd) =
        let tab = Tab(hwnd)
        let textBounds =
            this.ts.sprite.children.pick <| fun (tabOffset, tabSprite) ->
                let tabSprite = tabSprite :?> TabSprite<Tab>
                if tabSprite.id = tab then
                    Some(Rect(tabSprite.textLocation.add(tabOffset), tabSprite.textSize))
                else None
        let verticalMargin = 2

        let form = new FloatingTextBox()

        // Find which screen contains the tab strip window
        let containingScreen = Screen.FromHandle(this.ts.hwnd)

        // Create device context for the specific display to get physical vs logical resolution
        let hdc = WinUserApi.CreateDC(containingScreen.DeviceName, null, null, IntPtr.Zero)
        let logicalWidth = WinUserApi.GetDeviceCaps(hdc, int(DeviceCap.HORZRES))
        let physicalWidth = WinUserApi.GetDeviceCaps(hdc, int(DeviceCap.DESKTOPHORZRES))
        let logicalHeight = WinUserApi.GetDeviceCaps(hdc, int(DeviceCap.VERTRES))
        let physicalHeight = WinUserApi.GetDeviceCaps(hdc, int(DeviceCap.DESKTOPVERTRES))
        WinUserApi.DeleteDC(hdc) |> ignore

        // Calculate scale factor from physical/logical ratio
        let scaleX = if logicalWidth > 0 then float(physicalWidth) / float(logicalWidth) else 1.0
        let scaleY = if logicalHeight > 0 then float(physicalHeight) / float(logicalHeight) else 1.0
        let dpiScale = (scaleX + scaleY) / 2.0

        // Scale font size according to DPI
        let baseFont = SystemFonts.MenuFont
        let scaledFontSize = baseFont.Size * float32(dpiScale)
        form.textBox.Font <- new Font(baseFont.FontFamily, scaledFontSize, baseFont.Style)

        // Convert client coordinates to screen coordinates using Win32 API
        let mutable pt = POINT(X = textBounds.location.x, Y = textBounds.location.y + verticalMargin)
        WinUserApi.ClientToScreen(this.ts.hwnd, &pt) |> ignore

        // Calculate position: display offset + (relative position × DPI scale)
        let displayOffset = Point(containingScreen.Bounds.X, containingScreen.Bounds.Y)
        let relativePos = Point(pt.X - displayOffset.X, pt.Y - displayOffset.Y)
        let scaledRelativePos = Point(
            int(float(relativePos.X) * dpiScale),
            int(float(relativePos.Y) * dpiScale)
        )
        let finalPos = Point(
            displayOffset.X + scaledRelativePos.X,
            displayOffset.Y + scaledRelativePos.Y
        )

        form.Location <- finalPos
        form.SetSize(Size(
            int(float(textBounds.size.width) * dpiScale),
            int(float(textBounds.size.height - 2 * verticalMargin) * dpiScale)
        ))
        form.textBox.KeyPress.Add <| fun e ->
            if e.KeyChar = char(Keys.Enter) then
                let newName = form.textBox.Text
                group.setTabName(hwnd, if newName.Length = 0 then None else Some(newName))
                form.Close()
            elif e.KeyChar = char(Keys.Escape) then
                form.Close()
        let tabText = this.ts.tabInfo(Tab(hwnd)).text
        form.textBox.Text <- tabText
        form.textBox.SelectionStart <- 0
        form.textBox.SelectionLength <- tabText.Length
        form.textBox.LostFocus.Add <| fun _ ->
            form.Close()
        group.bb.write("renamingTab", true)
        form.Closed.Add <| fun _ ->
            group.bb.write("renamingTab", false)
        form.Show()

    member private this.onCloseWindow hwnd =
        os.windowFromHwnd(hwnd).close()

    member private this.onCloseOtherWindows hwnd =
        group.windows.items.where((<>)hwnd).iter this.onCloseWindow

    member private this.onCloseRightTabWindows hwnd =
        let currentTab = Tab(hwnd)
        let tabIndex = this.ts.lorder.tryFindIndex((=) currentTab)
        tabIndex.iter <| fun index ->
            let rightTabs = this.ts.lorder.skip(index + 1)
            rightTabs.iter <| fun tab ->
                let (Tab(tabHwnd)) = tab
                this.onCloseWindow tabHwnd

    member private this.onCloseLeftTabWindows hwnd =
        let currentTab = Tab(hwnd)
        let tabIndex = this.ts.lorder.tryFindIndex((=) currentTab)
        tabIndex.iter <| fun index ->
            let leftTabs = this.ts.lorder.take(index)
            leftTabs.iter <| fun tab ->
                let (Tab(tabHwnd)) = tab
                this.onCloseWindow tabHwnd

    member private this.onCloseAllWindows() =
        group.windows.items.iter this.onCloseWindow

    // Helper methods for multi-display detach support
    member private this.getAllScreensSorted() =
        Screen.AllScreens
        |> Array.sortBy (fun screen -> (screen.Bounds.X, screen.Bounds.Y))

    member private this.getScreenName(screen: Screen) =
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
            match Localization.getCurrentLanguage() with
            | Localization.English -> display + " " + main
            | Localization.Japanese -> main + display
        else
            let directionStr =
                match Localization.getCurrentLanguage() with
                | Localization.English -> String.concat " " directions
                | Localization.Japanese -> String.concat "" directions
            match Localization.getCurrentLanguage() with
            | Localization.English -> display + " " + directionStr
            | Localization.Japanese -> directionStr + display

    member private this.getCurrentScreenForWindow(hwnd: IntPtr) =
        let window = os.windowFromHwnd(hwnd)
        let bounds = window.bounds
        let centerX = bounds.location.x + bounds.size.width / 2
        let centerY = bounds.location.y + bounds.size.height / 2
        let centerPoint = System.Drawing.Point(centerX, centerY)
        Screen.FromPoint(centerPoint)

    /// Calculate new position based on position option and work area
    /// Returns (x, y) tuple
    member private this.calculatePositionInWorkArea(
        position: Option<string>,
        workArea: System.Drawing.Rectangle,
        currentX: int,
        currentY: int,
        width: int,
        height: int) : (int * int) =

        match position with
        | Some "right" ->
            let x = workArea.Right - width
            let y = max workArea.Top (min currentY (workArea.Bottom - height))
            (x, y)
        | Some "left" ->
            let x = workArea.Left
            let y = max workArea.Top (min currentY (workArea.Bottom - height))
            (x, y)
        | Some "top" ->
            let x = max workArea.Left (min currentX (workArea.Right - width))
            let y = workArea.Top
            (x, y)
        | Some "bottom" ->
            let x = max workArea.Left (min currentX (workArea.Right - width))
            let y = workArea.Bottom - height
            (x, y)
        | _ ->
            (currentX, currentY)

    member private this.detachTabToScreen(hwnd: IntPtr, targetScreen: Screen, position: Option<string>) =
        // This method is only called when group has multiple tabs (menu is disabled for single tab)
        if group.windows.items.count <= 1 then
            raise (System.InvalidOperationException("detachTabToScreen should not be called when there is only one tab"))

        let window = os.windowFromHwnd(hwnd)
        let bounds = window.bounds
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
            let finalX = max targetWorkArea.Left (min newLeft (targetWorkArea.Right - newWidth))
            let finalY = max targetWorkArea.Top (min newTop (targetWorkArea.Bottom - newHeight))

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

            window.move (Rect(Pt(finalX, finalY), Sz(newWidth, newHeight)))
            notifyDetached(hwnd)

        finally
            (ThreadHelper.cancelablePostBack 200 <| fun() ->
                Services.program.resumeTabMonitoring()) |> ignore

    member private this.moveTabToGroup(hwnd: IntPtr, targetGroup: WindowGroup) =
        // Move tab to another group if it's a different group
        if targetGroup.hwnd <> group.hwnd then
            let tab = Tab(hwnd)
            let window = os.windowFromHwnd(hwnd)

            try
                // Suspend tab monitoring to prevent auto-grouping during the move
                Services.program.suspendTabMonitoring()

                try
                    // First ensure the window is not in the target group already
                    if targetGroup.windows.contains hwnd then
                        System.Diagnostics.Debug.WriteLine(sprintf "Window %A is already in target group %A, skipping move" hwnd targetGroup.hwnd)
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
                        window.hideOffScreen(None)

                        // Restore window state if necessary
                        if wasMinimized || wasMaximized then
                            window.showWindow(ShowWindowCommands.SW_RESTORE)

                        // Use synchronous invoke to ensure completion
                        let moveCompleted = ref false
                        let moveException = ref None

                        targetGroup.invokeSync(fun() ->
                            try
                                // Double-check window is not already in target group
                                if not (targetGroup.windows.contains hwnd) then
                                    targetGroup.addWindow(hwnd, false)
                                    // Show window again (target group will handle positioning)
                                    window.showWindow(ShowWindowCommands.SW_SHOW)
                                    moveCompleted := true
                                else
                                    System.Diagnostics.Debug.WriteLine(sprintf "Window %A already in target during sync, skipping" hwnd)
                            with ex ->
                                moveException := Some ex
                        )

                        // Check if move failed
                        match !moveException with
                        | Some ex ->
                            // Restore to original group on failure
                            System.Diagnostics.Debug.WriteLine(sprintf "Move failed, restoring to original group: %s" ex.Message)
                            group.addWindow(hwnd, false)
                            window.showWindow(ShowWindowCommands.SW_SHOW)
                            raise ex
                        | None when not !moveCompleted ->
                            // Move didn't complete, restore
                            System.Diagnostics.Debug.WriteLine("Move didn't complete, restoring to original group")
                            group.addWindow(hwnd, false)
                            window.showWindow(ShowWindowCommands.SW_SHOW)
                        | None ->
                            // Move successful
                            System.Diagnostics.Debug.WriteLine(sprintf "Successfully moved tab %A from group %A to group %A" hwnd group.hwnd targetGroup.hwnd)

                            // Wait a bit for UI to update
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

    member private this.detachTabToPosition(hwnd: IntPtr, position: Option<string>) =
        // This method is only called when group has multiple tabs (menu is disabled for single tab)
        if group.windows.items.count <= 1 then
            raise (System.InvalidOperationException("detachTabToPosition should not be called when there is only one tab"))

        let window = os.windowFromHwnd(hwnd)
        let bounds = window.bounds
        let screen = this.getCurrentScreenForWindow(hwnd)

        let tab = Tab(hwnd)
        Services.program.suspendTabMonitoring()

        try
            this.ts.removeTab(tab)
            group.removeWindow(hwnd)

            window.hideOffScreen(None)

            if window.isMinimized || window.isMaximized then
                window.showWindow(ShowWindowCommands.SW_RESTORE)

            window.setPositionOnly bounds.location.x bounds.location.y

            let (newX, newY) = this.calculatePositionInWorkArea(
                position,
                screen.WorkingArea,
                bounds.location.x,
                bounds.location.y,
                bounds.size.width,
                bounds.size.height)

            if newX <> bounds.location.x || newY <> bounds.location.y then
                window.setPositionOnly newX newY

            notifyDetached(hwnd)

        finally
            (ThreadHelper.cancelablePostBack 200 <| fun() ->
                Services.program.resumeTabMonitoring()) |> ignore

    member private this.moveTabGroupToPosition(hwnd: IntPtr, position: Option<string>) =
        // Move the entire tab group to the specified position
        let window = os.windowFromHwnd(hwnd)
        let bounds = window.bounds
        let screen = this.getCurrentScreenForWindow(hwnd)

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
            bounds.size.height)

        // Move the active window
        if newX <> bounds.location.x || newY <> bounds.location.y then
            window.setPositionOnly newX newY

    member private this.moveTabGroupToScreen(hwnd: IntPtr, targetScreen: Screen, position: Option<string>) =
        // Move the entire tab group to the specified screen and position
        let window = os.windowFromHwnd(hwnd)
        let bounds = window.bounds
        let sourceScreen = this.getCurrentScreenForWindow(hwnd)
        let sourceWorkArea = sourceScreen.WorkingArea

        // Calculate size percentages for DPI-aware placement
        let widthPercent = float(bounds.size.width) / float(sourceWorkArea.Width)
        let heightPercent = float(bounds.size.height) / float(sourceWorkArea.Height)
        let topPercent = float(bounds.location.y - sourceWorkArea.Top) / float(sourceWorkArea.Height)

        // Restore window if minimized or maximized
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
        let finalX = max targetWorkArea.Left (min newLeft (targetWorkArea.Right - newWidth))
        let finalY = max targetWorkArea.Top (min newTop (targetWorkArea.Bottom - newHeight))

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

        window.move (Rect(Pt(finalX, finalY), Sz(newWidth, newHeight)))

    member private  this.contextMenu(hwnd) =
        let checked(isChecked) = if isChecked then List2([MenuFlags.MF_CHECKED]) else List2()
        let grayed(isGrayed) = if isGrayed then List2([MenuFlags.MF_GRAYED]) else List2()
        let window = os.windowFromHwnd(hwnd)
        let pid = window.pid
        let exeName = pid.exeName
        let processPath = pid.processPath

        // Helper function to get alternative launch command for UWP apps
        let getAlternativeLaunchCommand(path: string) =
            let fileName = System.IO.Path.GetFileName(path).ToLowerInvariant()
            if fileName.Contains("windowsterminal") then
                Some("wt.exe")
            else
                None

        let newWindowItem =
            CmiRegular({
                text = Localization.getString("NewWindow")
                flags = List2()
                image = None
                click = fun() ->
                    try
                        // Check if this is a known UWP app and use alternative launch command
                        let launchCommand =
                            if processPath.Contains("WindowsApps") then
                                getAlternativeLaunchCommand(processPath)
                            else
                                None

                        match launchCommand with
                        | Some(cmd) ->
                            // Use alternative launch command for known UWP apps
                            Process.Start(cmd) |> ignore
                        | None ->
                            // Try normal launch
                            Process.Start(processPath) |> ignore
                    with
                    | :? System.ComponentModel.Win32Exception as ex when processPath.Contains("WindowsApps") ->
                        // UWP app that we don't have an alternative for
                        let appName = System.IO.Path.GetFileNameWithoutExtension(processPath)
                        let message =
                            match Localization.getCurrentLanguage() with
                            | Localization.Japanese ->
                                sprintf "新規ウィンドウの起動に失敗しました。\n\nこのアプリケーション (%s) はUWPアプリのため、\n直接起動できません。\n\n代わりにスタートメニューから起動してください。" appName
                            | Localization.English ->
                                sprintf "Failed to start new window.\n\nThis application (%s) is a UWP app and\ncannot be launched directly.\n\nPlease launch it from the Start menu instead." appName
                        MessageBox.Show(message, "WindowTabs", MessageBoxButtons.OK, MessageBoxIcon.Information) |> ignore
                        System.Diagnostics.Debug.WriteLine(sprintf "UWP app cannot be launched: %s - %s" processPath ex.Message)
                    | :? System.ComponentModel.Win32Exception as ex ->
                        // Non-UWP app error
                        let message = sprintf "Failed to start new window:\n%s\n\nPath: %s\nError: %s"
                                              (Localization.getString("NewWindow"))
                                              processPath
                                              ex.Message
                        MessageBox.Show(message, "WindowTabs Error", MessageBoxButtons.OK, MessageBoxIcon.Error) |> ignore
                        System.Diagnostics.Debug.WriteLine(sprintf "Error starting process: %s - %s" processPath ex.Message)
                    | ex ->
                        let message = sprintf "Unexpected error starting new window:\n%s" ex.Message
                        MessageBox.Show(message, "WindowTabs Error", MessageBoxButtons.OK, MessageBoxIcon.Error) |> ignore
                        System.Diagnostics.Debug.WriteLine(sprintf "Unexpected error starting process: %s - %s" processPath ex.Message)
            })

        
        let makeTabsWiderItem =
            CmiRegular({
                text = Localization.getString("MakeTabsWider")
                image = None
                flags = List2()
                click = fun() ->
                    group.isIconOnly <- false
            })

        let makeTabsNarrowerItem =
            CmiRegular({
                text = Localization.getString("MakeTabsNarrower")
                image = None
                flags = List2()
                click = fun() ->
                    group.isIconOnly <- true
            })

        let renameTabItem =
            CmiRegular({
                text = Localization.getString("RenameTab")
                image = None
                flags = List2()
                click = fun() ->
                    this.beginRename(hwnd)
            })
        let restoreTabNameItem =
            CmiRegular({
                text = Localization.getString("RestoreTabName")
                image = None
                click = fun() -> group.setTabName(hwnd, None)
                flags = List2()
            })

                 
        let closeTabItem = 
            let tabText = this.ts.tabInfo(Tab(hwnd)).text
            let displayText = 
                if tabText.Length <= 9 then
                    tabText
                else
                    tabText.Substring(0, 9) + "..."
            CmiRegular({
                text = sprintf "%s(%s)" (Localization.getString("CloseTab")) displayText
                image = None
                click = fun() -> this.onCloseWindow hwnd
                flags = List2()
            })

        let closeRightTabsItem =
            let currentTab = Tab(hwnd)
            let tabIndex = this.ts.lorder.tryFindIndex((=) currentTab)
            let rightTabCount = 
                tabIndex |> Option.map(fun index -> this.ts.lorder.count - index - 1) |> Option.defaultValue 0
            let displayText = 
                if rightTabCount = 0 then
                    Localization.getString("CloseTabsToTheRight")
                else
                    let formatKey = "CloseTabsToTheRightFormat"
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
                flags = grayed(rightTabCount = 0)
            })

        let closeLeftTabsItem =
            let currentTab = Tab(hwnd)
            let tabIndex = this.ts.lorder.tryFindIndex((=) currentTab)
            let leftTabCount = 
                tabIndex |> Option.defaultValue 0
            let displayText = 
                if leftTabCount = 0 then
                    Localization.getString("CloseTabsToTheLeft")
                else
                    let formatKey = "CloseTabsToTheLeftFormat"
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
                flags = grayed(leftTabCount = 0)
            })

        let closeOtherTabsItem =
            CmiRegular({
                text = Localization.getString("CloseOtherTabs")
                image = None
                click = fun() -> this.onCloseOtherWindows hwnd
                flags = List2()
            })

        let closeAllTabsItem =
            CmiRegular({
                text = Localization.getString("CloseAllTabs")
                image = None
                click = fun() -> this.onCloseAllWindows()
                flags = List2()
            })

        let managerItem =
            CmiRegular({
                text = Localization.getString("SettingsMenu")
                image = None
                click = fun() -> Services.managerView.show()
                flags = List2()
            })

        let detachTabSubMenu =
            let isEnabled = group.zorder.value.length > 1  // Only enabled when there are 2+ tabs
            let allScreens = this.getAllScreensSorted()
            let currentScreen = this.getCurrentScreenForWindow(hwnd)

            let baseMenuItems = [
                CmiRegular({
                    text = Localization.getString("DetachTabSamePosition")
                    image = None
                    click = fun() -> this.detachTabToPosition(hwnd, None)
                    flags = List2()
                })
                CmiRegular({
                    text = Localization.getString("DetachTabMoveRight")
                    image = None
                    click = fun() -> this.detachTabToPosition(hwnd, Some "right")
                    flags = List2()
                })
                CmiRegular({
                    text = Localization.getString("DetachTabMoveLeft")
                    image = None
                    click = fun() -> this.detachTabToPosition(hwnd, Some "left")
                    flags = List2()
                })
                CmiRegular({
                    text = Localization.getString("DetachTabMoveTop")
                    image = None
                    click = fun() -> this.detachTabToPosition(hwnd, Some "top")
                    flags = List2()
                })
                CmiRegular({
                    text = Localization.getString("DetachTabMoveBottom")
                    image = None
                    click = fun() -> this.detachTabToPosition(hwnd, Some "bottom")
                    flags = List2()
                })
            ]

            let menuItems =
                if allScreens.Length > 1 then
                    let screenSubMenus =
                        allScreens
                        |> Array.map (fun screen ->
                            let screenName = this.getScreenName(screen)
                            let isCurrentScreen = screen.Equals(currentScreen)

                            let screenItems = [
                                CmiRegular({
                                    text = Localization.getString("DetachTabSamePosition")
                                    image = None
                                    click = fun() -> this.detachTabToScreen(hwnd, screen, None)
                                    flags = List2()
                                })
                                CmiRegular({
                                    text = Localization.getString("DetachTabMoveRight")
                                    image = None
                                    click = fun() -> this.detachTabToScreen(hwnd, screen, Some "right")
                                    flags = List2()
                                })
                                CmiRegular({
                                    text = Localization.getString("DetachTabMoveLeft")
                                    image = None
                                    click = fun() -> this.detachTabToScreen(hwnd, screen, Some "left")
                                    flags = List2()
                                })
                                CmiRegular({
                                    text = Localization.getString("DetachTabMoveTop")
                                    image = None
                                    click = fun() -> this.detachTabToScreen(hwnd, screen, Some "top")
                                    flags = List2()
                                })
                                CmiRegular({
                                    text = Localization.getString("DetachTabMoveBottom")
                                    image = None
                                    click = fun() -> this.detachTabToScreen(hwnd, screen, Some "bottom")
                                    flags = List2()
                                })
                            ]

                            CmiPopUp({
                                text = screenName
                                image = None
                                items = List2(screenItems)
                                flags = if isCurrentScreen then List2([MenuFlags.MF_GRAYED]) else List2()
                            })
                        )
                        |> Array.toList

                    baseMenuItems @ [CmiSeparator] @ screenSubMenus
                else
                    baseMenuItems

            Some(CmiPopUp({
                text = Localization.getString("DetachTab")
                image = None
                items = List2(menuItems)
                flags = if isEnabled then List2() else List2([MenuFlags.MF_GRAYED])
            }))

        let moveTabGroupSubMenu =
            let allScreens = this.getAllScreensSorted()
            let currentScreen = this.getCurrentScreenForWindow(hwnd)

            let baseMenuItems = [
                CmiRegular({
                    text = Localization.getString("DetachTabMoveRight")
                    image = None
                    click = fun() -> this.moveTabGroupToPosition(hwnd, Some "right")
                    flags = List2()
                })
                CmiRegular({
                    text = Localization.getString("DetachTabMoveLeft")
                    image = None
                    click = fun() -> this.moveTabGroupToPosition(hwnd, Some "left")
                    flags = List2()
                })
                CmiRegular({
                    text = Localization.getString("DetachTabMoveTop")
                    image = None
                    click = fun() -> this.moveTabGroupToPosition(hwnd, Some "top")
                    flags = List2()
                })
                CmiRegular({
                    text = Localization.getString("DetachTabMoveBottom")
                    image = None
                    click = fun() -> this.moveTabGroupToPosition(hwnd, Some "bottom")
                    flags = List2()
                })
            ]

            let menuItems =
                if allScreens.Length > 1 then
                    let screenSubMenus =
                        allScreens
                        |> Array.map (fun screen ->
                            let screenName = this.getScreenName(screen)
                            let isCurrentScreen = screen.Equals(currentScreen)

                            let screenItems = [
                                CmiRegular({
                                    text = Localization.getString("DetachTabMoveRight")
                                    image = None
                                    click = fun() -> this.moveTabGroupToScreen(hwnd, screen, Some "right")
                                    flags = List2()
                                })
                                CmiRegular({
                                    text = Localization.getString("DetachTabMoveLeft")
                                    image = None
                                    click = fun() -> this.moveTabGroupToScreen(hwnd, screen, Some "left")
                                    flags = List2()
                                })
                                CmiRegular({
                                    text = Localization.getString("DetachTabMoveTop")
                                    image = None
                                    click = fun() -> this.moveTabGroupToScreen(hwnd, screen, Some "top")
                                    flags = List2()
                                })
                                CmiRegular({
                                    text = Localization.getString("DetachTabMoveBottom")
                                    image = None
                                    click = fun() -> this.moveTabGroupToScreen(hwnd, screen, Some "bottom")
                                    flags = List2()
                                })
                            ]

                            CmiPopUp({
                                text = screenName
                                image = None
                                items = List2(screenItems)
                                flags = if isCurrentScreen then List2([MenuFlags.MF_GRAYED]) else List2()
                            })
                        )
                        |> Array.toList

                    baseMenuItems @ [CmiSeparator] @ screenSubMenus
                else
                    baseMenuItems

            Some(CmiPopUp({
                text = Localization.getString("MoveTabGroup")
                image = None
                items = List2(menuItems)
                flags = List2()
            }))

        List2([
            Some(newWindowItem)
            Some(CmiSeparator)
            Some(closeTabItem)
            Some(closeRightTabsItem)
            Some(closeLeftTabsItem)
            Some(closeOtherTabsItem)
            Some(closeAllTabsItem)
            Some(CmiSeparator)
            (if group.isIconOnly then Some(makeTabsWiderItem) else Some(makeTabsNarrowerItem))
            Some(renameTabItem)
            (if group.isRenamed(hwnd) then Some(restoreTabNameItem) else None)
            Some(CmiSeparator)
            // Add "Move tab" menu - use static group infos for thread-safe access
            (
                // Update all group infos before building menu
                let allDecorators = lock decorators (fun () ->
                    decorators.Values
                    |> List.ofSeq
                    |> List.filter (fun d ->
                        // Filter out invalid decorators
                        try
                            d.ts.hwnd <> IntPtr.Zero &&
                            WinUserApi.IsWindow(d.group.hwnd) &&
                            WinUserApi.IsWindow(d.ts.hwnd)
                        with _ -> false
                    )
                )

                // First, update the current group's info synchronously
                this.updateGroupInfo()

                // Update all other decorators' group info and wait for completion
                let updateTasks =
                    allDecorators
                    |> List.filter (fun d -> d.group.hwnd <> group.hwnd)  // Skip current group (already updated)
                    |> List.map (fun d ->
                        async {
                            try
                                // Double check the window is still valid
                                if WinUserApi.IsWindow(d.group.hwnd) && WinUserApi.IsWindow(d.ts.hwnd) then
                                    d.group.invokeSync(fun () -> d.updateGroupInfo())
                            with _ -> ()
                        }
                    )

                // Wait for all updates to complete
                updateTasks |> Async.Parallel |> Async.RunSynchronously |> ignore

                // Now get the updated group infos
                let allGroupInfos = lock groupInfos (fun () ->
                    groupInfos.Values
                    |> List.ofSeq
                    |> List.filter (fun info ->
                        info.hwnd <> group.hwnd && // Not current group
                        info.tabCount > 0 && // Has at least one tab
                        // Don't show groups that contain the tab we're moving
                        not (info.tabHwnds |> List.contains hwnd)
                    )
                )

                if not (List.isEmpty allGroupInfos) then
                    // Build menu items for each other group
                    // Use distinctBy to prevent duplicates
                    let uniqueGroupInfos = allGroupInfos |> List.distinctBy (fun info -> info.hwnd)
                    let menuItems =
                        uniqueGroupInfos
                        |> List.choose (fun info ->
                            try
                                // Get the decorator for this group to handle the click
                                let targetDecorator = lock decorators (fun () ->
                                    decorators.Values |> Seq.tryFind (fun d ->
                                        d.group.hwnd = info.hwnd &&
                                        WinUserApi.IsWindow(d.group.hwnd) &&
                                        WinUserApi.IsWindow(d.ts.hwnd)
                                    )
                                )

                                // Only create menu item if we have a valid decorator
                                match targetDecorator with
                                | Some decorator ->
                                    // Build menu text with tab names
                                    let fullNameString =
                                        if info.tabCount = 1 then
                                            // Single tab: show first 22 chars
                                            let tabName = info.tabNames |> List.head
                                            if tabName.Length > 22 then
                                                tabName.Substring(0, 22) + "..."
                                            else
                                                tabName
                                        elif info.tabCount = 2 then
                                            // 2 tabs: show 9 chars each
                                            let tabNames = info.tabNames |> List.take 2
                                            let truncatedNames = tabNames |> List.map (fun name ->
                                                if name.Length > 9 then
                                                    name.Substring(0, 9) + "..."
                                                else
                                                    name
                                            )
                                            String.Join(" ", truncatedNames)
                                        else
                                            // 3+ tabs: show first 3 tabs with 5 chars each
                                            let tabNames = info.tabNames |> List.take (min 3 info.tabCount)
                                            let truncatedNames = tabNames |> List.map (fun name ->
                                                if name.Length > 5 then
                                                    name.Substring(0, 5) + "..."
                                                else
                                                    name
                                            )
                                            let nameString = String.Join(" ", truncatedNames)
                                            // For 4+ tabs, still show only 3 tab names
                                            if info.tabCount > 3 then
                                                nameString + "..."
                                            else
                                                nameString

                                    // Use same pattern as CloseTabsToTheRight
                                    let formatString = Localization.getString("MoveTabGroupFormat")
                                    let tabWord =
                                        if info.tabCount = 1 then
                                            Localization.getString("TabSingular")
                                        else
                                            Localization.getString("TabPlural")
                                    let menuText = String.Format(formatString, info.tabCount, tabWord, fullNameString)

                                    Some(CmiRegular({
                                        text = menuText
                                        image = info.firstTabIcon
                                        click = fun() ->
                                            // Move the tab to the target group
                                            this.moveTabToGroup(hwnd, decorator.group)
                                        flags = List2()
                                    }))
                                | None ->
                                    // No valid decorator found, skip this item
                                    None
                            with ex ->
                                System.Diagnostics.Debug.WriteLine(sprintf "Exception in menu item creation: %s" ex.Message)
                                None
                        )

                    Some(CmiPopUp({
                        text = Localization.getString("MoveTab")
                        image = None
                        items = List2(menuItems)
                        flags = List2()
                    }))
                else
                    // No other groups available
                    None
            )
            detachTabSubMenu
            moveTabGroupSubMenu
            Some(CmiSeparator)
            Some(managerItem)
        ]).choose(id)

    member private this.initAutoHide() =
        let callbackRef = ref None
        // Create a cell that tracks whether tabs are shown inside
        let isWindowInside = Cell.create(false)
        let isMouseOver = Cell.import(group.isMouseOver)
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
        let isRecentlyChangedZorderCell =
            let cell = Cell.create(false)
            let cbRef = ref None
            group.zorder.changed.Add <| fun() ->
                cell.value <- true
                cbRef.Value.iter <| fun(d:IDisposable) -> d.Dispose()
                cbRef := Some(ThreadHelper.cancelablePostBack hideDelayCell.value <| fun() ->
                    cell.value <- false
                )
            cell
        // Create a function to handle the auto-hide logic
        let updateAutoHide() =
            // Update isWindowInside based on current tab position
            isWindowInside.value <- this.ts.showInside
            
            // Handle double-click mode separately
            if autoHideDoubleClickCell.value && this.ts.direction = TabDown then
                
                // Check if protection period has expired
                let protectionExpired = System.DateTime.Now > !doubleClickProtectUntil
                
                // In double-click mode, only show tabs when mouse is over and hidden
                if this.ts.isShrunk && isMouseOver.value && not isDraggingCell.value then
                    // Check if we should show tabs (protection period expired OR mouse left and returned)
                    if not !hiddenByDoubleClick || protectionExpired then
                        if protectionExpired && !hiddenByDoubleClick then
                            hiddenByDoubleClick := false
                        if not !hiddenByDoubleClick then
                            this.ts.isShrunk <- false
                // Clear the flag when mouse leaves
                elif not isMouseOver.value then
                    if !hiddenByDoubleClick && protectionExpired then
                        hiddenByDoubleClick := false
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
                    callbackRef := Some(ThreadHelper.cancelablePostBack hideDelayCell.value <| fun() ->
                        this.ts.isShrunk <- true
                    )
                else
                    this.ts.isShrunk <- false
        
        // Listen for changes to trigger auto-hide update
        Cell.listen updateAutoHide
        
        // When hideDelayCell value changes via notifyValue, restart timer if running
        Services.settings.notifyValue "hideTabsDelayMilliseconds" <| fun value ->
            group.invokeAsync <| fun() ->
                hideDelayCell.value <- 
                    try
                        value :?> int
                    with
                    | _ -> 3000
                // If a timer is running, restart it with the new delay
                if callbackRef.Value.IsSome then
                    updateAutoHide()

    interface ITabStripMonitor with
        member x.tabClick((btn, tab, part, action, pt)) =
            let (Tab(hwnd)) = tab
            mouseEvent.Trigger(hwnd, btn, part, action, pt)
            let ptScreen = os.windowFromHwnd(this.ts.hwnd).ptToScreen(pt)
            match action with
            | MouseDown ->
                group.flashTab(tab, false)
                match btn with
                | MouseRight ->
                    os.windowFromHwnd(group.topWindow).setForeground(false)
                | MouseLeft ->
                    group.tabActivate(tab, false)
                    if part <> TabClose then
                        let tabInfo = this.ts.tabInfo(tab)
                        let originalTabLocation = this.ts.tabLocation tab
                        let clickOffsetInTab = pt.sub(originalTabLocation)

                        // Calculate scaled offset for preview image (always needed when dragging to different location)
                        let dragTabLoc = this.ts.dragTabLocation tab
                        let previewWidth = tabInfo.preview().width
                        let originalWidth = this.ts.bounds.width
                        let scaleRatio = float(previewWidth) / float(originalWidth)
                        let scaledClickOffset = Pt(int(float(clickOffsetInTab.x) * scaleRatio), clickOffsetInTab.y)
                        let imageOffset = dragTabLoc.add(scaledClickOffset)

                        // For tab reordering within same group, use unscaled click offset
                        let tabOffset = clickOffsetInTab

                        let dragImage = fun() -> this.ts.dragImage(tab)
                        let dragInfo = box({ tab = tab; tabOffset = tabOffset; imageOffset = imageOffset; tabInfo = tabInfo})
                        Services.dragDrop.beginDrag(this.ts.hwnd, dragImage, imageOffset, ptScreen, dragInfo)
                | MouseMiddle ->
                    group.tabActivate(tab, false)
            | _ -> ()
    
        member x.tabActivate((tab)) =
            group.tabActivate(tab, false)

        member x.tabMoved(Tab(hwnd), index) =
            group.onTabMoved(hwnd, index)

        member x.tabClose(Tab(hwnd)) =
            // Get all tabs and current tab index before closing
            let currentTab = Tab(hwnd)
            let allTabs = this.ts.lorder
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
            ()
            
    interface IDragDropTarget with
        member this.dragBegin() =
            this.invokeAsync <| fun() -> 
                isDraggingCell.value <- true
                this.ts.transparent <- false
                
        member this.dragEnter dragInfo pt =
            this.invokeSync <| fun() -> 
                let dragInfo = dragInfo :?> TabDragInfo
                let (Tab(hwnd)) = dragInfo.tab
                let result = 
                    if this.ts.tabs.contains(dragInfo.tab) &&
                        this.ts.tabs.count = 1 then
                        dragPtCell.set(pt)
                        dragInfoCell.set(Some(dragInfo))
                        false
                    else 
                        dragPtCell.set(pt)
                        dragInfoCell.set(Some(dragInfo))
                        this.ts.addTabSlide dragInfo.tab this.tabSlide
                        this.ts.setTabInfo(dragInfo.tab, dragInfo.tabInfo)
                        group.addWindow(hwnd, false)
                        true
                this.updateTsSlide()
                result

        member this.dragMove(pt) =
            this.invokeSync <| fun() ->
                dragPtCell.set(pt)
                this.updateTsSlide()

        member this.dragExit() =
            this.invokeSync <| fun() ->
                match dragInfoCell.value with
                | Some(dragInfo) ->
                    let tab = dragInfo.tab
                    if this.ts.tabs.contains(tab) then
                        this.ts.removeTab(tab)
                        dragInfoCell.set(None)       
                        let (Tab(hwnd)) = tab
                        let window = os.windowFromHwnd(hwnd)
                        group.removeWindow(hwnd)
                        window.hideOffScreen(None)
                | None -> ()
                this.updateTsSlide()

        member this.dragEnd() =
            this.invokeAsync <| fun() ->
                isDraggingCell.value <- false
                this.ts.transparent <- true
                match this.ts.movedTab with
                | Some(tab, index) ->
                    this.ts.moveTab(tab, index)
                | None -> ()
                dragInfoCell.set(None)
                this.updateTsSlide()

    interface IDisposable with
        member this.Dispose() =
            // Unregister from the global registry
            lock decorators (fun () -> decorators.Remove(group.hwnd) |> ignore)
            lock groupInfos (fun () -> groupInfos.Remove(group.hwnd) |> ignore)
