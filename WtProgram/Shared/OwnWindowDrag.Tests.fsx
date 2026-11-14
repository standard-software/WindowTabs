#r "../../Newtonsoft.Json.dll"
#load "TabOrder.fs"
#load "SavedTabState.fs"
#load "SnapGeometry.fs"
#load "TabBehaviorPolicy.fs"
#load "TabEdges.fs"
open Bemo.TabEdges
let mutable checks = 0
let equal label expected actual =
    checks <- checks + 1
    if expected <> actual then failwithf "%s: expected %A, got %A" label expected actual
let all = allowedOf priority |> Option.get
let home = 0, 0, 1000, 800
for allowed in everyAllowed do
    equal "single-edge guard covers every allowed set"
        (List.length (edgesOf allowed) = 1) (isSingleEdge allowed)
for edge in priority do
    let allowed = allowedOf [edge;edge] |> Option.get
    equal "duplicate setting still means one edge" true (isSingleEdge allowed)
    for current in None :: (priority |> List.map Some) do
        equal "single edge never returns to own body" None (ownWindowEdge allowed home (500,400) current)
        // Geometry remains usable for a foreign body receiver; actual strip
        // registration and strip drop routing do not use the session guard.
        equal "body geometry still resolves the enabled edge" (Some edge)
            (ownWindowEdgeAt 1.0 allowed home (500,400) current)
    equal "enabled strip remains allowed" true (isAllowed allowed edge)
    equal "occupied strip needs no empty receiver" [] (receiverEdges allowed [edge])
    equal "direct foreign strip remains routable" (EnterForeign 2)
        (ownWindowPointerRoute (Some 1) false (Some 2) (fun () -> None))
for allowed in everyAllowed do
    for scale in [1; 2; 3] do
        for ox, oy in [0,0; -2000,-1200] do
            let bounds = ox, oy, 1000*scale, 800*scale
            let point x y = ox+x*scale, oy+y*scale
            equal "center stays on an allowed edge" (List.length (edgesOf allowed) > 1) ((ownWindowEdge allowed bounds (point 500 400) None).IsSome)
            equal "outside" None (ownWindowEdge allowed bounds (point 1000 100) None)
            for x,y in [500,10; 10,400; 990,400; 500,790] do
                let actual = ownWindowEdge allowed bounds (point x y) None
                if List.length (edgesOf allowed) = 1 then equal "single edge has no own-body return" None actual
                else equal "only allowed edges" true (actual |> Option.exists (fun e -> List.contains e (edgesOf allowed)))
equal "empty geometry" None (ownWindowEdge all (0,0,0,1) (0,0) None)
equal "corner retention" (Some Edge.Top) (ownWindowEdge all home (91,100) (Some Edge.Top))
equal "corner switches" (Some Edge.Left) (ownWindowEdge all home (80,100) (Some Edge.Top))
for horizontal in [true;false] do
    let rect = if horizontal then 0,0,100,20 else 0,0,20,100
    let near = if horizontal then 50,-19 else -19,50
    let far = if horizontal then 50,-21 else -21,50
    equal "strip tolerance" true (ownWindowStripContains false horizontal 20 rect near)
    equal "strip outside tolerance" false (ownWindowStripContains false horizontal 20 rect far)
    equal "receiver exact bounds" false (ownWindowStripContains true horizontal 20 rect near)
// A stationary anchor survives a transferred window moving above it.
let above = function 4 -> 3 | 3 -> 2 | 2 -> 1 | _ -> 0
equal "background below home" true (ownWindowHomeUncovered above 2 0 4)
equal "cover above home" false (ownWindowHomeUncovered above 2 0 1)
equal "missing hit" false (ownWindowHomeUncovered above 2 0 0)
let mutable walks = 0
equal "corrupt native chain bounded" false
    (ownWindowHomeUncovered (fun h -> walks <- walks+1; h) 2 0 3)
equal "cycle terminates" 1 walks
equal "many hidden siblings" true (ownWindowHomeUncovered (fun h -> h-1) 1 0 2000)

