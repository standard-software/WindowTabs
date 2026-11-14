// TestRuntime: Framework
// Run in Framework FSI with --lib:WtProgram/bin/Debug; no application startup.
#r "System.Drawing"
#r "System.Windows.Forms"
#r "Newtonsoft.Json.dll"
#r "FSharp.PowerPack.dll"
#r "Aga.Controls.dll"
#r "Win32.dll"
#r "WindowTabs.exe"
open Bemo
open Newtonsoft.Json.Linq
let mutable checks = 0
let equal label expected actual =
    checks <- checks + 1
    if expected <> actual then failwithf "%s: expected %A, got %A" label expected actual
let legacy = JObject.Parse("""{"name":"group-1","placement":{"showCmd":1,"x":10,"y":20,"width":300,"height":200},"windows":[]}""")
let oldGroup = WorkspaceGroup.deserialize legacy
equal "old workspace has no override" None oldGroup.snapTabHeightMargin
let baseline = oldGroup.serialize() |> string |> JObject.Parse
equal "old workspace still omits key" true (isNull baseline.["snapTabHeightMargin"])
for margin in [true; false] do
    oldGroup.snapTabHeightMargin <- Some margin
    let json = oldGroup.serialize() |> string |> JObject.Parse
    equal "boolean type" JTokenType.Boolean json.["snapTabHeightMargin"].Type
    equal "restart reader agrees" (Some margin) (SavedTabState.groupSnapMargin json)
    let restored = WorkspaceGroup.deserialize json
    equal "workspace round trip" (Some margin) restored.snapTabHeightMargin
    equal "second save stable" true (JToken.DeepEquals(json, restored.serialize()))
    json.Remove("snapTabHeightMargin") |> ignore
    equal "legacy fields unchanged" true (JToken.DeepEquals(baseline, json))
for token in ["null"; "\"invalid\""; "{}"; "[]"] do
    let json = baseline.DeepClone() :?> JObject
    json.["snapTabHeightMargin"] <- JToken.Parse(token)
    equal "invalid value uses default" None (WorkspaceGroup.deserialize json).snapTabHeightMargin
printfn "WorkspaceSnapMargin: %d checks, 0 failures" checks


