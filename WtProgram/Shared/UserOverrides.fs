namespace Bemo

open System
open System.IO
open System.Reflection
open Newtonsoft.Json.Linq

// Every JSON file WindowTabs ships beside the executable is a default, and a
// file of the same name under %APPDATA%\WindowTabs is the user's own. What
// the program reads is the two laid over one another.
//
//     <exe>\Settings\WindowMargin.json                 default, replaced every upgrade
//     <exe>\Settings\VersionFolder.json
//     <exe>\Settings\Language\Japanese.json
//     <exe>\Settings\Language\FileList.json
//
//     %APPDATA%\WindowTabs\Settings\...                the user's, never touched
//
// The installer owns everything beside the executable and lays it down fresh
// each time, so an edit made there is gone after the next upgrade - and under
// the default install folder, Program Files, the user cannot write there at
// all. The zip has no installer and no way to protect anything either.
// %APPDATA% has neither problem, and is where the settings file has always
// lived. Deleting the user's folder restores the shipped behaviour, and there
// is nothing else to undo.
//
// The rule for laying one file over the other follows from what the file is:
//
//   An OBJECT is merged by its top-level keys. The user's value replaces the
//   default's for a key they name, whole - a margin entry is four numbers and
//   is replaced as four numbers - and every key they do not name keeps its
//   default. So one added application, or one corrected string, is a
//   one-line file, and defaults added by a later version still arrive.
//
//   An ARRAY is the user's, whole. The only array is the language list, and
//   it says which languages the menu SHOWS, which is as much a way of hiding
//   languages as of adding them: half of one list and half of another is
//   never what was meant.
//
// The type of the JSON decides which, so no file needs naming here.
module UserOverrides =

    [<Literal>]
    let settingsDir = "Settings"

    [<Literal>]
    let languageDir = @"Settings\Language"

    let exeRoot () =
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)

    let userRoot () =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WindowTabs")

    let shippedPath (dir: string) (fileName: string) = Path.Combine(exeRoot (), dir, fileName)

    // Where the user puts their own copy. Reported whether or not it exists,
    // so that documentation and diagnostics can name the place before there
    // is anything there.
    let userPath (dir: string) (fileName: string) = Path.Combine(userRoot (), dir, fileName)

    // ----- the rule, on its own, so that it can be checked without files -----

    let private isObject (t: JToken) = t.Type = JTokenType.Object

    let overlay (shipped: JToken option) (mine: JToken option) : JToken option =
        match shipped, mine with
        | None, None -> None
        | Some(s), None -> Some(s)
        | None, Some(m) -> Some(m)
        | Some(s), Some(m) when isObject s && isObject m ->
            let merged = s.DeepClone() :?> JObject
            for prop in (m :?> JObject).Properties() do
                merged.[prop.Name] <- prop.Value.DeepClone()
            Some(merged :> JToken)
        | Some(_), Some(m) -> Some(m)

    // ----- files -----

    // A file edited into invalid JSON must not take the program down, and
    // must not take the default with it: the other side is used on its own.
    let private readToken (path: string) : JToken option =
        try
            if File.Exists(path) then Some(JToken.Parse(removeJsoncComments (File.ReadAllText(path))))
            else None
        with _ ->
            System.Diagnostics.Debug.WriteLine(sprintf "[UserOverrides] could not read %s" path)
            None

    let loadFrom (shippedRoot: string) (userRootDir: string) (dir: string) (fileName: string) =
        overlay
            (readToken (Path.Combine(shippedRoot, dir, fileName)))
            (readToken (Path.Combine(userRootDir, dir, fileName)))

    // The shipped default with the user's file laid over it, or None when
    // neither exists.
    let load (dir: string) (fileName: string) : JToken option =
        loadFrom (exeRoot ()) (userRoot ()) dir fileName

    let loadObject (dir: string) (fileName: string) : JObject option =
        match load dir fileName with
        | Some(:? JObject as o) -> Some(o)
        | _ -> None

    let loadArray (dir: string) (fileName: string) : JArray option =
        match load dir fileName with
        | Some(:? JArray as a) -> Some(a)
        | _ -> None

    // True when either side has the file. A language listed in the menu has
    // to exist somewhere to be worth showing.
    let exists (dir: string) (fileName: string) =
        File.Exists(shippedPath dir fileName) || File.Exists(userPath dir fileName)

    // Nothing here reads the folder that used to hold the languages,
    // <exe>\Language\. A file the user put there is theirs to move: an
    // automatic carry-over cannot tell an edited shipped file from an
    // unedited one whose default has since changed, and moving the latter
    // would freeze that language at the old strings.