// Executable lifecycle model, using production geometry, routing, block and
// drop rules. Native message delivery and the decorator are not instantiated.
let contiguous pivot selected (tabs: Placed<int> list) =
    let index = tabs |> List.findIndex (fun t -> t.id=pivot)
    let origin = tabs.[index]
    let joins t = List.contains t.id selected && t.edge=origin.edge && t.align=origin.align && t.pinned=origin.pinned
    let mutable first, last = index, index
    while first > 0 && joins tabs.[first-1] do first <- first-1
    while last+1 < tabs.Length && joins tabs.[last+1] do last <- last+1
    tabs |> List.skip first |> List.take (last-first+1) |> List.map (fun t -> t.id)

type DragModel(initial: Placed<int> list, selected: int list, pivot: int) =
    let original = normalize initial
    let originalBlock = contiguous pivot selected original
    let transfer t = List.contains t.id selected
    let mutable source = original
    let mutable foreign = [placed 99 Edge.Bottom AlignEnd false]
    let mutable floating : Placed<int> list = []
    let mutable block = originalBlock
    let mutable current = Some("G", (original |> List.find (fun t -> t.id=pivot)).edge)
    let mutable preview = 0
    let mutable ended = false
    let mutable detaches = 0
    let mutable parks = 0
    let leave() =
        if current.IsSome then parks <- parks + 1
        match current with
        | Some("G", _) ->
            floating <- source |> List.filter transfer
            source <- source |> List.filter (transfer >> not)
            detaches <- detaches+1
        | Some _ ->
            floating <- foreign |> List.filter transfer
            foreign <- foreign |> List.filter (transfer >> not)
        | None -> ()
        current <- None
    let arrive edge =
        source <- source @ floating
        floating <- []
        // Restore extra selections, then rebuild the block from present tabs.
        source <- source |> List.map (fun t ->
            if List.contains t.id originalBlock then t
            else original |> List.tryFind (fun o -> o.id=t.id) |> Option.defaultValue t)
        source <- source |> List.sortBy (fun t -> original |> List.findIndex (fun o -> o.id=t.id)) |> normalize
        block <- ownWindowBlock originalBlock (fun id -> source |> List.exists (fun t -> t.id=id))
        source <- source |> List.map (fun t -> if List.contains t.id block then {t with edge=edge} else t) |> normalize
    member _.Move(destination, at) =
        if not ended then
            let own = current |> Option.exists (fun (g,_) -> g="G")
            match ownWindowDragRoute current own (destination |> Option.map (fun e -> "G",e)) with
            | MoveOwn _ -> ()
            | EnterOwn (_,edge) ->
                if not own then leave()
                arrive edge
                current <- Some("G",edge)
            | LeaveOwn -> leave()
            | ContinueDrag -> ()
            | EnterForeign _ -> failwith "Use the foreign input"
            preview <- at
    member this.Pointer(pt) =
        let edge = current |> Option.bind (fun (g,e) -> if g="G" then Some e else None)
        let target = ownWindowEdgeAt 1.0 all home pt edge
        let x,y = pt
        let along = match target with Some e when not (runsHorizontally e) -> y | _ -> x
        this.Move(target,along)
    member _.ForeignAt(edge) =
        let destination = "H",edge
        let sameGroup (g,_) (h,_) = g=h
        if exitBeforeStripChange sameGroup current destination then leave()
        foreign <- foreign @ floating
        floating <- []
        foreign <- foreign |> List.map (fun t -> if transfer t then {t with edge=edge} else t)
        current <- Some destination
    member this.Foreign() = this.ForeignAt(Edge.Top)
    member this.Release(pt) =
        this.Pointer(pt) // Missing final mouse-move is deliberately modeled.
        match current with
        | Some("G",edge) ->
            let lengths = {stripLength=1000.;tabOverlap=0.;pinnedLength=80.;unpinnedLength=100.}
            source <- dropOnStripAt lengths edge block pivot preview source
        | _ -> ()
        ended <- true
    member _.Cancel() =
        if not ended then
            leave()
            source <- source @ floating
            source <- original |> List.choose (fun before ->
                source |> List.tryFind (fun present -> present.id=before.id)
                |> Option.map (fun present -> {present with edge=before.edge; align=before.align; pinned=before.pinned}))
            floating <- []
            block <- []
            current <- None
            ended <- true
    member _.Source = source
    member _.ForeignTabs = foreign
    member _.Floating = floating
    member _.Block = block
    member _.Parks = parks
    member _.Detaches = detaches
    member _.Current = current

