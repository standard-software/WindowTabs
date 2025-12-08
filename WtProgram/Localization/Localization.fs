namespace Bemo
open System
open System.IO
open System.Reflection
open System.Collections.Generic
open Newtonsoft.Json.Linq

module Localization =
    // Current language stored as string (e.g., "English", "Japanese")
    let mutable currentLanguage = "English"

    // Loaded strings from JSON file (if any)
    let mutable private loadedStrings : IDictionary<string, string> option = None

    let languageChanged = Event<unit>()

    // Normalize old format ("en"/"ja") to new format ("English"/"Japanese")
    let normalizeLanguageString(langStr: string) =
        match langStr with
        | "en" -> "English"
        | "ja" -> "Japanese"
        | other -> other

    // Get the Language folder path
    let getLanguageFolder() =
        let exePath = Assembly.GetExecutingAssembly().Location
        let exeDir = Path.GetDirectoryName(exePath)
        Path.Combine(exeDir, "Language")

    // Load language strings from JSON file
    let loadLanguageFromJson(langName: string) =
        try
            let jsonPath = Path.Combine(getLanguageFolder(), langName + ".json")
            if File.Exists(jsonPath) then
                let json = File.ReadAllText(jsonPath)
                let jobj = JObject.Parse(json)
                let dict = Dictionary<string, string>()
                for prop in jobj.Properties() do
                    dict.[prop.Name] <- prop.Value.ToString()
                Some(dict :> IDictionary<string, string>)
            else
                None
        with
        | _ -> None

    let setLanguage(langStr: string) =
        let normalized = normalizeLanguageString(langStr)
        if currentLanguage <> normalized then
            currentLanguage <- normalized
            // Try to load from JSON file
            loadedStrings <- loadLanguageFromJson(normalized)
            languageChanged.Trigger()

    // Initialize language (called at startup)
    let initLanguage(langStr: string) =
        let normalized = normalizeLanguageString(langStr)
        currentLanguage <- normalized
        loadedStrings <- loadLanguageFromJson(normalized)

    let getString(key: string) =
        // First, try to get from loaded JSON strings
        match loadedStrings with
        | Some(dict) ->
            match dict.TryGetValue(key) with
            | true, value -> value
            | false, _ ->
                // Fallback to Localization_English.fs
                match Localization_English.strings.TryGetValue(key) with
                | true, value -> value
                | false, _ -> key
        | None ->
            // No JSON loaded, use built-in English dictionary
            match Localization_English.strings.TryGetValue(key) with
            | true, value -> value
            | false, _ -> key
