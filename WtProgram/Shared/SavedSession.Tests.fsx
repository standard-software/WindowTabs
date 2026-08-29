// The whole journey - a screen full of groups, the program's own save, real
// settings JSON, the program's own load, and back onto the screen - run
// without starting WindowTabs.
//
//   dotnet fsi WtProgram/Shared/SavedSession.Tests.fsx
//
// This is the check the two earlier generations were asked for and neither
// managed. One of them checked the codec - that a record survives being
// written and read - and the other wrote JSON by hand inside the test and
// read it back in the next line. Neither could tell you whether the program
// writes the tabs in the order they are on screen, whether a window that
// reopens where it was gets its own state back rather than its twin's, or
// whether loading a file and saving it again leaves it alone. Those are
// decisions, and they now live in SavedSession, which this file LOADS - the
// same source the exe is built from, not a copy of it.
//
// What is still not exercised here: the handful of lines in Program.fs that
// read a live window's rectangle and the global per-window maps, and that turn
// the record below into the closed-tab cache's own type. Everything that
// decides anything is on this side of that line.

#r "../../Newtonsoft.Json.dll"
#load "TabOrder.fs"
#load "SavedTabState.fs"
#load "SavedSession.fs"

open System
open Newtonsoft.Json.Linq
open Bemo
open Bemo.SavedTabState
open Bemo.SavedSession

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

let isTrue name actual = is name true actual

// ---------------------------------------------------------------- fixtures --

let hwnd (n: int) = IntPtr(n)

let hidemaru = @"C:\Program Files\Hidemaru\Hidemaru.exe"
let terminal = @"C:\Program Files\WindowsTerminal\wt.exe"
let browser = @"C:\Program Files\Firefox\firefox.exe"

// A rectangle 100 wide and 100 high whose top left corner is where it is put,
// so its centre is 50 in from each corner.
let at (x: int) (y: int) = Some(x, y, 100, 100)

let tab (h: int) (exe: string) (title: string) =
    { ofHwnd (hwnd h) with exePath = Some(exe); windowTitle = Some(title) }

let liveWindow (h: int) (exe: string) (title: string) (rect: (int * int * int * int) option) =
    let w : LiveWindow = { handle = hwnd h; exePath = exe; title = title; center = centerOfRect rect }
    w

// The program's own save, driven from a description of a screen rather than
// from a desktop. `write` is the function saveTabGroupsToSettings calls, and
// the function passed to it is the only thing the desktop is needed for: what
// the program knows about one live window.
let saveScreen (groups: (SavedTab list * string option * bool option * PendingTab list) list) =
    let byHandle =
        groups
        |> List.collect (fun (ts, _, _, _) -> ts)
        |> List.map (fun t -> t.hwnd, t)
        |> Map.ofList
    write
        (fun h -> byHandle.TryFind(h))
        (groups |> List.map (fun (ts, pos, margin, seeds) ->
            let g : GroupToSave =
                { stripOrder = ts |> List.map (fun t -> t.hwnd)
                  mirrorOrder = ts |> List.map (fun t -> t.hwnd)
                  seeds = seeds
                  tabPosition = pos
                  snapMargin = margin }
            g))

let group ts pos margin = (ts, pos, margin, [])
let asText (a: JArray) = a.ToString()
// The journey the settings file makes across a restart: written, serialized,
// parsed again by whatever reads settings.json, and taken apart by the load.
let loadText (s: string) = read (JArray.Parse(s))

let seedMaxAgeDays = 30.0
let closedTabMaxAgeDays = 8.0
let closedTabSaveLimit = 50

let now = DateTime(2026, 8, 29, 3, 0, 0)
let daysAgo (d: float) = now.AddDays(-d)

let planOf (groups: SavedGroup list) (live: LiveWindow list) =
    plan now seedMaxAgeDays closedTabMaxAgeDays groups live

let titlesOf (tabs: SavedTab list) = tabs |> List.map (fun t -> t.windowTitle |> Option.defaultValue "?")

printfn "SavedSession checks"
printfn ""

// ---------------------------------------------------------------------------

printfn "1. A screen of two groups goes into the file and comes back out of it."
printfn "   Not a record and its JSON - the groups, their order, their settings"
printfn "   and every tab's state, through the program's own save and load."

