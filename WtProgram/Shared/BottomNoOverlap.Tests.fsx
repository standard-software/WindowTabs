#r "../../Newtonsoft.Json.dll"
#load "TabOrder.fs"
#load "SavedTabState.fs"
#load "SnapGeometry.fs"
#load "TabBehaviorPolicy.fs"
#load "TabEdges.fs"
#load "TabEdgeLayout.fs"
open Bemo
open Bemo.TabEdges
open Bemo.TabEdgeLayout

let mutable checks = 0
let equal label expected actual =
    checks <- checks + 1
    if expected <> actual then failwithf "%s: expected %A, got %A" label expected actual

let work = (0, 0, 1200, 900)
for thickness in [1; 24; 36; 48] do
    for offset in [-2; -1; 0; 1; 2; 4] do
        let spec =
            { edge = Edge.Bottom; direction = Direction.Outside; across = false
              thickness = thickness; minThickness = thickness
              outsideDistance = (10, 20); insideDistance = (30, 40)
              heightOffset = offset }
        let outside = place spec (100, 100, 600, 400) [work] []
        equal "outside touches frame without overlap or gap" (110, 500, 570, thickness) outside.rect
        equal "outside direction" false outside.inside
        let inside = place { spec with direction = Direction.AlwaysInside } (100, 100, 600, 400) [work] []
        equal "inside unchanged" (130, 500 - thickness, 530, thickness) inside.rect
        equal "forced inside" true inside.inside
        let reserve = snapReserve true None [spec]
        equal "full bottom thickness reserved" { noReserve with bottom = thickness } reserve
        equal "margin disabled" noReserve (snapReserve false (Some 24) [spec])
        equal "inside has no bottom margin" noReserve (snapReserve true None [{ spec with direction = Direction.AlwaysInside }])
        let snapped = reserveArea reserve work
        let placed = place spec snapped [work] []
        equal "snap leaves strip outside" false placed.inside
        let _, sy, _, sh = placed.rect
        equal "strip fits exactly at work-area bottom" 900 (sy + sh)
        equal "repeated destination snap stable" snapped (reserveArea (snapReserve true None [spec]) work)
        let atEdge = place spec (100, 500, 600, 400) [work] []
        equal "screen edge automatically goes inside" true atEdge.inside
        equal "automatic inside position unchanged" (130, 900 - thickness, 530, thickness) atEdge.rect
        let exact = place spec (100, 500 - thickness, 600, 400) [work] []
        equal "exact outside fit stays outside" false exact.inside
        let short = place spec (100, 501 - thickness, 600, 400) [work] []
        equal "one pixel short goes inside" true short.inside
        for edge in [Edge.Left; Edge.Right] do
            equal "side reserve unchanged" thickness
                (let r = snapReserve true None [{ spec with edge = edge }] in if edge = Edge.Left then r.left else r.right)

for height in [1; 24; 36; 48] do
    let reserve = snapReserve true (Some height) []
    equal "top-only reserve unchanged" { noReserve with top = height - 1 } reserve
    equal "top-only snap unchanged" (SnapGeometry.reserveTabHeight (height - 1) work) (reserveArea reserve work)
equal "empty edges reserve nothing" noReserve (snapReserve true None [])
printfn "%d checks, 0 failures" checks

