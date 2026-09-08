namespace Bemo
open System
open System.Runtime.InteropServices
open System.Threading
open System.Windows.Forms

type InputManagerPlugin(msgSet:Set2<Int32>) as this =
    // Only bounded-frequency button/wheel messages belong here. If a future
    // feature adds WM_MOUSEMOVE, it must coalesce events before queueing them.
    let hookProcDelegate = HOOKPROC(this.llHook)
    let mainInvoker = InvokerService.invoker
    let hookStarted = new ManualResetEvent(false)
    let hookStopped = new ManualResetEvent(false)
    let mutable hookInvoker : Invoker option = None
    let mutable hookId = IntPtr.Zero
    let mutable disposed = false

    member this.llHook nCode (wParam:IntPtr) lParam = 
        let msg = wParam.ToInt32()
        if nCode >= 0 && msgSet.contains(msg) then
            let hookStruct = unbox<MSLLHOOKSTRUCT>(Marshal.PtrToStructure(lParam, typeof<MSLLHOOKSTRUCT>))
            let pt = hookStruct.pt.Pt
            let data = hookStruct.mouseData.IntPtr
            let foregroundHwnd = WinUserApi.GetForegroundWindow()
            let capturedAt = DateTime.UtcNow
            // A low-level hook must return immediately. Resolving the current
            // group through Services is a synchronous main-thread call, so
            // queue the complete dispatch there instead of blocking this hook
            // thread while the settings dialog or shutdown work is running.
            mainInvoker.asyncInvoke <| fun() ->
                // Do not replay wheel input after a busy interval, or deliver
                // it to whichever group happened to become active meanwhile.
                if DateTime.UtcNow - capturedAt <= TimeSpan.FromMilliseconds(500.0) &&
                   WinUserApi.GetForegroundWindow() = foregroundHwnd then
                    Services.desktop.foregroundGroup.iter <| fun group ->
                        let groupInfo = group.cast<GroupInfo>()
                        groupInfo.invokeGroup <| fun() ->
                            groupInfo.group.postMouseLL(msg, pt, data)

        WinUserApi.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam)

    member this.registerMouseLLHook() =
        let runHookThread() =
            try
                hookInvoker <- Some(InvokerService.invoker)
                hookId <- WinUserApi.SetWindowsHookEx(WindowHookTypes.WH_MOUSE_LL, hookProcDelegate, IntPtr.Zero, 0)
            finally
                hookStarted.Set().ignore

            try Application.Run()
            finally
                if hookId <> IntPtr.Zero then
                    WinUserApi.UnhookWindowsHookEx(hookId).ignore
                    hookId <- IntPtr.Zero
                hookStopped.Set().ignore

        let thread = Thread(ThreadStart(runHookThread))
        thread.Name <- "WindowTabs mouse hook"
        thread.IsBackground <- true
        thread.SetApartmentState(ApartmentState.STA)
        thread.Start()
        let started = hookStarted.WaitOne(5000)
        if not started || hookId = IntPtr.Zero then
            // InputManager is an enhancement, not a reason to keep the whole
            // application from starting when Windows refuses a hook.
            System.Diagnostics.Trace.TraceWarning("Low-level mouse hook was not installed")

    // No keyboard hook. There used to be a WH_KEYBOARD_LL hook here that fed
    // every keystroke on the machine to the foreground group, for the
    // Ctrl+number plugin alone; that plugin is gone (the number keys are
    // RegisterHotKey hot keys now, see Program.syncHotKeys), and a hook with
    // no listener would still route all typing through this process.

    interface IPlugin with
        member x.init() =
            this.registerMouseLLHook()
    interface IDisposable with
        member x.Dispose() =
            if not disposed then
                disposed <- true
                hookInvoker.iter <| fun invoker ->
                    invoker.asyncInvoke <| fun() -> Application.ExitThread()
                hookStopped.WaitOne(2000).ignore
                // Keep the process-lifetime events alive if either wait timed
                // out. The hook thread may still signal them while shutting down.
