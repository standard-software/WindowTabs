#load "../WtProgram/Shared/SnapGeometry.fs"

open Bemo

let check name expected actual =
    if expected <> actual then failwithf "%s: expected %A, got %A" name expected actual

// Synthetic physical-pixel geometry, including an offset high-DPI display.
check "left with top reserve" (0, 24, 500, 776)
    (SnapGeometry.dropBounds (0, 0, 1000, 800) (0, 24, 1000, 776) (0, 400))
check "right with top reserve" (500, 24, 500, 776)
    (SnapGeometry.dropBounds (0, 0, 1000, 800) (0, 24, 1000, 776) (999, 400))
check "top on negative display" (-1500, -164, 1500, 582)
    (SnapGeometry.dropBounds (-1500, -200, 1500, 1200) (-1500, -164, 1500, 1164) (-750, -190))
check "bottom with side reserves" (32, 400, 936, 400)
    (SnapGeometry.dropBounds (0, 0, 1000, 800) (32, 0, 936, 800) (500, 799))
check "direction uses unreserved work area" (0, 200, 500, 600)
    (SnapGeometry.dropBounds (0, 0, 1000, 800) (0, 200, 1000, 600) (50, 100))
printfn "Snap preview geometry: 5 checks passed"

check "outer edges only" (8, 8, 492, 784)
    (SnapGeometry.previewInset 8 (0, 0, 1000, 800) (0, 0, 500, 800))
check "right half keeps center" (500, 8, 492, 784)
    (SnapGeometry.previewInset 8 (0, 0, 1000, 800) (500, 0, 500, 800))
check "interior unchanged" (100, 100, 200, 200)
    (SnapGeometry.previewInset 8 (0, 0, 1000, 800) (100, 100, 200, 200))
check "reserved top unchanged" (8, 24, 492, 768)
    (SnapGeometry.previewInset 8 (0, 0, 1000, 800) (0, 24, 500, 776))
check "offset scaled display" (-1488, -188, 738, 1176)
    (SnapGeometry.previewInset 12 (-1500, -200, 1500, 1200) (-1500, -200, 750, 1200))
check "tiny unchanged" (0, 0, 10, 10)
    (SnapGeometry.previewInset 8 (0, 0, 10, 10) (0, 0, 10, 10))
check "full work area" (8, 8, 984, 784)
    (SnapGeometry.previewInset 8 (0, 0, 1000, 800) (0, 0, 1000, 800))
printfn "Snap preview insets: 7 checks passed"

let origin = (-200, 100, 300, 200)
let destination = (500, -100, 500, 800)
check "animation starts at source" origin (SnapGeometry.previewInterpolate origin destination 0.0)
check "animation ends at target" destination (SnapGeometry.previewInterpolate origin destination 1.0)
check "progress lower clamp" 0.0 (SnapGeometry.previewEase -1.0)
check "progress upper clamp" 1.0 (SnapGeometry.previewEase 2.0)
check "cubic midpoint" 0.875 (SnapGeometry.previewEase 0.5)
let samples = [0 .. 100] |> List.map (fun i -> SnapGeometry.previewInterpolate origin destination (float i / 100.0))
check "edges progress monotonically" true
    (samples |> List.pairwise |> List.forall (fun ((x,y,w,h),(nx,ny,nw,nh)) ->
        nx >= x && ny <= y && nx+nw >= x+w && ny+nh >= y+h))
let middle = SnapGeometry.previewInterpolate origin destination 0.4
let redirected = (-500, 0, 400, 600)
check "retarget starts at displayed frame" middle (SnapGeometry.previewInterpolate middle redirected 0.0)
check "retarget ends at new target" redirected (SnapGeometry.previewInterpolate middle redirected 1.0)
check "default source centered at half size" (625, 100, 250, 400) (SnapGeometry.previewCentered 0.5 destination)
let collapsed = SnapGeometry.previewCentered 0.0 destination
check "collapse keeps positive size" (749, 299, 1, 1) collapsed
let shrinking = SnapGeometry.previewInterpolate destination collapsed 0.5
check "collapse contracts both dimensions" true
    (let _,_,w,h = shrinking in w > 0 && h > 0 && w < 500 && h < 800)
check "reappear starts at shrinking frame" shrinking (SnapGeometry.previewInterpolate shrinking origin 0.0)
printfn "Snap preview animation: 12 checks passed"

// Synthetic current/restored rectangles exercise maximized inputs.
let beforeRestore = (-8, -8, 1016, 816)
let afterRestore = (100, 120, 420, 300)
let sourceCases =
    [ false, true, false, false, true, beforeRestore
      false, true, false, false, false, beforeRestore
      false, false, true, false, true, beforeRestore
      false, true, true, false, true, beforeRestore
      false, false, true, false, false, afterRestore
      false, true, true, false, false, afterRestore
      false, false, true, true, true, afterRestore
      false, true, true, true, true, afterRestore
      false, false, false, false, true, afterRestore
      true, true, false, false, true, afterRestore
      true, false, true, false, true, afterRestore
      true, true, true, false, true, afterRestore ]
for detached, otherScreen, snap, percentage, topOnly, expected in sourceCases do
    let kind = SnapGeometry.menuPlacementSource detached otherScreen snap percentage topOnly
    check "placement source" expected (SnapGeometry.placementSourceBounds kind beforeRestore afterRestore)
    check "normal placement source" afterRestore (SnapGeometry.placementSourceBounds kind afterRestore afterRestore)
let previewSource = SnapGeometry.menuPlacementSource false false true false true
let executionSource = SnapGeometry.GroupKeepSizeSnap true
let snapFrom source =
    let _, _, width, height = SnapGeometry.placementSourceBounds source beforeRestore afterRestore
    SnapGeometry.snapBounds "snapleft" (0, 0, 1000, 800) width height
check "maximized keep-size preview matches execution" (snapFrom executionSource) (snapFrom previewSource)
check "maximized keep-size uses half width" (0, 0, 500, 800) (snapFrom previewSource)
printfn "Snap preview placement sources: 26 checks passed"
