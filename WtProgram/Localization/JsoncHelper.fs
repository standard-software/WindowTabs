namespace Bemo
open System.Text
open Newtonsoft.Json.Linq

// JSONC (JSON with Comments) utility module
[<AutoOpen>]
module JsoncHelper =
    // Remove JSONC comments (// and /* */) from JSON string.
    //
    // String literals are copied out verbatim. The settings file stores
    // window titles, and a title carrying a URL ("https://t.co/xxx") used to
    // lose everything from the "//" to the end of the line: that broke the
    // JSON for the WHOLE file, the read fell back to empty settings, and the
    // next periodic save wrote that empty state over the user's settings.
    let removeJsoncComments(json: string) : string =
        let sb = StringBuilder(json.Length)
        let mutable i = 0
        while i < json.Length do
            let c = json.[i]
            if c = '"' then
                // Inside a string literal nothing is a comment. Backslash
                // escapes are copied as a pair so that \" does not end it.
                sb.Append(c) |> ignore
                i <- i + 1
                let mutable inString = true
                while inString && i < json.Length do
                    let s = json.[i]
                    sb.Append(s) |> ignore
                    i <- i + 1
                    if s = '\\' && i < json.Length then
                        sb.Append(json.[i]) |> ignore
                        i <- i + 1
                    elif s = '"' then
                        inString <- false
            elif c = '/' && i + 1 < json.Length && json.[i + 1] = '/' then
                // Line comment: replaced by a space. The newline that ends it
                // is left for the copy below, so a parse error still names the
                // line it has in the original file.
                while i < json.Length && json.[i] <> '\n' do i <- i + 1
                sb.Append(' ') |> ignore
            elif c = '/' && i + 1 < json.Length && json.[i + 1] = '*' then
                // Block comment: replaced by a space - dropping it outright
                // would turn "1/* c */2" into "12" - followed by one newline
                // per line it spanned, so everything after it keeps the line
                // number it has in the original file.
                let commentStart = i
                i <- i + 2
                while i + 1 < json.Length && not (json.[i] = '*' && json.[i + 1] = '/') do i <- i + 1
                i <- if i + 1 < json.Length then i + 2 else json.Length
                sb.Append(' ') |> ignore
                for k in commentStart .. i - 1 do
                    if json.[k] = '\n' then sb.Append('\n') |> ignore
            else
                sb.Append(c) |> ignore
                i <- i + 1
        sb.ToString()

    // Parse JSON string with JSONC support (JObject)
    let parseJsoncObject(json: string) : JObject =
        let cleanJson = removeJsoncComments(json)
        JObject.Parse(cleanJson)

    // Parse JSON string with JSONC support (JArray)
    let parseJsoncArray(json: string) : JArray =
        let cleanJson = removeJsoncComments(json)
        JArray.Parse(cleanJson)