module ExternalSnapRoomTests =
    let mutable checks = 0
    let equal label expected actual =
        checks <- checks + 1
        if expected <> actual then failwithf "%s: expected %A, got %A" label expected actual
    let area = (0,0,1000,800)
    let top = { noReserve with top=24 }
    let memory workArea before after = Some {workArea=workArea; before=before; after=after}
    let run enabled normal moving remembered reserve bounds =
        externalSnapRoom (if enabled then SnapTabMarginMode.all else SnapTabMarginMode.none) normal moving false -1 remembered area (reserveArea reserve area) bounds
    let snap = (0,0,500,800)
    let corrected = (0,24,500,776)
    let first = memory area snap corrected
    equal "top" (first,5,Some corrected) (run true true false None top snap)
    equal "off clears memory" (None,0,None) (run false true false first top snap)
    equal "moving preserves memory" (first,1,None) (run true true true first top snap)
    equal "non-normal preserves memory" (first,1,None) (run true false false first top snap)
    equal "interior" (None,3,None) (run true true false None top (100,100,400,300))
    equal "own echo" (first,2,None) (run true true false first top corrected)
    equal "external undo" (first,2,None) (run true true false first top snap)
    equal "new boundary resize" (memory area (0,0,600,800) (0,24,600,776),5,Some(0,24,600,776))
        (run true true false first top (0,0,600,800))
    equal "detached clears memory" (None,3,None) (run true true false first top (100,100,400,300))
    equal "near reserved band clears memory" (None,3,None) (run true true false first top (0,26,500,774))
    equal "inside strips" (None,3,None) (run true true false None noReserve snap)
    equal "off monitor clears memory" (None,4,None) (run true true false first top (-100,0,500,800))
    let offset = (0,1,500,799)
    equal "one pixel boundary tolerance" (memory area offset corrected,5,Some corrected)
        (run true true false None top offset)
    equal "near but unattached" (None,3,None) (run true true false None top (0,2,500,798))
    for reserve in [{noReserve with top=24}; {noReserve with bottom=24};
                    {noReserve with left=70}; {noReserve with right=70};
                    {top=24;bottom=24;left=70;right=70}] do
        let expected = reserveArea reserve area
        let remembered = memory area area expected
        equal "same full-area reserve as internal snap" (remembered,5,Some expected)
            (run true true false None reserve area)
        equal "all edge echoes suppressed" (remembered,2,None)
            (run true true false remembered reserve expected)
    let rightSource = (500,100,500,600)
    let rightTarget = (500,100,430,600)
    equal "right edge only changes right" (memory area rightSource rightTarget,5,Some rightTarget)
        (run true true false None {noReserve with right=70} rightSource)
    let bottomSource = (100,400,700,400)
    let bottomTarget = (100,400,700,376)
    equal "bottom only changes bottom" (memory area bottomSource bottomTarget,5,Some bottomTarget)
        (run true true false None {noReserve with bottom=24} bottomSource)
    equal "too small" (None,4,None) (run true true false first top (0,0,20,20))
    let shifted = (-1000,-800,1000,800)
    let shiftedSource = (-1000,-800,500,800)
    let shiftedTarget = (-1000,-776,500,776)
    equal "different work area replaces memory" (memory shifted shiftedSource shiftedTarget,5,Some shiftedTarget)
        (externalSnapRoom SnapTabMarginMode.all true false false -1 first shifted (reserveArea top shifted) shiftedSource)
    let resizedArea = (0,0,1000,900)
    equal "changed work area with same bounds is new" (memory resizedArea snap corrected,5,Some corrected)
        (externalSnapRoom SnapTabMarginMode.all true false false -1 first resizedArea (reserveArea top resizedArea) snap)
    let rightHalf = (500,0,500,800)
    let rightCorrected = (500,24,500,776)
    let second = memory area rightHalf rightCorrected
    equal "left half then right half" (second,5,Some rightCorrected)
        (run true true false first top rightHalf)
    let afterUndo,undoReason,undoTarget = run true true false first top snap
    equal "undo is ignored" (first,2,None) (afterUndo,undoReason,undoTarget)
    equal "new snap after undo" (second,5,Some rightCorrected)
        (run true true false afterUndo top rightHalf)
    for bounds in [(1,1,500,800); (-1,-1,500,800); (1,0,498,800)] do
        equal "one pixel undo jitter is ignored" (first,2,None)
            (run true true false first top bounds)
    equal "one pixel own echo jitter is ignored" (first,2,None)
        (run true true false first top (1,25,500,776))
    equal "two pixel boundary displacement is new" (memory area (2,0,500,800) (2,24,500,776),5,Some(2,24,500,776))
        (run true true false first top (2,0,500,800))
    let constrained = memory area snap (0,20,500,780)
    equal "accepted constrained result is an echo" (constrained,2,None)
        (run true true false constrained top (0,20,500,780))
    let sequence observations =
        let mutable remembered = None
        let mutable writes = 0
        for bounds in observations do
            let next,_,target = run true true false remembered top bounds
            remembered <- next
            if target.IsSome then writes <- writes+1
        writes,remembered
    equal "one write despite repeated rejection" (1,first)
        (sequence [snap;corrected;snap;snap;corrected;snap])
    equal "repeated input before own echo writes once" (1,first)
        (sequence [snap;snap;snap;snap])
    equal "jitter never drifts the remembered baseline" (1,first)
        (sequence [snap;(1,0,500,800);(-1,0,500,800);snap])
    equal "undo then right snap writes twice" (2,second)
        (sequence [snap;corrected;snap;rightHalf;rightCorrected;rightHalf])
    equal "half quarter half writes three times" (3,first)
        (sequence [snap;corrected;(0,0,500,400);(0,24,500,376);snap;corrected])
    equal "clearing memory allows same snap again" (2,first)
        (sequence [snap;corrected;(100,100,400,300);snap])
    equal "move start reset allows same snap again" (first,5,Some corrected)
        (run true true false None top snap)
    printfn "ExternalSnapRoom: %d checks, 0 failures" checks

