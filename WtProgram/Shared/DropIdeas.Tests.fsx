// TestRuntime: Framework
#r "../../Newtonsoft.Json.dll"
#load "TabOrder.fs"
#load "SavedTabState.fs"
#load "SnapGeometry.fs"
#load "TabBehaviorPolicy.fs"
#load "TabEdges.fs"
#load "TabEdgeLayout.fs"
#load "TabButtonPolicy.fs"
#r "System.Drawing"
#load "TabShape.fs"
#load "TabEdgeSettings.fs"
open System.IO
open Bemo
open Bemo.TabEdges
open Bemo.TabEdgeLayout
open Newtonsoft.Json.Linq
let mutable checks = 0
let equal label expected actual =
    checks <- checks + 1
    if expected <> actual then failwithf "%s: %A <> %A" label expected actual
for edge in [Edge.Left; Edge.Right] do
 for scale in [1.;1.25;1.5;2.] do
  let px n = int (float n * scale)
  let spec = {edge=edge;direction=Direction.Outside;across=true;thickness=px 200;minThickness=px 25;outsideDistance=(px 10,px 10);insideDistance=(px 150,px 150);heightOffset=0}
  let monitor = (0,0,px 1200,px 1000)
  let frame = if edge=Edge.Left then (0,0,px 600,px 1000) else (px 600,0,px 600,px 1000)
  let resized = place spec frame [monitor] []
  let maximized = place spec monitor [monitor] []
  let _,ry,rw,rh=resized.rect
  let _,my,mw,mh=maximized.rect
  equal "resize inside" true resized.inside
  equal "maximize inside" true maximized.inside
  equal "same icon width and end distances" (ry,rw,rh) (my,mw,mh)
  equal "inside width and distance" (px 150,px 25,px 1000 - 2 * px 150) (my,mw,mh)
for height in [16;24;32;48] do
 for overlap in [-8;0;4;100] do
  let rows=rowsOf id height overlap
  let strip = [placed 0 Edge.Left AlignEnd true;placed 1 Edge.Left AlignStart false;placed 2 Edge.Left AlignStart true;placed 3 Edge.Left AlignEnd false]
              |> normalize |> stripTabsOf Edge.Left
  let lengths={stripLength=1000.;tabOverlap=float rows.overlap;pinnedLength=float rows.length;unpinnedLength=float rows.length}
  let starts=tabStartsAt lengths strip
  equal "alignment zones preserve pins" [2;1;0;3] (starts |> List.map fst)
  equal "start zone rows" [(2,0);(1,rows.pitch)] (starts |> List.take 2)
  equal "no scroll when rows fit" (0,1000) (viewport rows 4 1000 14)
for mode in ["all";"activeOnly";"none"] do
 for active in [false;true] do
  for hover in [false;true] do
   let buttons=TabButtonPolicy.decide mode mode active hover true false true true
   equal "pin has no close button" false buttons.showClose

let clean=JObject.Parse("{\"TabEdges\":[\"Top\",\"Left\"]}")
for value in ["\"Right\"";"null";"{}";"42"] do
 let legacy=JObject.Parse("{\"TabEdges\":[\"Top\",\"Left\"],\"NewWindowTabEdge\":"+value+",\"TabEdgesHint\":true,\"RecheckRestoresEdge\":true}")
 equal "unknown root fields ignored" (TabEdgeSettings.allowedOf clean) (TabEdgeSettings.allowedOf legacy)
 for edge in priority do
  equal "unknown fields do not change edge settings" (TabEdgeSettings.valuesOf clean edge) (TabEdgeSettings.valuesOf legacy edge)
  equal "new arrival uses active edge" (Some edge) (edgeFor (TabEdges.allowedOf priority |> Option.get) (Joins(Some edge)))
for edge in priority do
 let before = [placed 0 edge AlignEnd true]
 let remaining = TabEdges.allowedOf (priority |> List.filter ((<>) edge)) |> Option.get
 let moved = moveOffUnchecked remaining before
 let rechecked = setChecked remaining edge true
 equal "recheck keeps destination" moved (moveOffUnchecked rechecked moved)
 equal "pin preserved after uncheck" true moved.Head.pinned
let source file=File.ReadAllText(Path.Combine(__SOURCE_DIRECTORY__,"..",file))
let ts=source "TabStrip.fs"
for removed in ["displayEdgeOf";"maximizedSidesCell";"hasCombinedTop";"packedDrop";"verticalScroll";"uncheckedReturns"] do
 equal ("removed "+removed) false (ts.Contains removed)
equal "wheel only across" true (ts.Contains "WM_MOUSEWHEEL when this.isAcross edge")
equal "pins retain alignment" false (ts.Contains "pinnedRowAlignment")
equal "orientation changes normalize order" true (ts.Contains "if wasAcross <> this.isAcross edge then this.normalizeVisualOrder()")
let program=source "Program.fs"
equal "new windows join active edge" true (program.Contains "Option.defaultValue wg.joinEdge")
let decorator=source "TabStripDecorator.fs"
let first=decorator.IndexOf("member private this.updateSidePlacements")
let last=decorator.IndexOf("member private this.edgeAppearance",first)
let placement=decorator.Substring(first,last-first)
equal "one placement calculation" true (placement.Contains "TabEdgeLayout.place spec frame monitors")
equal "no maximize placement branch" false (placement.Contains "isMaximized")
for file in Directory.GetFiles(Path.Combine(__SOURCE_DIRECTORY__,"..","Settings","Language"),"*.json") |> Array.filter (fun p -> Path.GetFileName(p) <> "FileList.json") do
 let json=JObject.Parse(File.ReadAllText file)
 for key in ["NewWindowTabEdge";"NewWindowTabEdgeActive";"TabEdgesHint"] do
  equal "removed translation" null (json.[key])
printfn "DropIdeas: %d checks passed" checks