let editorTabs = [
    { tab 0x101 hidemaru "notes" with
        align = Some(AlignLeft); isPinned = true
        renamedTabName = Some("Notes"); rect = at 0 0 }
    { tab 0x102 hidemaru "todo" with align = Some(AlignLeft); rect = at 200 0 }
    // No alignment of its own: it follows whichever side the group is set to.
    { tab 0x104 terminal "Claude2" with rect = at 600 0 }
    { tab 0x105 browser "Docs" with align = Some(AlignRight); isPinned = true; rect = at 800 0 }
    { tab 0x103 terminal "Claude1" with
        align = Some(AlignRight); rect = at 400 0
        fillColor = Some("112233FF"); underlineColor = Some("445566CC")
        borderColor = Some("778899AA") } ]

let mailTabs = [
    { tab 0x201 browser "Mail" with align = Some(AlignLeft) }
    { tab 0x202 browser "Calendar" with align = Some(AlignLeft); renamedTabName = Some("Cal") } ]

let screenText =
    asText (saveScreen [ group editorTabs (Some("TopLeft")) (Some(true))
                         group mailTabs (Some("TopRight")) (Some(false)) ])
let loaded = loadText screenText

is "both groups come back" 2 (List.length loaded)
is "the first group's tabs, in the order they are drawn in" editorTabs loaded.[0].windows
is "the second group's" mailTabs loaded.[1].windows
is "the first group's tab position" (Some("TopLeft")) loaded.[0].tabPosition
is "and its snap margin" (Some(true)) loaded.[0].snapMargin
is "the second group's tab position" (Some("TopRight")) loaded.[1].tabPosition
is "and its snap margin" (Some(false)) loaded.[1].snapMargin

printfn ""
printfn "2. Nothing is invented. A tab that had no alignment of its own has"
printfn "   none afterwards either: an absent alignment means \"whichever side"
printfn "   this group is set to\", and writing the side it happens to be drawn"
printfn "   on would nail it there and stop the group's tabPosition moving it."
is "the tab with no alignment still has none" None loaded.[0].windows.[2].align
isTrue "and the file holds no alignment key for it"
    (isNull ((JArray.Parse(screenText).[0].["windows"].[2] :?> JObject).Property("tabAlignment")))
is "nor is a pin invented" false loaded.[0].windows.[1].isPinned
is "nor a name" None loaded.[0].windows.[1].renamedTabName

printfn ""
printfn "3. The order the tabs are written in is the strip's, and the strip's"
printfn "   snapshot and the group's mirror of it are BOTH consulted: the"
printfn "   snapshot does not hold a window added moments ago, and the mirror"
printfn "   goes stale after a pin/unpin normalization."
is "the mirror's extra window is kept, at the end"
    [hwnd 1; hwnd 2; hwnd 3] (mergedOrder [hwnd 1; hwnd 2] [hwnd 2; hwnd 3])
is "and the snapshot decides the order of the ones both know"
    [hwnd 2; hwnd 1] (mergedOrder [hwnd 2; hwnd 1] [hwnd 1; hwnd 2])
is "no window is written twice" [hwnd 1; hwnd 2] (mergedOrder [hwnd 1; hwnd 2] [hwnd 1; hwnd 2])
let mixedOrder =
    let a, b, c = tab 1 hidemaru "a", tab 2 hidemaru "b", tab 3 hidemaru "c"
    let byHandle = [a; b; c] |> List.map (fun t -> t.hwnd, t) |> Map.ofList
    let g : GroupToSave =
        { stripOrder = [hwnd 3; hwnd 1]; mirrorOrder = [hwnd 1; hwnd 2]
          seeds = []; tabPosition = None; snapMargin = None }
    (read (write (fun h -> byHandle.TryFind(h)) [g])).[0].windows
is "which is what the save writes" ["c"; "a"; "b"] (titlesOf mixedOrder)
let deadHandle =
    let g : GroupToSave =
        { stripOrder = [hwnd 1; hwnd 2]; mirrorOrder = []
          seeds = []; tabPosition = None; snapMargin = None }
    read (write (fun h -> if h = hwnd 1 then Some(tab 1 hidemaru "a") else None) [g])
is "a handle that is no longer a window is left out" ["a"] (titlesOf deadHandle.[0].windows)

