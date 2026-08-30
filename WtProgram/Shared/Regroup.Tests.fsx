// Runs the regrouping decision without starting WindowTabs.
//
//   fsi --exec Regroup.Tests.fsx        (or: dotnet fsi Regroup.Tests.fsx)
//
// It loads Shared/Regroup.fs itself - the same file the program compiles - so
// what is printed here is what the program would decide. Nothing is opened,
// moved or created; the whole of it is arithmetic over made-up numbers.
//
// Three sections:
//   * named scenarios, printed in full so the answers can be read rather than
//     trusted;
//   * properties the applying code RELIES ON, checked over random pictures -
//     above all that no group this pass sends a window into is a group this
//     pass empties, because the window arrives at it a moment later and it has
//     to still be there;
//   * chooseGroup against a transcription of the code it replaced, so that the
//     rule which greets a NEWLY OPENED window is provably unchanged.

#load "Regroup.fs"

open System
open Bemo
open Bemo.Regroup

let mutable failures = 0
let mutable checks = 0

let check name condition =
    checks <- checks + 1
    if condition then printfn "  PASS  %s" name
    else
        failures <- failures + 1
        printfn "  FAIL  %s" name

// ---------------------------------------------------------------- scenarios

let mutable private handles = Map.empty<IntPtr, string>
let mutable private nextHandle = 0

/// name, application, category (0 = none), z-order (0 = frontmost),
/// the group it is in now, whether this pass may move it
let win name app category zorder group isTarget : WindowView =
    nextHandle <- nextHandle + 1
    let h = IntPtr(nextHandle)
    handles <- handles.Add(h, name)
    { hwnd = h; procPath = app; category = category
      zorder = zorder; group = group; isTarget = isTarget }

let nameOf h = handles.[h]
let groupName = function
    | Existing(g) -> sprintf "group %d" g
    | Fresh(n) -> sprintf "NEW group #%d" n

/// Where every window ends up once the moves are applied, as a printable map.
let layout (windows: WindowView list) (moves: Move list) =
    let destinationOf (w: WindowView) =
        match moves |> List.tryFind (fun m -> m.hwnd = w.hwnd) with
        | Some(m) -> groupName m.destination
        | None ->
            match w.group with
            | Some(g) -> sprintf "group %d" g
            | None -> "(no group)"
    windows
    |> List.map (fun w -> destinationOf w, w)
    |> List.groupBy fst
    |> List.map (fun (g, pairs) ->
        g, pairs |> List.map (fun (_, w) -> nameOf w.hwnd) |> List.sort)
    |> List.sortBy fst

let runWith tabLimit title (windows: WindowView list) =
    printfn ""
    printfn "=== %s" title
    printfn "  before:"
    for w in windows |> List.sortBy (fun w -> w.zorder) do
        printfn "    %-7s app=%-11s cat=%d z=%d %-10s%s"
            (nameOf w.hwnd) w.procPath w.category w.zorder
            (match w.group with Some(g) -> sprintf "group %d" g | None -> "(no group)")
            (if w.isTarget then "  <- target" else "")
    let moves = plan tabLimit windows
    printfn "  moves:"
    if moves.IsEmpty then printfn "    (none)"
    else
        for m in moves do
            printfn "    %-7s %s -> %s"
                (nameOf m.hwnd)
                (match m.source with Some(g) -> sprintf "group %d" g | None -> "(no group)")
                (groupName m.destination)
    printfn "  after:"
    for (g, members) in layout windows moves do
        printfn "    %-14s %s" g (String.Join(", ", members))
    moves

let run title windows = runWith None title windows

printfn "############ scenarios ############"

// --- 1. only the application whose setting changed is touched
let s1 =
    [ win "npad1" "notepad.exe" 0 0 (Some 1) true
      win "npad2" "notepad.exe" 0 2 (Some 2) true
      win "chrome" "chrome.exe" 0 1 (Some 3) false
      win "excel"  "excel.exe"  0 3 (Some 4) false ]
let m1 = run "1. auto-grouping on for notepad - other applications untouched" s1
check "1: only notepad windows move"
    (m1 |> List.forall (fun m -> ["npad1"; "npad2"] |> List.contains (nameOf m.hwnd)))
