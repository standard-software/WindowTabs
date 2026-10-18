namespace Bemo
open System
open System.IO
open System.Threading
open System.Threading.Tasks
open System.Windows.Forms

// Shutdown diagnostics contain only fixed stages, type/method names and thread IDs.
module ShutdownTrace =
#if DEBUG
    let private startupClock = System.Diagnostics.Stopwatch.StartNew()
    let remainingDelay milliseconds = max 1 (milliseconds - int (min (int64 Int32.MaxValue) startupClock.ElapsedMilliseconds))
    let private gate = obj()
    let private writing = new ThreadLocal<bool>(fun () -> false)
    let mutable private stopping = 0
    let write stage =
        if not writing.Value then
            writing.Value <- true
            try
                try
                    lock gate (fun () ->
                        File.AppendAllText(Path.Combine(Path.GetTempPath(), "WindowTabs_shutdown_trace.txt"),
                            sprintf "thread=%d %s\r\n" Thread.CurrentThread.ManagedThreadId stage))
                with _ -> ()
            finally writing.Value <- false
    let beginShutdown() =
        Interlocked.Exchange(&stopping, 1) |> ignore
        write "shutdown begin"
    let install() =
        write "process start"
        AppDomain.CurrentDomain.FirstChanceException.Add(fun args ->
            if Volatile.Read(&stopping) <> 0 && not writing.Value then
                let target = args.Exception.TargetSite
                let declaringType =
                    if isNull target || isNull target.DeclaringType then "unknown"
                    else target.DeclaringType.FullName
                let methodName = if isNull target then "unknown" else target.Name
                write ("first-chance " + args.Exception.GetType().FullName +
                    "; target=" + declaringType + "." + methodName))
        Application.ApplicationExit.Add(fun _ -> write "ApplicationExit")
#else
    let inline write (_stage: string) = ()
    let inline beginShutdown() = ()
    let inline install() = ()
#endif

// Independent of every message loop; intentionally remains armed through CLR cleanup.
module ShutdownDeadline =
    let private clock = System.Diagnostics.Stopwatch.StartNew()
    let mutable private deadline = Int64.MaxValue
    let mutable private stage = "shutdown requested"
    let setStage value = Volatile.Write(&stage, value)

    let private killImmediately() =
        // Never fall through into CLR shutdown with live native callbacks.
        while true do
            try
                use current = System.Diagnostics.Process.GetCurrentProcess()
                current.Kill()
            with _ -> ()
            Thread.Sleep(50)

    let kill reason =
#if DEBUG
        // Diagnostic allocation, startup and IO must not prevent termination.
        try
            let message = reason + "; stage=" + Volatile.Read(&stage)
            let writer = Thread(ThreadStart(fun () -> ShutdownTrace.write message), IsBackground = true)
            writer.Start()
            writer.Join(100) |> ignore
        with _ -> ()
#endif
        killImmediately()

    let start() =
        try
            // Arm once before publishing to the UI; repeated requests cannot extend it.
            if Interlocked.CompareExchange(&deadline, clock.ElapsedMilliseconds + 15000L, Int64.MaxValue) = Int64.MaxValue then
                let monitor = Thread(ThreadStart(fun () ->
                    while clock.ElapsedMilliseconds < Interlocked.Read(&deadline) do
                        Thread.Sleep(25)
                    kill "shutdown deadline exceeded"), IsBackground = true, Name = "Shutdown deadline")
                monitor.Start()
        with _ ->
            // No diagnostic thread is needed when independent monitoring is unavailable.
            killImmediately()

    let saved() =
        let limit = min (Interlocked.Read(&deadline)) (clock.ElapsedMilliseconds + 10000L)
        Interlocked.Exchange(&deadline, limit) |> ignore

    let attempt name work =
        setStage name
        try work()
        with ex -> ShutdownTrace.write (name + ": " + ex.GetType().FullName)

#if DEBUG
    let stall() =
        match Int32.TryParse(Environment.GetEnvironmentVariable("WINDOWTABS_DEBUG_SHUTDOWN_STALL_MS")) with
        | true, ms when ms > 0 ->
            setStage "debug group shutdown stall"
            ShutdownTrace.write "debug group shutdown stall"
            Thread.Sleep(ms)
        | _ -> ()
#endif

// Defined before CrashLog so its exception-path IO/dialog can also be timed.
// Only the registered input thread is observed; never log ordinary message-loop
// waits. The sink is installed by InputStallTrace, and never writes on this thread.
module HookThreadTiming =
#if DEBUG
    let mutable private threadId = 0
    let mutable completed : (string -> float -> int64 -> unit) = fun _ _ _ -> ()
    let registerCurrentThread() = Volatile.Write(&threadId, Thread.CurrentThread.ManagedThreadId)
    let record name started ended =
        if Thread.CurrentThread.ManagedThreadId = Volatile.Read(&threadId) then
            let ms = float (ended - started) * 1000.0 / float System.Diagnostics.Stopwatch.Frequency
            if ms >= 20.0 then
                try completed name ms ended with _ -> ()
    let time name (work: unit -> 'a) =
        if Thread.CurrentThread.ManagedThreadId <> Volatile.Read(&threadId) then work() else
        let started = System.Diagnostics.Stopwatch.GetTimestamp()
        try work()
        finally record name started (System.Diagnostics.Stopwatch.GetTimestamp())
#else
    let inline registerCurrentThread() = ()
    let inline time (_name: string) (work: unit -> 'a) = work()
#endif

// Crash evidence must also survive in Release. Never query application windows.
module CrashLog =
    let mutable private installed = 0
    let private threadInstalled = new ThreadLocal<bool>(fun () -> false)

    let writeTo directory source (error: obj) =
        try
            Directory.CreateDirectory(directory) |> ignore
            let thread = Thread.CurrentThread
            let name = sprintf "crash_%s_%d_%s.log" (DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fffffff")) thread.ManagedThreadId (Guid.NewGuid().ToString("N"))
            let threadName = if isNull thread.Name then "<unnamed>" else thread.Name
            let text = sprintf "%s\r\nThread: %s (%d)\r\n%s\r\n" source threadName thread.ManagedThreadId (if isNull error then "<null>" else error.ToString())
            File.WriteAllText(Path.Combine(directory, name), text)
        with _ -> () // Reporting an exception must never replace the exception.

    let write source error =
        let directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowTabs")
        writeTo directory source error

    let installThread() =
        if not threadInstalled.Value then
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException, true)
            Application.ThreadException.Add(fun args ->
                HookThreadTiming.time "exception.write" (fun () -> write "Application.ThreadException" args.Exception)
                // Preserve WinForms' standard exception dialog / Abort behaviour.
                use dialog = HookThreadTiming.time "exception.dialog.create" (fun () -> new ThreadExceptionDialog(args.Exception))
                if HookThreadTiming.time "exception.dialog.show" (fun () -> dialog.ShowDialog()) = DialogResult.Abort then
                    HookThreadTiming.time "exception.exit" Application.Exit
                    ShutdownTrace.beginShutdown()
                    ShutdownTrace.write "exception dialog Environment.Exit"
                    Environment.Exit(0))
            threadInstalled.Value <- true

    let install() =
        if Interlocked.Exchange(&installed, 1) = 0 then
            AppDomain.CurrentDomain.UnhandledException.Add(fun args -> write "AppDomain.UnhandledException" args.ExceptionObject)
            TaskScheduler.UnobservedTaskException.Add(fun args -> write "TaskScheduler.UnobservedTaskException" args.Exception)
        installThread()
