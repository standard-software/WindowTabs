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
equal "old workspace has no override" None oldGroup.snapTabMarginMode
let baseline = oldGroup.serialize() |> string |> JObject.Parse
equal "old workspace still omits key" true (isNull baseline.["snapTabHeightMargin"])
for margin in [true; false] do
    oldGroup.snapTabMarginMode <- Some(SnapTabMarginMode.ofLegacy margin)
    let json = oldGroup.serialize() |> string |> JObject.Parse
    equal "boolean type" JTokenType.Boolean json.["snapTabHeightMargin"].Type
    equal "restart reader agrees" (Some margin) (SavedTabState.groupSnapMargin json)
    let restored = WorkspaceGroup.deserialize json
    equal "workspace round trip" (Some(SnapTabMarginMode.ofLegacy margin)) restored.snapTabMarginMode
    equal "second save stable" true (JToken.DeepEquals(json, restored.serialize()))
    json.Remove("snapTabHeightMargin") |> ignore
    json.Remove("snapTabMarginMode") |> ignore
    equal "legacy fields unchanged" true (JToken.DeepEquals(baseline, json))
for token in ["null"; "\"invalid\""; "{}"; "[]"] do
    let json = baseline.DeepClone() :?> JObject
    json.["snapTabHeightMargin"] <- JToken.Parse(token)
    equal "invalid value uses default" None (WorkspaceGroup.deserialize json).snapTabMarginMode
printfn "WorkspaceSnapMargin: %d checks, 0 failures" checks



for mode,_ in SnapTabMarginMode.choices do
    oldGroup.snapTabMarginMode <- Some mode
    let obj = oldGroup.serialize() |> string |> JObject.Parse
    equal "workspace full mode round trip" (Some mode) (WorkspaceGroup.deserialize obj).snapTabMarginMode
    equal "workspace agrees with restart mode" (Some mode) (SavedTabState.groupSnapMarginMode obj)
    equal "workspace legacy boolean" (Some(SnapTabMarginMode.enabled mode)) (SavedTabState.groupSnapMargin obj)

// Exercise the real one-shot helper without showing a form or pumping events.
// OnTick is invoked directly; disposal must precede even a throwing callback.
let mutable disposed = false
let mutable calls = 0
let timer = ThreadHelper.cancelablePostBack 60000 (fun () ->
    calls <- calls+1
    equal "disposed before callback" true disposed
    raise (System.InvalidOperationException("timer-1"))) :?> System.Windows.Forms.Timer
timer.Disposed.Add(fun _ -> disposed <- true)
let onTick = typeof<System.Windows.Forms.Timer>.GetMethod("OnTick", System.Reflection.BindingFlags.Instance ||| System.Reflection.BindingFlags.NonPublic)
let mutable threw = false
try onTick.Invoke(timer,[|box System.EventArgs.Empty|]) |> ignore
with :? System.Reflection.TargetInvocationException as ex -> threw <- ex.InnerException :? System.InvalidOperationException
equal "callback exception remains visible" true threw
equal "callback ran once" 1 calls
equal "timer disabled after exception" false timer.Enabled
equal "timer disposed after exception" true disposed
timer.Dispose()
printfn "Workspace modes and timer: %d checks, 0 failures" checks

// Check every translated choice using the production combo and width rule.
// Controls stay off screen; no settings service or application is started.
let languageRoot = System.IO.Path.Combine(__SOURCE_DIRECTORY__, "../Settings/Language")
let mutable languageCount = 0
for file in System.IO.Directory.GetFiles(languageRoot,"*.json") do
    if System.IO.Path.GetFileNameWithoutExtension(file) <> "FileList" then
        languageCount <- languageCount+1
        let json = JObject.Parse(System.IO.File.ReadAllText(file))
        equal "retired checkbox caption removed" true (isNull json.["SnapTabHeightMargin"])
        equal "retired menu caption removed" true (isNull json.["SnapTabMarginTop"])
        equal "mode caption present" false (isNull json.["SnapTabMarginMode"])
        let captions = SnapTabMarginMode.choices |> List.map (fun (_,key) ->
            equal "choice caption present" false (isNull json.[key])
            string json.[key])
        for scale in [1.0;1.25;1.5;1.75;2.0] do
            SettingsDpi.setCurrent scale
            use combo = EdgeRows.choice captions
            combo.Font <- defaultArg (SettingsDpi.font scale) System.Windows.Forms.Control.DefaultFont
            EdgeRows.fitChoiceWidth combo
            combo.Width <- combo.MinimumSize.Width
            for caption in captions do
                let needed = System.Windows.Forms.TextRenderer.MeasureText(caption,combo.Font).Width + SettingsDpi.px 24
                equal "closed choice fits full text and arrow" true (combo.Width >= needed)
                equal "dropdown fits full text and arrow" true (combo.DropDownWidth >= needed)
equal "all language dictionaries checked" 16 languageCount
SettingsDpi.setCurrent 1.0
printfn "Workspace modes, timer and localized widths: %d checks, 0 failures" checks

// Placement coordinate conversion is pure: no native windows are created.
let rect x y w h = RECT(Left=x,Top=y,Right=x+w,Bottom=y+h)
let box (r: RECT) = r.Left,r.Top,r.Width,r.Height
for monitor,work,bounds,expected in [
    rect 0 0 1000 800,rect 0 0 1000 760,rect 0 24 500 736,(0,24,500,736)
    rect 0 0 1000 800,rect 40 32 960 768,rect 40 56 480 744,(0,24,480,744)
    rect -1200 -900 1200 900,rect -1160 -868 1160 868,rect -1160 -844 580 844,(-1200,-876,580,844)
    rect 1500 0 1500 1200,rect 1560 48 1440 1152,rect 1560 84 720 1116,(1500,36,720,1116)] do
    equal "screen to workspace keeps size and subtracts taskbar offset" expected
        (Win32Helper.ScreenBoundsToNormalPlacement(bounds,monitor,work,false) |> box)
    equal "tool window keeps screen coordinates" (box bounds)
        (Win32Helper.ScreenBoundsToNormalPlacement(bounds,monitor,work,true) |> box)
printfn "Placement coordinate conversion: 8 checks, 0 failures"
