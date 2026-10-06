namespace Bemo

open System

// Debug-only trace of the session restore: which saved entry each window
// claimed, by which route, and where it was placed. Truncated at each start.
module RestoreTrace =
#if DEBUG
    let private path =
        IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WindowTabs", "restore_trace.log")
    let mutable private started = false
    let private gate = obj()
#endif
    // Takes a thunk, not a string: an argument is evaluated before the call,
    // so taking the message itself would leave every sprintf at every call
    // site running in Release - including two that walk the whole tab strip -
    // with only the file write compiled out.
    let log (f: unit -> string) =
#if DEBUG
        lock gate (fun () ->
            try
                if not started then
                    started <- true
                    try IO.File.WriteAllText(path, "") with _ -> ()
                IO.File.AppendAllText(path,
                    sprintf "%s %s\r\n" (DateTime.Now.ToString("HH:mm:ss.fff")) (f()))
            with _ -> ())
#else
        ignore f
#endif

