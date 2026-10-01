namespace Bemo
open System
open System.IO
open System.Threading
open System.Threading.Tasks
open System.Windows.Forms

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
                    Environment.Exit(0))
            threadInstalled.Value <- true

    let install() =
        if Interlocked.Exchange(&installed, 1) = 0 then
            AppDomain.CurrentDomain.UnhandledException.Add(fun args -> write "AppDomain.UnhandledException" args.ExceptionObject)
            TaskScheduler.UnobservedTaskException.Add(fun args -> write "TaskScheduler.UnobservedTaskException" args.Exception)
        installThread()
