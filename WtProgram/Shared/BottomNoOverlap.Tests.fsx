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
    if expected <> actual then failwithf "%s: expected %A, got %A" label expected actual

let work = (0, 0, 1200, 900)
for thickness in [1; 24; 36; 48] do
    for offset in [-2; -1; 0; 1; 2; 4] do
        let spec =
            { edge = Edge.Bottom; direction = Direction.Outside; across = false
              thickness = thickness; minThickness = thickness
              outsideDistance = (10, 20); insideDistance = (30, 40)
              heightOffset = offset }
        let outside = place spec (100, 100, 600, 400) [work] []
        equal "outside touches frame without overlap or gap" (110, 500, 570, thickness) outside.rect
        equal "outside direction" false outside.inside
        let inside = place { spec with direction = Direction.AlwaysInside } (100, 100, 600, 400) [work] []
        equal "inside unchanged" (130, 500 - thickness, 530, thickness) inside.rect
        equal "forced inside" true inside.inside
        let reserve = snapReserve true None [spec]
        equal "full bottom thickness reserved" { noReserve with bottom = thickness } reserve
        equal "margin disabled" noReserve (snapReserve false (Some 24) [spec])
        equal "inside has no bottom margin" noReserve (snapReserve true None [{ spec with direction = Direction.AlwaysInside }])
        let snapped = reserveArea reserve work
        let placed = place spec snapped [work] []
        equal "snap leaves strip outside" false placed.inside
        let _, sy, _, sh = placed.rect
        equal "strip fits exactly at work-area bottom" 900 (sy + sh)
        equal "repeated destination snap stable" snapped (reserveArea (snapReserve true None [spec]) work)
        let atEdge = place spec (100, 500, 600, 400) [work] []
        equal "screen edge automatically goes inside" true atEdge.inside
        equal "automatic inside position unchanged" (130, 900 - thickness, 530, thickness) atEdge.rect
        let exact = place spec (100, 500 - thickness, 600, 400) [work] []
        equal "exact outside fit stays outside" false exact.inside
        let short = place spec (100, 501 - thickness, 600, 400) [work] []
        equal "one pixel short goes inside" true short.inside
        for edge in [Edge.Left; Edge.Right] do
            equal "side reserve unchanged" thickness
                (let r = snapReserve true None [{ spec with edge = edge }] in if edge = Edge.Left then r.left else r.right)

for height in [1; 24; 36; 48] do
    let reserve = snapReserve true (Some height) []
    equal "top-only reserve unchanged" { noReserve with top = height - 1 } reserve
    equal "top-only snap unchanged" (SnapGeometry.reserveTabHeight (height - 1) work) (reserveArea reserve work)
equal "empty edges reserve nothing" noReserve (snapReserve true None [])
printfn "%d checks, 0 failures" checks
