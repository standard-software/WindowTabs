namespace Bemo

open System
open System.IO

module PortableSettings =
    let choosePath appData exe appDataExists exeExists =
        if appDataExists || not exeExists then appData else exe

    let appDataPath() =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowTabs", "WindowTabsSettings.json")
    let exePath() = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WindowTabsSettings.json")
    let legacyPath (appData: string) = Path.ChangeExtension(appData, ".txt")

    let shouldMigrate appDataExists exeExists legacyExists =
        not appDataExists && not exeExists && legacyExists

    // Separate portable backups from the APPDATA settings and from other executables.
    let backupPath (appData: string) (exe: string) (path: string) =
        if String.Equals(Path.GetFullPath(path), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase) then
            use hash = System.Security.Cryptography.SHA256.Create()
            let key = hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(exe).ToUpperInvariant()))
                      |> Array.map (fun b -> b.ToString("x2")) |> String.concat ""
            Path.Combine(Path.GetDirectoryName(appData), "WindowTabsSettings.portable-" + key + ".json")
        else path

    let retainedPath appData exe path kind stamp =
        backupPath appData exe path + "." + kind + "." + stamp

    let private copyVerified (source: string) (destination: string) =
        let bytes = File.ReadAllBytes(source)
        Directory.CreateDirectory(Path.GetDirectoryName(destination)) |> ignore
        do
            use stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            stream.Write(bytes, 0, bytes.Length)
        if File.ReadAllBytes(destination) <> bytes then failwith "Settings verification failed."

    // Preservation failure must not prevent recovery of usable settings.
    let replaceRecovered staging path kept =
        if File.Exists(path) then
            if String.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetDirectoryName(Path.GetFullPath(kept)), StringComparison.OrdinalIgnoreCase) then
                File.Replace(staging, path, kept)
                true
            else
                let preserved =
                    try
                        copyVerified path kept
                        true
                    with _ -> false
                File.Replace(staging, path, null)
                preserved
        else
            File.Move(staging, path)
            false

    let writeFailed = Event<string>()
    let mutable private warned = false
    let mutable private pending = None
    let mutable private ready = false
    let notifyFailure key =
        if not warned then
            pending <- Some key
            if ready then
                warned <- true
                pending <- None
                writeFailed.Trigger(key)
    let notifyWriteFailure() = notifyFailure "PortableWriteFailed"
    let notifyMigrationFailure() = notifyFailure "SettingsMigrationFailed"
    let writeNotificationReady() =
        ready <- true
        match pending with
        | Some key -> notifyFailure key
        | None -> ()

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
        let dir = Path.GetDirectoryName(path)
        if Directory.Exists(dir) then Directory.GetFiles(dir, Path.GetFileName(path) + ".bak.*")
        else [||]

    // Publish only verified bytes. Never rename the source before publication succeeds.
    let transferWithPaths appData exe source destination move overwrite =
        if String.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase) then
            invalidArg "destination" "Source and destination must differ."
        let bytes = File.ReadAllBytes(source)
        let dir = Path.GetDirectoryName(destination)
        Directory.CreateDirectory(dir) |> ignore
        let staging = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tmp")
        let previous = retainedPath appData exe destination "previous" (Guid.NewGuid().ToString("N"))
        let mutable published = false
        let mutable completed = false
        try
            try
                File.WriteAllBytes(staging, bytes)
                if File.ReadAllBytes(staging) <> bytes then failwith "Settings verification failed."
                if overwrite && File.Exists(destination) then
                    copyVerified destination previous
                    File.Replace(staging, destination, null)
                else File.Move(staging, destination)
                published <- true
                if File.ReadAllBytes(destination) <> bytes then failwith "Settings verification failed."
                if move then
                    let prefix = retainedPath appData exe source "moved" (DateTime.Now.ToString("yyyyMMdd_HHmmss"))
                    let mutable kept = prefix
                    let mutable suffix = 0
                    while File.Exists(kept) do
                        suffix <- suffix + 1
                        kept <- prefix + "." + string suffix
                    if backupPath appData exe source = source then File.Move(source, kept)
                    else
                        copyVerified source kept
                        if File.ReadAllBytes(kept) <> bytes then failwith "Settings verification failed."
                        File.Delete(source)
                completed <- true
            with _ ->
                if published then
                    try
                        if File.Exists(previous) then
                            File.Copy(previous, staging, true)
                            File.Replace(staging, destination, null)
                            File.Delete(previous)
                        else File.Delete(destination)
                    with _ -> ()
                reraise()
        finally
            try File.Delete(staging) with _ -> ()
            if completed || not published then
                try File.Delete(previous) with _ -> ()

    let transfer source destination move overwrite =
        transferWithPaths (appDataPath()) (exePath()) source destination move overwrite

    let migrate appData exe =
        if shouldMigrate (File.Exists(appData)) (File.Exists(exe)) (File.Exists(legacyPath appData)) then
            try
                transferWithPaths appData exe (legacyPath appData) appData false false
                false
            with _ -> true
        else false
