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
// of false keeps an entry while turning it off. Read once at startup.
module FramelessWindowSettings =
    [<Literal>]
    let fileName = "FramelessWindow.json"

    let enabledNames (parsed: JObject option) =
        let names = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        parsed |> Option.iter (fun value ->
            for prop in value.Properties() do
                if prop.Value.Type = JTokenType.Boolean && prop.Value.Value<bool>() then
                    names.Add(prop.Name) |> ignore)
        names

    // FilterService takes one startup snapshot; no file IO in window enumeration.
    let load () =
        try
            UserOverrides.loadObjectIgnoreCase UserOverrides.settingsDir fileName
            |> enabledNames
        with _ ->
            System.Diagnostics.Debug.WriteLine("[FramelessWindow] could not load settings")
            enabledNames None
