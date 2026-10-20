namespace Bemo

open System
open System.IO

module PortableSettings =
    let choosePath appData exe appDataExists exeExists =
        if appDataExists || not exeExists then appData else exe

    let appDataPath() =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowTabs", "WindowTabsSettings.txt")
    let exePath() = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WindowTabsSettings.txt")
    let writeFailed = Event<unit>()
    let mutable private warned = false
    let mutable private pending = false
    let mutable private ready = false
    let notifyWriteFailure() =
        if not warned then
            pending <- true
            if ready then
                warned <- true
                pending <- false
                writeFailed.Trigger()
    let writeNotificationReady() =
        ready <- true
        if pending then notifyWriteFailure()

    let canWrite (path: string) =
        let mutable probe = ""
        try
            if File.Exists(path) then
                use stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read)
                ()
            let dir = Path.GetDirectoryName(path)
            Directory.CreateDirectory(dir) |> ignore
            probe <- Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tmp")
            use stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            true
        with _ -> false
        |> fun result ->
            if probe <> "" then
                try File.Delete(probe) with _ -> ()
            result

    let backups (path: string) =
        Directory.GetFiles(Path.GetDirectoryName(path), Path.GetFileName(path) + ".bak.*")

    // Publish only verified bytes. Never rename the source before publication succeeds.
    let transfer source destination move overwrite =
        if String.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase) then
            invalidArg "destination" "Source and destination must differ."
        let bytes = File.ReadAllBytes(source)
        let dir = Path.GetDirectoryName(destination)
        Directory.CreateDirectory(dir) |> ignore
        let staging = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tmp")
        let previous = staging + ".previous"
        let mutable published = false
        let mutable completed = false
        try
            try
                File.WriteAllBytes(staging, bytes)
                if File.ReadAllBytes(staging) <> bytes then failwith "Settings verification failed."
                if overwrite && File.Exists(destination) then File.Replace(staging, destination, previous)
                else File.Move(staging, destination)
                published <- true
                if File.ReadAllBytes(destination) <> bytes then failwith "Settings verification failed."
                if move then
                    let prefix = source + ".moved." + DateTime.Now.ToString("yyyyMMdd_HHmmss")
                    let mutable kept = prefix
                    let mutable suffix = 0
                    while File.Exists(kept) do
                        suffix <- suffix + 1
                        kept <- prefix + "." + string suffix
                    File.Move(source, kept)
                completed <- true
            with _ ->
                if published then
                    try
                        if File.Exists(previous) then File.Replace(previous, destination, null)
                        else File.Delete(destination)
                    with _ -> ()
                reraise()
        finally
            try File.Delete(staging) with _ -> ()
            if completed then
                try File.Delete(previous) with _ -> ()