printfn ""
printfn "4. Windows that have not started yet. Their entries are held in"
printfn "   memory and written back at every save, at the index they held -"
printfn "   without that, the save that fires ten seconds after a reboot would"
printfn "   replace a group's record with whatever is running by then, which"
printfn "   is nothing."
let seedOf (rank: int) (t: SavedTab) =
    let p : PendingTab = { tab = { t with seedSince = Some(daysAgo 1.0) }; rank = rank; isRestoreSeed = true }
    p
let withSeeds =
    saveScreen [ (editorTabs |> List.filter (fun t -> t.hwnd = hwnd 0x101),
                  Some("TopLeft"), Some(true),
                  [ seedOf 1 editorTabs.[1]; seedOf 3 editorTabs.[3] ]) ]
let seededBack = (loadText (asText withSeeds)).[0].windows
is "the group comes back with its waiting entries in place"
    ["notes"; "todo"; "Docs"] (titlesOf seededBack)
is "and they keep the state they were saved with"
    (Some(AlignRight), true) (seededBack.[2].align, seededBack.[2].isPinned)
let pastTheEnd =
    let p : PendingTab = { tab = tab 0x999 browser "late"; rank = 9; isRestoreSeed = true }
    saveScreen [ ([editorTabs.[0]], None, None, [p]) ]
is "an entry whose index is past the end goes last"
    ["notes"; "late"] (titlesOf (loadText (asText pastTheEnd)).[0].windows)

printfn ""
printfn "5. How long an entry is kept. A window that has not started is waited"
printfn "   for a month; a tab the user closed is kept eight days, because"
printfn "   reopening it is a deliberate act and starting an application is not."
let aged (days: float) (isSeed: bool) =
    let p : PendingTab =
        { tab = { tab 0x900 browser "old" with
                    seedSince = Some(daysAgo days); closedByUser = not isSeed }
          rank = 0
          isRestoreSeed = isSeed }
    p
let survives (days: float) (isSeed: bool) =
    seedsToSave now seedMaxAgeDays closedTabMaxAgeDays closedTabSaveLimit [aged days isSeed]
    |> List.isEmpty |> not
is "a seed of 29 days is written back" true (survives 29.0 true)
is "a seed of 31 days is not" false (survives 31.0 true)
is "a closed tab of 7 days is written back" true (survives 7.0 false)
is "a closed tab of 9 days is not" false (survives 9.0 false)
let manyClosed =
    [ for i in 1 .. 60 ->
        let p : PendingTab =
            { tab = { tab (0x1000 + i) browser (sprintf "closed %d" i) with
                        seedSince = Some(daysAgo (float i / 24.0)); closedByUser = true }
              rank = i
              isRestoreSeed = false }
        p ]
let kept = seedsToSave now seedMaxAgeDays closedTabMaxAgeDays closedTabSaveLimit manyClosed
is "closed tabs are capped" closedTabSaveLimit (List.length kept)
is "and it is the newest that are kept, not the first in the list"
    ["closed 1"; "closed 2"] (titlesOf (kept |> List.truncate 2 |> List.map (fun p -> p.tab)))
let manySeeds =
    manyClosed
    |> List.map (fun p ->
        { p with
            isRestoreSeed = true
            tab = { p.tab with closedByUser = false } })
is "seeds are not capped with them" 60
    (List.length (seedsToSave now seedMaxAgeDays closedTabMaxAgeDays closedTabSaveLimit manySeeds))

printfn ""
printfn "6. A group with nothing left in it is not written at all: emptying a"
printfn "   group is the plainest way of saying it is finished with."
let emptyGroup =
    let g : GroupToSave =
        { stripOrder = [hwnd 1]; mirrorOrder = []; seeds = []
          tabPosition = Some("TopLeft"); snapMargin = Some(true) }
    write (fun _ -> None) [g]
is "no group object is written" 0 emptyGroup.Count

printfn ""
printfn "7. Coming back after a Windows restart. Every handle in the file is"
printfn "   dead and every window on screen is new, so the entries are found"
printfn "   again by application and title."
let restartedLive = [
    liveWindow 0x501 hidemaru "notes" (at 0 0)
    liveWindow 0x502 hidemaru "todo" (at 200 0)
    liveWindow 0x503 terminal "Claude1" (at 400 0)
    liveWindow 0x504 terminal "Claude2" (at 600 0)
    liveWindow 0x505 browser "Docs" (at 800 0) ]
