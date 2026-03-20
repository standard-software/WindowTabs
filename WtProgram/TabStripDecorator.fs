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
                        match json.getBool("enableMenuDarkMode") with
                        | Some(value) -> value
                        | None -> false
                    with | _ -> false
                let splitMenuEnabled =
                    try
                        let json = Services.settings.root
                        match json.getBool("enableSplitMoveSnapMenu") with
                        | Some(value) -> value
                        | None -> false
                    with | _ -> false
                // Split hover effect: per-item bitmap caches for split menu items (\t items)
                // Key = "{menuHandle}_{itemIndex}" to avoid collisions across different menus
                let splitNormalDC = System.Collections.Generic.Dictionary<string, IntPtr>()
                let splitNormalBmp = System.Collections.Generic.Dictionary<string, IntPtr>()
                let splitNormalOldBmp = System.Collections.Generic.Dictionary<string, IntPtr>()
                let splitNormalCached = System.Collections.Generic.Dictionary<string, bool>()
                let splitHoverDC = System.Collections.Generic.Dictionary<string, IntPtr>()
                let splitHoverBmp = System.Collections.Generic.Dictionary<string, IntPtr>()
                let splitHoverOldBmp = System.Collections.Generic.Dictionary<string, IntPtr>()
                let splitHoverCached = System.Collections.Generic.Dictionary<string, bool>()
                let splitWasOver = System.Collections.Generic.Dictionary<string, bool>()
                let splitTimer = new System.Windows.Forms.Timer(Interval = 16)
                Win32Menu.onMenuCreated <- Some(fun hMenu ->
                    if not splitMenuEnabled then () else
                    splitTimer.Tick.Add(fun _ ->
                        let mutable cursorPt = POINT()
                        WinUserApi.GetCursorPos(&cursorPt) |> ignore
                        // Collect all menu handles: top-level + all submenus recursively
                        let allMenus = System.Collections.Generic.List<IntPtr>()
                        let rec collectMenus (m: IntPtr) =
                            allMenus.Add(m)
                            let cnt = WinUserApi.GetMenuItemCount(m)
                            for j in 0..cnt-1 do
                                let sub = WinUserApi.GetSubMenu(m, j)
                                if sub <> IntPtr.Zero then
                                    collectMenus sub
                        collectMenus hMenu
                        for menuH in allMenus do
                            let count = WinUserApi.GetMenuItemCount(menuH)
                            for i in 0..count-1 do
                                let mii = MENUITEMINFO()
                                mii.fMask <- MenuItemInfoMask.MIIM_STRING
                                mii.cch <- 256
                                mii.dwTypeData <- new string(' ', 256)
                                WinUserApi.GetMenuItemInfo(menuH, i, true, mii) |> ignore
                                if mii.dwTypeData <> null && mii.dwTypeData.Contains("\t") then
                                    let mutable itemRect = RECT()
                                    let gotRect = WinUserApi.GetMenuItemRect(group.hwnd, menuH, i, &itemRect)
                                    if gotRect && itemRect.Right > itemRect.Left && itemRect.Bottom > itemRect.Top then
                                        // Find the popup window for this menu item using WindowFromPoint
                                        let centerPt = POINT(X = (itemRect.Left + itemRect.Right) / 2, Y = (itemRect.Top + itemRect.Bottom) / 2)
                                        let menuWnd = WinUserApi.WindowFromPoint(centerPt)
                                        // Verify this is a popup menu window (class "#32768") to avoid drawing on wrong windows
                                        let isMenuPopup =
                                            if menuWnd = IntPtr.Zero then false
                                            else
                                                let classNameBuf = System.Text.StringBuilder(32)
                                                WinUserApi.GetClassName(menuWnd, classNameBuf, 32) |> ignore
                                                classNameBuf.ToString() = "#32768"
                                        if isMenuPopup then
                                            let key = sprintf "%d_%d" (int64 menuH) i
                                            let mutable clientPt = POINT(X = itemRect.Left, Y = itemRect.Top)
                                            WinUserApi.ScreenToClient(menuWnd, &clientPt) |> ignore
                                            let cL = clientPt.X
                                            let cT = clientPt.Y
                                            let itemW = itemRect.Right - itemRect.Left
                                            let itemH = itemRect.Bottom - itemRect.Top
                                            let isOver = cursorPt.Y >= itemRect.Top && cursorPt.Y <= itemRect.Bottom
                                                         && cursorPt.X >= itemRect.Left && cursorPt.X <= itemRect.Right
                                            let onLeft = isOver && cursorPt.X <= (itemRect.Left + itemRect.Right) / 2
                                            let hdc = WinUserApi.GetDC(menuWnd)
                                            if hdc <> IntPtr.Zero then
                                                let dark = Win32Menu.isDarkMode
                                                let divX = itemW / 2
                                                let lineColor = if dark then Color.FromArgb(100, 100, 100) else Color.FromArgb(160, 160, 160)
                                                let wasOver = if splitWasOver.ContainsKey(key) then splitWasOver.[key] else false
                                                let nCached = if splitNormalCached.ContainsKey(key) then splitNormalCached.[key] else false
                                                let hCached = if splitHoverCached.ContainsKey(key) then splitHoverCached.[key] else false
                                                // Cache non-hovered state and bake divider line
                                                if not nCached && not isOver then
                                                    let memDC = WinGdiApi.CreateCompatibleDC(hdc)
                                                    let memBmp = WinGdiApi.CreateCompatibleBitmap(hdc, itemW, itemH)
                                                    let oldBmp = WinGdiApi.SelectObject(memDC, memBmp)
                                                    WinGdiApi.BitBlt(memDC, 0, 0, itemW, itemH, hdc, cL, cT, RasterOperations.SRCCOPY) |> ignore
                                                    use gNorm = Graphics.FromHdc(memDC)
                                                    use penNorm = new Pen(lineColor, 1.0f)
                                                    gNorm.DrawLine(penNorm, divX, 3, divX, itemH - 3)
                                                    splitNormalDC.[key] <- memDC
                                                    splitNormalBmp.[key] <- memBmp
                                                    splitNormalOldBmp.[key] <- oldBmp
                                                    splitNormalCached.[key] <- true
                                                // Cache hovered state on hover entry and bake divider
                                                if isOver && not wasOver && not hCached then
                                                    let memDC = WinGdiApi.CreateCompatibleDC(hdc)
                                                    let memBmp = WinGdiApi.CreateCompatibleBitmap(hdc, itemW, itemH)
                                                    let oldBmp = WinGdiApi.SelectObject(memDC, memBmp)
                                                    WinGdiApi.BitBlt(memDC, 0, 0, itemW, itemH, hdc, cL, cT, RasterOperations.SRCCOPY) |> ignore
                                                    use gHov = Graphics.FromHdc(memDC)
                                                    use penHov = new Pen(lineColor, 1.0f)
                                                    gHov.DrawLine(penHov, divX, 3, divX, itemH - 3)
                                                    splitHoverDC.[key] <- memDC
                                                    splitHoverBmp.[key] <- memBmp
                                                    splitHoverOldBmp.[key] <- oldBmp
                                                    splitHoverCached.[key] <- true
                                                splitWasOver.[key] <- isOver
                                                // Re-read cached state after potential updates
                                                let nCached = if splitNormalCached.ContainsKey(key) then splitNormalCached.[key] else false
                                                let hCached = if splitHoverCached.ContainsKey(key) then splitHoverCached.[key] else false
                                                // Composite phase: use double-buffer to prevent flicker
                                                if isOver && nCached && hCached then
                                                    // Composite to off-screen buffer, then blit in one operation
                                                    let tmpDC = WinGdiApi.CreateCompatibleDC(hdc)
                                                    let tmpBmp = WinGdiApi.CreateCompatibleBitmap(hdc, itemW, itemH)
                                                    let tmpOld = WinGdiApi.SelectObject(tmpDC, tmpBmp)
                                                    if onLeft then
                                                        WinGdiApi.BitBlt(tmpDC, 0, 0, divX, itemH, splitHoverDC.[key], 0, 0, RasterOperations.SRCCOPY) |> ignore
                                                        WinGdiApi.BitBlt(tmpDC, divX, 0, itemW - divX, itemH, splitNormalDC.[key], divX, 0, RasterOperations.SRCCOPY) |> ignore
                                                    else
                                                        WinGdiApi.BitBlt(tmpDC, 0, 0, divX, itemH, splitNormalDC.[key], 0, 0, RasterOperations.SRCCOPY) |> ignore
                                                        WinGdiApi.BitBlt(tmpDC, divX, 0, itemW - divX, itemH, splitHoverDC.[key], divX, 0, RasterOperations.SRCCOPY) |> ignore
                                                    // Single blit to screen
                                                    WinGdiApi.BitBlt(hdc, cL, cT, itemW, itemH, tmpDC, 0, 0, RasterOperations.SRCCOPY) |> ignore
                                                    WinGdiApi.SelectObject(tmpDC, tmpOld) |> ignore
                                                    WinGdiApi.DeleteObject(tmpBmp) |> ignore
                                                    WinGdiApi.DeleteDC(tmpDC) |> ignore
                                                elif not isOver && nCached then
                                                    // Restore full item to normal state (both halves)
                                                    WinGdiApi.BitBlt(hdc, cL, cT, itemW, itemH, splitNormalDC.[key], 0, 0, RasterOperations.SRCCOPY) |> ignore
                                                // Fallback divider when caches not yet captured
                                                if not nCached && not hCached then
                                                    let midX = cL + itemW / 2
                                                    use g = Graphics.FromHdc(hdc)
                                                    use pen = new Pen(lineColor, 1.0f)
                                                    g.DrawLine(pen, midX, cT + 3, midX, cT + itemH - 3)
                                                WinUserApi.ReleaseDC(menuWnd, hdc) |> ignore
                    )
                    splitTimer.Start()
                )
                Win32Menu.show group.hwnd ptScreen (this.contextMenu(hwnd, splitMenuEnabled)) darkModeEnabled
                splitTimer.Stop()
                splitTimer.Dispose()
                // Clean up split hover cached bitmaps
                for kvp in splitNormalCached do
                    if kvp.Value then
                        WinGdiApi.SelectObject(splitNormalDC.[kvp.Key], splitNormalOldBmp.[kvp.Key]) |> ignore
                        WinGdiApi.DeleteObject(splitNormalBmp.[kvp.Key]) |> ignore
                        WinGdiApi.DeleteDC(splitNormalDC.[kvp.Key]) |> ignore
                for kvp in splitHoverCached do
                    if kvp.Value then
                        WinGdiApi.SelectObject(splitHoverDC.[kvp.Key], splitHoverOldBmp.[kvp.Key]) |> ignore
                        WinGdiApi.DeleteObject(splitHoverBmp.[kvp.Key]) |> ignore
                        WinGdiApi.DeleteDC(splitHoverDC.[kvp.Key]) |> ignore
                Win32Menu.onMenuCreated <- None
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
            let newName = form.textBox.Text
            group.setTabName(hwnd, if newName.Length = 0 then None else Some(newName))
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
                    // Get all tabs in current group (copy to avoid modification during iteration)
                    let tabsToMove = group.windows.items.list

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

    member private this.splitRightTabsToPosition(hwnd: IntPtr, position: Option<string>) =
        // Split tabs from current tab to right and move to specified position
        // [A,B,C,D] with B selected -> [A] stays, [B,C,D] moves to new position
        // Approach: First detach the first tab to position (this creates a new group),
        // then move remaining tabs to that new group using moveTabToGroup
        let currentTab = Tab(hwnd)
        let tabIndex = this.ts.visualOrder.tryFindIndex((=) currentTab)

        match tabIndex with
        | Some index when index > 0 && this.ts.visualOrder.count > 1 ->
            // Get tabs to split (from current tab to right, including current)
            let tabsToSplit = this.ts.visualOrder.skip(index).list |> List.map (fun (Tab h) -> h)

            if tabsToSplit.Length > 0 then
                let firstHwnd = tabsToSplit.Head
                let remainingHwnds = tabsToSplit.Tail

                let window = os.windowFromHwnd(firstHwnd)
                let bounds = window.bounds
                let screen = this.getCurrentScreenForWindow(firstHwnd)

                // Step 1: Detach the first tab and move to position (with suspend/resume)
                Services.program.suspendTabMonitoring()

                try
                    let firstTab = Tab(firstHwnd)
                    this.ts.removeTab(firstTab)
                    group.removeWindow(firstHwnd)

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

                    notifyDetached(firstHwnd)
                finally
                    Services.program.resumeTabMonitoring()

                // Step 2: Wait for the new group to be created by WindowTabs
                // The new group is created asynchronously after notifyDetached
                let mutable newGroupFound = None
                let mutable attempts = 0
                let maxAttempts = 50 // 50 * 20ms = 1 second max wait
                while newGroupFound.IsNone && attempts < maxAttempts do
                    System.Threading.Thread.Sleep(20)
                    attempts <- attempts + 1
                    newGroupFound <- lock decorators (fun () ->
                        decorators.Values
                        |> Seq.tryFind (fun d ->
                            d.group.windows.contains firstHwnd && d.group.hwnd <> group.hwnd)
                    )

                // Step 3: Move remaining tabs to the newly created group
                if remainingHwnds.Length > 0 then
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
                                        tabWindow.showWindow(ShowWindowCommands.SW_SHOW)
                                )
                            )
                        finally
                            Services.program.resumeTabMonitoring()
                    | None -> ()
        | _ -> ()

    member private this.splitLeftTabsToPosition(hwnd: IntPtr, position: Option<string>) =
        // Split tabs from left to current tab and move to specified position
        // [A,B,C,D] with B selected -> [C,D] stays, [A,B] moves to new position
        // Approach: First detach the first tab to position (this creates a new group),
        // then move remaining tabs to that new group
        let currentTab = Tab(hwnd)
        let tabIndex = this.ts.visualOrder.tryFindIndex((=) currentTab)

        match tabIndex with
        | Some index when index < this.ts.visualOrder.count - 1 && this.ts.visualOrder.count > 1 ->
            // Get tabs to split (from left to current tab, including current)
            let tabsToSplit = this.ts.visualOrder.take(index + 1).list |> List.map (fun (Tab h) -> h)

            if tabsToSplit.Length > 0 then
                let firstHwnd = tabsToSplit.Head
                let remainingHwnds = tabsToSplit.Tail

                let window = os.windowFromHwnd(firstHwnd)
                let bounds = window.bounds
                let screen = this.getCurrentScreenForWindow(firstHwnd)

                // Step 1: Detach the first tab and move to position (with suspend/resume)
                Services.program.suspendTabMonitoring()

                try
                    let firstTab = Tab(firstHwnd)
                    this.ts.removeTab(firstTab)
                    group.removeWindow(firstHwnd)

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

                    notifyDetached(firstHwnd)
                finally
                    Services.program.resumeTabMonitoring()

                // Step 2: Wait for the new group to be created by WindowTabs
                // The new group is created asynchronously after notifyDetached
                let mutable newGroupFound = None
                let mutable attempts = 0
                let maxAttempts = 50 // 50 * 20ms = 1 second max wait
                while newGroupFound.IsNone && attempts < maxAttempts do
                    System.Threading.Thread.Sleep(20)
                    attempts <- attempts + 1
                    newGroupFound <- lock decorators (fun () ->
                        decorators.Values
                        |> Seq.tryFind (fun d ->
                            d.group.windows.contains firstHwnd && d.group.hwnd <> group.hwnd)
                    )

                // Step 3: Move remaining tabs to the newly created group
                if remainingHwnds.Length > 0 then
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
                                        tabWindow.showWindow(ShowWindowCommands.SW_SHOW)
                                )
                            )
                        finally
                            Services.program.resumeTabMonitoring()
                    | None -> ()
        | _ -> ()

    member private this.splitRightTabsToGroup(hwnd: IntPtr, targetGroup: WindowGroup) =
        // Split tabs from current tab to right and move to target group
        // [A,B,C,D] with B selected -> [A] stays, [B,C,D] moves to target group
        if targetGroup.hwnd <> group.hwnd then
            let currentTab = Tab(hwnd)
            let tabIndex = this.ts.visualOrder.tryFindIndex((=) currentTab)

            match tabIndex with
            | Some index when index > 0 && this.ts.visualOrder.count > 1 ->
                // Get tabs to split (from current tab to right, including current)
                let tabsToSplit = this.ts.visualOrder.skip(index).list |> List.map (fun (Tab h) -> h)

                if tabsToSplit.Length > 0 then
                    Services.program.suspendTabMonitoring()

                    try
                        tabsToSplit |> List.iter (fun tabHwnd ->
                            this.moveTabToGroup(tabHwnd, targetGroup)
                        )
                    finally
                        Services.program.resumeTabMonitoring()
            | _ -> ()

    member private this.splitLeftTabsToGroup(hwnd: IntPtr, targetGroup: WindowGroup) =
        // Split tabs from left to current tab and move to target group
        // [A,B,C,D] with B selected -> [C,D] stays, [A,B] moves to target group
        if targetGroup.hwnd <> group.hwnd then
            let currentTab = Tab(hwnd)
            let tabIndex = this.ts.visualOrder.tryFindIndex((=) currentTab)

            match tabIndex with
            | Some index when index < this.ts.visualOrder.count - 1 && this.ts.visualOrder.count > 1 ->
                // Get tabs to split (from left to current tab, including current)
                let tabsToSplit = this.ts.visualOrder.take(index + 1).list |> List.map (fun (Tab h) -> h)

                if tabsToSplit.Length > 0 then
                    Services.program.suspendTabMonitoring()

                    try
                        tabsToSplit |> List.iter (fun tabHwnd ->
                            this.moveTabToGroup(tabHwnd, targetGroup)
                        )
                    finally
                        Services.program.resumeTabMonitoring()
            | _ -> ()

    member private this.splitRightTabsToScreen(hwnd: IntPtr, targetScreen: Screen, position: Option<string>) =
        // Split tabs from current tab to right and move to specified screen and position
        // Approach: First detach the first tab to screen position (this creates a new group),
        // then move remaining tabs to that new group
        let currentTab = Tab(hwnd)
        let tabIndex = this.ts.visualOrder.tryFindIndex((=) currentTab)

        match tabIndex with
        | Some index when index > 0 && this.ts.visualOrder.count > 1 ->
            let tabsToSplit = this.ts.visualOrder.skip(index).list |> List.map (fun (Tab h) -> h)

            if tabsToSplit.Length > 0 then
                let firstHwnd = tabsToSplit.Head
                let remainingHwnds = tabsToSplit.Tail

                let window = os.windowFromHwnd(firstHwnd)
                let bounds = window.bounds
                let sourceScreen = this.getCurrentScreenForWindow(firstHwnd)
                let sourceWorkArea = sourceScreen.WorkingArea
                let widthPercent = float(bounds.size.width) / float(sourceWorkArea.Width)
                let heightPercent = float(bounds.size.height) / float(sourceWorkArea.Height)
                let topPercent = float(bounds.location.y - sourceWorkArea.Top) / float(sourceWorkArea.Height)

                // Step 1: Detach the first tab and move to screen position (with suspend/resume)
                Services.program.suspendTabMonitoring()

                try
                    let firstTab = Tab(firstHwnd)
                    this.ts.removeTab(firstTab)
                    group.removeWindow(firstHwnd)

                    window.hideOffScreen(None)

                    if window.isMinimized || window.isMaximized then
                        window.showWindow(ShowWindowCommands.SW_RESTORE)

                    let targetWorkArea = targetScreen.WorkingArea
                    let newWidth = int(float(targetWorkArea.Width) * widthPercent)
                    let newHeight = int(float(targetWorkArea.Height) * heightPercent)

                    let leftPercent = float(bounds.location.x - sourceWorkArea.Left) / float(sourceWorkArea.Width)
                    let currentX = targetWorkArea.Left + int(float(targetWorkArea.Width) * leftPercent)
                    let currentY = targetWorkArea.Top + int(float(targetWorkArea.Height) * topPercent)

                    let newLeft, newTop = this.calculatePositionInWorkArea(
                        position,
                        targetWorkArea,
                        currentX,
                        currentY,
                        newWidth,
                        newHeight)

                    let finalX = max targetWorkArea.Left (min newLeft (targetWorkArea.Right - newWidth))
                    let finalY = max targetWorkArea.Top (min newTop (targetWorkArea.Bottom - newHeight))

                    let initialDpi = WinUserApi.GetDpiForWindow(firstHwnd)
                    window.setPositionOnly finalX finalY

                    let mutable currentDpi = initialDpi
                    let mutable elapsed = 0
                    while elapsed < 200 && currentDpi = initialDpi do
                        System.Threading.Thread.Sleep(10)
                        elapsed <- elapsed + 10
                        currentDpi <- WinUserApi.GetDpiForWindow(firstHwnd)

                    if currentDpi <> initialDpi then
                        System.Threading.Thread.Sleep(20)

                    window.move (Rect(Pt(finalX, finalY), Sz(newWidth, newHeight)))
                    notifyDetached(firstHwnd)
                finally
                    Services.program.resumeTabMonitoring()

                // Step 2: Wait for the new group to be created by WindowTabs
                let mutable newGroupFound = None
                let mutable attempts = 0
                let maxAttempts = 50 // 50 * 20ms = 1 second max wait
                while newGroupFound.IsNone && attempts < maxAttempts do
                    System.Threading.Thread.Sleep(20)
                    attempts <- attempts + 1
                    newGroupFound <- lock decorators (fun () ->
                        decorators.Values
                        |> Seq.tryFind (fun d ->
                            d.group.windows.contains firstHwnd && d.group.hwnd <> group.hwnd)
                    )

                // Step 3: Move remaining tabs to the newly created group
                if remainingHwnds.Length > 0 then
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
                                        tabWindow.showWindow(ShowWindowCommands.SW_SHOW)
                                )
                            )
                        finally
                            Services.program.resumeTabMonitoring()
                    | None -> ()
        | _ -> ()

    member private this.splitLeftTabsToScreen(hwnd: IntPtr, targetScreen: Screen, position: Option<string>) =
        // Split tabs from left to current tab and move to specified screen and position
        // Approach: First detach the first tab to screen position (this creates a new group),
        // then move remaining tabs to that new group
        let currentTab = Tab(hwnd)
        let tabIndex = this.ts.visualOrder.tryFindIndex((=) currentTab)

        match tabIndex with
        | Some index when index < this.ts.visualOrder.count - 1 && this.ts.visualOrder.count > 1 ->
            let tabsToSplit = this.ts.visualOrder.take(index + 1).list |> List.map (fun (Tab h) -> h)

            if tabsToSplit.Length > 0 then
                let firstHwnd = tabsToSplit.Head
                let remainingHwnds = tabsToSplit.Tail

                let window = os.windowFromHwnd(firstHwnd)
                let bounds = window.bounds
                let sourceScreen = this.getCurrentScreenForWindow(firstHwnd)
                let sourceWorkArea = sourceScreen.WorkingArea
                let widthPercent = float(bounds.size.width) / float(sourceWorkArea.Width)
                let heightPercent = float(bounds.size.height) / float(sourceWorkArea.Height)
                let topPercent = float(bounds.location.y - sourceWorkArea.Top) / float(sourceWorkArea.Height)

                // Step 1: Detach the first tab and move to screen position (with suspend/resume)
                Services.program.suspendTabMonitoring()

                try
                    let firstTab = Tab(firstHwnd)
                    this.ts.removeTab(firstTab)
                    group.removeWindow(firstHwnd)

                    window.hideOffScreen(None)

                    if window.isMinimized || window.isMaximized then
                        window.showWindow(ShowWindowCommands.SW_RESTORE)

                    let targetWorkArea = targetScreen.WorkingArea
                    let newWidth = int(float(targetWorkArea.Width) * widthPercent)
                    let newHeight = int(float(targetWorkArea.Height) * heightPercent)

                    let leftPercent = float(bounds.location.x - sourceWorkArea.Left) / float(sourceWorkArea.Width)
                    let currentX = targetWorkArea.Left + int(float(targetWorkArea.Width) * leftPercent)
                    let currentY = targetWorkArea.Top + int(float(targetWorkArea.Height) * topPercent)

                    let newLeft, newTop = this.calculatePositionInWorkArea(
                        position,
                        targetWorkArea,
                        currentX,
                        currentY,
                        newWidth,
                        newHeight)

                    let finalX = max targetWorkArea.Left (min newLeft (targetWorkArea.Right - newWidth))
                    let finalY = max targetWorkArea.Top (min newTop (targetWorkArea.Bottom - newHeight))

                    let initialDpi = WinUserApi.GetDpiForWindow(firstHwnd)
                    window.setPositionOnly finalX finalY

                    let mutable currentDpi = initialDpi
                    let mutable elapsed = 0
                    while elapsed < 200 && currentDpi = initialDpi do
                        System.Threading.Thread.Sleep(10)
                        elapsed <- elapsed + 10
                        currentDpi <- WinUserApi.GetDpiForWindow(firstHwnd)

                    if currentDpi <> initialDpi then
                        System.Threading.Thread.Sleep(20)

                    window.move (Rect(Pt(finalX, finalY), Sz(newWidth, newHeight)))
                    notifyDetached(firstHwnd)
                finally
                    Services.program.resumeTabMonitoring()

                // Step 2: Wait for the new group to be created by WindowTabs
                let mutable newGroupFound = None
                let mutable attempts = 0
                let maxAttempts = 50 // 50 * 20ms = 1 second max wait
                while newGroupFound.IsNone && attempts < maxAttempts do
                    System.Threading.Thread.Sleep(20)
                    attempts <- attempts + 1
                    newGroupFound <- lock decorators (fun () ->
                        decorators.Values
                        |> Seq.tryFind (fun d ->
                            d.group.windows.contains firstHwnd && d.group.hwnd <> group.hwnd)
                    )

                // Step 3: Move remaining tabs to the newly created group
                if remainingHwnds.Length > 0 then
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
                                        tabWindow.showWindow(ShowWindowCommands.SW_SHOW)
                                )
                            )
                        finally
                            Services.program.resumeTabMonitoring()
                    | None -> ()
        | _ -> ()

    member private this.splitRightTabsToSnap(hwnd: IntPtr, snapDirection: string) =
        // Split tabs from current tab to right and snap to specified position
        let currentTab = Tab(hwnd)
        let tabIndex = this.ts.visualOrder.tryFindIndex((=) currentTab)

        match tabIndex with
        | Some index when index > 0 && this.ts.visualOrder.count > 1 ->
            let tabsToSplit = this.ts.visualOrder.skip(index).list |> List.map (fun (Tab h) -> h)

            if tabsToSplit.Length > 0 then
                let firstHwnd = tabsToSplit.Head
                let remainingHwnds = tabsToSplit.Tail

                let window = os.windowFromHwnd(firstHwnd)
                let bounds = window.bounds
                let screen = this.getCurrentScreenForWindow(firstHwnd)

                Services.program.suspendTabMonitoring()

                try
                    let firstTab = Tab(firstHwnd)
                    this.ts.removeTab(firstTab)
                    group.removeWindow(firstHwnd)

                    window.hideOffScreen(None)

                    if window.isMinimized || window.isMaximized then
                        window.showWindow(ShowWindowCommands.SW_RESTORE)

                    let (newX, newY, newWidth, newHeight) = this.calculateSnapBounds(
                        snapDirection,
                        screen.WorkingArea,
                        bounds.size.width,
                        bounds.size.height)

                    window.move (Rect(Pt(newX, newY), Sz(newWidth, newHeight)))
                    notifyDetached(firstHwnd)
                finally
                    Services.program.resumeTabMonitoring()

                // Wait for the new group to be created
                let mutable newGroupFound = None
                let mutable attempts = 0
                let maxAttempts = 50
                while newGroupFound.IsNone && attempts < maxAttempts do
                    System.Threading.Thread.Sleep(20)
                    attempts <- attempts + 1
                    newGroupFound <- lock decorators (fun () ->
                        decorators.Values
                        |> Seq.tryFind (fun d ->
                            d.group.windows.contains firstHwnd && d.group.hwnd <> group.hwnd)
                    )

                // Move remaining tabs to the newly created group
                if remainingHwnds.Length > 0 then
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
                                        tabWindow.showWindow(ShowWindowCommands.SW_SHOW)
                                )
                            )
                        finally
                            Services.program.resumeTabMonitoring()
                    | None -> ()
        | _ -> ()

    member private this.splitRightTabsToScreenSnap(hwnd: IntPtr, targetScreen: Screen, snapDirection: string) =
        // Split tabs from current tab to right and snap to specified screen position
        let currentTab = Tab(hwnd)
        let tabIndex = this.ts.visualOrder.tryFindIndex((=) currentTab)

        match tabIndex with
        | Some index when index > 0 && this.ts.visualOrder.count > 1 ->
            let tabsToSplit = this.ts.visualOrder.skip(index).list |> List.map (fun (Tab h) -> h)

            if tabsToSplit.Length > 0 then
                let firstHwnd = tabsToSplit.Head
                let remainingHwnds = tabsToSplit.Tail

                let window = os.windowFromHwnd(firstHwnd)
                let bounds = window.bounds
                let sourceScreen = this.getCurrentScreenForWindow(firstHwnd)
                let sourceWorkArea = sourceScreen.WorkingArea
                let widthPercent = float(bounds.size.width) / float(sourceWorkArea.Width)
                let heightPercent = float(bounds.size.height) / float(sourceWorkArea.Height)

                Services.program.suspendTabMonitoring()

                try
                    let firstTab = Tab(firstHwnd)
                    this.ts.removeTab(firstTab)
                    group.removeWindow(firstHwnd)

                    window.hideOffScreen(None)

                    if window.isMinimized || window.isMaximized then
                        window.showWindow(ShowWindowCommands.SW_RESTORE)

                    let targetWorkArea = targetScreen.WorkingArea
                    let newWidth = int(float(targetWorkArea.Width) * widthPercent)
                    let newHeight = int(float(targetWorkArea.Height) * heightPercent)

                    let (newX, newY, finalWidth, finalHeight) = this.calculateSnapBounds(
                        snapDirection,
                        targetWorkArea,
                        newWidth,
                        newHeight)

                    let initialDpi = WinUserApi.GetDpiForWindow(firstHwnd)
                    window.setPositionOnly newX newY

                    let mutable currentDpi = initialDpi
                    let mutable elapsed = 0
                    while elapsed < 200 && currentDpi = initialDpi do
                        System.Threading.Thread.Sleep(10)
                        elapsed <- elapsed + 10
                        currentDpi <- WinUserApi.GetDpiForWindow(firstHwnd)

                    if currentDpi <> initialDpi then
                        System.Threading.Thread.Sleep(20)

                    window.move (Rect(Pt(newX, newY), Sz(finalWidth, finalHeight)))
                    notifyDetached(firstHwnd)
                finally
                    Services.program.resumeTabMonitoring()

                // Wait for the new group to be created
                let mutable newGroupFound = None
                let mutable attempts = 0
                let maxAttempts = 50
                while newGroupFound.IsNone && attempts < maxAttempts do
                    System.Threading.Thread.Sleep(20)
                    attempts <- attempts + 1
                    newGroupFound <- lock decorators (fun () ->
                        decorators.Values
                        |> Seq.tryFind (fun d ->
                            d.group.windows.contains firstHwnd && d.group.hwnd <> group.hwnd)
                    )

                // Move remaining tabs to the newly created group
                if remainingHwnds.Length > 0 then
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
                                        tabWindow.showWindow(ShowWindowCommands.SW_SHOW)
                                )
                            )
                        finally
                            Services.program.resumeTabMonitoring()
                    | None -> ()
        | _ -> ()

    member private this.splitLeftTabsToSnap(hwnd: IntPtr, snapDirection: string) =
        // Split tabs from left to current tab and snap to specified position
        let currentTab = Tab(hwnd)
        let tabIndex = this.ts.visualOrder.tryFindIndex((=) currentTab)

        match tabIndex with
        | Some index when index < this.ts.visualOrder.count - 1 && this.ts.visualOrder.count > 1 ->
            let tabsToSplit = this.ts.visualOrder.take(index + 1).list |> List.map (fun (Tab h) -> h)

            if tabsToSplit.Length > 0 then
                let firstHwnd = tabsToSplit.Head
                let remainingHwnds = tabsToSplit.Tail

                let window = os.windowFromHwnd(firstHwnd)
                let bounds = window.bounds
                let screen = this.getCurrentScreenForWindow(firstHwnd)

                Services.program.suspendTabMonitoring()

                try
                    let firstTab = Tab(firstHwnd)
                    this.ts.removeTab(firstTab)
                    group.removeWindow(firstHwnd)

                    window.hideOffScreen(None)

                    if window.isMinimized || window.isMaximized then
                        window.showWindow(ShowWindowCommands.SW_RESTORE)

                    let (newX, newY, newWidth, newHeight) = this.calculateSnapBounds(
                        snapDirection,
                        screen.WorkingArea,
                        bounds.size.width,
                        bounds.size.height)

                    window.move (Rect(Pt(newX, newY), Sz(newWidth, newHeight)))
                    notifyDetached(firstHwnd)
                finally
                    Services.program.resumeTabMonitoring()

                // Wait for the new group to be created
                let mutable newGroupFound = None
                let mutable attempts = 0
                let maxAttempts = 50
                while newGroupFound.IsNone && attempts < maxAttempts do
                    System.Threading.Thread.Sleep(20)
                    attempts <- attempts + 1
                    newGroupFound <- lock decorators (fun () ->
                        decorators.Values
                        |> Seq.tryFind (fun d ->
                            d.group.windows.contains firstHwnd && d.group.hwnd <> group.hwnd)
                    )

                // Move remaining tabs to the newly created group
                if remainingHwnds.Length > 0 then
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
                                        tabWindow.showWindow(ShowWindowCommands.SW_SHOW)
                                )
                            )
                        finally
                            Services.program.resumeTabMonitoring()
                    | None -> ()
        | _ -> ()

    member private this.splitLeftTabsToScreenSnap(hwnd: IntPtr, targetScreen: Screen, snapDirection: string) =
        // Split tabs from left to current tab and snap to specified screen position
        let currentTab = Tab(hwnd)
        let tabIndex = this.ts.visualOrder.tryFindIndex((=) currentTab)

        match tabIndex with
        | Some index when index < this.ts.visualOrder.count - 1 && this.ts.visualOrder.count > 1 ->
            let tabsToSplit = this.ts.visualOrder.take(index + 1).list |> List.map (fun (Tab h) -> h)

            if tabsToSplit.Length > 0 then
                let firstHwnd = tabsToSplit.Head
                let remainingHwnds = tabsToSplit.Tail

                let window = os.windowFromHwnd(firstHwnd)
                let bounds = window.bounds
                let sourceScreen = this.getCurrentScreenForWindow(firstHwnd)
                let sourceWorkArea = sourceScreen.WorkingArea
                let widthPercent = float(bounds.size.width) / float(sourceWorkArea.Width)
                let heightPercent = float(bounds.size.height) / float(sourceWorkArea.Height)

                Services.program.suspendTabMonitoring()

                try
                    let firstTab = Tab(firstHwnd)
                    this.ts.removeTab(firstTab)
                    group.removeWindow(firstHwnd)

                    window.hideOffScreen(None)

                    if window.isMinimized || window.isMaximized then
                        window.showWindow(ShowWindowCommands.SW_RESTORE)

                    let targetWorkArea = targetScreen.WorkingArea
                    let newWidth = int(float(targetWorkArea.Width) * widthPercent)
                    let newHeight = int(float(targetWorkArea.Height) * heightPercent)

                    let (newX, newY, finalWidth, finalHeight) = this.calculateSnapBounds(
                        snapDirection,
                        targetWorkArea,
                        newWidth,
                        newHeight)

                    let initialDpi = WinUserApi.GetDpiForWindow(firstHwnd)
                    window.setPositionOnly newX newY

                    let mutable currentDpi = initialDpi
                    let mutable elapsed = 0
                    while elapsed < 200 && currentDpi = initialDpi do
                        System.Threading.Thread.Sleep(10)
                        elapsed <- elapsed + 10
                        currentDpi <- WinUserApi.GetDpiForWindow(firstHwnd)

                    if currentDpi <> initialDpi then
                        System.Threading.Thread.Sleep(20)

                    window.move (Rect(Pt(newX, newY), Sz(finalWidth, finalHeight)))
                    notifyDetached(firstHwnd)
                finally
                    Services.program.resumeTabMonitoring()

                // Wait for the new group to be created
                let mutable newGroupFound = None
                let mutable attempts = 0
                let maxAttempts = 50
                while newGroupFound.IsNone && attempts < maxAttempts do
                    System.Threading.Thread.Sleep(20)
                    attempts <- attempts + 1
                    newGroupFound <- lock decorators (fun () ->
                        decorators.Values
                        |> Seq.tryFind (fun d ->
                            d.group.windows.contains firstHwnd && d.group.hwnd <> group.hwnd)
                    )

                // Move remaining tabs to the newly created group
                if remainingHwnds.Length > 0 then
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
                                        tabWindow.showWindow(ShowWindowCommands.SW_SHOW)
                                )
                            )
                        finally
                            Services.program.resumeTabMonitoring()
                    | None -> ()
        | _ -> ()

    member private this.splitRightTabsToSnapWithPercent(hwnd: IntPtr, snapDirection: string, percent: int) =
        // Split tabs from current tab to right and snap with percentage
        let currentTab = Tab(hwnd)
        let tabIndex = this.ts.visualOrder.tryFindIndex((=) currentTab)

        match tabIndex with
        | Some index when index > 0 && this.ts.visualOrder.count > 1 ->
            let tabsToSplit = this.ts.visualOrder.skip(index).list |> List.map (fun (Tab h) -> h)

            if tabsToSplit.Length > 0 then
                let firstHwnd = tabsToSplit.Head
                let remainingHwnds = tabsToSplit.Tail

                let window = os.windowFromHwnd(firstHwnd)
                let screen = this.getCurrentScreenForWindow(firstHwnd)

                Services.program.suspendTabMonitoring()

                try
                    let firstTab = Tab(firstHwnd)
                    this.ts.removeTab(firstTab)
                    group.removeWindow(firstHwnd)

                    window.hideOffScreen(None)

                    if window.isMinimized || window.isMaximized then
                        window.showWindow(ShowWindowCommands.SW_RESTORE)

                    let (newX, newY, newWidth, newHeight) = this.calculateSnapBoundsWithPercent(
                        snapDirection,
                        percent,
                        screen.WorkingArea)

                    window.move (Rect(Pt(newX, newY), Sz(newWidth, newHeight)))
                    notifyDetached(firstHwnd)
                finally
                    Services.program.resumeTabMonitoring()

                let mutable newGroupFound = None
                let mutable attempts = 0
                let maxAttempts = 50
                while newGroupFound.IsNone && attempts < maxAttempts do
                    System.Threading.Thread.Sleep(20)
                    attempts <- attempts + 1
                    newGroupFound <- lock decorators (fun () ->
                        decorators.Values
                        |> Seq.tryFind (fun d ->
                            d.group.windows.contains firstHwnd && d.group.hwnd <> group.hwnd)
                    )

                if remainingHwnds.Length > 0 then
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
                                        tabWindow.showWindow(ShowWindowCommands.SW_SHOW)
                                )
                            )
                        finally
                            Services.program.resumeTabMonitoring()
                    | None -> ()
        | _ -> ()

    member private this.splitRightTabsToScreenSnapWithPercent(hwnd: IntPtr, targetScreen: Screen, snapDirection: string, percent: int) =
        // Split tabs from current tab to right and snap with percentage to target screen
        let currentTab = Tab(hwnd)
        let tabIndex = this.ts.visualOrder.tryFindIndex((=) currentTab)

        match tabIndex with
        | Some index when index > 0 && this.ts.visualOrder.count > 1 ->
            let tabsToSplit = this.ts.visualOrder.skip(index).list |> List.map (fun (Tab h) -> h)

            if tabsToSplit.Length > 0 then
                let firstHwnd = tabsToSplit.Head
                let remainingHwnds = tabsToSplit.Tail

                let window = os.windowFromHwnd(firstHwnd)

                Services.program.suspendTabMonitoring()

                try
                    let firstTab = Tab(firstHwnd)
                    this.ts.removeTab(firstTab)
                    group.removeWindow(firstHwnd)

                    window.hideOffScreen(None)

                    if window.isMinimized || window.isMaximized then
                        window.showWindow(ShowWindowCommands.SW_RESTORE)

                    let targetWorkArea = targetScreen.WorkingArea
                    let (newX, newY, newWidth, newHeight) = this.calculateSnapBoundsWithPercent(
                        snapDirection,
                        percent,
                        targetWorkArea)

                    let initialDpi = WinUserApi.GetDpiForWindow(firstHwnd)
                    window.setPositionOnly newX newY

                    let mutable currentDpi = initialDpi
                    let mutable elapsed = 0
                    while elapsed < 200 && currentDpi = initialDpi do
                        System.Threading.Thread.Sleep(10)
                        elapsed <- elapsed + 10
                        currentDpi <- WinUserApi.GetDpiForWindow(firstHwnd)

                    if currentDpi <> initialDpi then
                        System.Threading.Thread.Sleep(20)

                    window.move (Rect(Pt(newX, newY), Sz(newWidth, newHeight)))
                    notifyDetached(firstHwnd)
                finally
                    Services.program.resumeTabMonitoring()

                let mutable newGroupFound = None
                let mutable attempts = 0
                let maxAttempts = 50
                while newGroupFound.IsNone && attempts < maxAttempts do
                    System.Threading.Thread.Sleep(20)
                    attempts <- attempts + 1
                    newGroupFound <- lock decorators (fun () ->
                        decorators.Values
                        |> Seq.tryFind (fun d ->
                            d.group.windows.contains firstHwnd && d.group.hwnd <> group.hwnd)
                    )

                if remainingHwnds.Length > 0 then
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
                                        tabWindow.showWindow(ShowWindowCommands.SW_SHOW)
                                )
                            )
                        finally
                            Services.program.resumeTabMonitoring()
                    | None -> ()
        | _ -> ()

    member private this.splitLeftTabsToSnapWithPercent(hwnd: IntPtr, snapDirection: string, percent: int) =
        // Split tabs from left to current tab and snap with percentage
        let currentTab = Tab(hwnd)
        let tabIndex = this.ts.visualOrder.tryFindIndex((=) currentTab)

        match tabIndex with
        | Some index when index < this.ts.visualOrder.count - 1 && this.ts.visualOrder.count > 1 ->
            let tabsToSplit = this.ts.visualOrder.take(index + 1).list |> List.map (fun (Tab h) -> h)

            if tabsToSplit.Length > 0 then
                let firstHwnd = tabsToSplit.Head
                let remainingHwnds = tabsToSplit.Tail

                let window = os.windowFromHwnd(firstHwnd)
                let screen = this.getCurrentScreenForWindow(firstHwnd)

                Services.program.suspendTabMonitoring()

                try
                    let firstTab = Tab(firstHwnd)
                    this.ts.removeTab(firstTab)
                    group.removeWindow(firstHwnd)

                    window.hideOffScreen(None)

                    if window.isMinimized || window.isMaximized then
                        window.showWindow(ShowWindowCommands.SW_RESTORE)

                    let (newX, newY, newWidth, newHeight) = this.calculateSnapBoundsWithPercent(
                        snapDirection,
                        percent,
                        screen.WorkingArea)

                    window.move (Rect(Pt(newX, newY), Sz(newWidth, newHeight)))
                    notifyDetached(firstHwnd)
                finally
                    Services.program.resumeTabMonitoring()

                let mutable newGroupFound = None
                let mutable attempts = 0
                let maxAttempts = 50
                while newGroupFound.IsNone && attempts < maxAttempts do
                    System.Threading.Thread.Sleep(20)
                    attempts <- attempts + 1
                    newGroupFound <- lock decorators (fun () ->
                        decorators.Values
                        |> Seq.tryFind (fun d ->
                            d.group.windows.contains firstHwnd && d.group.hwnd <> group.hwnd)
                    )

                if remainingHwnds.Length > 0 then
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
                                        tabWindow.showWindow(ShowWindowCommands.SW_SHOW)
                                )
                            )
                        finally
                            Services.program.resumeTabMonitoring()
                    | None -> ()
        | _ -> ()

    member private this.splitLeftTabsToScreenSnapWithPercent(hwnd: IntPtr, targetScreen: Screen, snapDirection: string, percent: int) =
        // Split tabs from left to current tab and snap with percentage to target screen
        let currentTab = Tab(hwnd)
        let tabIndex = this.ts.visualOrder.tryFindIndex((=) currentTab)

        match tabIndex with
        | Some index when index < this.ts.visualOrder.count - 1 && this.ts.visualOrder.count > 1 ->
            let tabsToSplit = this.ts.visualOrder.take(index + 1).list |> List.map (fun (Tab h) -> h)

            if tabsToSplit.Length > 0 then
                let firstHwnd = tabsToSplit.Head
                let remainingHwnds = tabsToSplit.Tail

                let window = os.windowFromHwnd(firstHwnd)

                Services.program.suspendTabMonitoring()

                try
                    let firstTab = Tab(firstHwnd)
                    this.ts.removeTab(firstTab)
                    group.removeWindow(firstHwnd)

                    window.hideOffScreen(None)

                    if window.isMinimized || window.isMaximized then
                        window.showWindow(ShowWindowCommands.SW_RESTORE)

                    let targetWorkArea = targetScreen.WorkingArea
                    let (newX, newY, newWidth, newHeight) = this.calculateSnapBoundsWithPercent(
                        snapDirection,
                        percent,
                        targetWorkArea)

                    let initialDpi = WinUserApi.GetDpiForWindow(firstHwnd)
                    window.setPositionOnly newX newY

                    let mutable currentDpi = initialDpi
                    let mutable elapsed = 0
                    while elapsed < 200 && currentDpi = initialDpi do
                        System.Threading.Thread.Sleep(10)
                        elapsed <- elapsed + 10
                        currentDpi <- WinUserApi.GetDpiForWindow(firstHwnd)

                    if currentDpi <> initialDpi then
                        System.Threading.Thread.Sleep(20)

                    window.move (Rect(Pt(newX, newY), Sz(newWidth, newHeight)))
                    notifyDetached(firstHwnd)
                finally
                    Services.program.resumeTabMonitoring()

                let mutable newGroupFound = None
                let mutable attempts = 0
                let maxAttempts = 50
                while newGroupFound.IsNone && attempts < maxAttempts do
                    System.Threading.Thread.Sleep(20)
                    attempts <- attempts + 1
                    newGroupFound <- lock decorators (fun () ->
                        decorators.Values
                        |> Seq.tryFind (fun d ->
                            d.group.windows.contains firstHwnd && d.group.hwnd <> group.hwnd)
                    )

                if remainingHwnds.Length > 0 then
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
                                        tabWindow.showWindow(ShowWindowCommands.SW_SHOW)
                                )
                            )
                        finally
                            Services.program.resumeTabMonitoring()
                    | None -> ()
        | _ -> ()

    member private this.moveTabGroupToPosition(hwnd: IntPtr, position: Option<string>) =
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

    member private this.moveTabGroupToScreen(hwnd: IntPtr, targetScreen: Screen, position: Option<string>) =
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

    member private this.moveTabGroupToSnap(hwnd: IntPtr, snapDirection: string) =
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

    member private this.moveTabGroupToScreenSnap(hwnd: IntPtr, targetScreen: Screen, snapDirection: string) =
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

    member private this.moveTabGroupToSnapWithPercent(hwnd: IntPtr, snapDirection: string, percent: int) =
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

    member private this.moveTabGroupToScreenSnapWithPercent(hwnd: IntPtr, targetScreen: Screen, snapDirection: string, percent: int) =
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

    member private  this.contextMenu(hwnd, splitMenuEnabled: bool) =
        let checked(isChecked) = if isChecked then List2([MenuFlags.MF_CHECKED]) else List2()
        let grayed(isGrayed) = if isGrayed then List2([MenuFlags.MF_GRAYED]) else List2()

        // Helper: build position menu items for move/snap operations
        let buildPositionMenuItems (includeLeftRight: bool) (includeSnapDesktop: bool) (moveFn: string option -> unit) (snapFn: string -> unit) (snapPercentFn: string -> int -> unit) =
            let snapPercentSubMenu (pct: int) =
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
            // Key prefix for combined mode labels
            let c key = if splitMenuEnabled then "MoveSnapMenuCombined." + key else key
            let leftRightItems =
                if includeLeftRight then
                    if splitMenuEnabled then
                        [
                            CmiRegular({ text = Localization.getString(c "MoveEdgeLeft") + "\t" + Localization.getString(c "SnapLeft"); image = None; click = (fun() -> if Win32Menu.lastClickInRightHalf then snapFn("snapleft") else moveFn(Some "left")); flags = List2() })
                            CmiRegular({ text = Localization.getString(c "MoveEdgeRight") + "\t" + Localization.getString(c "SnapRight"); image = None; click = (fun() -> if Win32Menu.lastClickInRightHalf then snapFn("snapright") else moveFn(Some "right")); flags = List2() })
                        ]
                    else
                        [
                            CmiRegular({ text = Localization.getString("MoveEdgeLeft"); image = None; click = (fun() -> moveFn(Some "left")); flags = List2() })
                            CmiRegular({ text = Localization.getString("SnapLeft"); image = None; click = (fun() -> snapFn("snapleft")); flags = List2() })
                            CmiRegular({ text = Localization.getString("MoveEdgeRight"); image = None; click = (fun() -> moveFn(Some "right")); flags = List2() })
                            CmiRegular({ text = Localization.getString("SnapRight"); image = None; click = (fun() -> snapFn("snapright")); flags = List2() })
                        ]
                else []
            let separatorIfLeftRight = if includeLeftRight then [CmiSeparator] else []
            let topBottomItems =
                if splitMenuEnabled then
                    [
                        CmiRegular({ text = Localization.getString(c "MoveEdgeTop") + "\t" + Localization.getString(c "SnapTop"); image = None; click = (fun() -> if Win32Menu.lastClickInRightHalf then snapFn("snaptop") else moveFn(Some "top")); flags = List2() })
                        CmiRegular({ text = Localization.getString(c "MoveEdgeBottom") + "\t" + Localization.getString(c "SnapBottom"); image = None; click = (fun() -> if Win32Menu.lastClickInRightHalf then snapFn("snapbottom") else moveFn(Some "bottom")); flags = List2() })
                    ]
                else
                    [
                        CmiRegular({ text = Localization.getString("MoveEdgeTop"); image = None; click = (fun() -> moveFn(Some "top")); flags = List2() })
                        CmiRegular({ text = Localization.getString("SnapTop"); image = None; click = (fun() -> snapFn("snaptop")); flags = List2() })
                        CmiRegular({ text = Localization.getString("MoveEdgeBottom"); image = None; click = (fun() -> moveFn(Some "bottom")); flags = List2() })
                        CmiRegular({ text = Localization.getString("SnapBottom"); image = None; click = (fun() -> snapFn("snapbottom")); flags = List2() })
                    ]
            leftRightItems @ separatorIfLeftRight @ topBottomItems @
            [
                CmiSeparator
                CmiPopUp({
                    text = Localization.getString("MoveCorner")
                    image = None
                    items = List2([
                        CmiRegular({ text = Localization.getString("MoveEdgeTopRight"); image = None; click = (fun() -> moveFn(Some "topright")); flags = List2() })
                        CmiRegular({ text = Localization.getString("MoveEdgeTopLeft"); image = None; click = (fun() -> moveFn(Some "topleft")); flags = List2() })
                        CmiRegular({ text = Localization.getString("MoveEdgeBottomRight"); image = None; click = (fun() -> moveFn(Some "bottomright")); flags = List2() })
                        CmiRegular({ text = Localization.getString("MoveEdgeBottomLeft"); image = None; click = (fun() -> moveFn(Some "bottomleft")); flags = List2() })
                    ])
                    flags = List2()
                })
                CmiSeparator
                snapPercentSubMenu 90
                snapPercentSubMenu 70
                snapPercentSubMenu 50
                snapPercentSubMenu 30
                CmiSeparator
                CmiRegular({ text = Localization.getString("SnapMaximizeDisplay"); image = None; click = (fun() -> snapPercentFn "snapmaximizedisplay" 100); flags = List2() })
            ] @
            (if includeSnapDesktop && System.Windows.Forms.Screen.AllScreens.Length > 1 then
                [CmiRegular({ text = Localization.getString("SnapMaximizeDesktop"); image = None; click = (fun() -> snapPercentFn "snapmaximizedesktop" 100); flags = List2() })]
             else [])

        // Helper: build screen position submenu
        let buildScreenPositionSubMenu (screen: System.Windows.Forms.Screen) (isCurrentScreen: bool) (includeLeftRight: bool) (moveFn: System.Windows.Forms.Screen -> string option -> unit) (snapFn: System.Windows.Forms.Screen -> string -> unit) (snapPercentFn: System.Windows.Forms.Screen -> string -> int -> unit) (getScreenName: System.Windows.Forms.Screen -> string) =
            let screenItems =
                let samePositionItems =
                    if isCurrentScreen then []
                    else
                        [
                            CmiRegular({ text = Localization.getString("DetachTabSamePosition"); image = None; click = (fun() -> moveFn screen None); flags = List2() })
                            CmiSeparator
                        ]
                samePositionItems @
                buildPositionMenuItems includeLeftRight false
                    (fun pos -> moveFn screen pos)
                    (fun dir -> snapFn screen dir)
                    (fun dir pct -> snapPercentFn screen dir pct)
            CmiPopUp({
                text = getScreenName screen
                image = None
                items = List2(screenItems)
                flags = if isCurrentScreen then List2([MenuFlags.MF_GRAYED]) else List2()
            })

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
            let exeName = System.IO.Path.GetFileName(processPath)
            CmiRegular({
                text = String.Format(Localization.getString("NewTab"), exeName)
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
                            // Use alternative launch command for known UWP apps and dock to current group
                            Services.program.launchNewWindow group.hwnd hwnd cmd
                        | None ->
                            // Launch new window and dock to current group (regardless of auto-grouping settings)
                            Services.program.launchNewWindow group.hwnd hwnd processPath
                    with
                    | :? System.ComponentModel.Win32Exception as ex when processPath.Contains("WindowsApps") ->
                        // UWP app that we don't have an alternative for
                        let appName = System.IO.Path.GetFileNameWithoutExtension(processPath)
                        let message =
                            match Localization.currentLanguage with
                            | "Japanese" ->
                                sprintf "新規ウィンドウの起動に失敗しました。\n\nこのアプリケーション (%s) はUWPアプリのため、\n直接起動できません。\n\n代わりにスタートメニューから起動してください。" appName
                            | _ ->
                                sprintf "Failed to start new window.\n\nThis application (%s) is a UWP app and\ncannot be launched directly.\n\nPlease launch it from the Start menu instead." appName
                        MessageBox.Show(message, "WindowTabs", MessageBoxButtons.OK, MessageBoxIcon.Information) |> ignore
                        System.Diagnostics.Debug.WriteLine(sprintf "UWP app cannot be launched: %s - %s" processPath ex.Message)
                    | :? System.ComponentModel.Win32Exception as ex ->
                        // Non-UWP app error
                        let message = sprintf "Failed to start new tab:\n%s\n\nPath: %s\nError: %s"
                                              (Localization.getString("NewTab"))
                                              processPath
                                              ex.Message
                        MessageBox.Show(message, "WindowTabs Error", MessageBoxButtons.OK, MessageBoxIcon.Error) |> ignore
                        System.Diagnostics.Debug.WriteLine(sprintf "Error starting process: %s - %s" processPath ex.Message)
                    | ex ->
                        let message = sprintf "Unexpected error starting new window:\n%s" ex.Message
                        MessageBox.Show(message, "WindowTabs Error", MessageBoxButtons.OK, MessageBoxIcon.Error) |> ignore
                        System.Diagnostics.Debug.WriteLine(sprintf "Unexpected error starting process: %s - %s" processPath ex.Message)
            })

        
        let tabPositionSubMenu =
            let currentAlignment = group.getTabAlign(hwnd)
            CmiPopUp({
                text = Localization.getString("TabPositionMenu")
                image = None
                items = List2([
                    CmiRegular({
                        text = Localization.getString("AlignLeft")
                        image = None
                        flags = if currentAlignment = TopLeft then List2([MenuFlags.MF_GRAYED; MenuFlags.MF_CHECKED]) else List2()
                        click = fun() ->
                            group.setTabAlign(hwnd, TopLeft)
                    })
                    CmiRegular({
                        text = Localization.getString("AlignRight")
                        image = None
                        flags = if currentAlignment = TopRight then List2([MenuFlags.MF_GRAYED; MenuFlags.MF_CHECKED]) else List2()
                        click = fun() ->
                            group.setTabAlign(hwnd, TopRight)
                    })
                ])
                flags = List2()
            })

        let tabPinSubMenu =
            let isPinned = group.isPinned(hwnd)
            let tabName =
                let ti = this.ts.tabInfo(Tab(hwnd))
                if ti.text.Length > 20 then ti.text.Substring(0, 20) + "..." else ti.text
            let pinToggleText =
                if isPinned then
                    Localization.getString("UnpinThisTab") + " : " + tabName
                else
                    Localization.getString("PinThisTab") + " : " + tabName
            CmiPopUp({
                text = Localization.getString("PinTabMenu")
                image = None
                items = List2([
                    CmiRegular({
                        text = pinToggleText
                        image = None
                        flags = List2()
                        click = fun() ->
                            if isPinned then group.unpinTab(hwnd)
                            else group.pinTab(hwnd)
                    })
                    CmiSeparator
                    CmiRegular({
                        text = Localization.getString("PinAllTabs")
                        image = None
                        flags = if group.allPinned then List2([MenuFlags.MF_GRAYED]) else List2()
                        click = fun() -> group.pinAll()
                    })
                    CmiRegular({
                        text = Localization.getString("UnpinAllTabs")
                        image = None
                        flags = if group.nonePinned then List2([MenuFlags.MF_GRAYED]) else List2()
                        click = fun() -> group.unpinAll()
                    })
                    CmiSeparator
                    (let count = group.countToLeft(hwnd)
                     let alignGroupName =
                        match group.getTabAlign(hwnd) with
                        | TopLeft -> Localization.getString("AlignGroupTopLeft")
                        | TopRight -> Localization.getString("AlignGroupTopRight")
                     if not isPinned then
                        CmiRegular({
                            text = System.String.Format(Localization.getString("PinLeftTabsFormat"), count, alignGroupName)
                            image = None
                            flags = List2()
                            click = fun() -> group.pinLeftTabs(hwnd)
                        })
                     else
                        CmiRegular({
                            text = System.String.Format(Localization.getString("UnpinLeftTabsFormat"), count, alignGroupName)
                            image = None
                            flags = List2()
                            click = fun() -> group.unpinLeftTabs(hwnd)
                        }))
                    (let count = group.countToRight(hwnd)
                     let alignGroupName =
                        match group.getTabAlign(hwnd) with
                        | TopLeft -> Localization.getString("AlignGroupTopLeft")
                        | TopRight -> Localization.getString("AlignGroupTopRight")
                     if not isPinned then
                        CmiRegular({
                            text = System.String.Format(Localization.getString("PinRightTabsFormat"), count, alignGroupName)
                            image = None
                            flags = List2()
                            click = fun() -> group.pinRightTabs(hwnd)
                        })
                     else
                        CmiRegular({
                            text = System.String.Format(Localization.getString("UnpinRightTabsFormat"), count, alignGroupName)
                            image = None
                            flags = List2()
                            click = fun() -> group.unpinRightTabs(hwnd)
                        }))
                ])
                flags = List2()
            })

        let tabColorSubMenu =
            let currentFill = group.getTabFillColor(hwnd)
            let currentUnderline = group.getTabUnderlineColor(hwnd)
            let currentBorder = group.getTabBorderColor(hwnd)
            let tabText = this.ts.tabInfo(Tab(hwnd)).text
            let shortTabText =
                if tabText.Length <= 9 then tabText
                else tabText.Substring(0, 9) + "..."
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
            // Create a filled color swatch icon (with optional checkmark overlay)
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
            // Create an underline color icon (colored line at bottom)
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
            // Create a border color icon (colored rectangle outline)
            let createBorderIcon (color: Color) (isChecked: bool) =
                let size = 16
                let img = Img(Sz(size, size))
                let g = img.graphics
                g.Clear(Color.Transparent)
                use borderPen = new Pen(Color.FromArgb(255, int color.R, int color.G, int color.B), 2.0f)
                g.DrawRectangle(borderPen, 1, 1, size - 3, size - 3)
                if isChecked then drawCheckmark g
                img
            // Fill color items
            let fillItems =
                TabColorDefs.fillDefs |> List.map (fun def ->
                    let isChecked = isColorMatch currentFill def.color
                    CmiRegular({
                        text = Localization.getString(def.labelKey)
                        image = Some(createFillIcon def.color isChecked)
                        flags = List2()
                        click = fun() ->
                            if isChecked then group.setTabFillColor(hwnd, None)
                            else group.setTabFillColor(hwnd, Some(def.color))
                    })
                )
            // Underline color items
            let underlineItems =
                TabColorDefs.underlineDefs |> List.map (fun def ->
                    let isChecked = isColorMatch currentUnderline def.color
                    CmiRegular({
                        text = Localization.getString(def.labelKey)
                        image = Some(createUnderlineIcon def.color isChecked)
                        flags = List2()
                        click = fun() ->
                            if isChecked then group.setTabUnderlineColor(hwnd, None)
                            else group.setTabUnderlineColor(hwnd, Some(def.color))
                    })
                )
            // Border color items
            let borderItems =
                TabColorDefs.borderDefs |> List.map (fun def ->
                    let isChecked = isColorMatch currentBorder def.color
                    CmiRegular({
                        text = Localization.getString(def.labelKey)
                        image = Some(createBorderIcon def.color isChecked)
                        flags = List2()
                        click = fun() ->
                            if isChecked then group.setTabBorderColor(hwnd, None)
                            else group.setTabBorderColor(hwnd, Some(def.color))
                    })
                )
            let hasAnyColor = currentFill.IsSome || currentUnderline.IsSome || currentBorder.IsSome
            let vo = this.ts.visualOrder
            let currentTabIndex =
                vo.list |> List.tryFindIndex (fun (Tab(h)) -> h = hwnd)
                |> Option.defaultValue 0
            let leftCount = currentTabIndex
            let rightCount = vo.list.Length - currentTabIndex - 1
            let leftKey = if hasAnyColor then "TabColorApplyLeft" else "TabColorResetLeft"
            let rightKey = if hasAnyColor then "TabColorApplyRight" else "TabColorResetRight"
            let applyItems = [
                CmiSeparator
                CmiRegular({
                    text = String.Format(Localization.getString(leftKey), leftCount)
                    image = None
                    flags = if leftCount > 0 then List2() else List2([MenuFlags.MF_GRAYED])
                    click = fun() ->
                        vo.list |> List.iteri (fun i (Tab(h)) ->
                            if i < currentTabIndex then
                                group.setTabFillColor(h, currentFill)
                                group.setTabUnderlineColor(h, currentUnderline)
                                group.setTabBorderColor(h, currentBorder)
                        )
                })
                CmiRegular({
                    text = String.Format(Localization.getString(rightKey), rightCount)
                    image = None
                    flags = if rightCount > 0 then List2() else List2([MenuFlags.MF_GRAYED])
                    click = fun() ->
                        vo.list |> List.iteri (fun i (Tab(h)) ->
                            if i > currentTabIndex then
                                group.setTabFillColor(h, currentFill)
                                group.setTabUnderlineColor(h, currentUnderline)
                                group.setTabBorderColor(h, currentBorder)
                        )
                })
            ]
            let resetItems = [
                CmiSeparator
                CmiRegular({
                    text = String.Format(Localization.getString("TabColorReset"), shortTabText)
                    image = None
                    flags = if hasAnyColor then List2() else List2([MenuFlags.MF_GRAYED])
                    click = fun() ->
                        group.setTabFillColor(hwnd, None)
                        group.setTabUnderlineColor(hwnd, None)
                        group.setTabBorderColor(hwnd, None)
                })
                CmiRegular({
                    text = Localization.getString("TabColorResetAll")
                    image = None
                    flags =
                        let anyHasColor = this.ts.visualOrder.list |> List.exists (fun (Tab(h)) ->
                            group.getTabFillColor(h).IsSome || group.getTabUnderlineColor(h).IsSome || group.getTabBorderColor(h).IsSome)
                        if anyHasColor then List2() else List2([MenuFlags.MF_GRAYED])
                    click = fun() ->
                        this.ts.visualOrder.list |> List.iter (fun (Tab(h)) ->
                            group.setTabFillColor(h, None)
                            group.setTabUnderlineColor(h, None)
                            group.setTabBorderColor(h, None)
                        )
                })
            ]
            CmiPopUp({
                text = Localization.getString("TabColorMenu")
                image = None
                items = List2(fillItems @ [CmiSeparator] @ underlineItems @ [CmiSeparator] @ borderItems @ resetItems @ applyItems)
                flags = List2()
            })

        let tabNameSubMenu =
            CmiPopUp({
                text = Localization.getString("TabNameEdit")
                image = None
                items = List2([
                    CmiRegular({
                        text = Localization.getString("RenameTab")
                        image = None
                        flags = List2()
                        click = fun() ->
                            this.beginRename(hwnd)
                    })
                    CmiRegular({
                        text = Localization.getString("ResetTabName")
                        image = None
                        flags = if group.isRenamed(hwnd) then List2() else List2([MenuFlags.MF_GRAYED])
                        click = fun() -> group.setTabName(hwnd, None)
                    })
                ])
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
                text = String.Format(Localization.getString("CloseTab"), displayText)
                image = None
                click = fun() -> this.onCloseWindow hwnd
                flags = List2()
            })

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
                flags = grayed(rightTabCount = 0)
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

            let samePositionItem = [
                CmiRegular({ text = Localization.getString("DetachTabSamePosition"); image = None; click = (fun() -> this.detachTabToPosition(hwnd, None)); flags = List2() })
                CmiSeparator
            ]

            let baseMenuItems =
                samePositionItem @
                buildPositionMenuItems true true
                    (fun pos -> this.detachTabToPosition(hwnd, pos))
                    (fun dir -> this.detachTabToSnap(hwnd, dir))
                    (fun dir pct -> this.detachTabToSnapWithPercent(hwnd, dir, pct))

            let menuItems =
                if allScreens.Length > 1 then
                    let screenSubMenus =
                        allScreens
                        |> Array.map (fun screen ->
                            let isCurrentScreen = screen.Equals(currentScreen)
                            buildScreenPositionSubMenu screen isCurrentScreen true
                                (fun s pos -> this.detachTabToScreen(hwnd, s, pos))
                                (fun s dir -> this.detachTabToScreenSnap(hwnd, s, dir))
                                (fun s dir pct -> this.detachTabToScreenSnapWithPercent(hwnd, s, dir, pct))
                                (fun s -> this.getScreenName(s))
                        )
                        |> Array.toList

                    baseMenuItems @ [CmiSeparator] @ screenSubMenus
                else
                    baseMenuItems

            Some(CmiPopUp({
                text = Localization.getString("DetachAndMovePosTab")
                image = None
                items = List2(menuItems)
                flags = if isEnabled then List2() else List2([MenuFlags.MF_GRAYED])
            }))

        // Split right tabs to position submenu
        let splitRightTabsToPositionSubMenu =
            let currentTab = Tab(hwnd)
            let tabIndex = this.ts.visualOrder.tryFindIndex((=) currentTab)
            let totalTabs = this.ts.visualOrder.count

            let rightTabCount =
                match tabIndex with
                | Some index -> totalTabs - index
                | None -> 0

            let isEnabled =
                match tabIndex with
                | Some index -> totalTabs > 1 && index > 0 && index < totalTabs - 1
                | None -> false

            let allScreens = this.getAllScreensSorted()
            let currentScreen = this.getCurrentScreenForWindow(hwnd)

            let menuText = String.Format(Localization.getString("SplitRightTabsToPositionFormat"), rightTabCount)

            let samePositionItem = [
                CmiRegular({ text = Localization.getString("DetachTabSamePosition"); image = None; click = (fun() -> this.splitRightTabsToPosition(hwnd, None)); flags = List2() })
                CmiSeparator
            ]

            let baseMenuItems =
                samePositionItem @
                buildPositionMenuItems true true
                    (fun pos -> this.splitRightTabsToPosition(hwnd, pos))
                    (fun dir -> this.splitRightTabsToSnap(hwnd, dir))
                    (fun dir pct -> this.splitRightTabsToSnapWithPercent(hwnd, dir, pct))

            let menuItems =
                if allScreens.Length > 1 then
                    let screenSubMenus =
                        allScreens
                        |> Array.map (fun screen ->
                            let isCurrentScreen = screen.Equals(currentScreen)
                            buildScreenPositionSubMenu screen isCurrentScreen true
                                (fun s pos -> this.splitRightTabsToScreen(hwnd, s, pos))
                                (fun s dir -> this.splitRightTabsToScreenSnap(hwnd, s, dir))
                                (fun s dir pct -> this.splitRightTabsToScreenSnapWithPercent(hwnd, s, dir, pct))
                                (fun s -> this.getScreenName(s))
                        )
                        |> Array.toList

                    baseMenuItems @ [CmiSeparator] @ screenSubMenus
                else
                    baseMenuItems

            Some(CmiPopUp({
                text = menuText
                image = None
                items = List2(menuItems)
                flags = if isEnabled then List2() else List2([MenuFlags.MF_GRAYED])
            }))

        // Split left tabs to position submenu
        let splitLeftTabsToPositionSubMenu =
            let currentTab = Tab(hwnd)
            let tabIndex = this.ts.visualOrder.tryFindIndex((=) currentTab)
            let totalTabs = this.ts.visualOrder.count

            // Calculate left tab count (from leftmost to current tab, including current)
            let leftTabCount =
                match tabIndex with
                | Some index -> index + 1
                | None -> 0

            // Enabled when: more than 1 tab total, not at leftmost position, and not at rightmost position (index < totalTabs - 1)
            let isEnabled =
                match tabIndex with
                | Some index -> totalTabs > 1 && index > 0 && index < totalTabs - 1
                | None -> false

            let allScreens = this.getAllScreensSorted()
            let currentScreen = this.getCurrentScreenForWindow(hwnd)

            let menuText = String.Format(Localization.getString("SplitLeftTabsToPositionFormat"), leftTabCount)

            let samePositionItem = [
                CmiRegular({ text = Localization.getString("DetachTabSamePosition"); image = None; click = (fun() -> this.splitLeftTabsToPosition(hwnd, None)); flags = List2() })
                CmiSeparator
            ]

            let baseMenuItems =
                samePositionItem @
                buildPositionMenuItems true true
                    (fun pos -> this.splitLeftTabsToPosition(hwnd, pos))
                    (fun dir -> this.splitLeftTabsToSnap(hwnd, dir))
                    (fun dir pct -> this.splitLeftTabsToSnapWithPercent(hwnd, dir, pct))

            let menuItems =
                if allScreens.Length > 1 then
                    let screenSubMenus =
                        allScreens
                        |> Array.map (fun screen ->
                            let isCurrentScreen = screen.Equals(currentScreen)
                            buildScreenPositionSubMenu screen isCurrentScreen true
                                (fun s pos -> this.splitLeftTabsToScreen(hwnd, s, pos))
                                (fun s dir -> this.splitLeftTabsToScreenSnap(hwnd, s, dir))
                                (fun s dir pct -> this.splitLeftTabsToScreenSnapWithPercent(hwnd, s, dir, pct))
                                (fun s -> this.getScreenName(s))
                        )
                        |> Array.toList

                    baseMenuItems @ [CmiSeparator] @ screenSubMenus
                else
                    baseMenuItems

            Some(CmiPopUp({
                text = menuText
                image = None
                items = List2(menuItems)
                flags = if isEnabled then List2() else List2([MenuFlags.MF_GRAYED])
            }))

        // Top-level move/snap items: split mode or separate mode
        let topLevelMoveSnapItems =
            let c key = if splitMenuEnabled then "MoveSnapMenuCombined." + key else key
            if splitMenuEnabled then
                [
                    CmiRegular({
                        text = Localization.getString(c "MoveEdgeLeft") + "\t" + Localization.getString(c "SnapLeft")
                        image = None
                        click = (fun() ->
                            if Win32Menu.lastClickInRightHalf then this.moveTabGroupToSnap(hwnd, "snapleft")
                            else this.moveTabGroupToPosition(hwnd, Some "left"))
                        flags = List2()
                    })
                    CmiRegular({
                        text = Localization.getString(c "MoveEdgeRight") + "\t" + Localization.getString(c "SnapRight")
                        image = None
                        click = (fun() ->
                            if Win32Menu.lastClickInRightHalf then this.moveTabGroupToSnap(hwnd, "snapright")
                            else this.moveTabGroupToPosition(hwnd, Some "right"))
                        flags = List2()
                    })
                ]
            else
                [
                    CmiRegular({
                        text = Localization.getString("MoveEdgeLeft")
                        image = None
                        click = (fun() -> this.moveTabGroupToPosition(hwnd, Some "left"))
                        flags = List2()
                    })
                    CmiRegular({
                        text = Localization.getString("SnapLeft")
                        image = None
                        click = (fun() -> this.moveTabGroupToSnap(hwnd, "snapleft"))
                        flags = List2()
                    })
                    CmiRegular({
                        text = Localization.getString("MoveEdgeRight")
                        image = None
                        click = (fun() -> this.moveTabGroupToPosition(hwnd, Some "right"))
                        flags = List2()
                    })
                    CmiRegular({
                        text = Localization.getString("SnapRight")
                        image = None
                        click = (fun() -> this.moveTabGroupToSnap(hwnd, "snapright"))
                        flags = List2()
                    })
                ]

        let moveTabGroupSubMenu =
            let baseMenuItems =
                buildPositionMenuItems false true
                    (fun pos -> this.moveTabGroupToPosition(hwnd, pos))
                    (fun dir -> this.moveTabGroupToSnap(hwnd, dir))
                    (fun dir pct -> this.moveTabGroupToSnapWithPercent(hwnd, dir, pct))

            Some(CmiPopUp({
                text = Localization.getString("MovePositionOther")
                image = None
                items = List2(baseMenuItems)
                flags = List2()
            }))

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

        let screenDisplayItems =
            let allScreens = this.getAllScreensSorted()
            let currentScreen = this.getCurrentScreenForWindow(hwnd)
            if allScreens.Length > 1 then
                let screenSubMenus =
                    allScreens
                    |> Array.map (fun screen ->
                        let isCurrentScreen = screen.Equals(currentScreen)
                        Some(buildScreenPositionSubMenu screen isCurrentScreen true
                            (fun s pos -> this.moveTabGroupToScreen(hwnd, s, pos))
                            (fun s dir -> this.moveTabGroupToScreenSnap(hwnd, s, dir))
                            (fun s dir pct -> this.moveTabGroupToScreenSnapWithPercent(hwnd, s, dir, pct))
                            (fun s -> this.getScreenName(s)))
                    )
                    |> Array.toList
                [Some(CmiSeparator)] @ screenSubMenus @ [Some(CmiSeparator)]
            else
                []

        List2([
            Some(newWindowItem)
            Some(CmiSeparator)
        ] @ (topLevelMoveSnapItems |> List.map Some) @ [
            moveTabGroupSubMenu
        ] @ screenDisplayItems @ [
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
                            text = Localization.getString("DetachAndDockingTabToGroup")
                            image = None
                            items = List2(menuItems)
                            flags = if group.zorder.value.length <= 1 then List2([MenuFlags.MF_GRAYED]) else List2()
                        }))
                    else
                        // No other groups available
                        None

                // Build split right tabs to group menu
                let splitRightTabsToGroupMenu =
                    let currentTab = Tab(hwnd)
                    let tabIndex = this.ts.visualOrder.tryFindIndex((=) currentTab)
                    let totalTabs = this.ts.visualOrder.count

                    let rightTabCount =
                        match tabIndex with
                        | Some index -> totalTabs - index
                        | None -> 0

                    let isEnabled =
                        match tabIndex with
                        | Some index -> totalTabs > 1 && index > 0 && index < totalTabs - 1
                        | None -> false

                    let menuText = String.Format(Localization.getString("SplitRightTabsToGroupFormat"), rightTabCount)

                    if not (List.isEmpty allGroupInfos) && isEnabled then
                        let uniqueGroupInfos = allGroupInfos |> List.distinctBy (fun info -> info.hwnd)
                        let menuItems =
                            uniqueGroupInfos
                            |> List.choose (fun info ->
                                try
                                    let targetDecorator = lock decorators (fun () ->
                                        decorators.Values |> Seq.tryFind (fun d ->
                                            d.group.hwnd = info.hwnd &&
                                            WinUserApi.IsWindow(d.group.hwnd) &&
                                            WinUserApi.IsWindow(d.ts.hwnd)
                                        )
                                    )

                                    match targetDecorator with
                                    | Some decorator ->
                                        let fullNameString =
                                            if info.tabCount = 1 then
                                                let tabName = info.tabNames |> List.head
                                                if tabName.Length > 22 then tabName.Substring(0, 22) + "..." else tabName
                                            elif info.tabCount = 2 then
                                                let tabNames = info.tabNames |> List.take 2
                                                let truncatedNames = tabNames |> List.map (fun name ->
                                                    if name.Length > 9 then name.Substring(0, 9) + "..." else name)
                                                String.Join(" ", truncatedNames)
                                            else
                                                let tabNames = info.tabNames |> List.take (min 3 info.tabCount)
                                                let truncatedNames = tabNames |> List.map (fun name ->
                                                    if name.Length > 5 then name.Substring(0, 5) + "..." else name)
                                                String.Join(" ", truncatedNames)

                                        let formatString = Localization.getString("MoveTabGroupFormat")
                                        let tabWord = if info.tabCount = 1 then Localization.getString("TabSingular") else Localization.getString("TabPlural")
                                        let itemText = String.Format(formatString, info.tabCount, tabWord, fullNameString)

                                        Some(CmiRegular({
                                            text = itemText
                                            image = info.firstTabIcon
                                            click = fun() -> this.splitRightTabsToGroup(hwnd, decorator.group)
                                            flags = List2()
                                        }))
                                    | None -> None
                                with _ -> None
                            )

                        Some(CmiPopUp({
                            text = menuText
                            image = None
                            items = List2(menuItems)
                            flags = List2()
                        }))
                    else
                        Some(CmiPopUp({
                            text = menuText
                            image = None
                            items = List2([])
                            flags = List2([MenuFlags.MF_GRAYED])
                        }))

                // Build split left tabs to group menu
                let splitLeftTabsToGroupMenu =
                    let currentTab = Tab(hwnd)
                    let tabIndex = this.ts.visualOrder.tryFindIndex((=) currentTab)
                    let totalTabs = this.ts.visualOrder.count

                    let leftTabCount =
                        match tabIndex with
                        | Some index -> index + 1
                        | None -> 0

                    let isEnabled =
                        match tabIndex with
                        | Some index -> totalTabs > 1 && index > 0 && index < totalTabs - 1
                        | None -> false

                    let menuText = String.Format(Localization.getString("SplitLeftTabsToGroupFormat"), leftTabCount)

                    if not (List.isEmpty allGroupInfos) && isEnabled then
                        let uniqueGroupInfos = allGroupInfos |> List.distinctBy (fun info -> info.hwnd)
                        let menuItems =
                            uniqueGroupInfos
                            |> List.choose (fun info ->
                                try
                                    let targetDecorator = lock decorators (fun () ->
                                        decorators.Values |> Seq.tryFind (fun d ->
                                            d.group.hwnd = info.hwnd &&
                                            WinUserApi.IsWindow(d.group.hwnd) &&
                                            WinUserApi.IsWindow(d.ts.hwnd)
                                        )
                                    )

                                    match targetDecorator with
                                    | Some decorator ->
                                        let fullNameString =
                                            if info.tabCount = 1 then
                                                let tabName = info.tabNames |> List.head
                                                if tabName.Length > 22 then tabName.Substring(0, 22) + "..." else tabName
                                            elif info.tabCount = 2 then
                                                let tabNames = info.tabNames |> List.take 2
                                                let truncatedNames = tabNames |> List.map (fun name ->
                                                    if name.Length > 9 then name.Substring(0, 9) + "..." else name)
                                                String.Join(" ", truncatedNames)
                                            else
                                                let tabNames = info.tabNames |> List.take (min 3 info.tabCount)
                                                let truncatedNames = tabNames |> List.map (fun name ->
                                                    if name.Length > 5 then name.Substring(0, 5) + "..." else name)
                                                String.Join(" ", truncatedNames)

                                        let formatString = Localization.getString("MoveTabGroupFormat")
                                        let tabWord = if info.tabCount = 1 then Localization.getString("TabSingular") else Localization.getString("TabPlural")
                                        let itemText = String.Format(formatString, info.tabCount, tabWord, fullNameString)

                                        Some(CmiRegular({
                                            text = itemText
                                            image = info.firstTabIcon
                                            click = fun() -> this.splitLeftTabsToGroup(hwnd, decorator.group)
                                            flags = List2()
                                        }))
                                    | None -> None
                                with _ -> None
                            )

                        Some(CmiPopUp({
                            text = menuText
                            image = None
                            items = List2(menuItems)
                            flags = List2()
                        }))
                    else
                        Some(CmiPopUp({
                            text = menuText
                            image = None
                            items = List2([])
                            flags = List2([MenuFlags.MF_GRAYED])
                        }))

                // Wrap all detach/split menus in parent submenu
                let subMenuItems =
                    [
                        detachTabSubMenu
                        moveTabMenu
                        Some(CmiSeparator)
                        splitLeftTabsToPositionSubMenu
                        splitLeftTabsToGroupMenu
                        Some(CmiSeparator)
                        splitRightTabsToPositionSubMenu
                        splitRightTabsToGroupMenu
                    ] |> List.choose id

                if subMenuItems.IsEmpty then None
                else
                    Some(CmiPopUp({
                        text = Localization.getString("TabDetachAndSplit")
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
                    closeLeftTabsItem
                    closeRightTabsItem
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
                    group.tabActivate(tab, false)
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
                        let dragInfo = box({ tab = tab; tabOffset = tabOffset; imageOffset = imageOffset; tabInfo = tabInfo})
                        Services.dragDrop.beginDrag(this.ts.hwnd, dragImage, imageOffset, ptScreen, dragInfo)
                | MouseMiddle ->
                    group.tabActivate(tab, false)
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
                | Some(tab, index, newAlignment) ->
                    this.ts.moveTab(tab, index, newAlignment)
                    // Persist per-tab alignment to global
                    let (Tab hwnd) = tab
                    Services.program.setWindowAlignment(hwnd, Some(newAlignment))
                | None -> ()
                dragInfoCell.set(None)
                this.updateTsSlide()

    interface IDisposable with
        member this.Dispose() =
            // Unregister from the global registry
            lock decorators (fun () -> decorators.Remove(group.hwnd) |> ignore)
            lock groupInfos (fun () -> groupInfos.Remove(group.hwnd) |> ignore)