module ExternalSnapRoomBoundaryTests =
    let mutable checks = 0
    let equal label expected actual =
        checks <- checks+1
        if expected <> actual then failwithf "%s: expected %A, got %A" label expected actual
    let area = (-600,-400,1000,800)
    let reserve = {top=24;left=24;right=24;bottom=24}
    let reserved = reserveArea reserve area
    let read mode state bounds = externalSnapRoom mode true false false -1 state area reserved bounds
    let source = (-600,-400,500,400)
    let memory,_,_ = read SnapTabMarginMode.all None source
    for mode in [SnapTabMarginMode.none; SnapTabMarginMode.windowTabs; "invalid"] do
        equal "non-external mode never writes and clears memory" (None,0,None) (read mode memory source)
        equal "non-external mode never schedules geometry" 0 (externalSnapReadiness mode true false false)
    for normal,moving,held,expected in
        [false,false,false,1; true,true,false,1;
         true,false,true,6; true,false,false,5] do
        equal "readiness before expensive geometry" expected
            (externalSnapReadiness SnapTabMarginMode.all normal moving held)
    // Outside/inside tolerance at every boundary, including negative coordinates.
    for before,expected in [
        (-601,-300,501,300),(-576,-300,476,300)
        (-599,-300,499,300),(-576,-300,476,300)
        (-500,-401,300,401),(-500,-376,300,376)
        (-500,-399,300,399),(-500,-376,300,376)
        (0,-300,401,300),(0,-300,376,300)
        (0,-300,399,300),(0,-300,376,300)
        (-500,0,300,401),(-500,0,300,376)
        (-500,0,300,399),(-500,0,300,376)] do
        let _,reason,target = read SnapTabMarginMode.all None before
        equal "one pixel boundary remains eligible" (5,Some expected) (reason,target)
    for before in [(-602,-300,502,300);(-500,-402,300,402);(0,-300,402,300);(-500,0,300,402);
                   (-600,-400,1003,800);(-600,-400,1000,803);(-600,-400,0,400);(-600,-400,500,-1)] do
        equal "invalid placement clears remembered snap" (None,4,None) (read SnapTabMarginMode.all memory before)
    let cleared,_,_ = read SnapTabMarginMode.all memory (-602,-300,502,300)
    let _,reason,target = read SnapTabMarginMode.all cleared source
    equal "valid snap after invalid placement may write" (5,true) (reason,target.IsSome)
    for invalidArea in [(0,0,0,100);(0,0,100,0);(0,0,-1,100)] do
        equal "invalid work area clears memory" (None,4,None)
            (externalSnapRoom SnapTabMarginMode.all true false false -1 memory invalidArea invalidArea (0,0,10,10))
    let crossing = (-100,-100,500,500)
    let _,crossReason,crossTarget = read SnapTabMarginMode.all None crossing
    equal "right and bottom across zero" (5,Some(-100,-100,476,476)) (crossReason,crossTarget)
    let partialArea = (0,0,1000,800)
    let partialReserve = reserveArea {noReserve with top=24;left=50} partialArea
    let _,_,partialTarget = externalSnapRoom SnapTabMarginMode.all true false false -1 None partialArea partialReserve (500,0,500,800)
    equal "only touching edge changes with multiple strips" (Some(500,24,500,776)) partialTarget
    let negativeArea = (-1600,-1200,1000,800)
    let negativeReserve = reserveArea reserve negativeArea
    for rect in [(-900,-700,300,300);(-900,-700,301,301)] do
        let _,why,destination = externalSnapRoom SnapTabMarginMode.all true false false -1 None negativeArea negativeReserve rect
        equal "right and bottom with negative edges" (5,Some(-900,-700,276,276)) (why,destination)
    let oversizedReserve = reserveArea {top=900;left=600;right=600;bottom=900} partialArea
    equal "oversized reserve keeps usable area" partialArea oversizedReserve
    equal "oversized reserve needs no correction" (None,3,None)
        (externalSnapRoom SnapTabMarginMode.all true false false -1 memory partialArea oversizedReserve partialArea)
    let duringMove,_,_ = externalSnapRoom SnapTabMarginMode.all true true false -1 memory area reserved source
    equal "normal after moving still suppresses undo" (memory,2,None)
        (read SnapTabMarginMode.all duringMove source)
    printfn "ExternalSnapRoom boundaries: %d checks, 0 failures" checks

