namespace Bemo

open System
open System.IO
open System.Runtime.InteropServices
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Windows.Automation
open TaskbarAppIdPolicy

/// Groups the taskbar buttons of each tab group by giving its windows one
/// AppUserModelID per desktop, and gives each window its own value back. The
/// main thread hands over what its desktop pass has already read; every
/// native call runs on one worker thread, which exists only while there is
/// something to hold or to do. Whatever fails stays with that window (or that
/// button): nothing else waits for it.
module TaskbarAppId =
    type Snapshot = {
        enabled: bool
        /// Each group's strip window and its tabs in on-screen order.
        groups: (IntPtr * IntPtr list) list
        /// Non-shared windows held by several groups.
        excluded: Set<IntPtr>
        shared: Set<IntPtr>
        shown: Set<IntPtr>
        places: Map<IntPtr, Place>
        current: Guid option }

    // ---------------------------------------------------------------- trace --

    // One number per start tells runs apart in the appended file.
    let private random = Random()
    let private runId = random.Next()
    let private traceGate = obj()

    // Numbers, HRESULTs and fixed names only: no titles, paths or property values.
    let private trace (hwnd: IntPtr) (action: string) (reason: string) (hr: int) (detail: string) =
#if DEBUG
        try
            let directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowTabs")
            Directory.CreateDirectory(directory) |> ignore
            let line =
                sprintf "%s run=%08X hwnd=%X action=%s reason=%s hr=0x%08X%s\r\n"
                    (DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")) runId (int64 hwnd) action reason hr
                    (if detail = "" then "" else " " + detail)
            lock traceGate (fun () -> File.AppendAllText(Path.Combine(directory, "taskbar_trace.log"), line))
        with _ -> ()
#else
        ()
#endif

    // --------------------------------------------------------------- native --

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private PropertyKey =
        val mutable format: Guid
        val mutable pid: uint32
        new(pid) = { format = Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"); pid = pid }

    [<ComImport; Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"); InterfaceType(ComInterfaceType.InterfaceIsIUnknown)>]
    type private IPropertyStore =
        [<PreserveSig>] abstract GetCount: count: byref<uint32> -> int
        [<PreserveSig>] abstract GetAt: index: uint32 * key: byref<PropertyKey> -> int
        [<PreserveSig>] abstract GetValue: key: byref<PropertyKey> * value: IntPtr -> int
        [<PreserveSig>] abstract SetValue: key: byref<PropertyKey> * value: IntPtr -> int
        [<PreserveSig>] abstract Commit: unit -> int

    [<ComImport; Guid("56FDF342-FD6D-11D0-958A-006097C9A090"); InterfaceType(ComInterfaceType.InterfaceIsIUnknown)>]
    type private ITaskbarList =
        [<PreserveSig>] abstract HrInit: unit -> int
        [<PreserveSig>] abstract AddTab: hwnd: IntPtr -> int
        [<PreserveSig>] abstract DeleteTab: hwnd: IntPtr -> int
        [<PreserveSig>] abstract ActivateTab: hwnd: IntPtr -> int
        [<PreserveSig>] abstract SetActiveAlt: hwnd: IntPtr -> int

    // The private Shell services are probed on the worker STA, not by OS version.
    [<ComImport; Guid("6D5140C1-7436-11CE-8034-00AA006009FA"); InterfaceType(ComInterfaceType.InterfaceIsIUnknown)>]
    type private IShellServices =
        [<PreserveSig>] abstract QueryService: service: byref<Guid> * iid: byref<Guid> * result: byref<IntPtr> -> int

    [<ComImport; Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5"); InterfaceType(ComInterfaceType.InterfaceIsIUnknown)>]
    type private IViews =
        [<PreserveSig>] abstract All: result: byref<IntPtr> -> int
        [<PreserveSig>] abstract Ordered: result: byref<IntPtr> -> int
        [<PreserveSig>] abstract ById: [<MarshalAs(UnmanagedType.LPWStr)>] id: string * result: byref<IntPtr> -> int
        [<PreserveSig>] abstract ForWindow: hwnd: IntPtr * result: byref<IntPtr> -> int

    [<ComImport; Guid("4CE81583-1E4C-4632-A621-07A53543148F"); InterfaceType(ComInterfaceType.InterfaceIsIUnknown)>]
    type private IPins =
        [<PreserveSig>] abstract IsApp: [<MarshalAs(UnmanagedType.LPWStr)>] id: string * result: byref<int> -> int
        [<PreserveSig>] abstract PinApp: [<MarshalAs(UnmanagedType.LPWStr)>] id: string -> int
        [<PreserveSig>] abstract UnpinApp: [<MarshalAs(UnmanagedType.LPWStr)>] id: string -> int
        [<PreserveSig>] abstract IsView: view: IntPtr * result: byref<int> -> int
        [<PreserveSig>] abstract PinView: view: IntPtr -> int
        [<PreserveSig>] abstract UnpinView: view: IntPtr -> int

    let private withService (shell: IShellServices) service iid action =
        let mutable service = service
        let mutable iid = iid
        let mutable pointer = IntPtr.Zero
        try
            Marshal.ThrowExceptionForHR(shell.QueryService(&service, &iid, &pointer))
            let instance = Marshal.GetObjectForIUnknown(pointer)
            try action instance
            finally Marshal.ReleaseComObject(instance) |> ignore
        finally
            if pointer <> IntPtr.Zero then Marshal.Release(pointer) |> ignore

    let private withPinnedViewError hwnd failed action =
        try
            let shell = Activator.CreateInstance(Type.GetTypeFromCLSID(Guid("C2F03A33-21F5-47FA-B4BB-156362A2F239")))
            try
                withService (shell :?> IShellServices) (Guid("B5A399E7-1C87-46B8-88E9-FC5747B171BD")) typeof<IPins>.GUID (fun pins ->
                    withService (shell :?> IShellServices) typeof<IViews>.GUID typeof<IViews>.GUID (fun views ->
                        let mutable view = IntPtr.Zero
                        try
                            Marshal.ThrowExceptionForHR((views :?> IViews).ForWindow(hwnd, &view))
                            if view = IntPtr.Zero then failed -2147467261; false else action (pins :?> IPins) view
                        finally
                            if view <> IntPtr.Zero then Marshal.Release(view) |> ignore))
            finally Marshal.ReleaseComObject(shell) |> ignore
        with ex -> failed ex.HResult; trace hwnd "pin-failure" "service" ex.HResult ""; false

    let private withPinnedView hwnd action = withPinnedViewError hwnd ignore action

    let private canPin hwnd =
        withPinnedView hwnd (fun pins view ->
            let mutable pinned = 0
            pins.IsView(view, &pinned) >= 0)

    [<DllImport("shell32.dll", PreserveSig = true)>]
    extern int private SHGetPropertyStoreForWindow(IntPtr hwnd, Guid& iid, IPropertyStore& store)
    [<DllImport("ole32.dll", PreserveSig = true)>]
    extern int private PropVariantClear(IntPtr value)
    [<DllImport("user32.dll")>]
    extern uint32 private GetWindowThreadProcessId(IntPtr hwnd, uint32& processId)
    [<DllImport("user32.dll")>]
    extern bool private IsWindow(IntPtr hwnd)
    [<DllImport("user32.dll")>]
    extern bool private IsWindowVisible(IntPtr hwnd)
    [<DllImport("user32.dll", CharSet = CharSet.Unicode)>]
    extern IntPtr private FindWindowEx(IntPtr parent, IntPtr after, string className, string windowName)
    [<DllImport("user32.dll")>]
    extern IntPtr private GetWindow(IntPtr hwnd, uint32 command)
    [<DllImport("user32.dll", EntryPoint = "GetWindowLongW")>]
    extern int private GetWindowLong(IntPtr hwnd, int index)
    [<DllImport("user32.dll", CharSet = CharSet.Unicode)>]
    extern bool private SetProp(IntPtr hwnd, string name, IntPtr value)
    [<DllImport("user32.dll", CharSet = CharSet.Unicode)>]
    extern IntPtr private GetProp(IntPtr hwnd, string name)
    [<DllImport("user32.dll", CharSet = CharSet.Unicode)>]
    extern IntPtr private RemoveProp(IntPtr hwnd, string name)
    let private marker = "WindowTabs.TaskbarAppId.Generation"

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern IntPtr private OpenProcess(uint32 access, bool inheritHandle, uint32 processId)
    [<DllImport("kernel32.dll")>]
    extern bool private CloseHandle(IntPtr handle)
    [<DllImport("kernel32.dll")>]
    extern bool private GetProcessTimes(IntPtr handle, int64& creation, int64& exit, int64& kernel, int64& user)

    /// The shell's rule for a taskbar button: visible, and either an app
    /// window or neither a tool window nor owned. Others are never rebuilt -
    /// AddTab would give them a button they never had.
    let private hasButton hwnd =
        IsWindow(hwnd) && IsWindowVisible(hwnd) &&
        let exStyle = GetWindowLong(hwnd, -20)
        exStyle &&& 0x40000 <> 0 || (exStyle &&& 0x80 = 0 && GetWindow(hwnd, 4u) = IntPtr.Zero)

    let private processOf hwnd =
        let mutable pid = 0u
        GetWindowThreadProcessId(hwnd, &pid) |> ignore
        pid

    let private startedOf (pid: uint32) =
        // PROCESS_QUERY_LIMITED_INFORMATION
        let handle = OpenProcess(0x1000u, false, pid)
        if handle = IntPtr.Zero then None
        else
            try
                let mutable creation = 0L
                let mutable exited = 0L
                let mutable kernel = 0L
                let mutable user = 0L
                if GetProcessTimes(handle, &creation, &exited, &kernel, &user) then Some creation else None
            finally CloseHandle(handle) |> ignore

    let private withStore hwnd (f: IPropertyStore -> 'a) (failed: int -> 'a) =
#if DEBUG
        use metric = Bemo.Win32.ShellCallTrace.Measure(2)
#endif
        let mutable iid = typeof<IPropertyStore>.GUID
        let mutable store = Unchecked.defaultof<IPropertyStore>
        try
            let hr = SHGetPropertyStoreForWindow(hwnd, &iid, &store)
            if hr < 0 then failed hr else f store
        finally
            if not (isNull (box store)) then Marshal.ReleaseComObject(store) |> ignore

    // PROPVARIANT is 16 bytes on x86 and 24 on x64; its union begins at 8. A
    // smaller buffer is an ABI error that GetValue answers with VT_EMPTY and S_OK.
    let private withVariant f =
        let size = if IntPtr.Size = 8 then 24 else 16
        let value = Marshal.AllocCoTaskMem(size)
        try
            for offset in 0 .. size - 1 do Marshal.WriteByte(value, offset, 0uy)
            f value
        finally
            PropVariantClear(value) |> ignore
            Marshal.FreeCoTaskMem(value)

    /// PKEY_AppUserModel_ID of a window: Some None when it has none (VT_EMPTY),
    /// Some(Some id) for a string, None when it cannot be read or is of another type.
    let private readId hwnd : string option option =
#if DEBUG
        use metric = Bemo.Win32.ShellCallTrace.Measure(3)
#endif
        try
            withStore hwnd
                (fun store ->
                    withVariant (fun value ->
                        let mutable key = PropertyKey(5u)
                        if store.GetValue(&key, value) < 0 then None
                        else
                            match Marshal.ReadInt16(value) with
                            | 0s -> Some None
                            | 31s -> Some(Some(Marshal.PtrToStringUni(Marshal.ReadIntPtr(value, 8))))
                            | _ -> None))
                (fun _ -> None)
        with _ -> None

    /// SetValue of PKEY_AppUserModel_ID; None writes VT_EMPTY (an empty string
    /// is refused).
    let private setId hwnd (id: string option) =
#if DEBUG
        use metric = Bemo.Win32.ShellCallTrace.Measure(4)
#endif
        withStore hwnd
            (fun store ->
                withVariant (fun value ->
                    match id with
                    | Some text ->
                        Marshal.WriteInt16(value, 31s) // VT_LPWSTR
                        Marshal.WriteIntPtr(value, 8, Marshal.StringToCoTaskMemUni(text))
                    | None -> ()
                    let mutable key = PropertyKey(5u)
                    let hr = store.SetValue(&key, value)
                    // Window stores apply SetValue at once; Commit is a documented no-op.
                    if hr < 0 then hr else store.Commit()))
            (fun hr -> hr)

    /// The taskbar buttons, as (AutomationId, Name). Only the tray windows are
    /// asked, never other top-level windows (some of them are WindowTabs' own).
    let private readButtons secondary =
#if DEBUG
        use metric = Bemo.Win32.ShellCallTrace.Measure(5)
#endif
        let trays =
            [ for className in (if secondary then [ "Shell_TrayWnd"; "Shell_SecondaryTrayWnd" ] else [ "Shell_TrayWnd" ]) do
                let mutable after = IntPtr.Zero
                let mutable go = true
                while go do
                    after <- FindWindowEx(IntPtr.Zero, after, className, null)
                    if after = IntPtr.Zero then go <- false else yield after ]
        let request = CacheRequest()
        request.AutomationElementMode <- AutomationElementMode.None
        request.Add(AutomationElement.AutomationIdProperty)
        request.Add(AutomationElement.NameProperty)
        let condition = PropertyCondition(AutomationElement.ClassNameProperty, "Taskbar.TaskListButtonAutomationPeer")
        [ for tray in trays do
            let root = AutomationElement.FromHandle(tray)
            use scope = request.Activate()
            let buttons = root.FindAll(TreeScope.Descendants, condition)
            yield [ for i in 0 .. buttons.Count - 1 do
                      let cached = buttons.[i].Cached
                      yield cached.AutomationId, cached.Name ] ]

    let private explorerValue (name: string) =
        try
            use key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced")
            match (if isNull key then null else key.GetValue(name)) with
            | :? int as value -> Some value
            | _ -> None
        with _ -> None

    let private readCounts () =
        try
            let enabled = explorerValue "MMTaskbarEnabled"
            if not (alwaysCombined (explorerValue "TaskbarGlomLevel")) ||
               (enabled = Some 1 && splitAcrossTaskbars (explorerValue "MMTaskbarMode")) then Configuration
            else
                match countsById true false (readButtons (includeSecondary enabled (explorerValue "MMTaskbarGlomLevel"))) with
                | Some m -> Counted m
                | None -> TemporaryFailure
        with ex -> trace IntPtr.Zero "read-failure" "taskbar" ex.HResult ""; TemporaryFailure

    let mutable private sharedWindows: Set<IntPtr> = Set.empty

    let private onCurrentDesktop hwnd =
        if sharedWindows.Contains hwnd then true else
        match VirtualDesktopHelper.TryIsWindowOnCurrentVirtualDesktop(hwnd) with
        | 0, here -> here
        | _ -> not VirtualDesktopHelper.IsSupported

    // ---------------------------------------------------------- secrets --

    // An application's own ID may name a profile or an account: it is kept
    // only encrypted for the current user, never in plain text.
    let private entropy = Encoding.UTF8.GetBytes("WindowTabs.TaskbarAppId.1")

    let private seal (value: string option) =
        value |> Option.map (fun text ->
            Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(text), entropy, DataProtectionScope.CurrentUser)))

    /// Some original, or None when the secret cannot be opened (another user,
    /// a lost key, a damaged line). Only the explicit off path clears it.
    let private unseal (secret: string option) =
        match secret with
        | None -> Some None
        | Some text ->
            try Some(Some(Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(text), entropy, DataProtectionScope.CurrentUser))))
            with _ -> None

    // ---------------------------------------------------------------- state --

    // Owned by whichever worker thread runs; never two at a time (see post).
    /// A window that carries `id`, and its own value to give back.
    type private Held = { entry: Written; original: string option; id: string; pinPending: bool; pinIssued: bool; pinAt: int; lostDesktop: Guid option; lostAt: int; lostRejectedAt: int option }
    /// A window given its own value back, until the button of the ID it
    /// carried is seen without it.
    type private Given = { entry: Written; desktop: Guid option; at: int; rebuilt: bool; retried: bool; paused: bool; confirmed: bool; returnObservation: (Guid * int) option; slow: bool }
    type private Unpinning = { entry: Written; original: string option; desktop: Guid option; at: int }
    type private Check = { id: string; members: IntPtr list; rebuilt: bool; at: int; seq: int64; retried: bool }

    let mutable private slotState: Map<SlotKey, Slot> = Map.empty
    let mutable private filed: Map<IntPtr, SlotKey> = Map.empty
    let mutable private held: Map<IntPtr, Held> = Map.empty
    let mutable private unpinning: Map<IntPtr, Unpinning> = Map.empty
    let mutable private prepareMisses: Map<IntPtr, uint32 * int * int> = Map.empty
    let mutable private lastProcessed: Snapshot option = None
    let mutable private given: Map<IntPtr, Given> = Map.empty
    // Windows left as they are and kept in the recovery file for the next
    // start, untouched in this run: a give-back that failed, a value that
    // could not be read or decrypted, a window that cannot be told apart.
    let mutable private stuck: Map<IntPtr, Written> = Map.empty
    // Old rows survive cleanup until their off-return preparation is durable.
    let mutable private offSavePending: Set<IntPtr> = Set.empty
    // Excluded windows (by process): permanent failures, plus lost app pins
    // whose explicitly scheduled retry has not yet passed its safety checks.
    let mutable private declined: Map<IntPtr, uint32> = Map.empty
    type private Retry = { entry: Written; original: string option; attempt: int; at: int
                           members: Set<IntPtr * Set<IntPtr>>; pending: bool; observation: (Guid * int) option }
    // Keep the backoff after success; repeated external resets never restart it.
    let mutable private retries: Map<IntPtr, Retry> = Map.empty
    let mutable private retryGroups: (IntPtr * IntPtr list) list = []
    let private retryWaiting () = retries |> Map.exists (fun _ r -> r.pending)
    let private retryReady now groups =
        retries |> Map.exists (fun hwnd r ->
            r.pending && retryDue now r.at r.attempt (retryMembership hwnd groups <> r.members))
    // Sets of windows that, written and rebuilt, still did not combine.
    let mutable private refused: Set<Set<IntPtr>> = Set.empty
    let mutable private checks: Map<SlotKey, Check> = Map.empty
    // The slots of the previous pass: a slot is written once it reads the same twice.
    let mutable private lastDesired: Map<SlotKey, IntPtr list> = Map.empty
    let mutable private waiting = false
    let mutable private replan = false
    let mutable private lastRebuild: int option = None
    let mutable private lastSwitch: int option = None
    let mutable private lastWrite = Environment.TickCount

    let mutable private observed: Set<IntPtr> = Set.empty
    let mutable private damagedWindows: Set<IntPtr> = Set.empty
    let mutable private damagedJournal = false

    let mutable private lastCurrent: Guid option = None

    let mutable private holding = false
    let mutable private wantsPass = false
    /// Something is written or waits for a check: the desktop pass keeps handing over.
    let isHolding () = Volatile.Read(&holding)

    let private dueHere (desktop: Guid option) = desktop.IsNone || desktop = Some sharedDesktop || lastCurrent.IsNone || desktop = lastCurrent

    /// A later pass is wanted while a slot waits to settle, after a slot lost
    /// a window that set its own ID, and for checks on the desktop being
    /// looked at. Checks elsewhere wait for a switch, which changes the
    /// snapshot anyway.
    let private updateFlags () =
        let now = Environment.TickCount
        Volatile.Write(&wantsPass,
                       waiting || replan || retryWaiting () || not unpinning.IsEmpty || (held |> Map.exists (fun _ h -> h.entry.pin)) ||
                       (checks |> Map.exists (fun key c -> needsSettledObservation now c.at (fun () -> verificationHere lastCurrent key.desktop (fun () -> c.members |> List.forall onCurrentDesktop)))) ||
                       (given |> Map.exists (fun h g -> not g.paused && (g.confirmed || needsSettledObservation now g.at (fun () -> dueHere g.desktop && (lastCurrent.IsSome || onCurrentDesktop h))))))
        // Kept rows still need the next settings transition, even without active checks.
        Volatile.Write(&holding, waiting || retryWaiting () || not stuck.IsEmpty || not unpinning.IsEmpty || not held.IsEmpty || not checks.IsEmpty || (given |> Map.exists (fun _ g -> not g.paused)))

    // ------------------------------------------------------- recovery file --

    let private recoveryPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowTabs", "taskbar_applied.txt")

    // A recovery file that could neither be read nor be put aside: writing a
    // new one would destroy what it holds, so nothing new is written in this run.
    let mutable private fileBlocked = false

    let private listed () =
        (held |> Map.toList |> List.map (fun (_, h) -> { h.entry with phase = Applied })) @
        (stuck |> Map.toList |> List.map snd) @
        (unpinning |> Map.toList |> List.map (fun (_, u) -> u.entry)) @
        (given |> Map.toList |> List.map (fun (_, g) -> { g.entry with phase = Returned }))

    let mutable private saved: Written list = []

    /// Written before any ID is set, so a crash leaves the list behind. A
    /// temporary file flushed to disk replaces the old one: a crash while
    /// writing leaves the old list whole.
    let private saveFile (entries: Written list) =
        if entries.IsEmpty then
            if File.Exists(recoveryPath) then File.Delete(recoveryPath)
        else
            Directory.CreateDirectory(Path.GetDirectoryName(recoveryPath)) |> ignore
            let temp = recoveryPath + ".tmp"
            let bytes = Encoding.ASCII.GetBytes(format entries)
            using (new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) (fun output ->
                output.Write(bytes, 0, bytes.Length)
                output.Flush(true))
            if File.Exists(recoveryPath) then File.Replace(temp, recoveryPath, null) else File.Move(temp, recoveryPath)
        saved <- entries

    let private trySaveFile entries =
        if fileBlocked then false
        else
            try saveFile entries; true
            with ex -> trace IntPtr.Zero "file-failure" "save" ex.HResult ""; false

    let private saveIfChanged () =
        let entries = listed ()
        if entries <> saved then trySaveFile entries |> ignore

    /// A recovery file that cannot be read whole is kept beside the new one,
    /// never overwritten; when even that fails, no new file is written.
    let private putAside (move: bool) =
        try
            let target = sprintf "%s.%s.bad" recoveryPath (DateTime.Now.ToString("yyyyMMddHHmmssfff"))
            if move then File.Move(recoveryPath, target) else File.Copy(recoveryPath, target)
            trace IntPtr.Zero "file-aside" (if move then "unreadable" else "damaged") 0 ""
        with ex ->
            fileBlocked <- true
            trace IntPtr.Zero "file-failure" "aside" ex.HResult ""

    // ------------------------------------------------------------- rebuild --

    type private Rebuilt =
        | Done
        | Failed
        /// On another desktop: a rebuild never moves a window, and hiding and
        /// showing it again would.
        | Away

    /// DeleteTab then AddTab, nothing in between: the taskbar files the window
    /// again under its current ID. Only windows shown on this desktop. A
    /// window without a button has nothing to refile.
    let private rebuild reason (hwnd: IntPtr) =