check "1: chrome and excel keep their own groups"
    (layout s1 m1 |> List.exists (fun (_, ms) -> ms = ["chrome"]) &&
     layout s1 m1 |> List.exists (fun (_, ms) -> ms = ["excel"]))

// --- 2. three windows pulled apart come back as ONE group
let s2 =
    [ win "a" "notepad.exe" 0 0 (Some 1) true
      win "b" "notepad.exe" 0 1 (Some 2) true
      win "c" "notepad.exe" 0 2 (Some 3) true ]
let m2 = run "2. three separated windows gather into one group" s2
check "2: exactly one group holds all three"
    (layout s2 m2 = [("group 1", ["a"; "b"; "c"])])
check "2: the frontmost window's group is the one kept"
    (m2 |> List.forall (fun m -> m.destination = Existing(1)))

// --- 3. no notion of monitor or virtual desktop anywhere in the decision.
// "left" and "right" are on different screens; nothing here can tell.
let s3 =
    [ win "left"  "notepad.exe" 0 0 (Some 1) true
      win "right" "notepad.exe" 0 1 (Some 2) true
      win "vlc"   "vlc.exe"     0 2 (Some 3) false ]
let m3 = run "3. windows on different monitors follow the ordinary rule" s3
check "3: the two windows merge, whatever screen they are on"
    (layout s3 m3 |> List.contains ("group 1", ["left"; "right"]))

// --- 3b. a same-application window the caller withheld (it is on another
// virtual desktop, so ensureWindowIsGrouped would not touch it either) is not
// moved - but its group is still a candidate for the ones that ARE moving,
// because that is precisely what a newly opened window would find. Treating it
// as anything else would be special-casing the very thing the requirement says
// not to special-case.
let s3b =
    [ win "here1"   "notepad.exe" 0 0 (Some 1) true
      win "here2"   "notepad.exe" 0 1 (Some 2) true
      win "otherVd" "notepad.exe" 0 5 (Some 3) false ]
let m3b = run "3b. a window on another virtual desktop is not moved, but its group counts" s3b
check "3b: the withheld window itself is never moved"
    (m3b |> List.forall (fun m -> nameOf m.hwnd <> "otherVd"))
check "3b: the eligible windows join it, exactly as a newly opened window would"
    (layout s3b m3b = [("group 3", ["here1"; "here2"; "otherVd"])])

// --- 4. category: a window already in ANOTHER group moves to the category group
let s4 =
    [ win "np1"  "notepad.exe" 3 2 (Some 1) true
      win "np2"  "notepad.exe" 3 3 (Some 1) true
      win "calc" "calc.exe"    3 0 (Some 2) false
      win "edge" "edge.exe"    0 1 (Some 2) false ]
let m4 = run "4. category 3 ticked for notepad - it joins the group already using it" s4
check "4: both notepad windows move to the group holding category 3"
    (m4 |> List.forall (fun m -> m.destination = Existing(2)) && m4.Length = 2)
check "4: the category group's own windows are not disturbed"
    (layout s4 m4 = [("group 2", ["calc"; "edge"; "np1"; "np2"])])

// --- 4b. category with no other application using it yet
let s4b =
    [ win "np1" "notepad.exe" 7 1 (Some 1) true
      win "np2" "notepad.exe" 7 0 (Some 2) true
      win "vlc" "vlc.exe"     0 2 (Some 3) false ]
let m4b = run "4b. category 7 ticked, nobody else uses it - the windows gather alone" s4b
check "4b: they gather into one group and do not join the unrelated one"
    (layout s4b m4b |> List.contains ("group 2", ["np1"; "np2"]))

// --- 5. a second tick, straight after the first, decides on the new picture
let s5after =
    [ win "np1"  "notepad.exe" 5 1 (Some 1) true
      win "np2"  "notepad.exe" 5 2 (Some 1) true
      win "term" "wt.exe"      5 0 (Some 2) false ]
let m5 = run "5. category ticked after auto-grouping - each tick decides afresh" s5after
check "5: the category tick moves the pair on to the category group"
    (layout s5after m5 = [("group 2", ["np1"; "np2"; "term"])])

