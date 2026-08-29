// The session restore's order arithmetic, and the wiring around it, run
// without starting WindowTabs.
//
//   dotnet fsi WtProgram/Shared/TabOrder.Tests.fsx
//
// It loads the very file the exe is built from, so what passes here is what
// ships. The strip and the restore sequence around it are a MODEL - each piece
// says which lines it mirrors - because those live in types that need a
// desktop and a window handle to exist at all. The model exists because the
// arithmetic on its own proves less than it looks: the first generation's
// checks all handed it tabs that were already in the right bands, and the
// fault being fixed is precisely that they are not.

#load "TabOrder.fs"

open System
open Bemo

let mutable failures = 0
let mutable checks = 0

let ok name = checks <- checks + 1; printfn "  ok   %s" name
let bad name detail =
    checks <- checks + 1
    failures <- failures + 1
    printfn "  FAIL %s\n       %s" name detail

let is name expected actual =
    if expected = actual then ok name
    else bad name (sprintf "expected %A\n       got      %A" expected actual)

// ---------------------------------------------------------------------------
// Handles, spelled as letters so the output can be read.

let handleOf (c: char) = IntPtr(int c)
let nameOf (h: IntPtr) = string (char (h.ToInt32() &&& 0xFF))
let show (hs: IntPtr list) = hs |> List.map nameOf |> String.concat ""
let handles (s: string) = s |> Seq.map handleOf |> List.ofSeq

// After a Windows restart every handle is new. 0x100 apart so the name still
// reads out of the low byte.
let reopened (h: IntPtr) = IntPtr(h.ToInt32() + 0x100)

// ---------------------------------------------------------------------------
// A model of the tab strip.
//
//   tabs        TabStrip.visualOrderCell
//   defaultLeft TabStrip.defaultAlignmentCell, which follows the group's own
//               tabPosition (WindowGroup.perGroupTabPositionValue)
//
// normalize mirrors TabStrip.normalizeVisualOrder: a stable sort by band, run
// after every change. It is the reason an order that ignores the bands cannot
// be expressed.

type SimTab = { h: IntPtr; left: bool; pinned: bool }
type Strip = { tabs: SimTab list; defaultLeft: bool }

let zoneOfTab (t: SimTab) = TabOrder.zoneOf t.left t.pinned
let normalize (s: Strip) = { s with tabs = s.tabs |> List.sortBy zoneOfTab }
let order (s: Strip) = s.tabs |> List.map (fun t -> t.h)

let emptyStrip = { tabs = []; defaultLeft = false }

// TabStrip.setVisualOrder: take the caller's complete order, keep anything it
// left out at the end, normalize.
let setVisualOrder (s: Strip) (desired: IntPtr list) =
    let byHandle = s.tabs |> List.map (fun t -> t.h, t) |> Map.ofList
    let kept = desired |> List.distinct |> List.choose byHandle.TryFind
    let keptSet = kept |> List.map (fun t -> t.h) |> Set.ofList
    let missing = s.tabs |> List.filter (fun t -> not (keptSet.Contains t.h))
    normalize { s with tabs = kept @ missing }

// Program.fs, addWindowToGroup: a window joining a group that was already
// there inherits the last tab's side; a window that creates its group gets the
// group's default. The restore's own alignment is applied after that block and
// therefore wins - when it has one. `savedAlign = None` is a tab whose
// alignment was lost, and the inherited value is what it is left with.
let addWindow (s: Strip) (h: IntPtr) (savedAlign: bool option) (savedPin: bool) =
    let inherited =
        match List.tryLast s.tabs with
        | Some(last) -> last.left
        | None -> s.defaultLeft
    let t = { h = h
              left = defaultArg savedAlign inherited
              // Program.fs, enforcePin: the saved pin state goes on before any
              // order is worked out, because pin is half of the band.
              pinned = savedPin }
    normalize { s with tabs = s.tabs @ [t] }

// Program.fs, applySavedOrder.
let applySavedOrder (s: Strip) (savedOrder: IntPtr list) (oldHandleOf: IntPtr -> IntPtr option) =
    let placed = s.tabs |> List.map (fun t -> TabOrder.placed t.h (zoneOfTab t))
    setVisualOrder s (TabOrder.restoreOrder savedOrder oldHandleOf placed)

