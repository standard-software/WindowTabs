namespace Bemo
open System
open System.Collections.Generic
open System.Drawing
open System.Runtime.InteropServices
open System.Threading
open System.Windows.Forms

// Frosted glass that shows the windows behind a translucent tab, blurred by
// Windows itself (the first choice; TabGlass's blurred wallpaper is the
// second, the plain translucent strip the last).
//
// A window of its own sits directly below each strip window that has
// translucent tabs. Windows.UI.Composition fills it with a host backdrop
// brush: whatever is on screen behind it - other windows included - blurred
// by DWM, live. The strip is shown unchanged on top, its translucent tabs at
// the opacity they always had and nothing of the wallpaper mixed in, so
// their colour lies over that blur instead of over the sharp screen.
//
//  * Why a second window: the strip is a per-pixel-alpha layered window
//    (UpdateLayeredWindow). Composition content bound to a window is always
//    drawn on top of what the window draws itself, so on the strip it would
//    cover the tabs. The backdrop window has no redirection surface at all
//    (WS_EX_NOREDIRECTIONBITMAP); composition is all it shows.
//  * Only the tab shapes: one sprite per rectangle of the strip's
//    translucent tab pixels (TabGlassMath.glassRects), so the gaps between
//    tabs stay as they were. The window region is cut to the same
//    rectangles, which keeps the window out of hit testing everywhere the
//    strip does not already take the click.
//  * Why a host backdrop brush and not DWM's system backdrop
//    (DWMWA_SYSTEMBACKDROP_TYPE): the system backdrop turns to a solid
//    colour while its window is inactive, and these windows are never
//    active - that was the grey plate of an earlier attempt.
//  * Where Windows itself stops blurring - transparency effects off, energy
//    saver on, high contrast, effects the compositor reports unsupported -
//    this is not used, and the strip falls back to TabGlass.
//
// Threads. The host owns every HWND and Composition object. Strip threads
// publish native window operations and wait for at most a short budget, never
// synchronously send SetWindowPos/SetWindowRgn to the host. A timed-out slot is
// disabled and reclaimed by the host when it can run again. Backgrounds are
// hidden before shape/position changes and shown only after the strip update.
//
// The host thread ends at exit (shutdown): every backdrop and composition
// object is let go, and the DispatcherQueue is shut down while the thread
// still pumps its messages, before the thread ends.
//
// Debug builds write what happened - the way each strip window took and
// why, every API result, the conditions read, and where each backdrop
// window really is - to tab_glass_trace.log (TabGlassTrace).

/// The backdrop windows, for the code that reads the order of top-level
/// windows: each belongs to the strip directly above it, and is skipped as
/// if the strip were directly above whatever lies below the backdrop.
module TabGlassBackdrops =
    let private windows = System.Collections.Concurrent.ConcurrentDictionary<nativeint, byte>()
    let register (hwnd: nativeint) = windows.[hwnd] <- 0uy
    let unregister (hwnd: nativeint) = windows.TryRemove(hwnd) |> ignore
    let isBackdrop (hwnd: nativeint) = hwnd <> 0n && windows.ContainsKey hwnd
    /// `order` without the backdrop windows.
    let without (order: nativeint[]) =
        if windows.IsEmpty then order else order |> Array.filter (fun hwnd -> not (windows.ContainsKey hwnd))
    /// `hwnd`, or, while it is a backdrop window, the next window `step`
    /// gives (GW_HWNDNEXT or GW_HWNDPREV), bounded.
    let skip (step: nativeint -> nativeint) (hwnd: nativeint) =
        let rec go hwnd remaining =
            if remaining > 0 && isBackdrop hwnd then go (step hwnd) (remaining - 1) else hwnd
        go hwnd 64

/// Debug-only record of the tab glass in tab_glass_trace.log, in the same
/// folder as vdesktop_trace.log and found the same way. The previous run's
/// file is kept as tab_glass_trace.old.log. `note` writes a line only when
/// the state under its key changed, so a strip drawn again and again the
/// same way writes nothing. Window handles, rectangles, HRESULTs and
/// settings only: no window titles, no paths.
module TabGlassTrace =
#if DEBUG
    let private path =
        IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WindowTabs", "tab_glass_trace.log")
    let mutable private started = false
    let private gate = obj()
    let private rotate () =
        if IO.File.Exists(path) && IO.FileInfo(path).Length > 0L then
            let old = IO.Path.ChangeExtension(path, "old.log")
            if IO.File.Exists(old) then IO.File.Delete(old)
            IO.File.Move(path, old)
    let private writer = TraceWriter.Queued(fun line ->
        TraceWriter.write gate (fun () -> line) (fun line ->
            IO.Directory.CreateDirectory(IO.Path.GetDirectoryName(path)) |> ignore
            if not started then
                rotate ()
                started <- true
            elif IO.File.Exists(path) && IO.FileInfo(path).Length > 4L * 1024L * 1024L then rotate ()
            IO.File.AppendAllText(path, line)))
    let private last = Dictionary<string, string>()

    let log (f: unit -> string) =
        writer.Post(fun () ->
            sprintf "%s t%d %s\r\n" (DateTime.Now.ToString("HH:mm:ss.fff")) Thread.CurrentThread.ManagedThreadId (f()))

    /// `f` gives (state, line): the line is written when the state differs
    /// from the last one noted under `key`.
    let note (key: string) (f: unit -> string * string) =
        try
            let state, line = f()
            let changed =
                lock last (fun () ->
                    match last.TryGetValue key with
                    | true, previous when previous = state -> false
                    | _ ->
                        last.[key] <- state
                        true)
            if changed then log (fun () -> line)
        with _ -> ()

    /// Drops the remembered states whose key starts with `prefix`.
    let forget (prefix: string) =
        lock last (fun () ->
            for key in List.ofSeq last.Keys do
                if key.StartsWith(prefix, StringComparison.Ordinal) then last.Remove key |> ignore)
#else
    let inline log (_f: unit -> string) = ()
    let inline note (_key: string) (_f: unit -> string * string) = ()
    let inline forget (_prefix: string) = ()
#endif

/// The pure choice of way, kept apart for the fsi tests.
module TabGlassWay =
    /// WINDOWTABS_DEBUG_GLASS (Debug builds only): `host` tries Windows' blur
    /// whatever the conditions say, `host-probe` does the same with a plain
    /// magenta brush in place of the blur (to see where the backdrop windows
    /// are, apart from what DWM draws in them), `wallpaper` skips Windows'
    /// blur, `plain` skips the wallpaper too.
    type Forced =
        | Auto
        | Host
        | Probe
        | Wallpaper
        | Plain

    let parseForced (value: string) =
        match (if isNull value then "" else value.Trim().ToLowerInvariant()) with
        | "host" -> Host
        | "host-probe" -> Probe
        | "wallpaper" -> Wallpaper
        | "plain" -> Plain
        | _ -> Auto

    let forcedName forced =
        match forced with
        | Auto -> "none"
        | Host -> "host"
        | Probe -> "host-probe"
        | Wallpaper -> "wallpaper"
        | Plain -> "plain"

    /// What Windows says that bears on its blur. None: could not be read.
    type Conditions =
        { transparency: bool
          highContrast: bool
          /// SYSTEM_POWER_STATUS.SystemStatusFlag = 1 (battery saver on).
          powerSaver: bool
          /// PowerManager.EnergySaverStatus: 0 Disabled, 1 Off, 2 On.
          energySaver: int option
          remote: bool
          effectsSupported: bool option
          effectsFast: bool option }

    let unread =
        { transparency = true; highContrast = false; powerSaver = false; energySaver = None; remote = false
          effectsSupported = None; effectsFast = None }

    let describe (c: Conditions) =
        let known value =
            match value with
            | Some true -> "yes"
            | Some false -> "no"
            | None -> "unknown"
        let saver =
            match c.energySaver with
            | Some 0 -> "disabled"
            | Some 1 -> "off"
            | Some 2 -> "on"
            | Some other -> string other
            | None -> "unknown"
        sprintf "transparency=%b highContrast=%b powerSaver=%b energySaver=%s remote=%b effectsSupported=%s effectsFast=%s"
            c.transparency c.highContrast c.powerSaver saver c.remote (known c.effectsSupported) (known c.effectsFast)

    /// Energy saver is on. PowerManager says so where it can be read (Disabled
    /// - plugged in, say - is not On); the battery saver flag only otherwise.
    let savingEnergy (c: Conditions) =
        match c.energySaver with
        | Some status -> status = 2
        | None -> c.powerSaver

    /// Why Windows does not blur now, or None. A remote session and slow
    /// effects are only recorded: nothing documents the brush failing there.
    let blockedBy (c: Conditions) =
        if not c.transparency then Some "transparency-effects-off"
        elif c.highContrast then Some "high-contrast"
        elif savingEnergy c then Some "energy-saver"
        elif c.effectsSupported = Some false then Some "effects-unsupported"
        else None

    /// How far the compositor thread got.
    type Start =
        | NotStarted
        | Starting
        | Ready
        | Failed of string

    /// Why a strip window does not get Windows' blur now, or None to use it.
    let hostRefused (forced: Forced) (start: Start) (conditions: Conditions) (windowFailed: bool) : string option =
        match forced with
        | Wallpaper -> Some "forced-wallpaper"
        | Plain -> Some "forced-plain"
        | Auto | Host | Probe ->
            match start with
            | NotStarted | Starting -> Some "host-starting"
            | Failed reason -> Some("host-start-failed " + reason)
            | Ready when windowFailed -> Some "host-window-failed"
            | Ready ->
                match forced with
                | Host | Probe -> None
                | _ -> blockedBy conditions

    /// Whether the rectangles of a strip window's translucent tab pixels can
    /// be given the blur this time: some, and no more than `limit`. Neither
    /// is lasting - the next drawing decides again.
    let maskRefused (count: int) (limit: int) =
        if count = 0 then Some "mask-empty"
        elif count > limit then Some(sprintf "mask-too-complex rects=%d" count)
        else None

    /// Only Completed proves that the current-thread queue can be released.
    let queueCompleted status = status = Some 1

    let nextFailures count succeeded = if succeeded then 0 else min 3 (count + 1)
    let failuresExhausted count = count >= 3

    /// A failed attempt cools down until another drawing or notification arrives.
    let retryDue now retryAfter = retryAfter > 0L && now >= retryAfter
    let transientFailure reason =
        match reason with
        | "backdrop-cloaked" | "not-below-strip" | "bounds-changed"
        | "strip-hidden" | "backdrop-hidden" | "layer-mismatch"
        | "place-again" | "place-again-exhausted" -> true
        | _ -> false

    /// Retry budget includes transient failures; only their first wait is shorter.
    let retryDelay failures transient =
        if failuresExhausted failures then -1L
        elif failures = 0 then (if transient then 500L else 5000L)
        elif failures = 1 then 10000L
        else 20000L

    // SetWindowPos flags.
    let SWP_NOSIZE = 0x1u
    let SWP_NOMOVE = 0x2u
    let SWP_NOZORDER = 0x4u
    let SWP_NOACTIVATE = 0x10u
    let SWP_SHOWWINDOW = 0x40u
    let SWP_HIDEWINDOW = 0x80u
    let SWP_NOOWNERZORDER = 0x200u

    /// Whether a backdrop must be moved to sit directly below its strip, at
    /// the strip's rectangle, shown.
    let placeNeeded (directlyBelow: bool) (moved: bool) (shown: bool) =
        not directlyBelow || moved || not shown

    /// The SetWindowPos flags that do it, changing only what differs; never
    /// activating, never moving owned windows.
    let placeFlags (directlyBelow: bool) (moved: bool) (shown: bool) =
        SWP_NOACTIVATE ||| SWP_NOOWNERZORDER |||
        (if directlyBelow then SWP_NOZORDER else 0u) |||
        (if moved then 0u else SWP_NOMOVE ||| SWP_NOSIZE) |||
        (if shown then 0u else SWP_SHOWWINDOW)

    /// Whether a pending WINDOWPOS exposes pixels outside the old strip.
    let geometryChanging (before: Rectangle) (after: Rectangle) (flags: uint32) =
        flags &&& SWP_HIDEWINDOW <> 0u ||
        (flags &&& SWP_NOMOVE = 0u && before.Location <> after.Location) ||
        (flags &&& SWP_NOSIZE = 0u && before.Size <> after.Size)

    /// The flags that hide a backdrop and change nothing else.
    let hideFlags =
        SWP_HIDEWINDOW ||| SWP_NOMOVE ||| SWP_NOSIZE ||| SWP_NOZORDER ||| SWP_NOACTIVATE ||| SWP_NOOWNERZORDER

