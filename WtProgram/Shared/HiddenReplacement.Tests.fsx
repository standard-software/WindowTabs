#load "HiddenReplacement.fs"
open Bemo

let check name expected actual =
    if expected <> actual then failwithf "%s: expected %A, got %A" name expected actual

let pending = HiddenReplacement.remember true true false false 1 0L (Some "x")
check "remember hidden selection" true pending.IsSome
check "closed" None (HiddenReplacement.remember true false false false 1 0L (Some "x"))
check "visible" None (HiddenReplacement.remember true true true false 1 0L (Some "x"))
check "not selected" None (HiddenReplacement.remember false true false false 1 0L (Some "x"))
check "removal suppressed" None (HiddenReplacement.remember true true false true 1 0L (Some "x"))
check "unknown removed process" None (HiddenReplacement.remember true true false false 0 0L (Some "x"))
check "no neighbor" None (HiddenReplacement.remember true true false false 1 0L (None: string option))

let activate now selected pid exists visible skip record =
    HiddenReplacement.shouldActivate now selected pid exists visible skip true record
check "next pass" true (activate 1L "x" 1 true true false pending)
check "immediate" true (activate 0L "x" 1 true true false pending)
check "deadline inclusive" true (activate HiddenReplacement.lifetimeMs "x" 1 true true false pending)
check "expired" false (activate (HiddenReplacement.lifetimeMs + 1L) "x" 1 true true false pending)
check "invalid clock order" false (activate -1L "x" 1 true true false pending)
check "different process" false (activate 1L "x" 2 true true false pending)
check "unknown arriving process" false (activate 1L "x" 0 true true false pending)
check "selection changed" false (activate 1L "y" 1 true true false pending)
check "arrival destroyed" false (activate 1L "x" 1 false true false pending)
check "arrival hidden" false (activate 1L "x" 1 true false false pending)
check "arrival suppressed" false (activate 1L "x" 1 true true true pending)
check "record absent or consumed" false (activate 1L "x" 1 true true false None)
check "front lost" false (HiddenReplacement.shouldActivate 1L "x" 1 true true false false pending)
check "foreground is neighbor" true (HiddenReplacement.frontHeld "x" "x" "n" false)
check "foreground is arrival" true (HiddenReplacement.frontHeld "n" "x" "n" false)
check "foreground elsewhere" false (HiddenReplacement.frontHeld "other" "x" "n" false)
check "neighbor minimized" false (HiddenReplacement.frontHeld "x" "x" "n" true)
let later = HiddenReplacement.remember true true false false 1 5000L (Some "x")
check "later base immediate" true (activate 5000L "x" 1 true true false later)
check "later base deadline" true (activate (5000L + HiddenReplacement.lifetimeMs) "x" 1 true true false later)
check "later base expired" false (activate (5001L + HiddenReplacement.lifetimeMs) "x" 1 true true false later)
check "later base before removal" false (activate 4999L "x" 1 true true false later)
printfn "Hidden replacement tests passed (28 cases)."