// --- 6. nothing to do
let s6 =
    [ win "a" "notepad.exe" 0 0 (Some 1) true
      win "b" "notepad.exe" 0 1 (Some 1) true
      win "c" "notepad.exe" 0 2 (Some 1) true ]
let m6 = run "6. windows already together - the pass is silent" s6
check "6: no moves at all, so no group is destroyed and rebuilt" m6.IsEmpty

let s6b = s6 |> List.map (fun w -> { w with isTarget = false })
let m6b = run "6b. no target windows (a setting going ON to OFF never calls the pass)" s6b
check "6b: no moves" m6b.IsEmpty

// --- 7. a window in a mixed group is pulled out, and takes nobody with it
let s7 =
    [ win "np1"  "notepad.exe" 0 1 (Some 1) true
      win "np2"  "notepad.exe" 0 2 (Some 2) true
      win "edge" "edge.exe"    0 0 (Some 2) false ]
let m7 = run "7. a target sharing a group with another application" s7
check "7: edge stays in its own group"
    (layout s7 m7 |> List.exists (fun (g, ms) -> g = "group 2" && ms = ["edge"]))
check "7: the two notepad windows end up together and not in edge's group"
    (layout s7 m7 |> List.exists (fun (g, ms) -> g = "group 1" && ms = ["np1"; "np2"]))

// --- 8. running the pass twice changes nothing the second time
let applyTo (windows: WindowView list) (moves: Move list) =
    let freshBase = 1000
    windows |> List.map (fun w ->
        match moves |> List.tryFind (fun m -> m.hwnd = w.hwnd) with
        | Some(m) ->
            match m.destination with
            | Existing(g) -> { w with group = Some(g) }
            | Fresh(n) -> { w with group = Some(freshBase + n) }
        | None -> w)
let s8 = applyTo s2 m2
let m8 = run "8. the same pass run again on its own result" s8
check "8: idempotent - a second run has nothing to do" m8.IsEmpty

// --- 9. a window that is in no group yet joins the gathering
let s9 =
    [ win "a" "notepad.exe" 0 1 (Some 1) true
      win "b" "notepad.exe" 0 2 (Some 1) true
      win "c" "notepad.exe" 0 0 None      true ]
let m9 = run "9. an ungrouped window of the same application" s9
check "9: it joins the others rather than starting a group of its own"
    (layout s9 m9 = [("group 1", ["a"; "b"; "c"])])

// --- 10. every target ungrouped: a group really does have to be created
let s10 =
    [ win "a" "notepad.exe" 0 0 None true
      win "b" "notepad.exe" 0 1 None true ]
let m10 = run "10. nothing to move into - one new group, shared" s10
check "10: both go to the same new group"
    (m10 |> List.forall (fun m -> m.destination = Fresh(0)) && m10.Length = 2)

// --- 11. THE LONE WINDOW. The user has dragged their one notepad window into
// edge's group by hand. Ticking auto-grouping for notepad has nothing to
// gather it with, so tearing it out of the group the user built - into a group
// of one - would be this feature running backwards.
let s11 =
    [ win "edge" "edge.exe"    0 0 (Some 2) false
      win "np"   "notepad.exe" 0 1 (Some 2) true ]
let m11 = run "11. a lone target inside a hand-made mixed group" s11
check "11: it is left where the user put it"
    m11.IsEmpty

// --- 11b. the same window, but with nowhere to be: a lone target in no group
// does get one, because that is what a newly opened window gets.
let s11b = [ win "np'" "notepad.exe" 0 0 None true ]
let m11b = run "11b. a lone target in no group at all" s11b
check "11b: it is given a group, as a newly opened window would be"
    (m11b |> List.map (fun m -> m.destination) = [Fresh(0)])

// --- 12. two lone targets, each in a mixed group of its own. Now they have
// each other, so they do gather - the rule above is about having nothing to
// gather with, not about mixed groups being sacred.
let s12 =
    [ win "edge2"  "edge.exe"    0 0 (Some 1) false
      win "npA"    "notepad.exe" 0 1 (Some 1) true
      win "chrome" "chrome.exe"  0 2 (Some 2) false
      win "npB"    "notepad.exe" 0 3 (Some 2) true ]
