#load "TaskbarAppIdPolicy.fs"
open System
open Bemo
open TaskbarAppIdPolicy
let mutable count = 0
let check name condition =
    count <- count + 1
    if not condition then failwith name

let h (n: int) = nativeint n
let g1, g2 = h 1000, h 2000
let d1, d2 = Guid("11111111-1111-1111-1111-111111111111"), Guid("22222222-2222-2222-2222-222222222222")
let at (m: Map<int, Guid>) = fun (x: IntPtr) -> match m.TryFind(int x) with Some d -> OnDesktop d | None -> Unknown
let k g d = { group = g; desktop = d }
let slots groups excluded places previous refused =
    TaskbarAppIdPolicy.slots groups excluded places previous refused Set.empty Set.empty None

// --- ownership of a value
check "ours" (isOurs (Some (prefix + "abc")))
check "not ours" (not (isOurs (Some "Vendor.App")) && not (isOurs None))
check "prefix must lead" (not (isOurs (Some ("x" + prefix))))
check "original kept" (originalOf (Some "Vendor.App") = Some(Some "Vendor.App"))
check "no original" (originalOf None = Some None)
check "leftover original is unknown" (originalOf (Some (prefix + "old")) = None)

// --- gate: off and untouched costs nothing; startup restore defers everything
check "off untouched" (not (shouldPass false false false))
check "on" (shouldPass true false false)
check "off but holding gives back" (shouldPass false true false)
check "suspended waits (on)" (not (shouldPass true false true))
check "suspended waits (holding)" (not (shouldPass false true true))

// --- place of a window
check "here uses current" (placeOf (Some d1) (Some(Some true, Some d2)) = OnDesktop d1)
check "here without current uses window desktop" (placeOf None (Some(Some true, Some d2)) = OnDesktop d2)
check "here unknown" (placeOf None (Some(Some true, None)) = Unknown)
check "away uses its desktop" (placeOf (Some d1) (Some(Some false, Some d2)) = OnDesktop d2)
check "away without desktop" (placeOf (Some d1) (Some(Some false, None)) = Unknown)
check "unsure with desktop uses it" (placeOf (Some d1) (Some(None, Some d2)) = OnDesktop d2)
check "rebuild wobble (not here, GUID_NULL) decides nothing" (placeOf (Some d1) (Some(Some false, Some Guid.Empty)) = Unknown)
check "unsure GUID_NULL decides nothing" (placeOf (Some d1) (Some(None, Some Guid.Empty)) = Unknown)
check "here GUID_NULL without current" (placeOf None (Some(Some true, Some Guid.Empty)) = Unknown)
check "unread" (placeOf (Some d1) None = Unknown)

// --- slots: one per group and desktop, including single tabs
let one = slots [ g1, [ h 1; h 2; h 3 ] ] Set.empty (at (Map [ 1, d1; 2, d1; 3, d1 ])) Map.empty Set.empty
check "one slot" (one = Map [ k g1 d1, [ h 1; h 2; h 3 ] ])
check "single tab has a slot" (slots [ g1, [ h 1 ] ] Set.empty (at (Map [ 1, d1 ])) Map.empty Set.empty = Map [ k g1 d1, [ h 1 ] ])
let split = slots [ g1, [ h 1; h 2; h 3; h 4 ] ] Set.empty (at (Map [ 1, d2; 2, d1; 3, d1; 4, d2 ])) Map.empty Set.empty
check "per desktop" (split = Map [ k g1 d1, [ h 2; h 3 ]; k g1 d2, [ h 1; h 4 ] ])
check "per desktop leader is first tab on that desktop" (List.head split.[k g1 d1] = h 2)
let lone = slots [ g1, [ h 1; h 2 ] ] Set.empty (at (Map [ 1, d1; 2, d2 ])) Map.empty Set.empty
check "one tab per desktop has separate slots" (lone = Map [ k g1 d1, [ h 1 ]; k g1 d2, [ h 2 ] ])
check "excluded window keeps own button"
    (slots [ g1, [ h 1; h 2; h 3 ] ] (Set [ h 2 ]) (at (Map [ 1, d1; 2, d1; 3, d1 ])) Map.empty Set.empty = Map [ k g1 d1, [ h 1; h 3 ] ])
check "excluded first tab: next tab leads"
    (slots [ g1, [ h 1; h 2; h 3 ] ] (Set [ h 1 ]) (at (Map [ 1, d1; 2, d1; 3, d1 ])) Map.empty Set.empty = Map [ k g1 d1, [ h 2; h 3 ] ])
check "window in two groups keeps own button"
    (slots [ g1, [ h 1; h 2 ]; g2, [ h 2; h 3; h 4 ] ] Set.empty (at (Map [ 1, d1; 2, d1; 3, d1; 4, d1 ])) Map.empty Set.empty = Map [ k g1 d1, [ h 1 ]; k g2 d1, [ h 3; h 4 ] ])
check "two groups two buttons"
    ((slots [ g1, [ h 1; h 2 ]; g2, [ h 3; h 4 ] ] Set.empty (at (Map [ 1, d1; 2, d1; 3, d1; 4, d1 ])) Map.empty Set.empty).Count = 2)
let unknownKeeps = slots [ g1, [ h 1; h 2 ] ] Set.empty (at (Map [ 1, d1 ])) (Map [ h 2, k g1 d1 ]) Set.empty
check "unknown desktop keeps previous filing" (unknownKeeps = Map [ k g1 d1, [ h 1; h 2 ] ])
check "unknown desktop of another group is not kept"
    ((slots [ g1, [ h 1; h 2 ] ] Set.empty (at (Map [ 1, d1 ])) (Map [ h 2, k g2 d1 ]) Set.empty) = Map [ k g1 d1, [ h 1 ] ])
check "unknown and never filed is left out" ((slots [ g1, [ h 1; h 2 ] ] Set.empty (at (Map [ 1, d1 ])) Map.empty Set.empty) = Map [ k g1 d1, [ h 1 ] ])
check "duplicates in one group count once" ((slots [ g1, [ h 1; h 1 ] ] Set.empty (at (Map [ 1, d1 ])) Map.empty Set.empty) = Map [ k g1 d1, [ h 1 ] ])
// The wobble right after a rebuild: the reading says nothing, the filing stays.
let wobble = fun (x: IntPtr) -> placeOf (Some d1) (if x = h 2 then Some(Some false, Some Guid.Empty) else Some(Some true, Some d1))
check "rebuild wobble keeps the slot"
    (slots [ g1, [ h 1; h 2; h 3 ] ] Set.empty wobble (Map [ h 2, k g1 d1 ]) Set.empty = Map [ k g1 d1, [ h 1; h 2; h 3 ] ])
// A set of windows found not to combine is not tried again; any other set is.
let threeHere = at (Map [ 1, d1; 2, d1; 3, d1 ])
check "refused set is not tried again"
    ((slots [ g1, [ h 1; h 2; h 3 ] ] Set.empty threeHere Map.empty (Set [ Set [ h 1; h 2; h 3 ] ])).IsEmpty)
check "refused set in another order is not tried again"
    ((slots [ g1, [ h 3; h 1; h 2 ] ] Set.empty threeHere Map.empty (Set [ Set [ h 1; h 2; h 3 ] ])).IsEmpty)
check "refused set: a changed set is tried"
    (slots [ g1, [ h 1; h 2 ] ] Set.empty threeHere Map.empty (Set [ Set [ h 1; h 2; h 3 ] ]) = Map [ k g1 d1, [ h 1; h 2 ] ])
check "refused set: other groups unaffected"
    (slots [ g1, [ h 1; h 2; h 3 ]; g2, [ h 4; h 5 ] ] Set.empty (at (Map [ 1, d1; 2, d1; 3, d1; 4, d1; 5, d1 ])) Map.empty (Set [ Set [ h 1; h 2; h 3 ] ]) =
         Map [ k g2 d1, [ h 4; h 5 ] ])

// --- plan
let ids () =
    let mutable n = 0
    fun () -> n <- n + 1; sprintf "%sid%d" prefix n
let fresh () =
    let mutable n = 0
    fun () -> n <- n + 1; sprintf "%snew%d" prefix n
let applies steps = steps |> List.choose (function Apply(x, id) -> Some(x, id) | _ -> None)
let restores steps = steps |> List.choose (function Restore x -> Some x | _ -> None)

