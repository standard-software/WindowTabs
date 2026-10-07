namespace Bemo

open System

// Physical screen coordinates throughout. Only distance differences are scaled
// to logical pixels, using the live monitor; never scale virtual-screen origins.
module RestorePosition =
    let minimumGap = 32.0
    type Observation = { center: (float * float) option; scale: float; desktop: Guid option }
    type Candidate = { index: int; center: (float * float) option; desktop: Guid option }
    type Decision =
        | Selected of index: int * distance: float * gap: float option * byDesktop: bool
        | Ordered of index: int * distance: float option * gap: float option
        | Held of reason: string

    let desktop value =
        value |> Option.bind (fun s ->
            match Guid.TryParse(s) with
            | true, id when id <> Guid.Empty -> Some id
            | _ -> None)

    let center rect =
        rect |> Option.bind (fun (x, y, width, height) ->
            if width <= 0 || height <= 0 || x <= -30000 || y <= -30000 then None
            else Some(float x + float width / 2.0, float y + float height / 2.0))

    let private validPoint = function
        | Some(x,y) -> not (Double.IsNaN x || Double.IsInfinity x || Double.IsNaN y || Double.IsInfinity y)
        | None -> false

    // Candidate list order is saved group order, then saved tab order. The
    // index is an opaque lookup key and need not have that order (cache reversal).
    let choose (live: Observation) (candidates: Candidate list) =
        let reliable = validPoint live.center && live.scale > 0.0 &&
                       not (Double.IsNaN live.scale || Double.IsInfinity live.scale)
        let distance c =
            if reliable && validPoint c.center then
                let lx,ly = live.center.Value
                let x,y = c.center.Value
                Some(sqrt ((x-lx)*(x-lx) + (y-ly)*(y-ly)) / live.scale)
            else None
        let ranked = candidates |> List.mapi (fun order c -> c,order,distance c)
                     |> List.sortBy (fun (c,order,d) -> not (validPoint c.center), Option.defaultValue infinity d, order)
        match ranked with
        | [] -> Held "no-candidates"
        | (first,_,d)::rest ->
            let gap =
                match d,rest with
                | Some near,(_,_,Some next)::_ -> Some(next-near)
                | _ -> None
            let allKnown = reliable && candidates |> List.forall (fun c -> validPoint c.center)
            if allKnown && (rest.IsEmpty || gap |> Option.exists (fun g -> g >= minimumGap)) then
                Selected(first.index,d.Value,gap,false)
            else
                // Near distances form a tie band. Prefer a matching desktop
                // inside that band, then distance and original saved order.
                // Without live geometry, use known rectangles, desktop, order.
                let pool =
                    match d with
                    | Some near -> ranked |> List.filter (fun (_,_,value) -> value |> Option.exists (fun x -> x-near < minimumGap))
                    | None -> ranked |> List.filter (fun (c,_,_) -> validPoint c.center = validPoint first.center)
                let selected,_,value = pool |> List.minBy (fun (c,order,value) ->
                    not (live.desktop.IsSome && c.desktop=live.desktop), Option.defaultValue infinity value, order)
                Ordered(selected.index,value,gap)
