namespace Bemo
open System
open System.Runtime.InteropServices

module DarkMode =
    [<DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")>]
    extern int private DwmSetWindowAttributeNative(IntPtr hwnd, int attr, int& pvAttribute, int cbAttribute)

    [<DllImport("uxtheme.dll", CharSet = CharSet.Unicode, EntryPoint = "SetWindowTheme")>]
    extern int private SetWindowTheme(IntPtr hwnd, string pszSubAppName, string pszSubIdList)

    [<DllImport("kernel32.dll", CharSet = CharSet.Unicode)>]
    extern IntPtr private LoadLibraryW(string lpLibFileName)

    [<DllImport("kernel32.dll")>]
    extern IntPtr private GetProcAddress(IntPtr hModule, IntPtr lpProcName)

    [<UnmanagedFunctionPointer(CallingConvention.Winapi)>]
    type SetPreferredAppModeDelegate = delegate of int -> int

    [<UnmanagedFunctionPointer(CallingConvention.Winapi)>]
    type FlushMenuThemesDelegate = delegate of unit -> unit

    let DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19
    let DWMWA_USE_IMMERSIVE_DARK_MODE = 20

    let mutable private setPreferredAppMode : SetPreferredAppModeDelegate option = None
    let mutable private flushMenuThemes : FlushMenuThemesDelegate option = None

    // Initialize dark mode for menus
    let private initializeDarkModeForMenus() =
        try
            let hUxtheme = LoadLibraryW("uxtheme.dll")
            if hUxtheme <> IntPtr.Zero then
                // ordinal 135 = SetPreferredAppMode (Windows 10 1903+)
                let pSetPreferredAppMode = GetProcAddress(hUxtheme, IntPtr(135))
                if pSetPreferredAppMode <> IntPtr.Zero then
                    setPreferredAppMode <- Some(Marshal.GetDelegateForFunctionPointer<SetPreferredAppModeDelegate>(pSetPreferredAppMode))

                // ordinal 136 = FlushMenuThemes
                let pFlushMenuThemes = GetProcAddress(hUxtheme, IntPtr(136))
                if pFlushMenuThemes <> IntPtr.Zero then
                    flushMenuThemes <- Some(Marshal.GetDelegateForFunctionPointer<FlushMenuThemesDelegate>(pFlushMenuThemes))

        with
        | ex ->
            System.Diagnostics.Debug.WriteLine(sprintf "Failed to initialize dark mode menu APIs: %s" ex.Message)

    do initializeDarkModeForMenus()

    let setDarkModeForMenus(enabled: bool) =
        try
            match setPreferredAppMode with
            | Some(func) ->
                // 0 = Default (Light mode), 1 = AllowDark (Dark mode)
                let mode = if enabled then 1 else 0
                func.Invoke(mode) |> ignore
            | None -> ()

            match flushMenuThemes with
            | Some(func) ->
                func.Invoke()
            | None -> ()
        with
        | ex -> ()

    let useImmersiveDarkMode (handle: IntPtr) (enabled: bool) =
        try
            // Always try to apply dark mode on Windows (don't check version)
            // First set the window theme
            if enabled then
                SetWindowTheme(handle, "DarkMode_Explorer", null) |> ignore

            // Try both attribute values to ensure compatibility
            let mutable value = if enabled then 1 else 0

            // Try the newer attribute first (Windows 11/10 20H1+)
            let result1 = DwmSetWindowAttributeNative(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, &value, sizeof<int>)

            // Also try the older attribute (Windows 10 older builds)
            let result2 = DwmSetWindowAttributeNative(handle, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, &value, sizeof<int>)

            result1 = 0 || result2 = 0
        with
        | _ -> false
