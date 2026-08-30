// Save -> settings.json -> load, run without starting WindowTabs.
//
//   dotnet fsi WtProgram/Shared/SavedTabState.Tests.fsx
//
// It loads the very file the exe is built from, so what passes here is what
// ships. Every check goes through real JSON TEXT: the object is written,
// serialized, the string is parsed again and read back, which is the same
// journey the settings file makes across a restart.
//
// The point of it is the half of the restore nobody had ever checked. The
// order arithmetic can be perfect and still produce nothing, because what it
// is handed is whatever the file gave back: if the save writes an alignment
// under a key the load does not read, the alignment is gone and the group
// comes back in the wrong bands.

#r "../../Newtonsoft.Json.dll"
#load "SavedTabState.fs"

open System
open Newtonsoft.Json.Linq
open Bemo.SavedTabState

let mutable failures = 0
let mutable checks = 0

let ok name = checks <- checks + 1; printfn "  ok   %s" name
let bad name detail =
    checks <- checks + 1
    failures <- failures + 1
    printfn "  FAIL %s\n       %s" name detail

let is name expected actual =
    if expected = actual then ok name
    else bad name (sprintf "expected %A\n       got      %A" expected actual)

// The journey the settings file actually makes.
let roundTrip (t: SavedTab) =
    let text = (toJson t).ToString()
    match ofJson (JObject.Parse(text)) with
    | Some(back) -> back
    | None -> failwithf "entry did not read back at all: %s" text

let hwnd (n: int) = IntPtr(n)

let full = {
    ofHwnd (hwnd 0x407C2) with
        exePath = Some(@"C:\Program Files\Hidemaru\HmFilerClassic.exe")
        windowTitle = Some("WorkArchive")
        rect = Some(120, -40, 1280, 800)
        renamedTabName = Some("Archive")
        isPinned = true
        fillColor = Some("112233FF")
        underlineColor = Some("445566CC")
        borderColor = Some("778899AA")
        align = Some(AlignLeft)
        seedSince = Some(DateTime(2026, 8, 29, 1, 20, 23, 460))
        closedByUser = true }

printfn "SavedTabState checks"
printfn ""

printfn "1. A tab carrying every piece of state comes back unchanged."
is "every field survives the round trip" full (roundTrip full)

printfn ""
printfn "2. A tab carrying nothing but its handle comes back unchanged."
let bare = ofHwnd (hwnd 0x109AE)
is "bare entry survives" bare (roundTrip bare)
is "no key is written for a default" 1 ((toJson bare).Properties() |> Seq.length)

printfn ""
printfn "3. Each field ON ITS OWN. A field written under a key nobody reads"
printfn "   still comes back right when its neighbours are there to mask it,"
printfn "   so they are driven one at a time."
let variations : (string * SavedTab) list = [
    "exePath",        { bare with exePath = Some(@"C:\x\y.exe") }
    "windowTitle",    { bare with windowTitle = Some("a title") }
    "rect",           { bare with rect = Some(1, 2, 3, 4) }
    "renamedTabName", { bare with renamedTabName = Some("renamed") }
    "isPinned",       { bare with isPinned = true }
    "fillColor",      { bare with fillColor = Some("0A0B0C0D") }
    "underlineColor", { bare with underlineColor = Some("0A0B0C0D") }
    "borderColor",    { bare with borderColor = Some("0A0B0C0D") }
    "align left",     { bare with align = Some(AlignLeft) }
    "align right",    { bare with align = Some(AlignRight) }
    "seedSince",      { bare with seedSince = Some(DateTime(2026, 1, 2, 3, 4, 5, 678)) }
    "closedByUser",   { bare with closedByUser = true }
]
variations |> List.iter (fun (name, t) -> is name t (roundTrip t))

printfn ""
printfn "4. The key names themselves. A settings file written by the shipped"
printfn "   version has to keep loading, so these are fixed, not incidental."
let keysOf (t: SavedTab) = (toJson t).Properties() |> Seq.map (fun p -> p.Name) |> List.ofSeq
is "the keys of a fully populated tab"
    ["hwnd"; "exePath"; "windowTitle"; "rect"; "renamedTabName"; "isPinned";
     "tabFillColor"; "tabUnderlineColor"; "tabBorderColor"; "tabAlignment";
     "seedSince"; "closedByUser"]
    (keysOf full)

printfn ""
printfn "5. Alignment. This is the value the whole generation is about: it"
printfn "   decides which band the tab is drawn in, and the saved order can"
printfn "   only be honoured inside a band."
is "TopLeft on the way out" (Some("TopLeft")) (parseAlign "TopLeft" |> Option.map formatAlign)
is "TopRight on the way out" (Some("TopRight")) (parseAlign "TopRight" |> Option.map formatAlign)
is "an older file's Left is still read" (Some(AlignLeft)) (parseAlign "Left")
is "an older file's Right is still read" (Some(AlignRight)) (parseAlign "Right")
is "anything else is no alignment at all" None (parseAlign "sideways")
// An alignment that is absent is NOT the same as one that is TopRight: absent
// means "whichever side this group is set to", so writing a default would nail
// the tab to a side and the group's tabPosition would stop moving it.
is "an absent alignment stays absent" None (roundTrip bare).align

