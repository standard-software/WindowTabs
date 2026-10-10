namespace Bemo

open System
open System.Collections.Generic
open Newtonsoft.Json.Linq

// Which applications draw their whole window themselves, read from
// Settings\FramelessWindow.json beside the executable, with the user's own
// under %APPDATA%\WindowTabs\Settings laid over it name by name:
//
//     {
//       "example.exe": true
//     }
//
// Such a window (GTK 3 with its own header bar, for one) carries no caption,
// frame or minimise and maximise box, so by its styles alone it cannot be
// told from a splash screen or a popup. Only the executables named here get
// tabs on windows like that; everything else is judged as before. A value
// of false keeps an entry while turning it off. Objects enable the entry
// with left/top/right/bottom interior insets (0..200 logical pixels), unless
// enabled is false. A true override replaces the object with zero insets.
// Read once at startup.
module FramelessWindowSettings =
    [<Literal>]
    let fileName = "FramelessWindow.json"

    // Values are logical pixels at 96 DPI. Fractional values round down.
    let private side (value: JToken) =
        if isNull value then 0
        elif value.Type = JTokenType.Integer || value.Type = JTokenType.Float then
            try
                let number = value.Value<double>()
                if Double.IsNaN number || number < 0.0 then 0
                else int (min 200.0 number)
            with _ -> 0
        else 0

    let parseEntry (value: JToken) : FramelessWindow.Insets option =
        if isNull value then None
        else
            match value with
            | :? JObject as entry ->
                let enabled = entry.["enabled"]
                if not (isNull enabled) && enabled.Type = JTokenType.Boolean && not (enabled.Value<bool>()) then None
                else Some { left=side entry.["left"]; top=side entry.["top"]
                            right=side entry.["right"]; bottom=side entry.["bottom"] }
            | _ when value.Type = JTokenType.Boolean && value.Value<bool>() -> Some FramelessWindow.zeroInsets
            | _ -> None

    let parse (parsed: JObject option) =
        let entries = Dictionary<string, FramelessWindow.Insets>(StringComparer.OrdinalIgnoreCase)
        parsed |> Option.iter (fun value ->
            for prop in value.Properties() do
                match parseEntry prop.Value with
                | Some insets -> entries.[prop.Name] <- insets
                | None -> entries.Remove(prop.Name) |> ignore)
        entries

    let enabledNames parsed = HashSet<string>((parse parsed).Keys, StringComparer.OrdinalIgnoreCase)

    // Filtering and geometry share the same startup snapshot, including false
    // and true overrides that replace an entire shipped object by name.
    let private snapshot = lazy (
        try UserOverrides.loadObjectIgnoreCase UserOverrides.settingsDir fileName |> parse
        with _ ->
            System.Diagnostics.Debug.WriteLine("[FramelessWindow] could not load settings")
            parse None)

    let load () = HashSet<string>(snapshot.Value.Keys, StringComparer.OrdinalIgnoreCase)
    let hasInsets () = snapshot.Value.Values |> Seq.exists ((<>) FramelessWindow.zeroInsets)
    let insetsForName name =
        match snapshot.Value.TryGetValue name with
        | true, value -> value
        | _ -> FramelessWindow.zeroInsets