let scenarios =
    [ [placed 1 Edge.Top AlignStart false], [1], 1
      [placed 1 Edge.Top AlignStart false;placed 2 Edge.Top AlignStart false], [1;2], 2
      [placed 1 Edge.Top AlignStart true;placed 2 Edge.Top AlignStart true;placed 3 Edge.Top AlignStart false;placed 4 Edge.Top AlignEnd false], [1;2;4], 2
      [placed 1 Edge.Top AlignStart false;placed 2 Edge.Top AlignStart false;placed 3 Edge.Top AlignStart false;placed 4 Edge.Right AlignEnd true], [1;2;4], 1
      [placed 1 Edge.Top AlignStart false;placed 2 Edge.Top AlignStart false;placed 3 Edge.Top AlignStart false], [1;3], 1 ]
let destinations = [Edge.Top,(800,10); Edge.Left,(10,650); Edge.Right,(990,650); Edge.Bottom,(800,790)]
for initial,selected,pivot in scenarios do
    let expectedBlock = contiguous pivot selected (normalize initial)
    for edge,point in destinations do
        for viaForeign in [false;true] do
            let model = DragModel(initial,selected,pivot)
            model.Pointer(10,120)
            equal "band stays attached" 0 model.Detaches
            equal "band block" expectedBlock model.Block
            model.Pointer(500,400)
            equal "center keeps block attached" [] model.Floating
            equal "center never detaches" 0 model.Detaches
            equal "center keeps original block" expectedBlock model.Block
            model.Pointer(1100,400)
            equal "outside detaches once" 1 model.Detaches
            equal "outside floats selected set" (Set.ofList selected) (model.Floating |> List.map (fun t -> t.id) |> Set.ofList)
            if viaForeign then
                model.Foreign()
                let before = model.Parks
                model.ForeignAt(Edge.Left)
                equal "H direct edge move retains membership" (Set.ofList (99::selected)) (model.ForeignTabs |> List.map (fun t -> t.id) |> Set.ofList)
                equal "H direct edge move never detaches" before model.Parks
            model.Move(Some edge,200)
            equal "return restores source membership" (Set.ofList (initial |> List.map (fun t -> t.id))) (model.Source |> List.map (fun t -> t.id) |> Set.ofList)
            equal "return block, without another edge change" expectedBlock model.Block
            equal "foreign pre-existing tabs preserved" [99] (model.ForeignTabs |> List.map (fun t -> t.id))
            model.Release(point)
            let moved = model.Source |> List.filter (fun t -> List.contains t.id expectedBlock)
            equal "release edge" true (moved |> List.forall (fun t -> t.edge=edge))
            equal "release block order" expectedBlock (moved |> List.map (fun t -> t.id))
            equal "block stays adjacent" true
                (let indices = model.Source |> List.indexed |> List.choose (fun (i,t) -> if List.contains t.id expectedBlock then Some i else None)
                 List.last indices - List.head indices + 1 = expectedBlock.Length)
            for before in initial |> List.filter (fun t -> not (List.contains t.id expectedBlock)) do
                equal "extra selections resume original state" before (model.Source |> List.find (fun t -> t.id=before.id))
        for phase in ["band";"center";"outside";"foreign";"return"] do
            for reason in ["Esc";"capture loss"] do
                let model = DragModel(initial,selected,pivot)
                model.Move(Some edge,650)
                if phase<>"band" then model.Pointer(500,400)
                if phase="outside" then model.Pointer(1100,400)
                if phase="foreign" || phase="return" then model.Foreign()
                if phase="return" then model.Move(Some edge,700)
                model.Cancel()
                equal (reason+" exact state/order") (normalize initial) model.Source
                equal (reason+" foreign cleanup") [99] (model.ForeignTabs |> List.map (fun t -> t.id))
                equal (reason+" no parked remainder") [] model.Floating
                model.Cancel()
                equal "cancel idempotent" (normalize initial) model.Source
    let finalOnly = DragModel(initial,selected,pivot)
    finalOnly.Release(10,650)
    equal "release uses final edge without move" true
        (finalOnly.Source |> List.filter (fun t -> List.contains t.id expectedBlock) |> List.forall (fun t -> t.edge=Edge.Left))
