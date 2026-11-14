#load "TabButtonPolicy.fs"
open Bemo
let check name condition = if not condition then failwith name
for mode in [null; ""; "invalid"; "activeOnly"] do
    check "unknown values use safe default" (TabButtonPolicy.normalize mode = "activeOnly")
let bools = [false; true]
let modes = ["activeOnly"; "all"; "none"; "invalid"]
let mutable count = 0
for closeMode in modes do
 for pinMode in modes do
  for active in bools do
   for hover in bools do
    for pinned in bools do
     for iconOnly in bools do
      for closeRoom in bools do
       for pinRoom in bools do
        let decision = TabButtonPolicy.decide closeMode pinMode active hover pinned iconOnly closeRoom pinRoom
        let hit = TabButtonPolicy.decide closeMode pinMode active true pinned iconOnly closeRoom pinRoom
        check "paint agrees with hit on hover" (not hover || decision = hit)
        check "close is hover only" (hover || not decision.showClose)
        check "no close on pinned or icon-only or squeezed tabs" (not(pinned || iconOnly || not closeRoom) || not decision.showClose)
        check "pin keeps indicating pinned state" (decision.showPin = (pinned && not iconOnly && pinRoom))
        check "a hidden pin cannot be clicked" (not decision.pinClickable || decision.showPin)
        if not active && closeMode <> "all" then check "inactive close falls through" (not hit.showClose)
        if not active && pinMode <> "all" then check "inactive pin falls through" (not hit.pinClickable)
        if closeMode = "none" then check "hidden closes never hit" (not hit.showClose)
        if pinMode = "none" then check "display-only pins never hit" (not hit.pinClickable)
        if closeMode = "all" then check "legacy close" (decision.showClose = (hover && not pinned && not iconOnly && closeRoom))
        if pinMode = "all" then check "legacy pin" (decision.pinClickable = decision.showPin)
        if active && closeMode <> "none" then check "active close offered" (hit.showClose = (not pinned && not iconOnly && closeRoom))
        if active && pinMode <> "none" then check "active pin offered" (hit.pinClickable = hit.showPin)
        count <- count + 1
printfn "PASS %d button decisions" count
