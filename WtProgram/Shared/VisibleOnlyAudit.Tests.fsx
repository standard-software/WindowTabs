#r "../../Newtonsoft.Json.dll"
#load "TabOrder.fs"
#load "Regroup.fs"
#load "SavedTabState.fs"
#load "AppPath.fs"
#load "RestorePosition.fs"
#load "SavedSession.fs"
#load "VirtualDesktopIntegrity.fs"
#load "VirtualDesktopGroups.fs"
open System
open Bemo
open Bemo.VirtualDesktopGroups
let mutable count = 0
let mutable failures = 0
let check name condition =
    count <- count + 1
    if not condition then failures <- failures + 1; printfn "FAIL %s" name
let shown g = g = 1
keepAway <- true
check "same HWND returns to its hidden former group" (returnInto false (Some 2) shown = Some 2)
check "return to hidden group uses safe linked insertion" (restoreLinked false true true false)
check "local return unchanged" (returnInto false (Some 1) shown = Some 1)
check "missing group stays missing" (returnInto false None shown = None)
check "shared window retains local-group policy" (returnInto true (Some 2) shown = None)
keepAway <- false
check "legacy desktop separation unchanged" (returnInto false (Some 2) shown = None)
keepAway <- true
let mutable readDead = false
check "missed destroy is pruned without a current-desktop answer"
    (pruneMember false (fun () -> readDead <- true; false))
check "dead HWND is never queried for current desktop" (not readDead)
for tabbable in [false;true] do
 for recent in [false;true] do
  for cloak in [false;true] do
   for here in [false;true] do
    let old = not tabbable && not recent && not cloak && here
    check "live-member pruning unchanged"
        (pruneMember true (fun () -> not tabbable && not recent && not cloak && here) = old)
check "shell-cloaked member survives stale local reading"
    (not (pruneMember true (fun () -> not true && true)))
check "uncertain desktop read preserves live membership"
    (not (pruneMember true (fun () -> false)))
printfn "%d checks, %d failures" count failures
if failures > 0 then exit 1