let m12 = run "12. two targets, each alone in a mixed group" s12
check "12: they gather into one new group"
    (m12 |> List.forall (fun m -> m.destination = Fresh(0)) && m12.Length = 2)
check "12: neither mixed group loses its own application"
    (layout s12 m12 |> List.exists (fun (_, ms) -> ms = ["edge2"]) &&
     layout s12 m12 |> List.exists (fun (_, ms) -> ms = ["chrome"]))

// --- 13. a mixed group that already holds a window of the target application
// IS a home the ordinary rule would find, so the others join it there rather
// than starting something new. Nothing is pulled out of it.
let s13 =
    [ win "edge3" "edge.exe"    0 0 (Some 2) false
      win "npX"   "notepad.exe" 0 1 (Some 1) true
      win "npY"   "notepad.exe" 0 2 (Some 2) true ]
let m13 = run "13. a mixed group holding one of the targets" s13
check "13: the two notepad windows end up in one group"
    (let after = layout s13 m13
     after |> List.exists (fun (_, ms) -> ms = ["npX"; "npY"]))

// --- 14. the tab limit is respected as windows accumulate, because the
// picture is updated after each one is placed.
let s14 =
    [ win "t1" "notepad.exe" 0 0 (Some 1) true
      win "t2" "notepad.exe" 0 1 (Some 2) true
      win "t3" "notepad.exe" 0 2 (Some 3) true ]
let m14 = runWith (Some 2) "14. tabLimit = 2, three separated windows" s14
check "14: the third is left where it is rather than overfilling the group"
    (m14 |> List.forall (fun m -> nameOf m.hwnd <> "t3"))

// ------------------------------------------------------------- properties
//
// Over random pictures. The first of these is the precondition the applying
// code depends on and cannot check for itself.

printfn ""
printfn "############ properties over random pictures ############"

let rngP = Random(20260901)
let appsP = [| "notepad.exe"; "chrome.exe"; "excel.exe"; "vlc.exe" |]

/// A random desktop: some groups, some loose windows, one application's
/// settings just switched on.
let randomPicture (rng: Random) =
    let subject = appsP.[rng.Next(appsP.Length)]
    let useCategory = rng.Next(3) = 0
    let categories =
        appsP |> Array.map (fun a -> a, (if useCategory && rng.Next(2) = 0 then rng.Next(1, 4) else 0))
              |> Map.ofArray
    let categoryOf p = categories.TryFind(p) |> Option.defaultValue 0
    let groupCount = rng.Next(0, 5)
    let mutable z = 0
    let mutable ws = []
    let mutable h = 0
    let addWindow group =
        h <- h + 1
        z <- z + 1
        let app = appsP.[rng.Next(appsP.Length)]
        ws <- ws @ [ { hwnd = IntPtr(h); procPath = app; category = categoryOf app
                       zorder = z; group = group
                       isTarget = (app = subject) } ]
    for g in 1 .. groupCount do
        for _ in 1 .. rng.Next(1, 4) do addWindow (Some g)
    for _ in 1 .. rng.Next(0, 3) do addWindow None
    ws

/// Every group this pass sends a window INTO still has a window of its own
/// that is not going anywhere. Without this the group could be emptied,
/// swept away by destroyEmptyGroups, and the window arriving a moment later
/// would find nothing there.
let destinationsSurvive (windows: WindowView list) (moves: Move list) =
    let leaving = moves |> List.map (fun m -> m.hwnd) |> Set.ofList
    moves
    |> List.choose (fun m -> match m.destination with Existing(g) -> Some(g) | Fresh(_) -> None)
    |> List.distinct
    |> List.forall (fun g ->
        windows |> List.exists (fun w -> w.group = Some(g) && not (leaving.Contains w.hwnd)))

/// No window is given two destinations, and no window that may not move moves.
let movesAreWellFormed (windows: WindowView list) (moves: Move list) =
    let byHwnd = windows |> List.map (fun w -> w.hwnd, w) |> Map.ofList
    (moves |> List.map (fun m -> m.hwnd) |> List.distinct |> List.length) = List.length moves &&
    moves |> List.forall (fun m ->
        let w = byHwnd.[m.hwnd]
        w.isTarget && m.source = w.group && m.destination <> Existing(-1))

