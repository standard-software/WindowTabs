namespace Bemo

open System
open System.IO
open System.Text
open System.Threading
open System.Runtime.InteropServices
open System.Runtime.InteropServices.ComTypes
open Microsoft.Win32

module StartupRegistration =
    [<ComImport; Guid("000214F9-0000-0000-C000-000000000046"); InterfaceType(ComInterfaceType.InterfaceIsIUnknown)>]
    type private IShellLink =
        abstract GetPath: [<Out; MarshalAs(UnmanagedType.LPWStr)>] path: StringBuilder * count: int * data: nativeint * flags: uint32 -> unit
        abstract GetIDList: nativeint byref -> unit
        abstract SetIDList: nativeint -> unit
        abstract GetDescription: [<Out; MarshalAs(UnmanagedType.LPWStr)>] text: StringBuilder * count: int -> unit
        abstract SetDescription: [<MarshalAs(UnmanagedType.LPWStr)>] text: string -> unit
        abstract GetWorkingDirectory: [<Out; MarshalAs(UnmanagedType.LPWStr)>] path: StringBuilder * count: int -> unit
        abstract SetWorkingDirectory: [<MarshalAs(UnmanagedType.LPWStr)>] path: string -> unit
        abstract GetArguments: [<Out; MarshalAs(UnmanagedType.LPWStr)>] text: StringBuilder * count: int -> unit
        abstract SetArguments: [<MarshalAs(UnmanagedType.LPWStr)>] text: string -> unit
        abstract GetHotkey: int16 byref -> unit
        abstract SetHotkey: int16 -> unit
        abstract GetShowCmd: int byref -> unit
        abstract SetShowCmd: int -> unit
        abstract GetIconLocation: [<Out; MarshalAs(UnmanagedType.LPWStr)>] path: StringBuilder * count: int * index: int byref -> unit
        abstract SetIconLocation: [<MarshalAs(UnmanagedType.LPWStr)>] path: string * index: int -> unit
        abstract SetRelativePath: [<MarshalAs(UnmanagedType.LPWStr)>] path: string * reserved: uint32 -> unit
        abstract Resolve: nativeint * flags: uint32 -> unit
        abstract SetPath: [<MarshalAs(UnmanagedType.LPWStr)>] path: string -> unit

    exception ChangeFailed

    let private fullyQualified (path: string) =
        let separator c = c = '\\' || c = '/'
        (path.Length >= 3 && Char.IsLetter(path.[0]) && path.[1] = ':' && separator path.[2]) ||
        (path.Length > 2 && separator path.[0] && separator path.[1])

    let samePath (exe: string) (target: string) =
        try
            let target = Environment.ExpandEnvironmentVariables(target)
            not (String.IsNullOrWhiteSpace(target)) && fullyQualified target && fullyQualified exe &&
            String.Equals(Path.GetFullPath(exe), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)
        with _ -> false

    let registryMatches exe (value: obj) =
        match value with
        | :? string as command ->
            let command = command.Trim()
            if command.StartsWith("\"") then
                let closing = command.IndexOf('"', 1)
                closing > 1 && samePath exe (command.Substring(1, closing - 1)) &&
                (closing = command.Length - 1 || Char.IsWhiteSpace(command.[closing + 1]))
            else
                samePath exe command ||
                (command |> Seq.mapi (fun i c -> i, c) |> Seq.exists (fun (i, c) ->
                    Char.IsWhiteSpace(c) && samePath exe (command.Substring(0, i))))
        | _ -> false

    // Never resolve links: resolution can search disks or display shell UI.
    let private withLink action =
        let instance = Activator.CreateInstance(Type.GetTypeFromCLSID(Guid("00021401-0000-0000-C000-000000000046")))
        try action (instance :?> IShellLink) (instance :?> IPersistFile)
        finally Marshal.FinalReleaseComObject(instance) |> ignore

    let private onSta action =
        if Thread.CurrentThread.GetApartmentState() = ApartmentState.STA then action()
        else
            let mutable result = None
            let mutable error: exn option = None
            let thread = Thread(ThreadStart(fun () ->
                try result <- Some(action()) with ex -> error <- Some ex))
            thread.SetApartmentState(ApartmentState.STA)
            thread.Start()
            thread.Join()
            match error with
            | Some ex -> raise ex
            | None -> result.Value

    let private readTarget (link: IShellLink) (file: IPersistFile) path =
        file.Load(path, 0)
        let target = StringBuilder(32768)
        link.GetPath(target, target.Capacity, nativeint 0, 4u)
        target.ToString()

    let readShortcut path =
        onSta (fun () -> withLink (fun link file -> readTarget link file path))

    let createShortcut path exe =
        onSta (fun () -> withLink (fun link file ->
            link.SetPath(Path.GetFullPath(exe))
            link.SetWorkingDirectory(Path.GetDirectoryName(Path.GetFullPath(exe)))
            file.Save(path, true)))

    // A bad link must not hide a later matching registration.
    let matchingTargets exe readTarget onError paths =
        paths |> Seq.choose (fun path ->
            try if samePath exe (readTarget path) then Some path else None
            with ex -> onError ex; None)

    let private inspectShortcuts folder exe onError collect =
        let paths =
            try Directory.GetFiles(folder, "*.lnk")
            with :? DirectoryNotFoundException -> [||]
        if paths.Length = 0 then collect Seq.empty
        else
            onSta (fun () -> withLink (fun link file ->
                matchingTargets exe (readTarget link file) onError paths |> collect))

    let matchingShortcuts folder exe = inspectShortcuts folder exe ignore Seq.toList
    let hasMatchingShortcut folder exe = inspectShortcuts folder exe ignore (Seq.isEmpty >> not)

    type Change = { Create: bool; Delete: string list; DeleteLegacy: bool }

    let plan enabled shortcuts legacy =
        if enabled then { Create = List.isEmpty shortcuts; Delete = []; DeleteLegacy = false }
        else { Create = false; Delete = shortcuts; DeleteLegacy = legacy }

    // Reserve a vacant name so another installation's link is never replaced.
    let addShortcut folder exe =
        Directory.CreateDirectory(folder) |> ignore
        let rec reserve index =
            let name = if index = 0 then "WindowTabs.lnk" else sprintf "WindowTabs (%d).lnk" index
            let path = Path.Combine(folder, name)
            if File.Exists(path) || Directory.Exists(path) then reserve (index + 1)
            else
                try
                    use stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                    path
                with :? IOException when File.Exists(path) -> reserve (index + 1)
        let path = reserve 0
        try createShortcut path exe
        with _ ->
            try File.Delete(path) with _ -> ()
            reraise()

    let private runKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"
    let private legacyMatches exe =
        use key = Registry.CurrentUser.OpenSubKey(runKey, false)
        not (isNull key) && registryMatches exe (key.GetValue("WindowTabs"))

    let private startupFolder() =
        let folder = Environment.GetFolderPath(Environment.SpecialFolder.Startup)
        if String.IsNullOrWhiteSpace(folder) then failwith "Startup folder is unavailable."
        folder

    let isEnabled exe =
        let shortcut = try hasMatchingShortcut (startupFolder()) exe with _ -> false
        let legacy = try legacyMatches exe with _ -> false
        shortcut || legacy

    // Continue independent removals, retaining just one failure to report.
    let applyChange enabled scan create delete deleteLegacy =
        let mutable error: exn option = None
        let onError ex = error <- Some ex
        let attempt action = try action() with ex -> onError ex
        // An unreadable individual link is not evidence of a failed change.
        // Folder enumeration failures still propagate into the operation result.
        let shortcuts = try scan ignore with ex -> onError ex; []
        let actions = plan enabled shortcuts false
        if actions.Create then attempt create
        for path in actions.Delete do attempt (fun () -> delete path)
        if not enabled then attempt deleteLegacy
        match error with Some ex -> raise ex | None -> ()

    // Always reread after the attempt, including a partially successful change.
    let changeAndRead requested change read =
        let failed = try change(); false with _ -> true
        let actual = read()
        actual, (failed || actual <> requested)

    let change exe enabled =
        applyChange enabled
            (fun onError -> inspectShortcuts (startupFolder()) exe onError Seq.toList)
            (fun () -> addShortcut (startupFolder()) exe)
            File.Delete
            (fun () ->
                let matches = try legacyMatches exe with _ -> false
                if matches then
                    use key = Registry.CurrentUser.OpenSubKey(runKey, true)
                    if not (isNull key) && registryMatches exe (key.GetValue("WindowTabs")) then
                        key.DeleteValue("WindowTabs", false))
