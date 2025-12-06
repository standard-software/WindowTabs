namespace Bemo
open System

module Localization =
    type Language =
        | English
        | Japanese

    let mutable private currentLanguage = English

    let languageChanged = Event<unit>()

    let getCurrentLanguage() =
        currentLanguage

    let setLanguage(lang: Language) =
        if currentLanguage <> lang then
            currentLanguage <- lang
            languageChanged.Trigger()

    // Normalize old format ("en"/"ja") to new format ("English"/"Japanese")
    let normalizeLanguageString(langStr: string) =
        match langStr with
        | "en" -> "English"
        | "ja" -> "Japanese"
        | other -> other

    let setLanguageByString(langStr: string) =
        match normalizeLanguageString(langStr) with
        | "Japanese" -> setLanguage(Japanese)
        | "English" -> setLanguage(English)
        | _ -> setLanguage(English)

    let getLanguageString() =
        match currentLanguage with
        | English -> "English"
        | Japanese -> "Japanese"

    let getString(key: string) =
        let strings =
            match currentLanguage with
            | English -> Localization_en.strings
            | Japanese -> Localization_ja.strings

        match strings.TryGetValue(key) with
        | true, value -> value
        | false, _ ->
            // Fallback to English if key not found in current language
            match Localization_en.strings.TryGetValue(key) with
            | true, value -> value
            | false, _ -> key  // Last resort: return the key itself
