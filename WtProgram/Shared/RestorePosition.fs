namespace Bemo

open System

// Physical screen coordinates throughout, including normal placement rectangles.
module RestorePosition =
    type Observation = { center: (float * float) option; scale: float; desktop: Guid option }
    type Candidate = { index: int; center: (float * float) option; desktop: Guid option }
    type Decision =
        | Selected of index: int * distance: float * gap: float option * byDesktop: bool
        | Ordered of index: int * distance: float option * gap: float option
        | Held of reason: string

    let desktop (value: string option) =
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

    type Pair = { savedIndex: int; liveIndex: int; distance: float option; candidates: int }

    // Physical centre distance. Unknown geometry follows every known pair.
    // Ties use saved list order, then live list order. Indices are list offsets.
    let assign (saved: (float * float) option list) (live: (float * float) option list) =
        let distance a b =
            if validPoint a && validPoint b then
                let ax,ay = a.Value
                let bx,by = b.Value
                Some(sqrt ((ax-bx)*(ax-bx) + (ay-by)*(ay-by)))
            else None
        let pairs =
            [ for si,a in List.indexed saved do
                for wi,b in List.indexed live do
                    yield si,wi,distance a b ]
            |> List.sortBy (fun (si,wi,d) -> Option.defaultValue infinity d,si,wi)
        let usedSaved = Collections.Generic.HashSet<int>()
        let usedLive = Collections.Generic.HashSet<int>()
        [ for si,wi,d in pairs do
            if not (usedSaved.Contains si || usedLive.Contains wi) then
                let candidates = saved.Length - usedSaved.Count
                usedSaved.Add si |> ignore
                usedLive.Add wi |> ignore
                yield {savedIndex=si;liveIndex=wi;distance=d;candidates=candidates} ]

    let choose (live: Observation) (candidates: Candidate list) =
        match assign (candidates |> List.map (fun c -> c.center)) [live.center] with
        | pair::_ ->
            let index = candidates.[pair.savedIndex].index
            match pair.distance with
            | Some d -> Selected(index,d,None,false)
            | None -> Ordered(index,None,None)
        | [] -> Held "no-candidates"
