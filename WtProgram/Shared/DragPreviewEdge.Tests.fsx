#r "../../Newtonsoft.Json.dll"
#load "TabOrder.fs"
#load "SavedTabState.fs"
#load "SnapGeometry.fs"
#load "TabBehaviorPolicy.fs"
#load "TabEdges.fs"
open Bemo.TabEdges
let mutable checks = 0
let equal expected actual =
    checks <- checks + 1
    if expected <> actual then failwithf "Expected %A, got %A" expected actual
for edge in [Edge.Left; Edge.Right; Edge.Bottom] do
    for align in [AlignStart; AlignEnd] do
        for window in [(320,180); (40,20)] do
            for tab in [(120,28); (28,120); (18,28); (280,40)] do
                let ww, wh = window
                let tw, th = tab
                let (w,h), (x,y), (wx,wy) = dragPreviewPlacement edge align window tab
                equal true (x >= 0 && y >= 0 && x+tw <= w && y+th <= h)
                equal true (wx >= 0 && wy >= 0 && wx+ww <= w && wy+wh <= h)
                match edge with
                | Edge.Left -> equal wx (x+tw)
                | Edge.Right -> equal x (wx+ww)
                | _ -> equal y (wy+wh)
                if edge = Edge.Bottom then equal (if align = AlignStart then 0 else w-tw) x
                else equal (if align = AlignStart then 0 else h-th) y
equal ((440,180),(0,152),(120,0)) (dragPreviewPlacement Edge.Left AlignEnd (320,180) (120,28))
equal ((348,180),(320,60),(0,0)) (dragPreviewPlacement Edge.Right AlignEnd (320,180) (28,120))
equal ((320,208),(200,180),(0,0)) (dragPreviewPlacement Edge.Bottom AlignEnd (320,180) (120,28))
printfn "%d checks, 0 failures" checks
