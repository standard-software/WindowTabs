// Self-checks for WtProgram/Shared/CaptionDragFallback.fs (the MOVESIZESTART
// fallback of "lock window position"). Pure: no window is created, moved or
// queried. Run from the repository root:
//   fsi.exe --exec .\Tools\CaptionDragFallback.Tests.fsx
#load "../WtProgram/Shared/CaptionDragFallback.fs"

open Bemo.CaptionDragFallback

let mutable failed = 0
let mutable passed = 0

let check name condition =
    if condition then
        passed <- passed + 1
        printfn "PASS  %s" name
    else
        failed <- failed + 1
        printfn "FAIL  %s" name

// ----- grabOfPress (the press point and rectangle are the hook's, at the press) -----

// A window on a secondary monitor to the left, so coordinates are negative.
let win = box -1500 200 800 600 // right = -700, bottom = 800
let band96 = edgeBand 96

check "edge band is 16 px at 96 DPI" (band96 = 16)
check "edge band scales with DPI" (edgeBand 144 = 24 && edgeBand 192 = 32)
check "unknown DPI falls back to 96" (edgeBand 0 = 16)

check "caption answer in the middle of the title bar is a caption grab"
    (grabOfPress (Some hitCaption) win -1100 215 band96 = CaptionGrab)
check "caption answer right under the top edge is still a caption grab"
    (grabOfPress (Some hitCaption) win -1100 (win.y + 2) band96 = CaptionGrab)
check "caption answer outside the window is not trusted"
    (grabOfPress (Some hitCaption) win -600 215 band96 = UnknownGrab)

for code, name, cx, cy in
        [ 10, "left", win.x + 2, 500
          11, "right", win.right - 3, 500
          12, "top", -1100, win.y + 1
          15, "bottom", -1100, win.bottom - 1
          13, "top-left", win.x + 1, win.y + 1
          14, "top-right", win.right - 1, win.y + 1
          16, "bottom-left", win.x + 1, win.bottom - 1
          17, "bottom-right", win.right - 1, win.bottom - 1
          4, "grow box", win.right - 5, win.bottom - 5 ] do
    check (sprintf "%s border answer at that edge is a border grab" name)
        (grabOfPress (Some code) win cx cy band96 = BorderGrab)

check "border answer far from every edge is not trusted"
    (grabOfPress (Some 10) win -1100 500 band96 = UnknownGrab)
check "no answer in the title area leaves the grab unknown"
    (grabOfPress None win -1100 215 band96 = UnknownGrab)
check "client answer in a custom title bar leaves the grab unknown"
    (grabOfPress (Some 1) win -1100 215 band96 = UnknownGrab)
check "no answer right under the top edge is left to geometry (custom title bars sit there)"
    (grabOfPress None win -1100 (win.y + 2) band96 = UnknownGrab)
for name, cx, cy in [ "left", win.x + 3, 500; "right", win.right - 4, 500; "bottom", -1100, win.bottom - 2 ] do
    check (sprintf "unidentified press in the %s band is taken as a resize" name)
        (grabOfPress (Some 1) win cx cy band96 = BorderGrab && grabOfPress None win cx cy band96 = BorderGrab)
check "caption answer inside the left band is taken as a resize, not trusted as a caption"
    (grabOfPress (Some hitCaption) win (win.x + 3) 215 band96 = BorderGrab)

// ----- pressApplies -----

let anchor = box 100 100 800 600
let moved = box 160 100 800 600

check "button still held: the recorded press started the loop" (pressApplies true 50000 anchor anchor)
check "released flick that already moved the window applies" (pressApplies false 300 anchor moved)
check "keyboard move shortly after a click (rectangle untouched) does not apply" (not (pressApplies false 300 anchor anchor))
check "released press that is not recent does not apply" (not (pressApplies false (recentPressMilliseconds + 1) anchor moved))
check "negative elapsed time (tick confusion) does not apply" (not (pressApplies false -5 anchor moved))

// ----- start -----

check "border grab arms an undecided session" ((start BorderGrab anchor).Value.verdict = Undecided)
check "degenerate anchor arms nothing" (start UnknownGrab (box 0 0 0 0) = None)
check "unknown grab arms an undecided session"
    (match start UnknownGrab anchor with
     | Some s -> s.verdict = Undecided && s.anchor = anchor && s.settleUntil = None
     | None -> false)

// ----- single observations -----

let caption = (start CaptionGrab anchor).Value
let unknown = (start UnknownGrab anchor).Value

check "unchanged rectangle corrects nothing and decides nothing"
    (observe 0L true false caption anchor = (Some caption, NoCorrection))