#if DEBUG
        use metric = Bemo.Win32.ShellCallTrace.Measure(12)
#endif
        if not (hasButton hwnd) then
            trace hwnd "rebuild-skip" reason 0 "no-button"
            Done
        elif not (onCurrentDesktop hwnd) then
            trace hwnd "rebuild-skip" reason 0 "away"
            Away
        else
            try
                let bar = Activator.CreateInstance(Type.GetTypeFromCLSID(Guid("56FDF344-FD6D-11D0-958A-006097C9A090"))) :?> ITaskbarList
                try
                    let hr = bar.HrInit()
                    if hr < 0 then
                        trace hwnd "rebuild-failure" reason hr ""
                        Failed
                    else
                        let mutable deleted = 0
                        let mutable added = 0
                        lastRebuild <- Some Environment.TickCount
                        try deleted <- bar.DeleteTab(hwnd)
                        finally added <- bar.AddTab(hwnd)
                        trace hwnd "rebuild" reason (if deleted < 0 then deleted else added) ""
                        if deleted < 0 || added < 0 then Failed else Done
                finally Marshal.ReleaseComObject(bar) |> ignore
            with ex ->
                trace hwnd "rebuild-failure" reason ex.HResult ""
                Failed

    // ---------------------------------------------------------------- work --

    let private newId () = prefix + Guid.NewGuid().ToString("N")

