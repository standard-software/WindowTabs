namespace Bemo
open System
#if DEBUG
#nowarn "0044" // Obsolete thread suspension APIs are used only for freeze diagnostics.
#endif
open System.Windows.Forms
open System.Reflection
open Newtonsoft.Json.Linq
open System.Diagnostics
open System.IO
open System.Net
open System.Text.RegularExpressions
open System.Threading
open Microsoft.Win32

// Watchdog module to detect UI thread freeze and auto-restart
#if DEBUG
// Debug builds only: which processes have a hung top-level window, named in
// the watchdog log when the UI thread stalls (process names only, no titles).
module HungProbe =
    type EnumProc = delegate of nativeint * nativeint -> bool
    [<System.Runtime.InteropServices.DllImport("user32.dll")>]
    extern bool EnumWindows(EnumProc cb, nativeint l)
    [<System.Runtime.InteropServices.DllImport("user32.dll")>]
    extern bool IsHungAppWindow(nativeint h)
    [<System.Runtime.InteropServices.DllImport("user32.dll")>]
    extern bool IsWindowVisible(nativeint h)
    [<System.Runtime.InteropServices.DllImport("user32.dll")>]
    extern uint32 GetWindowThreadProcessId(nativeint h, uint32& pid)
    let describe () =
        let hung = System.Collections.Generic.List<string>()
        let cb = EnumProc(fun h _ ->
            if IsWindowVisible h && IsHungAppWindow h then
                let mutable pid = 0u
                GetWindowThreadProcessId(h, &pid) |> ignore
                let name = try System.Diagnostics.Process.GetProcessById(int pid).ProcessName with _ -> "?"
                hung.Add(sprintf "%s(%d)" name pid)
            true)
        EnumWindows(cb, 0n) |> ignore
        System.GC.KeepAlive cb
        if hung.Count = 0 then "none" else String.Join(", ", hung |> Seq.distinct)
#endif

module Watchdog =
    let mutable private watchdogThread: Thread option = None
    let mutable private stopRequested = false
    let mutable private uiThreadInvoker: Invoker option = None  // Store UI thread's invoker
#if DEBUG
    let mutable private uiNativeThreadId = 0u
    let mutable private uiThread: Thread option = None
    let mutable private switchesLogged = 0
    let mutable private lastTimer = 0L
    let mutable private lastIdle = 0L
    let mutable private heartbeatTimer: System.Windows.Forms.Timer option = None
#endif
    let private freezeTimeout = 10000  // 10 seconds timeout for freeze detection
#if DEBUG
    let private checkInterval = 1000   // Check every second, to catch short stalls too
#else
    let private checkInterval = 5000   // Check every 5 seconds
#endif
    let private requiredConsecutiveFailures = 1  // Restart after 1 timeout (10 seconds unresponsive)

    // Use AutoResetEvent for more reliable signaling
    let private pingResponse = new AutoResetEvent(false)

    // Debug builds only. This began as evidence from real machines that the
    // watchdog had nothing left to catch, but a released copy runs on machines
    // whose logs are never read: it would leave a file in everyone's AppData,
    // written on every single run, that nobody looks at. The one machine where
    // the question is actually asked runs a debug build, and a release build
    // now writes no log of any kind. What guards the settings file there is
    // the refusal to save over it and the recovery from a backup, neither of
    // which needs a log to work.
    // A timeout adds a stack trace and restart progress, then ends in a restart, so there is
    // no repeating state to spam. The 1 MB guard is there only in case that
    // assumption is ever wrong, and it keeps the overflow as watchdog.log.old
    // instead of dropping it.
#if DEBUG
    let private logPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WindowTabs",
            "watchdog.log")
#endif

    // Takes a thunk so that nothing is built outside Debug: an argument would
    // be evaluated before the call, leaving the message assembled and thrown
    // away on every run of a released build.
    let private writeLog (message: string) =
