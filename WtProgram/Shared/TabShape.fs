namespace Bemo
open System
open System.Drawing
open System.Drawing.Drawing2D

// Tab outline shapes, as pure data.
//
// TabSprite used to build its GraphicsPath inline; the geometry now lives
// here so it can be checked numerically without a window (see
// Tools/TabShape.Tests.fsx). The sprite only turns the segments into a path.
//
// Coordinates follow TabSprite.borderPath: x runs 0 .. width, `bottom` is the
// row the tab stands on (one row outside the bitmap) and `top` is the far edge
// of the tab. For tabs that point down `bottom` is above `top`; nothing here
// assumes a sign, so every shape works in both directions.
//
// Every shape uses the same tab layout: the same width, the same Tab Overlap
// with the neighbours, and the content (icon, text, close button) at the same
// place. Only the outline changes. At each end of a tab there is an
// `edgeWidth` band that the neighbour overlaps into:
//  * Band shapes (scurve, strong, slant, angular) climb from the base to the top
//    across that band, so neighbouring outlines interleave.
//  * Box shapes (square, rounded) stand a vertical side in the middle of the
//    band, where a band shape passes at half height (boxInset). With the
//    default overlap, neighbouring boxes meet on a single line; with a small
//    or negative overlap they stand apart, as the band shapes do.
//  * Cut trapezoids combine a diagonal side with one inset vertical side.
// `strong` and `angular` stand their middle upright on that same column.
module TabShape =
    // Setting values (TabAppearanceInfo.tabShape).
    [<Literal>]
    let scurve = "S-curve"      // the original S-curve (default)
    [<Literal>]
    let strong = "Strong rounded vertical line"
    [<Literal>]
    let slant = "Trapezoid"
    [<Literal>]
    let rounded = "Rounded vertical line"
    [<Literal>]
    let angular = "Angular vertical line"
    [<Literal>]
    let cutLeft = "Left-cut trapezoid"
    [<Literal>]
    let cutRight = "Right-cut trapezoid"
    [<Literal>]
    let rectangle = "Rectangle"
    [<Literal>]
    let roundedRectangle = "Rounded rectangle"
    [<Literal>]
    let strongRoundedRectangle = "Strong rounded rectangle"
    [<Literal>]
    let angularRectangle = "Angular rectangle"

    // Order shown in the settings dialog.
    let all = [ scurve; rounded; strong; angular; slant; cutRight; cutLeft;
                rectangle; roundedRectangle; strongRoundedRectangle; angularRectangle ]

    // Unknown, empty or missing values fall back to the original look.
    // Surrounding blanks and letter case are forgiven, since the value can be
    // edited by hand in the settings file.
    let normalize (value: string) =
        if isNull value then scurve
        else
            let value = value.Trim()
            match all |> List.tryFind (fun name -> String.Equals(name, value, StringComparison.OrdinalIgnoreCase)) with
            | Some name -> name
            | None -> scurve

    let isBox (shape: string) =
        let s = normalize shape
        s = rounded || s = rectangle || s = roundedRectangle || s = strongRoundedRectangle || s = angularRectangle

    let needsOutlineHit (shape: string) =
        let s = normalize shape
        s <> scurve && s <> strong

    /// Distance from the tab's outer edge to the side of the box shape: half
    /// the edge band, in whole pixels so the side is one crisp column. The
    /// hit test trims the ends of a group by the same amount
    /// (TabStripSprite.tryHitForTooltip).
    let boxInset (edgeWidth: int) = edgeWidth / 2

    type Segment =
        | Line of PointF * PointF
        | Bezier of PointF * PointF * PointF * PointF

    // Distance of a Bezier control point from the end of a quarter ellipse
    // (4/3 * (sqrt 2 - 1)), the usual cubic approximation of a circular arc.
    let private kappa = float32(0.5522847498)

    // The original edge, reproduced point for point. Note that the second
    // Bezier starts at bezPoints.[2], not at the end of the first one, so
    // GraphicsPath inserts a short connecting line back to it. That is part
    // of the shape users have always seen, so it is kept exactly.
    let private sCurveEdge (startPoint: PointF) (endPoint: PointF) =
        let width = endPoint.X - startPoint.X
        let height = endPoint.Y - startPoint.Y
        let xInc = width / float32(3)
        let xCurveInc = xInc / float32(3)
        let yCurveInc = height / float32(3)
        let bezPoints =
            [|
                startPoint
                PointF(startPoint.X + xInc, startPoint.Y)
                PointF(startPoint.X + xInc + xCurveInc, startPoint.Y + yCurveInc)
                PointF(startPoint.X + xInc + float32(2) * xCurveInc, startPoint.Y + float32(2) * yCurveInc)
                PointF(startPoint.X + float32(2) * xInc, startPoint.Y + float32(3) * yCurveInc)
                PointF(startPoint.X + float32(3) * xInc, startPoint.Y + float32(3) * yCurveInc)
            |]
        [
            Bezier(bezPoints.[0], bezPoints.[1], bezPoints.[2], bezPoints.[3])
            Bezier(bezPoints.[2], bezPoints.[3], bezPoints.[4], bezPoints.[5])
        ]

    // The whole outline reflected left to right about the tab's middle: the
    // right edge of a band shape is its left edge drawn backwards.
    let private mirrored (width: float32) (segments: Segment list) =
        let m (p: PointF) = PointF(width - p.X, p.Y)
        segments
        |> List.rev
        |> List.map (function
            | Line(a, b) -> Line(m b, m a)
            | Bezier(a, b, c, d) -> Bezier(m d, m c, m b, m a))

    // strong's left edge across a band `edgeWidth` wide: a quarter-round foot
    // that leaves the base, an upright middle, and a quarter-round shoulder
    // onto the top. The middle stands on the column where the box shape
    // stands its side (boxInset), in whole pixels so it is one crisp column
    // at every scale. Where the original S leans through its middle third,
    // this one stands up, so the S reads clearly even at 100%.
    let private strongLeftEdge (bottom: float32) (top: float32) (edgeWidth: float32) =
        let middle = float32(boxInset (int edgeWidth))
        let dir = float32(Math.Sign(top - bottom))
        let halfHeight = abs (top - bottom) / float32(2)
        // Each turn is a quarter round as wide as its side of the middle
        // (9 px either side at 100%), never more than half the height.
        let footX, footY = middle, dir * min middle halfHeight
        let shoulderX, shoulderY = edgeWidth - middle, dir * min (edgeWidth - middle) halfHeight
        [
            Bezier(PointF(float32(0), bottom),
                   PointF(footX * kappa, bottom),
                   PointF(middle, bottom + footY * (float32(1) - kappa)),
                   PointF(middle, bottom + footY))
            Line(PointF(middle, bottom + footY), PointF(middle, top - shoulderY))
            Bezier(PointF(middle, top - shoulderY),
                   PointF(middle, top - shoulderY * (float32(1) - kappa)),
                   PointF(edgeWidth - shoulderX * kappa, top),
                   PointF(edgeWidth, top))
        ]

    // Small rounded shoulders and outward feet, turning like the strong S.
    let private roundedOutline (width: float32) (bottom: float32) (top: float32) (edgeWidth: float32) =
        let left = max 0.0f (min (float32(boxInset (int edgeWidth))) (floor ((width - 1.0f) / 2.0f)))
        let right = max left (width - 1.0f - left)
        let radius = min (edgeWidth / 4.0f) (min ((right - left) / 2.0f) (abs (top - bottom) / 2.0f))
        let dy = float32(Math.Sign(bottom - top)) * radius
        [ Bezier(PointF(left - radius, bottom), PointF(left - radius * (1.0f - kappa), bottom),
                 PointF(left, bottom - dy * (1.0f - kappa)), PointF(left, bottom - dy))
          Line(PointF(left, bottom - dy), PointF(left, top + dy))
          Bezier(PointF(left, top + dy), PointF(left, top + dy * (1.0f - kappa)),
                 PointF(left + radius * (1.0f - kappa), top), PointF(left + radius, top))
          Line(PointF(left + radius, top), PointF(right - radius, top))
          Bezier(PointF(right - radius, top), PointF(right - radius * (1.0f - kappa), top),
                 PointF(right, top + dy * (1.0f - kappa)), PointF(right, top + dy))
          Line(PointF(right, top + dy), PointF(right, bottom - dy))
          Bezier(PointF(right, bottom - dy), PointF(right, bottom - dy * (1.0f - kappa)),
                 PointF(right + radius * (1.0f - kappa), bottom), PointF(right + radius, bottom)) ]

    // C-shaped corners turn inward at both ends, unlike the outward S feet.
    let private rectangleOutline (width: float32) (bottom: float32) (top: float32) (edgeWidth: float32) radiusFactor beveled =
        let left = max 0.0f (min (float32(boxInset (int edgeWidth))) (floor ((width - 1.0f) / 2.0f)))
        let right = max left (width - 1.0f - left)
        let radius = min (edgeWidth * radiusFactor) (min ((right - left) / 2.0f) (abs (top - bottom) / 2.0f))
        let dy = float32(Math.Sign(bottom - top)) * radius
        let corner a b c d = if beveled || radius = 0.0f then Line(a,d) else Bezier(a,b,c,d)
        [ corner (PointF(left + radius,bottom)) (PointF(left + radius*(1.0f-kappa),bottom))
                 (PointF(left,bottom - dy*(1.0f-kappa))) (PointF(left,bottom-dy))
          Line(PointF(left,bottom-dy),PointF(left,top+dy))
          corner (PointF(left,top+dy)) (PointF(left,top+dy*(1.0f-kappa)))
                 (PointF(left+radius*(1.0f-kappa),top)) (PointF(left+radius,top))
          Line(PointF(left+radius,top),PointF(right-radius,top))
          corner (PointF(right-radius,top)) (PointF(right-radius*(1.0f-kappa),top))
                 (PointF(right,top+dy*(1.0f-kappa))) (PointF(right,top+dy))
          Line(PointF(right,top+dy),PointF(right,bottom-dy))
          corner (PointF(right,bottom-dy)) (PointF(right,bottom-dy*(1.0f-kappa)))
                 (PointF(right-radius*(1.0f-kappa),bottom)) (PointF(right-radius,bottom)) ]

    /// The whole outline: left edge (bottom -> top), top line, right edge
    /// (top -> bottom). The figure is left open; FillPath closes it along
    /// `bottom`, outside the bitmap.
    let outline (shape: string) (width: float32) (bottom: float32) (top: float32) (edgeWidth: float32) : Segment list =
        // A crowded strip can make a tab narrower than its two edge bands.
        // The new shapes then meet in the middle instead of crossing over;
        // the original shape is left exactly as it always was.
        let halfWidth = width / float32(2)
        match normalize shape with
        | "Rectangle" -> rectangleOutline width bottom top edgeWidth 0.0f false
        | "Rounded rectangle" -> rectangleOutline width bottom top edgeWidth 0.25f false
        | "Strong rounded rectangle" -> rectangleOutline width bottom top edgeWidth 0.5f false
        | "Angular rectangle" -> rectangleOutline width bottom top edgeWidth 0.25f true
        | "Rounded vertical line" -> roundedOutline width bottom top edgeWidth
        | "Trapezoid" ->
            let run = min (abs (top - bottom) / sqrt 3.0f) halfWidth
            // Keep the steeper sides centred in their existing edge bands.
            // Otherwise the wider base would cover the next tab's icon.
            let inset = max 0.0f ((min edgeWidth halfWidth - run) / 2.0f)
            [ Line(PointF(inset, bottom), PointF(inset + run, top))
              Line(PointF(inset + run, top), PointF(width - inset - run, top))
              Line(PointF(width - inset - run, top), PointF(width - inset, bottom)) ]
        | "Left-cut trapezoid" ->
            let right = max 0.0f (width - 1.0f)
            let edge = min (abs (top - bottom) / sqrt 3.0f) (right / 2.0f)
            let inset = min (float32(boxInset (int edgeWidth))) (right / 2.0f)
            let baseInset = max 0.0f ((min edgeWidth (right / 2.0f) - edge) / 2.0f)
            [ Line(PointF(baseInset, bottom), PointF(baseInset + edge, top))
              Line(PointF(baseInset + edge, top), PointF(right - inset, top))
              Line(PointF(right - inset, top), PointF(right - inset, bottom)) ]
        | "Right-cut trapezoid" ->
            let right = max 0.0f (width - 1.0f)
            let edge = min (abs (top - bottom) / sqrt 3.0f) (right / 2.0f)
            let inset = min (float32(boxInset (int edgeWidth))) (right / 2.0f)
            let baseInset = max 0.0f ((min edgeWidth (right / 2.0f) - edge) / 2.0f)
            [ Line(PointF(inset, bottom), PointF(inset, top))
              Line(PointF(inset, top), PointF(right - baseInset - edge, top))
              Line(PointF(right - baseInset - edge, top), PointF(right - baseInset, bottom)) ]
        | "Angular vertical line" ->
            let edge = min edgeWidth halfWidth
            let middle = edge / 2.0f
            // Match the angular rectangle's corner length, including narrow tabs.
            let boxLeft = max 0.0f (min (float32(boxInset (int edgeWidth))) (floor ((width - 1.0f) / 2.0f)))
            let boxRight = max boxLeft (width - 1.0f - boxLeft)
            let chamfer = min (edgeWidth / 4.0f) (min ((boxRight - boxLeft) / 2.0f) (abs (top - bottom) / 2.0f))
            let dy = float32(Math.Sign(top - bottom)) * chamfer
            let left =
                [ Line(PointF(middle - chamfer, bottom), PointF(middle, bottom + dy))
                  Line(PointF(middle, bottom + dy), PointF(middle, top - dy))
                  Line(PointF(middle, top - dy), PointF(middle + chamfer, top)) ]
            left @ [ Line(PointF(middle + chamfer, top), PointF(width - middle - chamfer, top)) ] @ mirrored width left
        | "Strong rounded vertical line" ->
            let edgeWidth = min edgeWidth halfWidth
            let left = strongLeftEdge bottom top edgeWidth
            left
            @ [ Line(PointF(edgeWidth, top), PointF(width - edgeWidth, top)) ]
            @ mirrored width left
        | _ ->
            let edge = sCurveEdge
            edge (PointF(float32(0), bottom)) (PointF(edgeWidth, top))
            @ [ Line(PointF(edgeWidth, top), PointF(width - edgeWidth, top)) ]
            @ edge (PointF(width - edgeWidth, top)) (PointF(width, bottom))

    let addToPath (path: GraphicsPath) (segments: Segment list) =
        for segment in segments do
            match segment with
            | Line(a, b) -> path.AddLine(a, b)
            | Bezier(a, b, c, d) -> path.AddBezier(a, b, c, d)
