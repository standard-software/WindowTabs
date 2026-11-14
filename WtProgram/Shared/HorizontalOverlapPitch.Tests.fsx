#r "../../Newtonsoft.Json.dll"
#load "TabOrder.fs"
#load "SavedTabState.fs"
#load "SnapGeometry.fs"
#load "TabBehaviorPolicy.fs"
#load "TabEdges.fs"
#load "TabEdgeLayout.fs"
open Bemo
let mutable checks = 0
let check label condition =
    checks <- checks + 1
    if not condition then failwith label
for scale in [1.0; 1.25; 1.5; 2.0; 3.0] do
    let px n = int (float n * scale + 0.5)
    for height in [1; 12; 24; 25; 32; 48; 80] do
        let h = px height
        let mutable previous = System.Int32.MaxValue
        for overlap in [-100; 0; 1; 2; 4; 8; 24; 1000] do
            let rows = TabEdgeLayout.rowsOf px h (px overlap)
            check "fixed height" (rows.length = max 1 (h-1))
            check "pitch formula" (rows.pitch = rows.length - rows.overlap && rows.pitch > 0)
            check "increasing overlap packs rows" (rows.pitch <= previous)
            check "gap retained" (rows.overlap <= px overlap)
            check "requested overlap within geometric limits" (rows.overlap = min (rows.length-1) (px overlap))
            check "content length" (TabEdgeLayout.contentLength rows 10 = 9 * rows.pitch + rows.length)
            check "no tabs" (TabEdgeLayout.contentLength rows 0 = 0)
            let view = 3 * h
            let last = TabEdgeLayout.maxScroll rows 10 view
            check "scroll clamped" (TabEdgeLayout.clampScroll rows 10 view 100000 = last)
            check "wheel advances by pitch" (TabEdgeLayout.wheelScroll rows 10 view 0 -120 = min last rows.pitch)
            check "last row shown" (TabEdgeLayout.scrollToShow rows 10 view 0 (9 * rows.pitch) = last)
            previous <- rows.pitch
let zero = TabEdgeLayout.rowsOf id 32 0
let dense = TabEdgeLayout.rowsOf id 32 4
check "more rows fit" (TabEdgeLayout.contentLength zero 10 > 300 && TabEdgeLayout.contentLength dense 10 <= 300)
check "scroll buttons disappear when rows fit" (TabEdgeLayout.scrolls zero 10 300 14 && not (TabEdgeLayout.scrolls dense 10 300 14))
check "full z-order preserved" (TabEdgeLayout.rowOrder [2;3;1] [1;2;3] [] = [2;3;1])
check "other edge filtered" (TabEdgeLayout.rowOrder [4;2;3;1] [1;2;3] [] = [2;3;1])
check "missing order falls back" (TabEdgeLayout.rowOrder [] [1;2;3] [] = [1;2;3])
check "drag order matches top" (TabEdgeLayout.rowOrder [2;3;1] [1;2;3] [1;2] = [3;1;2])
let defaultZero = TabEdgeLayout.rowsOf id 25 0
let defaultDense = TabEdgeLayout.rowsOf id 25 20
let capacity rows = [1..100] |> List.filter (fun n -> TabEdgeLayout.contentLength rows n <= 500) |> List.length
check "requested twenty pixels overlap" (defaultDense.overlap = 20 && defaultDense.pitch = 4)
check "requested density" (capacity defaultZero = 20 && capacity defaultDense = 100)
check "default density removes scrolling for 22 rows" (TabEdgeLayout.scrolls defaultZero 22 500 14 && not (TabEdgeLayout.scrolls defaultDense 22 500 14))
let gap = TabEdgeLayout.rowsOf id 25 -5
check "negative overlap opens gap" (gap.overlap = -5 && gap.pitch = 29)
check "gap content extent" (TabEdgeLayout.contentLength gap 3 = 82)
check "gap is partitioned at midpoint" (TabEdgeLayout.rowAt gap [1,0; 2,29] 0.0 0.0 27 = Some 2)
check "gap wheel uses pitch" (TabEdgeLayout.wheelScroll gap 10 100 0 -120 = 29)
check "extreme negative overlap bounded" ((TabEdgeLayout.rowsOf id 25 System.Int32.MinValue).pitch = System.Int32.MaxValue)
let middleRows = TabEdgeLayout.rowsOf id 25 20
check "midpoint belongs to later row" (TabEdgeLayout.rowAt middleRows [1,0;2,4] 0.0 10.0 14 = Some 2)
check "midpoint ignores z-order" (TabEdgeLayout.rowAt middleRows [2,4;1,0] 0.0 10.0 14 = Some 2)
check "before midpoint belongs to first row" (TabEdgeLayout.rowAt middleRows [2,4;1,0] 0.0 10.0 13 = Some 1)
printfn "%d checks, 0 failures" checks


