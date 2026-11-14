#load "TabBehaviorPolicy.fs"
open Bemo
let mutable checks = 0
let equal label expected actual =
    checks <- checks + 1
    if expected <> actual then failwithf "%s: expected %A, got %A" label expected actual
for hide in [false; true] do
    for hidden in [false; true] do
        for delay in [0; 100; 3000] do
            for pending in [None; Some 0; Some 100; Some 3000] do
                let expected = hide && not hidden && pending <> Some delay
                equal "delay scheduling" expected (TabBehaviorPolicy.restartHideDelay hide hidden delay pending)
// Simulate independent deadlines: opening one strip must not postpone another.
let mutable pending = Map.ofList [0, Some 3000; 1, Some 3000; 2, Some 3000; 3, Some 3000]
for hovered in [0; 1; 2; 3] do
    pending <- pending.Add(hovered, None)
    for edge in [0; 1; 2; 3] do
        equal "hover preserves other pending delays" false
            (TabBehaviorPolicy.restartHideDelay (edge <> hovered) false 3000 pending.[edge])
    equal "leaving starts own delay" true
        (TabBehaviorPolicy.restartHideDelay true false 3000 pending.[hovered])
    pending <- pending.Add(hovered, Some 3000)
printfn "InsideHidePerEdge: %d checks passed" checks

for delay in [0; 1; 100; 3000; 10000] do
    equal "positive hide timer interval" (max 1 delay) (TabBehaviorPolicy.hideTimerInterval delay)
printfn "InsideHidePerEdge including zero interval: %d checks passed" checks
