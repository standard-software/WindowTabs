#load "TaskbarAppIdPolicy.fs"
open System
open Bemo.TaskbarAppIdPolicy

let mutable checks = 0
let check name actual expected =
    checks <- checks + 1
    if actual <> expected then failwithf "%s failed" name

let expectedWaits = [5000; 10000; 20000; 30000; 60000; 120000; 300000]
check "stage count" retryStageCount expectedWaits.Length
check "backoff" ([1 .. retryStageCount] |> List.map retryWaitMs) expectedWaits
for attempt in [Int32.MinValue; 0] do
    check "first stage clamp" (retryWaitMs attempt) 5000
for attempt in [retryStageCount + 1; Int32.MaxValue] do
    check "last stage clamp" (retryWaitMs attempt) 300000
for attempt, delay in List.indexed (expectedWaits @ [300000]) do
    let attempt = attempt + 1
    check "before deadline" (retryDue (100 + delay - 1) 100 attempt false) false
    check "at deadline" (retryDue (100 + delay) 100 attempt false) true
    check "membership bypass" (retryDue 100 100 attempt true) true
    let start = Int32.MaxValue - 100
    check "tick wrap before" (retryDue (start + delay - 1) start attempt false) false
    check "tick wrap due" (retryDue (start + delay) start attempt false) true

check "original empty restored" (retryLostPin "pin-lost" true true None (Some None) (Some appPinnedDesktop)) true
check "different value" (retryLostPin "pin-lost" true true None (Some(Some String.Empty)) (Some appPinnedDesktop)) false
check "unreadable" (retryLostPin "pin-lost" true true None None (Some appPinnedDesktop)) false
check "identity changed" (retryLostPin "pin-lost" true false None (Some None) (Some appPinnedDesktop)) false
check "ordinary window" (retryLostPin "pin-lost" false true None (Some None) (Some appPinnedDesktop)) false
check "restored window pin" (retryLostPin "pin-lost" true true None (Some None) (Some windowPinnedDesktop)) true
for desktop in [None; Some Guid.Empty; Some(Guid("11111111-1111-1111-1111-111111111111"))] do
    check "not shared" (retryLostPin "pin-lost" true true None (Some None) desktop) false
for reason in ["prepare-unreadable"; "write-failed"; "readback-failed"; "rewrite-application-changed";
               "verify-application-changed"; "pin-changed"; "pin-command-failed"; "pin-unverified"] do
    check "permanent refusal" (retryLostPin reason true true None (Some None) (Some appPinnedDesktop)) false
// Synthetic handles only; these tests never call Windows APIs.
let window = nativeint 1
let peer = nativeint 2
let group = nativeint 3
let membership = retryMembership window [(group, [window; peer])]
check "tab reorder" (retryMembership window [(group, [peer; window])]) membership
check "duplicate member" (retryMembership window [(group, [window; peer; peer])]) membership
check "member removed" (retryMembership window [(group, [window])] <> membership) true
check "group move" (retryMembership window [(nativeint 4, [window; peer])] <> membership) true
check "unrelated group" (retryMembership window [group, [window; peer]; nativeint 4, [peer]]) membership

let markerCases =
    [ "unowned verified marker", Same, true, false, true
      "active or recovery owner retains marker", Same, true, true, false
      "missing cookie cannot prove ownership", Same, false, false, false
      "reused or gone window is untouched", Gone, true, false, false
      "uncertain identity is untouched", Undecided, true, false, false ]
for name, identity, hasCookie, retained, expected in markerCases do
    if removeRetryMarker identity hasCookie retained <> expected then failwith name

// A recovered journal row must release the gate only after fresh observations.
let returned = { hwnd = 1L; pid = 2u; started = 3L; phase = Returned; secret = None
                 id = Some(prefix + "1"); previous = None; cookie = Some 1; pin = true }
