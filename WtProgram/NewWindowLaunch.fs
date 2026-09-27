namespace Bemo
open System
open System.Runtime.InteropServices
open System.Text

// Shared by the tab menu and the new-tab hot key. Package activation follows
// the application's own new-window/new-tab preference.
module NewWindowLaunch =
    [<DllImport("user32.dll")>]
    extern uint32 private GetWindowThreadProcessId(nativeint hwnd, uint32& processId)
    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern nativeint private OpenProcess(uint32 access, bool inheritHandle, uint32 processId)
    [<DllImport("kernel32.dll")>]
    extern bool private CloseHandle(nativeint handle)
    [<DllImport("kernel32.dll", CharSet = CharSet.Unicode)>]
    extern int private GetApplicationUserModelId(nativeint processHandle, uint32& length, StringBuilder appId)

    [<ComImport; Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"); InterfaceType(ComInterfaceType.InterfaceIsIUnknown)>]
    type private IApplicationActivationManager =
        [<PreserveSig>]
        abstract ActivateApplication:
            [<MarshalAs(UnmanagedType.LPWStr)>] appId: string *
            [<MarshalAs(UnmanagedType.LPWStr)>] arguments: string *
            options: uint32 * processId: byref<uint32> -> int

    let private trace methodName hr =
#if DEBUG
        Diagnostics.Debug.WriteLine(sprintf "NewWindowLaunch: %s HRESULT=0x%08X" methodName hr)
#else
        ()
#endif

    let private win32Hresult error =
        if error = 0 then 0 else int (0x80070000u ||| (uint32 error &&& 0xFFFFu))

    let private appIdForWindow hwnd =
        let fromProcess () =
            let mutable pid = 0u
            GetWindowThreadProcessId(hwnd, &pid) |> ignore
            let processHandle = OpenProcess(0x1000u (* PROCESS_QUERY_LIMITED_INFORMATION *), false, pid)
            if processHandle = IntPtr.Zero then
                trace "OpenProcess" (win32Hresult (Marshal.GetLastWin32Error()))
                None
            else
                try
                    let mutable length = 0u
                    let first = GetApplicationUserModelId(processHandle, &length, null)
                    if first <> 122 || length = 0u then
                        trace "GetApplicationUserModelId(size)" (win32Hresult first)
                        None
                    else
                        let buffer = StringBuilder(int length)
                        let result = GetApplicationUserModelId(processHandle, &length, buffer)
                        trace "GetApplicationUserModelId" (win32Hresult result)
                        if result = 0 && not (String.IsNullOrWhiteSpace(buffer.ToString())) then Some(buffer.ToString())
                        else None
                finally CloseHandle(processHandle) |> ignore
        let attempt methodName read =
            try read ()
            with ex ->
                trace methodName ex.HResult
                None
        match attempt "GetApplicationUserModelId" fromProcess with
        | Some id -> Some id
        | None ->
            attempt "GetWindowAppId" (fun () ->
                let id = Win32Helper.GetWindowAppId(hwnd)
                trace "GetWindowAppId" 0
                if String.IsNullOrWhiteSpace id then None else Some id)

    let private activate appId =
        try
            let instance = Activator.CreateInstance(Type.GetTypeFromCLSID(Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")))
            try
                let manager = instance :?> IApplicationActivationManager
                let mutable pid = 0u
                let result = manager.ActivateApplication(appId, null, 0u (* AO_NONE *), &pid)
                trace "ActivateApplication" result
                result >= 0
            finally Marshal.ReleaseComObject(instance) |> ignore
        with ex ->
            trace "ActivateApplication" ex.HResult
            false

    /// Direct launches retain their existing placement callbacks. Activation
    /// may reuse an existing window; it deliberately does not manufacture a
    /// pending placement request for a window that might never be created.
    let start (hwnd: IntPtr) (processPath: string) (launch: string -> unit) =
        let showUwpError () =
            let appName = IO.Path.GetFileNameWithoutExtension(processPath)
            let message = String.Format(Localization.getString("NewLaunchErrorUWP"), appName)
            AppDialog.info "WindowTabs" message
        try
            let direct path =
                try launch path
                with ex ->
                    trace "Direct launch" ex.HResult
                    reraise()
            if not (LaunchPath.startWith processPath direct (fun () -> appIdForWindow hwnd) activate) then
                showUwpError ()
        with
        | :? System.ComponentModel.Win32Exception as ex ->
            let message = String.Format(Localization.getString("NewLaunchErrorProcess"), processPath, ex.Message)
            AppDialog.info "WindowTabs Error" message
            trace "Process launch failed" ex.HResult
        | ex ->
            let message = String.Format(Localization.getString("NewLaunchErrorUnexpected"), ex.Message)
            AppDialog.info "WindowTabs Error" message
            trace "Unexpected launch failure" ex.HResult