// Same-edge release positions must change order/alignment, not just edge.
let ordered = [1;2;3] |> List.map (fun id -> placed id Edge.Top AlignStart false)
let early = DragModel(ordered,[1;2],1)
early.Release(10,10)
equal "early release order" [1;2;3] (early.Source |> List.map (fun t -> t.id))
let late = DragModel(ordered,[1;2],1)
late.Release(900,10)
equal "late release order" [3;1;2] (late.Source |> List.map (fun t -> t.id))
equal "late release alignment" true (late.Source |> List.filter (fun t -> t.id<>3) |> List.forall (fun t -> t.align=AlignEnd))
// Detect the predecessor's before-arrival bug rather than testing strings.
equal "before-arrival reconstruction loses block" [] (ownWindowBlock [1;2] (fun _ -> false))
equal "after-arrival reconstruction retains block" [1;2] (ownWindowBlock [1;2] (fun id -> List.contains id [1;2;4]))
// Source begin is synchronous. Foreign queued begin must keep an incoming block.
equal "queued incoming begin preserves block" false (clearBlockOnBegin all true)
equal "top-only queued begin unchanged" true (clearBlockOnBegin topOnly true)
// Stale replacement scheduling: H returns before G calls back into H.
let queue = System.Collections.Generic.Queue<unit -> unit>()
let mutable callerBlocked = true
let mutable exitRan, resumed = false, false
queue.Enqueue(fun () ->
    equal "caller released before foreign exit" false callerBlocked
    exitRan <- true
    queue.Enqueue(fun () -> resumed <- true))
callerBlocked <- false
while queue.Count>0 do queue.Dequeue()()
equal "stale exits before resuming" (true,true) (exitRan,resumed)
// The production priority decision receives simultaneous native-hit facts.
for current in [None; Some ("G",Edge.Top); Some ("H",Edge.Top)] do
    let own = current |> Option.exists (fun (g,_) -> g="G")
    let mutable homeQueried = false
    let decision = ownWindowPointerRoute current own (Some ("J",Edge.Left)) (fun () -> homeQueried <- true; Some ("G",Edge.Left))
    equal "foreign strip wins over empty home below anchor" (EnterForeign ("J",Edge.Left)) decision
    equal "foreign hit avoids home walk" false homeQueried

// Exercise production geometry over the whole body, including the old center.
for scale in [1.0; 1.5; 2.0; 3.0] do
    for w,h in [1,1; 4,4; 80,60; 200,120; 1600,900; 1920,1040; 800,600; 600,1000; 2000,80; 80,2000; 3840,2160] do
        for ox,oy in [0,0; -2000,-1000] do
            let bounds = ox,oy,w,h
            let distance x y = function
                | Edge.Top -> y | Edge.Left -> x
                | Edge.Right -> w-1-x | Edge.Bottom -> h-1-y
            for allowed in everyAllowed do
                for x in [0; w/4; w/2; 3*w/4; w-1] do
                    for y in [0; h/4; h/2; 3*h/4; h-1] do
                        let expected = edgesOf allowed |> List.minBy (distance x y)
                        equal "nearest pixel distance" (Some expected)
                            (ownWindowEdgeAt scale allowed bounds (ox+x,oy+y) None)
                        for current in priority do
                            let actual = ownWindowEdgeAt scale allowed bounds (ox+x,oy+y) (Some current)
                            equal "body never detaches" true actual.IsSome
                            equal "body chooses allowed edge" true (actual |> Option.exists (isAllowed allowed))
                for point in [ox-1,oy; ox,oy-1; ox+w,oy; ox,oy+h] do
                    equal "outside half-open body" None (ownWindowEdgeAt scale allowed bounds point (Some Edge.Top))
    let hold = int (System.Math.Round(float ownWindowEdgeHysteresis * scale, System.MidpointRounding.AwayFromZero))
    let mutable current = Some Edge.Top
    for x in [100;99;101;100;100-hold] do
        current <- ownWindowEdgeAt scale all home (x,100) current
        equal "diagonal jitter retains top" (Some Edge.Top) current
    current <- ownWindowEdgeAt scale all home (99-hold,100) current
    equal "beyond tolerance switches left" (Some Edge.Left) current
    for x in [99;100;101;100+hold] do
        current <- ownWindowEdgeAt scale all home (x,100) current
        equal "reverse jitter retains left" (Some Edge.Left) current
    equal "reverse beyond tolerance switches top" (Some Edge.Top)
        (ownWindowEdgeAt scale all home (101+hold,100) current)