let mutable pictures = 0
let mutable survivesFailed = 0
let mutable wellFormedFailed = 0
let mutable settlesFailed = 0
for _ in 1 .. 20000 do
    let ws = randomPicture rngP
    let moves = plan None ws
    pictures <- pictures + 1
    if not (destinationsSurvive ws moves) then survivesFailed <- survivesFailed + 1
    if not (movesAreWellFormed ws moves) then wellFormedFailed <- wellFormedFailed + 1
    // Applying the answer and asking again must produce nothing: a pass that
    // still had work to do would mean the moves it just made were not the
    // answer to the question it was asked.
    if not ((plan None (applyTo ws moves)).IsEmpty) then settlesFailed <- settlesFailed + 1

printfn "  %d random pictures" pictures
check "every existing destination keeps a window that is not moving" (survivesFailed = 0)
check "moves are well formed - one per window, targets only, source is the window's group"
    (wellFormedFailed = 0)
check "the pass settles - running it on its own result has nothing left to do"
    (settlesFailed = 0)

// ------------------------------------------------------------- the handover
//
// The plan says where each window goes; Program.fs hands it over by taking the
// window out of its group and letting the ORDINARY pass put it back, so that
// everything addWindowToGroup does for a newly opened window is done for this
// one too. The order the windows come back in is not ours to choose - each
// removal completes on its own group's thread - so what follows plays every
// order out.
//
// pendingRegroups, tryRegroup, completeRegroup and the applying loop of
// regroupNow are transcribed here over plain values. destroyEmptyGroups is run
// after every single arrival, which is the harshest interleaving the real pass
// can produce.

printfn ""
printfn "############ the handover, over every arrival order ############"

type Pending = { pgroup: int option; setId: int }

/// Returns: where every window ended up, and what was left in pendingRegroups.
let handover (windows: WindowView list) (moves: Move list) (arrivalOrder: IntPtr list) =
    let mutable groups =
        windows
        |> List.choose (fun w -> w.group |> Option.map (fun g -> g, w.hwnd))
        |> List.groupBy fst
        |> List.map (fun (g, ps) -> g, ps |> List.map snd |> Set.ofList)
        |> Map.ofList
    let mutable pending = Map.empty<IntPtr, Pending>
    let mutable nextNewGroup = -1
    let destroyEmptyGroups () =
        groups <- groups |> Map.filter (fun _ ws -> not (Set.isEmpty ws))
    // regroupNow: record the destination, then leave the old group. Nothing else.
    for m in moves do
        pending <-
            pending.Add(m.hwnd,
                match m.destination with
                | Existing(g) -> { pgroup = Some(g); setId = -1 }
                | Fresh(n) -> { pgroup = None; setId = n })
        match m.source with
        | Some(g) -> groups <- groups.Add(g, groups.[g].Remove m.hwnd)
        | None -> ()
    destroyEmptyGroups ()
    // tryRegroup, as one of findGroupForWindow's handlers.
    let tryRegroup hwnd =
        match pending.TryFind hwnd with
        | Some(p) ->
            match p.pgroup with
            | Some(g) when groups.ContainsKey g -> Some(Some(g))
            | Some(_) ->
                pending <- pending.Remove hwnd
                None
            | None -> Some(None)
        | None -> None
    // completeRegroup, from inside addWindowToGroup.
    let completeRegroup hwnd group isNewGroup =
        match pending.TryFind hwnd with
        | Some(p) ->
            pending <- pending.Remove hwnd
            if p.pgroup.IsNone && isNewGroup then
                pending <-
                    pending |> Map.map (fun _ q ->
                        if q.pgroup.IsNone && q.setId = p.setId then { q with pgroup = Some(group) } else q)
        | None -> ()
    // ensureWindowIsGrouped -> addWindowToGroup, one window at a time.
    for hwnd in arrivalOrder do
        if not (groups |> Map.exists (fun _ ws -> ws.Contains hwnd)) then
            let group, isNewGroup =
                match tryRegroup hwnd with
                | Some(Some(g)) -> g, false
                | _ ->
                    let g = nextNewGroup
                    nextNewGroup <- nextNewGroup - 1
                    groups <- groups.Add(g, Set.empty)
                    g, true
            completeRegroup hwnd group isNewGroup
            groups <- groups.Add(group, groups.[group].Add hwnd)
            destroyEmptyGroups ()
    let placeOf =
        groups |> Map.toList |> List.collect (fun (g, ws) -> ws |> Set.toList |> List.map (fun w -> w, g)) |> Map.ofList
    placeOf, pending