#if DEBUG
        try
            let dir = Path.GetDirectoryName(logPath)
            if not (Directory.Exists(dir)) then Directory.CreateDirectory(dir) |> ignore
            if File.Exists(logPath) && (FileInfo(logPath)).Length > 1_000_000L then
                File.Copy(logPath, logPath + ".old", true)
                File.Delete(logPath)
            File.AppendAllText(
                logPath,
                sprintf "%s [pid %d] %s%s"
                    (DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    (Process.GetCurrentProcess().Id)
                    message
                    Environment.NewLine)
        with _ -> ()
#else
        ignore message
#endif

#if DEBUG
    let private logGate = obj()
#endif
    let private log message =
#if DEBUG
        TraceWriter.write logGate message writeLog
#else
        ignore message
#endif

    // The replacement waits before creating any windows or acquiring the mutex.
    let awaitRestartParent (args: string[]) =
        match RestartRequest.parse args with
        | Some(parentId, started) ->
            try
                use parent = Process.GetProcessById(parentId)
                try
                    if not parent.HasExited && RestartRequest.matches started (parent.StartTime.ToUniversalTime().Ticks) then
                        log (fun () -> "restart: waiting for previous process")
                        if not (parent.WaitForExit(5000)) then
                            log (fun () -> "restart: terminating previous process after bounded wait")
                            parent.Kill()
                        parent.WaitForExit()
                    log (fun () -> "restart: previous process exited or identity changed")
                with ex when parent.HasExited ->
                    log (fun () -> "restart: previous process exited during hand-over")
            with :? ArgumentException ->
                log (fun () -> "restart: previous process already exited")
            true
        | None -> false

    let respondToPing() =
        pingResponse.Set() |> ignore

#if DEBUG
    // Do not suspend a thread again if an earlier stack walk is still pending.
    let private activeCaptures = System.Collections.Concurrent.ConcurrentDictionary<int, byte>()

    let private logAllStacks sample waitMilliseconds =
        let elapsed = Stopwatch.StartNew()
        let watchdogId = Thread.CurrentThread.ManagedThreadId
        // Enumeration, worker startup and logging also belong to the bounded
        // diagnostic worker, never to the watchdog's restart path.
        let capture = new Thread(ThreadStart(fun () ->
            try
                let workers =
                    InvokerThreads.snapshot()
                    |> Array.choose (fun (target, id, name) ->
                        if id = watchdogId then None
                        elif not (activeCaptures.TryAdd(id, 0uy)) then
                            log (fun () -> sprintf "All stacks sample %d: thread id=%d name=%s capture still pending" sample id name)
                            None
                        else
                            let worker = new Thread(ThreadStart(fun () ->
                                try
                                    try
                                        let mutable suspended = false
                                        let stack =
                                            try
                                                target.Suspend()
                                                suspended <- true
                                                new StackTrace(target, false)
                                            finally
                                                if suspended then target.Resume()
                                        // Resume before formatting or touching the log file.
                                        log (fun () ->
                                            sprintf "All stacks sample %d: thread id=%d name=%s%s%s"
                                                sample id (if isNull name then "<unnamed>" else name)
                                                Environment.NewLine (stack.ToString()))
                                    with ex ->
                                        log (fun () -> sprintf "All stacks sample %d: thread id=%d name=%s capture failed: %s" sample id name (ex.GetType().Name))
                                finally
                                    let mutable removed = 0uy
                                    activeCaptures.TryRemove(id, &removed) |> ignore))
                            worker.IsBackground <- true
                            worker.Name <- sprintf "WindowTabs Stack Capture %d" id
                            try
                                worker.Start()
                                Some worker
                            with ex ->
                                let mutable removed = 0uy
                                activeCaptures.TryRemove(id, &removed) |> ignore
                                log (fun () -> sprintf "All stacks sample %d: thread id=%d name=%s worker failed: %s" sample id name (ex.GetType().Name))
                                None)
                // Independent workers let other threads finish even when one
                // suspension or stack walk stalls inside the runtime.
                for worker in workers do worker.Join()
            with ex ->
                log (fun () -> sprintf "All stacks sample %d failed: %s" sample (ex.GetType().Name))))
        capture.IsBackground <- true
        capture.Name <- sprintf "WindowTabs All Stacks %d" sample
        capture.Start()
        capture.Join(max 0 (waitMilliseconds - int elapsed.ElapsedMilliseconds)) |> ignore

    let private logUiStack() =
        try
            match uiThread with
            | None -> log (fun () -> "UI stack unavailable - UI thread was not captured")
            | Some target ->
                // Suspension or stack walking can itself stall. Keep the watchdog
                // independent and cap the total diagnostic wait at five seconds.
                let capture = new Thread(ThreadStart(fun () ->
                    try
                        if activeCaptures.TryAdd(target.ManagedThreadId, 0uy) then
                            try
                                let mutable suspended = false
                                let stack =
                                    try
                                        target.Suspend()
                                        suspended <- true
                                        new StackTrace(target, false)
                                    finally
                                        if suspended then target.Resume()
                                // Format and write only after resuming the target.
                                log (fun () -> sprintf "UI managed stack:%s%s" Environment.NewLine (stack.ToString()))
                            finally
                                let mutable removed = 0uy
                                activeCaptures.TryRemove(target.ManagedThreadId, &removed) |> ignore
                    with ex ->
                        log (fun () -> sprintf "UI stack capture failed: %s" (ex.GetType().Name))))
                capture.IsBackground <- true
                capture.Name <- "WindowTabs Watchdog Stack Capture"
                capture.Start()
                if not (capture.Join(5000)) then
                    log (fun () -> "UI stack capture timed out after 5000 ms; continuing restart")
        with ex ->
            log (fun () -> sprintf "UI stack diagnostic failed: %s" (ex.GetType().Name))
#endif

    let private trySaveAndRestart() =
        // Written before anything else: the process is about to be replaced,
        // and a restart nobody can account for afterwards is worse than the
        // freeze it was meant to cure.
        log (fun () -> "FIRED - the UI thread stopped answering; saving the tab groups and restarting")
        try
            // Try to save tab groups before restart
            let saveComplete = new ManualResetEvent(false)
            try
                log (fun () -> "restart: save requested (waiting up to 2000 ms)")
                match uiThreadInvoker with
                | Some invoker ->
                    invoker.asyncInvoke(fun () ->
                        try
                            log (fun () -> "restart: save started on UI thread")
                            Services.program.saveTabGroupsBeforeExit()
                            log (fun () -> "restart: save completed")
                        with ex ->
                            log (fun () -> sprintf "restart: save failed: %s" (ex.GetType().Name))
                        saveComplete.Set() |> ignore
                    )
                | None -> log (fun () -> "restart: save unavailable - no UI invoker")
                // Wait max 2 seconds for save
                if not (saveComplete.WaitOne(2000)) then
                    log (fun () -> "restart: save wait timed out after 2000 ms")
            with ex ->
                log (fun () -> sprintf "restart: save dispatch failed: %s" (ex.GetType().Name))

            // Launch directly so hand-over depends on process exit, not a delay.
            let exePath = Assembly.GetExecutingAssembly().Location
            use current = Process.GetCurrentProcess()
            let startInfo = ProcessStartInfo(exePath)
            startInfo.UseShellExecute <- false
            startInfo.WorkingDirectory <- Path.GetDirectoryName(exePath)
            startInfo.Arguments <- sprintf "--watchdog-restart %d %d" current.Id (current.StartTime.ToUniversalTime().Ticks)
#if DEBUG
            startInfo.EnvironmentVariables.Remove("WINDOWTABS_DEBUG_FREEZE_UI_AFTER_MS")
            startInfo.EnvironmentVariables.Remove("WINDOWTABS_DEBUG_RESTART_EXIT_DELAY_MS")
#endif
            use restartProcess = Process.Start(startInfo)
            if isNull restartProcess then failwith "Restart process was not created."
            log (fun () -> sprintf "restart: replacement started (pid %d)" restartProcess.Id)
            ForceExitState.isForceExiting <- true
            log (fun () -> "restart: exiting current process with code 0")
#if DEBUG
            match Int32.TryParse(Environment.GetEnvironmentVariable("WINDOWTABS_DEBUG_RESTART_EXIT_DELAY_MS")) with
            | true, ms when ms > 0 ->
                log (fun () -> sprintf "debug restart exit delay active: %d ms" ms)
                Thread.Sleep(ms)
            | _ -> ()
#endif
            Environment.Exit(0)
        with ex ->
            log (fun () -> sprintf "restart: failed: %s; exiting current process with code 1" (ex.GetType().Name))
            Environment.Exit(1)

    let private watchdogLoop() =
        // Wait before starting monitoring to allow app to initialize
        Thread.Sleep(10000)

        let mutable consecutiveFailures = 0

        while not stopRequested do
            try
                // Send ping to UI thread using the stored UI thread invoker
                match uiThreadInvoker with
                | Some invoker ->
                    try
#if DEBUG
                        let posted = Stopwatch.GetTimestamp()
                        invoker.asyncInvoke(fun () ->
                            let answered = Stopwatch.GetTimestamp()
                            respondToPing()
                            let ms = float (answered - posted) * 1000.0 / float Stopwatch.Frequency
                            if ms >= 100.0 then
                                InputStallTrace.completed InputStallTrace.Kind.PingAck "ping" ms answered)
#else
                        invoker.asyncInvoke(fun () -> respondToPing())
#endif
                    with _ -> ()
                | None -> ()

                // Wait for response with timeout
#if DEBUG
                // A stall shorter than the restart timeout is felt too: after
                // 1.5 s, log the stacks and any hung applications, then keep
                // waiting for the full timeout as before.
                let stallWatch = Stopwatch.StartNew()
                let quick = pingResponse.WaitOne(1500)
                let responded =
                    if quick then true else
                    InputStallTrace.context InputStallTrace.Kind.Watchdog IntPtr.Zero (float stallWatch.ElapsedMilliseconds)
                    log (fun () -> Bemo.Win32.UiStallDiagnostics.PumpSnapshot(uiNativeThreadId))
                    let age stamp = (Stopwatch.GetTimestamp() - stamp) * 1000L / Stopwatch.Frequency
                    log (fun () -> sprintf "[PumpProbe] timer-age-ms=%d idle-age-ms=%d" (age (Interlocked.Read(&lastTimer))) (age (Interlocked.Read(&lastIdle))))
                    log (fun () -> "STALL: UI thread has not answered for 1500 ms; hung windows: " + (try HungProbe.describe() with ex -> ex.GetType().Name))
                    Bemo.Win32.UiStallDiagnostics.Capture(uiNativeThreadId, Bemo.Win32.UiStallDiagnostics.LogLine(fun line -> log (fun () -> line)))
                    logUiStack()
                    logAllStacks 0 500
                    let late = pingResponse.WaitOne(max 0 (freezeTimeout - int stallWatch.ElapsedMilliseconds))
                    if late then log (fun () -> sprintf "STALL ended: UI thread answered after %d ms" stallWatch.ElapsedMilliseconds)
                    late
#else
                let responded = pingResponse.WaitOne(freezeTimeout)
#endif

                if responded then
                    // UI thread responded, reset failure count
                    consecutiveFailures <- 0
                else
                    // UI thread did not respond
                    consecutiveFailures <- consecutiveFailures + 1
                    log (fun () -> sprintf "ping timed out after %d ms (%d of %d before a restart)"
                                        freezeTimeout consecutiveFailures requiredConsecutiveFailures)
#if DEBUG
                    log (fun () -> Bemo.Win32.UiStallDiagnostics.PumpSnapshot(uiNativeThreadId))
                    let age stamp = (Stopwatch.GetTimestamp() - stamp) * 1000L / Stopwatch.Frequency
                    log (fun () -> sprintf "[PumpProbe] timeout timer-age-ms=%d idle-age-ms=%d" (age (Interlocked.Read(&lastTimer))) (age (Interlocked.Read(&lastIdle))))
                    logUiStack()
                    // Two 500 ms capture allowances total at most one second.
                    // Reserve the final allowance inside the five-second window
                    // so the follow-up completes before restart, not next tick.
                    let diagnostics = Stopwatch.StartNew()
                    logAllStacks 1 500
                    let delay = max 0 (4500 - int diagnostics.ElapsedMilliseconds)
                    if delay > 0 then Thread.Sleep(delay)
                    logAllStacks 2 (max 0 (min 500 (5000 - int diagnostics.ElapsedMilliseconds)))
#endif

                    if consecutiveFailures >= requiredConsecutiveFailures && not stopRequested && not ForceExitState.isForceExiting then
                        // UI thread is frozen (confirmed by multiple consecutive failures), force restart
                        trySaveAndRestart()

                // Wait before next check
                Thread.Sleep(checkInterval)
            with _ ->
                Thread.Sleep(checkInterval)

    let start() =
#if DEBUG
        if Interlocked.Exchange(&switchesLogged, 1) = 0 then
            log (fun () ->
                sprintf "EXPERIMENT NO_CAPTION_QUERY=%d SYNC_FOLLOWERS=%d NO_MOUSE_HOOK=%d"
                    (if StallExperiment.noCaptionQuery then 1 else 0)
                    (if StallExperiment.syncFollowers then 1 else 0)
                    (if StallExperiment.noMouseHook then 1 else 0))
#endif
        // Don't start watchdog when debugger is attached (prevents false positives during debugging)
        if Debugger.IsAttached then
            log (fun () -> "not armed - a debugger is attached")
        elif watchdogThread.IsNone then
            // Capture UI thread's invoker (must be called from UI thread)
            uiThreadInvoker <- Some(InvokerService.invoker)
#if DEBUG
            Bemo.Win32.UiStallDiagnostics.InstallDisplayProbe(Bemo.Win32.UiStallDiagnostics.LogLine(fun line -> log (fun () -> line)))
            let beat = new System.Windows.Forms.Timer(Interval = 250)
            lastTimer <- Stopwatch.GetTimestamp()
            lastIdle <- lastTimer
            beat.Tick.Add(fun _ -> Interlocked.Exchange(&lastTimer, Stopwatch.GetTimestamp()) |> ignore)
            Application.Idle.Add(fun _ -> Interlocked.Exchange(&lastIdle, Stopwatch.GetTimestamp()) |> ignore)
            beat.Start()
            heartbeatTimer <- Some beat
            match Int32.TryParse(Environment.GetEnvironmentVariable("WINDOWTABS_DEBUG_FREEZE_UI_AFTER_MS")) with
            | true, ms when ms > 0 ->
                log (fun () -> sprintf "debug freeze hook active: after %d ms" ms)
                let freeze = new System.Windows.Forms.Timer(Interval = ms)
                freeze.Tick.Add(fun _ ->
                    freeze.Stop()
                    log (fun () -> "debug freeze hook: blocking UI thread")
                    Thread.Sleep(Timeout.Infinite))
                freeze.Start()
            | _ -> ()
            uiThread <- Some Thread.CurrentThread
            uiNativeThreadId <- Bemo.Win32.UiStallDiagnostics.GetCurrentThreadId()
#endif
            stopRequested <- false
            let thread = new Thread(ThreadStart(watchdogLoop))
            thread.IsBackground <- true
            thread.Name <- "WindowTabs Watchdog"
            thread.Start()
            watchdogThread <- Some(thread)
            log (fun () -> sprintf "armed - monitoring starts in 10 s, then a ping every %d ms with a %d ms timeout"
                                checkInterval freezeTimeout)

    let stop() =
        if watchdogThread.IsSome && not stopRequested then log (fun () -> "stopped")
        stopRequested <- true
        pingResponse.Set() |> ignore  // Unblock any waiting

// Auto-update: checks the GitHub releases of this repository and installs a
// newer release on request. The MSI install is detected by comparing the exe
// folder with the InstallPath the installer writes to HKCU; anything else
// (the portable zip, dev builds) uses the zip overwrite flow.
module UpdateChecker =
    let private releaseApiUrl = "https://api.github.com/repos/standard-software/WindowTabs/releases/latest"

    type ReleaseInfo = {
        tag: string
        msiUrl: string option
        zipUrl: string option
    }

    // Version strings look like ss_2026.07.10, ss_jp_2026.03.25 or the dev
    // form ss_2026.07.10_next3 — compare by the embedded date only.
    let versionDate (v: string) =
        let m = Regex.Match(v, @"(\d{4})\.(\d{2})\.(\d{2})")
        if m.Success then
            try Some(DateTime(int m.Groups.[1].Value, int m.Groups.[2].Value, int m.Groups.[3].Value)) with _ -> None
        else None

    let isNewer (currentVersion: string) (tag: string) =
        match versionDate currentVersion, versionDate tag with
        | Some(cur), Some(latest) -> latest > cur
        | _ -> false

    let private newWebClient() =
        // GitHub requires TLS 1.2+, which .NET Framework does not enable by default
        ServicePointManager.SecurityProtocol <- ServicePointManager.SecurityProtocol ||| SecurityProtocolType.Tls12
        let wc = new WebClient()
        wc.Headers.Add("User-Agent", "WindowTabs")
        wc

    let waitForResponse timeoutMs (response: System.Threading.Tasks.Task<string>) =
        if not (response.Wait(timeoutMs: int)) then
            raise (TimeoutException("Update check response timed out."))
        response.Result

    let fetchLatestRelease() =
        use wc = newWebClient()
        // Bound the entire response, including a stalled or trickling body.
        // Waiting happens on the worker thread, never on the UI thread.
        let response = wc.DownloadStringTaskAsync(Uri(releaseApiUrl))
        let body =
            try waitForResponse 15000 response
            with :? TimeoutException ->
                wc.CancelAsync()
                reraise()
        let json = JObject.Parse(body)
        let assetUrl (name: string) =
            match json.["assets"] with
            | :? JArray as assets ->
                assets
                |> Seq.tryPick (fun a ->
                    let o = a :?> JObject
                    if String.Equals(o.["name"].ToString(), name, StringComparison.OrdinalIgnoreCase)
                    then Some(o.["browser_download_url"].ToString())
                    else None)
            | _ -> None
        {
            tag = json.["tag_name"].ToString()
            msiUrl = assetUrl "WtSetup.msi"
            zipUrl = assetUrl "WindowTabs.zip"
        }

    let appDir() = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)

    let isMsiInstall() =
        try
            use key = Registry.CurrentUser.OpenSubKey(@"Software\WindowTabs")
            match key with
            | null -> false
            | key ->
                match key.GetValue("InstallPath") with
                | :? string as installPath when installPath <> "" ->
                    let norm (p: string) = p.Trim().TrimEnd('\\').ToLowerInvariant()
                    norm installPath = norm (appDir())
                | _ -> false
        with _ -> false

    let download (url: string) =
        let dest = Path.Combine(Path.GetTempPath(), "WindowTabsUpdate_" + Path.GetFileName(Uri(url).LocalPath))
        use wc = newWebClient()
        wc.DownloadFile(url, dest)
        dest

    // The installer closes the running WindowTabs itself (util:CloseApplication)
    // and offers to relaunch it when done.
    let installMsi (msiPath: string) =
        Process.Start("msiexec.exe", sprintf "/i \"%s\"" msiPath) |> ignore

    // Overwrite-in-place update for the portable zip: a detached PowerShell
    // waits for this process to exit, extracts the zip over the app folder
    // and restarts WindowTabs.
    let installZipAndExit (zipPath: string) =
        let extractDir = zipPath + ".extracted"
        let exePath = Path.Combine(appDir(), "WindowTabs.exe")
        let pid = Process.GetCurrentProcess().Id
        let command =
            sprintf "Wait-Process -Id %d -ErrorAction SilentlyContinue; Start-Sleep -Seconds 1; Expand-Archive -LiteralPath '%s' -DestinationPath '%s' -Force; Copy-Item -Path '%s\\*' -Destination '%s' -Recurse -Force; Start-Process -FilePath '%s'"
                pid zipPath extractDir extractDir (appDir()) exePath
        let psi = ProcessStartInfo()
        psi.FileName <- "powershell.exe"
        psi.Arguments <- sprintf "-NoProfile -ExecutionPolicy Bypass -Command \"%s\"" command
        psi.WindowStyle <- ProcessWindowStyle.Hidden
        psi.CreateNoWindow <- true
        Process.Start(psi) |> ignore
        Services.program.shutdown()