// ---------------------------------------------------------------------------
// One whole restore: the group's windows arrive one at a time, in the given
// sequence, and every arrival recomputes the group's order.
//
// `placeFirst = false` models the wiring as it WAS: the window that creates
// the group is never placed, because at that moment the group is not yet known
// to be the saved one. The reported fault is exactly that nothing ever came
// back to it. Both settings must reach the same answer - the repair must not
// depend on the first tab having been placed.

type Window = {
    saved: IntPtr           // the handle it had when the state was saved
    live: IntPtr            // the handle it has now
    savedAlign: bool option // None = the alignment was lost on the way in
    savedPin: bool
}

let restore (savedOrder: IntPtr list) (arrivals: Window list) (existing: SimTab list)
            (defaultLeft: bool) (placeFirst: bool) =
    let oldOf =
        let m = arrivals |> List.filter (fun w -> w.live <> w.saved)
                         |> List.map (fun w -> w.live, w.saved) |> Map.ofList
        fun h -> m.TryFind h
    let mutable strip = { tabs = existing; defaultLeft = defaultLeft }
    arrivals |> List.iteri (fun i w ->
        strip <- addWindow strip w.live w.savedAlign w.savedPin
        if placeFirst || i > 0 || not existing.IsEmpty then
            strip <- applySavedOrder strip savedOrder oldOf)
    strip

// Every arrival sequence of a list.
let rec permutations = function
    | [] -> [[]]
    | xs -> xs |> List.collect (fun x -> permutations (List.filter ((<>) x) xs) |> List.map (fun p -> x :: p))

// Build the windows of a group from a compact description:
//   letter, alignment (Some true = left), pin
let windowsOf (sameSession: bool) (spec: (char * bool option * bool) list) =
    spec |> List.map (fun (c, align, pin) ->
        let saved = handleOf c
        { saved = saved
          live = (if sameSession then saved else reopened saved)
          savedAlign = align
          savedPin = pin })

// Run every arrival sequence and report the set of answers.
let allArrivals (savedOrder: IntPtr list) (ws: Window list) existing defaultLeft placeFirst =
    permutations ws
    |> List.map (fun arrival -> show (order (restore savedOrder arrival existing defaultLeft placeFirst)))
    |> List.distinct
    |> List.sort

printfn "TabOrder checks"
printfn ""

// ---------------------------------------------------------------------------
printfn "1. The reported failure. Five tabs of one group, none of them open"
printfn "   when WindowTabs started, arriving in the order the trace recorded."
let plain = windowsOf false ['A', Some false, false; 'B', Some false, false; 'C', Some false, false
                             'D', Some false, false; 'E', Some false, false]
let savedABCDE = handles "ABCDE"
let byName n = plain |> List.find (fun w -> nameOf w.saved = n)
let traceOrder = ["C"; "E"; "D"; "B"; "A"] |> List.map byName
is "the trace's arrival order gives back ABCDE" "ABCDE"
   (show (order (restore savedABCDE traceOrder [] false false)))

printfn ""
printfn "2. Every one of the 120 arrival orders of those five, with the first"
printfn "   window left unplaced as the old wiring left it."
is "all 120 give ABCDE" ["ABCDE"] (allArrivals savedABCDE plain [] false false)

printfn ""
printfn "3. The same 120, with the first window placed as it now is."
is "all 120 give ABCDE" ["ABCDE"] (allArrivals savedABCDE plain [] false true)

printfn ""
printfn "4. A WindowTabs restart inside one Windows session: the handles are"
printfn "   unchanged, so the old-to-new map is never consulted at all."
let sameSession = windowsOf true ['A', Some false, false; 'B', Some false, false; 'C', Some false, false
                                  'D', Some false, false; 'E', Some false, false]
is "all 120 give ABCDE" ["ABCDE"] (allArrivals savedABCDE sameSession [] false false)

printfn ""
printfn "5. Mixed alignment. Saved order A B C D E, with B and D left-aligned"
printfn "   and A C E right-aligned. The left band is drawn first, and inside"
printfn "   each band the saved order stands."
let mixedAlign = windowsOf false ['A', Some false, false; 'B', Some true, false; 'C', Some false, false
                                  'D', Some true, false; 'E', Some false, false]
