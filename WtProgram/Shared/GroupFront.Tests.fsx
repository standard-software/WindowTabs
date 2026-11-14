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
    if actual <> expected then failwithf "%s: expected %A, got %A" label expected actual
// Symbolic windows: a source front, a dragged pivot and a destination front.
let source, pivot, destination = 1, 2, 3
let original = [source;pivot;destination;4]
let raiseOrder = dragGroupFrontOrder pivot destination
let preview = raiseOrder @ (original |> List.filter (fun h -> not (List.contains h raiseOrder)))
equal "destination preview leads source" [pivot;destination;source;4] preview
equal "one window is not positioned twice" [pivot] (dragGroupFrontOrder pivot pivot)
// The caller saves nearest-first predecessors excluding the departing pivot.
let predecessors = [source]
let anchor = dragGroupRestoreAnchor (fun _ -> true) predecessors 0
equal "cancel restores prior slot" source anchor
let remaining = preview |> List.filter (fun h -> h <> pivot && h <> destination)
let index = 1 + List.findIndex ((=) anchor) remaining
let restored = List.take index remaining @ [destination] @ List.skip index remaining
equal "destination returns behind source" [source;destination;4] restored
equal "closed predecessor falls back to next live one" 5
    (dragGroupRestoreAnchor ((<>) source) [source;5;6] 0)
equal "no live predecessor uses top" 0 (dragGroupRestoreAnchor (fun _ -> false) [source] 0)
equal "no predecessor uses top" 0 (dragGroupRestoreAnchor (fun _ -> true) [] 0)
let allowed = allowedOf priority |> Option.get
for edge in priority do
    let point = match edge with Edge.Top -> 50,0 | Edge.Left -> 0,50 | Edge.Right -> 99,50 | Edge.Bottom -> 50,99
    for scale in [1.;1.5;2.] do
        let hit = ownWindowEdgeAt scale allowed (0,0,100,100) point None
        equal "body chooses real edge receiver" (Some edge) hit
        let target = hit |> Option.map rank
        let mutable sourceRead = false
        let ownHit() = sourceRead <- true; None
        equal "body receiver enters immediately" (EnterForeign(rank edge))
            (ownWindowPointerRoute (Some 9) true target ownHit)
        equal "foreign body wins without querying source" false sourceRead
        equal "body remains in receiver" ContinueDrag
            (ownWindowPointerRoute target false target ownHit)
        equal "same destination edge switch does not detach" false
            (exitBeforeStripChange (fun _ _ -> true) (Some 9) (rank edge))
        equal "different destination exits old receiver" true
            (exitBeforeStripChange (fun _ _ -> false) (Some 9) (rank edge))
equal "outside destination body is not accepted" None
    (ownWindowEdgeAt 1. allowed (0,0,100,100) (100,50) None)
equal "source route stays unchanged" (MoveOwn 9)
    (ownWindowPointerRoute (Some 9) true None (fun () -> Some 9))
// Commit remains activation then selection, after capture is released.
let events = ResizeArray<_>()
completeDragDrop (fun h -> events.Add("activate",h)) (fun h -> events.Add("select",h)) pivot [4;5]
equal "commit keeps pivot active" [("activate",pivot);("select",4);("select",5)] (List.ofSeq events)
// Exercise the production journal against symbolic native Z-order changes.
// Each leave keeps the receiver; cancel unwinds visits, including repeats.
for visits in [[3];[3;4];[3;4;3];[4;3;4;3]] do
    for cancel in [false;true] do
        let initial = [1;3;4;5]
        let mutable order = initial
        let mutable history = []
        for receiver in visits do
            let before = order |> List.takeWhile ((<>) receiver) |> List.rev
            let restore() =
                let after = dragGroupRestoreAnchor (fun h -> List.contains h order) before 0
                let rest = order |> List.filter ((<>) receiver)
                let index = if after = 0 then 0 else 1 + List.findIndex ((=) after) rest
                order <- List.take index rest @ [receiver] @ List.skip index rest
            history <- dragRecordRestore restore history
            order <- receiver :: (order |> List.filter ((<>) receiver))
            // Leaving for desktop, another screen/group or source does not undo.
            equal "leave keeps receiver above source" receiver (List.head order)
        let kept = order
        if cancel then history |> List.iter (fun undo -> undo())
        equal "cancel outside restores all visits; commit keeps raises"
            (if cancel then initial else kept) order
let mutable undoOrder = []
let history = ["B";"D";"E";"B"] |> List.fold (fun saved name ->
    dragRecordRestore (fun () -> undoOrder <- undoOrder @ [name]) saved) []
history |> List.iter (fun undo -> undo())
equal "reverse visit order includes repeated receiver" ["B";"E";"D";"B"] undoOrder
printfn "GroupFront: %d checks passed" checks