type NotifyIconPlugin() as this =
    let Cell = CellScope()

    member this.icon = Cell.cacheProp this <| fun() ->
        let notifyIcon = new NotifyIcon()
        notifyIcon.Visible <- true
        notifyIcon.Text <- "WindowTabs version " + Services.program.version
        notifyIcon.Icon <- Services.openIcon("Bemo.ico")
        let contextMenu = new ContextMenu()

        // Apply dark mode setting and update menu texts when menu is about to be shown
        contextMenu.Popup.Add <| fun _ ->
            let dialogBusy = DialogState.isBusy()
            let darkModeEnabled =
                try
                    let json = Services.settings.root
                    match json.getBool("EnableDarkMode") with
                    | Some(value) -> value
                    | None -> false
                with | _ -> false
            DarkMode.setDarkModeForMenus(darkModeEnabled)

            // Update all menu item texts by checking their Tags
            for i in 0 .. contextMenu.MenuItems.Count - 1 do
                let menuItem = contextMenu.MenuItems.[i]
                match menuItem.Tag with
                | :? string as tag ->
                    match tag with
                    | "Settings" -> menuItem.Text <- Localization.getString("Settings")
                    | "CheckForUpdates" -> menuItem.Text <- Localization.getString("CheckForUpdates")
                    | "Language" ->
                        menuItem.Text <- Localization.getString("Language")
                        // Update language menu checkmarks using current language from Localization module
                        let currentLanguage = Localization.currentLanguage

                        for j in 0 .. menuItem.MenuItems.Count - 1 do
                            let langItem = menuItem.MenuItems.[j]
                            // Get language name from Tag (stored without .json extension)
                            match langItem.Tag with
                            | :? string as langName ->
                                langItem.Checked <- (currentLanguage = langName)
                                langItem.Enabled <- not dialogBusy && not (currentLanguage = langName)
                            | _ -> ()
                    | "Disable" ->
                        menuItem.Text <- Localization.getString("Disable")
                        // Update checkbox state
                        menuItem.Checked <- Services.program.isDisabled
                    | "RestartWindowTabs" -> menuItem.Text <- Localization.getString("RestartWindowTabs")
                    | "CloseWindowTabs" -> menuItem.Text <- Localization.getString("CloseWindowTabs")
                    | _ -> ()
                | _ -> ()

            // Block all tray commands while a dialog session is active.
            for i in 0 .. contextMenu.MenuItems.Count - 1 do
                let menuItem = contextMenu.MenuItems.[i]
                match menuItem.Tag with
                | :? string as tag ->
                    menuItem.Enabled <- DialogState.canUseTrayCommand tag Services.program.isDisabled
                | _ -> ()

        notifyIcon.ContextMenu <- contextMenu
        notifyIcon.DoubleClick.Add <| fun _ ->
            if not (DialogState.isBusy()) then Services.managerView.show()
        notifyIcon

    member this.contextMenuItems = this.icon.ContextMenu.MenuItems

    member this.addItem(text, handler) =
        this.contextMenuItems.Add(text, EventHandler(fun obj (e:EventArgs) -> handler())) |> ignore

    member this.onNewVersion() =
        this.icon.ShowBalloonTip(
            1000,
            "A new version is available.",
            "Please visit windowtabs.com to download the latest version.",
            ToolTipIcon.Info
        )

    // Check the latest GitHub release and report the result. Only invoked
    // from the tray-menu item — WindowTabs never checks on its own.
    member this.checkForUpdates() =
        match DialogState.tryAcquire() with
        | None -> ()
        | Some session ->
            try
                let show message buttons defaultButton =
                    AppDialog.showReserved "WindowTabs" message buttons defaultButton
                let invoker = InvokerService.invoker
                let currentVersion = Services.program.version
                ThreadHelper.queueBackground <| fun() ->
                    let release = try Some(UpdateChecker.fetchLatestRelease()) with _ -> None
                    invoker.asyncInvoke <| fun() ->
                        let mutable install = None
                        try
                            match release with
                            | None ->
                                show (Localization.getString("UpdateCheckFailed")) AppDialog.OkOnly AppDialog.DefaultOk |> ignore
                            | Some(release) ->
                                if UpdateChecker.isNewer currentVersion release.tag then
                                    let message = String.Format(Localization.getString("UpdateAvailableFormat"), release.tag)
                                    if show message AppDialog.OkCancel AppDialog.DefaultCancel = DialogResult.OK then
                                        install <- Some release
                                else
                                    show (String.Format(Localization.getString("UpdateUpToDateFormat"), currentVersion)) AppDialog.OkOnly AppDialog.DefaultOk |> ignore
                        finally
                            session.Dispose()
                        install |> Option.iter this.startUpdate
            with _ ->
                session.Dispose()
                reraise()

    member this.startUpdate(release: UpdateChecker.ReleaseInfo) =
        let invoker = InvokerService.invoker
        let useMsi = UpdateChecker.isMsiInstall()
        match (if useMsi then release.msiUrl else release.zipUrl) with
        | None ->
            AppDialog.info "WindowTabs" (Localization.getString("UpdateDownloadFailed"))
        | Some(url) ->
            this.icon.ShowBalloonTip(1000, "WindowTabs", Localization.getString("UpdateDownloading"), ToolTipIcon.Info)
            ThreadHelper.queueBackground <| fun() ->
                let downloaded = try Some(UpdateChecker.download url) with _ -> None
                invoker.asyncInvoke <| fun() ->
                    match downloaded with
                    | None ->
                        AppDialog.info "WindowTabs" (Localization.getString("UpdateDownloadFailed"))
                    | Some(path) ->
                        if useMsi then UpdateChecker.installMsi path
                        else UpdateChecker.installZipAndExit path

    // Restart application using normal shutdown
    member this.restartApplication() =
        let exePath = Assembly.GetExecutingAssembly().Location
        // Start new process with a delay using cmd. ping rather than timeout:
        // see trySaveAndRestart.
        let startInfo = ProcessStartInfo()
        startInfo.FileName <- "cmd.exe"
        startInfo.Arguments <- sprintf "/c ping -n 4 127.0.0.1 >nul && start \"\" \"%s\"" exePath
        startInfo.WindowStyle <- ProcessWindowStyle.Hidden
        startInfo.CreateNoWindow <- true
        try
            Process.Start(startInfo) |> ignore
            Services.program.shutdown()
        with
        | ex -> AppDialog.info "Error" ex.Message

    // Returns list of (displayName, fileName) tuples (supports JSONC format with comments).
    //
    // The list says which languages the menu shows. The shipped one is under
    // <exe>\Settings\Language; a FileList.json under
    // %APPDATA%\WindowTabs\Settings\Language replaces it whole, which is how a
    // user shows only the languages they want as much as how they add one.
    // An entry whose file exists in neither place is left out: a menu item
    // that does nothing is worse than no item.
    member this.getLanguageListFromFileList() : (string * string) list =
        try
            match UserOverrides.loadArray UserOverrides.languageDir "FileList.json" with
            | Some(arr) ->
                arr
                |> Seq.choose (fun t ->
                    let obj = t :?> JObject
                    let name = obj.["name"].ToString()
                    let file = obj.["fileName"].ToString()
                    if UserOverrides.exists UserOverrides.languageDir file
                    then Some(name, file.Replace(".json", ""))
                    else None)
                |> Seq.toList
            | None ->
                // No FileList.json anywhere - return empty list
                []
        with
        | _ -> []

    member this.createLanguageMenu() =
        // Load language list from FileList.json
        let languages = this.getLanguageListFromFileList()

        // If language list is empty, return None (hide Language menu)
        if languages.IsEmpty then
            None
        else
            let languageMenu = new MenuItem(Localization.getString("Language"))
            let currentLanguage = Localization.currentLanguage

            for (displayName, fileName) in languages do
                let langItem = new MenuItem(displayName)
                langItem.Checked <- (currentLanguage = fileName)
                langItem.Enabled <- not (currentLanguage = fileName)
                langItem.Tag <- box(fileName)  // Store fileName (without .json) in Tag for language switching
                langItem.Click.Add <| fun _ -> this.changeLanguage(displayName, fileName)
                languageMenu.MenuItems.Add(langItem) |> ignore

            Some(languageMenu)

    member this.changeLanguage(displayName, fileName) =
        match DialogState.tryAcquire() with
        | None -> ()
        | Some session ->
            use lifetime = session
            let show title message =
                AppDialog.showReservedEnglish title message |> ignore
            try
                let json = Services.settings.root
                json.["language"] <- JToken.FromObject(fileName)
                Services.settings.root <- json