module private TabGlassHostNative =
    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type DispatcherQueueOptions =
        val mutable dwSize: int
        val mutable threadType: int
        val mutable apartmentType: int

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type Vector2 =
        val mutable X: float32
        val mutable Y: float32
        new(x, y) = { X = x; Y = y }

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type Vector3 =
        val mutable X: float32
        val mutable Y: float32
        val mutable Z: float32
        new(x, y, z) = { X = x; Y = y; Z = z }

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type SystemPowerStatus =
        val mutable ACLineStatus: byte
        val mutable BatteryFlag: byte
        val mutable BatteryLifePercent: byte
        val mutable SystemStatusFlag: byte
        val mutable BatteryLifeTime: int
        val mutable BatteryFullLifeTime: int

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type WinRect =
        val mutable left: int
        val mutable top: int
        val mutable right: int
        val mutable bottom: int

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type WindowPos =
        val mutable hwnd: nativeint
        val mutable insertAfter: nativeint
        val mutable x: int
        val mutable y: int
        val mutable cx: int
        val mutable cy: int
        val mutable flags: uint32

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type Msg =
        val mutable hwnd: nativeint
        val mutable message: uint32
        val mutable wParam: nativeint
        val mutable lParam: nativeint
        val mutable time: uint32
        val mutable x: int
        val mutable y: int
        val mutable lPrivate: uint32

    [<DllImport("kernel32.dll")>]
    extern uint32 WaitForSingleObject(nativeint handle, uint32 milliseconds)

    [<DllImport("CoreMessaging.dll")>]
    extern int CreateDispatcherQueueController(DispatcherQueueOptions options, nativeint& controller)
    [<DllImport("combase.dll", CharSet = CharSet.Unicode)>]
    extern int WindowsCreateString(string sourceString, int length, nativeint& hstring)
    [<DllImport("combase.dll")>]
    extern int WindowsDeleteString(nativeint hstring)
    [<DllImport("combase.dll")>]
    extern int RoActivateInstance(nativeint activatableClassId, nativeint& instance)
    [<DllImport("combase.dll")>]
    extern int RoGetActivationFactory(nativeint activatableClassId, Guid& iid, nativeint& factory)
    [<DllImport("dwmapi.dll")>]
    extern int DwmSetWindowAttribute(nativeint hwnd, int attribute, int& value, int size)
    [<DllImport("dwmapi.dll")>]
    extern int DwmGetWindowAttribute(nativeint hwnd, int attribute, int& value, int size)
    [<DllImport("user32.dll", SetLastError = true)>]
    extern bool SetWindowPos(nativeint hwnd, nativeint insertAfter, int x, int y, int cx, int cy, uint32 flags)
    [<DllImport("user32.dll", SetLastError = true)>]
    extern int SetWindowRgn(nativeint hwnd, nativeint region, bool redraw)
    [<DllImport("user32.dll")>]
    extern bool ShowWindowAsync(nativeint hwnd, int command)
    [<DllImport("gdi32.dll")>]
    extern nativeint CreateRectRgn(int left, int top, int right, int bottom)
    [<DllImport("gdi32.dll")>]
    extern int CombineRgn(nativeint destination, nativeint source1, nativeint source2, int mode)
    [<DllImport("gdi32.dll")>]
    extern bool DeleteObject(nativeint handle)
    [<DllImport("user32.dll")>]
    extern nativeint GetWindow(nativeint hwnd, uint32 command)
    [<DllImport("user32.dll")>]
    extern bool GetWindowRect(nativeint hwnd, WinRect& rect)
    [<DllImport("user32.dll")>]
    extern bool IsWindow(nativeint hwnd)
    [<DllImport("user32.dll")>]
    extern bool IsWindowVisible(nativeint hwnd)
    [<DllImport("user32.dll", EntryPoint = "GetWindowLongW")>]
    extern int GetWindowLong(nativeint hwnd, int index)
    [<DllImport("user32.dll")>]
    extern int GetSystemMetrics(int index)
    [<DllImport("kernel32.dll")>]
    extern bool GetSystemPowerStatus(SystemPowerStatus& status)
    [<DllImport("user32.dll")>]
    extern bool PeekMessage(Msg& msg, nativeint hwnd, uint32 filterMin, uint32 filterMax, uint32 remove)
    [<DllImport("user32.dll")>]
    extern bool TranslateMessage(Msg& msg)
    [<DllImport("user32.dll")>]
    extern nativeint DispatchMessage(Msg& msg)
    [<DllImport("user32.dll")>]
    extern uint32 MsgWaitForMultipleObjects(uint32 count, nativeint handles, bool waitAll, uint32 milliseconds, uint32 wakeMask)

    let GW_HWNDNEXT = 2u
    let GW_HWNDPREV = 3u
    let GWL_EXSTYLE = -20
    let WS_EX_TOPMOST = 0x8
    let HWND_TOPMOST = nativeint -1
    let HWND_NOTOPMOST = nativeint -2
    let SW_HIDE = 0
    let RGN_OR = 2
    let ERROR_RGN = 0
    let NULLREGION = 1
    let DWMWA_CLOAKED = 14
    let DWMWA_USE_HOSTBACKDROPBRUSH = 17
    let SM_REMOTESESSION = 0x1000
    let PM_REMOVE = 1u
    let QS_ALLINPUT = 0x04FFu

    // Raw COM calls through the vtable. The WinRT interfaces are not declared
    // as .NET COM interfaces on purpose: .NET Framework treats an
    // IInspectable object specially (it looks up its runtime class), and
    // these objects are only ever handed back to the compositor. Every
    // pointer here is owned explicitly and released exactly once. No
    // reference to the Windows SDK's metadata is needed to build this.
    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type QueryInterfaceCall = delegate of nativeint * byref<Guid> * byref<nativeint> -> int
    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type ReleaseCall = delegate of nativeint -> uint32
    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type NoArgumentCall = delegate of nativeint -> int
    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type GetObjectCall = delegate of nativeint * byref<nativeint> -> int
    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type GetBooleanCall = delegate of nativeint * byref<byte> -> int
    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type GetInt32Call = delegate of nativeint * byref<int> -> int
    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type PutObjectCall = delegate of nativeint * nativeint -> int
    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type PutBooleanCall = delegate of nativeint * byte -> int
    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type PutVector2Call = delegate of nativeint * Vector2 -> int
    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type PutVector3Call = delegate of nativeint * Vector3 -> int
    // Windows.UI.Color (bytes A, R, G, B) passed as its four bytes.
    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type CreateWithColorCall = delegate of nativeint * uint32 * byref<nativeint> -> int
    [<UnmanagedFunctionPointer(CallingConvention.StdCall)>]
    type CreateDesktopWindowTargetCall = delegate of nativeint * nativeint * int * byref<nativeint> -> int

    // IIDs, from the Windows SDK 10.0.26100 headers (winrt/windows.ui.
    // composition.h, windows.ui.composition.interop.h, windows.foundation.h,
    // windows.system.h, windows.system.power.h, windows.ui.viewmanagement.h,
    // asyncinfo.h).
    let IID_ICompositor = Guid("B403CA50-7F8C-4E83-985F-CC45060036D8")
    let IID_ICompositor3 = Guid("C9DD8EF0-6EB1-4E3C-A658-675D9C64D4AB")
    let IID_ICompositorDesktopInterop = Guid("29E691FA-4567-4DCA-B319-D0F207EB6807")
    let IID_ICompositionTarget = Guid("A1BEA8BA-D726-4663-8129-6B5E7927FFA6")
    let IID_ICompositionBrush = Guid("AB0D7608-30C0-40E9-B568-B60A6BD1FB46")
    let IID_IContainerVisual = Guid("02F6BC74-ED20-4773-AFE6-D49B4A93DB32")
    let IID_IVisual = Guid("117E202D-A859-4C89-873B-C2AA566788E3")
    let IID_IClosable = Guid("30D5A829-7FA4-4026-83BB-D75BAE4EA99E")
    let IID_ICompositionCapabilitiesStatics = Guid("F7B7A86E-6416-49E5-8DDF-AFE949E20562")
    let IID_IDispatcherQueueController = Guid("22F34E66-50DB-4E36-A98D-61C01B384D20")
    let IID_IAsyncInfo = Guid("00000036-0000-0000-C000-000000000046")
    let IID_IUISettings4 = Guid("52BB3002-919B-4D6B-9B78-8DD66FF4B93B")
    let IID_IPowerManagerStatics = Guid("1394825D-62CE-4364-98D5-AA28C7FBD15B")

    // Vtable slots: IUnknown 0-2, IInspectable 3-5, then each interface's
    // methods in header order (checked against the same headers).
    let QueryInterfaceSlot = 0
    let ReleaseSlot = 2
    let ICompositor_CreateColorBrushWithColor = 8
    let ICompositor_CreateContainerVisual = 9
    let ICompositor_CreateSpriteVisual = 22
    let ICompositor3_CreateHostBackdropBrush = 6
    let ICompositorDesktopInterop_CreateDesktopWindowTarget = 3
    let ICompositionTarget_put_Root = 7
    let IContainerVisual_get_Children = 6
    let IVisualCollection_InsertAtTop = 9
    let ISpriteVisual_put_Brush = 7
    let IVisual_put_IsVisible = 19
    let IVisual_put_Offset = 21
    let IVisual_put_Size = 36
    let IClosable_Close = 6
    let ICompositionCapabilitiesStatics_GetForCurrentView = 6
    let ICompositionCapabilities_AreEffectsSupported = 6
    let ICompositionCapabilities_AreEffectsFast = 7
    let IDispatcherQueueController_ShutdownQueueAsync = 7
    let IAsyncInfo_get_Status = 7
    let IUISettings4_get_AdvancedEffectsEnabled = 6
    let IPowerManagerStatics_get_EnergySaverStatus = 6

    // One delegate per function, made once: every sprite shares one vtable.
    // Used on the host thread only.
    let private calls = Dictionary<nativeint * Type, Delegate>()
    let method<'T when 'T :> Delegate> (instance: nativeint) (slot: int) : 'T =
        let table = Marshal.ReadIntPtr(instance)
        let key = Marshal.ReadIntPtr(table, slot * IntPtr.Size), typeof<'T>
        match calls.TryGetValue key with
        | true, call -> call :?> 'T
        | _ ->
            let call = Marshal.GetDelegateForFunctionPointer<'T>(fst key)
            calls.[key] <- call
            call

    exception HostStepFailed of string * int

    let hex (hr: int) = sprintf "0x%08X" hr

    /// Every step's result goes to the trace, failures raise.
    let check step hr =
        TabGlassTrace.log (fun () -> sprintf "api step=%s hr=%s" step (hex hr))
        if hr < 0 then raise (HostStepFailed(step, hr))

    // Per-sprite and per-move calls: only failures go to the trace.
    let checkQuiet step hr =
        if hr < 0 then
            TabGlassTrace.log (fun () -> sprintf "api step=%s hr=%s" step (hex hr))
            raise (HostStepFailed(step, hr))

    let release (instance: nativeint) =
        if instance <> 0n then (method<ReleaseCall> instance ReleaseSlot).Invoke(instance) |> ignore

    let query step (instance: nativeint) (iid: Guid) =
        let mutable id = iid
        let mutable result = 0n
        check step ((method<QueryInterfaceCall> instance QueryInterfaceSlot).Invoke(instance, &id, &result))
        if result = 0n then raise (HostStepFailed(step, 0))
        result

    let getObject step (instance: nativeint) slot =
        let mutable result = 0n
        check step ((method<GetObjectCall> instance slot).Invoke(instance, &result))
        if result = 0n then raise (HostStepFailed(step, 0))
        result

    let putObject step (instance: nativeint) slot (value: nativeint) =
        checkQuiet step ((method<PutObjectCall> instance slot).Invoke(instance, value))

    // IClosable.Close where the object has it; errors ignored (closing is
    // only to let go of DWM resources before the last release).
    let close (instance: nativeint) =
        if instance <> 0n then
            try
                let mutable id = IID_IClosable
                let mutable closable = 0n
                if (method<QueryInterfaceCall> instance QueryInterfaceSlot).Invoke(instance, &id, &closable) >= 0 && closable <> 0n then
                    try (method<NoArgumentCall> closable IClosable_Close).Invoke(closable) |> ignore
                    finally release closable
            with _ -> ()

    let withString (text: string) (f: nativeint -> 'a) =
        let mutable name = 0n
        check ("hstring " + text) (WindowsCreateString(text, text.Length, &name))
        try f name
        finally WindowsDeleteString(name) |> ignore

/// A backdrop window: no redirection surface, never activated, never
/// hit while covered by its strip (HTTRANSPARENT alone does not cross threads).
/// Shape and location changes hide it before the strip changes.
type private BackdropWindow() =
    inherit NativeWindow()
    override this.WndProc(m: Message byref) =
        match m.Msg with
        | 0x0084 -> m.Result <- nativeint -1 // WM_NCHITTEST: HTTRANSPARENT
        | 0x0021 -> m.Result <- nativeint 3  // WM_MOUSEACTIVATE: MA_NOACTIVATE
        | _ -> base.WndProc(&m)

/// How far the host thread got with a strip window's backdrop.
type private SlotState =
    | Making
    | Made of nativeint
    | Broken of string

/// One strip window's backdrop as its strip's thread keeps it. Made, used and
/// let go on that thread; only `state` and `sprites` are shared with the host
/// thread, under the slot's lock.
type private Slot(strip: nativeint, invoker: Invoker) =
    member val Strip = strip
    member val Invoker = invoker
    member val ThreadId = Thread.CurrentThread.ManagedThreadId
    member val State = Making with get, set
    // Ownership survives Broken until the owning thread destroys the HWND.
    member val OwnedHwnd = 0n with get, set
    /// Rectangles for the sprites, not yet taken by the host thread.
    member val Sprites : Rectangle list option = None with get, set
    member val Released = false with get, set
    member val RetryAfter = 0L with get, set
    member val RetryQueued = false with get, set
    member val CleanupQueued = false with get, set
    // Retry count and deadline are shared under the slot lock.
    member val Failures = 0 with get, set
    member val CloakCheck : IDisposable option = None with get, set
    // The strip's thread only.
    /// The window region now set, None when unknown (never set, or a set
    /// that failed).
    member val Region : Rectangle list option = None with get, set
    member val Bounds = Rectangle.Empty with get, set
    member val Shown = false with get, set
    /// The strip last asked for the blur, and has not asked it away since.
    member val Wanted = false with get, set
    member val Placing = false with get, set
    member val Again = false with get, set
    member val Preparing = false with get, set
    // Any thread: 1 while a follow posted by `reordered` has not run yet.
    [<DefaultValue>] val mutable followQueued: int

/// The composition objects of one backdrop window. Host thread only.
type private Composition =
    { window: BackdropWindow
      hwnd: nativeint
      target: nativeint           // ICompositionTarget
      root: nativeint             // IVisual (a ContainerVisual)
      children: nativeint         // IVisualCollection
      sprites: ResizeArray<nativeint * nativeint>   // (IVisual, ISpriteVisual)
      mutable used: int
      mutable rects: Rectangle list }

module TabGlassHost =
    open TabGlassHostNative

    // At most this many rectangles (and so sprites) per strip window; more
    // are coarsened by TabGlassMath, and a shape that still has more is not
    // given the blur that time. Bounded memory and composition work.
    [<Literal>]
    let MaxRects = 96

    /// WINDOWTABS_DEBUG_GLASS, Debug builds only (TabGlassWay.Forced).
    let forced =
#if DEBUG
        TabGlassWay.parseForced (Environment.GetEnvironmentVariable("WINDOWTABS_DEBUG_GLASS"))
#else
        TabGlassWay.Auto
#endif

    // Raised, and the generation moved on, whenever a strip may now take
    // another way: the host became ready or failed, the conditions changed,
    // a backdrop window was made or could not be made or kept.
    let private changedEvent = Event<unit>()
    let changed = changedEvent.Publish
    // Subscription changes are serialized; notify only the owning group's listeners.
    let private stripListeners = Dictionary<Invoker, Dictionary<obj, nativeint -> unit>>()
    let subscribeStrip (owner: Invoker) callback =
        let key = obj()
        lock stripListeners (fun () ->
            let listeners =
                match stripListeners.TryGetValue owner with
                | true, listeners -> listeners
                | _ ->
                    let listeners = Dictionary<obj, nativeint -> unit>()
                    stripListeners.Add(owner, listeners)
                    listeners
            listeners.Add(key, callback))
        { new IDisposable with
            member _.Dispose() =
                lock stripListeners (fun () ->
                    match stripListeners.TryGetValue owner with
                    | true, listeners ->
                        listeners.Remove key |> ignore
                        if listeners.Count = 0 then stripListeners.Remove owner |> ignore
                    | _ -> ()) }
    let private notifyStrip (slot: Slot) =
        if not (lock slot (fun () -> slot.Released)) then
            let listeners = lock stripListeners (fun () ->
                match stripListeners.TryGetValue slot.Invoker with
                | true, listeners -> Seq.toArray listeners.Values
                | _ -> [||])
            for callback in listeners do
                try callback slot.Strip with _ -> ()
    let mutable private generationValue = 0
    let generation() = Volatile.Read(&generationValue)
    let private notify() =
        Interlocked.Increment(&generationValue) |> ignore
        try changedEvent.Trigger() with _ -> ()

    // Published by the host thread; read anywhere.
    let mutable private startState = TabGlassWay.NotStarted
    let mutable private conditions = TabGlassWay.unread
    let mutable private invoker : Invoker = null
    let private clock = System.Diagnostics.Stopwatch.StartNew()
    let mutable private conditionsAt = Int64.MinValue / 2L
    let mutable private conditionsQueued = 0
    // Set once the host thread is letting everything go: nothing new is
    // posted to it, and what was posted before does nothing.
    let mutable private closing = false
    // Every strip window that asked for a backdrop, by strip.
    let private slots = System.Collections.Concurrent.ConcurrentDictionary<nativeint, Slot>()

    // Host thread state.
    let mutable private controller = 0n          // IDispatcherQueueController
    let mutable private compositor = 0n          // ICompositor
    let mutable private desktopInterop = 0n      // ICompositorDesktopInterop
    let mutable private brush = 0n               // ICompositionBrush
    let mutable private capabilities = 0n        // ICompositionCapabilities, or 0
    let mutable private uiSettings = 0n          // IUISettings4, or 0
    let mutable private powerManager = 0n        // IPowerManagerStatics, or 0
    let private compositions = Dictionary<nativeint, Composition>()

    let private buildNumber() =
        try
            use key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")
            match (if isNull key then null else key.GetValue("CurrentBuildNumber")) with
            | :? string as text ->
                match Int32.TryParse(text) with
                | true, value -> value
                | _ -> 0
            | _ -> 0
        with _ -> 0

    // Host thread. One property of a WinRT object read through its vtable;
    // None when the object is missing or the call fails (noted once per
    // result under `name`).
    let private readProperty<'T> (instance: nativeint) (name: string) (read: nativeint -> int * 'T) : 'T option =
        if instance = 0n then None
        else
            try
                let hr, value = read instance
                if hr < 0 then
                    TabGlassTrace.note ("api:" + name) (fun () -> hex hr, sprintf "api step=%s hr=%s" name (hex hr))
                    None
                else Some value
            with e ->
                TabGlassTrace.note ("api:" + name) (fun () -> e.GetType().Name, sprintf "api step=%s error=%s" name (e.GetType().Name))
                None

    let private readBoolean instance slot name =
        readProperty instance name (fun instance ->
            let mutable value = 0uy
            let hr = (method<GetBooleanCall> instance slot).Invoke(instance, &value)
            hr, value <> 0uy)

    // Host thread. What Windows says now. Transparency effects from
    // UISettings.AdvancedEffectsEnabled, the registry value behind it where
    // that cannot be read; the capabilities only when the compositor gave
    // them.
    let private readConditions() : TabGlassWay.Conditions * string =
        let advanced = readBoolean uiSettings IUISettings4_get_AdvancedEffectsEnabled "advanced-effects"
        let transparency, source =
            match advanced with
            | Some value -> value, "uisettings"
            | None ->
                (try
                    use key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")
                    match (if isNull key then null else key.GetValue("EnableTransparency")) with
                    | :? int as value -> value <> 0
                    | _ -> true
                 with _ -> true), "registry"
        let mutable status = SystemPowerStatus()
        let saver = try GetSystemPowerStatus(&status) && status.SystemStatusFlag = 1uy with _ -> false
        let energy =
            readProperty powerManager "energy-saver-status" (fun instance ->
                let mutable value = 0
                let hr = (method<GetInt32Call> instance IPowerManagerStatics_get_EnergySaverStatus).Invoke(instance, &value)
                hr, value)
        { transparency = transparency
          highContrast = (try SystemInformation.HighContrast with _ -> false)
          powerSaver = saver
          energySaver = energy
          remote = (try GetSystemMetrics(SM_REMOTESESSION) <> 0 with _ -> false)
          effectsSupported = readBoolean capabilities ICompositionCapabilities_AreEffectsSupported "effects-supported"
          effectsFast = readBoolean capabilities ICompositionCapabilities_AreEffectsFast "effects-fast" },
        source

    let private publishConditions() =
        let next, source = readConditions()
        conditionsAt <- clock.ElapsedMilliseconds
        let previous = conditions
        conditions <- next
        TabGlassTrace.note "conditions" (fun () ->
            let text = TabGlassWay.describe next + " transparencyFrom=" + source
            text, sprintf "conditions %s blocked=%s" text (defaultArg (TabGlassWay.blockedBy next) "no"))
        if next <> previous then notify()

    // Optional objects: without them the conditions fall back as described
    // at readConditions, and nothing fails.
    let private startOptional() =
        let optional step (f: unit -> nativeint) =
            try f()
            with e ->
                match e with
                | HostStepFailed _ -> ()
                | _ -> TabGlassTrace.log (fun () -> sprintf "api step=%s error=%s" step (e.GetType().Name))
                0n
        capabilities <-
            optional "capabilities" (fun () ->
                withString "Windows.UI.Composition.CompositionCapabilities" (fun name ->
                    let mutable iid = IID_ICompositionCapabilitiesStatics
                    let mutable statics = 0n
                    check "capabilities-factory" (RoGetActivationFactory(name, &iid, &statics))
                    try getObject "capabilities-for-current-view" statics ICompositionCapabilitiesStatics_GetForCurrentView
                    finally release statics))
        uiSettings <-
            optional "ui-settings" (fun () ->
                withString "Windows.UI.ViewManagement.UISettings" (fun name ->
                    let mutable instance = 0n
                    check "ui-settings" (RoActivateInstance(name, &instance))
                    try query "ui-settings4" instance IID_IUISettings4
                    finally release instance))
        powerManager <-
            optional "power-manager" (fun () ->
                withString "Windows.System.Power.PowerManager" (fun name ->
                    let mutable iid = IID_IPowerManagerStatics
                    let mutable statics = 0n
                    check "power-manager" (RoGetActivationFactory(name, &iid, &statics))
                    statics))

    // Starts the compositor on this (the host) thread. Each step's HRESULT
    // goes to the trace. What a failed start made is let go by `finish`.
    let private start() =
        let build = buildNumber()
        TabGlassTrace.log (fun () -> sprintf "start build=%d forced=%s" build (TabGlassWay.forcedName forced))
        // DWMWA_USE_HOSTBACKDROPBRUSH, which a desktop window needs for the
        // brush, is Windows 11 (22000) and later. Forced, it is tried anyway.
        if build < 22000 && forced = TabGlassWay.Auto then raise (HostStepFailed(sprintf "build-%d" build, 0))
        let mutable options = DispatcherQueueOptions()
        options.dwSize <- Marshal.SizeOf(typeof<DispatcherQueueOptions>)
        options.threadType <- 2 // DQTYPE_THREAD_CURRENT
        options.apartmentType <- 0 // DQTAT_COM_NONE: this thread is already STA
        let mutable made = 0n
        check "dispatcher-queue" (CreateDispatcherQueueController(options, &made))
        // Owned until `finish` shuts its queue down.
        controller <- made
        let instance =
            withString "Windows.UI.Composition.Compositor" (fun name ->
                let mutable instance = 0n
                check "compositor" (RoActivateInstance(name, &instance))
                instance)
        try
            compositor <- query "compositor-interface" instance IID_ICompositor
            desktopInterop <- query "desktop-interop" instance IID_ICompositorDesktopInterop
            if forced = TabGlassWay.Probe then
                // Opaque magenta: where the backdrops are, not what DWM blurs.
                let mutable color = 0n
                check "probe-brush"
                    ((method<CreateWithColorCall> compositor ICompositor_CreateColorBrushWithColor)
                        .Invoke(compositor, 0xFF00FFFFu, &color))
                try brush <- query "probe-brush-interface" color IID_ICompositionBrush
                finally release color
            else
                let compositor3 = query "compositor3" instance IID_ICompositor3
                try
                    let backdrop = getObject "host-backdrop-brush" compositor3 ICompositor3_CreateHostBackdropBrush
                    try brush <- query "host-backdrop-brush-interface" backdrop IID_ICompositionBrush
                    finally release backdrop
                finally release compositor3
        finally release instance
        startOptional()

    let private ready() =
        match startState with
        | TabGlassWay.Ready -> not closing && not (isNull invoker)
        | _ -> false

    // Runs `f` on the host thread, unless it is closing by then.
    let private post (f: unit -> unit) =
        if ready() then
            try
                invoker.tryAsyncInvoke (fun () -> if not closing then f())
            with _ -> false
        else false

    // Conditions are read on the host thread (the objects they come from
    // live there), at most every 3 seconds, and only when a strip asks.
    let private requestConditions force =
        if ready() && (force || clock.ElapsedMilliseconds - conditionsAt >= 3000L) &&
           Interlocked.CompareExchange(&conditionsQueued, 1, 0) = 0 then
            let queued =
                post (fun () ->
                    Interlocked.Exchange(&conditionsQueued, 0) |> ignore
                    try publishConditions() with _ -> ())
            if not queued then Interlocked.Exchange(&conditionsQueued, 0) |> ignore

    /// Read the conditions again (WM_SETTINGCHANGE and the like).
    let invalidate() =
        for slot in slots.Values do
            lock slot (fun () ->
                slot.Failures <- 0
                if slot.RetryAfter <> 0L then slot.RetryAfter <- clock.ElapsedMilliseconds + 1L)
        requestConditions true

    /// Called before a translucent strip decides whether it can be left as
    /// it is: the conditions are read again if they are 3 seconds old.
    let refresh() = requestConditions false

    // ------------------------------------------------------- host thread --

    let private cloaked (hwnd: nativeint) =
        let mutable value = 0
        DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &value, 4) >= 0 && value <> 0

    let private topmost (hwnd: nativeint) = GetWindowLong(hwnd, GWL_EXSTYLE) &&& WS_EX_TOPMOST <> 0

    let private destroy (c: Composition) =
        // Keep ownership and composition intact if native destruction fails.
        c.window.DestroyHandle()
        if IsWindow c.hwnd then raise (InvalidOperationException("backdrop-destroy-failed"))
        TabGlassBackdrops.unregister c.hwnd
        (try putObject "root-clear" c.target ICompositionTarget_put_Root 0n with _ -> ())
        for visual, sprite in c.sprites do
            release sprite
            release visual
        c.sprites.Clear()
        release c.children
        close c.root
        release c.root
        close c.target
        release c.target

    let private create() : Composition =
        let window = new BackdropWindow()
        let parameters = CreateParams()
        parameters.Caption <- ""
        parameters.Style <- int 0x80000000u // WS_POPUP
        // WS_EX_TOOLWINDOW, WS_EX_NOACTIVATE, WS_EX_NOREDIRECTIONBITMAP
        parameters.ExStyle <- 0x00000080 ||| 0x08000000 ||| 0x00200000
        window.CreateHandle(parameters)
        let hwnd = window.Handle
        let mutable target = 0n
        let mutable root = 0n
        let mutable children = 0n
        try
            if hwnd = 0n then raise (HostStepFailed("window", Marshal.GetLastWin32Error()))
            // Nothing of it can show or be hit until the strip's thread cuts
            // it to the tabs.
            let empty = CreateRectRgn(0, 0, 0, 0)
            if empty = 0n then raise (HostStepFailed("empty-region", 0))
            if SetWindowRgn(hwnd, empty, false) = 0 then
                DeleteObject(empty) |> ignore
                raise (HostStepFailed("empty-region-set", Marshal.GetLastWin32Error()))
            let mutable on = 1
            check "use-host-backdrop-attribute" (DwmSetWindowAttribute(hwnd, DWMWA_USE_HOSTBACKDROPBRUSH, &on, 4))
            let mutable desktopTarget = 0n
            check "desktop-window-target"
                ((method<CreateDesktopWindowTargetCall> desktopInterop ICompositorDesktopInterop_CreateDesktopWindowTarget)
                    .Invoke(desktopInterop, hwnd, 1, &desktopTarget))
            if desktopTarget = 0n then raise (HostStepFailed("desktop-window-target", 0))
            try target <- query "desktop-window-target-interface" desktopTarget IID_ICompositionTarget
            finally release desktopTarget
            let container = getObject "container" compositor ICompositor_CreateContainerVisual
            try
                root <- query "container-visual" container IID_IVisual
                let containerVisual = query "container-interface" container IID_IContainerVisual
                try children <- getObject "children" containerVisual IContainerVisual_get_Children
                finally release containerVisual
            finally release container
            putObject "root" target ICompositionTarget_put_Root root
            TabGlassBackdrops.register hwnd
            { window = window; hwnd = hwnd; target = target; root = root; children = children
              sprites = ResizeArray(); used = 0; rects = [] }
        with _ ->
            (if target <> 0n && root <> 0n then try putObject "root-clear" target ICompositionTarget_put_Root 0n with _ -> ())
            release children
            release root
            close target
            release target
            (try window.DestroyHandle() with _ -> ())
            reraise()

    let private addSprite (c: Composition) =
        let mutable sprite = 0n
        (method<GetObjectCall> compositor ICompositor_CreateSpriteVisual).Invoke(compositor, &sprite) |> checkQuiet "sprite"
        if sprite = 0n then raise (HostStepFailed("sprite", 0))
        let mutable visual = 0n
        try
            let mutable id = IID_IVisual
            (method<QueryInterfaceCall> sprite QueryInterfaceSlot).Invoke(sprite, &id, &visual) |> checkQuiet "sprite-visual"
            if visual = 0n then raise (HostStepFailed("sprite-visual", 0))
            putObject "brush" sprite ISpriteVisual_put_Brush brush
            putObject "insert" c.children IVisualCollection_InsertAtTop visual
            c.sprites.Add((visual, sprite))
        with _ ->
            release visual
            release sprite
            reraise()

    // The sprites follow the rectangles (relative to the window). Spare
    // sprites are hidden and kept for the next shape, up to MaxRects.
    let private cut (c: Composition) (rects: Rectangle list) =
        while c.sprites.Count < rects.Length do addSprite c
        rects |> List.iteri (fun i r ->
            let visual, _ = c.sprites.[i]
            checkQuiet "offset" ((method<PutVector3Call> visual IVisual_put_Offset).Invoke(visual, Vector3(float32 r.X, float32 r.Y, 0.0f)))
            checkQuiet "size" ((method<PutVector2Call> visual IVisual_put_Size).Invoke(visual, Vector2(float32 r.Width, float32 r.Height)))
            if i >= c.used then
                checkQuiet "visible" ((method<PutBooleanCall> visual IVisual_put_IsVisible).Invoke(visual, 1uy)))
        for i in rects.Length .. c.used - 1 do
            let visual, _ = c.sprites.[i]
            checkQuiet "hidden" ((method<PutBooleanCall> visual IVisual_put_IsVisible).Invoke(visual, 0uy))
        c.used <- rects.Length
        c.rects <- rects

    // Preview HWNDs stay on their caller's UI thread. Only their composition
    // objects live here; no synchronous window operations cross that boundary.
    let private previewCleanups = Dictionary<obj, unit -> unit>()

    type PreviewSurface(hwnd: nativeint) =
        let gate = obj()
        let key = obj()
        let mutable disposed = false
        let mutable queued = false
        let mutable status = 0 // pending, ready (1), failed (-1)
        let mutable wanted = Size.Empty, 0u
        // Host thread only below this line.
        let mutable target = 0n
        let mutable root = 0n
        let mutable children = 0n
        let mutable backdrop = 0n
        let mutable tint = 0n
        let mutable tintColor = None
        let sprites = ResizeArray<nativeint * nativeint>()
        let cleanup() =
            let oldTarget = target
            let owned = [tint; backdrop; children; root; target]
            let oldSprites = List.ofSeq sprites
            tint <- 0n
            backdrop <- 0n
            children <- 0n
            root <- 0n
            target <- 0n
            sprites.Clear()
            previewCleanups.Remove(key) |> ignore
            let safely action = try action() with _ -> ()
            if oldTarget <> 0n then
                safely (fun () -> putObject "preview-root-clear" oldTarget ICompositionTarget_put_Root 0n)
            for visual, sprite in oldSprites do
                safely (fun () -> release sprite)
                safely (fun () -> release visual)
            for pointer in owned do
                if pointer <> 0n then
                    safely (fun () -> close pointer)
                    safely (fun () -> release pointer)
        let add brush =
            let sprite = getObject "preview-sprite" compositor ICompositor_CreateSpriteVisual
            let mutable visual = 0n
            try
                visual <- query "preview-visual" sprite IID_IVisual
                putObject "preview-brush" sprite ISpriteVisual_put_Brush brush
                putObject "preview-insert" children IVisualCollection_InsertAtTop visual
                sprites.Add(visual, sprite)
            with _ ->
                release visual
                release sprite
                reraise()
        let create() =
            previewCleanups.[key] <- cleanup
            let mutable desktopTarget = 0n
            check "preview-target" ((method<CreateDesktopWindowTargetCall> desktopInterop ICompositorDesktopInterop_CreateDesktopWindowTarget)
                                        .Invoke(desktopInterop, hwnd, 1, &desktopTarget))
            try target <- query "preview-target-interface" desktopTarget IID_ICompositionTarget
            finally release desktopTarget
            let container = getObject "preview-container" compositor ICompositor_CreateContainerVisual
            try
                root <- query "preview-root" container IID_IVisual
                let inner = query "preview-container-interface" container IID_IContainerVisual
                try children <- getObject "preview-children" inner IContainerVisual_get_Children
                finally release inner
            finally release container
            let compositor3 = query "preview-compositor3" compositor IID_ICompositor3
            try
                let brush = getObject "preview-backdrop" compositor3 ICompositor3_CreateHostBackdropBrush
                try backdrop <- query "preview-backdrop-interface" brush IID_ICompositionBrush
                finally release brush
            finally release compositor3
            add backdrop
            add backdrop // Replaced by the tint before the root becomes visible.
        let render() =
            let state = lock gate (fun () ->
                queued <- false
                if disposed || status < 0 then None else Some wanted)
            match state with
            | None -> cleanup()
            | Some(size, color) ->
                try
                    let first = target = 0n
                    if first then create()
                    if tintColor <> Some color then
                        let mutable brush = 0n
                        checkQuiet "preview-color" ((method<CreateWithColorCall> compositor ICompositor_CreateColorBrushWithColor)
                                                        .Invoke(compositor, color, &brush))
                        let mutable replacement = 0n
                        try replacement <- query "preview-color-interface" brush IID_ICompositionBrush
                        finally release brush
                        try putObject "preview-tint" (snd sprites.[1]) ISpriteVisual_put_Brush replacement
                        with _ -> release replacement; reraise()
                        release tint
                        tint <- replacement
                        tintColor <- Some color
                    for visual, _ in sprites do
                        checkQuiet "preview-size" ((method<PutVector2Call> visual IVisual_put_Size)
                                                    .Invoke(visual, Vector2(float32 size.Width, float32 size.Height)))
                    if first then putObject "preview-root" target ICompositionTarget_put_Root root
                    lock gate (fun () -> status <- 1)
                with _ ->
                    (try cleanup() with _ -> ())
                    lock gate (fun () -> status <- -1)
        member _.Status = lock gate (fun () -> status)
        member _.Update(size: Size, color: uint32) =
            let send = lock gate (fun () ->
                if disposed || status < 0 || (status = 1 && wanted = (size, color)) then false
                else
                    wanted <- size, color
                    if queued then false
                    else
                        queued <- true
                        true)
            if send && not (post render) then lock gate (fun () -> queued <- false; status <- -1)
        interface IDisposable with
            member _.Dispose() =
                let send = lock gate (fun () ->
                    if disposed then false
                    else
                        disposed <- true
                        true)
                if send then post cleanup |> ignore

    let private failure (strip: nativeint) (e: exn) =
        TabGlassTrace.log (fun () ->
            match e with
            | HostStepFailed(step, hr) -> sprintf "backdrop=failed strip=%X step=%s hr=%s" (int64 strip) step (hex hr)
            | _ -> sprintf "backdrop=failed strip=%X error=%s" (int64 strip) (e.GetType().Name))

    let private reason (e: exn) =
        match e with
        | HostStepFailed(step, hr) -> sprintf "%s hr=%s" step (hex hr)
        | _ -> e.GetType().Name

    // Called under the slot lock. Do not extend an existing cooldown.
    let private scheduleRetry (slot: Slot) transient =
        if slot.RetryAfter = 0L then
            let delay = TabGlassWay.retryDelay slot.Failures transient
            slot.RetryAfter <- if delay < 0L then -1L else clock.ElapsedMilliseconds + delay

    // Host thread only. A broken window remains owned until destruction succeeds.
    let private breakSlot (slot: Slot) (why: string) =
        let changed = lock slot (fun () ->
            let changed = match slot.State with Broken _ -> false | _ -> true
            slot.State <- Broken why
            if why <> "retry-cleanup" then scheduleRetry slot false
            changed)
        match compositions.TryGetValue slot.Strip with
        | true, c when c.hwnd = (lock slot (fun () -> slot.OwnedHwnd)) ->
            if not (SetWindowPos(c.hwnd, 0n, 0, 0, 0, 0, TabGlassWay.hideFlags)) then
                TabGlassTrace.log (fun () -> "backdrop hide-failed at=destroy")
            try
                destroy c
                compositions.Remove slot.Strip |> ignore
                lock slot (fun () -> slot.OwnedHwnd <- 0n)
            with e -> failure slot.Strip e
        | _ -> ()
        if changed then notifyStrip slot

    // One cleanup request per slot, including while the host is stalled.
    let private queueCleanup (slot: Slot) why =
        let queue = lock slot (fun () ->
            if slot.CleanupQueued then false
            else
                slot.CleanupQueued <- true
                slot.State <- Broken why
                scheduleRetry slot false
                true)
        if queue then
            if not (post (fun () ->
                try breakSlot slot why
                finally lock slot (fun () -> slot.CleanupQueued <- false))) then
                // Keep calls disabled if the host no longer accepts cleanup.
                let hwnd = lock slot (fun () -> slot.OwnedHwnd)
                if hwnd <> 0n then ShowWindowAsync(hwnd, SW_HIDE) |> ignore
                TabGlassTrace.log (fun () -> "backdrop cleanup-not-posted")

    // No cross-thread synchronous HWND calls on a strip's message loop.
    // The host performs the operation; the caller waits only for this budget.
    // Timed-out work is cancelled before it starts, or destroyed when it returns.
    let private windowCall (slot: Slot) (work: nativeint -> bool) =
        let callable = lock slot (fun () ->
            not slot.CleanupQueued && not slot.Released &&
            match slot.State with Made _ -> true | _ -> false)
        if not callable then false
        else
            let gate = obj()
            use completed = new ManualResetEvent(false)
            let mutable result : bool option = None
            let mutable cancelled = false
            let run() =
                let hwnd = lock slot (fun () -> slot.OwnedHwnd)
                if hwnd <> 0n && not (lock gate (fun () -> cancelled)) then
                    let ok = try work hwnd with _ -> false
                    if not ok then
                        let error = Marshal.GetLastWin32Error()
                        TabGlassTrace.note (sprintf "native-call:%X" (int64 slot.Strip)) (fun () ->
                            string error, sprintf "backdrop native-call-failed error=%d" error)
                    // A timed-out call is followed by the single FIFO cleanup.
                    lock gate (fun () ->
                        if not cancelled then
                            result <- Some ok
                            completed.Set() |> ignore)
                else
                    lock gate (fun () ->
                        if not cancelled then
                            result <- Some false
                            completed.Set() |> ignore)
            let queued = post run
            // Native wait does not pump STA messages. The host never synchronously
            // invokes the strip; notifications use BeginInvoke and HWNDs are unowned.
            if queued then WaitForSingleObject(completed.SafeWaitHandle.DangerousGetHandle(), 100u) |> ignore
            let answer = lock gate (fun () ->
                match result with
                | Some ok -> Some ok
                | None -> cancelled <- true; None)
            match answer with
            | Some ok -> ok
            | None ->
                lock slot (fun () ->
                    slot.State <- Broken "host-call-timeout"
                    scheduleRetry slot false)
                // Independent of the host's message pump. Never uncloak this slot:
                // the queued cleanup is its only remaining operation.
                let hwnd = lock slot (fun () -> slot.OwnedHwnd)
                if hwnd <> 0n then
                    let mutable cloak = 1
                    let hr = DwmSetWindowAttribute(hwnd, 13, &cloak, 4) // DWMWA_CLOAK
                    let asked = ShowWindowAsync(hwnd, SW_HIDE)
                    TabGlassTrace.note (sprintf "timeout:%X" (int64 slot.Strip)) (fun () ->
                        "timeout", sprintf "backdrop host-call-timeout cloakHr=%s hide-posted=%b" (hex hr) asked)
                queueCleanup slot "host-call-timeout"
                false

    let private make (slot: Slot) =
        if not (lock slot (fun () -> slot.Released)) then
            try
                let c = create()
                compositions.[slot.Strip] <- c
                let kept =
                    lock slot (fun () ->
                        if slot.Released then false
                        else
                            slot.OwnedHwnd <- c.hwnd
                            slot.State <- Made c.hwnd
                            true)
                TabGlassTrace.log (fun () -> sprintf "backdrop=created strip=%X hwnd=%X" (int64 slot.Strip) (int64 c.hwnd))
                if kept then notifyStrip slot
                else
                    destroy c
                    compositions.Remove slot.Strip |> ignore
            with e ->
                failure slot.Strip e
                lock slot (fun () ->
                    if not slot.Released then
                        slot.State <- Broken(reason e)
                        scheduleRetry slot false)
                notifyStrip slot

    let private takeSprites (slot: Slot) =
        match compositions.TryGetValue slot.Strip with
        | true, c when not (lock slot (fun () -> slot.Released)) ->
            let taken =
                lock slot (fun () ->
                    let rects = slot.Sprites
                    slot.Sprites <- None
                    rects)
            match taken with
            | Some rects when rects <> c.rects ->
                try
                    PerfTrace.time "glassHost.sprites" (fun () -> cut c rects)
                    TabGlassTrace.note (sprintf "sprites:%X" (int64 slot.Strip)) (fun () ->
                        let state = sprintf "used=%d allocated=%d" c.used c.sprites.Count
                        state, sprintf "sprites strip=%X hwnd=%X %s" (int64 slot.Strip) (int64 c.hwnd) state)
                with e ->
                    PerfTrace.count "glassHostFailed"
                    failure slot.Strip e
                    breakSlot slot ("sprites " + reason e)
            | _ -> ()
        | _ -> ()

    let private unmake (strip: nativeint) =
        match compositions.TryGetValue strip with
        | true, c ->
            try
                destroy c
                compositions.Remove strip |> ignore
                match slots.TryGetValue strip with
                | true, slot -> lock slot (fun () -> slot.OwnedHwnd <- 0n)
                | _ -> ()
                TabGlassTrace.log (fun () -> "backdrop=released")
            with e -> failure strip e
        | _ -> ()
        TabGlassTrace.forget (sprintf "backdrop:%X" (int64 strip))
        TabGlassTrace.forget (sprintf "sprites:%X" (int64 strip))
        TabGlassTrace.forget (sprintf "region:%X" (int64 strip))

    // Pumps this thread's messages, the DispatcherQueue's work among them,
    // until completion is proven. The main thread has a separate wait budget;
    // ShutdownDeadline bounds process lifetime if the queue cannot complete.
    let private pumpUntil (finished: unit -> bool) =
        let mutable msg = Msg()
        while not (finished()) do
            MsgWaitForMultipleObjects(0u, 0n, false, 15u, QS_ALLINPUT) |> ignore
            while PeekMessage(&msg, 0n, 0u, 0u, PM_REMOVE) do
                TranslateMessage(&msg) |> ignore
                DispatchMessage(&msg) |> ignore

    // Host thread, when its message loop has ended: every backdrop and
    // composition object let go, then the DispatcherQueue shut down while
    // this thread still pumps, as its controller requires, then the
    // controller released.
    let private finish() =
        closing <- true
        startState <- TabGlassWay.Failed "shut-down"
        for cleanup in List.ofSeq previewCleanups.Values do
            try cleanup() with _ -> ()
        for slot in slots.Values do
            lock slot (fun () -> slot.State <- Broken "shut-down")
        for strip in List.ofSeq compositions.Keys do
            unmake strip
        if compositions.Count <> 0 then
            raise (InvalidOperationException("backdrop-cleanup-incomplete"))
        let letGo (instance: nativeint) closeIt =
            if instance <> 0n then
                (if closeIt then close instance)
                (try release instance with _ -> ())
        letGo brush true
        brush <- 0n
        letGo capabilities false
        capabilities <- 0n
        letGo uiSettings false
        uiSettings <- 0n
        letGo powerManager false
        powerManager <- 0n
        letGo desktopInterop false
        desktopInterop <- 0n
        letGo compositor true
        compositor <- 0n
        if controller <> 0n then
            let mutable queue = 0n
            let mutable action = 0n
            let mutable info = 0n
            try
                queue <- query "shutdown-controller" controller IID_IDispatcherQueueController
                action <- getObject "shutdown-queue" queue IDispatcherQueueController_ShutdownQueueAsync
                info <- query "shutdown-async-info" action IID_IAsyncInfo
                let state() =
                    let mutable value = 0
                    let hr = (method<GetInt32Call> info IAsyncInfo_get_Status).Invoke(info, &value)
                    let status = if hr < 0 then None else Some value
                    TabGlassTrace.note "shutdown-queue" (fun () ->
                        let text =
                            match status with
                            | Some 0 -> "started"
                            | Some 1 -> "completed"
                            | Some 2 -> "cancelled"
                            | Some 3 -> "error"
                            | Some n -> sprintf "unknown-%d" n
                            | None -> "status-read-failed hr=" + hex hr
                        text, "shutdown queue state=" + text)
                    status
                pumpUntil (fun () -> TabGlassWay.queueCompleted (state()))
            with e ->
                // Retain every acquired reference, not only the controller.
                TabGlassTrace.log (fun () -> "shutdown queue retained error=" + e.GetType().Name)
                pumpUntil (fun () -> false)
            release info
            release action
            release queue
            release controller
            controller <- 0n

    let private thread = lazy (
        let run() =
            CrashLog.installThread()
            let inner = new WindowsFormsSynchronizationContext()
            SynchronizationContext.SetSynchronizationContext(NonBlockingSyncContext(inner))
            invoker <- InvokerService.invoker
            let result =
                try
                    start()
                    TabGlassWay.Ready
                with
                | HostStepFailed(step, hr) -> TabGlassWay.Failed(sprintf "%s hr=%s" step (hex hr))
                | e -> TabGlassWay.Failed(e.GetType().Name)
            (try publishConditions() with _ -> ())
            startState <- result
            TabGlassTrace.log (fun () ->
                match result with
                | TabGlassWay.Failed reason -> "start state=failed reason=" + reason
                | _ -> "start state=ready")
            notify()
            // The loop runs even when the start failed: a DispatcherQueue
            // made on this thread is shut down only while the thread pumps.
            try
                try Application.Run()
                finally
                    (try finish() with e ->
                        TabGlassTrace.log (fun () -> "shutdown error=" + e.GetType().Name)
                        if controller <> 0n then pumpUntil (fun () -> false))
            finally
                (invoker :> IDisposable).Dispose()
                inner.Dispose()
                TabGlassTrace.log (fun () -> "host thread ended")
        // Retain the native queue even if main has exhausted its Join budget.
        // ShutdownDeadline terminates the process if this foreground thread stalls.
        let thread = new Thread(ThreadStart(run), IsBackground = false, Name = "WindowTabs tab glass")
        thread.SetApartmentState(ApartmentState.STA)
        startState <- TabGlassWay.Starting
        thread.Start()
        thread)

    /// At exit, after every group's thread has ended: the host thread lets
    /// go of every composition object and shuts its DispatcherQueue down,
    /// then ends. Bounded: the process does not wait on it for long.
    let shutdown() =
        if thread.IsValueCreated then
            ShutdownDeadline.start()
            let t = thread.Value
            let asked =
                not (isNull invoker) &&
                (try
                    invoker.tryAsyncInvoke (fun () -> Application.ExitThread())
                 with _ -> false)
            if not asked || not (t.Join(3000)) then
                TabGlassTrace.log (fun () -> sprintf "shutdown wait gave up asked=%b" asked)

    /// Why the backdrop is not used now, or None when it may be. Never
    /// waits: the first call starts the host, and until it is ready the
    /// answer is "host-starting" (`changed` follows).
    let unavailable () : string option =
        match forced with
        | TabGlassWay.Wallpaper | TabGlassWay.Plain -> TabGlassWay.hostRefused forced startState conditions false
        | _ ->
            thread.Force() |> ignore
            requestConditions false
            TabGlassWay.hostRefused forced startState conditions false

    /// Starts the shared host without requiring application startup. Preview
    /// effects follow the tab host policy, including debug host overrides.
    let previewAvailable() =
        try
            unavailable() = None
        with _ -> false

    // ------------------------------------------------- strip's own thread --

    let private slotHwnd (slot: Slot) =
        lock slot (fun () -> slot.OwnedHwnd)

    // Where the backdrop window really is, as Windows has it: written when
    // anything but its position changed (the position follows every drag).
    // `at` says which step placed it.
    let private record (at: string) (slot: Slot) (hwnd: nativeint) =
        TabGlassTrace.note (sprintf "backdrop:%X" (int64 slot.Strip)) (fun () ->
            let strip = slot.Strip
            let mutable r = WinRect()
            GetWindowRect(hwnd, &r) |> ignore
            let mutable s = WinRect()
            GetWindowRect(strip, &s) |> ignore
            let order =
                if GetWindow(strip, GW_HWNDNEXT) = hwnd then "directly-below-strip"
                elif GetWindow(hwnd, GW_HWNDNEXT) = strip then "directly-above-strip"
                else "apart"
            let state =
                sprintf "shown=%b visible=%b cloaked=%b stripVisible=%b stripCloaked=%b z=%s topmost=%b/%b size=%dx%d offset=%d,%d rects=%d"
                    slot.Shown (IsWindowVisible hwnd) (cloaked hwnd) (IsWindowVisible strip) (cloaked strip) order
                    (topmost hwnd) (topmost strip) (r.right - r.left) (r.bottom - r.top)
                    (r.left - s.left) (r.top - s.top) (match slot.Region with Some rects -> rects.Length | None -> -1)
            state,
            sprintf "backdrop at=%s strip=%X hwnd=%X rect=%d,%d,%d,%d above=%X below=%X %s"
                at (int64 strip) (int64 hwnd) r.left r.top (r.right - r.left) (r.bottom - r.top)
                (int64 (GetWindow(hwnd, GW_HWNDPREV))) (int64 (GetWindow(hwnd, GW_HWNDNEXT))) state)

    // Confirm hidden before recording it. Failure keeps ownership for cleanup.
    let private hideSlot (at: string) (slot: Slot) =
        let hwnd = slotHwnd slot
        let hidden =
            if hwnd = 0n || not (IsWindowVisible hwnd) then true
            else
                windowCall slot (fun h ->
                    let ok = SetWindowPos(h, 0n, 0, 0, 0, 0, TabGlassWay.hideFlags)
                    ok && not (IsWindowVisible h))
        if hidden then slot.Shown <- false
        elif not (lock slot (fun () ->
            slot.CleanupQueued || (match slot.State with Broken _ -> true | _ -> false))) then
            TabGlassTrace.note (sprintf "hide-failed:%X" (int64 slot.Strip)) (fun () ->
                at, "backdrop hide-failed at=" + at)
            queueCleanup slot "hide-failed"
        hidden

    // The region of the backdrop window, applied on the host thread: the
    // rectangles (relative to the window). None when set, else why not.
    let private setRegion (hwnd: nativeint) (rects: Rectangle list) : string option =
        let region = CreateRectRgn(0, 0, 0, 0)
        if region = 0n then Some "region-create"
        else
            let mutable made = true
            for r in rects do
                if made then
                    let part = CreateRectRgn(r.Left, r.Top, r.Right, r.Bottom)
                    if part = 0n then made <- false
                    else
                        if CombineRgn(region, region, part, RGN_OR) = ERROR_RGN then made <- false
                        DeleteObject(part) |> ignore
            if not made then
                DeleteObject(region) |> ignore
                Some "region-combine"
            // The system owns the region once it is set.
            elif SetWindowRgn(hwnd, region, true) = 0 then
                let error = Marshal.GetLastWin32Error()
                DeleteObject(region) |> ignore
                Some(sprintf "region-set error=%d" error)
            else None

    // Directly below the strip, in its layer, at the strip's rectangle as
    // Windows has it now, shown - or hidden while the strip is not shown.
    // Only what differs is changed, so a backdrop already in place causes no
    // reorder. None when it is in place (or rightly hidden), else why not,
    // and it is hidden.
    let private placeOnce (at: string) (slot: Slot) : string option =
        let hwnd = slotHwnd slot
        let strip = slot.Strip
        if hwnd = 0n then Some "host-window-gone"
        elif (lock slot (fun () -> match slot.State with Made _ -> false | _ -> true)) then Some "host-window-failed"
        elif slot.Placing then
            slot.Again <- true
            Some "place-again"
        else
            slot.Placing <- true
            try
                PerfTrace.time "glassHost.place" (fun () ->
                    if not (IsWindowVisible strip) || cloaked strip then
                        if cloaked strip then
                            TabGlassTrace.note (sprintf "cloak:%X" (int64 strip)) (fun () -> "hidden", "backdrop cloak-hidden")
                        if hideSlot at slot then None else Some "hide-failed"
                    else
                        let mutable r = WinRect()
                        if not (GetWindowRect(strip, &r)) then
                            hideSlot at slot |> ignore
                            Some "strip-rect"
                        else
                            let bounds = Rectangle(r.left, r.top, r.right - r.left, r.bottom - r.top)
                            let layerChanged = topmost strip <> topmost hwnd
                            let mutable layerReady = true
                            if layerChanged then
                                layerReady <- hideSlot "layer-change" slot
                                if layerReady then
                                    TabGlassTrace.log (fun () -> "backdrop layer-change hidden")
                                    layerReady <- windowCall slot (fun h ->
                                        let flags = TabGlassWay.SWP_NOACTIVATE ||| TabGlassWay.SWP_NOOWNERZORDER
                                        let changed =
                                            not (IsWindowVisible h) &&
                                            SetWindowPos(h, (if topmost strip then HWND_TOPMOST else HWND_NOTOPMOST), 0, 0, 0, 0,
                                                         flags ||| TabGlassWay.SWP_NOMOVE ||| TabGlassWay.SWP_NOSIZE)
                                        let placed = changed && SetWindowPos(h, strip, bounds.X, bounds.Y, bounds.Width, bounds.Height, flags)
                                        let mutable actual = WinRect()
                                        let mutable current = WinRect()
                                        placed && not (IsWindowVisible h) && topmost h = topmost strip &&
                                        GetWindow(strip, GW_HWNDNEXT) = h &&
                                        GetWindowRect(h, &actual) && GetWindowRect(strip, &current) &&
                                        actual.left = current.left && actual.top = current.top &&
                                        actual.right = current.right && actual.bottom = current.bottom)
                                if not layerReady then slot.Bounds <- Rectangle.Empty
                            // Showing a window puts it on the current virtual
                            // desktop. A strip shown again there has left its
                            // backdrop on the desktop before: hidden and shown
                            // again, the backdrop follows.
                            if slot.Shown && cloaked hwnd then hideSlot at slot |> ignore
                            let below = GetWindow(strip, GW_HWNDNEXT) = hwnd
                            let moved = bounds <> slot.Bounds
                            let placed =
                                if (lock slot (fun () -> match slot.State with Made _ -> false | _ -> true)) then Some "host-window-failed"
                                elif not layerReady then Some "layer-change-failed"
                                elif not (TabGlassWay.placeNeeded below moved slot.Shown) then None
                                elif windowCall slot (fun h ->
                                    SetWindowPos(h, strip, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                                                 TabGlassWay.placeFlags below moved slot.Shown)) then
                                    slot.Bounds <- bounds
                                    slot.Shown <- true
                                    None
                                else Some "place-failed"
                            // Read back: where it is now decides, not what was asked.
                            let mutable actual = WinRect()
                            let mutable current = WinRect()
                            let rectsMatch = GetWindowRect(hwnd, &actual) && GetWindowRect(strip, &current) &&
                                             actual.left = current.left && actual.top = current.top &&
                                             actual.right = current.right && actual.bottom = current.bottom
                            let result =
                                match placed with
                                | Some _ -> placed
                                | None when not rectsMatch -> Some "bounds-changed"
                                | None when topmost hwnd <> topmost strip -> Some "layer-mismatch"
                                | None when not (IsWindowVisible strip) || cloaked strip -> Some "strip-hidden"
                                | None when not (IsWindowVisible hwnd) -> Some "backdrop-hidden"
                                | None when cloaked hwnd -> Some "backdrop-cloaked"
                                | None when GetWindow(strip, GW_HWNDNEXT) <> hwnd -> Some "not-below-strip"
                                | None -> None
                            match result with
                            | Some _ ->
                                slot.Bounds <- Rectangle.Empty
                                hideSlot at slot |> ignore
                            | None -> record at slot hwnd
                            result)
            finally slot.Placing <- false

    let private place at (slot: Slot) =
        if slot.Placing then
            slot.Again <- true
            Some "place-again"
        else
            slot.Again <- false
            let result = placeOnce at slot
            if slot.Again then
                slot.Again <- false
                TabGlassTrace.log (fun () -> "backdrop place-again retry=1")
                let retry = placeOnce at slot
                if slot.Again then
                    hideSlot "place-again" slot |> ignore
                    Some "place-again-exhausted"
                else retry
            else result

    // The backdrop of the strip window `strip`, made (asked of the host
    // thread) the first time. This thread's.
    let private slotOf (strip: nativeint) =
        match slots.TryGetValue strip with
        | true, slot -> slot
        | _ ->
            let slot = Slot(strip, InvokerService.invoker)
            slots.[strip] <- slot
            if not (post (fun () -> make slot)) then
                lock slot (fun () ->
                    slot.State <- Broken "host-not-ready"
                    scheduleRetry slot false)
            slot

    // Strip thread only. No timer: an ordinary drawing/notification must arrive.
    let private retrySlot (slot: Slot) =
        let due = lock slot (fun () ->
            if TabGlassWay.retryDue clock.ElapsedMilliseconds slot.RetryAfter &&
               not slot.Released && not slot.RetryQueued && not slot.CleanupQueued &&
               not (TabGlassWay.failuresExhausted slot.Failures) then
                slot.Failures <- TabGlassWay.nextFailures slot.Failures false
                slot.RetryAfter <- 0L
                true
            else false)
        if due then
            slot.Region <- None
            slot.Bounds <- Rectangle.Empty
            TabGlassTrace.log (fun () -> "backdrop retry requested")
            match lock slot (fun () -> slot.State) with
            | Made _ -> lock slot (fun () -> slot.RetryAfter <- 0L)
            | Broken _ ->
                lock slot (fun () -> slot.RetryQueued <- true)
                let queued = post (fun () ->
                    try
                        // FIFO execution keeps late operations ahead of replacement.
                        breakSlot slot "retry-cleanup"
                        let clean = lock slot (fun () ->
                            if slot.OwnedHwnd = 0n && not slot.Released then
                                slot.State <- Making
                                slot.Sprites <- None
                                slot.RetryAfter <- 0L
                                true
                            else
                                scheduleRetry slot false
                                false)
                        if clean then make slot
                    finally lock slot (fun () -> slot.RetryQueued <- false))
                if not queued then lock slot (fun () ->
                    slot.RetryQueued <- false
                    scheduleRetry slot false)
            | Making -> ()
        due

    let private failedAttempt (slot: Slot) why =
        if TabGlassWay.transientFailure why then
            TabGlassTrace.note (sprintf "transient:%X" (int64 slot.Strip)) (fun () ->
                why, "backdrop transient-failure reason=" + why)
        slot.Wanted <- false
        lock slot (fun () -> scheduleRetry slot (TabGlassWay.transientFailure why))
        TabGlassTrace.note (sprintf "cooldown:%X" (int64 slot.Strip)) (fun () ->
            why, "backdrop retry-cooldown reason=" + why)

    /// Before the strip window `strip` is drawn with `rects` (relative to
    /// it) under Windows' blur: its backdrop cut to them. None when the
    /// backdrop can be used for this drawing, else why not. Strip's thread.
    let prepare (strip: nativeint) (rects: Rectangle list) (bounds: Rectangle) : string option =
        let slot = slotOf strip
        retrySlot slot |> ignore
        match lock slot (fun () -> slot.State) with
        | _ when lock slot (fun () -> slot.RetryAfter < 0L) -> Some "follow-failures"
        | _ when lock slot (fun () -> slot.RetryAfter > clock.ElapsedMilliseconds || slot.RetryQueued || slot.CleanupQueued) -> Some "retry-cooldown"
        | Making -> Some "host-window-making"
        | Broken why -> Some("host-window-failed " + why)
        | Made hwnd ->
            slot.Preparing <- true
            // Retain a stable background on colour-only redraws. Shape and
            // location changes must hide it before UpdateLayeredWindow.
            let shapeChanged = slot.Region <> Some rects
            let changing = shapeChanged || slot.Bounds <> bounds
            if changing && not (hideSlot "prepare" slot) then Some "hide-failed"
            else
                let mutable error = None
                let ok =
                    if slot.Region = Some rects then true
                    else windowCall slot (fun h ->
                        error <- setRegion h rects
                        error.IsNone)
                if not ok then
                    slot.Region <- None
                    let why = defaultArg error "region-host-call-failed"
                    failedAttempt slot why
                    Some why
                else
                    slot.Region <- Some rects
                    TabGlassTrace.note (sprintf "region:%X" (int64 strip)) (fun () ->
                        let state = sprintf "rects=%d" rects.Length
                        state, "backdrop region-ready " + state)
                    slot.Wanted <- true
                    if not shapeChanged then None
                    else
                        let queue = lock slot (fun () ->
                            let queue = slot.Sprites.IsNone
                            slot.Sprites <- Some rects
                            queue)
                        if queue && not (post (fun () -> takeSprites slot)) then
                            slot.Region <- None
                            failedAttempt slot "sprites-not-posted"
                            Some "sprites-not-posted"
                        else None

    /// After the strip window `strip` was drawn (prepare said None): its
    /// backdrop placed under it as it now is. None when it is there, else
    /// why not, and the backdrop is hidden. Strip's thread.
    let commit (strip: nativeint) : string option =
        match slots.TryGetValue strip with
        | true, slot when slot.Wanted ->
            slot.Preparing <- false
            let result = place "show" slot
            match result with
            | Some why -> failedAttempt slot why
            | None -> lock slot (fun () -> slot.Failures <- 0)
            result
        | _ -> Some "host-window-gone"

    /// The strip window shows no blur now: its backdrop hidden. Strip's
    /// thread.
    let hide (strip: nativeint) =
        match slots.TryGetValue strip with
        | true, slot ->
            slot.Wanted <- false
            slot.Preparing <- false
            hideSlot "hide" slot |> ignore
        | _ -> ()

    /// The strip window moved in the order of windows, or was moved, shown
    /// or hidden (WM_WINDOWPOSCHANGED): its backdrop follows at once. Where
    /// it cannot, only this strip is told to draw again. Repeated failures
    /// back off, then stop until conditions are invalidated. Strip's thread.
    let rec follow (strip: nativeint) = followAt "follow" strip

    and private followAt (at: string) (strip: nativeint) =
        match slots.TryGetValue strip with
        | true, slot when slot.Wanted && not slot.Preparing && (lock slot (fun () -> slot.RetryAfter = 0L && not slot.CleanupQueued)) ->
            match place at slot with
            | Some "place-again" -> ()
            | Some why ->
                failedAttempt slot why
                notifyStrip slot
            | None -> lock slot (fun () -> slot.Failures <- 0)
        | true, slot when retrySlot slot -> notifyStrip slot
        | _ -> ()

    /// Before the strip's geometry changes, keep the old backdrop out of input.
    let changing strip data =
        match slots.TryGetValue strip with
        | true, slot ->
            let mutable r = WinRect()
            let changes =
                if data = 0n || not (GetWindowRect(strip, &r)) then true
                else
                    let p = Marshal.PtrToStructure<WindowPos>(data)
                    TabGlassWay.geometryChanging
                        (Rectangle(r.left, r.top, r.right - r.left, r.bottom - r.top))
                        (Rectangle(p.x, p.y, p.cx, p.cy)) p.flags
            if changes then hideSlot "changing" slot |> ignore
        | _ -> ()

    /// Called from the group's existing shell cloak/uncloak notification.
    let cloakChanged strip =
        match slots.TryGetValue strip with
        | true, slot ->
            if cloaked strip then
                hideSlot "cloak" slot |> ignore
                TabGlassTrace.note (sprintf "cloak:%X" (int64 strip)) (fun () -> "hidden", "backdrop cloak-hidden")
            else followAt "uncloak" strip
        | _ -> ()

    /// Shell can notify the application before its strip is cloaked.
    /// One coalesced, one-shot check; desktop passes do not schedule timers.
    let cloakNotice strip =
        cloakChanged strip
        match slots.TryGetValue strip with
        | true, slot when slot.CloakCheck.IsNone ->
            slot.CloakCheck <- Some(ThreadHelper.cancelablePostBack 100 (fun () ->
                slot.CloakCheck |> Option.iter (fun timer -> timer.Dispose())
                slot.CloakCheck <- None
                if not (lock slot (fun () -> slot.Released)) then cloakChanged strip))
        | _ -> ()

    /// Windows were put in a new order (OS.setZorder and the like), on any
    /// thread: a window may now lie between a strip and its backdrop. Each
    /// such backdrop is put back by its strip's thread - now if that is this
    /// thread, else as soon as it gets to it.
    let reordered () =
        if not slots.IsEmpty then
            let here = Thread.CurrentThread.ManagedThreadId
            for slot in slots.Values do
                if slot.Wanted && slot.Shown then
                    let hwnd = slotHwnd slot
                    if hwnd <> 0n && GetWindow(slot.Strip, GW_HWNDNEXT) <> hwnd then
                        if slot.ThreadId = here then followAt "reorder" slot.Strip
                        elif Interlocked.CompareExchange(&slot.followQueued, 1, 0) = 0 then
                            let queued =
                                try
                                    slot.Invoker.tryAsyncInvoke (fun () ->
                                        Interlocked.Exchange(&slot.followQueued, 0) |> ignore
                                        followAt "reorder" slot.Strip)
                                with _ -> false
                            if not queued then Interlocked.Exchange(&slot.followQueued, 0) |> ignore

    /// The strip window is going away: so is its backdrop. Strip's thread.
    let release (strip: nativeint) =
        match slots.TryRemove strip with
        | true, slot ->
            slot.CloakCheck |> Option.iter (fun timer -> timer.Dispose())
            slot.CloakCheck <- None
            slot.Wanted <- false
            hideSlot "release" slot |> ignore
            lock slot (fun () -> slot.Released <- true)
            if not (post (fun () -> unmake strip)) then
                TabGlassTrace.log (fun () -> sprintf "backdrop release-not-posted strip=%X" (int64 strip))
        | _ -> ()
