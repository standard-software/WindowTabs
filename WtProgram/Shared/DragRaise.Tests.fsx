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
let pivot, other, selected = 10, 11, 12
// The body under the pointer and the group's old front are deliberately not
// inputs to the production hover selection. Only the carried drag block is.
for top in [pivot;other;selected] do
    for pointed in [pivot;other;selected] do
        equal "hover never substitutes current top or pointed member" [pivot;selected]
            (dragHoverWindows pivot [selected;pivot;selected] (fun _ -> true))
        equal "departed selection is excluded" [pivot]
            (dragHoverWindows pivot [selected] ((=) pivot))
for bands in [[1];[1;2];[1;2;3;4]] do
    for bodies in [[pivot];[pivot;selected];[pivot;other;selected]] do
        for anchor in [0;99] do
            let order = dragStackOrder anchor bands bodies
            equal "one plan contains insertion anchor, bands and bodies" (anchor :: bands @ bodies) order
            equal "every band precedes every body" true
                (bands |> List.forall (fun band -> bodies |> List.forall (fun body ->
                    List.findIndex ((=) band) order < List.findIndex ((=) body) order)))
            equal "preview retains pivot before previous destination front" pivot order.[bands.Length+1]
            equal "no duplicated native targets" (List.distinct order) order
            // Model the single ordered batch against an overlapping source.
            let original = [99;other;4;3;2;1;pivot;selected]
            let moving = List.tail order
            let remaining = original |> List.filter (fun h -> not (List.contains h moving))
            let index = if anchor=0 then 0 else 1 + List.findIndex ((=) anchor) remaining
            let result = List.take index remaining @ moving @ List.skip index remaining
            equal "destination bands and body stay together" moving (result |> List.skip index |> List.take moving.Length)
equal "deduplicate band and body inputs" [0;1;2;pivot]
    (dragStackOrder 0 [1;2;1] [pivot;pivot])
equal "do not move anchor behind itself" [99;1;pivot]
    (dragStackOrder 99 [1] [99;pivot])
// A queued source hover must fail when a foreign receiver has taken over,
// even if the cursor has not moved and the native visible hit is unchanged.
equal "source hover valid only while source is receiver" false
    (dragHoverRequestValid false 1L 1L true true false 4 (0,0) (0,0) false)
equal "source hover retains existing capture checks" false
    (dragHoverRequestValid false 1L 1L false true false 4 (0,0) (0,0) true)
printfn "DragRaise: %d checks passed" checks