check "caption: a move is put back by position only"
    (snd (observe 0L true false caption (box 150 130 800 600)) = RestorePosition(100, 100))
check "caption: a left-half snap at the work-area origin is undone with its size"
    (let a = box 0 0 800 600
     let s = (start CaptionGrab a).Value
     snd (observe 0L true false s (box 0 0 960 1040)) = RestoreBounds a)
check "a maximized window ends the lock without a correction"
    (observe 0L false false caption (box 150 130 800 600) = (None, NoCorrection))
check "a degenerate rectangle ends the lock without a correction"
    (observe 0L true false caption (box 0 0 0 0) = (None, NoCorrection))
check "a correction echo corrects nothing"
    (let s, _ = observe 0L true false caption (box 150 130 800 600)
     snd (observe 0L true false s.Value anchor) = NoCorrection)
check "inside the loop a newer press does not end the lock"
    (snd (observe 0L true true unknown (box 90 100 800 600)) = RestorePosition(100, 100))

check "unknown: a pure move is put back"
    (snd (observe 0L true false unknown (box 90 100 800 600)) = RestorePosition(100, 100))
for dx, dy, dw, dh, name in
        [ 0, 0, 30, 0, "right"; -30, 0, 30, 0, "left"; 0, -30, 0, 30, "top"; 0, 0, 0, 30, "bottom"
          -30, -30, 30, 30, "top-left"; 0, -30, 30, 30, "top-right"
          -30, 0, 30, 30, "bottom-left"; 0, 0, 30, 30, "bottom-right"
          0, 0, -30, -30, "shrinking bottom-right"; 30, 30, -30, -30, "shrinking top-left" ] do
    let r = box (anchor.x + dx) (anchor.y + dy) (anchor.width + dw) (anchor.height + dh)
    check (sprintf "unknown: %s border resize is left alone and ends the lock" name)
        (observe 0L true false unknown r = (None, NoCorrection))
check "unknown: move plus 1 px resize is still a move (no edge standing)"
    (snd (observe 0L true false unknown (box 200 200 801 600)) = RestoreBounds anchor)
check "unknown: a size change keeping one edge per axis counts as a resize"
    (decideFromGeometry anchor (box 100 50 900 650) = Resizing)
// The counter-example raised against geometry alone: a resize that keeps its
// centre looks like a move. A border grab must still allow that size change.
check "centre-keeping resize from a side-band press is allowed"
    (let s = (start (grabOfPress (Some 1) anchor (anchor.x + 3) 400 (edgeBand 96)) anchor).Value
     observe 0L true false s (box 90 90 820 620) = (None, NoCorrection))
check "border: moving the top edge during resizing remains allowed"
    (observe 0L true false (start BorderGrab anchor).Value (box 100 70 800 630) = (None, NoCorrection))
check "Terminal HTTOP same-size translation from the recorded incident is restored"
    (let a = box -1920 24 1497 1008
     let grab = grabOfPress (Some 12) a -1718 32 (edgeBand 96)
     grab = BorderGrab &&
     snd (observe 0L true false (start grab a).Value (box -1776 36 1497 1008)) = RestorePosition(-1920, 24))

// ----- event sequences -----

type Step =
    | Rect of Box       // the system loop, the shell or the app sets the rectangle
    | EndLoop           // MOVESIZEEND
    | Wait of int64     // milliseconds pass
    | Maximize of Box   // the window becomes maximized with this rectangle
    | Press             // a mouse button goes down somewhere

type Outcome =
    {
        final: Box
        corrections: Correction list
        echoes: int
        session: Session option
    }

// Drive one drag the way WindowGroup does: every rectangle change is
// observed, a correction is applied to the simulated window, and the change
// that correction itself causes is observed too (it must correct nothing).
let simulate grab (anchor: Box) steps =
    let mutable now = 10000L
    let mutable rect = anchor
    let mutable normal = true
    let mutable newerPress = false
    let mutable session = start grab anchor
    let mutable echoes = 0
    let corrections = ResizeArray<Correction>()
    for step in steps do
        let changed =
            match step with
            | Rect r -> Some r
            | EndLoop ->
                session <- session |> Option.bind (finish now)
                None
            | Wait ms ->
                now <- now + ms
                None
            | Maximize r ->
                normal <- false
                Some r
            | Press ->
                newerPress <- true
                None
        match changed, session with
        | Some r, None -> rect <- r
        | Some r, Some s ->
            rect <- r
            let next, correction = observe now normal newerPress s rect
            session <- next
            if correction <> NoCorrection then
                corrections.Add correction
                rect <-
                    match correction with
                    | RestorePosition(x, y) -> { rect with x = x; y = y }
                    | RestoreBounds b -> b
                    | NoCorrection -> rect
                match session with
                | Some s ->
                    let next, echo = observe now normal newerPress s rect
                    session <- next
                    if echo <> NoCorrection then echoes <- echoes + 1
                | None -> ()
        | None, _ -> ()
    { final = rect; corrections = List.ofSeq corrections; echoes = echoes; session = session }

