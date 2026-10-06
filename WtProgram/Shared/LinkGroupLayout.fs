namespace Bemo

// Pure menu geometry. Doubled centres preserve half-pixel positions.
module LinkGroupLayout =
    type Bounds = { x: int; y: int; width: int; height: int }

    type DisplayPrefix = NoPrefix | OnDisplay of int | OnNoDisplay

    let centre bounds =
        2L * int64 bounds.x + int64 bounds.width,
        2L * int64 bounds.y + int64 bounds.height

    let orderKey bounds stableId =
        let x, y = centre bounds
        x, y, stableId

    // Right and bottom edges are exclusive, as with monitor rectangles.
    let displayIndex bounds (displays: Bounds list) =
        if displays.Length <= 1 then NoPrefix else
        let x, y = centre bounds
        displays |> List.tryFindIndex (fun display ->
            let left, top = 2L * int64 display.x, 2L * int64 display.y
            let right = left + 2L * int64 display.width
            let bottom = top + 2L * int64 display.height
            x >= left && x < right && y >= top && y < bottom)
        |> function
           | Some index -> OnDisplay index
           | None -> OnNoDisplay