let s1, p1 = plan (ids ()) Map.empty Map.empty (Map [ k g1 d1, [ h 1; h 2; h 3 ] ])
check "first plan writes all, leader first" (applies p1 = [ h 1, prefix + "id1"; h 2, prefix + "id1"; h 3, prefix + "id1" ])
check "first plan leader" (s1.[k g1 d1] = { id = prefix + "id1"; leader = h 1 })
let carried1 = Map [ h 1, prefix + "id1"; h 2, prefix + "id1"; h 3, prefix + "id1" ]
let s2, p2 = plan (ids ()) s1 carried1 (Map [ k g1 d1, [ h 1; h 2; h 3 ] ])
check "steady state does nothing" (p2.IsEmpty && s2 = s1)
let _, p3 = plan (ids ()) s1 carried1 (Map [ k g1 d1, [ h 1; h 2; h 3; h 4 ] ])
check "new tab only" (p3 = [ Apply(h 4, prefix + "id1") ])
let _, p4 = plan (ids ()) s1 carried1 (Map [ k g1 d1, [ h 1; h 3 ] ])
check "leaving tab given back" (p4 = [ Restore(h 2) ])
let _, p5 = plan (ids ()) s1 carried1 (Map [ k g1 d1, [ h 1; h 3; h 2 ] ])
check "reorder behind leader does nothing" (p5.IsEmpty)
let s6, p6 = plan (fresh ()) s1 carried1 (Map [ k g1 d1, [ h 2; h 1; h 3 ] ])
check "leader change: new id, new leader first, nobody restored"
    (p6 = [ Apply(h 2, prefix + "new1"); Apply(h 1, prefix + "new1"); Apply(h 3, prefix + "new1") ] && s6.[k g1 d1].leader = h 2)
let _, p7 = plan (fresh ()) s1 carried1 (Map [ k g1 d1, [ h 1; h 2 ] ] |> Map.add (k g1 d2) [ h 3; h 4 ])
check "window moving to another desktop is rewritten, not restored"
    (restores p7 = [] && applies p7 = [ h 3, prefix + "new1"; h 4, prefix + "new1" ])
let _, p8 = plan (ids ()) s1 carried1 Map.empty
check "off gives everything back" (List.sort (restores p8) = [ h 1; h 2; h 3 ] && applies p8 = [])
let _, p9 = plan (fresh ()) s1 carried1 (Map [ k g1 d1, [ h 2; h 3 ] ])
check "leader leaves: restore first, then new id from the new leader"
    (p9 = [ Restore(h 1); Apply(h 2, prefix + "new1"); Apply(h 3, prefix + "new1") ])
// A declined leader (application set its own ID again) is excluded: it is no
// longer carried, and the next tab leads under a new ID.
let afterDecline = Map [ h 2, prefix + "id1"; h 3, prefix + "id1" ]
let desiredAfterDecline = slots [ g1, [ h 1; h 2; h 3 ] ] (Set [ h 1 ]) (at (Map [ 1, d1; 2, d1; 3, d1 ])) Map.empty Set.empty
let s10, p10 = plan (fresh ()) s1 afterDecline desiredAfterDecline
check "declined leader: next tab leads a new id"
    (p10 = [ Apply(h 2, prefix + "new1"); Apply(h 3, prefix + "new1") ] && s10.[k g1 d1].leader = h 2)
let _, p11 = plan (fresh ()) s1 (Map [ h 1, prefix + "id1"; h 2, prefix + "id1" ])
                  (slots [ g1, [ h 1; h 2; h 3 ] ] (Set [ h 3 ]) (at (Map [ 1, d1; 2, d1; 3, d1 ])) Map.empty Set.empty)
check "declined member: nothing else changes" (p11.IsEmpty)
// plan is stable: applying its own result twice changes nothing
for members in [ [ h 1; h 2 ]; [ h 2; h 1 ]; [ h 1; h 2; h 3 ]; [ h 3; h 1 ] ] do
    let desired = Map [ k g1 d1, members ]
    let s, p = plan (fresh ()) s1 carried1 desired
    let carriedAfter =
        p |> List.fold (fun (c: Map<IntPtr, string>) step ->
            match step with
            | Apply(x, id) -> c.Add(x, id)
            | Restore x -> c.Remove x) carried1
    let _, again = plan (fresh ()) s carriedAfter desired
    check "idempotent" again.IsEmpty

// --- shared membership and stable desktop hand-over
let shared = Set [ h 3; h 4 ]
let sharedSlots groups shown current =
    TaskbarAppIdPolicy.slots groups Set.empty (at (Map [ 1, d1; 2, d2 ])) Map.empty Set.empty shared shown (Some current)
let mixedGroups = [ g1, [ h 3; h 1; h 4; h 2 ] ]
let mixedA = sharedSlots mixedGroups (Set [ g1 ]) d1
let mixedB = sharedSlots mixedGroups (Set [ g1 ]) d2
check "bound first despite shared tab order" (mixedA = Map [ k g1 d1, [ h 1; h 3; h 4 ]; k g1 d2, [ h 2 ] ])
let sharedIds = ids ()
let mixedState, mixedSteps = plan sharedIds Map.empty Map.empty mixedA
let mixedCarry = applies mixedSteps |> Map.ofList
let switched, switchSteps, switchWaiting = settledSharedPlan sharedIds mixedA mixedState mixedCarry mixedB shared
check "switch retains both slot IDs" (switched = mixedState)
check "switch writes only shared windows without restoration" (switchSteps = [ Apply(h 3, mixedState.[k g1 d2].id); Apply(h 4, mixedState.[k g1 d2].id) ] && not switchWaiting)
let onlyA = sharedSlots [g1, [h 3; h 4]] (Set [g1]) d1
let onlyB = sharedSlots [g1, [h 3; h 4]] (Set [g1]) d2
check "shared-only key survives switch" (onlyA = onlyB && onlyA = Map [k g1 sharedDesktop, [h 3; h 4]])
let onlyState, onlySteps = plan sharedIds Map.empty Map.empty onlyA
let onlyNext, onlyAgain = plan sharedIds onlyState (applies onlySteps |> Map.ofList) onlyB
check "shared-only ID survives switch" (onlyState = onlyNext && onlyAgain.IsEmpty)
check "shared-only verification on either desktop" (verificationHere (Some d1) sharedDesktop (fun () -> false) && verificationHere (Some d2) sharedDesktop (fun () -> false))
let overlapping = [g1, [h 3; h 1]; g2, [h 3; h 2]]
check "shown group wins shared ownership" ((sharedSlots overlapping (Set [g2]) d2).[k g2 d2] = [h 2; h 3])
check "shared ownership ties use lowest handle" ((sharedSlots (List.rev overlapping) (Set [g1; g2]) d1).[k g1 d1] = [h 1; h 3])
let departed = slots mixedGroups (Set [h 3; h 4]) (at (Map [1,d1; 2,d2])) Map.empty Set.empty
let departureState, departureSteps = plan sharedIds mixedState mixedCarry departed
check "shared departure preserves bound IDs" (departureState = mixedState && restores departureSteps = [h 3; h 4])
let joinedState, joinedSteps = plan sharedIds departureState (mixedCarry.Remove(h 3).Remove(h 4)) mixedA
check "shared joining preserves bound IDs" (joinedState = mixedState && (applies joinedSteps |> List.map fst) = [h 3; h 4])
check "refused singleton remains refused" ((slots [g1,[h 1]] Set.empty (at (Map [1,d1])) Map.empty (Set [Set [h 1]])).IsEmpty)

let switchedCarry = switchSteps |> List.fold (fun c step -> match step with Apply(h,id) -> Map.add h id c | Restore h -> Map.remove h c) mixedCarry
let backState, backSteps, _ = settledSharedPlan sharedIds mixedB switched switchedCarry mixedA shared
check "switch back preserves IDs and writes only shared windows" (backState = mixedState && backSteps = [Apply(h 3,mixedState.[k g1 d1].id); Apply(h 4,mixedState.[k g1 d1].id)])
check "shared singleton has dedicated slot" (sharedSlots [g1,[h 3]] (Set [g1]) d1 = Map [k g1 sharedDesktop,[h 3]])
let filedShared = Map [h 1,k g1 d1; h 2,k g1 d2; h 3,k g1 d1; h 4,k g1 d1]
check "unknown current keeps shared filing" (TaskbarAppIdPolicy.slots mixedGroups Set.empty (at (Map [1,d1; 2,d2])) filedShared Set.empty shared (Set [g1]) None = mixedA)
check "refused shared set stays refused" ((TaskbarAppIdPolicy.slots [g1,[h 3;h 4]] Set.empty (fun _ -> Unknown) Map.empty (Set [shared]) shared (Set [g1]) (Some d1)).IsEmpty)
let singletonState, singletonSteps = plan sharedIds Map.empty Map.empty lone
check "singletons get different IDs" (singletonState.[k g1 d1].id <> singletonState.[k g1 d2].id && (applies singletonSteps).Length = 2)

