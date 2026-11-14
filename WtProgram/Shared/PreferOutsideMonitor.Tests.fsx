#r "../../Newtonsoft.Json.dll"
#load "TabOrder.fs"
#load "SavedTabState.fs"
#load "SnapGeometry.fs"
#load "TabBehaviorPolicy.fs"
#load "TabEdges.fs"
#load "TabEdgeLayout.fs"
open Bemo
open Bemo.TabEdges
open Bemo.TabEdgeLayout

let mutable checks = 0
let equal label expected actual =
    checks <- checks + 1
    if actual <> expected then failwithf "%s: expected %A, got %A" label expected actual

// The low-level outside rule still serves top-only users. Four-edge
// placement restricts its evidence to the window's monitor first.
let rotate edge (x,y,w,h) =
    match edge with
    | Edge.Left -> x,y,w,h
    | Edge.Right -> -x-w,y,w,h
    | Edge.Top -> y,x,h,w
    | Edge.Bottom -> y,-x-w,h,w

let spec edge across =
    { edge = edge; direction = Direction.Outside; across = across
      thickness = 20; minThickness = 8; outsideDistance = 0,0
      insideDistance = 10,10; heightOffset = 0 }

for edge in [Edge.Top; Edge.Left; Edge.Right; Edge.Bottom] do
    let box = rotate edge
    for scale in [1;2;3] do
        let scaled (x,y,w,h) = box (x*scale,y*scale,w*scale,h*scale)
        let home = scaled (0,0,200,200)
        let inside, outside = scaled (0,0,20,100), scaled (-20,0,20,100)
        for visible in [0;50;89;90;91;100] do
            let all = [scaled (-200,0,200,visible);home]
            for ordered in [all;List.rev all] do
                let monitors = placementMonitors false (Some home) ordered
                equal "only the window monitor" [home] monitors
                equal "neighbor cannot prevent folding" true (showsInside edge monitors inside outside)
                equal "explicit inside" true (goesInside Direction.AlwaysInside edge monitors inside outside)
                equal "top-only retains all monitors" ordered (placementMonitors true (Some home) ordered)
                equal "legacy outside visibility threshold" (visible < 90) (showsInside edge all inside outside)
        // A window well inside its display still places the strip outside.
        let inside, outside = scaled (40,20,20,100), scaled (20,20,20,100)
        equal "room on home display" false (showsInside edge [home] inside outside)

equal "no resolved monitor" [] (placementMonitors false None [0,0,200,200])
equal "top-only unresolved monitor" [0,0,200,200] (placementMonitors true None [0,0,200,200])

for edge in [Edge.Left; Edge.Right; Edge.Bottom] do
    let box = rotate edge
    for across in (if edge = Edge.Bottom then [false] else [false;true]) do
        let frame = box (0,0,100,100)
        let home = box (0,0,200,100)
        let s = spec edge across
        for visible in [0;89;90;100] do
            let all = [box (-200,0,200,visible);home]
            let monitors = placementMonitors false (Some home) all
            let expected = place s frame monitors []
            equal "placement folds at home boundary" true expected.inside
            equal "placement uses inside distances" (box (0,10,(if across then 8 else 20),80)) expected.rect
            equal "explicit inside uses full width" true (place {s with direction = Direction.AlwaysInside} frame monitors []).inside
            for iteration in 1..100 do
                equal "same conditions stay stable" expected (place s frame monitors [])
                equal "neighbor ordering stays stable" expected
                    (place s frame (placementMonitors false (Some home) (List.rev all)) [])
        // Corner trimming and end distances cannot make a neighbor eligible.
        let monitors = placementMonitors false (Some home) [box (-200,20,200,80);home]
        equal "corner trimming keeps home monitor" true
            (place s frame monitors [box (-20,0,120,20)]).inside
        equal "end distances keep home monitor" true
            (place {s with outsideDistance = 20,0} frame monitors []).inside

// A strip entirely over the neighboring monitor must also fold inward.
for edge in [Edge.Top;Edge.Left;Edge.Right;Edge.Bottom] do
    let box = rotate edge
    equal "straddling frame folds toward home" true
        (showsInsideHome edge [box (0,0,200,200)] (box (-60,0,20,100)))

printfn "%d checks, 0 failures" checks