for w,h in [1600,900;1920,1040;800,600;600,1000;2000,80;80,2000] do
    let mutable vertical = Some Edge.Top
    for y in 0 .. h-1 do
        vertical <- ownWindowEdgeAt 1.0 all (0,0,w,h) (w/2,y) vertical
        equal "vertical center crossing stays attached" true vertical.IsSome
    equal "vertical reaches bottom" (Some Edge.Bottom) vertical
    let mutable horizontal = Some Edge.Left
    for x in 0 .. w-1 do
        horizontal <- ownWindowEdgeAt 1.0 all (0,0,w,h) (x,h/2) horizontal
        equal "horizontal center crossing stays attached" true horizontal.IsSome
    equal "horizontal reaches right" (Some Edge.Right) horizontal
let released = DragModel(ordered,[1;2],1)
released.Release(500,400)
equal "release in center stays attached" [] released.Floating
equal "release in center does not park" 0 released.Parks
let returned = DragModel(ordered,[1;2],1)
returned.Pointer(1100,400)
returned.Pointer(500,400)
equal "return directly to center attaches" [] returned.Floating
equal "center return restores block" [1;2] returned.Block
// Production hover clock: stationary ticks, slop, target changes, cancellation.
let mutable hover = None
let step now target point =
    let next, fire = dragHoverStep now 4 target point hover
    hover <- next
    fire
equal "hover starts without activation" false (step 0L (Some "G-body") (10,10))
equal "passing target never raises" false (step 250L (Some "H-strip") (10,10))
equal "hover delay not elapsed" false (step 549L (Some "H-strip") (10,10))
equal "stationary tick raises once" true (step 550L (Some "H-strip") (10,10))
equal "continued hover does not raise repeatedly" false (step 900L (Some "H-strip") (11,11))
equal "movement resets dwell" false (step 950L (Some "H-strip") (20,20))
equal "new dwell waits" false (step 1249L (Some "H-strip") (20,20))
equal "new dwell fires" true (step 1250L (Some "H-strip") (20,20))
equal "leaving cancels candidate" false (step 1300L None (20,20))
equal "cancellation cleared" None hover
for reason in ["Esc"; "capture loss"; "release"] do
    hover <- None
    step 0L (Some "H-strip") (20,20) |> ignore
    let mutable epoch = 1
    let mutable ended = false
    let mutable front = "title-1"
    let oldEpoch = epoch
    let apply() = if not ended && epoch=oldEpoch then front <- "title-2"
    equal "scheduled hover ready" true (step 300L (Some "H-strip") (20,20))
    ended <- true
    epoch <- epoch+1
    apply()
    equal (reason+" rejects queued raise") "title-1" front
// A visit remembers the old front across H's edges, restores on departure,
// and keeps the previewed front only on an accepted drop.
for commit in [false;true] do
    let mutable front, saved = "title-1", Some "title-1"
    let mutable state = None
    let tick now =
        let next, fire = dragHoverStep now 4 (Some "H-strip") (0,0) state
        state <- next
        if fire then front <- "title-2"
    tick 0L
    tick 299L
    equal "preview not raised early" "title-1" front
    tick 300L
    equal "pivot preview raised" "title-2" front
    equal "H top to left does not exit" false (exitBeforeStripChange (fun (g,_) (h,_) -> g=h) (Some("H",Edge.Top)) ("H",Edge.Left))
    if not commit then front <- saved.Value
    saved <- None
    equal "visit front restoration" (if commit then "title-2" else "title-1") front
    equal "visit snapshot disposed" None saved
