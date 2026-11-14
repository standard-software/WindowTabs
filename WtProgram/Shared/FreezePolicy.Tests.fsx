#load "RestartRequest.fs"
open Bemo.RestartRequest
let mutable checks = 0
let check name actual expected =
    checks <- checks + 1
    if actual <> expected then failwithf "%s failed: %A" name actual

// The same policy runs in Debug and Release, with no CPU or stack evidence.
check "confirmed foreign hang waits" (restartDecision true true 0L) "wait-desktop-hung"
check "isolated wait or Invoke deadlock restarts" (restartDecision true false 0L) "restart-local"
check "failed probe preserves old restart" (restartDecision false false 0L) "restart-local"
check "incomplete probe does not suppress restart" (restartDecision false true 0L) "restart-local"
check "desktop recovery allows next timeout restart"
    ([true; true; false] |> List.map (fun hung -> restartDecision true hung 0L))
    ["wait-desktop-hung"; "wait-desktop-hung"; "restart-local"]
check "probe failure after suppression allows next timeout restart"
    ([true, true; false, false] |> List.map (fun (known, hung) -> restartDecision known hung 0L))
    ["wait-desktop-hung"; "restart-local"]
check "wait immediately below cap" (restartDecision true true 119999L) "wait-desktop-hung"
check "restart exactly at cap" (restartDecision true true 120000L) "restart-after-wait"
check "restart after delayed next timeout" (restartDecision true true 145000L) "restart-after-wait"
check "recovered desktop preserves local restart" (restartDecision true false 120000L) "restart-local"
check "unknown probe preserves local restart at cap" (restartDecision false true 120000L) "restart-local"
check "new unanswered period gets fresh budget" (restartDecision true true 0L) "wait-desktop-hung"
printfn "FreezePolicy: %d checks passed" checks

// Block dispatch without invoking UI, then verify the caller can continue.
let blocked = new System.Threading.ManualResetEvent(false)
let exited = new System.Threading.ManualResetEvent(false)
let clock = System.Diagnostics.Stopwatch.StartNew()
check "blocked dispatch times out" (waitForSave 50 (fun complete ->
    blocked.WaitOne() |> ignore
    complete()
    exited.Set() |> ignore)) false
check "restart continuation remains reachable" (clock.ElapsedMilliseconds < 2000L) true
blocked.Set() |> ignore
check "late completion remains safe" (exited.WaitOne(2000)) true
blocked.Dispose()
exited.Dispose()
check "dispatch exception returns failure" (waitForSave 2000 (fun _ -> failwith "dispatch failure")) false
check "completed save succeeds" (waitForSave 2000 (fun complete -> complete())) true
check "posted but unfinished save times out" (waitForSave 20 (fun _ -> ())) false
printfn "FreezePolicy including dispatch: %d checks passed" checks
