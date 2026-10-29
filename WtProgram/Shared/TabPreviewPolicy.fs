namespace Bemo

module TabPreviewPolicy =
    let eligible enabled hoverActivate active dragging visible =
        enabled && not hoverActivate && not active && not dragging && visible

    let delay warm milliseconds =
        if warm then 0 else max 1 (min 5000 milliseconds)

    let fit width height edge availableWidth availableHeight =
        if width <= 0 || height <= 0 then 0, 0
        else
            let ratio = min (float edge / float (max width height))
                            (min (float (max 1 availableWidth) / float width) (float (max 1 availableHeight) / float height))
            max 1 (int (float width * ratio)), max 1 (int (float height * ratio))

    let place left top right bottom width height x y =
        max left (min x (right - width)), max top (min y (bottom - height))

    // Try the preferred side, then the opposite side after work-area clamping.
    // No placement is safer than covering the tooltip when neither side fits.
    let besideTooltip edge (left, top, right, bottom) (tipLeft, tipTop, tipRight, tipBottom) gap width height =
        let candidate below =
            let y = if below then tipBottom + gap else tipTop - gap - height
            let x, y = place left top right bottom width height tipLeft y
            let inside = width > 0 && height > 0 && x >= left && y >= top && x + width <= right && y + height <= bottom
            let separate = x + width <= tipLeft || x >= tipRight || y + height <= tipTop || y >= tipBottom
            if inside && separate then Some(x, y) else None
        let below = edge <> TabEdges.Edge.Bottom
        match candidate below with
        | Some position -> Some position
        | None -> candidate (not below)