#if DEBUG
    // Diagnostic state is separate from policy state; elapsed time is not a key.
    let mutable private diagnosticStates = Map.empty
    let mutable private returnReasons: Map<IntPtr, string> = Map.empty
    let private traceChange hwnd action reason state detail =
        try
            let key = hwnd, action, reason
            if diagnosticStates.TryFind key <> Some state then
                diagnosticStates <- diagnosticStates.Add(key, state)
                trace hwnd action reason 0 detail
        with _ -> ()

    let private idClass (entry: Written) original value =
        match value with
        | None -> "unreadable"
        | Some None -> "empty"
        | Some value when entry.id.IsSome && value = entry.id -> "ours-current"
        | Some value when entry.previous.IsSome && value = entry.previous -> "ours-previous"
        | Some value when value = original -> "original"
        | _ -> "other"

    let private desktopClass = function
        | None -> "failed"
        | Some d when d = windowPinnedDesktop -> "window"
        | Some d when d = appPinnedDesktop -> "app"
        | Some _ -> "normal"
#endif

    // Return the exact reading to the caller, without another property-store read.
    let private observedId path hwnd (h: Held) value =
#if DEBUG
        let classification = idClass { h.entry with id = Some h.id } h.original value
        let detail = sprintf "id=%s original=%b" classification (value = Some h.original)
        traceChange hwnd "id-observed" path detail detail
#endif
        value

    let private sameWindow (entry: Written) =
        let hwnd = nativeint entry.hwnd
        let alive = IsWindow(hwnd)
        let pid = if alive then processOf hwnd else 0u
        let result = identity entry alive pid (if alive && pid = entry.pid then startedOf pid else None)
        if result = Same && (entry.cookie |> Option.exists (fun c -> GetProp(hwnd, marker) <> nativeint c)) then Undecided else result

    let private cancelRetry hwnd =
        let retry = retries.TryFind hwnd
        retries <- retries.Remove hwnd
        match retry with
        | Some r ->
            let retained = held.ContainsKey hwnd || given.ContainsKey hwnd ||
                           unpinning.ContainsKey hwnd || stuck.ContainsKey hwnd ||
                           offSavePending.Contains hwnd || damagedWindows.Contains hwnd ||
                           (saved |> List.exists (fun entry -> entry.hwnd = int64 hwnd))
            if not retained && removeRetryMarker (sameWindow r.entry) r.entry.cookie.IsSome retained then
                RemoveProp(hwnd, marker) |> ignore
        | _ -> ()

    let private decline reason hwnd =
#if DEBUG
        let detail = if declined.ContainsKey hwnd then "membership=existing" else "membership=new"
        traceChange hwnd "decline" reason detail detail
#endif
        cancelRetry hwnd
        declined <- declined.Add(hwnd, processOf hwnd)

    let private desktopReading hwnd =
        let mutable desktop = Guid.Empty
        if not VirtualDesktopHelper.IsSupported then Some Guid.Empty, 0
        else
            let hr = VirtualDesktopHelper.TryGetWindowDesktopId(hwnd, &desktop)
            (if hr = 0 && desktop <> Guid.Empty then Some desktop else None), hr

    let private desktopOf hwnd = desktopReading hwnd |> fst

    let private changePin hwnd pin =
#if DEBUG
        use metric = Bemo.Win32.ShellCallTrace.Measure(11)
#endif
        withPinnedView hwnd (fun pins view ->
            let hr = if pin then pins.PinView(view) else pins.UnpinView(view)
            trace hwnd "pin" (if pin then "apply" else "return") hr ""
            hr >= 0)

    let private readPinResult hwnd =
        let mutable result = None
        let mutable hr = 0
        withPinnedViewError hwnd (fun failure -> hr <- failure) (fun pins view ->
            let mutable pinned = 0
            hr <- pins.IsView(view, &pinned)
            if hr >= 0 then result <- Some(pinned <> 0)
            result.IsSome) |> ignore
        result, hr

    let private readPin hwnd = readPinResult hwnd |> fst

    /// Left as it is; listed for the next start, never touched again in this run.
    let private keep reason hwnd (entry: Written) =
        stuck <- stuck.Add(hwnd, entry)
        decline reason hwnd

    /// Writes the window's own value back if it still carries an ID of ours.
    /// An application that set a value of its own since keeps it.
    let private giveBackCore off unpinned reason hwnd (entry: Written) (original: string option) desktop =
        try