let restored, damaged = parse (format [returned])
check "return journal reload" damaged false
let row = List.exactlyOne restored
let desktop = Some(Guid("11111111-1111-1111-1111-111111111111"))
let first, ready = returnedPinObservation row true None (Some None) 1000 None desktop
check "reloaded return needs observation" ready false
let interrupted, interruptedReady = returnedPinObservation row true None (Some None) 1500 first None
check "failed desktop read resets return" (interrupted, interruptedReady) (None, false)
let second, secondReady = returnedPinObservation row true None (Some None) 2000 interrupted desktop
check "return restarts after unavailable read" secondReady false
let _, finalReady = returnedPinObservation row true None (Some None) 2500 second desktop
check "return resumes enabled planning" (resumeAfterReturn true finalReady) true
check "return never enables disabled planning" (resumeAfterReturn false finalReady) false
check "shell surplus still requires verification" (givenVerdict false (Some(Map.ofList [prefix + "1", 1])) row.id 0) RebuildGiven
check "shell empty permits journal removal" (givenVerdict false (Some Map.empty) row.id 0) Refiled
check "normal return does not change lost pin retry rule" (retryLostPin "pin-lost" true true None (Some None) desktop) false

let retryFirst, retryFirstReady = returnedPinObservation row true None (Some None) 1000 None desktop
check "ordinary retry first read waits" (retryReturnedPin true None (Some None) desktop retryFirstReady false) false
let _, retryReady = returnedPinObservation row true None (Some None) 6000 retryFirst desktop
check "ordinary retry awaits journal retirement" (retryReturnedPin true None (Some None) desktop retryReady true) false
check "ordinary retry leaves declined after return" (retryReturnedPin true None (Some None) desktop retryReady false) true
check "ordinary retry still checks value" (retryReturnedPin true None None desktop retryReady false) false
for live in [None; Some Guid.Empty; Some windowPinnedDesktop] do
    let _, ready = returnedPinObservation row true None (Some None) 6000 retryFirst live
    check "unverified return stays observable at slow interval" (returnedCheckDue false true 35999 6000) false
    check "unverified return eventually reads again" (returnedCheckDue false true 36000 6000) true
    check "uncertain return never proves ordinary" ready false
check "app pin retry preserved" (retryReturnedPin true None (Some None) (Some appPinnedDesktop) false true) true
check "window pin retry preserved" (retryReturnedPin true None (Some None) (Some windowPinnedDesktop) false true) true

check "cleared ID" (externallyChangedId (prefix + "1") (Some None)) true
check "application ID" (externallyChangedId (prefix + "1") (Some(Some "app-1"))) true
check "unchanged ID" (externallyChangedId (prefix + "1") (Some(Some(prefix + "1")))) false
check "another owned ID" (externallyChangedId (prefix + "1") (Some(Some(prefix + "2")))) false
check "failed ID read" (externallyChangedId (prefix + "1") None) false
check "event settling" (idObservationDue 399 (Some 0) None false) false
check "event ready" (idObservationDue 400 (Some 0) None false) true
check "event throttled" (idObservationDue 900 (Some 0) (Some 0) false) false
check "event throttle elapsed" (idObservationDue 1000 (Some 0) (Some 0) false) true
check "no event" (idObservationDue 1000 None None false) false
check "slow observation" (idObservationDue 1000 None None true) true
check "slow observation throttled" (idObservationDue 999 None (Some 0) true) false
check "observation tick wrap" (idObservationDue (Int32.MinValue + 399) (Some(Int32.MaxValue - 100)) None false) true

for attempt in 1 .. retryStageCount do
    let stable = 15000
    check "rapid reset advances" (externalRetryAttempt 101 100 attempt) (min retryStageCount (attempt + 1))
    check "before stable boundary" (externalRetryAttempt (100 + stable - 1) 100 attempt) (min retryStageCount (attempt + 1))
    check "stable reset" (externalRetryAttempt (100 + stable) 100 attempt) 1
    check "after stable boundary" (externalRetryAttempt (100 + stable + 1) 100 attempt) 1
    check "ordinary navigation starts over" (externalRetryAttempt 50100 100 attempt) 1
    let start = Int32.MaxValue - 100
    check "before stable boundary tick wrap" (externalRetryAttempt (start + stable - 1) start attempt) (min retryStageCount (attempt + 1))
    check "stable reset tick wrap" (externalRetryAttempt (start + stable) start attempt) 1

printfn "TaskbarRetry: %d checks passed" (checks + markerCases.Length)
