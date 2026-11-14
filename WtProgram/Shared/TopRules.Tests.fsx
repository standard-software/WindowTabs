// TestRuntime: Framework
#r "System.Drawing"
#r "System.Windows.Forms"
#r "../../Aga.Controls.dll"
#r "FSharp.PowerPack.dll"
#r "../../Newtonsoft.Json.dll"
#r "Win32.dll"
// Load pure production functions only; never start the application.
#r "WindowTabs.exe"
open System
open Bemo
open Bemo.TabEdges
let mutable checks = 0
let equal label expected actual =
    checks <- checks + 1
    if expected <> actual then failwithf "%s: expected %A, got %A" label expected actual
let rect (x,y,w,h) = Rect(Pt(x,y), Sz(w,h))
let box (r: Rect) = r.x,r.y,r.width,r.height
let decorator frame monitors h offset left right =
    {windowBounds=rect frame; monitorBounds=List2(monitors |> List.map rect)
     decoratorHeight=h; decoratorHeightOffset=offset
     decoratorIndentNormal=left; decoratorIndentNormalRight=right
     decoratorIndentFlipped=15; decoratorIndentFlippedRight=25}
let legacy (d: WindowDecorator) =
    let f=d.windowBounds
    let outside=rect(f.x+d.decoratorIndentNormal,f.y-d.decoratorHeight+d.decoratorHeightOffset,
                     f.width-d.decoratorIndentNormal-d.decoratorIndentNormalRight,d.decoratorHeight)
    d.monitorBounds.list |> List.exists(fun m ->
        let overlap (s: Rect) =
            if min m.right s.right > max m.left s.left then max 0 (min m.bottom s.bottom - max m.top s.top) else 0
        overlap d.insideBounds > overlap outside)
for visible in [0;50;89;90;91;100] do
    let monitors=[(10,-200,visible,200);(0,0,200,200)]
    let d=decorator (0,0,130,100) monitors 20 0 10 20
    equal "old stacked decision" true (legacy d)
    equal "stacked threshold" (visible < 90) (d.showInside "auto")
    equal "explicit inside wins" true (d.showInside "down")
    if visible >= 90 then equal "existing outside dimensions" (10,-20,100,20) (box(d.boundsFor "auto"))
    for iteration in 1..100 do
        equal "stable repeated decision" (d.boundsFor "auto") (d.boundsFor "auto")
        equal "monitor order independent" (d.boundsFor "auto")
            ((decorator (0,0,130,100) (List.rev monitors) 20 0 10 20).boundsFor "auto")
let offsetMonitors=[(-200,-200,200,400);(0,0,200,200)]
for x,expected in [(-100,false);(-90,false);(-89,true)] do
    let d={decorator (x,0,100,100) offsetMonitors 20 0 0 0 with decoratorIndentFlipped=0;decoratorIndentFlippedRight=0}
    equal "side by side offset tops" expected d.shouldShowInside
let partial=decorator (0,0,130,100) [(10,-19,100,19);(0,0,200,200)] 20 0 10 20
equal "full thickness required" true partial.shouldShowInside
let split=decorator (0,0,130,100) [(10,-200,50,200);(60,-200,50,200);(0,0,200,200)] 20 0 10 20
equal "coverage not pooled" true split.shouldShowInside
let overlap=decorator (0,0,130,100) [(0,-200,200,205);(0,0,200,200)] 20 5 10 20
equal "height offset preserved" (10,-15,100,20) (box(overlap.boundsFor "auto"))
for x in [-150;-20;0;20;150;220] do
 for y in [-50;0;1;19;20;25;190;210] do
  for h in [20;30;50] do
   for offset in [0;5;10] do
    let d=decorator (x,y,130,100) [(0,0,200,200)] h offset 10 20
    equal "single monitor unchanged" (legacy d) d.shouldShowInside
let appearance =
    let fields=Microsoft.FSharp.Reflection.FSharpType.GetRecordFields typeof<TabAppearanceInfo>
    let values=fields |> Array.map(fun p -> if p.PropertyType.IsValueType then Activator.CreateInstance(p.PropertyType) else null)
    let empty=Microsoft.FSharp.Reflection.FSharpValue.MakeRecord(typeof<TabAppearanceInfo>,values) :?> TabAppearanceInfo
    {empty with tabHeight=25;tabMaxWidth=200;tabIndentNormal=10;tabIndentNormalRight=10}
for enabled in [false;true] do
 for direction in ["auto";"down";"unknown"] do
  for scale in [1.0;1.5;2.0] do
   for used in [[Edge.Top];[Edge.Top;Edge.Left];[Edge.Left];priority] do
    let reserve sideDirection =
        let values=priority |> List.map(fun edge -> edge,{TabEdgeSettings.noValues with direction=Some sideDirection}) |> Map.ofList
        TabStripDecorator.snapReserveAt(used,values,appearance,appearance,scale,enabled,direction)
    let outside=reserve Direction.Outside
    let inside=reserve Direction.AlwaysInside
    let expected=if enabled && direction <> "down" && List.contains Edge.Top used then (Dpi.scaleAppearance scale appearance).tabHeight-1 else 0
    equal "top direction and occupancy" expected outside.top
    equal "top uses its own setting" expected inside.top
    equal "left inside zero" 0 inside.left
    equal "right inside zero" 0 inside.right
    equal "bottom inside zero" 0 inside.bottom
    for edge,amount in [Edge.Left,outside.left;Edge.Right,outside.right;Edge.Bottom,outside.bottom] do
        equal "automatic side still reserves" (enabled && List.contains edge used) (amount>0)
    for h in [25;38;50;2000] do
        let room=if TabBehaviorPolicy.reserveTopMargin enabled direction then h-1 else 0
        equal "legacy height no clamp" (if enabled && direction<>"down" then h-1 else 0) room
printfn "TopRules: %d checks passed" checks