#if DEBUG
            // Keep the initiating reason through deferred unpin completion.
            if reason <> "unpin-observed" || not (returnReasons.ContainsKey hwnd) then
                returnReasons <- returnReasons.Add(hwnd, reason)
#endif
            let current = if off then readId hwnd else None
            match (if offReturn (not off) current then Same else sameWindow entry) with
            | Gone ->
#if DEBUG
                traceChange hwnd "restore-skip" reason "gone" "identity=gone"
#endif
                ()
            | Undecided ->
#if DEBUG
                traceChange hwnd "restore-skip" reason "undecided" "identity=undecided"
#endif
                keep "restore-identity" hwnd entry
            | Same ->
                match (if off then current else readId hwnd) with
                | None ->
                    trace hwnd "restore-failure" reason 0 "unreadable"
                    keep "restore-unreadable" hwnd entry
                | Some current ->
#if DEBUG
                    if held.ContainsKey hwnd then
                        let detail = sprintf "id=%s original=%b" (idClass entry original (Some current)) (current = original)
                        traceChange hwnd "id-observed" "give-back" detail detail
#endif
                    // Only the off path trusts the prefix without generation evidence.
                    let owned = offReturn (not off) (Some current) || ownsGeneration entry current
                    if owned then
                        let live = desktopOf hwnd
                        if entry.pin && live.IsNone && not unpinned then invalidOp "Window pin state unavailable."
                        let removing = unpinBeforeReturn entry current live && not unpinned
                        if removing && not (changePin hwnd false) then
                            invalidOp "Window pin return failed."
                        if removing && readPin hwnd <> Some false then
                            stuck <- stuck.Remove hwnd
                            unpinning <- unpinning.Add(hwnd, { entry = entry; original = original; desktop = desktop; at = Environment.TickCount })
                        else
                            let hr = try setId hwnd original with ex -> ex.HResult
                            lastWrite <- Environment.TickCount
                            trace hwnd "restore" reason hr (if original.IsSome then "own" else "empty")
                            if readId hwnd = Some original then
                                stuck <- stuck.Remove hwnd
                                given <- given.Add(hwnd, { entry = { entry with phase = Returned }
                                                           desktop = desktop; at = lastWrite; rebuilt = false; retried = false; paused = false; confirmed = false; returnObservation = None; slow = false })
                            else
                                // A failed ID return must not strand our window unpinned.
                                if (removing || unpinned) && sameWindow entry = Same && ownsGeneration entry (readId hwnd |> Option.flatten) then
                                    changePin hwnd true |> ignore
                                keep "restore-readback-failed" hwnd entry
                    elif current = original then
#if DEBUG
                        let detail = sprintf "already-original id=%s release=%s" (idClass entry original (Some current)) (defaultArg (returnReasons.TryFind hwnd) reason)
                        traceChange hwnd "restore-skip" reason detail detail
#endif
                        stuck <- stuck.Remove hwnd
                        given <- given.Add(hwnd, { entry = { entry with phase = Returned }; desktop = desktop
                                                   at = Environment.TickCount; rebuilt = false; retried = false; paused = false; confirmed = false; returnObservation = None; slow = false })
                    else
                        trace hwnd "leave" reason 0 (if entry.id.IsNone && isOurs current then "legacy-unknown-generation" else "application-changed")
                        keep "restore-application-changed" hwnd entry
        with ex ->
            trace hwnd "restore-failure" reason ex.HResult "exception"
            keep "restore-exception" hwnd entry

    let private giveBackTo reason hwnd entry original desktop =
        giveBackCore false false reason hwnd entry original desktop

    /// A closed window needs no ID read; a live reused handle remains protected.
    let private cleanupClosedOff () =
        let closed = offSavePending |> Set.filter (fun hwnd -> not (IsWindow hwnd))
        if not closed.IsEmpty then
            let remaining = listed () |> List.filter (fun e -> not (closed.Contains(nativeint e.hwnd)))
            let pending, rows = finishClosedOff (trySaveFile remaining) closed offSavePending stuck
            offSavePending <- pending
            stuck <- rows

    /// Adopt the live identity and ID so ordinary pin return and verification
    /// follow the actual button, not the stale journal generation.
    let private returnOff hwnd (record: Written option) =
        if unpinning.ContainsKey hwnd then true
        else
            match readId hwnd with
            | current when offReturn false current ->
                try
                    let existing =
                        record |> Option.bind (fun entry ->
                            let original =
                                match held.TryFind hwnd with
                                | Some h -> Some h.original
                                | None -> unseal entry.secret
                            if offCanUseRecord (sameWindow entry) entry (Option.flatten current) original then
                                original |> Option.map (fun value -> entry, value)
                            else None)
                    match existing with
                    | Some(entry, original) ->
                        let desktop = returnDesktop (desktopOf hwnd)
                        held <- held.Remove hwnd
                        filed <- filed.Remove hwnd
                        given <- given.Remove hwnd
                        stuck <- stuck.Add(hwnd, entry)
                        offSavePending <- offSavePending.Remove hwnd
                        giveBackTo "off-ours" hwnd entry original desktop
                        true
                    | None ->
                        let pid = processOf hwnd
                        let started = startedOf pid
                        let source = offSource (IsWindow hwnd) pid started record
                        let opened = source |> Option.bind (fun e -> unseal e.secret)
                        let original = offOriginal opened
                        let cookie = GetProp(hwnd, marker).ToInt64()
                        let entry =
                            { hwnd = int64 hwnd; pid = pid
                              started = defaultArg started 0L
                              phase = Applied; secret = if opened.IsSome then source |> Option.bind (fun e -> e.secret) else None
                              id = Option.flatten current; previous = None
                              cookie = if cookie > 0L && cookie <= int64 Int32.MaxValue then Some(int cookie) else None
                              pin = source |> Option.exists (fun e -> e.pin) }
                        let desktop = returnDesktop (desktopOf hwnd)
                        let remaining = listed () |> List.filter (fun e -> e.hwnd <> int64 hwnd)
                        if trySaveFile (entry :: remaining) then
                            held <- held.Remove hwnd
                            filed <- filed.Remove hwnd
                            given <- given.Remove hwnd
                            stuck <- stuck.Add(hwnd, entry)
                            offSavePending <- offSavePending.Remove hwnd
                            giveBackCore true false "off-ours" hwnd entry original desktop
                            true
                        else false
                with ex ->
                    trace hwnd "restore-failure" "off-ours" ex.HResult "exception"
                    false
            | current ->
                if discardOffStuck false current then
                    // If a failed target changed its own ID, persist the removal first.
                    let pending = offSavePending.Contains hwnd
                    if not pending || trySaveFile (listed () |> List.filter (fun e -> e.hwnd <> int64 hwnd)) then
                        stuck <- stuck.Remove hwnd
                        offSavePending <- offSavePending.Remove hwnd
                        true
                    else false
                else true

    /// Failed preparation must not fall through to held/given release or Gone cleanup.
    let private returnOffKeeping hwnd record =
        if not (returnOff hwnd record) then
            record |> Option.iter (fun entry ->
                offSavePending <- offSavePending.Add hwnd
                held <- held.Remove hwnd
                filed <- filed.Remove hwnd
                given <- given.Remove hwnd
                stuck <- stuck.Add(hwnd, entry))

    let private returnOffKnown () =
        let records = listed () |> List.map (fun e -> nativeint e.hwnd, e) |> Map.ofList
        let windows = Set.unionMany [observed; damagedWindows; records |> Map.toSeq |> Seq.map fst |> Set.ofSeq]
        for hwnd in windows do returnOffKeeping hwnd (records.TryFind hwnd)

    /// A successful command is never repeated while its observation is pending.
    let private finishUnpins final =
        for KeyValue(hwnd, u) in unpinning do
            match sameWindow u.entry with
            | Gone -> unpinning <- unpinning.Remove hwnd
            | Undecided ->
                unpinning <- unpinning.Remove hwnd
                keep "unpin-identity" hwnd u.entry
            | Same ->
                let state = readPin hwnd
                if state = Some false || final || settled Environment.TickCount u.at then
                    unpinning <- unpinning.Remove hwnd
                    if state = Some true then
                        // Keep the applied record while our protection is still present.
                        keep "unpin-still-pinned" hwnd u.entry
                    else
                        // On expiry attempt the original ID even if IsView is unavailable.
                        // A failed write compensates only our own removed protection.
                        giveBackCore false true "unpin-observed" hwnd u.entry u.original u.desktop
#if DEBUG
                    trace hwnd "pin-return-state" "observed" 0
                        (sprintf "pin=%A original=%b" (readPin hwnd) (readId hwnd = Some u.original))
#endif
                    replan <- true

    /// The entry leaves `held` only once the give-back is done, failed into
    /// `stuck` or left to the application: it is never dropped on the way.
    let private release' reason hwnd =
        if reason <> "pin-lost" then cancelRetry hwnd
        let holding = held.TryFind hwnd
#if DEBUG
        let detail = sprintf "held=%b" holding.IsSome
        traceChange hwnd "release-enter" reason detail detail
