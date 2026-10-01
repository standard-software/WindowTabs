namespace Bemo
open System
open System.IO
open System.Threading
open System.Threading.Tasks
open System.Windows.Forms

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
                write "Application.ThreadException" args.Exception
                // Preserve WinForms' standard exception dialog / Abort behaviour.
                use dialog = new ThreadExceptionDialog(args.Exception)
                if dialog.ShowDialog() = DialogResult.Abort then
                    Application.Exit()
                    Environment.Exit(0))
            threadInstalled.Value <- true

    let install() =
        if Interlocked.Exchange(&installed, 1) = 0 then
            AppDomain.CurrentDomain.UnhandledException.Add(fun args -> write "AppDomain.UnhandledException" args.ExceptionObject)
            TaskScheduler.UnobservedTaskException.Add(fun args -> write "TaskScheduler.UnobservedTaskException" args.Exception)
        installThread()