// --- settling: a slot is written once it reads the same on two passes
let want = Map [ k g1 d1, [ h 1; h 2; h 3 ] ]
let sa, pa, wa = settledPlan (ids ()) Map.empty Map.empty Map.empty want
check "new slot waits a pass" (pa.IsEmpty && sa.IsEmpty && wa)
let sb, pb, wb = settledPlan (ids ()) want Map.empty Map.empty want
check "same twice: written" (applies pb = [ h 1, prefix + "id1"; h 2, prefix + "id1"; h 3, prefix + "id1" ] && not wb && sb.[k g1 d1].leader = h 1)
let _, pc, wc = settledPlan (ids ()) want s1 carried1 want
check "settled steady state does nothing" (pc.IsEmpty && not wc)
// A tab dragged to the front: the button keeps its ID until the order holds.
let front = Map [ k g1 d1, [ h 2; h 1; h 3 ] ]
let sd, pd, wd = settledPlan (fresh ()) want s1 carried1 front
check "leader change waits: no new id yet" (pd.IsEmpty && wd && sd.[k g1 d1] = s1.[k g1 d1])
let se, pe, we = settledPlan (fresh ()) front sd carried1 front
check "leader change once settled: new id from new leader"
    (applies pe = [ h 2, prefix + "new1"; h 1, prefix + "new1"; h 3, prefix + "new1" ] && not we && se.[k g1 d1].leader = h 2)
// A window joining waits; nothing else is written meanwhile.
let more = Map [ k g1 d1, [ h 1; h 2; h 3; h 4 ] ]
let _, pf, wf = settledPlan (ids ()) want s1 carried1 more
check "joining window waits" (pf.IsEmpty && wf)
let _, pg, _ = settledPlan (ids ()) more s1 carried1 more
check "joining window written once settled" (pg = [ Apply(h 4, prefix + "id1") ])
// A window leaving is given back at once; the rest keep the ID.
let less = Map [ k g1 d1, [ h 1; h 3 ] ]
let sh, ph, wh = settledPlan (fresh ()) want s1 carried1 less
check "leaving window given back at once" (ph = [ Restore(h 2) ] && wh && sh.[k g1 d1] = s1.[k g1 d1])
// The window that named the button leaves: the others keep the ID while
// waiting; once settled the new first tab names a new ID.
let noLeader = Map [ k g1 d1, [ h 2; h 3 ] ]
let si, pi, _ = settledPlan (fresh ()) want s1 carried1 noLeader
check "leader leaves: given back, ID kept while waiting"
    (pi = [ Restore(h 1) ] && si.[k g1 d1] = s1.[k g1 d1])
let carriedAfterLeave = carried1.Remove(h 1)
let _, pj, _ = settledPlan (fresh ()) noLeader si carriedAfterLeave noLeader
check "leader left: renamed once settled"
    (applies pj = [ h 2, prefix + "new1"; h 3, prefix + "new1" ])
check "off: everything given back at once"
    (let _, p, w = settledPlan (ids ()) want s1 carried1 Map.empty in List.sort (restores p) = [ h 1; h 2; h 3 ] && applies p = [] && not w)
// A slot that changes on every pass is never written: no churn of IDs.
let mutable last = Map.empty
let mutable st = Map.empty
let mutable writes = 0
for i in 1 .. 10 do
    let d = Map [ k g1 d1, (if i % 2 = 0 then [ h 1; h 2 ] else [ h 2; h 1 ]) ]
    let s, p, _ = settledPlan (fresh ()) last st Map.empty d
    writes <- writes + (applies p).Length
    last <- d
    st <- s
check "flapping order writes nothing" (writes = 0)

// --- reading the taskbar
check "appid" (buttonAppId "Appid: WindowTabs.Group.x" = Some "WindowTabs.Group.x")
check "no appid" (buttonAppId "something" = None && buttonAppId null = None)
check "count en" (windowCount "Some App - 3 running windows" = Some 3)
check "count ja" (windowCount "Some App - 12 個の実行中ウィンドウ" = Some 12)
check "count uses last dash" (windowCount "A - B - 2 running windows" = Some 2)
check "no count" (windowCount "Some App" = None && windowCount null = None)
check "digits glued to words are not a count" (windowCount "App - 2x" = None)
check "year-like long number rejected" (windowCount "App - 20245 x" = None)
check "count followed by a pinned mark" (windowCount "Some App - 3 個の実行中ウィンドウ 固定済み" = Some 3)
let buttons = [ "Appid: " + prefix + "a", "X - 3 running windows"
                "Appid: " + prefix + "b", "Y"
                "Appid: Other.App", "Z - 4 running windows" ]
check "counts by id" (countsById true false [ buttons ] = Some(Map [ prefix + "a", 3; prefix + "b", 1 ]))
let twoButtons = [ "Appid: " + prefix + "a", "X - 3 running windows"
                   "Appid: " + prefix + "a", "X - 1 running window" ]
check "two buttons of one id on one taskbar are not one button" (countsById true false [ twoButtons ] = Some(Map [ prefix + "a", splitButton ]))
check "two buttons on one taskbar, split mode too" (countsById true true [ twoButtons; [] ] = Some(Map [ prefix + "a", splitButton ]))
check "not combined: no count" (countsById false false [ buttons ] = None)
check "unreadable taskbar" (countsById true false [ [ ("", "X - 2 running windows") ] ] = None)
check "no taskbar" (countsById true false [] = None)
check "readable, none ours" (countsById true false [ [ ("Appid: Other.App", "Z") ] ] = Some Map.empty)
let monitorA = [ ("Appid: " + prefix + "a", "X - 3 running windows") ]
let monitorB = [ ("Appid: " + prefix + "a", "X - 3 running windows") ]
check "all taskbars list every window once each" (countsById true false [ monitorA; monitorB ] = Some(Map [ prefix + "a", 3 ]))
check "main plus own taskbar" (countsById true false [ monitorA; [ ("Appid: " + prefix + "a", "X - 1 running window") ] ] = Some(Map [ prefix + "a", 3 ]))
check "split taskbars add up" (countsById true true [ monitorA; [ ("Appid: " + prefix + "a", "X - 2 running windows") ] ] = Some(Map [ prefix + "a", 5 ]))
check "empty secondary taskbar" (countsById true false [ monitorA; [] ] = Some(Map [ prefix + "a", 3 ]))
check "mode 2 is split" (splitAcrossTaskbars (Some 2) && not (splitAcrossTaskbars (Some 0)) && not (splitAcrossTaskbars (Some 1)) && not (splitAcrossTaskbars None))
check "glom: always combined" (alwaysCombined None && alwaysCombined (Some 0) && not (alwaysCombined (Some 1)) && not (alwaysCombined (Some 2)))