#endif
        match holding with
        | Some h ->
            giveBackTo reason hwnd h.entry h.original (filed.TryFind hwnd |> Option.map (fun key -> key.desktop))
            held <- held.Remove hwnd
        | None -> ()
        filed <- filed.Remove hwnd

    /// Startup, whatever the setting, once the startup restore is done:
    /// windows that an earlier run left with an ID of ours get their own value
    /// back; windows it gave back are checked. What cannot be decided is kept.
    let private recover off =
        let text =
            try
                if File.Exists(recoveryPath) then Some(File.ReadAllText(recoveryPath, Encoding.ASCII)) else None
            with ex ->
                trace IntPtr.Zero "file-failure" "read" ex.HResult ""
                damagedJournal <- true
                putAside true
                None
        let entries, damaged = match text with Some text -> parse text | None -> [], false
        // Kept aside for diagnosis; the lines that could be read are handled.
        if damaged then
            damagedJournal <- true
            damagedWindows <- text |> Option.map damagedHandles |> Option.defaultValue Set.empty
            for hwnd in damagedWindows do trace hwnd "keep" "damaged-row" 0 ""
            putAside false
        saved <- entries
        let mutable alive = 0
        if off then
            for entry in entries do stuck <- stuck.Add(nativeint entry.hwnd, entry)
        for entry in entries do
            let hwnd = nativeint entry.hwnd
            if off && offReturn false (readId hwnd) then
                alive <- alive + 1
                returnOffKeeping hwnd (Some entry)
            else
                match (if damagedWindows.Contains hwnd then Undecided else sameWindow entry) with
                | Gone -> stuck <- stuck.Remove hwnd
                | Undecided ->
                    alive <- alive + 1
                    trace hwnd "keep" "undecided" 0 ""
                    keep "recover-identity" hwnd entry
                | Same ->
                    alive <- alive + 1
                    let desktop = desktopOf hwnd
                    match entry.phase with
                    | Returned ->
                        stuck <- stuck.Remove hwnd
                        given <- given.Add(hwnd, { entry = entry; desktop = desktop; at = Environment.TickCount; rebuilt = false; retried = false; paused = false; confirmed = false; returnObservation = None; slow = false })
                    | Applied ->
                        match unseal entry.secret with
                        | Some original -> giveBackTo "startup-leftover" hwnd entry original desktop
                        | None ->
                            // On retains the unknown original for a later start.
                            trace hwnd "keep" (if entry.secret = Some "unknown-original" then "legacy-unknown-original" else "unseal-failure") 0 ""
                            keep "recover-original-unavailable" hwnd entry
        if off then
            for hwnd in damagedWindows do
                returnOffKeeping hwnd (entries |> List.tryFind (fun e -> e.hwnd = int64 hwnd))
            stuck <- stuck |> Map.filter (fun hwnd _ -> not (discardOffStuck (offSavePending.Contains hwnd) (readId hwnd)))
        trace IntPtr.Zero "recover" "startup" 0 (sprintf "listed=%d alive=%d damaged=%b" entries.Length alive damaged)
        saveIfChanged ()

    let private clearRetries () =
        for KeyValue(hwnd, _) in retries do cancelRetry hwnd

    let private releaseAll reason =
        clearRetries ()
        if reason = "disable" then returnOffKnown ()
        let windows = held |> Map.toList |> List.map fst
        for hwnd in windows do
            match held.TryFind hwnd with
            | Some h ->
                match sameWindow h.entry with
                | Same -> release' reason hwnd
                | Undecided ->
                    held <- held.Remove hwnd
                    keep "release-identity" hwnd h.entry
                | Gone -> held <- held.Remove hwnd
            | None -> ()
        lastProcessed <- None
        filed <- Map.empty
        slotState <- Map.empty
        checks <- Map.empty
        lastDesired <- Map.empty
        waiting <- false
        replan <- false
        trace IntPtr.Zero "release" reason 0 (sprintf "windows=%d" windows.Length)
        saveIfChanged ()

    /// Prefer Shell evidence. If counting is unavailable, an identified window
    /// with its original value read back can finish without guessing a rebuild.
    /// Other failures retain the durable row but do not keep observation alive.
    let private verifyGiven (counts: Lazy<CountReading>) final =
        let now = Environment.TickCount
        let mutable completed = false
        // Keep the row and its identity marker until its durable removal succeeds.
        // A failed save is retried on the next hand-over without repeating Shell work.
        let finish hwnd (g: Given) =
            given <- given.Add(hwnd, { g with confirmed = true })
            let remaining = listed () |> List.filter (fun e -> e.hwnd <> int64 hwnd)
            if trySaveFile remaining then
                given <- given.Remove hwnd
                completed <- true
                if not (retries.ContainsKey hwnd) && g.entry.cookie.IsSome && sameWindow g.entry = Same then RemoveProp(hwnd, marker) |> ignore
        let due = given |> Map.filter (fun h g -> not g.paused && (g.confirmed || (onCurrentDesktop h && returnedCheckDue final g.slow now g.at)))
        for KeyValue(hwnd, g) in due do
            let pause reason =
                trace hwnd "verify" reason 0 ""
                given <- given.Add(hwnd, { g with paused = true })
                // A paused bound member no longer gates a shared-only destination.
                replan <- true
            match sameWindow g.entry with
            | Gone -> given <- given.Remove hwnd
            | Undecided -> pause "given-identity"
            | Same when g.confirmed -> finish hwnd g
            | Same ->
                let original = unseal g.entry.secret
                let current = readId hwnd
                let desktop = desktopOf hwnd
                let observedAt = Environment.TickCount
                let observation, pinVerified =
                    match original with
                    | Some value -> returnedPinObservation g.entry true value current observedAt g.returnObservation desktop
                    | None -> None, false
                let slow = not pinVerified && observation.IsNone
                if slow <> g.slow then replan <- true
                let g = { g with returnObservation = observation; slow = slow }
                given <- given.Add(hwnd, g)
                match original with
                | Some original when current = Some original && pinVerified ->
                    let carriers =
                        Set.unionMany [observed; held |> Map.toSeq |> Seq.map fst |> Set.ofSeq
                                       stuck |> Map.toSeq |> Seq.map fst |> Set.ofSeq]
                        |> Set.remove hwnd
                    let ids = [g.entry.id; g.entry.previous] |> List.choose id |> List.distinct
                    let outcomes =
                        ids |> List.map (fun id ->
                            let holders =
                                match countMap counts.Value with
                                | Some m when defaultArg (m.TryFind id) 0 > 0 ->
                                    carriers |> Seq.filter (fun h -> hasButton h && onCurrentDesktop h && readId h = Some(Some id)) |> Seq.length
                                | _ -> 0
                            givenVerdict g.rebuilt (countMap counts.Value) (Some id) holders)
                    let outcome =
                        if ids.IsEmpty || List.contains GivenUnverified outcomes then GivenUnverified
                        elif List.contains GivenStuck outcomes then GivenStuck
                        elif List.contains RebuildGiven outcomes then RebuildGiven
                        else Refiled
                    let valueOnly = outcome = GivenUnverified && not ids.IsEmpty && returnWithoutShell g.retried counts.Value
                    match (if valueOnly then Refiled else outcome) with
                    | Refiled ->
                        let reason = if not valueOnly then "given-back" else
                                     match counts.Value with Configuration -> "given-value-only-configuration" | _ -> "given-value-only-unavailable"
                        trace hwnd "verify" reason 0 ""
#if DEBUG
                        let detail = sprintf "release=%s" (defaultArg (returnReasons.TryFind hwnd) "startup")
                        traceChange hwnd "return-complete" reason detail detail
#endif
                        finish hwnd g
                    | GivenUnverified when retryGivenCount final g.retried counts.Value ->
                        given <- given.Add(hwnd, { g with retried = true; at = observedAt })
                    | GivenUnverified -> pause "given-unverified"
                    | GivenStuck -> pause "given-stuck"
                    | RebuildGiven ->
                        let currentDesktop = desktopOf hwnd
                        if not (canRebuildReturned g.entry.pin pinVerified desktop currentDesktop) then
                            // A transient reread must not permanently pause a return.
                            given <- given.Add(hwnd, { g with returnObservation = None;
                                                              slow = returnedRebuildSlow currentDesktop; at = Environment.TickCount })
                            replan <- true
                        elif not final then
                            match rebuild "given-back" hwnd with
                            | Done -> given <- given.Add(hwnd, { g with rebuilt = true; at = Environment.TickCount })
                            | Failed -> pause "given-rebuild-failed"
                            | Away -> ()
                | Some original when current = Some original && g.entry.pin ->
                    // Unverifiable pins retain their row without gating the group.
                    given <- given.Add(hwnd, { g with at = observedAt })
                | _ -> pause "given-value-unverified"
        completed

#if DEBUG
    let private retryVerification = System.Collections.Generic.Dictionary<IntPtr, int * int>()
    let private retryResult members verdict =
        for hwnd in members do
            match retryVerification.TryGetValue hwnd with
            | true, (attempt, at) ->
                Bemo.Win32.ShellCallTrace.Retry(verdict, attempt, Environment.TickCount - at)
                retryVerification.Remove hwnd |> ignore
            | _ -> ()
#endif

#if DEBUG
    let mutable private awayCountAt = 0
#endif

    let private verify (s: Snapshot) seq =
        let now = Environment.TickCount
        let due = checks |> Map.filter (fun key c -> verificationHere s.current key.desktop (fun () -> c.members |> List.forall onCurrentDesktop) && c.seq < seq && settled now c.at && (c.members |> List.forall (fun h -> held.TryFind h |> Option.forall (fun h -> not (pinSettling h.pinPending h.lostDesktop)))))
        let counts = lazy (readCounts ())
        for KeyValue(key, c) in due do
            let expected = c.members |> List.filter hasButton |> List.length
            let actual = countMap counts.Value |> Option.bind (fun m -> m.TryFind c.id) |> Option.defaultValue 0
            let detail = sprintf "group=%X expected=%d actual=%d" (int64 key.group) expected actual
            match verdict c.rebuilt expected (countMap counts.Value) c.id (fun () -> c.members |> List.map (fun hwnd ->
                let value = readId hwnd
                hwnd, (match held.TryFind hwnd with Some h -> observedId "verify" hwnd h value | None -> value))) with
            | Merged ->
