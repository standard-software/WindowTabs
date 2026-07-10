namespace Bemo
open System
open System.Windows.Forms

type ExceptionHandlerPlugin() as this =

    member this.onException(e:UnhandledExceptionEventArgs) =
        match e.ExceptionObject with
        | :? exn as ex -> CrashLog.write "AppDomain.UnhandledException" ex
        | o -> CrashLog.write "AppDomain.UnhandledException" (Exception(string o))

    interface IPlugin with
        member x.init() =
            AppDomain.CurrentDomain.UnhandledException.Add this.onException
            // Log-and-continue instead of the default dialog/exit for
            // unhandled exceptions in the main thread's message loop
            // (Application.ThreadException handlers are per-thread; group
            // threads register their own in ThreadHelper).
            Application.ThreadException.Add(fun e -> CrashLog.write "main-thread ThreadException" e.Exception)
