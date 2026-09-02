namespace Bemo

open System
open System.IO
open System.Reflection
open Newtonsoft.Json.Linq

// A JSON file that ships beside the executable is a default, and a file of the
// same name under %APPDATA%\WindowTabs is the user's own. What the program
// reads is the two merged, key by key, with the user's value winning.
//
//     <exe>\Settings\Version_Folder.json          default, replaced every upgrade
//     %APPDATA%\WindowTabs\Settings\Version_...   the user's, never touched
//
// The installer owns everything beside the executable and lays it down fresh
// each time, so an entry added there would be gone after the next upgrade.
// It also installs, by default, under Program Files, which the user cannot
// write to at all. Neither is true of %APPDATA%, which is where the settings
// file has always lived.
//
// Merging by key rather than by file keeps the two purposes apart: someone who
// names one application of their own still receives every default added since,
// and can turn a default off by naming it with the opposite value. Deleting
// their file restores the shipped behaviour, and there is nothing else to undo.
module UserOverrides =

    let private exeRoot () =
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)

    let userRoot () =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WindowTabs")

    // Where the user puts their own copy. Reported whether or not it exists,
    // so that documentation and diagnostics can name the place before there is
    // anything there.
    let userPath (folder: string) (fileName: string) =
        Path.Combine(userRoot (), folder, fileName)

    let private read (path: string) =
        try
            if File.Exists(path) then Some(parseJsoncObject (File.ReadAllText(path))) else None
        with _ ->
            // A file edited into invalid JSON must not take the program down,
            // and must not take the defaults with it: the other level is used.
            System.Diagnostics.Debug.WriteLine(
                sprintf "[UserOverrides] could not read %s" path)
            None

    // Objects are merged key by key at every depth. Anything else - a string,
    // a number, an array - is taken whole from the user's side, because half of
    // one array and half of another is never what was meant.
    //
    // Written out rather than using JContainer.Merge so the rule is the one
    // described above whatever version of Newtonsoft is linked in.
    let rec private mergeInto (target: JObject) (source: JObject) =
        for prop in source.Properties() do
            match target.[prop.Name], prop.Value with
            | (:? JObject as into), (:? JObject as from) -> mergeInto into from
            | _ -> target.[prop.Name] <- prop.Value.DeepClone()

    // The shipped defaults with the user's file merged over the top.
    let load (folder: string) (fileName: string) : JObject option =
        let shipped = read (Path.Combine(exeRoot (), folder, fileName))
        let mine = read (userPath folder fileName)
        match shipped, mine with
        | None, None -> None
        | Some(o), None -> Some(o)
        | None, Some(o) -> Some(o)
        | Some(shipped), Some(mine) ->
            let merged = shipped.DeepClone() :?> JObject
            mergeInto merged mine
            Some(merged)
