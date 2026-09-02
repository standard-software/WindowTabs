// Checks for UserOverrides. Run from the repository root with:
//     fsi.exe --exec WtProgram/Shared/UserOverrides.Tests.fsx
//
// Needs no build: the module is loaded from source, with Newtonsoft from the
// restored package. The overlay rule is checked on its own, then the file
// side is checked against two temporary folders standing in for the exe
// folder and %APPDATA%.

#r "../../Newtonsoft.Json.dll"
#load "../Localization/JsoncHelper.fs"
#load "UserOverrides.fs"

open System
open System.IO
open Newtonsoft.Json.Linq
open Bemo

let mutable passed = 0
let mutable failed = 0

let check name condition =
    if condition then passed <- passed + 1
    else
        failed <- failed + 1
        printfn "FAIL  %s" name

let equal name (expected: string) (actual: string) =
    if expected = actual then passed <- passed + 1
    else
        failed <- failed + 1
        printfn "FAIL  %s\n        expected %s\n        actual   %s" name expected actual

let json (s: string) = Some(JToken.Parse(s))
let text (t: JToken option) =
    match t with
    | Some(t) -> t.ToString(Newtonsoft.Json.Formatting.None)
    | None -> "<none>"

let over shipped mine = text (UserOverrides.overlay (json shipped) (json mine))

// ----- objects: top-level keys, the user's value whole -----

equal "an application the user adds is kept beside the defaults"
    """{"LineMediaPlayer.exe":true,"MyApp.exe":true}"""
    (over """{"LineMediaPlayer.exe":true}""" """{"MyApp.exe":true}""")

equal "a default the user turns off stays off"
    """{"LineMediaPlayer.exe":false}"""
    (over """{"LineMediaPlayer.exe":true}""" """{"LineMediaPlayer.exe":false}""")

equal "a default added later reaches someone with a file of their own"
    """{"LineMediaPlayer.exe":true,"NewDefault.exe":true,"MyApp.exe":true}"""
    (over """{"LineMediaPlayer.exe":true,"NewDefault.exe":true}""" """{"MyApp.exe":true}""")

equal "one corrected string leaves every other string as shipped"
    """{"CloseTab":"Close tab","Language":"Sprache","Settings":"Settings..."}"""
    (over """{"CloseTab":"Close tab","Language":"Language","Settings":"Settings..."}"""
          """{"Language":"Sprache"}""")

equal "a margin entry is replaced as a whole, not field by field"
    """{"a.exe":{"left":9}}"""
    (over """{"a.exe":{"top":1,"left":2,"right":3,"bottom":4}}""" """{"a.exe":{"left":9}}""")

equal "an entry the user does not name keeps all of its fields"
    """{"a.exe":{"top":1,"left":2},"b.exe":{"top":5}}"""
    (over """{"a.exe":{"top":1,"left":2}}""" """{"b.exe":{"top":5}}""")

// ----- arrays: the user's, whole -----

equal "a shorter list hides languages"
    """[{"name":"Japanese","fileName":"Japanese.json"}]"""
    (over """[{"name":"English","fileName":"English.json"},{"name":"Japanese","fileName":"Japanese.json"}]"""
          """[{"name":"Japanese","fileName":"Japanese.json"}]""")

equal "a longer list adds languages, and is never spliced with the default"
    """[{"name":"Mine","fileName":"Mine.json"}]"""
    (over """[{"name":"English","fileName":"English.json"}]""" """[{"name":"Mine","fileName":"Mine.json"}]""")

// ----- one side only -----

equal "no user file means the default as it is"
    """{"a":1}""" (text (UserOverrides.overlay (json """{"a":1}""") None))

equal "no default means the user's file as it is"
    """{"b":2}""" (text (UserOverrides.overlay None (json """{"b":2}""")))

equal "neither is nothing" "<none>" (text (UserOverrides.overlay None None))

check "the default is not changed by being laid under something"
    (let shipped = JToken.Parse("""{"a":1}""")
     UserOverrides.overlay (Some shipped) (json """{"a":2}""") |> ignore
     shipped.ToString(Newtonsoft.Json.Formatting.None) = """{"a":1}""")

// ----- files -----

let root = Path.Combine(Path.GetTempPath(), "WtUserOverrides_" + Guid.NewGuid().ToString("N"))
let exe = Path.Combine(root, "exe")
let appdata = Path.Combine(root, "appdata")
let write (dir: string) (name: string) (content: string) =
    Directory.CreateDirectory(dir) |> ignore
    File.WriteAllText(Path.Combine(dir, name), content)

try
    write (Path.Combine(exe, "Settings")) "VersionFolder.json" """{ "LineMediaPlayer.exe": true }"""
    write (Path.Combine(appdata, "Settings")) "VersionFolder.json" """{ "MyApp.exe": true }"""
    equal "the two files are laid over one another"
        """{"LineMediaPlayer.exe":true,"MyApp.exe":true}"""
        (text (UserOverrides.loadFrom exe appdata "Settings" "VersionFolder.json"))

    write (Path.Combine(exe, "Settings")) "OnlyShipped.json" """{ "a": 1 }"""
    equal "a file with no user copy reads as shipped"
        """{"a":1}""" (text (UserOverrides.loadFrom exe appdata "Settings" "OnlyShipped.json"))

    write (Path.Combine(appdata, "Settings")) "OnlyMine.json" """{ "b": 2 }"""
    equal "a file the user adds with no default reads as theirs"
        """{"b":2}""" (text (UserOverrides.loadFrom exe appdata "Settings" "OnlyMine.json"))

    equal "a file that exists nowhere is nothing"
        "<none>" (text (UserOverrides.loadFrom exe appdata "Settings" "Missing.json"))

    write (Path.Combine(exe, "Settings")) "Comments.json" "{ // a comment\n \"a\": 1 /* another */ }"
    equal "comments in either file are allowed"
        """{"a":1}""" (text (UserOverrides.loadFrom exe appdata "Settings" "Comments.json"))

    write (Path.Combine(exe, "Settings")) "Broken.json" """{ "a": 1 }"""
    write (Path.Combine(appdata, "Settings")) "Broken.json" """{ "a": """
    equal "a user file that is not JSON is ignored and the default used"
        """{"a":1}""" (text (UserOverrides.loadFrom exe appdata "Settings" "Broken.json"))

    write (Path.Combine(exe, @"Settings\Language")) "FileList.json"
        """[{"name":"English","fileName":"English.json"},{"name":"Japanese","fileName":"Japanese.json"}]"""
    write (Path.Combine(appdata, @"Settings\Language")) "FileList.json"
        """[{"name":"Japanese","fileName":"Japanese.json"}]"""
    equal "the user's language list replaces the shipped one"
        """[{"name":"Japanese","fileName":"Japanese.json"}]"""
        (text (UserOverrides.loadFrom exe appdata @"Settings\Language" "FileList.json"))
finally
    try Directory.Delete(root, true) with _ -> ()

printfn ""
printfn "UserOverrides: %d passed, %d failed" passed failed
if failed > 0 then exit 1