let restarted = planOf (loadText screenText) restartedLive
let editorPlan = restarted.[0]
is "every tab of the group is matched" 5 (editorPlan.tabs |> List.filter (fun t -> t.outcome = Matched) |> List.length)
is "each to the window with its title"
    [Some(hwnd 0x501); Some(hwnd 0x502); Some(hwnd 0x504); Some(hwnd 0x505); Some(hwnd 0x503)]
    (editorPlan.tabs |> List.map (fun t -> t.live))
is "the name comes back with it" (Some("Notes")) editorPlan.tabs.[0].applied.renamedTabName
is "so does the pin" true editorPlan.tabs.[0].applied.isPinned
is "so does the alignment" (Some(AlignLeft)) editorPlan.tabs.[0].applied.align
is "and the colours" (Some("112233FF")) (editorPlan.tabs |> List.last).applied.fillColor
is "the group's settings survive" (Some("TopLeft"), Some(true)) (editorPlan.tabPosition, editorPlan.snapMargin)
is "the saved order is carried forward in the old handles"
    (editorTabs |> List.map (fun t -> t.hwnd)) editorPlan.savedOrder
is "and the group is named by the first of them" (hwnd 0x101) editorPlan.token
let mailPlan = restarted.[1]
is "a group none of whose windows are up yet waits"
    [Waiting; Waiting] (mailPlan.tabs |> List.map (fun t -> t.outcome))
is "and its entries keep everything they were saved with"
    mailTabs (mailPlan.tabs |> List.map (fun t -> t.saved))

printfn ""
printfn "8. A WindowTabs restart inside one Windows session: the windows still"
printfn "   carry the handles they were saved with, so they are matched by"
printfn "   handle and are certainly themselves."
let sameSession =
    planOf (loadText screenText)
           (editorTabs |> List.map (fun t ->
                liveWindow (int (t.hwnd.ToInt64())) (t.exePath.Value) (t.windowTitle.Value) t.rect))
is "matched to themselves"
    (editorTabs |> List.map (fun t -> Some(t.hwnd)))
    (sameSession.[0].tabs |> List.map (fun t -> t.live))
is "and nothing is held back from them"
    (editorTabs |> List.map Some)
    (sameSession.[0].tabs |> List.map (fun t -> if t.stateIsCertain then Some(t.applied) else None))

printfn ""
printfn "9. A handle that Windows has handed to something else. An entry now"
printfn "   sits in the file for days, so the number in it can belong to an"
printfn "   unrelated window by the time it is read - it is trusted only while"
printfn "   the application still agrees."
let reused = planOf [ { windows = [ { tab 0x101 hidemaru "notes" with rect = at 0 0 } ]
                        tabPosition = None; snapMargin = None } ]
                    [ liveWindow 0x101 browser "something else" (at 0 0) ]
is "the window is not taken" None reused.[0].tabs.[0].live
is "the entry waits for its own application instead" Waiting reused.[0].tabs.[0].outcome
// A tab the user closed is matched on its title and nothing else: its saved
// handle belongs to a window that no longer exists, and reserving it would let
// an unrelated window of the same application take the closed tab's name,
// colours and pin without its title ever being looked at.
let closedByUserEntry = { tab 0x101 hidemaru "notes" with closedByUser = true }
let closedReuse = planOf [ { windows = [closedByUserEntry]; tabPosition = None; snapMargin = None } ]
                         [ liveWindow 0x101 hidemaru "a completely different document" None ]
is "a closed tab does not claim a window by handle either" None closedReuse.[0].tabs.[0].live

printfn ""
printfn "10. Windows that cannot be told apart. Two of one application with"
printfn "    one title in one group: this is what the third generation was"
printfn "    asked to DEFINE, so what follows is the definition."
printfn ""
printfn "    (a) They are put back where they were. The evidence is the saved"
printfn "        rectangle, and the arrangement that is nearest overall wins -"
printfn "        not each entry taking the nearest window in turn, which lets"
printfn "        the first entry take the only window that fitted the second."
let twinTabs = [
    { tab 0x301 terminal "Claude" with align = Some(AlignLeft); isPinned = true; rect = at 0 0 }
    { tab 0x302 terminal "Claude" with align = Some(AlignRight); rect = at 500 0 } ]