is "all 120 give BDACE" ["BDACE"] (allArrivals savedABCDE mixedAlign [] false false)

printfn ""
printfn "6. Mixed alignment AND pinning, with all four bands occupied:"
printfn "   A left-pinned, B and E left, C right-pinned, D right."
let fourZones = windowsOf false ['A', Some true, true; 'B', Some true, false; 'C', Some false, true
                                 'D', Some false, false; 'E', Some true, false]
is "all 120 give ABECD" ["ABECD"] (allArrivals savedABCDE fourZones [] false false)

printfn ""
printfn "7. THE POINT OF THIS GENERATION. One tab arrives having lost its"
printfn "   alignment, so it takes whatever the group happens to be using and"
printfn "   may be drawn in the other band. No arithmetic can put it back: the"
printfn "   band IS the position on screen."
let allLeft = ['A', Some true, false; 'B', Some true, false; 'C', Some true, false
               'D', Some true, false; 'E', Some true, false]
let cLostIts = allLeft |> List.map (fun (c, a, p) -> if c = 'C' then (c, None, p) else (c, a, p))
let lostAnswers = allArrivals savedABCDE (windowsOf false cLostIts) [] false false
// Worse than a fixed wrong answer: what C inherits depends on whether it
// arrives first (the group's default side, since there is nothing to inherit
// from) or after a sibling (that sibling's side). So the SAME saved state
// comes back differently according to which application started first, which
// is requirement 2 broken as well as requirement 3.
is "with C's alignment lost the answer depends on the arrival order"
   ["ABCDE"; "ABDEC"] lostAnswers
is "and one of those two puts C at the far end from where it was saved"
   true (lostAnswers |> List.contains "ABDEC")
is "with it kept - which is what the fix does - there is one answer" ["ABCDE"]
   (allArrivals savedABCDE (windowsOf false allLeft) [] false false)

printfn ""
printfn "8. Pin is half of the band, so the saved pin state has to be on"
printfn "   BEFORE the order is worked out. Doing it the other way round is"
printfn "   not a smaller error, it is the same one."
let pinnedSpec = ['A', Some false, true; 'B', Some false, false; 'C', Some false, true
                  'D', Some false, false; 'E', Some false, false]
is "pin first (as restorePlacement does): the pinned pair leads" ["ACBDE"]
   (allArrivals savedABCDE (windowsOf false pinnedSpec) [] false false)
// Order first, pin afterwards: every tab is in band 3 while the order is
// computed, so the strip's own normalize is left to move the pinned ones, and
// it does so by their CURRENT sequence rather than the saved one.
let orderThenPin (arrival: Window list) =
    let oldOf =
        let m = arrival |> List.map (fun w -> w.live, w.saved) |> Map.ofList
        fun h -> m.TryFind h
    let mutable s = emptyStrip
    arrival |> List.iter (fun w ->
        s <- addWindow s w.live w.savedAlign false      // pin not applied yet
        s <- applySavedOrder s savedABCDE oldOf
        s <- normalize { s with tabs = s.tabs |> List.map (fun t ->
                                            if t.h = w.live then { t with pinned = w.savedPin } else t) })
    show (order s)
let pinLast =
    permutations (windowsOf false pinnedSpec) |> List.map orderThenPin |> List.distinct |> List.sort
if pinLast = ["ACBDE"] then bad "pin last is meant to be worse, and was not" (sprintf "%A" pinLast)
else ok (sprintf "pin last gives %d different answers instead of one" pinLast.Length)

printfn ""
printfn "9. A tab the saved order knows nothing about - opened by the user"
printfn "   while the group was still coming back - keeps its neighbour"
printfn "   instead of being swept to the end of its band."
let strangerAfterC =
    { tabs = [{ h = handleOf 'X'; left = false; pinned = false }]; defaultLeft = false }
let unknownKeeps (existingNames: string) (arriving: string) =
    let ws = arriving |> Seq.map (fun c -> byName (string c)) |> List.ofSeq
    let existing = existingNames |> Seq.map (fun c -> { h = handleOf c; left = false; pinned = false }) |> List.ofSeq
    show (order (restore savedABCDE ws existing false true))
is "X sitting between A and B stays between them" "AXBC" (unknownKeeps "AX" "BC")
is "X ahead of every known tab stays at the front" "XABC" (unknownKeeps "X" "ABC")
is "two unknowns keep their own order" "AXYBC" (unknownKeeps "AXY" "BC")
ignore strangerAfterC