#if DEBUG
                let languageClock = System.Diagnostics.Stopwatch.StartNew()
#endif
                Localization.setLanguage(fileName)
#if DEBUG
                DesktopManagerFormState.log (sprintf "language load and notifications ms=%.1f" languageClock.Elapsed.TotalMilliseconds)
#endif
                Services.managerView.preparePresentation()
                // Keep this message in English so a user can recover after
                // accidentally choosing a language they cannot read.
                show "Language Change" (sprintf "Language has been changed to %s." displayName)
            with
            | ex -> show "Error" ex.Message

    interface IPlugin with
        member this.init() =
            AppDialog.initialize()
            let notifyIcon = this.icon
            let contextMenu = notifyIcon.ContextMenu

            // Create menu items
            // Non-clickable caption showing the running version
            let versionMenuItem = new MenuItem("version " + Services.program.version)
            versionMenuItem.Enabled <- false
            this.contextMenuItems.Add(versionMenuItem) |> ignore

            this.contextMenuItems.Add("-") |> ignore

            let settingsMenuItem = new MenuItem(Localization.getString("Settings"))
            settingsMenuItem.Click.Add <| fun _ -> Services.managerView.show()
            settingsMenuItem.Tag <- box("Settings")
            // Bold: matches the tray icon double-click default action
            settingsMenuItem.DefaultItem <- true
            this.contextMenuItems.Add(settingsMenuItem) |> ignore

            // Only add Language menu if FileList.json exists and is not empty
            match this.createLanguageMenu() with
            | Some(languageMenu) ->
                languageMenu.Tag <- box("Language")
                this.contextMenuItems.Add(languageMenu) |> ignore
            | None -> ()

            //this.addItem(Localization.getString("Feedback"), Forms.openFeedback) // 404 Not Found.
            this.contextMenuItems.Add("-") |> ignore

            let disableMenuItem = new MenuItem(Localization.getString("Disable"))
            disableMenuItem.Click.Add <| fun _ ->
                if not (DialogState.isBusy()) then
                    let newState = not Services.program.isDisabled
                    Services.program.setDisabled(newState)
            disableMenuItem.Tag <- box("Disable")
            this.contextMenuItems.Add(disableMenuItem) |> ignore

            this.contextMenuItems.Add("-") |> ignore

            let updateMenuItem = new MenuItem(Localization.getString("CheckForUpdates"))
            updateMenuItem.Click.Add <| fun _ -> this.checkForUpdates()
            updateMenuItem.Tag <- box("CheckForUpdates")
            this.contextMenuItems.Add(updateMenuItem) |> ignore

            // Both ask first: a slip on the tray menu would otherwise take
            // every tab strip down with it. Cancel is the default.
            let restartMenuItem = new MenuItem(Localization.getString("RestartWindowTabs"))
            restartMenuItem.Click.Add <| fun _ ->
                if AppDialog.confirm "WindowTabs" (Localization.getString("RestartConfirm")) then
                    this.restartApplication()
            restartMenuItem.Tag <- box("RestartWindowTabs")
            this.contextMenuItems.Add(restartMenuItem) |> ignore

            let closeMenuItem = new MenuItem(Localization.getString("CloseWindowTabs"))
            closeMenuItem.Click.Add <| fun _ ->
                if AppDialog.confirm "WindowTabs" (Localization.getString("CloseConfirm")) then
                    Services.program.shutdown()
            closeMenuItem.Tag <- box("CloseWindowTabs")
            this.contextMenuItems.Add(closeMenuItem) |> ignore

            Services.program.newVersion.Add this.onNewVersion

            // Start watchdog to detect UI freeze and auto-restart
            if not (SettingsTiming.enabled()) then Watchdog.start()

    interface IDisposable with
        member this.Dispose() =
            Watchdog.stop()
            this.icon.Dispose()