#if DEBUG
                retryResult c.members "merged"
#endif
                trace IntPtr.Zero "verify" "merged" 0 detail
                checks <- checks.Remove key
            | Declined off ->
#if DEBUG
                retryResult c.members "application-changed"
#endif
                // The application set its own ID again: it keeps its own
                // button, and the slot is planned again without it.
                for hwnd in off do
                    trace hwnd "decline" "application-changed" 0 ""
                    decline "verify-application-changed" hwnd
                    release' "declined" hwnd
                checks <- checks.Remove key
                replan <- true
            | RebuildOnce ->
                let mutable rebuiltCount = 0
                for hwnd in c.members do
                    match held.TryFind hwnd with
                    | Some h when sameWindow h.entry = Same && canRebuild h.entry.pin (desktopOf hwnd) && (readId hwnd |> observedId "verify-rebuild" hwnd h) = Some(Some c.id) ->
                        if rebuild "verify" hwnd = Done then rebuiltCount <- rebuiltCount + 1
                    | _ -> ()
#if DEBUG
                trace IntPtr.Zero "verify" "short-rebuild" 0 (sprintf "%s rebuilt=%d" detail rebuiltCount)
#endif
                checks <- checks.Add(key, { c with rebuilt = true; at = Environment.TickCount; seq = seq })
            | Short ->
#if DEBUG
                retryResult c.members "short"
#endif
                // Which window stays out cannot be told: the whole button
                // is given back, and this set of windows is not tried again.
                trace IntPtr.Zero "verify" "short" 0 detail
                refused <- refused.Add(Set.ofList c.members)
                for hwnd in c.members do release' "short" hwnd
                checks <- checks.Remove key
                slotState <- slotState.Remove key
                replan <- true
            | Unverified when retryCount c.retried counts.Value ->
                checks <- checks.Add(key, { c with retried = true; seq = seq; at = now })
            | Unverified ->
#if DEBUG
                retryResult c.members "unverified"
#endif
                trace IntPtr.Zero "verify" "unverified" 0 detail
                checks <- checks.Remove key
#if DEBUG
        if uint32 (now - awayCountAt) >= 5000u && not held.IsEmpty then
            awayCountAt <- now
            match countMap counts.Value with
            | Some buttons ->
                let ids = held |> Map.toSeq |> Seq.map (fun (_, h) -> h.id) |> Set.ofSeq
                let mutable missing = 0
                let mutable counted = 0
                for id in ids do
                    let actual = defaultArg (buttons.TryFind id) 0
                    if actual > 0 then
                        let here = observed |> Seq.exists (fun hwnd -> hasButton hwnd && onCurrentDesktop hwnd && readId hwnd = Some(Some id))
                        if not here then
                            missing <- missing + 1
                            counted <- counted + actual
                trace IntPtr.Zero "verify-away" "counts" 0 (sprintf "ids=%d unmatched-ids=%d unmatched-count=%d" ids.Count missing counted)
            | None -> ()
#endif
        let completed = verifyGiven counts false
        replan <- replan || resumeAfterReturn s.enabled completed


    let private finishPin hwnd (h: Held) =
        let fail reason =
#if DEBUG
            let detail = sprintf "pin=%b pending=%b issued=%b" h.entry.pin h.pinPending h.pinIssued
            traceChange hwnd "finish-pin-failed" reason detail detail
#endif
            decline reason hwnd
            release' reason hwnd
            replan <- true
        if sameWindow h.entry <> Same || (readId hwnd |> observedId "finish-pin" hwnd h) <> Some(Some h.id) then
            fail "pin-changed"
        else
            match pinObservation Environment.TickCount h.pinAt h.pinIssued (desktopOf hwnd) with
            | PinReady -> held <- held.Add(hwnd, { h with pinPending = false })
            | PinIssue ->
                if changePin hwnd true then
                    let pending = { h with pinIssued = true; pinAt = Environment.TickCount }
                    held <- held.Add(hwnd, { pending with pinPending = desktopOf hwnd <> Some windowPinnedDesktop })
                else fail "pin-command-failed"
            | PinWait -> ()
            | PinExpired -> fail "pin-unverified"

    let private finishPins () =
        for KeyValue(hwnd, h) in held do
            if h.pinPending then finishPin hwnd h
            elif h.entry.pin && sameWindow h.entry = Same && pinLossCheckDue Environment.TickCount h.lostRejectedAt then
                let live, hr = desktopReading hwnd
                let now = Environment.TickCount
                let candidate, desktopConfirmed = lostPinObservation now h.lostAt h.lostDesktop live
                let view, viewHr = if desktopConfirmed then readPinResult hwnd else None, 0
                let candidate, confirmed = corroboratePinLoss candidate desktopConfirmed view
                held <- held.Add(hwnd, { h with lostDesktop = candidate;
                                               lostAt = (if candidate = h.lostDesktop then h.lostAt else now);
                                               lostRejectedAt = pinLossRejectedAt Environment.TickCount desktopConfirmed view })
                if confirmed && settled now h.pinAt then
#if DEBUG
                    let elapsed at = at |> Option.map (fun t -> uint32 (now - t) |> string) |> Option.defaultValue "none"
                    let exe =
                        try
                            use process = System.Diagnostics.Process.GetProcessById(int h.entry.pid)
                            process.ProcessName + ".exe"
                        with _ -> "unknown"
                    trace IntPtr.Zero "pin-lost-observation" "confirmed" hr
                        (sprintf "desktop=%s previous=%s observation-ms=%d apply-ms=%d rebuild-ms=%s switch-ms=%s exe=%s"
                            (desktopClass live) (desktopClass h.lostDesktop) (now - h.lostAt) (now - h.pinAt)
                            (elapsed lastRebuild) (elapsed lastSwitch) exe)
                    trace IntPtr.Zero "pin-lost-observation" "view" viewHr
                        (sprintf "view=%s id=%s exe=%s"
                            (match view with Some true -> "pinned" | Some false -> "unpinned" | None -> "failed")
                            (idClass h.entry h.original (readId hwnd)) exe)
#endif
                    let previous = retries.TryFind hwnd
                    decline "pin-lost" hwnd
                    let attempt = previous |> Option.map (fun r -> min 4 (r.attempt + 1)) |> Option.defaultValue 1
                    retries <- retries.Add(hwnd, { entry = h.entry; original = h.original; attempt = attempt
                                                   at = now; members = retryMembership hwnd retryGroups; pending = true; observation = None })
                    trace hwnd "retry" "pin-lost" 0 (sprintf "attempt=%d wait-ms=%d" attempt (retryWaitMs attempt))
                    release' "pin-lost" hwnd
                    replan <- true
            elif h.lostDesktop.IsSome then
                held <- held.Add(hwnd, { h with lostDesktop = None })

    /// Retire only a verified return, durably, before allowing normal preparation.
    let private retryPending (s: Snapshot) =
        for KeyValue(hwnd, r) in retries do
            let changed = retryMembership hwnd s.groups <> r.members
            if r.pending && retryDue Environment.TickCount r.at r.attempt changed then
                if sameWindow r.entry <> Same then decline "retry-identity" hwnd
                elif stuck.ContainsKey hwnd then decline "retry-return-pending" hwnd
                else
                    let value = readId hwnd
                    let desktop = desktopOf hwnd
                    let observedAt = Environment.TickCount
                    let observation, ordinary = returnedPinObservation r.entry true r.original value observedAt r.observation desktop
                    let r = { r with observation = observation }
                    retries <- retries.Add(hwnd, r)
                    if value.IsSome && value <> Some r.original && not (value |> Option.exists isOurs) then
                        decline "retry-application-changed" hwnd
                    elif unpinning.ContainsKey hwnd || not (retryReturnedPin r.entry.pin r.original value desktop ordinary (given.ContainsKey hwnd)) then
                        retries <- retries.Add(hwnd, { r with at = Environment.TickCount; members = retryMembership hwnd s.groups })
                    else
                        let retired =
                            not (given.ContainsKey hwnd) ||
                            trySaveFile (listed () |> List.filter (fun e -> e.hwnd <> int64 hwnd))
                        if retired then
                            given <- given.Remove hwnd
                            declined <- declined.Remove hwnd
                            retries <- retries.Add(hwnd, { r with pending = false })
#if DEBUG
                            retryVerification.[hwnd] <- (r.attempt, Environment.TickCount)
#endif
                            trace hwnd "retry-due" "pin-lost" 0
                                (sprintf "attempt=%d wait-ms=%d changed=%b" r.attempt (retryWaitMs r.attempt) changed)
                        else
                            // File failures must not turn a due retry into a busy loop.
                            retries <- retries.Add(hwnd, { r with at = Environment.TickCount; members = retryMembership hwnd s.groups })

    let private fullPass (s: Snapshot) seq =
        lastCurrent <- s.current
        observed <- Set.union (s.places |> Map.toSeq |> Seq.map fst |> Set.ofSeq) (s.groups |> List.collect snd |> Set.ofList)
        replan <- false
        if not s.enabled then
            clearRetries ()
            returnOffKnown ()
        // Windows that closed, or whose handle another process now uses.
        let gone (entry: Written) = forgetGone (offSavePending.Contains(nativeint entry.hwnd)) (sameWindow entry)
        for KeyValue(hwnd, h) in held do
            if gone h.entry then
                held <- held.Remove hwnd
                filed <- filed.Remove hwnd
        given <- given |> Map.filter (fun _ g -> not (gone g.entry))
                       |> Map.map (fun hwnd g ->
                           match s.places.TryFind hwnd with
                           | Some(OnDesktop d) -> { g with desktop = Some d }
                           | _ -> { g with desktop = returnDesktop (desktopOf hwnd) })
        stuck <- stuck |> Map.filter (fun _ e -> not (gone e))
        declined <- declined |> Map.filter (fun hwnd pid -> IsWindow(hwnd) && processOf hwnd = pid)
