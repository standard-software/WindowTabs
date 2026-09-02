namespace Bemo

open System
open System.IO
open System.Reflection
open Newtonsoft.Json.Linq

// Which executables keep each version in a directory of its own, read from
// Settings\VersionFolder.json beside the executable, with the user's own
// under %APPDATA%\WindowTabs\Settings laid over it:
//
//     {
//       "LineMediaPlayer.exe": true
//     }
//
// A path whose file name is listed here has any directory made only of digits
// and dots replaced by "*" before it is compared or stored, so the setting
// survives the application's next update. Everything else keeps its full
// path: "C:\Program Files\Microsoft Visual Studio\18\..." is the same shape,
// and somebody who keeps two versions of Visual Studio installed wants them
// told apart. Store applications need no entry - Windows decides their
// layout, so AppPath always handles them.
//
// The same shape as Settings\WindowMargin.json, which already names this
// application for a different reason, so adding one is a matter of editing a
// file rather than rebuilding. A value of false keeps an entry while turning
// it off.
//
// This lives apart from AppPath so that AppPath keeps no dependency on
// Newtonsoft or the file system, and its checks can run under fsi without a
// build.
module AppPathSettings =

    [<Literal>]
    let fileName = "VersionFolder.json"

    // The file shipped beside the executable is the default; the one under
    // %APPDATA%\WindowTabs\Settings is the user's, and is laid over it name
    // by name. The installer replaces the shipped file every upgrade, so an
    // application added there would not survive one - and under the default
    // install folder it cannot be written at all.
    let private read () =
        try
            match UserOverrides.loadObject UserOverrides.settingsDir fileName with
            | Some(parsed) ->
                [ for prop in parsed.Properties() do
                    match prop.Value.Type with
                    | JTokenType.Boolean -> if prop.Value.Value<bool>() then yield prop.Name
                    | _ -> yield prop.Name ]
            | None ->
                System.Diagnostics.Debug.WriteLine(
                    sprintf "[AppPath] no %s beside the executable or under %s"
                        fileName (UserOverrides.userRoot ()))
                []
        with ex ->
            System.Diagnostics.Debug.WriteLine(
                sprintf "[AppPath] error loading settings: %s" ex.Message)
            []

    // Called once at startup, before anything reads or rewrites a stored path.
    let load () =
        let names = read ()
        AppPath.setVersionDirectoryApps names
        System.Diagnostics.Debug.WriteLine(
            sprintf "[AppPath] version-directory applications: %s"
                (if names.IsEmpty then "(none)" else String.Join(", ", names)))
