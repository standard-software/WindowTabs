namespace Bemo

open System.IO

// The path handed to Process.Start to open another window of a tab's
// program. Pure, so Tools\LaunchPath.Tests.fsx can load it on its own.
//
// A Store application cannot be started from its package folder under
// WindowsApps: the executable there refuses to run outside its package. The
// one known case with a launcher alias is Windows Terminal (wt.exe); every
// other Store application resolves to None and the caller tries its AUMID.
module LaunchPath =

    /// Whether the executable lives in a Store package folder.
    let isStoreApp (processPath: string) = processPath.Contains("WindowsApps")

    /// The launcher alias of a Store application, when there is one.
    let storeAppAlias (processPath: string) =
        let fileName = Path.GetFileName(processPath).ToLowerInvariant()
        if fileName.Contains("windowsterminal") then Some("wt.exe")
        else None

    /// What to start for a process path: the path itself for an ordinary
    /// program, the alias for a Store application that has one, None for a
    /// Store application that has none.
    let resolve (processPath: string) : string option =
        if isStoreApp processPath then storeAppAlias processPath
        else Some(processPath)

    /// Injected effects keep the launch decision testable without COM or processes.
    /// False means package activation was unavailable or failed; the caller shows
    /// the existing UWP error. Other process errors keep their original handling.
    let startWith (processPath: string) (launch: string -> unit)
                  (appId: unit -> string option) (activate: string -> bool) =
        let package () =
            match appId () with
            | Some id when not (System.String.IsNullOrWhiteSpace id) -> activate id
            | _ -> false
        match resolve processPath with
        | None -> package ()
        | Some path ->
            let launched =
                try
                    launch path
                    true
                with :? System.ComponentModel.Win32Exception when isStoreApp processPath -> false
            if launched then true else package ()