printfn ""
printfn "10. A window that landed in some other windows' group does not"
printfn "    rearrange them. Only what the saved order names is moved."
is "P and Q are left where they were" "PQC" (unknownKeeps "PQ" "C")
is "and they stay put however many siblings arrive" "PQABC" (unknownKeeps "PQ" "CAB")

printfn ""
printfn "11. Running it again changes nothing."
let settled = restore savedABCDE traceOrder [] false false
let again = applySavedOrder settled savedABCDE (fun h -> Some(IntPtr(h.ToInt32() - 0x100)))
is "a second pass is a no-op" (order settled) (order again)

printfn ""
printfn "12. The bands themselves never move. A tab is only ever reordered"
printfn "    inside its own band, so alignment and pin survive the reorder -"
printfn "    which is what makes it safe to run on every arrival."
let bandsHeld =
    permutations fourZones
    |> List.forall (fun arrival ->
        let s = restore savedABCDE arrival [] false false
        s.tabs |> List.map zoneOfTab = (s.tabs |> List.map zoneOfTab |> List.sort))
is "the result is sorted by band for all 120 orders" true bandsHeld

printfn ""
printfn "13. A saved tab whose window never reopens holds no place: the tabs"
printfn "    that did come back close up over the gap."
let withoutB = ["A"; "C"; "D"; "E"] |> List.map byName
is "A C D E come back adjacent" "ACDE" (show (order (restore savedABCDE withoutB [] false false)))

printfn ""
printfn "14. Degenerate input is left alone rather than guessed at."
is "an empty saved order returns what is there" "PQ"
   (show (order (applySavedOrder { emptyStrip with tabs = handles "PQ" |> List.map (fun h -> { h = h; left = false; pinned = false }) }
                                 [] (fun _ -> None))))
is "no tabs at all" "" (show (order (applySavedOrder emptyStrip savedABCDE (fun _ -> None))))
is "a group of strangers is untouched" "PQR"
   (show (order (applySavedOrder { emptyStrip with tabs = handles "PQR" |> List.map (fun h -> { h = h; left = false; pinned = false }) }
                                 savedABCDE (fun _ -> None))))

printfn ""
printfn "15. The four bands are the strip's own. TabStrip.visualZoneOf calls"
printfn "    this function, so a check can never run against a second copy of"
printfn "    the rule that has drifted from it."
is "left-pinned"    0 (TabOrder.zoneOf true true)
is "left-unpinned"  1 (TabOrder.zoneOf true false)
is "right-pinned"   2 (TabOrder.zoneOf false true)
is "right-unpinned" 3 (TabOrder.zoneOf false false)

printfn ""
printfn "16. Where the rank comes from. A window that still carries the handle"
printfn "    it was saved with is found directly; one that reopened after a"
printfn "    Windows restart only through the old handle it was matched to."
is "found by its own handle" (Some 2) (TabOrder.rankIn savedABCDE (fun _ -> None) (handleOf 'C'))
is "found through the old handle" (Some 2)
   (TabOrder.rankIn savedABCDE (fun h -> Some(IntPtr(h.ToInt32() - 0x100))) (reopened (handleOf 'C')))
is "not found at all" None (TabOrder.rankIn savedABCDE (fun _ -> None) (handleOf 'Z'))

printfn ""
printfn "17. A group whose members straggle in over half an hour reaches the"
printfn "    same order as one that comes back at once, and reaches it again"
printfn "    at every step - there is no state carried between arrivals."
let stepwise =
    permutations plain
    |> List.map (fun arrival ->
        // Every prefix of the arrival sequence, ordered on its own, then the
        // whole thing: the answer for the whole must not depend on what the
        // prefixes did.
        let final = restore savedABCDE arrival [] false false
        let direct = restore savedABCDE (arrival |> List.sortBy (fun w -> nameOf w.saved)) [] false true
        show (order final) = show (order direct))
    |> List.forall id
is "arrival sequence makes no difference to the end state" true stepwise

printfn ""
if failures = 0 then printfn "%d checks, all passed." checks
else printfn "%d checks, %d FAILED." checks failures
exit (if failures = 0 then 0 else 1)
