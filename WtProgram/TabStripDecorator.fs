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
    let hiddenByDoubleClick = ref false
    let doubleClickProtectUntil = ref System.DateTime.MinValue
    let firstClickTab = ref None  // Track the tab that was clicked first in potential double-click

    // Explorer-like selection: was the MouseDown'd tab already part of the
    // selection (or the active tab)? If yes, MouseUp / dragEnd without drag
    // reduces the selection to just the clicked tab (clears the multi-select).
    let mouseDownOnSelected : bool ref = ref false
    // Drag origin tab — set on MouseDown so dragBegin can compute the
    // contiguous selected range and set ts.dragGroup before slide rendering
    // starts. Cleared in dragEnd.
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
                // Double-click always opens rename UI for the active tab
                if hwnd = group.topWindow && !firstClickTab = Some(hwnd) then
                    this.beginRename(hwnd)

                // Clear first click tracking after double-click
                firstClickTab := None
            | MouseUp, MouseLeft ->
                // Hide tabs when clicking the icon of the active tab (if icon-click hide mode is enabled)
                capturedHwnd.Value.iter <| fun captured ->
                    if hwnd = captured && hwnd = group.topWindow && part = TabIcon then
                        let autoHideIconClick = group.bb.read("autoHideDoubleClick", false)
                        if autoHideIconClick then
                            hiddenByDoubleClick := true
                            doubleClickProtectUntil := System.DateTime.Now.AddMilliseconds(300.0)
                            group.invokeAsync <| fun() ->
                                this.ts.isShrunk <- true
            | MouseUp, MouseRight ->
                let ptScreen = os.windowFromHwnd(group.hwnd).ptToScreen(pt)
                group.bb.write("contextMenuVisible", true)
                let darkModeEnabled =
                    try
                        let json = Services.settings.root
                        match json.getBool("EnableDarkMode") with
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
            // Check if tabs should be hidden due to fullscreen window
            let hideForFullscreen =
                try
                    let hideTabsOnFullscreen = Services.settings.getValue("hideTabsOnFullscreen") :?> bool
                    hideTabsOnFullscreen && group.isFullscreen.value
                with _ -> false
            this.ts.visible <- not hideForFullscreen
            
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

        // The rename box is a DPI-unaware WinForms form, like the rest of the
        // app, so the OS already scales its logical bounds to physical on
        // display. The strip draws each tab's text with SystemFonts.MenuFont at
        // these same logical client coordinates, so the box must use the
        // UNSCALED logical position, size and font to line up. The previous code
        // multiplied all three by the DPI scale, which the OS then scaled again -
        // placing the box at pt*2.25 instead of pt*1.5, a rightward offset that
        // grew with the tab's distance from the monitor's left edge.
        form.textBox.Font <- SystemFonts.MenuFont

        // Convert the tab's text client position to screen coordinates.
        let mutable pt = POINT(X = textBounds.location.x, Y = textBounds.location.y + verticalMargin)
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

    // Multi-select drag-reorder: from the drag-origin tab, expand left and
    // right through visualOrder as long as the next tab is selected or
    // active, AND shares the same pin state AND same alignment. Used by
    // dragBegin to set the visual drag group and by dragEnd to splice-move
    // the whole range.
    // Variant A (-4): pin + alignment boundary, with synchronized visual
    // slide of all group members.
    member private this.contiguousSelectedFromTab(origin: Tab) : Tab list =
        let activeHwnd = group.topWindow
        let inSelection (Tab h) =
            h = activeHwnd || group.selectedTabs.contains h
        if not (inSelection origin) then [origin]
        else
            let samePinAlign (a: Tab) (b: Tab) =
                this.ts.isPinned a = this.ts.isPinned b &&
                this.ts.getTabAlign a = this.ts.getTabAlign b
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
        group.windows.items.where((<>)hwnd).iter this.onCloseWindow

    member private this.onCloseRightTabWindows hwnd =
        let currentTab = Tab(hwnd)
        let vo = this.ts.visualOrder
        let tabIndex = vo.tryFindIndex((=) currentTab)
        tabIndex.iter <| fun index ->
            let rightTabs = vo.skip(index + 1)
            rightTabs.iter <| fun tab ->
                let (Tab(tabHwnd)) = tab
                this.onCloseWindow tabHwnd

    member private this.onCloseLeftTabWindows hwnd =
        let currentTab = Tab(hwnd)
        let vo = this.ts.visualOrder
        let tabIndex = vo.tryFindIndex((=) currentTab)
        tabIndex.iter <| fun index ->
            let leftTabs = vo.take(index)
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

        let tabOffset = this.getTabHeightForSnap()
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
            let y = workArea.Top + tabOffset
            (x, y)
        | Some "bottom" ->
            let x = max workArea.Left (min currentX (workArea.Right - width))
            let y = workArea.Bottom - height
            (x, y)
        // Corner positions
        | Some "topright" ->
            let x = workArea.Right - width
            let y = workArea.Top + tabOffset
            (x, y)
        | Some "topleft" ->
            let x = workArea.Left
            let y = workArea.Top + tabOffset
            (x, y)
        | Some "bottomright" ->
            let x = workArea.Right - width
            let y = workArea.Bottom - height
            (x, y)
        | Some "bottomleft" ->
            let x = workArea.Left
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
                                    // Inherit the destination group's last-tab alignment
                                    // so a fully left-aligned group stays all-left and any
                                    // right-aligned tab puts the joiner at the right, regardless
                                    // of the joiner's previous per-tab alignment.
                                    // Then splice the joiner to the rightmost slot: normalize is
                                    // a stable sort, so when the joiner ends up in the same zone
                                    // as the existing tabs (e.g. all-left group joining all-right)
                                    // it would otherwise stay at its original index inside that
                                    // zone instead of landing at the visual end.
                                    let newTab = Tab(hwnd)
                                    let others = targetGroup.ts.visualOrder.where(fun t -> t <> newTab)
                                    match others.list |> List.tryLast with
                                    | Some(lastTab) ->
                                        let lastAlign = targetGroup.ts.getTabAlign(lastTab)
                                        targetGroup.setTabAlign(hwnd, lastAlign)
                                        let endIndex = targetGroup.ts.visualOrder.list.Length
                                        targetGroup.ts.moveTab(newTab, endIndex)
                                    | None -> ()
                                    // Show window again (target group will handle positioning)
                                    window.showWindow(ShowWindowCommands.SW_SHOW)
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
                            window.showWindow(ShowWindowCommands.SW_SHOW)
                            raise ex
                        | None when not !moveCompleted ->
                            // Move didn't complete, restore
                            group.addWindow(hwnd, false)
                            window.showWindow(ShowWindowCommands.SW_SHOW)
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

    member private this.moveTabGroupToGroup(targetGroup: WindowGroup) =
        // Move all tabs from current group to target group
        if targetGroup.hwnd <> group.hwnd then
            try
                // Suspend tab monitoring to prevent auto-grouping during the move
                Services.program.suspendTabMonitoring()

                try
                    // Get all tabs in current group in VISUAL order (left-to-right).
                    // group.windows is a Set with no insertion order, so iterating it
                    // would scramble the source tabs in the destination group.
                    let tabsToMove = group.visualOrder.list

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
            applyToSeedWindow: System.Windows.Forms.Screen -> Rect -> unit
        ) =
        match tabsInOrder with
        | [] | [_] -> ()
        | firstHwnd :: remainingHwnds ->
            let window = os.windowFromHwnd(firstHwnd)
            let bounds = window.bounds
            let screen = this.getCurrentScreenForWindow(firstHwnd)

            // Step 1: detach seed tab from this group; place / snap window.
            Services.program.suspendTabMonitoring()
            try
                this.ts.removeTab(Tab(firstHwnd))
                group.removeWindow(firstHwnd)

                window.hideOffScreen(None)

                if window.isMinimized || window.isMaximized then
                    window.showWindow(ShowWindowCommands.SW_RESTORE)

                applyToSeedWindow screen bounds

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
            this.detachTabsCommon(tabsInOrder, fun screen bounds ->
                let window = os.windowFromHwnd(List.head tabsInOrder)
                window.setPositionOnly bounds.location.x bounds.location.y
                let (newX, newY) = this.calculatePositionInWorkArea(
                    position, screen.WorkingArea,
                    bounds.location.x, bounds.location.y,
                    bounds.size.width, bounds.size.height)
                if newX <> bounds.location.x || newY <> bounds.location.y then
                    window.setPositionOnly newX newY)

    member private this.detachTabsToSnap(tabsInOrder: List<IntPtr>, snapDirection: string) =
        match tabsInOrder with
        | [] -> ()
        | [singleHwnd] -> this.detachTabToSnap(singleHwnd, snapDirection)
        | _ ->
            this.detachTabsCommon(tabsInOrder, fun screen bounds ->
                let window = os.windowFromHwnd(List.head tabsInOrder)
                let (newX, newY, newWidth, newHeight) = this.calculateSnapBounds(
                    snapDirection, screen.WorkingArea,
                    bounds.size.width, bounds.size.height)
                window.move (Rect(Pt(newX, newY), Sz(newWidth, newHeight))))

    member private this.detachTabsToSnapWithPercent(tabsInOrder: List<IntPtr>, snapDirection: string, percent: int) =
        match tabsInOrder with
        | [] -> ()
        | [singleHwnd] -> this.detachTabToSnapWithPercent(singleHwnd, snapDirection, percent)
        | _ ->
            this.detachTabsCommon(tabsInOrder, fun screen _bounds ->
                let window = os.windowFromHwnd(List.head tabsInOrder)
                let (newX, newY, newWidth, newHeight) = this.calculateSnapBoundsWithPercent(
                    snapDirection, percent, screen.WorkingArea)
                window.move (Rect(Pt(newX, newY), Sz(newWidth, newHeight))))

    member private this.detachTabsToScreen(tabsInOrder: List<IntPtr>, targetScreen: System.Windows.Forms.Screen, position: Option<string>) =
        match tabsInOrder with
        | [] -> ()
        | [singleHwnd] -> this.detachTabToScreen(singleHwnd, targetScreen, position)
        | _ ->
            this.detachTabsCommon(tabsInOrder, fun sourceScreen bounds ->
                let window = os.windowFromHwnd(List.head tabsInOrder)
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
                window.move (Rect(Pt(initialX, initialY), Sz(newWidth, newHeight)))
                let (newX, newY) = this.calculatePositionInWorkArea(
                    position, targetWorkArea, initialX, initialY, newWidth, newHeight)
                if newX <> initialX || newY <> initialY then
                    window.setPositionOnly newX newY)

    member private this.detachTabsToScreenSnap(tabsInOrder: List<IntPtr>, targetScreen: System.Windows.Forms.Screen, snapDirection: string) =
        match tabsInOrder with
        | [] -> ()
        | [singleHwnd] -> this.detachTabToScreenSnap(singleHwnd, targetScreen, snapDirection)
        | _ ->
            this.detachTabsCommon(tabsInOrder, fun sourceScreen bounds ->
                let window = os.windowFromHwnd(List.head tabsInOrder)
                let sourceWorkArea = sourceScreen.WorkingArea
                let widthPercent = float(bounds.size.width) / float(sourceWorkArea.Width)
                let heightPercent = float(bounds.size.height) / float(sourceWorkArea.Height)
                let targetWorkArea = targetScreen.WorkingArea
                let newWidth = int(float(targetWorkArea.Width) * widthPercent)
                let newHeight = int(float(targetWorkArea.Height) * heightPercent)
                let (newX, newY, finalWidth, finalHeight) = this.calculateSnapBounds(
                    snapDirection, targetWorkArea, newWidth, newHeight)
                window.move (Rect(Pt(newX, newY), Sz(finalWidth, finalHeight))))

    member private this.detachTabsToScreenSnapWithPercent(tabsInOrder: List<IntPtr>, targetScreen: System.Windows.Forms.Screen, snapDirection: string, percent: int) =
        match tabsInOrder with
        | [] -> ()
        | [singleHwnd] -> this.detachTabToScreenSnapWithPercent(singleHwnd, targetScreen, snapDirection, percent)
        | _ ->
            this.detachTabsCommon(tabsInOrder, fun _src _bounds ->
                let window = os.windowFromHwnd(List.head tabsInOrder)
                let (newX, newY, newWidth, newHeight) = this.calculateSnapBoundsWithPercent(
                    snapDirection, percent, targetScreen.WorkingArea)
                window.move (Rect(Pt(newX, newY), Sz(newWidth, newHeight))))

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
        if List.length targets <= 1 then this.detachTabToPosition(hwnd, position)
        else this.detachTabsToPosition(targets, position)
    member private this.detachOrSplitToSnap(hwnd: IntPtr, snapDirection: string) =
        let targets = this.actionTargetsInVisualOrder(hwnd)
        // Persist the realigned position before detaching so the freshly formed
        // group inherits it (WindowGroup.addWindow reads the per-window alignment)
        this.applySnapRealign(targets, snapDirection, fun h a -> Services.program.setWindowAlignment(h, Some a))
        if List.length targets <= 1 then this.detachTabToSnap(hwnd, snapDirection)
        else this.detachTabsToSnap(targets, snapDirection)
    member private this.detachOrSplitToSnapWithPercent(hwnd: IntPtr, snapDirection: string, percent: int) =
        let targets = this.actionTargetsInVisualOrder(hwnd)
        this.applySnapRealign(targets, snapDirection, fun h a -> Services.program.setWindowAlignment(h, Some a))
        if List.length targets <= 1 then this.detachTabToSnapWithPercent(hwnd, snapDirection, percent)
        else this.detachTabsToSnapWithPercent(targets, snapDirection, percent)
    member private this.detachOrSplitToScreen(hwnd: IntPtr, targetScreen: System.Windows.Forms.Screen, position: Option<string>) =
        let targets = this.actionTargetsInVisualOrder(hwnd)
        if List.length targets <= 1 then this.detachTabToScreen(hwnd, targetScreen, position)
        else this.detachTabsToScreen(targets, targetScreen, position)
    member private this.detachOrSplitToScreenSnap(hwnd: IntPtr, targetScreen: System.Windows.Forms.Screen, snapDirection: string) =
        let targets = this.actionTargetsInVisualOrder(hwnd)
        this.applySnapRealign(targets, snapDirection, fun h a -> Services.program.setWindowAlignment(h, Some a))
        if List.length targets <= 1 then this.detachTabToScreenSnap(hwnd, targetScreen, snapDirection)
        else this.detachTabsToScreenSnap(targets, targetScreen, snapDirection)
    member private this.detachOrSplitToScreenSnapWithPercent(hwnd: IntPtr, targetScreen: System.Windows.Forms.Screen, snapDirection: string, percent: int) =
        let targets = this.actionTargetsInVisualOrder(hwnd)
        this.applySnapRealign(targets, snapDirection, fun h a -> Services.program.setWindowAlignment(h, Some a))
        if List.length targets <= 1 then this.detachTabToScreenSnapWithPercent(hwnd, targetScreen, snapDirection, percent)
        else this.detachTabsToScreenSnapWithPercent(targets, targetScreen, snapDirection, percent)

    member private this.getTabHeightForSnap() =
        try
            if group.snapTabHeightMargin then group.tabAppearance.tabHeight - 1
            else 0
        with | _ -> 0

    member private this.adjustWorkAreaForTabHeight(workArea: System.Drawing.Rectangle) =
        let tabHeight = this.getTabHeightForSnap()
        if tabHeight > 0 then
            System.Drawing.Rectangle(workArea.X, workArea.Y + tabHeight, workArea.Width, workArea.Height - tabHeight)
        else
            workArea

    member private this.calculateSnapBounds(
        snapDirection: string,
        workArea: System.Drawing.Rectangle,
        currentWidth: int,
        currentHeight: int) : (int * int * int * int) =
        // Returns (x, y, width, height) for snap position
        // Snap right/left: maintain width, expand height to full
        // Snap top/bottom: maintain height, expand width to full
        let workArea = this.adjustWorkAreaForTabHeight(workArea)
        let clampedWidth = min currentWidth workArea.Width
        let clampedHeight = min currentHeight workArea.Height
        match snapDirection with
        | "snapright" ->
            let newWidth = clampedWidth
            let newHeight = workArea.Height
            let x = workArea.Right - newWidth
            let y = workArea.Top
            (x, y, newWidth, newHeight)
        | "snapleft" ->
            let newWidth = clampedWidth
            let newHeight = workArea.Height
            let x = workArea.Left
            let y = workArea.Top
            (x, y, newWidth, newHeight)
        | "snaptop" ->
            let newWidth = workArea.Width
            let newHeight = clampedHeight
            let x = workArea.Left
            let y = workArea.Top
            (x, y, newWidth, newHeight)
        | "snapbottom" ->
            let newWidth = workArea.Width
            let newHeight = clampedHeight
            let x = workArea.Left
            let y = workArea.Bottom - newHeight
            (x, y, newWidth, newHeight)
        | _ ->
            (workArea.Left, workArea.Top, clampedWidth, clampedHeight)

    member private this.calculateSnapBoundsWithPercent(
        snapDirection: string,
        percent: int,
        workArea: System.Drawing.Rectangle) : (int * int * int * int) =
        // Returns (x, y, width, height) for snap position with percentage
        // Snap right/left: width = workArea.Width * percent, height = full
        // Snap top/bottom: width = full, height = workArea.Height * percent
        let workArea =
            if snapDirection = "snapmaximizedesktop" then
                this.adjustWorkAreaForTabHeight(System.Windows.Forms.SystemInformation.VirtualScreen)
            else
                this.adjustWorkAreaForTabHeight(workArea)
        let percentFloat = float(percent) / 100.0
        match snapDirection with
        | "snapright" ->
            let newWidth = int(float(workArea.Width) * percentFloat)
            let newHeight = workArea.Height
            let x = workArea.Right - newWidth
            let y = workArea.Top
            (x, y, newWidth, newHeight)
        | "snapleft" ->
            let newWidth = int(float(workArea.Width) * percentFloat)
            let newHeight = workArea.Height
            let x = workArea.Left
            let y = workArea.Top
            (x, y, newWidth, newHeight)
        | "snaptop" ->
            let newWidth = workArea.Width
            let newHeight = int(float(workArea.Height) * percentFloat)
            let x = workArea.Left
            let y = workArea.Top
            (x, y, newWidth, newHeight)
        | "snapbottom" ->
            let newWidth = workArea.Width
            let newHeight = int(float(workArea.Height) * percentFloat)
            let x = workArea.Left
            let y = workArea.Bottom - newHeight
            (x, y, newWidth, newHeight)
        | "snaptopleft" ->
            let newWidth = int(float(workArea.Width) * percentFloat)
            let newHeight = int(float(workArea.Height) * percentFloat)
            let x = workArea.Left
            let y = workArea.Top
            (x, y, newWidth, newHeight)
        | "snaptopright" ->
            let newWidth = int(float(workArea.Width) * percentFloat)
            let newHeight = int(float(workArea.Height) * percentFloat)
            let x = workArea.Right - newWidth
            let y = workArea.Top
            (x, y, newWidth, newHeight)
        | "snapbottomleft" ->
            let newWidth = int(float(workArea.Width) * percentFloat)
            let newHeight = int(float(workArea.Height) * percentFloat)
            let x = workArea.Left
            let y = workArea.Bottom - newHeight
            (x, y, newWidth, newHeight)
        | "snapbottomright" ->
            let newWidth = int(float(workArea.Width) * percentFloat)
            let newHeight = int(float(workArea.Height) * percentFloat)
            let x = workArea.Right - newWidth
            let y = workArea.Bottom - newHeight
            (x, y, newWidth, newHeight)
        | "snapcenter" ->
            let newWidth = int(float(workArea.Width) * percentFloat)
            let newHeight = int(float(workArea.Height) * percentFloat)
            let x = workArea.Left + (workArea.Width - newWidth) / 2
            let y = workArea.Top + (workArea.Height - newHeight) / 2
            (x, y, newWidth, newHeight)
        | "snapcenterhorizontal" ->
            let newWidth = int(float(workArea.Width) * percentFloat)
            let newHeight = workArea.Height
            let x = workArea.Left + (workArea.Width - newWidth) / 2
            let y = workArea.Top
            (x, y, newWidth, newHeight)
        | "snapcentervertical" ->
            let newWidth = workArea.Width
            let newHeight = int(float(workArea.Height) * percentFloat)
            let x = workArea.Left
            let y = workArea.Top + (workArea.Height - newHeight) / 2
            (x, y, newWidth, newHeight)
        | "snapmaximizedisplay" | "snapmaximizedesktop" ->
            (workArea.Left, workArea.Top, workArea.Width, workArea.Height)
        | _ ->
            (workArea.Left, workArea.Top, workArea.Width, workArea.Height)

    member private this.detachTabToSnap(hwnd: IntPtr, snapDirection: string) =
        // This method is only called when group has multiple tabs (menu is disabled for single tab)
        if group.windows.items.count <= 1 then
            raise (System.InvalidOperationException("detachTabToSnap should not be called when there is only one tab"))

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

            let (newX, newY, newWidth, newHeight) = this.calculateSnapBounds(
                snapDirection,
                screen.WorkingArea,
                bounds.size.width,
                bounds.size.height)

            window.move (Rect(Pt(newX, newY), Sz(newWidth, newHeight)))

            notifyDetached(hwnd)

        finally
            (ThreadHelper.cancelablePostBack 200 <| fun() ->
                Services.program.resumeTabMonitoring()) |> ignore

    member private this.detachTabToScreenSnap(hwnd: IntPtr, targetScreen: Screen, snapDirection: string) =
        // This method is only called when group has multiple tabs (menu is disabled for single tab)
        if group.windows.items.count <= 1 then
            raise (System.InvalidOperationException("detachTabToScreenSnap should not be called when there is only one tab"))

        let window = os.windowFromHwnd(hwnd)
        let bounds = window.bounds
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

            window.move (Rect(Pt(newX, newY), Sz(finalWidth, finalHeight)))

            notifyDetached(hwnd)

        finally
            (ThreadHelper.cancelablePostBack 200 <| fun() ->
                Services.program.resumeTabMonitoring()) |> ignore

    member private this.detachTabToSnapWithPercent(hwnd: IntPtr, snapDirection: string, percent: int) =
        // Snap with percentage: width/height based on display percentage
        if group.windows.items.count <= 1 then
            raise (System.InvalidOperationException("detachTabToSnapWithPercent should not be called when there is only one tab"))

        let window = os.windowFromHwnd(hwnd)
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

            window.move (Rect(Pt(newX, newY), Sz(newWidth, newHeight)))

            notifyDetached(hwnd)

        finally
            (ThreadHelper.cancelablePostBack 200 <| fun() ->
                Services.program.resumeTabMonitoring()) |> ignore

    member private this.detachTabToScreenSnapWithPercent(hwnd: IntPtr, targetScreen: Screen, snapDirection: string, percent: int) =
        // Snap with percentage to another screen
        if group.windows.items.count <= 1 then
            raise (System.InvalidOperationException("detachTabToScreenSnapWithPercent should not be called when there is only one tab"))

        let window = os.windowFromHwnd(hwnd)

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

            window.move (Rect(Pt(newX, newY), Sz(newWidth, newHeight)))

            notifyDetached(hwnd)

        finally
            (ThreadHelper.cancelablePostBack 200 <| fun() ->
                Services.program.resumeTabMonitoring()) |> ignore

    member this.moveTabGroupToPosition(hwnd: IntPtr, position: Option<string>) =
        // Move the entire tab group to the specified position
        // Always use the active window (topWindow) as the reference point
        let activeHwnd = group.topWindow
        let window = os.windowFromHwnd(activeHwnd)
        let bounds = window.bounds
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
            bounds.size.height)

        // Move the active window
        if newX <> bounds.location.x || newY <> bounds.location.y then
            window.setPositionOnly newX newY

    member this.moveTabGroupToScreen(hwnd: IntPtr, targetScreen: Screen, position: Option<string>) =
        // Move the entire tab group to the specified screen and position
        // Always use the active window (topWindow) as the reference point
        let activeHwnd = group.topWindow
        let window = os.windowFromHwnd(activeHwnd)
        let bounds = window.bounds
        let sourceScreen = this.getCurrentScreenForWindow(activeHwnd)
        let sourceWorkArea = sourceScreen.WorkingArea

        // Use group bounds (expanded by margin) for percentage calculation
        let (mTop, mLeft, mRight, mBottom) = group.getExeMargin(activeHwnd)
        let groupWidth = bounds.size.width + mLeft + mRight
        let groupHeight = bounds.size.height + mTop + mBottom
        let groupX = bounds.location.x - mLeft
        let groupY = bounds.location.y - mTop

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

        let moveBounds = Rect(Pt(finalX, finalY), Sz(newWidth, newHeight))
        let finalBounds = group.applyExeMarginForWrite(activeHwnd, moveBounds)
        window.move(finalBounds)
        if group.hasExeMargin(activeHwnd) then
            group.recordMarginApplied(activeHwnd, finalBounds.width, finalBounds.height)

    // Whether the "change tab position on left/right snap" setting is enabled.
    member private this.snapRealignEnabled() =
        try (Services.settings.getValue("changeTabPositionOnSnap") :?> string) = "change"
        with _ -> false

    // Realign tabs to match a left/right snap, when the setting is enabled and
    // the given tabs are uniformly aligned (all left or all right). A left snap
    // makes them all left, a right snap all right; a mixed group is left as is.
    // Non-left/right snaps (top/bottom) never realign. setAlign applies the new
    // alignment (in-place group snap updates the visible tabs; detach persists
    // it so the freshly formed group inherits it). tabHwnds are read from the
    // current group for the uniformity check, so this must run before detaching.
    member private this.applySnapRealign(tabHwnds: IntPtr list, snapDirection: string, setAlign: IntPtr -> TabAlign -> unit) =
        if this.snapRealignEnabled() && not tabHwnds.IsEmpty then
            let desiredOpt =
                match snapDirection with
                | "snapleft" -> Some TopLeft
                | "snapright" -> Some TopRight
                | _ -> None
            match desiredOpt with
            | Some desired ->
                let allLeft = tabHwnds |> List.forall (fun h -> group.getTabAlign(h) = TopLeft)
                let allRight = tabHwnds |> List.forall (fun h -> group.getTabAlign(h) = TopRight)
                if allLeft || allRight then
                    tabHwnds |> List.iter (fun h -> setAlign h desired)
            | None -> ()

    // Realign the whole current group after an in-place left/right snap.
    member private this.applyGroupSnapRealign(snapDirection: string) =
        this.applySnapRealign(group.visualOrder.list, snapDirection, fun h a -> group.setTabAlign(h, a))

    member this.moveTabGroupToSnap(hwnd: IntPtr, snapDirection: string) =
        // Move the entire tab group to snap position (resize and position)
        let activeHwnd = group.topWindow
        let window = os.windowFromHwnd(activeHwnd)
        let bounds = window.bounds
        let screen = this.getCurrentScreenForWindow(activeHwnd)

        // Restore window if minimized or maximized
        if window.isMinimized || window.isMaximized then
            window.showWindow(ShowWindowCommands.SW_RESTORE)

        // Use group bounds (expanded by margin) for snap calculation so margin is not applied twice
        let (mTop, mLeft, mRight, mBottom) = group.getExeMargin(activeHwnd)
        let snapWidth = bounds.size.width + mLeft + mRight
        let snapHeight = bounds.size.height + mTop + mBottom

        let (newX, newY, newWidth, newHeight) = this.calculateSnapBounds(
            snapDirection,
            screen.WorkingArea,
            snapWidth,
            snapHeight)

        let snapBounds = Rect(Pt(newX, newY), Sz(newWidth, newHeight))
        // Apply per-exe margin (e.g., LINE.exe always needs 30px margin)
        let finalBounds = group.applyExeMarginForWrite(activeHwnd, snapBounds)
        window.move(finalBounds)
        // Record shrunk size for tracking
        if group.hasExeMargin(activeHwnd) then
            group.recordMarginApplied(activeHwnd, finalBounds.width, finalBounds.height)
        this.applyGroupSnapRealign(snapDirection)

    member this.moveTabGroupToScreenSnap(hwnd: IntPtr, targetScreen: Screen, snapDirection: string) =
        // Move the entire tab group to snap position on target screen (resize and position)
        let activeHwnd = group.topWindow
        let window = os.windowFromHwnd(activeHwnd)
        let bounds = window.bounds
        let sourceScreen = this.getCurrentScreenForWindow(activeHwnd)
        let sourceWorkArea = sourceScreen.WorkingArea

        // Use group bounds (expanded by margin) for percentage calculation
        let (mTop, mLeft, mRight, mBottom) = group.getExeMargin(activeHwnd)
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

        let snapBounds = Rect(Pt(newX, newY), Sz(finalWidth, finalHeight))
        // Apply per-exe margin (e.g., LINE.exe always needs 30px margin)
        let finalBounds2 = group.applyExeMarginForWrite(activeHwnd, snapBounds)
        window.move(finalBounds2)
        if group.hasExeMargin(activeHwnd) then
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
            screen.WorkingArea)

        let snapBounds = Rect(Pt(newX, newY), Sz(newWidth, newHeight))
        let finalBounds = group.applyExeMarginForWrite(activeHwnd, snapBounds)
        window.move(finalBounds)
        if group.hasExeMargin(activeHwnd) then
            group.recordMarginApplied(activeHwnd, finalBounds.width, finalBounds.height)
        this.applyGroupSnapRealign(snapDirection)

    member this.moveTabGroupToScreenSnapWithPercent(hwnd: IntPtr, targetScreen: Screen, snapDirection: string, percent: int) =
        // Move the entire tab group to snap position on target screen with percentage
        let activeHwnd = group.topWindow
        let window = os.windowFromHwnd(activeHwnd)

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

        let snapBounds = Rect(Pt(newX, newY), Sz(newWidth, newHeight))
        let finalBounds2 = group.applyExeMarginForWrite(activeHwnd, snapBounds)
        window.move(finalBounds2)
        if group.hasExeMargin(activeHwnd) then
            group.recordMarginApplied(activeHwnd, finalBounds2.width, finalBounds2.height)
        this.applyGroupSnapRealign(snapDirection)

    member private  this.contextMenu(hwnd) =
        let checked(isChecked) = if isChecked then List2([MenuFlags.MF_CHECKED]) else List2()
        let grayed(isGrayed) = if isGrayed then List2([MenuFlags.MF_GRAYED]) else List2()

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

        // Inner items for a "Position Move" submenu, flat (the old "Snap Other"
        // submenu is expanded in place): Snap Left / Right / Top / Bottom, the
        // percent-variant snap submenus, Snap Display (and Snap Desktop on
        // multi-monitor when includeSnapDesktop), then the Move edge submenu,
        // each block separated by a divider.
        // Display submenus are appended separately by the caller when needed.
        let buildPositionMoveInnerItems (includeSnapDesktop: bool) (moveFn: string option -> unit) (snapFn: string -> unit) (snapPercentFn: string -> int -> unit) =
            let desktopSnap =
                if includeSnapDesktop && System.Windows.Forms.Screen.AllScreens.Length > 1 then
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
                (contextHwnd: IntPtr)
                (menuTextPrefix: string)
                (currentScreenExtraItems: ContextMenuItem list)
                (moveFn: string option -> unit)
                (snapFn: string -> unit)
                (snapPercentFn: string -> int -> unit)
                (screenMoveFn: System.Windows.Forms.Screen -> string option -> unit)
                (screenSnapFn: System.Windows.Forms.Screen -> string -> unit)
                (screenSnapPercentFn: System.Windows.Forms.Screen -> string -> int -> unit) : ContextMenuItem list =
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
                    items = List2(items)
                    flags = List2()
                }))
            |> Array.toList

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

        // Resolve the actual path to hand to Process.Start.
        // For known UWP apps we use an alternative launch command (e.g. wt.exe).
        // Returns None only for UWP apps with no alternative - the caller then surfaces an error.
        let resolveLaunchPath () =
            if processPath.Contains("WindowsApps") then
                getAlternativeLaunchCommand(processPath)
            else
                Some(processPath)

        // Shared error reporting wrapper used by all three new-launch menu items
        let showUwpError () =
            let appName = System.IO.Path.GetFileNameWithoutExtension(processPath)
            let message = String.Format(Localization.getString("NewLaunchErrorUWP"), appName)
            MessageBox.Show(message, "WindowTabs", MessageBoxButtons.OK, MessageBoxIcon.Information) |> ignore
        let handleLaunchError (launchCall: string -> unit) =
            try
                match resolveLaunchPath() with
                | Some(path) -> launchCall path
                | None -> showUwpError ()
            with
            | :? System.ComponentModel.Win32Exception as ex when processPath.Contains("WindowsApps") ->
                showUwpError ()
                System.Diagnostics.Debug.WriteLine(sprintf "UWP app cannot be launched: %s - %s" processPath ex.Message)
            | :? System.ComponentModel.Win32Exception as ex ->
                let message = String.Format(Localization.getString("NewLaunchErrorProcess"), processPath, ex.Message)
                MessageBox.Show(message, "WindowTabs Error", MessageBoxButtons.OK, MessageBoxIcon.Error) |> ignore
                System.Diagnostics.Debug.WriteLine(sprintf "Error starting process: %s - %s" processPath ex.Message)
            | ex ->
                let message = String.Format(Localization.getString("NewLaunchErrorUnexpected"), ex.Message)
                MessageBox.Show(message, "WindowTabs Error", MessageBoxButtons.OK, MessageBoxIcon.Error) |> ignore
                System.Diagnostics.Debug.WriteLine(sprintf "Unexpected error starting process: %s - %s" processPath ex.Message)

        // Find the decorator whose group contains the given window hwnd.
        // Used by the "new window + position" callback to reach the newly created group.
        let findDecoratorForWindow (winHwnd: IntPtr) =
            lock decorators (fun () ->
                decorators.Values
                |> Seq.tryFind (fun d ->
                    try
                        WinUserApi.IsWindow(d.group.hwnd) && d.group.windows.contains(winHwnd)
                    with _ -> false))

        let currentTabName = TabNameHelper.truncate (this.ts.tabInfo(Tab(hwnd)).text)

        // Disabled header at the top of the menu showing which tab it targets
        let targetTabHeaderItem =
            CmiRegular({
                text = TabNameHelper.format (Localization.getString("TargetTabHeader")) currentTabName
                image = None
                flags = List2([MenuFlags.MF_GRAYED])
                click = fun() -> ()
            })

        // Item 1: "New tab : right of this tab" — launch and dock into the current tab group
        let newTabInGroupItem =
            CmiRegular({
                text = TabNameHelper.format (Localization.getString("NewTabInGroup")) currentTabName
                flags = List2()
                image = None
                click = fun() ->
                    handleLaunchError (fun path -> Services.program.launchNewWindow group.hwnd hwnd path)
            })

        // Item 2: "New window (position)" — launch as standalone then apply a position to the new group.
        // Single monitor: one "New window (position)" submenu. Multiple monitors:
        // one flattened "New window (position) <display>" item per display,
        // spliced directly into the "New launch" menu.
        let newWindowPositionItems =
            let launchStandaloneThen (postAction: TabStripDecorator -> IntPtr -> unit) =
                handleLaunchError (fun path ->
                    Services.program.launchStandaloneWindow path (fun newHwnd ->
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
                buildPerDisplayPositionItems hwnd (Localization.getString("NewWindowPositionMenu")) currentScreenExtraItems
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
            let allDecorators = lock decorators (fun () ->
                decorators.Values
                |> List.ofSeq
                |> List.filter (fun d ->
                    try
                        d.ts.hwnd <> IntPtr.Zero &&
                        WinUserApi.IsWindow(d.group.hwnd) &&
                        WinUserApi.IsWindow(d.ts.hwnd)
                    with _ -> false))
            this.updateGroupInfo()
            let updateTasks =
                allDecorators
                |> List.filter (fun d -> d.group.hwnd <> group.hwnd)
                |> List.map (fun d ->
                    async {
                        try
                            if WinUserApi.IsWindow(d.group.hwnd) && WinUserApi.IsWindow(d.ts.hwnd) then
                                d.group.invokeSync(fun () -> d.updateGroupInfo())
                        with _ -> ()
                    })
            updateTasks |> Async.Parallel |> Async.RunSynchronously |> ignore
            let otherGroupInfos = lock groupInfos (fun () ->
                groupInfos.Values
                |> List.ofSeq
                |> List.filter (fun info -> info.hwnd <> group.hwnd && info.tabCount > 0))
            let groupItems =
                if List.isEmpty otherGroupInfos then []
                else
                    let uniqueGroupInfos = otherGroupInfos |> List.distinctBy (fun info -> info.hwnd)
                    uniqueGroupInfos
                    |> List.choose (fun info ->
                        try
                            let targetDecorator = lock decorators (fun () ->
                                decorators.Values
                                |> Seq.tryFind (fun d ->
                                    d.group.hwnd = info.hwnd &&
                                    WinUserApi.IsWindow(d.group.hwnd) &&
                                    WinUserApi.IsWindow(d.ts.hwnd)))
                            match targetDecorator with
                            | Some decorator ->
                                let fullNameString =
                                    if info.tabCount = 1 then
                                        let tabName = info.tabNames |> List.head
                                        if tabName.Length > 22 then tabName.Substring(0, 22) + "..." else tabName
                                    elif info.tabCount = 2 then
                                        let tabNames = info.tabNames |> List.take 2
                                        let truncatedNames =
                                            tabNames
                                            |> List.map (fun name -> if name.Length > 9 then name.Substring(0, 9) + "..." else name)
                                        String.Join(" ", truncatedNames)
                                    else
                                        let tabNames = info.tabNames |> List.take (min 3 info.tabCount)
                                        let truncatedNames =
                                            tabNames
                                            |> List.map (fun name -> if name.Length > 5 then name.Substring(0, 5) + "..." else name)
                                        let nameString = String.Join(" ", truncatedNames)
                                        if info.tabCount > 3 then nameString + "..." else nameString
                                let formatString = Localization.getString("MoveTabGroupFormat")
                                let tabWord =
                                    if info.tabCount = 1 then Localization.getString("TabSingular")
                                    else Localization.getString("TabPlural")
                                let menuText = String.Format(formatString, info.tabCount, tabWord, fullNameString)
                                Some(CmiRegular({
                                    text = menuText
                                    image = info.firstTabIcon
                                    click = fun() ->
                                        handleLaunchError (fun path ->
                                            Services.program.launchNewWindow decorator.group.hwnd hwnd path)
                                    flags = List2()
                                }))
                            | None -> None
                        with _ -> None)
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
                items = List2([newTabInGroupItem; CmiSeparator] @ newWindowPositionItems @ [newWindowLinkGroupItem])
                flags = List2()
            })

        
        let tabPositionSubMenu =
            let currentAlignment = group.getTabAlign(hwnd)
            let allTopLeft = group.visualOrder.list |> List.forall (fun h -> group.getTabAlign(h) = TopLeft)
            let allTopRight = group.visualOrder.list |> List.forall (fun h -> group.getTabAlign(h) = TopRight)
            let shortTabName = TabNameHelper.truncate (this.ts.tabInfo(Tab(hwnd)).text)
            let alignTargets = this.actionTargets(hwnd)
            let isMultiSelect = alignTargets.Length > 1
            // Per-tab item gating: union of targets in multi-select,
            // just the right-clicked tab otherwise.
            let allTargetsLeft = alignTargets |> List.forall (fun h -> group.getTabAlign(h) = TopLeft)
            let allTargetsRight = alignTargets |> List.forall (fun h -> group.getTabAlign(h) = TopRight)

            let leftItemText =
                if isMultiSelect then
                    System.String.Format(Localization.getString("AlignSelectedTabsLeftFormat"), alignTargets.Length)
                else
                    TabNameHelper.format (Localization.getString("AlignThisTabLeft")) shortTabName
            let leftItemFlags =
                let disable = if isMultiSelect then allTargetsLeft else currentAlignment = TopLeft
                if disable then List2([MenuFlags.MF_GRAYED]) else List2()
            let leftItem =
                CmiRegular({
                    text = leftItemText
                    image = None
                    flags = leftItemFlags
                    click = fun() -> alignTargets |> List.iter (fun h -> group.setTabAlign(h, TopLeft))
                })

            let rightItemText =
                if isMultiSelect then
                    System.String.Format(Localization.getString("AlignSelectedTabsRightFormat"), alignTargets.Length)
                else
                    TabNameHelper.format (Localization.getString("AlignThisTabRight")) shortTabName
            let rightItemFlags =
                let disable = if isMultiSelect then allTargetsRight else currentAlignment = TopRight
                if disable then List2([MenuFlags.MF_GRAYED]) else List2()
            let rightItem =
                CmiRegular({
                    text = rightItemText
                    image = None
                    flags = rightItemFlags
                    click = fun() -> alignTargets |> List.iter (fun h -> group.setTabAlign(h, TopRight))
                })

            CmiPopUp({
                text = Localization.getString("TabAlignMenu")
                image = None
                items = List2([
                    CmiRegular({
                        text = Localization.getString("AlignAllTopLeft")
                        image = None
                        flags = if allTopLeft then List2([MenuFlags.MF_GRAYED]) else List2()
                        click = fun() ->
                            group.visualOrder.list |> List.iter (fun h -> group.setTabAlign(h, TopLeft))
                    })
                    CmiRegular({
                        text = Localization.getString("AlignAllTopRight")
                        image = None
                        flags = if allTopRight then List2([MenuFlags.MF_GRAYED]) else List2()
                        click = fun() ->
                            group.visualOrder.list |> List.iter (fun h -> group.setTabAlign(h, TopRight))
                    })
                    CmiSeparator
                    leftItem
                    rightItem
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
                    System.String.Format(Localization.getString("PinSelectedTabsFormat"), pinTargets.Length)
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
                    System.String.Format(Localization.getString("UnpinSelectedTabsFormat"), pinTargets.Length)
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

            // "Pin all tabs" / "Unpin all tabs" — gated against the whole group.
            let pinAllFlags = if group.allPinned then List2([MenuFlags.MF_GRAYED]) else List2()
            let pinAllItem =
                CmiRegular({
                    text = Localization.getString("PinAllTabs")
                    image = None
                    flags = pinAllFlags
                    click = fun() -> group.pinAll()
                })
            let unpinAllFlags = if group.nonePinned then List2([MenuFlags.MF_GRAYED]) else List2()
            let unpinAllItem =
                CmiRegular({
                    text = Localization.getString("UnpinAllTabs")
                    image = None
                    flags = unpinAllFlags
                    click = fun() -> group.unpinAll()
                })

            CmiPopUp({
                text = Localization.getString("PinTabMenu")
                image = None
                items = List2([
                    pinItem
                    unpinItem
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
            let createFillIcon (color: Color) (isChecked: bool) =
                let size = 16
                let img = Img(Sz(size, size))
                let g = img.graphics
                g.Clear(Color.White)
                use brush = new SolidBrush(Color.FromArgb(255, int color.R, int color.G, int color.B))
                g.FillRectangle(brush, 1, 1, size - 2, size - 2)
                use pen = new Pen(Color.FromArgb(160, 160, 160), 1.0f)
                g.DrawRectangle(pen, 0, 0, size - 1, size - 1)
                if isChecked then drawCheckmark g
                img
            let createUnderlineIcon (color: Color) (isChecked: bool) =
                let size = 16
                let img = Img(Sz(size, size))
                let g = img.graphics
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
                img
            let createBorderIcon (color: Color) (isChecked: bool) =
                let size = 16
                let img = Img(Sz(size, size))
                let g = img.graphics
                g.Clear(Color.Transparent)
                use borderPen = new Pen(Color.FromArgb(255, int color.R, int color.G, int color.B), 2.0f)
                g.DrawRectangle(borderPen, 1, 1, size - 3, size - 3)
                if isChecked then drawCheckmark g
                img
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
                    String.Format(Localization.getString("TabColorSelectedTabsFormat"), colorTargets.Length)
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
                    String.Format(Localization.getString("TabColorClearSelectedTabsFormat"), colorTargets.Length)
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
                        text = TabNameHelper.format (Localization.getString("ResetTabNameFormat")) resetName
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
                        text = String.Format(Localization.getString("SystemCopySelectedTabsExePathFormat"), sysTargets.Length)
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
                        text = String.Format(Localization.getString("SystemCopySelectedTabsWindowTitleFormat"), sysTargets.Length)
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
                    String.Format(Localization.getString("CloseSelectedTabsFormat"), closeTargets.Length)
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
            let tabIndex = this.ts.visualOrder.tryFindIndex((=) currentTab)
            let rightTabCount =
                tabIndex |> Option.map(fun index -> this.ts.visualOrder.count - index - 1) |> Option.defaultValue 0
            let displayText =
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
                flags = grayed(isCloseMultiSelect || rightTabCount = 0)
            })

        let closeLeftTabsItem =
            let currentTab = Tab(hwnd)
            let tabIndex = this.ts.visualOrder.tryFindIndex((=) currentTab)
            let leftTabCount =
                tabIndex |> Option.defaultValue 0
            let displayText =
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
                buildPerDisplayPositionItems hwnd detachMoveText currentScreenExtraItems
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
                buildPerDisplayPositionItems hwnd (Localization.getString("PositionMoveMenu")) []
                    moveFn snapFn snapPercentFn
                    (fun s pos -> this.moveTabGroupToScreen(hwnd, s, pos))
                    (fun s dir -> this.moveTabGroupToScreenSnap(hwnd, s, dir))
                    (fun s dir pct -> this.moveTabGroupToScreenSnapWithPercent(hwnd, s, dir, pct))
            else
                [ CmiPopUp({
                    text = Localization.getString("PositionMoveMenu")
                    image = None
                    items = List2(buildPositionMoveInnerItems true moveFn snapFn snapPercentFn)
                    flags = List2()
                }) ]

        let moveTabGroupToGroupMenu =
            // Update all group infos before building menu (same as moveTabMenu)
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
                    info.tabCount > 0 // Has at least one tab
                )
            )

            if not (List.isEmpty allGroupInfos) then
                // Build menu items for each other group
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
                                // Build menu text with tab names (same as moveTabMenu)
                                let fullNameString =
                                    if info.tabCount = 1 then
                                        let tabName = info.tabNames |> List.head
                                        if tabName.Length > 22 then
                                            tabName.Substring(0, 22) + "..."
                                        else
                                            tabName
                                    elif info.tabCount = 2 then
                                        let tabNames = info.tabNames |> List.take 2
                                        let truncatedNames = tabNames |> List.map (fun name ->
                                            if name.Length > 9 then
                                                name.Substring(0, 9) + "..."
                                            else
                                                name
                                        )
                                        String.Join(" ", truncatedNames)
                                    else
                                        let tabNames = info.tabNames |> List.take (min 3 info.tabCount)
                                        let truncatedNames = tabNames |> List.map (fun name ->
                                            if name.Length > 5 then
                                                name.Substring(0, 5) + "..."
                                            else
                                                name
                                        )
                                        let nameString = String.Join(" ", truncatedNames)
                                        if info.tabCount > 3 then
                                            nameString + "..."
                                        else
                                            nameString

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
                                        // Move all tabs to the target group
                                        this.moveTabGroupToGroup(decorator.group)
                                    flags = List2()
                                }))
                            | None ->
                                None
                        with ex ->
                            System.Diagnostics.Debug.WriteLine(sprintf "Exception in menu item creation: %s" ex.Message)
                            None
                    )

                CmiPopUp({
                    text = Localization.getString("DockingTabGroupToGroup")
                    image = None
                    items = List2(menuItems)
                    flags = List2()
                })
            else
                // No other groups available - show disabled menu
                CmiPopUp({
                    text = Localization.getString("DockingTabGroupToGroup")
                    image = None
                    items = List2([])
                    flags = List2([MenuFlags.MF_GRAYED])
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
                // Update all group infos before building menus (shared by moveTabMenu and split menus)
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

                // Now get the updated group infos (shared by all menus that need group info)
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

                let moveTabMenu =
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
                                                // Move the right-clicked tab plus any
                                                // selected tabs (and the active tab) to
                                                // the target group when multi-select is
                                                // active. Otherwise just move the
                                                // right-clicked tab.
                                                // Iterate in visual (left-to-right) order
                                                // so the joiners land in the destination
                                                // group's tail in the same order they had
                                                // in the source strip.
                                                this.actionTargetsInVisualOrder(hwnd)
                                                |> List.iter (fun h -> this.moveTabToGroup(h, decorator.group))
                                            flags = List2()
                                        }))
                                    | None ->
                                        // No valid decorator found, skip this item
                                        None
                                with ex ->
                                    System.Diagnostics.Debug.WriteLine(sprintf "Exception in menu item creation: %s" ex.Message)
                                    None
                            )

                        // The tab name / selected-tab count now lives on the
                        // parent "Tab detach" menu
                        Some(CmiPopUp({
                            text = Localization.getString("DetachAndDockingTabToGroup")
                            image = None
                            items = List2(menuItems)
                            flags = if group.zorder.value.length <= 1 then List2([MenuFlags.MF_GRAYED]) else List2()
                        }))
                    else
                        // No other groups available
                        None

                // Wrap detach menus in parent submenu (the detach-position part
                // expands to one item per display on multi-monitor setups)
                let subMenuItems =
                    (detachTabPositionItems |> List.map Some) @ [ moveTabMenu ]
                    |> List.choose id

                if subMenuItems.IsEmpty then None
                else
                    // "Detach this tab : <name>", or "Detach <n> selected tabs"
                    // when a multi-select is active
                    let detachParentTargets = this.actionTargets(hwnd)
                    let detachParentText =
                        if detachParentTargets.Length > 1 then
                            String.Format(Localization.getString("TabDetachSelectedFormat"), detachParentTargets.Length)
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
                    closeAllTabsItem
                ])
                flags = List2()
            }))
            Some(CmiSeparator)
            Some(CmiPopUp({
                text = Localization.getString("SnapTabMarginMenu")
                image = None
                items = List2([
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
            Some(tabPinSubMenu)
            Some(tabColorSubMenu)
            Some(tabNameSubMenu)
            Some(CmiSeparator)
            Some(systemSubMenu)
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

            // Handle icon-click-to-hide mode separately
            if autoHideDoubleClickCell.value then

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
                            let dragTabLoc = this.ts.dragTabLocation tab
                            let previewWidth = tabInfo.preview().width
                            let originalWidth = this.ts.bounds.width
                            let scaleRatio = float(previewWidth) / float(originalWidth)
                            let scaledClickOffset = Pt(int(float(clickOffsetInTab.x) * scaleRatio), clickOffsetInTab.y)
                            let imageOffset = dragTabLoc.add(scaledClickOffset)

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
                                group.selectedTabs.items.list
                                |> List.filter (fun h -> h <> hwnd)
                            let dragInfo =
                                box({ tab = tab
                                      tabOffset = tabOffset
                                      imageOffset = imageOffset
                                      tabInfo = tabInfo
                                      sourceGroupHwnd = group.hwnd
                                      selectedHwnds = selectedSnapshot })
                            Services.dragDrop.beginDrag(this.ts.hwnd, dragImage, imageOffset, ptScreen, dragInfo)
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
            ()
            
    interface IDragDropTarget with
        member this.dragBegin() =
            this.invokeAsync <| fun() ->
                isDraggingCell.value <- true
                this.ts.transparent <- false
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
                    let grp = this.contiguousSelectedFromTab(originTab)
                    if grp.Length > 1 then
                        this.ts.dragGroup <- grp
                    else
                        this.ts.dragGroup <- []
                | _ ->
                    this.ts.dragGroup <- []
                
        member this.dragEnter dragInfo pt =
            this.invokeSync <| fun() ->
                let dragInfo = dragInfo :?> TabDragInfo
                let (Tab(hwnd)) = dragInfo.tab
                // No special case for a single-tab group: it used to return
                // false here (= reject the strip as a drop target), which sent
                // the drag straight into floating-window mode. Sliding the
                // lone tab inside the strip is still useful — dropping it on
                // the other half of the strip switches its left/right
                // alignment — and dragging beyond the strip bounds still
                // exits into floating-window mode as before.
                dragPtCell.set(pt)
                dragInfoCell.set(Some(dragInfo))
                this.ts.addTabSlide dragInfo.tab this.tabSlide
                this.ts.setTabInfo(dragInfo.tab, dragInfo.tabInfo)
                group.addWindow(hwnd, false)

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
                        for selHwnd in dragInfo.selectedHwnds do
                            if not (group.windows.contains selHwnd) then
                                group.addWindow(selHwnd, false)
                            let selWindow = os.windowFromHwnd(selHwnd)
                            selWindow.showWindow(ShowWindowCommands.SW_SHOW)
                    finally
                        Services.program.resumeTabMonitoring()
                    // Re-establish the active + selected state at the
                    // destination: dragged is the active, prior
                    // selection becomes the new selected set.
                    group.tabActivate(dragInfo.tab, false)
                    for selHwnd in dragInfo.selectedHwnds do
                        group.setSelected(selHwnd, true)

                this.updateTsSlide()
                true

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
                                group.removeWindow(selHwnd)
                            let selWindow = os.windowFromHwnd(selHwnd)
                            selWindow.hideOffScreen(None)
                | None -> ()
                this.updateTsSlide()

        member this.dragEnd() =
            this.invokeAsync <| fun() ->
                isDraggingCell.value <- false
                this.ts.transparent <- true
                let didMove =
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

                dragInfoCell.set(None)
                this.updateTsSlide()

    interface IDisposable with
        member this.Dispose() =
            // Unregister from the global registry
            lock decorators (fun () -> decorators.Remove(group.hwnd) |> ignore)
            lock groupInfos (fun () -> groupInfos.Remove(group.hwnd) |> ignore)