#if DEBUG
        diagnosticStates <- diagnosticStates |> Map.filter (fun (hwnd, _, _) _ -> IsWindow(hwnd))
        returnReasons <- returnReasons |> Map.filter (fun hwnd _ -> IsWindow(hwnd))
#endif
        for KeyValue(hwnd, r) in retries do
            let alive = IsWindow(hwnd) && processOf hwnd = r.entry.pid
            if not (alive && (s.groups |> List.exists (fun (_, members) -> List.contains hwnd members))) then
                if alive && r.pending then declined <- declined.Remove hwnd
                cancelRetry hwnd
        if s.enabled then retryPending s
        prepareMisses <- prepareMisses |> Map.filter (fun hwnd (pid, _, _) -> IsWindow(hwnd) && processOf hwnd = pid)
        refused <- refused |> Set.filter (Set.forall (fun h -> IsWindow(h)))
        finishUnpins false
        if s.enabled then finishPins ()
        // An ID change may temporarily remove the Shell's shared classification.
        // Our durable promotion intent keeps membership stable during hand-over.
        let shared = Set.union s.shared (held |> Map.toSeq |> Seq.choose (fun (h, v) -> if v.entry.pin then Some h else None) |> Set.ofSeq)
        sharedWindows <- shared
        let eligible =
            if s.enabled then
                let excluded = declined |> Map.fold (fun (set: Set<IntPtr>) hwnd _ -> set.Add hwnd) s.excluded
                let excluded = stuck |> Map.fold (fun (set: Set<IntPtr>) hwnd _ -> set.Add hwnd) excluded
                let excluded = unpinning |> Map.fold (fun (set: Set<IntPtr>) hwnd _ -> set.Add hwnd) excluded
                let excluded = Set.union excluded damagedWindows
                let excluded = given |> Map.fold (fun (set: Set<IntPtr>) hwnd _ -> set.Add hwnd) excluded
                slots s.groups excluded (fun h -> defaultArg (s.places.TryFind h) Unknown) filed refused shared s.shown s.current
            else Map.empty
        let returning = given |> Map.toSeq |> Seq.choose (fun (hwnd, g) -> if g.paused || g.slow then None else Some hwnd) |> Set.ofSeq
        let desired = eligible |> deferReturningGroups s.groups returning shared
        let next, steps, waitingNow = settledSharedPlan newId lastDesired slotState (held |> Map.map (fun _ h -> h.id)) desired shared
        let steps = retainWaitingShared shared filed desired eligible steps
        let steps = steps |> List.filter (function
            | Apply(hwnd, _) ->
                let leaderPending = desired |> Map.exists (fun key members ->
                    List.contains hwnd members && (held.TryFind next.[key].leader |> Option.exists (fun h -> pinSettling h.pinPending h.lostDesktop)))
                if leaderPending then replan <- true
                not leaderPending
            | _ -> true)
        lastDesired <- desired
        waiting <- waitingNow
        let slotOf = desired |> Map.toList |> List.collect (fun (key, members) -> members |> List.map (fun h -> h, key)) |> Map.ofList
        for step in steps do
            match step with
            | Restore hwnd -> release' "left" hwnd
            | Apply _ -> ()
        // Each new window's own value is read first, then listed, then replaced.
        let fresh =
            steps |> List.choose (function
                | Apply(hwnd, _) when not (held.ContainsKey hwnd) && IsWindow(hwnd) ->
                    try
                        let pid = processOf hwnd
                        match startedOf pid, readId hwnd, desktopOf hwnd with
                        | _, _, None ->
                            let now = Environment.TickCount
                            let misses, first =
                                match prepareMisses.TryFind hwnd with
                                | Some(p, n, first) when p = pid -> min 3 (n + 1), first
                                | _ -> 1, now
                            prepareMisses <- prepareMisses.Add(hwnd, (pid, misses, first))
                            if misses >= 3 && settled now first then
                                trace hwnd "refuse" "desktop-unreadable" 0 ""
                                decline "prepare-desktop-unreadable" hwnd
                            replan <- true
                            None
                        | Some started, Some value, Some desktop ->
                            prepareMisses <- prepareMisses.Remove hwnd
                            match originalOf value with
                            | None ->
                                trace hwnd "keep" (if damagedJournal then "damaged-original" else "unknown-original") 0 ""
                                decline "prepare-unknown-original" hwnd
                                replan <- true
                                None
                            | Some original ->
                                let secret = seal original
                                let cookie = random.Next(1, Int32.MaxValue)
                                if not (SetProp(hwnd, marker, nativeint cookie)) then invalidOp "Window identity unavailable."
                                let target = steps |> List.pick (function Apply(h, id) when h = hwnd -> Some id | _ -> None)
                                let entry = { hwnd = int64 hwnd; pid = pid; started = started; phase = Applied; secret = secret; id = Some target; previous = None; cookie = Some cookie; pin = isAppPinned (Some desktop) }
                                Some(hwnd, (entry, original))
                        | _ ->
                            // Its own value or its process cannot be read: never written, never retried.
                            trace hwnd "refuse" "unreadable" 0 ""
                            decline "prepare-unreadable" hwnd
                            None
                    with ex ->
                        trace hwnd "refuse" "prepare" ex.HResult ""
                        decline "prepare-exception" hwnd
                        None
                | _ -> None)
            |> Map.ofList
        let rotating =
            steps |> List.choose (function
                | Apply(hwnd, id) -> held.TryFind hwnd |> Option.filter (fun h -> not (pinSettling h.pinPending h.lostDesktop)) |> Option.map (fun h -> hwnd, { h.entry with id = Some id; previous = Some h.id })
                | _ -> None) |> Map.ofList
        // Not listed, not written; tried again on a later change (the file may
        // be free again then).
        let isListed =
            (fresh.IsEmpty && rotating.IsEmpty) ||
            trySaveFile ((listed () |> List.map (fun e -> defaultArg (rotating.TryFind(nativeint e.hwnd)) e)) @ (fresh |> Map.toList |> List.map (fun (_, (entry, _)) -> entry)))
        if not isListed then trace IntPtr.Zero "refuse" "unlisted" 0 (sprintf "windows=%d" fresh.Count)
        if isListed then
            // Adopt every prepared row before the first native mutation can throw.
            for KeyValue(hwnd, (entry, _)) in fresh do stuck <- stuck.Add(hwnd, entry)
            for KeyValue(hwnd, entry) in rotating do held <- held.Add(hwnd, { held.[hwnd] with entry = entry })
        else
            for KeyValue(hwnd, (entry, _)) in fresh do
                if entry.cookie |> Option.exists (fun c -> GetProp(hwnd, marker) = nativeint c) then RemoveProp(hwnd, marker) |> ignore
        let mutable touched = Set.empty
        let write hwnd id (key: SlotKey) (entry: Written, original) =
            // Recheck live state immediately before replacing the ID, even on rotation.
            let rotating = held.ContainsKey hwnd
            let live = desktopOf hwnd
            let allowed = pinWriteAllowed entry.pin rotating live
            if not allowed || (entry.pin && not (canPin hwnd)) then
                let reason = if not allowed then "pin-write-disallowed" else "can-pin-failed"
#if DEBUG
                let detail = sprintf "desktop=%s pin=%b rotating=%b" (desktopClass live) entry.pin rotating
                traceChange hwnd "write-refused" reason detail detail