// --- verification
let counts = Some(Map [ ("a", 3) ])
let mutable reads = 0
let values (v: (IntPtr * string option option) list) = fun () -> reads <- reads + 1; v
let carrying = values [ h 1, Some(Some "a"); h 2, Some(Some "a"); h 3, Some(Some "a"); h 4, Some(Some "a") ]
check "merged" (verdict false 3 counts "a" carrying = Merged)
check "more than expected is merged" (verdict false 2 counts "a" carrying = Merged)
check "merged reads no values" (reads = 0)
check "short rebuilds once" (verdict false 4 counts "a" carrying = RebuildOnce)
check "short reads values" (reads = 1)
check "missing button rebuilds once" (verdict false 2 counts "b" (values [ h 1, Some(Some "b") ]) = RebuildOnce)
check "short after rebuild stops" (verdict true 4 counts "a" carrying = Short)
check "unreadable taskbar: no rebuild on a guess" (verdict false 2 None "a" carrying = Unverified)
check "unreadable after rebuild stops" (verdict true 2 None "a" carrying = Unverified)
check "nothing visible is merged" (verdict false 0 (Some Map.empty) "a" carrying = Merged)
check "two buttons are not merged" (verdict false 2 (Some(Map [ ("a", splitButton) ])) "a" carrying = RebuildOnce)
check "two buttons after rebuild: short" (verdict true 2 (Some(Map [ ("a", splitButton) ])) "a" carrying = Short)
let resetter = values [ h 1, Some(Some "Vendor.App"); h 2, Some(Some "a"); h 3, Some(Some "a") ]
check "application set its own again: declined before any rebuild" (verdict false 3 (Some(Map [ ("a", 2) ])) "a" resetter = Declined [ h 1 ])
check "declined after the rebuild too" (verdict true 3 (Some(Map [ ("a", 2) ])) "a" resetter = Declined [ h 1 ])
check "declined when the taskbar is unreadable" (verdict false 3 None "a" resetter = Declined [ h 1 ])
check "emptied value is declined" (verdict false 2 (Some Map.empty) "a" (values [ h 1, Some None; h 2, Some(Some "a") ]) = Declined [ h 1 ])
check "unreadable value is not declined" (verdict false 2 (Some Map.empty) "a" (values [ h 1, None; h 2, Some(Some "a") ]) = RebuildOnce)
// Given back: judged on the button of the ID the window carried, by itself.
check "given: refiled" (givenVerdict false (Some(Map [ ("a", 2) ])) (Some "a") 2 = Refiled)
check "given: gone from the button" (givenVerdict false (Some Map.empty) (Some "a") 0 = Refiled)
check "given: one too many rebuilds" (givenVerdict false (Some(Map [ ("a", 3) ])) (Some "a") 2 = RebuildGiven)
check "given: still too many after the rebuild stops" (givenVerdict true (Some(Map [ ("a", 3) ])) (Some "a") 2 = GivenStuck)
check "given: other buttons of ours do not matter" (givenVerdict false (Some(Map [ ("b", 4) ])) (Some "a") 0 = Refiled)
check "given: two buttons of its ID" (givenVerdict false (Some(Map [ ("a", splitButton) ])) (Some "a") 0 = RebuildGiven)
check "given: unreadable taskbar" (givenVerdict false None (Some "a") 0 = GivenUnverified)
check "given: no ID known" (givenVerdict false (Some(Map [ ("a", 3) ])) None 0 = GivenUnverified)
check "settle" (not (settled 1000 900) && settled 1500 1000)
check "settle across tick wrap" (settled (Int32.MinValue + 600) (Int32.MaxValue - 10))

// --- recovery file
let entries =
    [ { hwnd = 0x1A2BL; pid = 0x10u; started = 123L; phase = Applied; secret = Some "QUJD+/0="; id = None; previous = None; cookie = None; pin = false }
      { hwnd = 0x30L; pid = 0x20u; started = 5L; phase = Returned; secret = None; id = Some(prefix + "0123abcd"); previous = None; cookie = None; pin = false } ]
check "round trip" (parse (format entries) = (entries, false))
check "no plain text of a value" (not ((format entries).Contains("Vendor")))
check "an id that is not ours is never written" ((format [ { entries.[1] with id = Some "Vendor.App" } ]).Contains("Vendor") |> not)
let damaged, flagged = parse ("zz q\r\n" + format entries + "\r\n1 2\r\n1 2 3 X -\r\n")
check "garbage lines skipped" (damaged = entries)
check "garbage lines flagged" flagged
check "previous build's lines: no id"
    (parse "1A 10 5 R -\r\n" = ([ { hwnd = 0x1AL; pid = 0x10u; started = 5L; phase = Returned; secret = None; id = None; previous = None; cookie = None; pin = false } ], false))
check "earliest build's lines: applied, no own value"
    (parse "1A 10 5\r\n" = ([ { hwnd = 0x1AL; pid = 0x10u; started = 5L; phase = Applied; secret = Some "unknown-original"; id = None; previous = None; cookie = None; pin = false } ], false))
check "unreadable field is flagged" (parse "1A 10 zz" = ([], true))
check "empty" (parse "" = ([], false) && parse null = ([], false))
check "same window" (identity entries.[0] true 0x10u (Some 123L) = Same)
check "closed window" (identity entries.[0] false 0x10u (Some 123L) = Gone)
check "reused handle" (identity entries.[0] true 0x11u (Some 123L) = Gone)
check "reused pid" (identity entries.[0] true 0x10u (Some 1L) = Gone)
check "unreadable process: undecided, never dropped" (identity entries.[0] true 0x10u None = Undecided)

let generation = { entries.[0] with id = Some(prefix + "new"); previous = Some(prefix + "old"); cookie = Some 123 }
check "generation round trip" (parse (format [generation]) = ([generation], false))
check "new generation owned" (ownsGeneration generation (Some(prefix + "new")))
check "old generation owned during write" (ownsGeneration generation (Some(prefix + "old")))
check "unrelated prefix not owned" (not (ownsGeneration generation (Some(prefix + "other"))))
check "damaged handle retained" (damagedHandles "1A bad pid\r\nzz 2" = Set.ofList [nativeint 0x1A])
check "good handle not quarantined" (damagedHandles (format entries) = Set.empty)
check "transient count retries once" (retryCount false TemporaryFailure && not (retryCount true TemporaryFailure))
check "configuration never retries" (not (retryCount false Configuration))
check "secondary noncombined excluded" (not (includeSecondary (Some 1) (Some 2)))
check "secondary disabled ignored" (not (includeSecondary (Some 0) (Some 0)))
check "secondary combined included" (includeSecondary (Some 1) (Some 0))
check "fresh recheck accepts unchanged original" (canApply true (Some "value-1") (Some(Some "value-1")))
check "fresh recheck refuses changed original" (not (canApply true (Some "value-1") (Some(Some "value-2"))))
check "fresh recheck refuses changed identity" (not (canApply false None (Some None)))
check "fresh recheck refuses unreadable value" (not (canApply true None None))
check "rewrite refuses another generation" (rewriteGeneration (prefix + "new") (Some(Some(prefix + "other"))) = ApplicationChanged)
check "rewrite distinguishes unreadable" (rewriteGeneration (prefix + "new") None = Unreadable)
check "legacy generation not inferred" (not (ownsGeneration entries.[0] (Some(prefix + "old"))))
// Recovery excludes returned windows until Shell confirmation. The snapshot
// itself stays identical throughout the following planning transitions.
let recoveredDesired = Map.ofList [k g1 d1, [h 1; h 2]]
let recoveringSlots, recoveringSteps, recoveringWait = settledPlan (fun () -> prefix + "recovered") Map.empty Map.empty Map.empty Map.empty
check "recovery gate initially writes nothing" (recoveringSteps.IsEmpty && not recoveringWait)
let completedReturn = givenVerdict false (Some Map.empty) (Some(prefix + "old")) 0 = Refiled
check "on recovery requests another pass" (resumeAfterReturn true completedReturn)
let settlingSlots, settlingSteps, settlingWait = settledPlan (fun () -> prefix + "recovered") Map.empty recoveringSlots Map.empty recoveredDesired
check "same snapshot resumes settling" (settlingSteps.IsEmpty && settlingWait)
let regroupedSlots, regroupedSteps, regroupedWait = settledPlan (fun () -> prefix + "recovered") recoveredDesired settlingSlots Map.empty recoveredDesired
check "same snapshot regroups after settling" (regroupedSteps.Length = 2 && not regroupedWait)
let _, stableSteps, stableWait = settledPlan (fun () -> failwith "Unexpected rotation") recoveredDesired regroupedSlots (Map.ofList [h 1, prefix + "recovered"; h 2, prefix + "recovered"]) recoveredDesired
check "completed recovery stops requesting passes" (stableSteps.IsEmpty && not stableWait && not (resumeAfterReturn true false))
check "off recovery does not regroup" (not (resumeAfterReturn false completedReturn))
check "disable waits for settling" (not (givenCheckDue false 1100 1000) && givenCheckDue false 1500 1000)
check "only exit skips settling" (givenCheckDue true 1100 1000)
check "disable first failure requests one observation" (retryGivenCount false false TemporaryFailure)
check "disable second failure stops" (not (retryGivenCount false true TemporaryFailure))
check "disable next successful observation completes" (givenVerdict false (countMap (Counted Map.empty)) (Some(prefix + "old")) 0 = Refiled)
check "exit failure remains durable without retry" (not (retryGivenCount true false TemporaryFailure))
check "configuration return completes on verified value" (returnWithoutShell false Configuration)
check "first unavailable return still retries" (not (returnWithoutShell false TemporaryFailure))
check "repeated unavailable return completes on verified value" (returnWithoutShell true TemporaryFailure)
check "countable incomplete return cannot bypass Shell" (not (returnWithoutShell true (Counted Map.empty)))
check "unknown desktop checks windows that are here" (verificationHere None d1 (fun () -> true))
check "unknown desktop does not check away windows" (not (verificationHere None d1 (fun () -> false)))
check "known desktop keeps separation" (not (verificationHere (Some d1) d2 (fun () -> true)))
check "no virtual desktops uses ordinary confirmation" (verificationHere (Some Guid.Empty) Guid.Empty (fun () -> failwith "Unexpected native read"))
check "value-only return releases replan gate" (resumeAfterReturn true (returnWithoutShell true TemporaryFailure))
check "rebuild wobble keeps next observation" (needsSettledObservation 1100 1000 (fun () -> false))
check "settled away window does not keep waking" (not (needsSettledObservation 1600 1000 (fun () -> false)))
check "settled current window remains observable" (needsSettledObservation 1600 1000 (fun () -> true))
// When promotion is unavailable, app pins are excluded and window pins remain shared.
check "app pin sentinel" (isAppPinned (Some appPinnedDesktop))
check "window pin sentinel remains eligible" (not (isAppPinned (Some windowPinnedDesktop)))
check "ordinary desktop remains eligible" (not (isAppPinned (Some d1)))
check "missing desktop is not app pinning" (not (isAppPinned None))
let pinExcluded = [h 1, Some appPinnedDesktop; h 2, Some windowPinnedDesktop; h 3, Some d1]
                  |> List.choose (fun (hwnd, desktop) -> if isAppPinned desktop then Some hwnd else None) |> Set.ofList
