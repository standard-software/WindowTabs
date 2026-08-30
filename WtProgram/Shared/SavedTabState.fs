namespace Bemo

open System
open Newtonsoft.Json.Linq

// One saved tab as it is written to settings.json and read back, and nothing
// else.
//
// It exists because the same handful of fields used to be written by two
// separate pieces of code and read by three more: a live window was written
// from the global per-window maps, a window that had not started yet was
// written from its closed-tab entry, and the restore parsed the result in one
// place for the group members, another for the reservation pass and a third
// for the identity count. Six copies of "the key is called tabAlignment and
// it holds TopLeft or TopRight" is five chances for the reader and the writer
// to disagree, and a disagreement there is silent: a value that is written
// under a name nobody reads simply vanishes at the next restart.
//
// Putting the record and its two conversions here also makes the round trip
// something that can be RUN without starting WindowTabs - the only dependency
// is Newtonsoft, so SavedTabState.Tests.fsx next to this file loads this very
// file, writes the JSON, parses the text back and compares. See requirement 2
// of the second generation brief.
module SavedTabState =

    // Which edge of the strip a tab is drawn against. TabAlign itself lives in
    // TabStripTypes, which drags in System.Drawing; repeating the two cases
    // here is what keeps this file loadable by a bare script. Program.fs
    // converts between the two in one place.
    type SavedAlign =
        | AlignLeft
        | AlignRight

    // Colours travel as their RRGGBBAA text. The conversion to and from
    // System.Drawing.Color is Program.fs's business; what matters here is that
    // the two ends agree on the byte order, so both halves live together.
    module Rgba =
        let format (r: byte, g: byte, b: byte, a: byte) =
            sprintf "%02X%02X%02X%02X" (int r) (int g) (int b) (int a)

        let parse (s: string) =
            if String.IsNullOrEmpty(s) then None
            else
                let s = if s.StartsWith("#") then s.Substring(1) else s
                if s.Length <> 8 then None
                else
                    try
                        let byteAt i = Convert.ToInt32(s.Substring(i, 2), 16) |> byte
                        Some(byteAt 0, byteAt 2, byteAt 4, byteAt 6)
                    with _ -> None

    type SavedTab = {
        // The handle the window had when it was saved. Valid only inside one
        // Windows session; after a restart it is nothing but a unique token
        // that names this entry in the group's order.
        hwnd: IntPtr
        exePath: string option
        windowTitle: string option
        // x, y, width, height as it stood when saved.
        rect: (int * int * int * int) option
        renamedTabName: string option
        isPinned: bool
        fillColor: string option
        underlineColor: string option
        borderColor: string option
        align: SavedAlign option
        // Set only on an entry that was written back while still waiting for
        // its window; a window that was running at save time has none and
        // starts its wait when it is next read.
        seedSince: DateTime option
        // The user closed this tab, as against the window simply not having
        // started yet. Only an exact title match may claim one of these.
        closedByUser: bool
    }

    let ofHwnd (hwnd: IntPtr) = {
        hwnd = hwnd
        exePath = None
        windowTitle = None
        rect = None
        renamedTabName = None
        isPinned = false
        fillColor = None
        underlineColor = None
        borderColor = None
        align = None
        seedSince = None
        closedByUser = false
    }

    let formatAlign = function AlignLeft -> "TopLeft" | AlignRight -> "TopRight"

    // "Left" and "Right" are accepted as well: they are what a settings file
    // written by an older version holds.
    let parseAlign (s: string) =
        match s with
        | "TopLeft" | "Left" -> Some(AlignLeft)
        | "TopRight" | "Right" -> Some(AlignRight)
        | _ -> None

    let formatRect (x, y, w, h) = sprintf "%d,%d,%d,%d" x y w h

    let parseRect (s: string) =
        match (if isNull s then "" else s).Split(',') with
        | [| xs; ys; ws; hs |] ->
            match Int32.TryParse xs, Int32.TryParse ys, Int32.TryParse ws, Int32.TryParse hs with
            | (true, x), (true, y), (true, w), (true, h) -> Some(x, y, w, h)
            | _ -> None
        | _ -> None

    // Keys are matched without regard to case, as everywhere else the settings
    // file is read: a file written by an older version may not agree on it.
    let private valueOf (o: JObject) (key: string) =
        o.Properties()
        |> Seq.tryFind (fun p -> String.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase))
        |> Option.map (fun p -> p.Value)

    let private scalarOf (o: JObject) (key: string) =
        match valueOf o key with
        | Some(:? JValue as v) when not (isNull v.Value) -> Some(v.Value)
        | _ -> None

    let private stringOf (o: JObject) (key: string) =
        match scalarOf o key with
        | Some(:? string as s) -> Some(s)
        | _ -> None

    let private boolOf (o: JObject) (key: string) =
        match scalarOf o key with
        | Some(:? bool as b) -> Some(b)
        | _ -> None

    let private int64Of (o: JObject) (key: string) =
        match scalarOf o key with
        | Some(:? int64 as i) -> Some(i)
        | Some(:? int as i) -> Some(int64 i)
        | _ -> None

    // A field is written only when it has something to say. An absent key and
    // a key holding the default mean the same thing to the reader, and leaving
    // the defaults out keeps the file readable by hand - which is how the
    // alignment ratchet was spotted in the first place.
    let toJson (t: SavedTab) : JObject =
        let o = JObject()
        let put (key: string) (v: JValue) = o.Add(key, v)
        put "hwnd" (JValue(t.hwnd.ToInt64()))
        t.exePath |> Option.iter (fun v -> put "exePath" (JValue(v)))
        t.windowTitle |> Option.iter (fun v -> put "windowTitle" (JValue(v)))
        t.rect |> Option.iter (fun r -> put "rect" (JValue(formatRect r)))
        t.renamedTabName |> Option.iter (fun v -> put "renamedTabName" (JValue(v)))
        if t.isPinned then put "isPinned" (JValue(true))
        t.fillColor |> Option.iter (fun v -> put "tabFillColor" (JValue(v)))
        t.underlineColor |> Option.iter (fun v -> put "tabUnderlineColor" (JValue(v)))
        t.borderColor |> Option.iter (fun v -> put "tabBorderColor" (JValue(v)))
        t.align |> Option.iter (fun a -> put "tabAlignment" (JValue(formatAlign a)))
        t.seedSince |> Option.iter (fun d -> put "seedSince" (JValue(d.ToString("o"))))
        if t.closedByUser then put "closedByUser" (JValue(true))
        o

    // Nothing without a handle is an entry: the handle is the name every other
    // part of the restore refers to it by (the group's saved order is a list of
    // them), so an object that has lost it cannot be placed at all.
    let ofJson (o: JObject) : SavedTab option =
        match int64Of o "hwnd" with
        | None -> None
        | Some(h) ->
            Some {
                hwnd = IntPtr(h)
                exePath = stringOf o "exePath"
                windowTitle = stringOf o "windowTitle"
                rect = stringOf o "rect" |> Option.bind parseRect
                renamedTabName = stringOf o "renamedTabName"
                isPinned = (boolOf o "isPinned" = Some(true))
                fillColor = stringOf o "tabFillColor"
                underlineColor = stringOf o "tabUnderlineColor"
                borderColor = stringOf o "tabBorderColor"
                align = stringOf o "tabAlignment" |> Option.bind parseAlign
                seedSince =
                    stringOf o "seedSince"
                    |> Option.bind (fun s ->
                        match DateTime.TryParse(s, Globalization.CultureInfo.InvariantCulture,
                                                Globalization.DateTimeStyles.RoundtripKind) with
                        | true, d -> Some(d)
                        | _ -> None)
                closedByUser = (boolOf o "closedByUser" = Some(true))
            }

    // ----- one saved group -----
    //
    // The group's own two settings live beside its window list, and the same
    // argument applies to them as to the fields above: the writer and the
    // reader belong together. Both file formats are read - the old one is a
    // bare array of windows with no group settings of its own, the new one an
    // object - and only the new one is ever written.

    let private windowsArrayOf (groupToken: JToken) =
        match groupToken with
        | :? JObject as g ->
            (match valueOf g "windows" with
             | Some(:? JArray as a) -> a
             | _ -> JArray())
        | :? JArray as a -> a
        | _ -> JArray()

    let groupWindows (groupToken: JToken) : SavedTab list =
        windowsArrayOf groupToken
        |> Seq.choose (fun t ->
            match t with
            | :? JObject as o -> ofJson o
            | _ -> None)
        |> List.ofSeq

    let groupTabPosition (groupToken: JToken) =
        match groupToken with
        | :? JObject as g -> stringOf g "tabPosition"
        | _ -> None

    let groupSnapMargin (groupToken: JToken) =
        match groupToken with
        | :? JObject as g -> boolOf g "snapTabHeightMargin"
        | _ -> None

    // The window array is passed in rather than built from a SavedTab list:
    // the save has to splice not-yet-started windows into it at the index they
    // held in the saved order, which is an operation on the array itself.
    let groupToJson (windows: JArray) (tabPosition: string option) (snapMargin: bool option) =
        let o = JObject()
        o.Add("windows", windows)
        tabPosition |> Option.iter (fun p -> o.Add("tabPosition", JValue(p)))
        snapMargin |> Option.iter (fun m -> o.Add("snapTabHeightMargin", JValue(m)))
        o
