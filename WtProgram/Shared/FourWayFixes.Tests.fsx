// TestRuntime: Framework
#r "../../Newtonsoft.Json.dll"
#load "TabOrder.fs"
#load "SavedTabState.fs"
#load "SnapGeometry.fs"
#load "TabBehaviorPolicy.fs"
#load "TabEdges.fs"
#r "System.Drawing"
#load "TabShape.fs"
#load "TabEdgeSettings.fs"
open Bemo
open Bemo.TabEdges
open Newtonsoft.Json.Linq
let mutable checks = 0
let equal label expected actual =
    checks <- checks + 1
    if actual <> expected then failwithf "%s: expected %A, got %A" label expected actual
for edge in priority do
    equal "menu axis" (if edge = Edge.Left || edge = Edge.Right then "v" else "h") (menuKey edge "h" "v")
    equal "saved edge round trip" edge (edge |> savedOfEdge |> edgeOfSaved)
for top in [AlignStart; AlignEnd] do
    for left in [AlignStart; AlignEnd] do
        for right in [AlignStart; AlignEnd] do
            for bottom in [AlignStart; AlignEnd] do
                let defaults = Map.ofList [Edge.Left,left; Edge.Right,right; Edge.Bottom,bottom]
                equal "top keeps group default" top (defaultAlignment Edge.Top top defaults)
                equal "left uses panel" left (defaultAlignment Edge.Left top defaults)
                equal "right uses panel" right (defaultAlignment Edge.Right top defaults)
                equal "bottom uses panel" bottom (defaultAlignment Edge.Bottom top defaults)
let tabs =
    [for edge in priority do
        for align in [AlignStart; AlignEnd] do
            for pin in [false;true] do
                for n in [0;1] do
                    yield placed (rank edge,align,pin,n) edge align pin]
for allowed in everyAllowed do
    for order in [tabs; List.rev tabs; normalize tabs] do
        let restored =
            order |> residentsFirst allowed (fun t -> t.edge)
            |> List.map (fun t -> {t with edge = fallback allowed t.edge})
            |> normalize
        equal "restore matches unchecked-edge migration" (moveOffUnchecked allowed order) restored
        equal "restore retains members" (order |> List.map (fun t -> t.id) |> Set.ofList) (restored |> List.map (fun t -> t.id) |> Set.ofList)
        for t in restored do equal "restored edge allowed" true (isAllowed allowed t.edge)
let topTabs = tabs |> List.filter (fun t -> t.edge = Edge.Top)
equal "top restore order unchanged" topTabs (residentsFirst topOnly (fun t -> t.edge) topTabs)
for t in topTabs do equal "top zones unchanged" (TabOrder.zoneOf (t.align = AlignStart) t.pinned) (zoneOfPlaced t)
let root = JObject.Parse("""{"TabEdgeLeft":{"tabAlignment":"TopLeft"},"TabEdgeRight":{"tabAlignment":"TopRight"},"TabEdgeBottom":{"tabAlignment":"TopLeft"}}""")
let defaults = [Edge.Left;Edge.Right;Edge.Bottom] |> List.map(fun edge -> edge, (TabEdgeSettings.edgeLayout root edge).defaultAlign) |> Map.ofList
for edge,expected in [Edge.Left,AlignStart;Edge.Right,AlignEnd;Edge.Bottom,AlignStart] do
    equal "panel values reach missing alignment" expected (defaultAlignment edge AlignEnd defaults)
printfn "%d checks, 0 failures" checks
let keys = ["NewTabBelowInGroup"; "CloseTabsBelowFormat"; "CloseTabsAboveFormat"; "AlignAllToTopFormat"; "AlignAllToBottomFormat"; "AlignThisTabTop"; "AlignThisTabBottom"; "AlignSelectedTabsTopFormat"; "AlignSelectedTabsTopPlusTargetFormat"; "AlignSelectedTabsBottomFormat"; "AlignSelectedTabsBottomPlusTargetFormat"]
let languageDir = System.IO.Path.Combine(__SOURCE_DIRECTORY__, "../Settings/Language")
let files = System.IO.Directory.GetFiles(languageDir, "*.json") |> Array.filter(fun p -> System.IO.Path.GetFileName(p) <> "FileList.json")
equal "language count" 16 files.Length
for file in files do
    let text = System.IO.File.ReadAllText file
    let json = JObject.Parse text
    for key in keys do
        equal "translated key exists" true (json.[key] <> null && not (System.String.IsNullOrWhiteSpace(string json.[key])))
        equal "key unique" 1 (System.Text.RegularExpressions.Regex.Matches(text, "\"" + key + "\"\\s*:").Count)
        if key.EndsWith("Format") then equal "format placeholder" true ((string json.[key]).Contains("{0}"))
    for key in ["SnapTabMarginMode"; "SnapTabMarginNone"; "SnapTabMarginWindowTabs"; "SnapTabMarginAll"] do
        equal "snap mode caption exists" true (json.[key] <> null && not (System.String.IsNullOrWhiteSpace(string json.[key])))
    equal "old snap checkbox caption removed" true (isNull json.["SnapTabHeightMargin"])
    equal "old snap menu caption removed" true (isNull json.["SnapTabMarginTop"])
let english = System.IO.File.ReadAllText(System.IO.Path.Combine(__SOURCE_DIRECTORY__, "../Localization/Localization_English.fs"))
for key in keys do equal "English fallback exists" true (english.Contains("(\"" + key + "\","))
printfn "Total: %d checks, 0 failures" checks