let pinDesired = TaskbarAppIdPolicy.slots [g1, [h 1; h 2; h 3]] pinExcluded (fun _ -> OnDesktop d1) Map.empty Set.empty (Set [h 1; h 2]) (Set [g1]) (Some d1)
check "app pinned member excluded while remaining members group" (pinDesired = Map [k g1 d1, [h 3; h 2]])
let _, pinSteps = plan (fun () -> prefix + "pin") Map.empty (Map [h 1, prefix + "old"]) pinDesired
check "previously assigned app pinned member restored" (List.contains (Restore(h 1)) pinSteps && not (pinSteps |> List.exists (function Apply(hwnd, _) -> hwnd = h 1 | _ -> false)))

// A failed promotion keeps app-pinned windows excluded on every settled pass.
let onlyAppPins = TaskbarAppIdPolicy.slots [g1, [h 1; h 2]] (Set [h 1; h 2]) (fun _ -> OnDesktop d1) Map.empty Set.empty (Set [h 1; h 2]) (Set [g1]) (Some d1)
let safeSlots, safeSteps, safeWaiting = settledSharedPlan (fun () -> failwith "Unexpected assignment") Map.empty Map.empty Map.empty onlyAppPins (Set [h 1; h 2])
check "unpromoted app pins produce no writes or waiting" (safeSlots.IsEmpty && safeSteps.IsEmpty && not safeWaiting)
let _, safeRepeat, safeRepeatWaiting = settledSharedPlan (fun () -> failwith "Unexpected assignment") onlyAppPins safeSlots Map.empty onlyAppPins (Set [h 1; h 2])
check "unpromoted app pins stay idle on repeated observations" (safeRepeat.IsEmpty && not safeRepeatWaiting)
let _, restoreOnly = plan (fun () -> failwith "Unexpected assignment") Map.empty (Map [h 1, prefix + "old"]) onlyAppPins
check "unpromoted app pins only return an existing assignment" (restoreOnly = [Restore(h 1)])

// Promotion intent must survive every crash point without claiming existing pins.
let promoted = { generation with pin = true }
check "pin intent journal round trip" (parse (format [promoted]) = ([promoted], false))
let oldGenerationRow = format [generation] |> fun row -> row.Substring(0, row.LastIndexOf(' '))
check "eight field legacy row has no owned pin" (parse oldGenerationRow = ([generation], false))
check "invalid pin ownership is quarantined" (parse (oldGenerationRow + " invalid") = ([], true))
check "malformed pin row retains handle" (damagedHandles (oldGenerationRow + " invalid") = Set [nativeint generation.hwnd])
check "own window pin removed before ID return" (unpinBeforeReturn promoted promoted.id (Some windowPinnedDesktop))
check "previous generation pin removed during interrupted rotation" (unpinBeforeReturn promoted promoted.previous (Some windowPinnedDesktop))
check "preexisting window pin untouched" (not (unpinBeforeReturn generation generation.id (Some windowPinnedDesktop)))
check "restored original never unpinned" (not (unpinBeforeReturn promoted (Some "value-1") (Some appPinnedDesktop)))
check "restored original with window sentinel never unpinned" (not (unpinBeforeReturn promoted (Some "value-1") (Some windowPinnedDesktop)))
check "unrelated generation never unpinned" (not (unpinBeforeReturn promoted (Some(prefix + "other")) (Some windowPinnedDesktop)))
check "pending ID change does not unpin app" (not (unpinBeforeReturn promoted promoted.id (Some appPinnedDesktop)))
check "crash before pin needs no unpin" (not (unpinBeforeReturn promoted promoted.id (Some d1)))
check "unknown desktop does not authorize unpin" (not (unpinBeforeReturn promoted promoted.id None))
check "pin return requires app sentinel" (pinReturnVerified promoted (Some appPinnedDesktop))
check "pin return rejects bound window" (not (pinReturnVerified promoted (Some d1)))
check "pin return rejects lingering window pin" (not (pinReturnVerified promoted (Some windowPinnedDesktop)))
check "ordinary return has no pin requirement" (pinReturnVerified generation None)
check "live app pin with durable intent can write" (pinWriteAllowed true false (Some appPinnedDesktop))
check "late app pin without intent cannot write" (not (pinWriteAllowed false false (Some appPinnedDesktop)))
check "new user window pin aborts planned promotion" (not (pinWriteAllowed true false (Some windowPinnedDesktop)))
check "owned pin may rotate ID" (pinWriteAllowed true true (Some windowPinnedDesktop))
check "lost owned pin cannot rotate ID" (not (pinWriteAllowed true true (Some d1)))
check "unknown owned pin cannot rotate ID" (not (pinWriteAllowed true true None))
let promotedDesired = TaskbarAppIdPolicy.slots [g1, [h 1; h 2]] Set.empty (fun _ -> OnDesktop d1) Map.empty Set.empty (Set [h 1]) (Set [g1]) (Some d1)
check "promoted app pin shares current group with bound leader" (promotedDesired = Map [k g1 d1, [h 2; h 1]])


check "unknown desktop cannot write" (not (pinWriteAllowed false false None))
check "unsupported desktop can write" (pinWriteAllowed false false (Some Guid.Empty))
check "early old classification waits" (pinObservation 1100 1000 false (Some appPinnedDesktop) = PinWait)
check "early unreadable classification waits" (pinObservation 1100 1000 false None = PinWait)
check "expired classification returns" (pinObservation 1500 1000 false None = PinExpired)
check "ordinary classification pins immediately" (pinObservation 1000 1000 false (Some d1) = PinIssue)
check "successful command awaits reflection" (pinObservation 1100 1000 true (Some d1) = PinWait)
check "successful command not repeated" (pinObservation 1500 1000 true (Some d1) = PinExpired)
check "observed pin completes" (pinObservation 1100 1000 true (Some windowPinnedDesktop) = PinReady)
check "disabled shared return stays due" (returnDesktop (Some appPinnedDesktop) = Some sharedDesktop)
let startupGroups = [g1, [h 1; h 2]]
let startupShared = Set [h 2]
let temporaryDesired = Map [k g1 sharedDesktop, [h 2]]
let gatedDesired = deferReturningGroups startupGroups (Set [h 1]) startupShared temporaryDesired
let _, gatedSteps, _ = settledSharedPlan (fun () -> failwith "Temporary shared slot") Map.empty Map.empty Map.empty gatedDesired startupShared
check "startup returning bound tab blocks temporary shared slot" (gatedSteps.IsEmpty)
let finalDesired = Map [k g1 d1, [h 1; h 2]]
let startupNext, startupSteps, startupWaiting = settledSharedPlan (fun () -> prefix + "startup") gatedDesired Map.empty Map.empty finalDesired startupShared
check "startup waits without shared restore" (startupSteps.IsEmpty && startupWaiting)
let _, startupFinal, _ = settledSharedPlan (fun () -> prefix + "startup") finalDesired startupNext Map.empty finalDesired startupShared
check "startup assigns once after return" (startupFinal = [Apply(h 1, prefix + "startup"); Apply(h 2, prefix + "startup")])
let permanentDesired = deferReturningGroups [g1, [h 2]] Set.empty startupShared temporaryDesired
let permanentSlots, permanentSteps, permanentWait = settledSharedPlan (fun () -> prefix + "shared") Map.empty Map.empty Map.empty permanentDesired startupShared
check "permanent shared slot remains immediate" (permanentSteps.Length = 1 && not permanentWait)
let cache = retainSharedSlots startupGroups startupShared permanentSlots (Map [k g1 d1, { id = prefix + "bound"; leader = h 1 }])
let _, revisit = plan (fun () -> failwith "Unexpected shared ID") cache (Map [h 2, prefix + "bound"]) temporaryDesired
check "shared slot ID survives away and back" (revisit = [Apply(h 2, prefix + "shared")])
check "removed group drops shared cache" ((retainSharedSlots [] startupShared permanentSlots Map.empty).IsEmpty)
check "removed shared leader drops cache" ((retainSharedSlots startupGroups Set.empty permanentSlots Map.empty).IsEmpty)

