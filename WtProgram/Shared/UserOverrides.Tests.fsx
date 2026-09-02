// Check the merge rule without starting WindowTabs: shipped defaults under a
// fake exe folder, the user's file under a fake %APPDATA%, and the result.
//
//   dotnet fsi UserOverrides.Tests.fsx
#r "../../Newtonsoft.Json.dll"

open System
open System.IO
open Newtonsoft.Json.Linq

let mutable failures = 0
let is name (expected: string) (actual: string) =
    if expected = actual then printfn "  ok   %s" name
    else
        failures <- failures + 1
        printfn "  FAIL %s\n       expected %s\n       got      %s" name expected actual

// The rule under test, copied from UserOverrides so this runs without a build.
let rec mergeInto (target: JObject) (source: JObject) =
    for prop in source.Properties() do
        match target.[prop.Name], prop.Value with
        | (:? JObject as into), (:? JObject as from) -> mergeInto into from
        | _ -> target.[prop.Name] <- prop.Value.DeepClone()

let merge (shipped: string) (mine: string) =
    let m = JObject.Parse(shipped).DeepClone() :?> JObject
    mergeInto m (JObject.Parse(mine))
    m.ToString(Newtonsoft.Json.Formatting.None)

printfn "-- Version_Folder.json --"

is "an application the user adds is kept"
    """{"LineMediaPlayer.exe":true,"MyApp.exe":true}"""
    (merge """{"LineMediaPlayer.exe":true}""" """{"MyApp.exe":true}""")

is "a default the user turns off stays off"
    """{"LineMediaPlayer.exe":false}"""
    (merge """{"LineMediaPlayer.exe":true}""" """{"LineMediaPlayer.exe":false}""")

is "a default added later reaches someone with their own file"
    """{"LineMediaPlayer.exe":true,"NewDefault.exe":true,"MyApp.exe":true}"""
    (merge """{"LineMediaPlayer.exe":true,"NewDefault.exe":true}""" """{"MyApp.exe":true}""")

printfn "-- nested objects (the shape Window_Margin.json uses) --"

is "one field is overridden, the rest of the entry survives"
    """{"a.exe":{"top":1,"left":9,"right":3,"bottom":4}}"""
    (merge """{"a.exe":{"top":1,"left":2,"right":3,"bottom":4}}""" """{"a.exe":{"left":9}}""")

printfn "-- arrays --"

is "an array is replaced whole, never spliced"
    """{"list":[9]}"""
    (merge """{"list":[1,2,3]}""" """{"list":[9]}""")

printfn ""
if failures = 0 then printfn "all passed" else printfn "%d FAILED" failures
