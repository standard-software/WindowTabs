namespace Bemo
open System

module Localization =
    // Current language stored as string (e.g., "English", "Japanese")
    // This allows dynamic language support via JSON files in the future
    let mutable currentLanguage = "English"

    let languageChanged = Event<unit>()

    // Normalize old format ("en"/"ja") to new format ("English"/"Japanese")
    let normalizeLanguageString(langStr: string) =
        match langStr with
        | "en" -> "English"
        | "ja" -> "Japanese"
        | other -> other

    let setLanguage(langStr: string) =
        let normalized = normalizeLanguageString(langStr)
        if currentLanguage <> normalized then
            currentLanguage <- normalized
            languageChanged.Trigger()

    let getString(key: string) =
        let strings =
            match currentLanguage with
            | "Japanese" -> Localization_ja.strings
            | _ -> Localization_en.strings  // Default to English

        match strings.TryGetValue(key) with
        | true, value -> value
        | false, _ ->
            // Fallback to English if key not found in current language
            match Localization_en.strings.TryGetValue(key) with
            | true, value -> value
            | false, _ -> key  // Last resort: return the key itself