equal "restore extra preserves unrelated reorder" [3;4;2;1] (ownWindowRestoreOrder [1;2;3;4] ((=)4) [3;2;4;1])
equal "full cancel restores original order" [1;2;3;4] (ownWindowRestoreOrder [1;2;3;4] (fun _ -> true) [4;3;2;1])
equal "restore ignores closed tabs" [1;3;4] (ownWindowRestoreOrder [1;2;3;4] (fun _ -> true) [4;3;1])
// Destination lifecycle with native Z order represented by a list, front first.
// Unlike the earlier model, snapshots start empty and are taken on entry.
for owner in ["G"; "H"] do
    for selected in [[2]; [2;3;5]] do
        for hoverFirst in [false;true] do
            for outcome in ["drop"; "Esc"; "capture loss"; "leave"] do
                let mutable z = [1;2;3;5]
                let mutable saved = None
                let mutable explicitSelection = Set.empty
                let mutable captured = true
                let mutable previewRaised = false
                let mutable committed = false
                let events = ResizeArray<string>()
                let raise id = z <- id :: (z |> List.filter ((<>) id))
                let enter() = saved <- dragRememberFront saved (List.tryHead z)
                enter()
                equal "G and H save before arrival" (Some 1) saved
                // Body hover has put the destination ABOVE the arriving pivot.
                raise 1
                if hoverFirst then
                    raise 2
                    previewRaised <- true
                enter() // Direct H top -> left must preserve the first snapshot.
                equal "same-group edge movement keeps first front" (Some 1) saved
                let pending = System.Collections.Generic.Queue<unit -> unit>()
                if outcome = "drop" then
                    captured <- false
                    events.Add "release"
                    pending.Enqueue(fun () -> committed <- true; events.Add "drop")
                    pending.Enqueue(fun () ->
                        completeDragDrop
                            (fun pivot ->
                                equal "activation after capture release" false captured
                                equal "activation after placement commit" true committed
                                events.Add "activate"
                                raise pivot
                                explicitSelection <- explicitSelection.Remove pivot)
                            (fun tab ->
                                events.Add "select"
                                if tab <> List.head z then explicitSelection <- explicitSelection.Add tab)
                            2 selected)
                elif outcome <> "leave" then
                    if previewRaised || owner="G" then saved |> Option.iter raise
                    saved <- None
                    captured <- false
                while pending.Count>0 do pending.Dequeue()()
                equal "final front after drop, leave or rollback"
                    (if outcome="drop" || (outcome="leave" && hoverFirst) then 2 else 1) (List.head z)
                if outcome="drop" then
                    equal "implicit front plus explicit selection" (Set.ofList selected) (explicitSelection.Add(List.head z))
                    equal "release/commit/activation order" ["release";"drop";"activate"] (events |> Seq.take 3 |> Seq.toList)
                else equal "cancel never activates" [] (List.ofSeq events)
// Queued hover validity includes movement before its next timer tick, even
// when the current pointer still hits the same large body window.
let valid ended epoch capture button escape point sameHit =
    dragHoverRequestValid ended epoch 7L capture button escape 4 (10,10) point sameHit
for ended,epoch,capture,button,escape,point,sameHit,expected in
    [false,7L,true,true,false,(10,10),true,true
     true,7L,true,true,false,(10,10),true,false
     false,8L,true,true,false,(10,10),true,false
     false,7L,false,true,false,(10,10),true,false
     false,7L,true,false,false,(10,10),true,false
     false,7L,true,true,true,(10,10),true,false
     false,7L,true,true,false,(15,10),true,false
     false,7L,true,true,false,(10,10),false,false] do
    equal "queued hover rejects changed native conditions" expected (valid ended epoch capture button escape point sameHit)
let before,_ = dragHoverStep 0L 4 (Some 1) (10,10) None
let fired,_ = dragHoverStep 300L 4 (Some 1) (10,10) before
let moved,_ = dragHoverStep 301L 4 (Some 1) (20,10) fired
equal "moving invalidates fired epoch before next dwell" true (moved <> fired)
for inStrip,inMargin,foreign,below,own,desktop,outside,expected in
    [true,true,false,true,false,false,false,true
     true,true,false,false,true,false,false,false
     false,true,false,false,false,false,true,true
     false,true,true,false,false,false,true,false
     false,true,false,false,false,false,false,false
     false,true,false,false,true,false,false,true
     false,true,false,false,false,true,false,true
     false,false,false,false,false,true,true,false] do
    equal "strip visibility and taskbar tolerance" expected (dragStripKeeps inStrip inMargin foreign below own desktop outside)
// A replacement stays behind the departing window, below pre-existing cover.
let zBefore = [9;2;8;1]
let replacement = zBefore |> List.filter ((<>) 1) |> List.collect (fun h -> if h=2 then [h;1] else [h])
equal "source replacement preserves external cover" [9;2;1;8] replacement
printfn "OwnWindowDrag: %d checks passed" checks
