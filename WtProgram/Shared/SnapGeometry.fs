namespace Bemo

// Pure snap geometry shared by the tab menu's "Detach and snap" items
// (TabStripDecorator) and the snap performed when a tab is dragged out of a
// strip to form a new group (Desktop.dragDrop). No Win32, no settings, so the
// rules can be checked without starting WindowTabs (Tools\SnapGeometry.Tests.fsx).
//
// Rectangles are (x, y, width, height) tuples in device pixels, the same
// values System.Drawing.Rectangle carries at the call sites.
module SnapGeometry =
    [<Literal>]
    let snapLeft = "snapleft"

    [<Literal>]
    let snapRight = "snapright"

    [<Literal>]
    let snapTop = "snaptop"

    [<Literal>]
    let snapBottom = "snapbottom"

    // Which of the four triangles cut by the work area's two diagonals contains
    // the point: the right triangle snaps right, the left one left, the top one
    // top and the bottom one bottom.
    //
    // Use the integer mouse point itself, without a half-pixel offset.
    // On either diagonal left/right wins; the intersection snaps right.
    // Points on a taskbar or in a monitor gap use the extended diagonals.
    // Widen before subtraction. Decimal integers also avoid overflow in the
    // products for any Int32 point and rectangle dimensions.
    let directionForPoint (areaX: int, areaY: int, areaWidth: int, areaHeight: int) (x: int, y: int) =
        let w = decimal areaWidth
        let h = decimal areaHeight
        let px = decimal x - decimal areaX
        let py = decimal y - decimal areaY
        let d1 = px * h - py * w
        let d2 = px * h + py * w - w * h
        if d1 >= 0M && d2 >= 0M then snapRight
        elif d1 <= 0M && d2 <= 0M then snapLeft
        elif d1 > 0M then snapTop
        else snapBottom

    // The work area left after reserving room for the tab strip above a
    // snapped window ("Add tab height margin when snapping"). tabHeight <= 0
    // reserves nothing.
    let reserveTabHeight (tabHeight: int) (areaX: int, areaY: int, areaWidth: int, areaHeight: int) =
        if tabHeight > 0 then (areaX, areaY + tabHeight, areaWidth, areaHeight - tabHeight)
        else (areaX, areaY, areaWidth, areaHeight)

    // Returns (x, y, width, height) for a snap inside an (already reserved)
    // work area.
    // Snap right/left: maintain width, expand height to full
    // Snap top/bottom: maintain height, expand width to full
    let snapBounds (snapDirection: string) (areaX: int, areaY: int, areaWidth: int, areaHeight: int) (currentWidth: int) (currentHeight: int) =
        let areaRight = areaX + areaWidth
        let areaBottom = areaY + areaHeight
        let clampedWidth = min currentWidth areaWidth
        let clampedHeight = min currentHeight areaHeight
        match snapDirection with
        | "snapright" -> (areaRight - clampedWidth, areaY, clampedWidth, areaHeight)
        | "snapleft" -> (areaX, areaY, clampedWidth, areaHeight)
        | "snaptop" -> (areaX, areaY, areaWidth, clampedHeight)
        | "snapbottom" -> (areaX, areaBottom - clampedHeight, areaWidth, clampedHeight)
        | _ -> (areaX, areaY, clampedWidth, clampedHeight)

    // Percentage geometry extracted from the tab menu. The caller reserves
    // the tab margin and resolves the virtual desktop before calling.
    let calculateSnapBoundsWithPercent (snapDirection: string) (percent: int) (areaX: int, areaY: int, areaWidth: int, areaHeight: int) =
        let areaRight = areaX + areaWidth
        let areaBottom = areaY + areaHeight
        let percentFloat = float(percent) / 100.0
        match snapDirection with
        | "snapright" ->
            let newWidth = int(float(areaWidth) * percentFloat)
            let newHeight = areaHeight
            let x = areaRight - newWidth
            let y = areaY
            (x, y, newWidth, newHeight)
        | "snapleft" ->
            let newWidth = int(float(areaWidth) * percentFloat)
            let newHeight = areaHeight
            let x = areaX
            let y = areaY
            (x, y, newWidth, newHeight)
        | "snaptop" ->
            let newWidth = areaWidth
            let newHeight = int(float(areaHeight) * percentFloat)
            let x = areaX
            let y = areaY
            (x, y, newWidth, newHeight)
        | "snapbottom" ->
            let newWidth = areaWidth
            let newHeight = int(float(areaHeight) * percentFloat)
            let x = areaX
            let y = areaBottom - newHeight
            (x, y, newWidth, newHeight)
        | "snaptopleft" ->
            let newWidth = int(float(areaWidth) * percentFloat)
            let newHeight = int(float(areaHeight) * percentFloat)
            let x = areaX
            let y = areaY
            (x, y, newWidth, newHeight)
        | "snaptopright" ->
            let newWidth = int(float(areaWidth) * percentFloat)
            let newHeight = int(float(areaHeight) * percentFloat)
            let x = areaRight - newWidth
            let y = areaY
            (x, y, newWidth, newHeight)
        | "snapbottomleft" ->
            let newWidth = int(float(areaWidth) * percentFloat)
            let newHeight = int(float(areaHeight) * percentFloat)
            let x = areaX
            let y = areaBottom - newHeight
            (x, y, newWidth, newHeight)
        | "snapbottomright" ->
            let newWidth = int(float(areaWidth) * percentFloat)
            let newHeight = int(float(areaHeight) * percentFloat)
            let x = areaRight - newWidth
            let y = areaBottom - newHeight
            (x, y, newWidth, newHeight)
        | "snapcenter" ->
            let newWidth = int(float(areaWidth) * percentFloat)
            let newHeight = int(float(areaHeight) * percentFloat)
            let x = areaX + (areaWidth - newWidth) / 2
            let y = areaY + (areaHeight - newHeight) / 2
            (x, y, newWidth, newHeight)
        | "snapcenterhorizontal" ->
            let newWidth = int(float(areaWidth) * percentFloat)
            let newHeight = areaHeight
            let x = areaX + (areaWidth - newWidth) / 2
            let y = areaY
            (x, y, newWidth, newHeight)
        | "snapcentervertical" ->
            let newWidth = areaWidth
            let newHeight = int(float(areaHeight) * percentFloat)
            let x = areaX
            let y = areaY + (areaHeight - newHeight) / 2
            (x, y, newWidth, newHeight)
        | "snapmaximizedisplay" | "snapmaximizedesktop" ->
            (areaX, areaY, areaWidth, areaHeight)
        | _ ->
            (areaX, areaY, areaWidth, areaHeight)

    // The alignment a left/right snap gives the snapped tabs ("Change tab
    // position on left/right snap"): all left for a left snap, all right for a
    // right snap, but only when the tabs are uniformly aligned (all left or all
    // right) beforehand. A mixed set, a top/bottom snap, an empty set or the
    // setting being off leaves the alignment alone (None).
    let realignment (enabled: bool) (snapDirection: string) (alignments: 'a list) (left: 'a) (right: 'a) : 'a option =
        if not enabled || List.isEmpty alignments then None
        else
            let desired =
                match snapDirection with
                | "snapleft" -> Some left
                | "snapright" -> Some right
                | _ -> None
            match desired with
            | Some _ when alignments |> List.forall ((=) left) || alignments |> List.forall ((=) right) -> desired
            | _ -> None