printfn ""
printfn "6. Colours. The text is RRGGBBAA - alpha last - and the two ends have"
printfn "   to agree, which is why both halves live in the one module."
is "bytes come back in the same order" (Some(0x11uy, 0x22uy, 0x33uy, 0x44uy)) (Rgba.parse "11223344")
is "and go out in that order" "11223344" (Rgba.format (0x11uy, 0x22uy, 0x33uy, 0x44uy))
is "a leading # is tolerated" (Some(0xAAuy, 0xBBuy, 0xCCuy, 0xDDuy)) (Rgba.parse "#AABBCCDD")
is "a truncated colour is refused, not guessed at" None (Rgba.parse "AABBCC")
is "an unparsable colour is refused" None (Rgba.parse "ZZZZZZZZ")

printfn ""
printfn "7. Rectangles, including the off-screen place Windows parks a"
printfn "   minimized window at."
is "a minimized window's rectangle" (Some(-32000, -32000, 160, 28))
    (roundTrip { bare with rect = Some(-32000, -32000, 160, 28) }).rect
is "a malformed rectangle is refused, not guessed at" None (parseRect "1,2,3")
is "so is one that is not numbers" None (parseRect "a,b,c,d")

printfn ""
printfn "8. A whole group. The order of the entries in the file IS the saved"
printfn "   tab order - the restore reads no index, it reads the sequence - so"
printfn "   the array has to come back in the order it went out."
let groupTabs =
    [0x109AE; 0x109B8; 0x407C2; 0x109BE; 0x3077E]
    |> List.mapi (fun i h ->
        { ofHwnd (hwnd h) with
            exePath = Some(sprintf @"C:\app%d.exe" i)
            windowTitle = Some(sprintf "window %d" i)
            align = Some(if i % 2 = 0 then AlignLeft else AlignRight)
            isPinned = (i = 1) })
let groupArray = JArray()
groupTabs |> List.iter (fun t -> groupArray.Add(toJson t))
let groupJson = (groupToJson groupArray (Some("TopLeft")) (Some(true))).ToString()
let groupBack = JObject.Parse(groupJson)
is "the five tabs come back in the saved order" groupTabs (groupWindows groupBack)
is "the group's tab position comes back" (Some("TopLeft")) (groupTabPosition groupBack)
is "the group's snap margin comes back" (Some(true)) (groupSnapMargin groupBack)

printfn ""
printfn "9. The old file format - a bare array of windows, no group object -"
printfn "   still loads. A user upgrading has one of these."
let oldFormat = JArray()
groupTabs |> List.iter (fun t -> oldFormat.Add(toJson t))
let oldBack = JArray.Parse(oldFormat.ToString())
is "the windows of an old-format group" groupTabs (groupWindows oldBack)
is "an old-format group has no tab position of its own" None (groupTabPosition oldBack)

printfn ""
printfn "10. Damaged input is skipped, never thrown on: the settings file is"
printfn "    edited by hand and written by older versions."
let damaged = JArray.Parse("""[
    {"hwnd": 100, "tabAlignment": "TopLeft"},
    {"exePath": "C:\\no-handle.exe"},
    "not an object at all",
    {"hwnd": 200, "isPinned": "yes please", "rect": 5, "seedSince": "not a date"},
    {"hwnd": 300, "somethingFromAFutureVersion": 42}
]""")
let damagedBack = groupWindows damaged
is "the entries that have a handle are kept" [hwnd 100; hwnd 200; hwnd 300] (damagedBack |> List.map (fun t -> t.hwnd))
is "an entry with no handle is dropped" 3 damagedBack.Length
is "a field of the wrong type falls back to its default" false damagedBack.[1].isPinned
is "and a malformed one to nothing at all" None damagedBack.[1].rect
is "an unparsable date is not a date" None damagedBack.[1].seedSince
is "a key from a later version is ignored" (ofHwnd (hwnd 300)) damagedBack.[2]

printfn ""
printfn "11. Writing what was read gives the same file back. A settings file"
printfn "    that is loaded and saved without anything happening in between"
printfn "    must not drift - that is what a not-yet-started window's entry"
printfn "    does at every save while it waits for its application."
let twice = roundTrip (roundTrip full)
is "a second round trip changes nothing" (roundTrip full) twice
is "and the text is identical" ((toJson full).ToString()) ((toJson (roundTrip full)).ToString())

printfn ""
if failures = 0 then printfn "%d checks, all passed." checks
else printfn "%d checks, %d FAILED." checks failures
exit (if failures = 0 then 0 else 1)
