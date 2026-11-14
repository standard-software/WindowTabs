#load "RestartRequest.fs"
open Bemo
let mutable checks = 0
let check name expected actual =
    checks <- checks + 1
    if expected <> actual then failwith name
check "ordinary start" None (RestartRequest.parse [||])
check "ordinary arguments" None (RestartRequest.parse [|"x"|])
check "missing identity" None (RestartRequest.parse [|"--watchdog-restart"; "1"|])
check "invalid pid" None (RestartRequest.parse [|"--watchdog-restart"; "x"; "1"|])
check "invalid identity" None (RestartRequest.parse [|"--watchdog-restart"; "1"; "x"|])
check "negative pid" None (RestartRequest.parse [|"--watchdog-restart"; "-1"; "1"|])
check "zero identity" None (RestartRequest.parse [|"--watchdog-restart"; "1"; "0"|])
check "valid hand-over" (Some(1, 2L)) (RestartRequest.parse [|"--watchdog-restart"; "1"; "2"|])
check "same process" true (RestartRequest.matches 2L 2L)
check "reused pid" false (RestartRequest.matches 2L 3L)
printfn "%d checks, 0 failures" checks
