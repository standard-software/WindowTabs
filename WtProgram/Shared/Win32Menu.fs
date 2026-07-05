namespace Bemo
open System
open System.Drawing
open System.Runtime.InteropServices

type ContextMenuItem =
    | CmiRegular of CmiRegular
    | CmiSeparator
    | CmiPopUp of CmiPopUp

and CmiRegular = {
    text: string
    image: Option<Img>
    click: unit -> unit
    flags: List2<int>
    }

and CmiPopUp = {
    text: string
    image: Option<Img>
    items: List2<ContextMenuItem>
    flags: List2<int>
    }

module Win32Menu =
    /// Top-level menu handle (valid while menu is displayed)
    let mutable lastTopMenuHandle = IntPtr.Zero
    /// Whether dark mode is enabled for the current menu
    let mutable isDarkMode = false

    let show (hwnd:IntPtr) (pt:Pt) (items:List2<_>) (enableDarkMode:bool) =
        // Set dark mode or light mode for this menu
        DarkMode.setDarkModeForMenus(enableDarkMode)
        isDarkMode <- enableDarkMode

        let id = ref 0

        let nextId() =
            id := id.Value + 1
            id.Value

        let handlers = ref (Map2())

        let menus = ref (Set2())

        // Track item ID to (menuHandle, zero-based position) for GetMenuItemRect
        let idToMenuPos = ref (Map.empty<int, IntPtr * int>)
        let menuPosCounter = ref (Map.empty<IntPtr, int>)

        // Track submenu handle to its parent menu handle (cascade hierarchy)
        let subMenuParent = ref (Map.empty<IntPtr, IntPtr>)

        let rec createMenu (items:List2<_>) : int =
            let hMenu = WinUserApi.CreatePopupMenu()
            let addImage id (image:Option<Img>) =
                image.iter <| fun image ->
                    let hBitmap = image.resize(Sz(16,16)).hbitmap
                    WinUserApi.SetMenuItemBitmaps(hMenu, id, 1, hBitmap, hBitmap).ignore
            menus := menus.Value.add hMenu
            menuPosCounter := (!menuPosCounter).Add(hMenu, 0)
            items.iter <| fun item ->
                let pos = (!menuPosCounter).[hMenu]
                menuPosCounter := (!menuPosCounter).Add(hMenu, pos + 1)
                match item with
                | CmiRegular(item) ->
                    let id = nextId()
                    idToMenuPos := (!idToMenuPos).Add(id, (hMenu, pos))
                    WinUserApi.AppendMenu(hMenu,
                        item.flags.append(MenuFlags.MF_STRING).reduce((|||)),
                        id, item.text).ignore
                    addImage id item.image
                    handlers := handlers.Value.add id item.click
                | CmiSeparator ->
                    let id = nextId()
                    WinUserApi.AppendMenu(hMenu, MenuFlags.MF_SEPARATOR, id, "").ignore
                | CmiPopUp(item) ->
                    let hSubMenu = createMenu item.items
                    subMenuParent := (!subMenuParent).Add(IntPtr(hSubMenu), hMenu)
                    let flags = item.flags.append(MenuFlags.MF_POPUP).reduce((|||))
                    WinUserApi.AppendMenu(hMenu, flags, hSubMenu, item.text).ignore
                    addImage hSubMenu item.image
            int(hMenu)

        let hMenu = IntPtr(createMenu items)
        lastTopMenuHandle <- hMenu

        // Windows opens a submenu to the left only when it does not fit on the
        // right, and each level decides independently.  So when level 2 opens
        // to the left (menu near the right screen edge), level 3 can still
        // open to the right and cover level 1.  Enforce a consistent cascade:
        // once a submenu has opened to the left of its parent, place its child
        // submenus on the left side too (unless there is no room on the left).
        let windowRect (w:IntPtr) =
            let mutable r = RECT()
            WinUserApi.GetWindowRect(w, &r) |> ignore
            r
        let centerX (r:RECT) = (r.Left + r.Right) / 2
        // Map our currently visible menus to their windows (class #32768)
        let visibleMenuWindows() =
            let rec collectMenuWindows (prev:IntPtr) acc =
                let w = WinUserApi.FindWindowEx(IntPtr.Zero, prev, "#32768", IntPtr.Zero)
                if w = IntPtr.Zero then acc else collectMenuWindows w (w :: acc)
            collectMenuWindows IntPtr.Zero []
            |> List.choose(fun w ->
                if WinUserApi.IsWindowVisible(w) then
                    let hm = WinUserApi.SendMessage(w, WindowMessages.MN_GETHMENU, IntPtr.Zero, IntPtr.Zero)
                    if menus.Value.contains(hm) then Some(hm, w) else None
                else None)
            |> Map.ofList
        // If childMenu's parent submenu opened to the left of the grandparent
        // menu and childMenu (proposed at x, width wide) would open on the
        // right side of the parent, return the x that places it on the left.
        let leftCascadeX (childMenu:IntPtr) (x:int) (width:int) =
            match (!subMenuParent).TryFind(childMenu) with
            | None -> None
            | Some(parentMenu) ->
                let hmenuToHwnd = visibleMenuWindows()
                let parentWnd = hmenuToHwnd.TryFind(parentMenu)
                let grandWnd = (!subMenuParent).TryFind(parentMenu) |> Option.bind hmenuToHwnd.TryFind
                match parentWnd, grandWnd with
                | Some(pw), Some(gw) ->
                    let pr, gr = windowRect pw, windowRect gw
                    let parentOpenedLeft = centerX pr < centerX gr
                    let childOnRight = x + width / 2 > centerX pr
                    if parentOpenedLeft && childOnRight then
                        // Reproduce the horizontal overlap Windows used
                        // between the parent and grandparent menus
                        let overlapX = pr.Right - gr.Left
                        let newX = pr.Left - width + overlapX
                        if newX >= System.Windows.Forms.SystemInformation.VirtualScreen.Left
                        then Some(newX) else None
                    else None
                | _ -> None

        // Primary, flicker-free path: a thread-local CBT hook subclasses each
        // menu window as it is created, and the subclass rewrites the
        // WM_WINDOWPOSCHANGING that positions the window before it is shown.
        let subclassed = ref (Map.empty<IntPtr, IntPtr>)
        let removeSubclass (hwndMenu:IntPtr) =
            match (!subclassed).TryFind(hwndMenu) with
            | Some(orig) ->
                WinUserApi.SetWindowLong(hwndMenu, WindowLongFieldOffset.GWL_WNDPROC, orig) |> ignore
                subclassed := (!subclassed).Remove(hwndMenu)
            | None -> ()
        let menuWndProc = WNDPROC(fun hwndMenu msg wParam lParam ->
            let orig = match (!subclassed).TryFind(hwndMenu) with Some(o) -> o | None -> IntPtr.Zero
            if msg = WindowMessages.WM_WINDOWPOSCHANGING then
                try
                    let wp = Marshal.PtrToStructure(lParam, typeof<WINDOWPOS>) :?> WINDOWPOS
                    if wp.IsMove then
                        let hm = WinUserApi.SendMessage(hwndMenu, WindowMessages.MN_GETHMENU, IntPtr.Zero, IntPtr.Zero)
                        let width = if wp.IsSize then wp.cx else (let r = windowRect hwndMenu in r.Right - r.Left)
                        match leftCascadeX hm wp.x width with
                        | Some(newX) ->
                            wp.x <- newX
                            Marshal.StructureToPtr(wp, lParam, false)
                        | None -> ()
                with _ -> ()
            if msg = WindowMessages.WM_NCDESTROY then removeSubclass hwndMenu
            if orig = IntPtr.Zero then 0
            else WinUserApi.CallWindowProc(orig, hwndMenu, msg, wParam, lParam))
        let cbtProc = HOOKPROC(fun nCode wParam lParam ->
            if nCode = WindowHookCbtEvents.HCBT_CREATEWND then
                try
                    if Win32Helper.GetClassName(wParam) = "#32768" then
                        let orig = WinUserApi.SetWindowLong(wParam, WindowLongFieldOffset.GWL_WNDPROC, menuWndProc)
                        subclassed := (!subclassed).Add(wParam, orig)
                with _ -> ()
            WinUserApi.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam))
        let menuWndProcHandle = GCHandle.Alloc(menuWndProc)
        let cbtProcHandle = GCHandle.Alloc(cbtProc)
        let cbtHook = WinUserApi.SetWindowsHookEx(WindowHookTypes.WH_CBT, cbtProc, IntPtr.Zero, WinBaseApi.GetCurrentThreadId())

        // Fallback: fix up any submenu that was still shown on the wrong side
        let movedSubMenus = ref (Set.empty<IntPtr>)
        let enforceCascadeDirection() =
            let hmenuToHwnd = visibleMenuWindows()
            // A submenu that closed becomes eligible for repositioning again
            movedSubMenus := (!movedSubMenus) |> Set.filter hmenuToHwnd.ContainsKey
            for kvp in hmenuToHwnd do
                let childMenu, cw = kvp.Key, kvp.Value
                if not ((!movedSubMenus).Contains(childMenu)) then
                    let cr = windowRect cw
                    match leftCascadeX childMenu cr.Left (cr.Right - cr.Left) with
                    | Some(newX) ->
                        WinUserApi.SetWindowPos(cw, IntPtr.Zero, newX, cr.Top, 0, 0,
                            SetWindowPosFlags.SWP_NOSIZE ||| SetWindowPosFlags.SWP_NOZORDER ||| SetWindowPosFlags.SWP_NOACTIVATE).ignore
                        movedSubMenus := (!movedSubMenus).Add(childMenu)
                    | None -> ()

        // Timer to cache cursor position and item rects while menu is displayed
        let cachedCursor = ref (POINT())
        let cachedItemRects = ref (Map.empty<int, RECT>)
        let trackTimer = new System.Windows.Forms.Timer(Interval = 16)
        trackTimer.Tick.Add(fun _ ->
            let mutable pt = POINT()
            WinUserApi.GetCursorPos(&pt) |> ignore
            cachedCursor := pt
            for kvp in !idToMenuPos do
                let (menuHandle, pos) = kvp.Value
                let mutable rect = RECT()
                if WinUserApi.GetMenuItemRect(hwnd, menuHandle, pos, &rect) then
                    cachedItemRects := (!cachedItemRects).Add(kvp.Key, rect)
            try enforceCascadeDirection() with _ -> ()
        )
        trackTimer.Start()

        let id = WinUserApi.TrackPopupMenuEx(hMenu, TrackPopupMenuFlags.TPM_RETURNCMD, pt.x, pt.y, hwnd, IntPtr.Zero)

        trackTimer.Stop()
        trackTimer.Dispose()

        WinUserApi.UnhookWindowsHookEx(cbtHook).ignore
        // Restore any menu window that is somehow still subclassed
        (!subclassed) |> Map.toList |> List.iter(fun (hwndMenu, _) -> removeSubclass hwndMenu)
        cbtProcHandle.Free()
        menuWndProcHandle.Free()

        if id <> 0 then
            match handlers.Value.tryFind id with
            | Some(click) -> click()
            | None -> ()
        lastTopMenuHandle <- IntPtr.Zero
        menus.Value.items.iter <| fun hMenu ->
            WinUserApi.DestroyMenu(hMenu).ignore