#endif
                if live.IsSome then decline reason hwnd
                replan <- true
                if held.ContainsKey hwnd then release' "app-pinned" hwnd
                else giveBackTo "app-pinned" hwnd entry original (Some key.desktop)
            else
                let hr = setId hwnd (Some id)
                lastWrite <- Environment.TickCount
                // Read back from a fresh store: the value the shell sees.
                let accepted = hr >= 0 && readId hwnd = Some(Some id)
                let role = if next.[key].leader = hwnd then "leader" else "member"
                if accepted then
                    stuck <- stuck.Remove hwnd
                    held <- held.Add(hwnd, { entry = entry; original = original; id = id; pinPending = entry.pin && not rotating; pinIssued = rotating; pinAt = lastWrite; lostDesktop = None; lostAt = lastWrite; lostRejectedAt = None })
                    retries.TryFind hwnd |> Option.iter (fun r -> retries <- retries.Add(hwnd, { r with entry = entry }))
                    filed <- filed.Add(hwnd, key)
                    touched <- touched.Add key
                    trace hwnd "apply" role hr (sprintf "group=%X" (int64 key.group))
                    // Use the first live reading immediately; never wait for Shell here.
                    if held.[hwnd].pinPending then finishPin hwnd held.[hwnd]
                else
                    trace hwnd "refuse" (if hr < 0 then "write" else "readback") hr role
                    decline (if hr < 0 then "write-failed" else "readback-failed") hwnd
                    // Whatever of ours may be on it goes; its own value comes back.
                    if held.ContainsKey hwnd then release' "refused" hwnd
                    else giveBackTo "refused" hwnd entry original (Some key.desktop)
        for step in steps do
            match step with
            | Apply(hwnd, id) ->
                try
                    match held.TryFind hwnd with
                    | Some h when pinSettling h.pinPending h.lostDesktop -> replan <- true
                    | Some h ->
                        // Moving to a new ID: only over a value of ours.
                        match (if not isListed || sameWindow h.entry <> Same then Unreadable
                               else rewriteGeneration h.id (readId hwnd |> observedId "rewrite" hwnd h)) with
                        | Rewrite -> write hwnd id slotOf.[hwnd] (h.entry, h.original)
                        | ApplicationChanged ->
                            trace hwnd "decline" "application-changed" 0 "rewrite"
                            decline "rewrite-application-changed" hwnd
                            release' "declined" hwnd
                            replan <- true
                        | Unreadable ->
                            trace hwnd "refuse" "unreadable" 0 "rewrite"
                            decline "rewrite-unreadable" hwnd
                            replan <- true
                    | None ->
                        match fresh.TryFind hwnd with
                        | Some (entry, original) when isListed ->
                            if canApply (sameWindow entry = Same) original (readId hwnd) then
                                write hwnd id slotOf.[hwnd] (entry, original)
                            else
                                trace hwnd "keep" "prepare-changed" 0 ""
                                keep "prepare-changed" hwnd entry
                                replan <- true
                        | _ -> ()
                with ex ->
                    trace hwnd "failure" "apply" ex.HResult ""
                    match held.TryFind hwnd, fresh.TryFind hwnd with
                    | Some _, _ -> release' "apply-exception" hwnd
                    | None, Some(entry, original) when isListed -> giveBackTo "apply-exception" hwnd entry original (Some slotOf.[hwnd].desktop)
                    | _ -> ()
                    decline "apply-exception" hwnd
                    replan <- true
            | Restore _ -> ()
        slotState <- retainSharedSlots s.groups shared slotState next
        checks <- checks |> Map.filter (fun key _ -> next.ContainsKey key && desired.ContainsKey key)
        for key in touched do
            checks <- checks.Add(key, { id = next.[key].id; members = []; rebuilt = false; at = lastWrite; seq = seq; retried = false })
        // A slot checks against the windows that carry its ID now.
        checks <- checks |> Map.map (fun key c ->
            let members = desired.[key] |> List.filter (fun h -> held.TryFind h |> Option.exists (fun x -> x.id = c.id))
            { c with members = members })
        if not steps.IsEmpty then
            trace IntPtr.Zero "pass" (if s.enabled then "on" else "off") 0
                (sprintf "slots=%d steps=%d waiting=%b" desired.Count steps.Length waiting)
        saveIfChanged ()
        verify s seq
        saveIfChanged ()

    let private pass (s: Snapshot) seq =
        if s.current <> lastCurrent then lastSwitch <- Some Environment.TickCount
        retryGroups <- s.groups
        let previous = lastProcessed
        lastProcessed <- None
        let monitorOnly = previous = Some s && (s.enabled || (given |> Map.exists (fun _ g -> not g.paused && g.slow))) && not waiting && not replan &&
                          unpinning.IsEmpty && not (retryReady Environment.TickCount s.groups) &&
                          (checks |> Map.forall (fun key c -> not (needsSettledObservation Environment.TickCount c.at (fun () -> verificationHere s.current key.desktop (fun () -> c.members |> List.forall onCurrentDesktop))))) &&
                          (given |> Map.forall (fun h g -> g.paused || (not g.confirmed && not (returnedCheckDue false g.slow Environment.TickCount g.at && dueHere g.desktop && (s.current.IsSome || onCurrentDesktop h))))) &&
                          (held |> Map.forall (fun _ h -> not (pinSettling h.pinPending h.lostDesktop)))
        if monitorOnly then
            // Stable snapshots only inspect windows whose protection we own.
            for KeyValue(hwnd, h) in held do
                if h.entry.pin && sameWindow h.entry = Gone then
                    held <- held.Remove hwnd
                    filed <- filed.Remove hwnd
                    replan <- true
            finishPins ()
            if replan then fullPass s seq
        else fullPass s seq
        lastProcessed <- Some s

    // --------------------------------------------------------------- worker --

    type private Inbox = {
        mutable recover: bool
        mutable release: string option
        mutable snapshot: Snapshot option
        mutable stop: bool
        mutable seq: int64 }
    let private gate = obj()
    let private inbox = { recover = false; release = None; snapshot = None; stop = false; seq = 0L }
    // Created with the first worker: off and untouched, not even this handle exists.
    let private signal = lazy (new AutoResetEvent(false))
    let mutable private worker: Thread = null
    // A recovery file was found at start; it is handled with the first hand-over.
    let mutable private recoveryPending = false

    // While something is held the thread waits for the next hand-over (no
    // timeout); with nothing held, or at exit, it ends.
    let private work () =
        try
            let mutable go = true
            while go do
                let recoverNow, releaseNow, snapshot, seq, final =
                    lock gate (fun () ->
                        let taken = inbox.recover, inbox.release, inbox.snapshot, inbox.seq, inbox.stop
                        inbox.recover <- false
                        inbox.release <- None
                        inbox.snapshot <- None
                        taken)
                if not recoverNow && releaseNow.IsNone && snapshot.IsNone then
                    // Decide under the gate, so a post either finds this
                    // thread still running or starts a new one.
                    let wait = lock gate (fun () ->
                        if inbox.recover || inbox.release.IsSome || inbox.snapshot.IsSome then Some false
                        elif isHolding () && not inbox.stop then Some true
                        else
                            worker <- null
                            None)
                    match wait with
                    | Some true -> signal.Value.WaitOne() |> ignore
                    | Some false -> ()
                    | None -> go <- false
                else
                    try
                        cleanupClosedOff ()
                        if recoverNow then recover (releaseNow = Some "disable" || (snapshot |> Option.exists (fun s -> not s.enabled)))
                        releaseNow |> Option.iter (fun reason ->
                            releaseAll reason
                            finishUnpins final
                            // Disable keeps normal settling and bounded observation.
                            // Only an actual stop uses the final, immediate check.
                            verifyGiven (lazy (readCounts ())) final |> ignore
                            saveIfChanged ())
                        snapshot |> Option.iter (fun s -> pass s seq)
                    with ex -> trace IntPtr.Zero "failure" "worker" ex.HResult ""
                    updateFlags()
        finally
            VirtualDesktopHelper.ReleaseCurrentThread()

    /// Under the gate.
    let private start () =
        if isNull worker then
            // STA: the shell's property store and ITaskbarList expect it.
            let thread = Thread(ThreadStart(work), IsBackground = true, Name = "WindowTabs taskbar buttons")
            thread.SetApartmentState(ApartmentState.STA)
            worker <- thread
            thread.Start()
        else signal.Value.Set() |> ignore

    let private post (fill: Inbox -> unit) =
        lock gate (fun () ->
            fill inbox
            if recoveryPending then
                inbox.recover <- true
                recoveryPending <- false
            inbox.seq <- inbox.seq + 1L
            start ())

    // ------------------------------------------------------------------ API --

    let mutable private lastPosted: Snapshot option = None

    /// Main thread, from the desktop pass. Posts only what changed, or when a
    /// slot waits to settle or a written group waits for its check.
    let publish (snapshot: Snapshot) =
        if lastPosted <> Some snapshot || Volatile.Read(&wantsPass) then
            lastPosted <- Some snapshot
            post (fun inbox -> inbox.snapshot <- Some snapshot)

    let private running () = lock gate (fun () -> not (isNull worker))

    /// Gives every window its own button back (disable). Asynchronous. Also
    /// reaches a first pass still in progress, which holds nothing yet.
    let release reason =
        lastPosted <- None
        if isHolding () || running () then post (fun inbox -> inbox.release <- Some reason; inbox.snapshot <- None)

    /// Exit: give everything back and wait for it, at most `timeoutMs`. What
    /// does not finish stays in the recovery file for the next start.
    let releaseAndWait reason (timeoutMs: int) =
        lastPosted <- None
        lock gate (fun () -> inbox.stop <- true)
        if isHolding () || running () then post (fun inbox -> inbox.release <- Some reason; inbox.snapshot <- None)
        elif signal.IsValueCreated then signal.Value.Set() |> ignore
        let thread = lock gate (fun () -> worker)
        if not (isNull thread) then thread.Join(timeoutMs) |> ignore

    /// Startup, whatever the setting: one file check. When an earlier run left
    /// a list, the first desktop pass hands it over - after the startup
    /// restore, which holds the desktop passes back - whatever the setting.
    let startRecovery () =
        let pending = try File.Exists(recoveryPath) with _ -> false
        if pending then
            lock gate (fun () -> recoveryPending <- true)
            Volatile.Write(&holding, true)

    /// A start in the disabled state runs no startup restore and no desktop
    /// pass: the list is handled at once.
    let recoverNow () =
        lock gate (fun () ->
            if recoveryPending then
                inbox.recover <- true
                inbox.release <- Some "disable"
                recoveryPending <- false
                start ())

    /// Existing main-thread observations continue recovery while tabs are disabled.
    /// With only away windows pending, an unchanged desktop does not wake the STA.
    let observeDisabled (windows: unit -> IntPtr list) =
        if isHolding () then
            let mutable current = Guid.Empty
            let desktop =
                if not VirtualDesktopHelper.IsSupported then Some Guid.Empty
                elif VirtualDesktopHelper.TryReadCurrentDesktopId(&current) then Some current
                else None
            if Volatile.Read(&wantsPass) || (lastPosted |> Option.bind (fun s -> s.current)) <> desktop then
                publish { enabled = false; groups = []; excluded = Set.empty; shared = Set.empty; shown = Set.empty
                          places = windows () |> List.map (fun h -> h, Unknown) |> Map.ofList; current = desktop }