let twinsFile = [ { windows = twinTabs; tabPosition = None; snapMargin = None } ]
// Enumerated in the other order on purpose: the answer must come from the
// rectangles, not from the order Windows happened to list the windows in.
let twinsHome = planOf twinsFile [ liveWindow 0x402 terminal "Claude" (at 500 0)
                                   liveWindow 0x401 terminal "Claude" (at 0 0) ]
is "each entry gets the window standing where it stood"
    [Some(hwnd 0x401); Some(hwnd 0x402)] (twinsHome.[0].tabs |> List.map (fun t -> t.live))
is "so each window gets its own alignment and pin"
    [(Some(AlignLeft), true); (Some(AlignRight), false)]
    (twinsHome.[0].tabs |> List.map (fun t -> (t.applied.align, t.applied.isPinned)))
// Saved at 0 and at 50; the windows are at -1000 and at 60. Taking the
// nearest in turn hands the window at 60 to the entry saved at 0 - it is the
// nearer of the two to it - and leaves the entry saved at 50 with the window
// a thousand away. Taken as a whole the window at 60 goes to the entry saved
// at 50, ten away from where it was, which is plainly the right answer.
is "the arrangement is chosen as a whole, not one entry at a time"
    [Some(0); Some(1)]
    (assignTwins [Some(0.0, 0.0); Some(50.0, 0.0)] [Some(-1000.0, 0.0); Some(60.0, 0.0)])
printfn ""
printfn "    (b) When there is nothing to choose between them - no rectangles,"
printfn "        or two windows standing in the same place - the entries are"
printfn "        taken in saved order against the order the windows came in,"
printfn "        so the answer is at least the same every time."
let twinsNowhere () =
    planOf [ { windows = twinTabs |> List.map (fun t -> { t with rect = None })
               tabPosition = None; snapMargin = None } ]
           [ liveWindow 0x401 terminal "Claude" None
             liveWindow 0x402 terminal "Claude" None ]
is "saved order against arrival order"
    [Some(hwnd 0x401); Some(hwnd 0x402)] ((twinsNowhere ()).[0].tabs |> List.map (fun t -> t.live))
is "and it is the same answer the second time"
    ((twinsNowhere ()).[0].tabs |> List.map (fun t -> t.live))
    ((twinsNowhere ()).[0].tabs |> List.map (fun t -> t.live))
printfn ""
printfn "    (c) Whichever way round they end up, the GROUP gets back exactly"
printfn "        what it was saved with. No window is given two entries, no"
printfn "        entry is given to two windows, and no alignment or pin is"
printfn "        dropped on the way - the set is preserved even when the"
printfn "        individuals cannot be told apart."
let nowhere = (twinsNowhere ()).[0]
is "one entry each" 2 (nowhere.tabs |> List.choose (fun t -> t.live) |> List.distinct |> List.length)
is "the alignments the group gets back are the ones it was saved with"
    (twinTabs |> List.map (fun t -> t.align) |> List.sort)
    (nowhere.tabs |> List.map (fun t -> t.applied.align) |> List.sort)
is "and so are the pins"
    (twinTabs |> List.map (fun t -> t.isPinned) |> List.sort)
    (nowhere.tabs |> List.map (fun t -> t.applied.isPinned) |> List.sort)
is "and the places in the saved order are untouched"
    [0; 1] (nowhere.tabs |> List.map (fun t -> t.rank))
printfn ""
printfn "    (d) A name is different. Putting one twin's rename on the other"
printfn "        is a visible mistake and there is no argument that the set is"
printfn "        preserved, so it is applied only when the twins agree about"
printfn "        it - which is the ordinary case of two terminals renamed"
printfn "        alike - and otherwise held back."
let renamedTwins (a: string) (b: string) =
    planOf [ { windows = [ { tab 0x301 terminal "Claude" with renamedTabName = Some(a); rect = None }
                           { tab 0x302 terminal "Claude" with renamedTabName = Some(b); rect = None } ]
               tabPosition = None; snapMargin = None } ]
           [ liveWindow 0x401 terminal "Claude" None
             liveWindow 0x402 terminal "Claude" None ]
is "twins that agree keep the name"
    [Some("work"); Some("work")]
    ((renamedTwins "work" "work").[0].tabs |> List.map (fun t -> t.applied.renamedTabName))
