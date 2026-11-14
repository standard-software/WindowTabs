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
    if expected <> actual then failwithf "%s: %A <> %A" label expected actual
for sourceEdge, destination in [Edge.Top, Edge.Bottom; Edge.Left, Edge.Right; Edge.Right, Edge.Left] do
    for orientation in [SideText.Horizontal; SideText.Vertical] do
        for shape in TabShape.all do
            for align in [AlignStart; AlignEnd] do
                for direction in [Direction.Outside; Direction.AlwaysInside] do
                    let root = JObject.Parse("{\"TabAppearance\":{\"tabHeight\":31},\"TabEdges\":[\"Top\"]}")
                    TabEdgeSettings.writeString root destination "tabText" (TabEdgeSettings.textName orientation)
                    TabEdgeSettings.writeString root destination "other" "title-1"
                    let before = root.ToString()
                    let source = { TabEdgeSettings.noValues with
                                      tabHeight = Some 31; tabMaxWidth = Some 234
                                      tabPinnedTabWidth = Some 92; tabPinnedTabWidthIcon = Some true
                                      tabOverlap = Some 0; tabShape = Some shape
                                      text = Some(if orientation = SideText.Horizontal then SideText.Vertical else SideText.Horizontal)
                                      align = Some align; direction = Some direction
                                      outsideStart = Some 12; outsideEnd = Some 23
                                      insideStart = Some 134; insideEnd = Some 145 }
                    let result = TabEdgeSettings.copyOppositeValues root sourceEdge source
                    let expectedText = if (sourceEdge = Edge.Left || sourceEdge = Edge.Right) then source.text else Some orientation
                    if not (runsHorizontally destination) then
                        equal "raw orientation retained by copy" (TabEdgeSettings.textName source.text.Value) (string result.[TabEdgeSettings.edgeKey destination].["tabText"])
                    equal "all requested values copied without conversion" { source with text = expectedText } (TabEdgeSettings.valuesOf result destination)
                    if (sourceEdge = Edge.Left || sourceEdge = Edge.Right) && List.contains shape (TabEdgeSettings.shapesFor source.text) then
                        equal "copied shape remains displayable" shape (TabEdgeSettings.shapeFor (TabEdgeSettings.valuesOf result destination).text shape)
                    equal "input remains unchanged" before (root.ToString())
                    equal "top unchanged" (root.["TabAppearance"].ToString()) (result.["TabAppearance"].ToString())
                    equal "allowed unchanged" [Edge.Top] (TabEdgeSettings.listedOf result)
                    equal "unrelated value retained" "title-1" (string result.[TabEdgeSettings.edgeKey destination].["other"])
                    equal "repeat stable" (result.ToString()) ((TabEdgeSettings.copyOppositeValues result sourceEdge source).ToString())
let fresh = JObject()
let result = TabEdgeSettings.copyOppositeValues fresh Edge.Top {TabEdgeSettings.noValues with tabShape = Some TabShape.scurve; tabPinnedTabWidthIcon = Some false}
equal "unset text remains unset" None (TabEdgeSettings.valuesOf result Edge.Bottom).text
equal "shape unchanged" (Some TabShape.scurve) (TabEdgeSettings.valuesOf result Edge.Bottom).tabShape
equal "specified pinned width mode" (Some false) (TabEdgeSettings.valuesOf result Edge.Bottom).tabPinnedTabWidthIcon
printfn "CopyEdgeAppearance: %d checks passed" checks

for align in [AlignStart; AlignEnd] do
    for direction in [Direction.Outside; Direction.AlwaysInside] do
        let root = JObject.Parse("{\"tabappearance\":{\"tabHeight\":20,\"other\":7},\"tabpositionbydefault\":\"TopLeft\",\"tabverticaldirection\":\"auto\",\"TabEdges\":[\"Bottom\"]}")
        let before = root.ToString()
        let source = { TabEdgeSettings.noValues with
                          tabHeight = Some 32; tabMaxWidth = Some 241
                          tabPinnedTabWidth = Some 95; tabPinnedTabWidthIcon = Some false
                          tabOverlap = Some 19; tabShape = Some TabShape.rectangle
                          align = Some align; direction = Some direction
                          outsideStart = Some 13; outsideEnd = Some 24
                          insideStart = Some 135; insideEnd = Some 146 }
        let result = TabEdgeSettings.copyOppositeValues root Edge.Bottom source
        let a = result.["tabappearance"]
        for key, value in ["tabHeight",32; "tabMaxWidth",241; "tabPinnedTabWidth",95; "tabOverlap",19;
                           "tabIndentNormal",13; "tabIndentNormalRight",24; "tabIndentFlipped",135; "tabIndentFlippedRight",146] do
            equal key value (int a.[key])
        equal "pinned mode" false (a.["tabPinnedTabWidthIcon"].ToObject<bool>())
        equal "shape" TabShape.rectangle (string a.["tabShape"])
        equal "alignment" (TabEdgeSettings.alignName align) (string result.["tabpositionbydefault"])
        equal "direction uses legacy top values" (if direction = Direction.AlwaysInside then "down" else "auto") (string result.["tabverticaldirection"])
        equal "unrelated appearance retained" 7 (int a.["other"])
        equal "no separate top object" true (isNull result.["TabEdgeTop"])
        equal "no duplicate case variant" true (isNull result.["TabAppearance"])
        equal "allowed unchanged" [Edge.Bottom] (TabEdgeSettings.listedOf result)
        equal "source unchanged" before (root.ToString())
        equal "repeat stable" (result.ToString()) ((TabEdgeSettings.copyOppositeValues result Edge.Bottom source).ToString())
printfn "CopyEdgeAppearance including reverse directions: %d checks passed" checks