// Keep an already promoted member through return and destination settling.
let priorCarry = Map [h 2, prefix + "prior"]
let priorFiled = Map [h 2, k g1 d2]
let priorSlot = Map [k g1 d2, { id = prefix + "prior"; leader = h 2 }]
let _, returnGateSteps, _ = settledSharedPlan (fun () -> prefix + "next") Map.empty priorSlot priorCarry gatedDesired startupShared
check "returning group retains promoted shared member" ((retainWaitingShared startupShared priorFiled gatedDesired temporaryDesired returnGateSteps).IsEmpty)
check "departed shared member returns" (retainWaitingShared startupShared priorFiled gatedDesired Map.empty returnGateSteps = [Restore(h 2)])
check "excluded shared member returns" (retainWaitingShared Set.empty priorFiled gatedDesired temporaryDesired returnGateSteps = [Restore(h 2)])
let returnGateSlot, returnGateNextSteps, returnGateWaiting = settledSharedPlan (fun () -> prefix + "next") gatedDesired priorSlot priorCarry finalDesired startupShared
check "destination settling retains shared assignment" (returnGateWaiting && (retainWaitingShared startupShared priorFiled finalDesired finalDesired returnGateNextSteps).IsEmpty)
let _, directSteps, _ = settledSharedPlan (fun () -> prefix + "next") finalDesired returnGateSlot priorCarry finalDesired startupShared
check "accepted destination rotates directly" (directSteps = [Apply(h 1, prefix + "next"); Apply(h 2, prefix + "next")])
let pausedDesired = deferReturningGroups startupGroups Set.empty startupShared temporaryDesired
check "paused return does not block shared destination" (pausedDesired = temporaryDesired)

// A shared member moved to another returning group must leave its old button.
let movedGroups = [g1, [h 1]; g2, [h 3; h 2]]
let movedFiled = Map [h 2, k g1 d1]
let movedEligible = TaskbarAppIdPolicy.slots movedGroups (Set [h 3]) (at (Map [1, d1; 3, d2])) movedFiled Set.empty startupShared (Set [g1; g2]) (Some d1)
let movedDesired = deferReturningGroups movedGroups (Set [h 3]) startupShared movedEligible
let movedPrevious = Map [k g1 d1, { id = prefix + "prior"; leader = h 1 }]
let _, movedSteps, _ = settledSharedPlan (fun () -> prefix + "next") Map.empty movedPrevious priorCarry movedDesired startupShared
check "cross-group returning destination is deferred" (movedEligible.ContainsKey (k g2 sharedDesktop) && not (movedDesired.ContainsKey (k g2 sharedDesktop)))
check "cross-group deferred member returns" (retainWaitingShared startupShared movedFiled movedDesired movedEligible movedSteps = [Restore(h 2)])
let movingDesired = TaskbarAppIdPolicy.slots movedGroups Set.empty (at (Map [1, d1; 3, d1])) movedFiled Set.empty startupShared (Set [g1; g2]) (Some d1)
let _, movingSteps, movingWaiting = settledSharedPlan (fun () -> prefix + "next") Map.empty movedPrevious priorCarry movingDesired startupShared
check "cross-group accepted destination retains while settling" (movingWaiting && movingSteps = [Restore(h 2)] && (retainWaitingShared startupShared movedFiled movingDesired movingDesired movingSteps).IsEmpty)
check "unknown prior group cannot retain deferred member" (retainWaitingShared startupShared Map.empty movedDesired movedEligible movedSteps = [Restore(h 2)])

// Off trusts only the live prefix; On retains identity and generation checks.
let liveOurs = Some(Some(prefix + "new"))
check "off stale cookie and generation" (offReturn false liveOurs)
check "on stale generation stays cautious" (not (offReturn true liveOurs))
check "off unreadable ID is untouched" (not (offReturn false None))
check "off empty ID is untouched" (not (offReturn false (Some None)))
check "off application ID is untouched" (not (offReturn false (Some(Some "Vendor.App"))))
check "off prefix is ordinal" (not (offReturn false (Some(Some "windowtabs.Group.x"))))
check "off prefix must lead" (not (offReturn false (Some(Some("x" + prefix)))))
check "off original restored" (offOriginal (Some(Some "Vendor.App")) = Some "Vendor.App")
check "off original empty" (offOriginal (Some None) = None)
check "off missing or unreadable original cleared" (offOriginal None = None)
let staleOff : Written =
    { hwnd = 1L; pid = 2u; started = 3L; phase = Applied; secret = None
      id = Some(prefix + "old"); previous = Some(prefix + "older"); cookie = Some 4; pin = true }
check "off ignores mismatched recorded IDs" (not (ownsGeneration staleOff (Option.flatten liveOurs)) && offReturn false liveOurs)
check "off accepts reused hwnd" (identity staleOff true 5u (Some 6L) = Gone && offReturn false liveOurs)
check "off accepts undecided identity" (identity staleOff true 2u None = Undecided && offReturn false liveOurs)
let adoptedOff = { staleOff with id = Option.flatten liveOurs; previous = None; phase = Returned }
check "off adopted current ID unpins P row" (unpinBeforeReturn adoptedOff (Option.flatten liveOurs) (Some windowPinnedDesktop))
check "off unpinned row leaves pin alone" (not (unpinBeforeReturn { adoptedOff with pin = false } (Option.flatten liveOurs) (Some windowPinnedDesktop)))
check "off adopted ID checks shell count" (givenVerdict false (Some(Map [prefix + "new", 1])) adoptedOff.id 0 = RebuildGiven)
check "off adopted ID can finish" (givenVerdict false (Some Map.empty) adoptedOff.id 0 = Refiled)

// An original and pin intent belong to the recorded process lifetime, not just hwnd.
let originalRow = { staleOff with secret = Some "encrypted-original" }
let returnFields alive pid started record =
    let source = offSource alive pid started record
    source |> Option.bind (fun e -> e.secret), source |> Option.exists (fun e -> e.pin)
check "off matching process keeps original and pin" (returnFields true 2u (Some 3L) (Some originalRow) = (Some "encrypted-original", true))
check "off reused process clears original and pin" (returnFields true 9u (Some 3L) (Some originalRow) = (None, false))
check "off reused process lifetime clears original and pin" (returnFields true 2u (Some 9L) (Some originalRow) = (None, false))
check "off unknown lifetime clears original and pin" (returnFields true 2u None (Some originalRow) = (None, false))
check "off missing row clears original and pin" (returnFields true 2u (Some 3L) None = (None, false))
check "off closed window does not inherit original" (returnFields false 2u (Some 3L) (Some originalRow) = (None, false))
check "off cookie does not decide original and pin" (returnFields true 2u (Some 3L) (Some { originalRow with cookie = Some 99 }) = (Some "encrypted-original", true))
check "off legacy cookie keeps matching original" (returnFields true 2u (Some 3L) (Some { originalRow with cookie = None }) = (Some "encrypted-original", true))
// Failed preparation protects old rows from Gone and non-ours cleanup until a save succeeds.
check "off failed preparation protects reused handle row" (not (forgetGone true Gone))
check "off failed preparation protects undecided row" (not (forgetGone true Undecided))
check "off completed preparation allows Gone cleanup" (forgetGone false Gone)
check "off normal cleanup retains live row" (not (forgetGone false Same))
check "off non-ours stuck can leave" (discardOffStuck false (Some(Some "Vendor.App")))
check "off empty stuck can leave" (discardOffStuck false (Some None))
check "off unreadable stuck stays" (not (discardOffStuck false None))
check "off ours stuck stays" (not (discardOffStuck false liveOurs))
check "off failed preparation retains row before durable removal" (not (discardOffStuck true (Some None)))

