namespace Bemo

// Pure snap geometry shared by the tab menu's "Detach and snap" items
// (TabStripDecorator) and the snap performed when a tab is dragged out of a
// strip to form a new group (Desktop.dragDrop). No Win32, no settings, so the
// rules can be checked without starting WindowTabs (Tools\SnapGeometry.Tests.fsx).
//
// Rectangles are (x, y, width, height) tuples in device pixels, the same
// values System.Drawing.Rectangle carries at the call sites.
module SnapGeometry =
    type PlacementSource =
        | RestoredPlacement
        | GroupScreenMove
        | GroupKeepSizeSnap of topOnly: bool

    // Preserve the execution path's choice of pre-restore or restored geometry.
    let placementSourceBounds source before restored =
        match source with
        | GroupScreenMove | GroupKeepSizeSnap true -> before
        | RestoredPlacement | GroupKeepSizeSnap false -> restored

    let menuPlacementSource detached otherScreen snap percentage topOnly =
        if detached then RestoredPlacement
        elif snap then
            if percentage then RestoredPlacement else GroupKeepSizeSnap topOnly
        elif otherScreen then GroupScreenMove
        else RestoredPlacement

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

    // Align uniformly placed tabs to the horizontal component of a snap or move.
    // Corners use their left/right component. Vertical-only moves, centered or
    // maximized snaps, mixed/empty groups and a disabled setting leave tabs alone.
    let realignment (enabled: bool) (snapDirection: string) (alignments: 'a list) (left: 'a) (right: 'a) : 'a option =
        if not enabled || List.isEmpty alignments then None
        else
            let desired =
                match snapDirection with
                | "snapleft" | "snaptopleft" | "snapbottomleft"
                | "left" | "topleft" | "bottomleft" -> Some left
                | "snapright" | "snaptopright" | "snapbottomright"
                | "right" | "topright" | "bottomright" -> Some right
                | _ -> None
            match desired with
            | Some _ when alignments |> List.forall ((=) left) || alignments |> List.forall ((=) right) -> desired
            | _ -> None

    // Presets and placement use physical pixels, independently of display DPI.
    let sizePresets = [
        "640x400", 640, 400
        "VGA 640x480", 640, 480
        "SVGA 800x600", 800, 600
        "XGA 1024x768", 1024, 768
        "HD 1280x720", 1280, 720
        "WXGA 1280x800", 1280, 800
        "SXGA 1280x1024", 1280, 1024
        "1366x768", 1366, 768
        "WXGA+ 1440x900", 1440, 900
        "HD+ 1600x900", 1600, 900
        "WSXGA+ 1680x1050", 1680, 1050
        "Full HD 1920x1080", 1920, 1080
        "WUXGA 1920x1200", 1920, 1200
        "WQHD 2560x1440", 2560, 1440
        "WQXGA 2560x1600", 2560, 1600
        "4K UHD 3840x2160", 3840, 2160
    ]

    let sizeBounds (areaX, areaY, areaWidth, areaHeight) (x, y) (width, height) =
        if width <= 0 || height <= 0 || width > areaWidth || height > areaHeight then None
        else
            Some(max areaX (min x (areaX + areaWidth - width)),
                 max areaY (min y (areaY + areaHeight - height)), width, height)

    // Map position between unreserved work areas, as screen moves do.
    // The requested size remains in physical pixels and is clamped separately.
    let sizePosition sameScreen (sourceX, sourceY, sourceWidth, sourceHeight)
                     (targetX, targetY, targetWidth, targetHeight) (x, y) =
        if sameScreen then x, y
        else
            let leftPercent = float (x - sourceX) / float sourceWidth
            let topPercent = float (y - sourceY) / float sourceHeight
            targetX + int (float targetWidth * leftPercent),
            targetY + int (float targetHeight * topPercent)

    // Only size menus use destination DPI; existing snap margins are unchanged.
    let sizeWorkArea sameScreen currentScale targetScale tabHeightAt enabled area =
        let scale = if sameScreen then currentScale else targetScale
        let margin = if enabled then tabHeightAt scale - 1 else 0
        reserveTabHeight margin area

    // The pointer uses the full work area; reserved tab margins affect only
    // the destination rectangle. Shared by the drag guide and final drop.
    let dropBounds workArea reservedArea point =
        calculateSnapBoundsWithPercent (directionForPoint workArea point) 50 reservedArea

    // Drawing-only inset: shared boundaries inside the work area stay aligned.
    let previewInset gap (workX, workY, workWidth, workHeight) (x, y, width, height) =
        let gap = max 0 gap
        let left = if x = workX then gap else 0
        let top = if y = workY then gap else 0
        let right = if int64 x + int64 width = int64 workX + int64 workWidth then gap else 0
        let bottom = if int64 y + int64 height = int64 workY + int64 workHeight then gap else 0
        if width <= left + right || height <= top + bottom then x, y, width, height
        else x + left, y + top, width - left - right, height - top - bottom

    // One progress value moves all four edges together.
    let previewEase progress =
        let u = max 0.0 (min 1.0 progress)
        1.0 - (1.0 - u) ** 3.0

    let previewInterpolate (x, y, width, height) (tx, ty, tw, th) progress =
        let p = previewEase progress
        let lerp a b = int (System.Math.Round(float a + (float b - float a) * p))
        let left, top = lerp x tx, lerp y ty
        let right, bottom = lerp (x + width) (tx + tw), lerp (y + height) (ty + th)
        left, top, max 1 (right - left), max 1 (bottom - top)

    let previewCentered scale (x, y, width, height) =
        let w, h = max 1 (int (float width * scale)), max 1 (int (float height * scale))
        x + (width - w) / 2, y + (height - h) / 2, w, h