let moveBy dx dy = Rect(box (anchor.x + dx) (anchor.y + dy) anchor.width anchor.height)

let dragThenSnap =
    simulate UnknownGrab anchor
        [ moveBy 3 1; moveBy 40 10; moveBy 400 50; Rect(box 0 0 960 1040); EndLoop ]
check "sequence: custom title bar drag then snap ends where it started"
    (dragThenSnap.final = anchor)
check "sequence: every step of that drag was corrected"
    (dragThenSnap.corrections.Length = 4)
check "sequence: no correction ever caused another one"
    (dragThenSnap.echoes = 0)
check "sequence: a corrected drag keeps settling after the loop ends"
    (match dragThenSnap.session with Some s -> s.settleUntil.IsSome | None -> false)

let snapAfterEnd =
    simulate UnknownGrab anchor
        [ moveBy 20 0; moveBy 700 0; EndLoop; Wait 60L; Rect(box 960 0 960 1040) ]
check "sequence: a snap the shell applies after MOVESIZEEND is undone"
    (snapAfterEnd.final = anchor)

let menuMoveAfterEnd =
    simulate UnknownGrab anchor
        [ moveBy 20 0; EndLoop; Wait 100L; Press; Rect(box 600 100 800 600) ]
check "sequence: a move that follows a new click inside the settle time stands (WindowTabs menu)"
    (menuMoveAfterEnd.final = box 600 100 800 600 && menuMoveAfterEnd.session = None)

let lateMove =
    simulate UnknownGrab anchor
        [ moveBy 20 0; EndLoop; Wait (settleMilliseconds + 1L); moveBy 500 0 ]
check "sequence: a move after the settle time is not touched"
    (lateMove.final = box 600 100 800 600 && lateMove.session = None)

let clickThenMaximize =
    simulate UnknownGrab anchor
        [ EndLoop; Wait 150L; Maximize(box -8 -8 1936 1056) ]
check "sequence: a click that started a loop arms nothing after it"
    (clickThenMaximize.session = None && clickThenMaximize.corrections.IsEmpty)
check "sequence: so the double-click that follows still maximizes"
    (clickThenMaximize.final = box -8 -8 1936 1056)

let jitterThenMaximize =
    simulate UnknownGrab anchor
        [ moveBy 1 0; EndLoop; Wait 150L; Maximize(box -8 -8 1936 1056) ]
check "sequence: a 1 px jitter before a double-click does not undo the maximize"
    (jitterThenMaximize.final = box -8 -8 1936 1056 && jitterThenMaximize.session = None)

let unknownResize =
    simulate UnknownGrab anchor
        [ Rect(box 90 100 810 600); Rect(box 20 100 880 600); Rect(box 60 100 840 600); EndLoop ]
check "sequence: unidentified left-border resize is never corrected"
    (unknownResize.final = box 60 100 840 600 && unknownResize.corrections.IsEmpty)
check "sequence: and leaves no session behind"
    (unknownResize.session = None)

let resizeThroughOriginalSize =
    simulate UnknownGrab anchor
        [ Rect(box 100 100 850 600); Rect(box 100 100 800 600); Rect(box 100 100 760 600) ]
check "sequence: resizing back through the original size stays a resize"
    (resizeThroughOriginalSize.final = box 100 100 760 600 && resizeThroughOriginalSize.corrections.IsEmpty)

let borderDrag =
    simulate BorderGrab anchor
        [ Rect(box 70 100 830 600); Rect(box 70 70 830 630); EndLoop ]
check "sequence: identified border drag resizes freely, top-left included"
    (borderDrag.final = box 70 70 830 630 && borderDrag.corrections.IsEmpty)

let secondEnd =
    let s = (start UnknownGrab anchor).Value
    let moved = (fst (observe 0L true false s (box 120 100 800 600))).Value
    let first = (finish 1000L moved).Value
    let again = (finish 1300L first).Value
    again.settleUntil = first.settleUntil
check "a second end-of-loop report does not extend the settle time" secondEnd

if failed <> 0 then failwithf "%d of %d checks failed" failed (failed + passed)
printfn "all %d checks passed" passed