is "twins that disagree are left unnamed"
    [None; None]
    ((renamedTwins "left" "right").[0].tabs |> List.map (fun t -> t.applied.renamedTabName))
is "but the names are still in the entries, so they are still in the file"
    [Some("left"); Some("right")]
    ((renamedTwins "left" "right").[0].tabs |> List.map (fun t -> t.saved.renamedTabName))
is "and the entries say so, for a claim that arrives later"
    [false; false] ((renamedTwins "left" "right").[0].tabs |> List.map (fun t -> t.stateIsCertain))
// The same question for twins whose windows are not up yet: it is settled now,
// while all of them are still there to be compared, and carried on the entry.
// Asking it at the claim instead would look at whichever twins had not been
// claimed yet, and the last one left would look unique when it is not.
let waitingTwins (a: string) (b: string) =
    planOf [ { windows = [ { tab 0x301 terminal "Claude" with renamedTabName = Some(a) }
                           { tab 0x302 terminal "Claude" with renamedTabName = Some(b) } ]
               tabPosition = None; snapMargin = None } ]
           []
is "waiting twins that agree may hand their name over when claimed"
    [true; true] ((waitingTwins "work" "work").[0].tabs |> List.map (fun t -> t.stateIsCertain))
is "waiting twins that disagree may not"
    [false; false] ((waitingTwins "left" "right").[0].tabs |> List.map (fun t -> t.stateIsCertain))
is "and both are still waiting, with their names intact"
    [(Waiting, Some("left")); (Waiting, Some("right"))]
    ((waitingTwins "left" "right").[0].tabs |> List.map (fun t -> (t.outcome, t.saved.renamedTabName)))
printfn ""
printfn "    (e) The same identity in two DIFFERENT groups is not restored at"
printfn "        all. Picking one of them is not an uncertain restore, it is a"
printfn "        certain change to the grouping."
let crossGroup =
    planOf [ { windows = [tab 0x301 terminal "Claude"]; tabPosition = None; snapMargin = None }
             { windows = [tab 0x302 terminal "Claude"]; tabPosition = None; snapMargin = None } ]
           [ liveWindow 0x401 terminal "Claude" None ]
is "neither group takes the window"
    [None; None] (crossGroup |> List.map (fun g -> g.tabs.[0].live))
is "and neither waits for it either"
    [Unusable; Unusable] (crossGroup |> List.map (fun g -> g.tabs.[0].outcome))

printfn ""
printfn "11. Loading a file and saving it again changes nothing. This is what"
printfn "    a group whose applications have not started does at every save"
printfn "    for as long as a month - a cycle that drifts loses the group one"
printfn "    field at a time, and that is exactly how the alignments were"
printfn "    being lost."
// The first cycle does add one thing, and it is not drift: an entry written by
// a window that was running has no record of when its wait began, so the load
// starts the clock now and the save writes that down.
let waitingWriteBack (groups: SavedGroup list) =
    let planned = planOf groups []
    write (fun _ -> None)
          (planned |> List.map (fun g ->
                let gs : GroupToSave =
                    { stripOrder = []; mirrorOrder = []
                      seeds = g.tabs
                              |> List.filter (fun t -> t.outcome = Waiting)
                              |> List.map pendingOfPlanned
                      tabPosition = g.tabPosition
                      snapMargin = g.snapMargin }
                gs))
let cycle1 = asText (waitingWriteBack (loadText screenText))
let cycle2 = asText (waitingWriteBack (loadText cycle1))
let cycle3 = asText (waitingWriteBack (loadText cycle2))
// The one thing the first cycle adds, and nothing else. A key appearing here
// that nobody asked for would be the other way of losing a group: writing
// state that was never saved is as much a change to the file as dropping it.
let keysOf (s: string) =
    [ for g in JArray.Parse(s) do
        for w in g.["windows"] do
            for p in (w :?> JObject).Properties() -> p.Name ]
    |> List.distinct |> List.sort
is "the first cycle adds the moment the wait began, and nothing else"
    (("seedSince" :: keysOf screenText) |> List.distinct |> List.sort) (keysOf cycle1)
is "a second cycle changes nothing" cycle2 cycle3
is "and a third" cycle3 (asText (waitingWriteBack (loadText cycle3)))
let cycledEditor = (loadText cycle3).[0]
is "the tabs are all still there, in the saved order"
    (titlesOf editorTabs) (titlesOf cycledEditor.windows)
