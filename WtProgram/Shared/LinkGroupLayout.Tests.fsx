#load "LinkGroupLayout.fs"
open Bemo.LinkGroupLayout

let check name expected actual =
    if actual <> expected then failwithf "%s: expected %A, got %A" name expected actual
let rect x y w h = { x = x; y = y; width = w; height = h }
let displays = [rect -100 0 100 100; rect 0 0 100 100; rect 100 0 100 100]
let groups = [rect 80 0 20 20, 4; rect 0 40 20 20, 3; rect 0 0 20 20, 2; rect 0 0 20 20, 1]
check "X then Y then stable identity" [1;2;3;4]
    (groups |> List.sortBy (fun (r, id) -> orderKey r id) |> List.map snd)
check "centre, not left edge" (OnDisplay 2) (displayIndex (rect 80 0 60 20) displays)
check "boundary belongs to right display" (OnDisplay 1) (displayIndex (rect -10 0 20 20) displays)
check "negative centre" (OnDisplay 0) (displayIndex (rect -90 0 20 20) displays)
check "off-screen has no display" OnNoDisplay (displayIndex (rect 250 150 20 20) displays)
check "single display omits prefix" NoPrefix (displayIndex (rect 250 0 20 20) [rect 0 0 100 100])
check "no display omits prefix" NoPrefix (displayIndex (rect 0 0 20 20) [])
check "half-pixel centre" (1L, 1L) (centre (rect 0 0 1 1))
check "bottom boundary" (OnDisplay 1) (displayIndex (rect 0 90 20 20) [rect 0 0 100 100; rect 0 100 100 100])
check "gap between displays has no display" OnNoDisplay (displayIndex (rect 0 0 0 0) [rect -20 -10 10 20; rect 10 -10 10 20])
printfn "LinkGroupLayout: 10 checks passed"