// Follow the durable and in-memory states across failure, closure, and recovery.
let closedHandle, reusedHandle = h 51, h 52
let closedRow = { originalRow with hwnd = int64 closedHandle }
let reusedRow = { originalRow with hwnd = int64 reusedHandle }
let mutable durableOffRows = Map [closedHandle, closedRow; reusedHandle, reusedRow]
let mutable pendingOffRows = Set [closedHandle; reusedHandle]
let mutable memoryOffRows = durableOffRows
let mutable offSaveAttempts = 0
let cleanupOffScenario live canSave =
    let closed = Set.difference pendingOffRows live
    if not closed.IsEmpty then
        offSaveAttempts <- offSaveAttempts + 1
        let next = durableOffRows |> Map.filter (fun hwnd _ -> not (closed.Contains hwnd))
        if canSave then durableOffRows <- next
        let pending, rows = finishClosedOff canSave closed pendingOffRows memoryOffRows
        pendingOffRows <- pending
        memoryOffRows <- rows
// Preparation has failed; both old rows are protected and the handles still live.
cleanupOffScenario (Set [closedHandle; reusedHandle]) false
check "closed off live failed targets stay protected without removal save" (offSaveAttempts = 0 && memoryOffRows = durableOffRows && pendingOffRows.Count = 2)
// One handle closes while journal writes are still failing.
cleanupOffScenario (Set [reusedHandle]) false
check "closed off failed deletion retains old durable row" (durableOffRows.ContainsKey closedHandle && durableOffRows.[closedHandle] = closedRow)
check "closed off failed deletion retains row and protection in memory" (memoryOffRows.ContainsKey closedHandle && pendingOffRows.Contains closedHandle && offSaveAttempts = 1)
// A later hand-over can save again; only the actually closed handle is removed.
cleanupOffScenario (Set [reusedHandle]) true
check "closed off recovered save removes durable row" (not (durableOffRows.ContainsKey closedHandle) && offSaveAttempts = 2)
check "closed off recovered save releases row and protection" (not (memoryOffRows.ContainsKey closedHandle) && not (pendingOffRows.Contains closedHandle))
check "closed off live reused handle keeps original row and protection" (memoryOffRows = Map [reusedHandle, reusedRow] && durableOffRows = memoryOffRows && pendingOffRows = Set [reusedHandle])
cleanupOffScenario (Set [reusedHandle]) true
check "closed off completed deletion is not saved again" (offSaveAttempts = 2)
cleanupOffScenario Set.empty true
check "closed off final closure empties pending state" (memoryOffRows.IsEmpty && durableOffRows.IsEmpty && pendingOffRows.IsEmpty)

check "off matching current generation uses existing durable row" (offCanUseRecord Same originalRow originalRow.id (Some None))
check "off matching previous generation uses existing durable row" (offCanUseRecord Same originalRow originalRow.previous (Some None))
check "off cookie mismatch still needs preparation" (not (offCanUseRecord Undecided originalRow originalRow.id (Some None)))
check "off reused handle still needs preparation" (not (offCanUseRecord Gone originalRow originalRow.id (Some None)))
check "off changed generation still needs preparation" (not (offCanUseRecord Same originalRow (Some(prefix + "different")) (Some None)))
check "off empty ID cannot use old generation" (not (offCanUseRecord Same originalRow None (Some None)))

// A matching row with an unreadable original must prepare a verifiable empty return.
let unreadableOff = { originalRow with secret = Some "unreadable"; pin = false }
let openOffTest secret : string option option =
    match secret with None -> Some None | Some "unreadable" -> None | Some value -> Some(Some value)
let failedOriginal = openOffTest unreadableOff.secret
check "off unreadable matching original cannot reuse row" (not (offCanUseRecord Same unreadableOff unreadableOff.id failedOriginal))
check "off held original permits matching row" (offCanUseRecord Same unreadableOff unreadableOff.id (Some(Some "held-original")))
check "off held empty original permits matching row" (offCanUseRecord Same unreadableOff unreadableOff.id (Some None))
check "off decrypted original permits matching row" (offCanUseRecord Same originalRow originalRow.id (openOffTest originalRow.secret))
let preparedUnreadableOff =
    if offCanUseRecord Same unreadableOff unreadableOff.id failedOriginal then failwith "must prepare unreadable original"
    else
        let source = offSource true unreadableOff.pid (Some unreadableOff.started) (Some unreadableOff)
        let opened = source |> Option.bind (fun e -> openOffTest e.secret)
        { unreadableOff with phase = Applied
                             secret = if opened.IsSome then source |> Option.bind (fun e -> e.secret) else None
                             previous = None }, offOriginal opened
let preparedRow, returnedValue = preparedUnreadableOff
check "off unreadable preparation records empty original" (preparedRow.secret = None && returnedValue = None && preparedRow.phase = Applied)
// Persist and reload before the write, as returnOff does; verification reads this record.
let reloadedOff, damagedOff = parse (format [preparedRow])
let verificationOff = { List.exactlyOne reloadedOff with phase = Returned }
check "off unreadable preparation survives journal roundtrip" (not damagedOff && verificationOff.secret = None && verificationOff.id = unreadableOff.id)
let readBackOff = Some returnedValue
let completedOff =
    match openOffTest verificationOff.secret with
    | Some original when readBackOff = Some original && pinReturnVerified verificationOff (Some d1) ->
        givenVerdict false (Some Map.empty) verificationOff.id 0 = Refiled
    | _ -> false
check "off unreadable prepared return completes verification" completedOff
let remainingOff = if completedOff then [] else [verificationOff]
check "off unreadable verified return can leave journal and eligibility exclusion" (remainingOff.IsEmpty)


// Pin loss must survive a second observation without a failed read between them.
check "first normal reading waits" (lostPinObservation 1000 0 None (Some d1) = (Some d1, false))
check "matching reading before settle waits" (lostPinObservation 1499 1000 (Some d1) (Some d1) = (Some d1, false))
check "matching settled reading confirms" (lostPinObservation 1500 1000 (Some d1) (Some d1) = (Some d1, true))
check "different normal desktop starts over" (lostPinObservation 1500 1000 (Some d1) (Some d2) = (Some d2, false))
for desktop in [None; Some Guid.Empty; Some windowPinnedDesktop] do
    check "unknown or recovered pin resets observation" (lostPinObservation 1500 1000 (Some d1) desktop = (None, false))
check "app pin must also settle" (lostPinObservation 1500 1000 (Some appPinnedDesktop) (Some appPinnedDesktop) = (Some appPinnedDesktop, true))
let mutable candidate = None
let mutable candidateAt = 0
let mutable losses = 0
for now, desktop in [1000, Some d1; 1500, None; 2000, Some d1; 2500, Some windowPinnedDesktop; 3000, Some d1; 3500, Some d1] do
    let next, confirmed = lostPinObservation now candidateAt candidate desktop
    if next <> candidate then candidateAt <- now
    candidate <- next
    if confirmed then losses <- losses + 1
check "interrupted observations confirm only final pair" (losses = 1)
for desktop in [None; Some windowPinnedDesktop; Some appPinnedDesktop] do
    check "unsafe rebuild is skipped" (not (canRebuild false desktop))
for desktop in [None; Some Guid.Empty; Some d1; Some windowPinnedDesktop; Some appPinnedDesktop] do
    check "owned promotion is never rebuilt" (not (canRebuild true desktop))
check "ordinary desktop can rebuild" (canRebuild false (Some d1))
check "no virtual desktops can rebuild" (canRebuild false (Some Guid.Empty))

