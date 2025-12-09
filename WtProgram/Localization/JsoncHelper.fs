namespace Bemo
open System.Text.RegularExpressions
open Newtonsoft.Json.Linq

// JSONC (JSON with Comments) utility module
[<AutoOpen>]
module JsoncHelper =
    // Remove JSONC comments (// and /* */) from JSON string
    let removeJsoncComments(json: string) : string =
        // Remove single-line comments (// ...)
        let withoutSingleLine = Regex.Replace(json, @"//.*?(?=\r?\n|$)", "")
        // Remove multi-line comments (/* ... */)
        let withoutMultiLine = Regex.Replace(withoutSingleLine, @"/\*[\s\S]*?\*/", "")
        withoutMultiLine

    // Parse JSON string with JSONC support (JObject)
    let parseJsoncObject(json: string) : JObject =
        let cleanJson = removeJsoncComments(json)
        JObject.Parse(cleanJson)

    // Parse JSON string with JSONC support (JArray)
    let parseJsoncArray(json: string) : JArray =
        let cleanJson = removeJsoncComments(json)
        JArray.Parse(cleanJson)
