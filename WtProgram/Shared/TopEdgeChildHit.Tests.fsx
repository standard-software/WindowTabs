#load "TopEdgeChildHit.fs"
open System
open Bemo.TopEdgeChildHit
let owner = { left=0; top=0; right=1000; bottom=100 }
let child bounds = { hwnd=nativeint 2; bounds=bounds }
let check name expected actual =
    if expected <> actual then failwithf "%s: expected %A, got %A" name expected actual
    printfn "PASS %s" name
check "owner 2 and child 8" 8 (combine 9 2 [8])
check "no child" 6 (combine 9 6 [])
check "owner remains larger" 9 (combine 8 9 [2;8])
check "fallback only without hits" 9 (combine 9 0 [0])
check "child replaces fallback" 8 (combine 9 0 [8])
let mutable calls = 0
let top _ _ _ = calls <- calls+1; Some 12
check "child below top ignored" 0 ((measureColumn owner (child {owner with top=2}) top).height)
check "no out-of-bounds calls" 0 calls
check "outside horizontal bounds ignored" 0 ((measureColumn owner (child {owner with left=1000; right=1100}) top).height)
check "child bottom clips HTTOP" 8 ((measureColumn owner (child {owner with left=700; bottom=8}) top).height)
check "child column stays inside" (Some(850,8)) (sampleColumn owner {owner with left=700; right=1200; bottom=8})
check "non-HTTOP ends run" 2 ((measureColumn owner (child owner) (fun _ _ y -> Some(if y<2 then 12 else 1))).height)
check "corner is not top" 0 ((measureColumn owner (child owner) (fun _ _ _ -> Some 13)).height)
check "timeout ends run" 0 ((measureColumn owner (child owner) (fun _ _ _ -> None)).height)
let old = [child {owner with left=700; bottom=8}]
check "changed child geometry invalidates" false (signature owner old = signature owner [child {owner with left=700; bottom=9}])
check "translation preserves signature" (signature owner old) (signature {owner with left=20; right=1020; top=30; bottom=130} [child {left=720; right=1020; top=30; bottom=38}])

let partial = measureColumn owner (child owner) (fun _ _ y -> if y<4 then Some 12 else None)
check "partial failure retains measured height" 4 partial.height
check "partial failure not cached" None (completedEntry 1 partial)
let exceptional = measureColumn owner (child owner) (fun _ _ _ -> failwith "synthetic")
check "exception not cached" None (completedEntry 1 exceptional)
let mutable elapsed = 0
let timed = measureTargetWith (fun () -> elapsed) (fun _ _ _ -> elapsed<-100; Some 12) owner (child owner)
check "budget exhausted retains one row" 1 timed.height
check "budget exhaustion not cached" None (completedEntry 1 timed)
let full = measureColumn owner (child owner) (fun _ _ y -> Some(if y<8 then 12 else 1))
check "completed result cached" (Some(1,8)) (completedEntry 1 full)
let resized = {owner with right=1400; bottom=400}
check "owner and child resizing preserves key" (signature owner [child owner]) (signature resized [child resized])
check "child bottom below cap invalidates" false (signature owner [child {owner with bottom=8}] = signature owner [child {owner with bottom=9}])
check "enumeration reused before interval" true (reuseChildren 999L 0L true)
check "enumeration refreshed at interval" false (reuseChildren 1000L 0L true)
check "changed bounds enumerate immediately" false (reuseChildren 1L 0L false)
check "retry throttled" false (retryDue 999L 0L)
check "retry allowed" true (retryDue 1000L 0L)
let sample bounds target =
    let mutable used = 0
    measureTargetWith (fun () -> 0) (fun hwnd packed _ ->
        used<-used+1
        if used>28 then failwith "column exceeded budget"
        let y = packed >>> 16
        Some(if y < (if hwnd=nativeint 1 then 6 else 24) then 12 else 1)) bounds target
let scan = Scan()
let independent = scan.Advance(nativeint 1,owner,[child owner],8,sample)
check "parent and child have independent budgets" {height=24;complete=true} independent
let mutable visited = []
let fairSample bounds target =
    visited <- target.hwnd :: visited
    if target.hwnd=nativeint 2 then {height=4;complete=false}
    else {height=8;complete=true}
let fair = Scan()
let targets = [child owner; {hwnd=nativeint 3;bounds=owner}]
let first = fair.Advance(nativeint 1,owner,targets,12,fairSample)
check "partial uses observed value rather than larger fallback" 8 first.height
check "unfinished child prevents caching" None (completedEntry 1 first)
let second = fair.Advance(nativeint 1,owner,targets,12,fairSample)
check "hung child does not starve later child" true (List.contains (nativeint 3) visited)
check "completed parent not sampled again" 1 (visited |> List.filter ((=) (nativeint 1)) |> List.length)
let recovered = fair.Advance(nativeint 1,owner,targets,12,(fun _ _ -> {height=8;complete=true}))
check "retry can complete cache" (Some(1,8)) (completedEntry 1 recovered)