/// Every ordering of a list.
let rec permutations = function
    | [] -> [[]]
    | xs -> xs |> List.collect (fun x -> permutations (xs |> List.filter ((<>) x)) |> List.map (fun p -> x :: p))

let mutable orders = 0
let mutable strandedFailed = 0
let mutable existingFailed = 0
let mutable setSplitFailed = 0
let mutable leftoverFailed = 0
let mutable untouchedFailed = 0

let checkOneOrder (windows: WindowView list) (moves: Move list) (order: IntPtr list) =
        orders <- orders + 1
        let placeOf, leftover = handover windows moves order
        if not leftover.IsEmpty then leftoverFailed <- leftoverFailed + 1
        // Nobody is left without a group.
        if order |> List.exists (fun h -> not (placeOf.ContainsKey h)) then
            strandedFailed <- strandedFailed + 1
        // A destination that was already on screen is the group they land in.
        for m in moves do
            match m.destination with
            | Existing(g) -> if placeOf.TryFind m.hwnd <> Some(g) then existingFailed <- existingFailed + 1
            | Fresh(_) -> ()
        // A set that had to make a group makes exactly one, whoever gets there first.
        let sets =
            moves |> List.choose (fun m -> match m.destination with Fresh(n) -> Some(n, m.hwnd) | _ -> None)
                  |> List.groupBy fst
        for (_, members) in sets do
            let landed = members |> List.map (fun (_, h) -> placeOf.TryFind h) |> List.distinct
            if List.length landed <> 1 then setSplitFailed <- setSplitFailed + 1
        // A window this pass was not moving is exactly where it was.
        let moving = moves |> List.map (fun m -> m.hwnd) |> Set.ofList
        for w in windows do
            if not (moving.Contains w.hwnd) && w.group.IsSome then
                if placeOf.TryFind w.hwnd <> w.group then untouchedFailed <- untouchedFailed + 1

/// Every order the windows could come back in.
let checkHandover (windows: WindowView list) (moves: Move list) =
    // Only the windows the pass let go of come back; the rest never leave.
    for order in permutations (moves |> List.map (fun m -> m.hwnd)) do
        checkOneOrder windows moves order

// Every scenario above that actually moves something, plus a set of four with
// nowhere to go - 24 orders on its own - and the mixed-group pair.
for (ws, ms) in [ (s1, m1); (s2, m2); (s3, m3); (s3b, m3b); (s4, m4); (s4b, m4b)
                  (s5after, m5); (s7, m7); (s9, m9); (s10, m10); (s12, m12); (s13, m13); (s14, m14) ] do
    checkHandover ws ms

let s15 =
    [ win "q1" "notepad.exe" 0 0 (Some 1) true
      win "q2" "notepad.exe" 0 1 (Some 2) true
      win "q3" "notepad.exe" 0 2 (Some 3) true
      win "q4" "notepad.exe" 0 3 (Some 4) true
      win "e1" "edge.exe"    0 4 (Some 1) false
      win "e2" "edge.exe"    0 5 (Some 2) false
      win "e3" "edge.exe"    0 6 (Some 3) false
      win "e4" "edge.exe"    0 7 (Some 4) false ]
let m15 = run "15. four targets, each alone in a mixed group - a set of four" s15
check "15: all four are given the one new group"
    (m15 |> List.forall (fun m -> m.destination = Fresh(0)) && m15.Length = 4)
checkHandover s15 m15