module ExternalSnapRetryTests =
    open TabEdgeLayout.ExternalSnapRetry
    let run() =
        let mutable checks = 0
        let equal name expected actual =
            checks <- checks + 1
            if expected <> actual then failwithf "%s: expected %A, got %A" name expected actual
        equal "bounded delay" 250 delayMs
        equal "bounded button retries" 20 maxButtonChecks
        let candidate, action = transition Idle Position
        equal "position schedules candidate" Schedule action
        let waiting, action = transition candidate (Tick true)
        equal "unrelated click keeps candidate" (ButtonWaiting 19, Schedule) (waiting, action)
        let released, action = transition waiting (Tick false)
        equal "release inspects without position event" Inspect action
        let completed, action = transition released Complete
        equal "one completed candidate stops" (Idle, Nothing) (completed, action)
        equal "idle release does no work" (Idle, Nothing) (transition completed (Tick false))
        let blocked, action = transition candidate Blocked
        equal "state wait has no timer" (StateWaiting, Nothing) (blocked, action)
        equal "state wait ignores timer" (StateWaiting, Nothing) (transition blocked (Tick false))
        let resumed, action = transition blocked StateChanged
        equal "existing state event resumes without position" (Checking 20, Schedule) (resumed, action)
        equal "restored candidate inspected" (Checking 20, Inspect) (transition resumed (Tick false))
        let mutable longPress = candidate
        for _ in 1..maxButtonChecks do
            let next, action = transition longPress (Tick true)
            equal "bounded lightweight retry" Schedule action
            longPress <- next
        equal "last allowed release still inspected" (Checking 0, Inspect) (transition longPress (Tick false))
        let exhausted, action = transition longPress (Tick true)
        equal "long press stops at cap" (NotificationWaiting, Nothing) (exhausted, action)
        equal "release after cap needs position event" (NotificationWaiting, Nothing) (transition exhausted (Tick false))
        equal "state notice cannot renew expired budget" (NotificationWaiting, Nothing) (transition exhausted StateChanged)
        equal "position notice renews expired candidate" (Checking 20, Schedule) (transition exhausted Position)
        equal "notifications do not renew active button budget" (waiting, Schedule) (transition waiting Position)
        for state in [candidate;waiting;blocked;exhausted] do
            equal "reset cancels pending candidate" (Idle, Nothing) (transition state Reset)
        printfn "ExternalSnapRetry: %d checks, 0 failures" checks
    run()

