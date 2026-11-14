#r "../../Newtonsoft.Json.dll"
#load "TabOrder.fs"
#load "SavedTabState.fs"
#load "SnapGeometry.fs"
#load "TabBehaviorPolicy.fs"
#load "TabEdges.fs"
open Bemo.TabEdges
let mutable checks = 0
let equal label expected actual =
    checks <- checks + 1
    if actual <> expected then failwithf "%s: expected %A, got %A" label expected actual
for scale in [1.; 1.25; 1.5; 2.] do
    let thin = int (8. * scale)
    let height = int (24. * scale)
    for x,y in [100,200; -900,-600; 0,0] do
        for edge in priority do
            let w,h = if runsHorizontally edge then 300,thin else thin,300
            let a,b,c,d = receiverHitRect edge height (x,y,w,h)
            equal "tab height" height (if runsHorizontally edge then d else c)
            equal "length" 300 (if runsHorizontally edge then c else d)
            equal "contains thin start" true (a <= x && b <= y)
            equal "contains thin end" true (a+c >= x+w && b+d >= y+h)
            match edge with
            | Edge.Top -> equal "top inner boundary" (y+h) (b+d); equal "top length origin" x a
            | Edge.Left -> equal "left inner boundary" (x+w) (a+c); equal "left length origin" y b
            | Edge.Right -> equal "right inner boundary" x a; equal "right length origin" y b
            | Edge.Bottom -> equal "bottom inner boundary" y b; equal "bottom length origin" x a
            equal "small tab keeps target" (x,y,w,h) (receiverHitRect edge 1 (x,y,w,h))
for allowed in everyAllowed do
    for used in [[]; [Edge.Top]; priority] do
        let actual = receiverEdges allowed used
        equal "only empty allowed edges" (if isTopOnly allowed then [] else edgesOf allowed |> List.filter(fun e -> not (List.contains e used))) actual
for used in [[]; [Edge.Top]; priority] do equal "top only never receives" [] (receiverEdges topOnly used)
printfn "ReceiverEasier: %d checks passed" checks