check "promotion waits before verification" (pinSettling true None)
check "unconfirmed loss waits before rewrite and verification" (pinSettling false (Some d1))
check "recovered observation resumes work" (not (pinSettling false None))

// Return verification uses only synthetic values and desktops.
let returnRow = { originalRow with pin = true; phase = Returned }
let observeReturn same current now previous desktop =
    returnedPinObservation returnRow same None current now previous desktop
let firstReturn, firstReady = observeReturn true (Some None) 1000 None (Some d1)
check "normal return first read waits" (firstReturn = Some(d1, 1000) && not firstReady)
check "normal return early read waits" (observeReturn true (Some None) 1499 firstReturn (Some d1) = (firstReturn, false))
check "normal return settled read completes" (observeReturn true (Some None) 1500 firstReturn (Some d1) = (firstReturn, true))
check "normal return moved desktop restarts" (observeReturn true (Some None) 1500 firstReturn (Some d2) = (Some(d2, 1500), false))
for desktop in [None; Some Guid.Empty; Some windowPinnedDesktop] do
    check "uncertain or window pin return resets" (observeReturn true (Some None) 1500 firstReturn desktop = (None, false))
check "app pin return remains immediate" (observeReturn true (Some None) 1500 firstReturn (Some appPinnedDesktop) = (None, true))
check "reused identity cannot complete" (observeReturn false (Some None) 1500 firstReturn (Some d1) = (None, false))
check "unreadable ID cannot complete" (observeReturn true None 1500 firstReturn (Some d1) = (None, false))
check "changed ID cannot complete" (observeReturn true (Some(Some "value-1")) 1500 firstReturn (Some d1) = (None, false))
check "ordinary return retains previous behavior" (returnedPinObservation { returnRow with pin = false } true None (Some None) 1000 None None = (None, true))

check "nonempty original completes only when matching" (returnedPinObservation returnRow true (Some "value-1") (Some(Some "value-1")) 1500 firstReturn (Some d1) = (firstReturn, true))
check "empty string differs from empty property" (returnedPinObservation returnRow true (Some "") (Some None) 1500 firstReturn (Some d1) = (None, false))
check "restart requires a fresh pair" (observeReturn true (Some None) 3000 None (Some d1) = (Some(d1, 3000), false))
let wrapAt = Int32.MaxValue - 100
check "return observation crosses tick sign boundary" (observeReturn true (Some None) (wrapAt + 500) (Some(d1, wrapAt)) (Some d1) = (Some(d1, wrapAt), true))

// Synthetic timing models a slow earlier member followed by a quick next pass.
let delayed, _ = observeReturn true (Some None) 1800 None (Some d1)
check "read completion anchors observation" (observeReturn true (Some None) 2000 delayed (Some d1) = (delayed, false))
check "read completion plus settle confirms" (observeReturn true (Some None) 2300 delayed (Some d1) = (delayed, true))
check "slow return waits" (not (returnedCheckDue false true 30999 1000))
check "slow return becomes due" (returnedCheckDue false true 31000 1000)
check "final return bypasses slow interval" (returnedCheckDue true true 1000 1000)
check "ordinary observation resumes fast interval" (returnedCheckDue false false 1500 1000)
check "slow interval crosses tick sign" (returnedCheckDue false true (wrapAt + 30000) wrapAt)
check "verified ordinary return rebuilds" (canRebuildReturned true true (Some d1) (Some d1))
check "unverified promotion cannot rebuild" (not (canRebuildReturned true false (Some d1) (Some d1)))
for live in [None; Some Guid.Empty; Some d2; Some appPinnedDesktop; Some windowPinnedDesktop] do
    check "changed return cannot rebuild" (not (canRebuildReturned true true (Some d1) live))
for view in [None; Some true] do
    check "view veto clears loss and settling" (corroboratePinLoss (Some d1) true view = (None, false))
check "view confirms loss" (corroboratePinLoss (Some d1) true (Some false) = (Some d1, true))
check "first observation needs no view read" (corroboratePinLoss (Some d1) false None = (Some d1, false))

// A rejected rebuild discards the old proof and can resume after fresh reads.
for live in [None; Some Guid.Empty; Some d2; Some appPinnedDesktop; Some windowPinnedDesktop] do
    let slow = returnedRebuildSlow live
    check "rejected rebuild uses ordinary or slow interval" (slow = (live <> Some d2))
    let interval = if slow then 30000 else 500
    check "rejected rebuild remains scheduled" (returnedCheckDue false slow (1000 + interval) 1000)
    let fresh, ready = observeReturn true (Some None) (1000 + interval) None (Some d2)
    check "rejected rebuild requires fresh first read" (fresh = Some(d2, 1000 + interval) && not ready)
    let _, ready = observeReturn true (Some None) (1500 + interval) fresh (Some d2)
    check "stable return can rebuild after rejection" (ready && canRebuildReturned true ready (Some d2) (Some d2))

check "initial pin loss has no extra delay" (pinLossCheckDue 1000 None)
check "first desktop observation does not start cooldown" (pinLossRejectedAt 1000 false None = None)
check "confirmed loss does not start cooldown" (pinLossRejectedAt 1500 true (Some false) = None)
for view in [None; Some true] do
    let rejected = pinLossRejectedAt 1500 true view
    check "view veto starts cooldown" (rejected = Some 1500)
    check "view veto waits until boundary" (not (pinLossCheckDue 31499 rejected))
    check "view veto resumes at boundary" (pinLossCheckDue 31500 rejected)
    check "view veto crosses tick sign" (pinLossCheckDue (wrapAt + 30000) (pinLossRejectedAt wrapAt true view))
    let mutable previous = None
    let mutable at = 0
    let mutable cooldown = None
    let mutable viewReads = 0
    let mutable confirmedLosses = 0
    let mutable settlingDuringCooldown = false
    for now in 1000 .. 500 .. 62500 do
        if pinLossCheckDue now cooldown then
            let candidate, confirmed = lostPinObservation now at previous (Some d1)
            let liveView = if now >= 62500 then Some false else view
            if confirmed then viewReads <- viewReads + 1
            let candidate, lost = corroboratePinLoss candidate confirmed liveView
            cooldown <- pinLossRejectedAt now confirmed liveView
            if candidate <> previous then at <- now
            previous <- candidate
            if lost then confirmedLosses <- confirmedLosses + 1
        else
            settlingDuringCooldown <- settlingDuringCooldown || pinSettling false previous
    check "persistent veto limits view calls" (viewReads = 3)
    check "cooldown does not defer group work" (not settlingDuringCooldown)
    check "real loss confirms after cooldown and fresh pair" (confirmedLosses = 1)

let repairLeader, repairMember = nativeint 901, nativeint 902
let repairKey = { group = nativeint 903; desktop = Guid.Empty }
let repairId = prefix + "repair"
let repairDesired = Map.ofList [repairKey, [repairLeader; repairMember]]
let repairNext = Map.ofList [repairKey, { id = repairId; leader = repairLeader }]
let repairSteps = [Apply(repairLeader, repairId); Apply(repairMember, repairId)]
let repairPending = Set.singleton repairLeader
check "waiting leader holds members" (orderIdRepairs repairDesired repairNext repairPending Set.empty repairSteps = [])
check "due leader precedes members" (orderIdRepairs repairDesired repairNext repairPending repairPending (List.rev repairSteps) = repairSteps)
check "leader repair inserted first" (orderIdRepairs repairDesired repairNext repairPending repairPending [Apply(repairMember, repairId)] = repairSteps)
check "pending member does not block leader" (orderIdRepairs repairDesired repairNext (Set.singleton repairMember) Set.empty repairSteps = [Apply(repairLeader, repairId)])
check "restore retained while leader waits" (orderIdRepairs repairDesired repairNext repairPending Set.empty (Restore(nativeint 904)::repairSteps) = [Restore(nativeint 904)])
for value in [Some None; Some(Some "app-id-1")] do
    check "undetected reset schedules repair" (idRewriteDecision false true repairId value = Some ApplicationChanged && externallyChangedId repairId value)
    check "due reset writes" (idRewriteDecision true true repairId value = Some Rewrite)
    check "desktop change defers repair" (idRewriteDecision true false repairId value = None)
check "other generation is not external" (not (externallyChangedId repairId (Some(Some(prefix + "other")))))
check "unreadable is not external" (not (externallyChangedId repairId None))

printfn "TaskbarAppIdPolicy: %d checks passed" (count)