is "the alignments are all still there"
    (editorTabs |> List.map (fun t -> t.align)) (cycledEditor.windows |> List.map (fun t -> t.align))
is "the tab that had no alignment still has none"
    None cycledEditor.windows.[2].align
is "the pins are still there"
    (editorTabs |> List.map (fun t -> t.isPinned)) (cycledEditor.windows |> List.map (fun t -> t.isPinned))
is "the names are still there"
    (editorTabs |> List.map (fun t -> t.renamedTabName))
    (cycledEditor.windows |> List.map (fun t -> t.renamedTabName))
is "the colours are still there"
    (editorTabs |> List.map (fun t -> (t.fillColor, t.underlineColor, t.borderColor)))
    (cycledEditor.windows |> List.map (fun t -> (t.fillColor, t.underlineColor, t.borderColor)))
is "the rectangles are still there"
    (editorTabs |> List.map (fun t -> t.rect)) (cycledEditor.windows |> List.map (fun t -> t.rect))
is "and so are the group's own settings"
    (Some("TopLeft"), Some(true)) (cycledEditor.tabPosition, cycledEditor.snapMargin)
// The twins' names are the case where the state cannot be applied. It must
// still not be thrown away: an entry nobody has claimed is the file's only
// copy of it.
let twinNamesFile =
    asText (saveScreen [ ([ { tab 0x301 terminal "Claude" with renamedTabName = Some("left") }
                            { tab 0x302 terminal "Claude" with renamedTabName = Some("right") } ],
                          None, None, []) ])
let twinNamesBack = (loadText (asText (waitingWriteBack (loadText twinNamesFile)))).[0].windows
is "a name that could not be applied is still written back"
    [Some("left"); Some("right")] (twinNamesBack |> List.map (fun t -> t.renamedTabName))

printfn ""
printfn "12. An entry that has waited too long leaves the file, and one that"
printfn "    has not stays in it."
let waitedFile (days: float) (byUser: bool) =
    [ { windows = [ { tab 0x301 terminal "Claude" with
                        seedSince = Some(daysAgo days); closedByUser = byUser } ]
        tabPosition = None; snapMargin = None } ]
is "a seed of 31 days is dropped" Expired ((planOf (waitedFile 31.0 false) []).[0].tabs.[0].outcome)
is "a seed of 29 days waits on" Waiting ((planOf (waitedFile 29.0 false) []).[0].tabs.[0].outcome)
is "a closed tab of 9 days is dropped" Expired ((planOf (waitedFile 9.0 true) []).[0].tabs.[0].outcome)
is "a closed tab of 7 days waits on" Waiting ((planOf (waitedFile 7.0 true) []).[0].tabs.[0].outcome)
is "and a dropped entry is not written back"
    0 (waitingWriteBack (waitedFile 31.0 false)).Count

printfn ""
printfn "13. The end of the journey: the tabs on the strip, in order."
printfn ""
printfn "    Everything above says the state survives the file. This says the"
printfn "    group LOOKS the way it was left, which is the thing the user"
printfn "    actually asked for. The saved file is the one written in check 1,"
printfn "    the windows come back as they do in check 7, and the order is"
printfn "    worked out by TabOrder - all three of the program's own."

// The strip draws its tabs in four bands and can only honour the saved order
// inside a band, so a tab's alignment and pin ARE its position. A tab with no
// alignment of its own follows the group's tabPosition, which is why turning
// that absence into an explicit side (check 2) would be a change of position
// and not a tidy-up.
let zoneOfTab (groupTabPosition: string option) (t: SavedTab) =
    let leftAligned =
        match t.align with
        | Some(AlignLeft) -> true
        | Some(AlignRight) -> false
        | None -> groupTabPosition <> Some("TopRight")
    TabOrder.zoneOf leftAligned t.isPinned

