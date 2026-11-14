#r "../../Newtonsoft.Json.dll"
#load "SavedTabState.fs"
#load "TabOrder.fs"
#load "SnapGeometry.fs"
#load "TabBehaviorPolicy.fs"
#load "TabEdges.fs"
#load "TabPreviewPolicy.fs"
open Bemo
let check expected actual = if expected <> actual then failwithf "Expected %A, got %A" expected actual
check (320, 180) (TabPreviewPolicy.fit 1920 1080 320 2000 1000)
check (180, 320) (TabPreviewPolicy.fit 1080 1920 320 2000 1000)
check (480, 270) (TabPreviewPolicy.fit 1920 1080 480 2000 1000)
check (160, 90) (TabPreviewPolicy.fit 1920 1080 480 160 1000)
check (177, 100) (TabPreviewPolicy.fit 1920 1080 480 2000 100)
check (0, 0) (TabPreviewPolicy.fit 0 100 320 1000 1000)
check (0, 0) (TabPreviewPolicy.fit 100 0 320 1000 1000)
check (-1920, -100) (TabPreviewPolicy.place -1920 -100 0 980 320 212 -2000 -200)
check (-320, 768) (TabPreviewPolicy.place -1920 -100 0 980 320 212 100 1000)
// Eligibility is independent of virtual-desktop dimming.
check true (TabPreviewPolicy.eligible true false false false true)
for enabled, hover, active, dragging, visible in
    [false,false,false,false,true; true,true,false,false,true;
     true,false,true,false,true; true,false,false,true,true;
     true,false,false,false,false] do
    check false (TabPreviewPolicy.eligible enabled hover active dragging visible)
check 500 (TabPreviewPolicy.delay false 500)
check 0 (TabPreviewPolicy.delay true 500)
check 1 (TabPreviewPolicy.delay false -1)
check 5000 (TabPreviewPolicy.delay false 10000)
// Top and side strips prefer below; bottom strips prefer above.
for edge, expected in
    [TabEdges.Edge.Top, Some(100, 244); TabEdges.Edge.Bottom, Some(100, 116);
     TabEdges.Edge.Left, Some(100, 244); TabEdges.Edge.Right, Some(100, 244)] do
    check expected (TabPreviewPolicy.besideTooltip edge (0, 0, 600, 600) (100, 200, 300, 240) 4 120 80)
// Clamping near the bottom must flip above instead of covering the tooltip.
check (Some(100, 176)) (TabPreviewPolicy.besideTooltip TabEdges.Edge.Top (0, 0, 400, 300) (100, 260, 300, 290) 4 120 80)
// Clamping near the top must flip below for bottom strips.
check (Some(100, 44)) (TabPreviewPolicy.besideTooltip TabEdges.Edge.Bottom (0, 0, 400, 300) (100, 10, 300, 40) 4 120 80)
// Negative monitor origins and horizontal clamping.
check (Some(-120, -116)) (TabPreviewPolicy.besideTooltip TabEdges.Edge.Right (-400, -200, 0, 60) (-70, -160, -10, -120) 4 120 80)
// A full-height tooltip or oversized preview leaves no non-overlapping placement.
check None (TabPreviewPolicy.besideTooltip TabEdges.Edge.Top (0, 0, 400, 100) (100, 0, 300, 100) 4 120 80)
check None (TabPreviewPolicy.besideTooltip TabEdges.Edge.Bottom (0, 0, 100, 300) (10, 100, 90, 140) 4 120 80)
// Every returned placement stays inside the work area and outside the tooltip.
for edge in [TabEdges.Edge.Top; TabEdges.Edge.Bottom; TabEdges.Edge.Left; TabEdges.Edge.Right] do
    for tipX in [-400; -250; -80] do
        for tipY in [-200; -180; -60; 40] do
            match TabPreviewPolicy.besideTooltip edge (-400, -200, 0, 100) (tipX, tipY, tipX + 80, tipY + 40) 4 120 80 with
            | None -> ()
            | Some(x, y) ->
                check true (x >= -400 && y >= -200 && x + 120 <= 0 && y + 80 <= 100)
                check true (x + 120 <= tipX || x >= tipX + 80 || y + 80 <= tipY || y >= tipY + 40)
printfn "TabPreviewPolicy: PASS"
