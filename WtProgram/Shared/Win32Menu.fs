namespace Bemo
open System
open System.Drawing

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
    /// Whether the last menu click was in the right half of the menu item
    let mutable lastClickInRightHalf = false
    /// Top-level menu handle (valid while menu is displayed)
    let mutable lastTopMenuHandle = IntPtr.Zero
    /// Optional callback fired after menu is created but before TrackPopupMenuEx
    let mutable onMenuCreated : (IntPtr -> unit) option = None
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

        let rec createMenu (items:List2<_>) =
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
                    let flags = item.flags.append(MenuFlags.MF_POPUP).reduce((|||))
                    WinUserApi.AppendMenu(hMenu, flags, hSubMenu, item.text).ignore
                    addImage hSubMenu item.image
            int(hMenu)

        let hMenu = IntPtr(createMenu items)
        lastTopMenuHandle <- hMenu

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
        )
        trackTimer.Start()

        match onMenuCreated with
        | Some(callback) -> callback(hMenu)
        | None -> ()

        let id = WinUserApi.TrackPopupMenuEx(hMenu, TrackPopupMenuFlags.TPM_RETURNCMD, pt.x, pt.y, hwnd, IntPtr.Zero)

        trackTimer.Stop()
        trackTimer.Dispose()

        // Determine click position using cached data from while menu was displayed
        lastClickInRightHalf <- false
        if id <> 0 then
            match (!cachedItemRects).TryFind(id) with
            | Some(rect) ->
                let cursor = !cachedCursor
                let midX = (rect.Left + rect.Right) / 2
                lastClickInRightHalf <- cursor.X > midX
            | None -> ()

            match handlers.Value.tryFind id with
            | Some(click) -> click()
            | None -> ()
        lastTopMenuHandle <- IntPtr.Zero
        menus.Value.items.iter <| fun hMenu ->
            WinUserApi.DestroyMenu(hMenu).ignore
