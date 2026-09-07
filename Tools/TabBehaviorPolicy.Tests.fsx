#load "../WtProgram/Shared/TabBehaviorPolicy.fs"

open Bemo
open Bemo.TabBehaviorPolicy

let mutable failed = 0

let check name condition =
    if condition then
        printfn "PASS  %s" name
    else
        failed <- failed + 1
        printfn "FAIL  %s" name

check "missing or unknown direction keeps automatic placement"
    (normalizeVerticalDirection "unknown" = verticalAuto)
check "automatic placement stays above when it fits" (not (showInside verticalAuto false))
check "automatic placement moves down when above is off-screen" (showInside verticalAuto true)
check "always-down placement ignores available space above" (showInside verticalAlwaysDown false)

check "ordinary windows remain visible" (not (hideTabs true false true false))
check "fullscreen setting hides fullscreen tabs" (hideTabs true true false false)
check "disabled fullscreen setting does not hide" (not (hideTabs false true false false))
check "moving setting hides tabs during move/size" (hideTabs false false true true)
check "disabled moving setting does not hide" (not (hideTabs false false false true))
check "one active reason keeps tabs hidden" (hideTabs true true true false)

check "active tab icon mouse-down hides tabs immediately"
    (hideFromIconMouseDown true true true)
check "mouse-down that activates an inactive tab does not hide tabs"
    (not (hideFromIconMouseDown true false true))
check "mouse-down outside the icon does not hide tabs"
    (not (hideFromIconMouseDown true true false))
check "disabled icon-click mode does not hide tabs"
    (not (hideFromIconMouseDown false true true))

if failed <> 0 then failwithf "%d checks failed" failed
printfn "all 14 checks passed"