// And the same over random pictures, so the shapes are not only the ones
// thought of here. Every order when there are few enough to enumerate,
// shuffles otherwise.
let rngH = Random(20260902)
for _ in 1 .. 3000 do
    let ws = randomPicture rngH
    let moves = plan None ws
    if not moves.IsEmpty then
        let arrivals = moves |> List.map (fun m -> m.hwnd)
        if List.length arrivals <= 4 then
            for order in permutations arrivals do checkOneOrder ws moves order
        else
            for _ in 1 .. 12 do
                checkOneOrder ws moves (arrivals |> List.sortBy (fun _ -> rngH.Next()))

printfn "  %d arrival orders played out" orders
check "no window is left without a group, whatever order they come back in" (strandedFailed = 0)
check "a destination already on screen is still there when the window arrives" (existingFailed = 0)
check "a set that has to make a group makes exactly one" (setSplitFailed = 0)
check "every entry is retired - nothing is left pending" (leftoverFailed = 0)
check "a window the pass was not moving does not move" (untouchedFailed = 0)

// ------------------------------------------- chooseGroup vs. the code it replaced

printfn ""
printfn "############ chooseGroup against the previous implementation ############"

// Program.tryAutoGroup as it stood before this change, transcribed line for
// line over plain values. A group is (procPaths, zorders).
let previousImplementation (tabLimit: int option) (procPath: string) (categoryOf: string -> int)
                           (groups: (int * (string list * int list)) list) =
    let groups =
        match tabLimit with
        | Some(limit) -> groups |> List.filter (fun (_, (ps, _)) -> List.length ps < limit)
        | None -> groups
    let groups = groups |> List.filter (fun (_, (ps, _)) -> List.length ps > 0)
    let groups = groups |> List.sortBy (fun (_, (_, zs)) -> List.min zs)
    let windowCategory = categoryOf procPath
    if windowCategory > 0 then
        groups |> List.tryFind (fun (_, (ps, _)) ->
            (ps |> List.tryFind (fun p -> categoryOf p = windowCategory)).IsSome)
    else
        groups |> List.tryFind (fun (_, (ps, _)) -> ps |> List.exists ((=) procPath))
    |> Option.map fst

let rng = Random(20260831)
let apps = [| "notepad.exe"; "chrome.exe"; "excel.exe"; "vlc.exe" |]
// A category belongs to the application, so the table is per application.
let mutable disagreements = 0
let mutable cases = 0
for _ in 1 .. 20000 do
    let categories =
        apps |> Array.map (fun a -> a, (if rng.Next(3) = 0 then rng.Next(1, 4) else 0)) |> Map.ofArray
    let categoryOf p = categories.TryFind(p) |> Option.defaultValue 0
    let groupCount = rng.Next(0, 5)
    let mutable z = 0
    let groups =
        [ for gi in 0 .. groupCount - 1 ->
            let n = rng.Next(0, 4)   // a group with no windows is possible and must be skipped
            let ps = [ for _ in 1 .. n -> apps.[rng.Next(apps.Length)] ]
            let zs = [ for _ in 1 .. n -> (z <- z + 1; rng.Next(0, 40)) ]
            gi, (ps, zs) ]
    let subject = apps.[rng.Next(apps.Length)]
    let tabLimit = if rng.Next(4) = 0 then Some(rng.Next(1, 4)) else None
    let views =
        groups |> List.map (fun (gi, (ps, zs)) ->
            { id = gi
              procPaths = ps
              categories = ps |> List.map categoryOf
              count = List.length ps
              rank = zs |> List.fold min Int32.MaxValue })
    let now = chooseGroup tabLimit subject (categoryOf subject) views
    let before = previousImplementation tabLimit subject categoryOf groups
    cases <- cases + 1
    if now <> before then
        disagreements <- disagreements + 1
        if disagreements <= 5 then
            printfn "  DIFF subject=%s tabLimit=%A groups=%A now=%A before=%A"
                subject tabLimit groups now before

printfn "  %d random cases compared" cases
check "chooseGroup agrees with the implementation it replaced in every case"
    (disagreements = 0)

printfn ""
printfn "############ %d checks, %d failed ############" checks failures
exit (if failures = 0 then 0 else 1)