// The group as it fills up: each window arrives, and the whole group is put
// back into the saved order. Nothing is carried between arrivals, so the
// answer cannot depend on which application started first.
let stripAfter (g: PlannedGroup) (arrivals: (IntPtr * SavedTab) list) =
    let stateOf = arrivals |> List.map (fun (h, t) -> h, t) |> Map.ofList
    let oldHandleOf (h: IntPtr) =
        g.tabs |> List.tryPick (fun t -> if t.live = Some(h) then Some(t.saved.hwnd) else None)
    let mutable strip : IntPtr list = []
    for (h, _) in arrivals do
        strip <- strip @ [h]
        let placed =
            strip |> List.map (fun x -> TabOrder.placed x (zoneOfTab g.tabPosition stateOf.[x]))
        strip <- TabOrder.restoreOrder g.savedOrder oldHandleOf placed
    strip

// Every arrival order of the five, since an application that is not in
// Startup can come up half an hour after the rest.
let rec permutations = function
    | [] -> [[]]
    | xs -> xs |> List.collect (fun x -> permutations (xs |> List.filter ((<>) x)) |> List.map (fun r -> x :: r))

let restoredTabs =
    editorPlan.tabs |> List.map (fun t -> (t.live.Value, t.applied))
let expectedOrder = editorPlan.tabs |> List.map (fun t -> t.live.Value)
let allOrders =
    permutations restoredTabs |> List.map (fun order -> stripAfter editorPlan order) |> List.distinct
is "every one of the 120 arrival orders ends in the saved order"
    [expectedOrder] allOrders
is "which is the order the tabs were saved in"
    ["notes"; "todo"; "Claude2"; "Docs"; "Claude1"]
    (expectedOrder |> List.map (fun h ->
        editorPlan.tabs |> List.pick (fun t -> if t.live = Some(h) then t.saved.windowTitle else None)))

// The half of the group that was not running when WindowTabs started. Its
// entries wait, and each window is placed as it arrives - including the one
// that arrives first and finds no group to join, which is the fault this
// whole line of work started from.
let partialLive = [ liveWindow 0x502 hidemaru "todo" (at 200 0)
                    liveWindow 0x505 browser "Docs" (at 800 0) ]
let partial = (planOf (loadText screenText) partialLive).[0]
is "the two that are up are matched" 2 (partial.tabs |> List.filter (fun t -> t.live.IsSome) |> List.length)
is "and the other three wait" 3 (partial.tabs |> List.filter (fun t -> t.outcome = Waiting) |> List.length)
// The three that were waiting start later and claim their entries. A claimed
// entry hands over the state it was holding - all of it, since none of these
// identities is shared.
let lateArrivals =
    [ (hwnd 0x601, "notes"); (hwnd 0x603, "Claude1"); (hwnd 0x604, "Claude2") ]
    |> List.map (fun (h, title) ->
        (h, partial.tabs |> List.pick (fun t ->
                if t.saved.windowTitle = Some(title) then Some(t.saved) else None)))
let claimedFrom =
    (partial.tabs |> List.choose (fun t -> t.live |> Option.map (fun h -> (h, t.saved.hwnd))))
    @ (List.zip (lateArrivals |> List.map fst)
               (lateArrivals |> List.map (fun (_, t) -> t.hwnd)))
let lateStrip (order: (IntPtr * SavedTab) list) =
    let stateOf = order |> Map.ofList
    let oldHandleOf (h: IntPtr) = claimedFrom |> List.tryPick (fun (n, o) -> if n = h then Some(o) else None)
    let mutable strip : IntPtr list = []
    for (h, _) in order do
        strip <- strip @ [h]
        let placed = strip |> List.map (fun x -> TabOrder.placed x (zoneOfTab partial.tabPosition stateOf.[x]))
        strip <- TabOrder.restoreOrder partial.savedOrder oldHandleOf placed
    strip
let startingPoint = partial.tabs |> List.choose (fun t -> t.live |> Option.map (fun h -> (h, t.applied)))
let lateOrders =
    permutations lateArrivals
    |> List.map (fun late -> lateStrip (startingPoint @ late))
    |> List.distinct
is "however late the rest start, the group ends up in the saved order"
    [["notes"; "todo"; "Claude2"; "Docs"; "Claude1"]]
    (lateOrders |> List.map (List.map (fun h ->
        let saved = claimedFrom |> List.pick (fun (n, o) -> if n = h then Some(o) else None)
        editorTabs |> List.pick (fun t -> if t.hwnd = saved then t.windowTitle else None))))

printfn ""
if failures = 0 then printfn "%d checks, all passed." checks
else printfn "%d checks, %d FAILED." checks failures
exit (if failures = 0 then 0 else 1)