module ExternalSnapRearrangeTests =
    let run() =
        let mutable checks = 0
        let equal name expected actual =
            checks <- checks + 1
            if expected <> actual then failwithf "%s: expected %A, got %A" name expected actual
        let area = (0,0,1000,800)
        let reserved = (0,24,1000,776)
        let before = (0,0,500,800)
        let after = (0,24,500,776)
        let memory = Some {workArea=area;before=before;after=after}
        let run mode released arranged bounds = externalSnapRoom mode true false released arranged memory area reserved bounds
        for released,arranged in [false,1;false,0;false,-1;true,0;true,-1] do
            equal "no verified fresh arrangement keeps undo latch" (memory,2,None)
                (run SnapTabMarginMode.all released arranged before)
        let next,reason,target = run SnapTabMarginMode.all true 1 before
        equal "verified new arrangement reapplies before" (5,Some after) (reason,target)
        equal "new arrangement never reapplies result echo" (memory,2,None) (run SnapTabMarginMode.all true 1 after)
        equal "consumed proof prevents repeated same arranged write" (next,2,None)
            (externalSnapRoom SnapTabMarginMode.all true false false 1 next area reserved before)
        for mode in [SnapTabMarginMode.none;SnapTabMarginMode.windowTabs] do
            equal "other modes never use release evidence" (None,0,None) (run mode true 1 before)
        for normal,moving in [false,false;true,true] do
            equal "state gates precede new snap evidence" (memory,1,None)
                (externalSnapRoom SnapTabMarginMode.all normal moving true 1 memory area reserved before)
        equal "bottom half unchanged" (None,3,None)
            (externalSnapRoom SnapTabMarginMode.all true false true 1 None area reserved (0,400,500,400))
        equal "normal undo after confirmed release still suppressed" (memory,2,None) (run SnapTabMarginMode.all true 0 before)
        printfn "ExternalSnapRearrange: %d checks, 0 failures" checks
    run()

module ExternalSnapDeadlineTests =
    open TabEdgeLayout.ExternalSnapRetry
    let run() =
        let mutable checks = 0
        let equal name expected actual =
            checks <- checks + 1
            if expected <> actual then failwithf "%s: expected %A, got %A" name expected actual
        equal "first position waits three seconds" 3000 (timerDelay 3000 (Checking 20) 0L 0L)
        equal "last millisecond still waits" true (waitsForSettle 3000 (Checking 20) 2999L 0L)
        equal "deadline permits inspection" false (waitsForSettle 3000 (Checking 20) 3000L 0L)
        // An existing timer fires at 3000 after a change at 2900. Its next
        // deadline is 5900, not 6000 or 8900; notifications create no timer.
        equal "coalesced position waits only remaining time" 2900 (timerDelay 3000 (Checking 20) 3000L 2900L)
        equal "new position deadline reached" 0 (remainingMs 3000 5900L 2900L)
        equal "late timer adds no quiet period" 0 (remainingMs 3000 9000L 2900L)
        equal "new front has its own deadline" 3000 (remainingMs 3000 4500L 4500L)
        let resumed,_ = transition StateWaiting StateChanged
        equal "state wake retains recent position deadline" 1200 (timerDelay 3000 resumed 1800L 0L)
        equal "state wake after quiet period does not add three seconds" 1 (timerDelay 3000 resumed 4000L 0L)
        let mutable state = Checking maxButtonChecks
        let mutable delayTotal = 0
        for index in 1..maxButtonChecks do
            let next,action = transition state (Tick true)
            state <- next
            equal "button retry scheduled" Schedule action
            let delay = timerDelay 3000 state (3000L + int64 (index-1)*250L) 0L
            equal "button interval unchanged" 250 delay
            delayTotal <- delayTotal + delay
        equal "button budget remains five seconds" 5000 delayTotal
        equal "button limit stops" (NotificationWaiting,Nothing) (transition state (Tick true))
        equal "new position does not slow held-button polling" 250 (timerDelay 3000 (ButtonWaiting 8) 5000L 4900L)
        equal "button phase bypasses quiet wait" false (waitsForSettle 3000 (ButtonWaiting 8) 5000L 4900L)
        let released,_ = transition (ButtonWaiting 8) (Tick false)
        equal "release waits only remaining quiet period" 2900 (timerDelay 3000 released 5000L 4900L)
        equal "release after deadline adds no second quiet period" 1 (timerDelay 3000 released 8000L 4900L)
        equal "settings or move reset cancels candidate" (Idle,Nothing) (transition released Reset)
        printfn "ExternalSnapDeadline: %d checks, 0 failures" checks
    run()
