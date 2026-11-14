// TestRuntime: Framework
// Numeric checks for the tab outline shapes (WtProgram/Shared/TabShape.fs).
// Run: pwsh -File Tools/Run-Tests.ps1 -Filter TabShape.Tests.fsx
// It opens no window. Given an output folder it also writes
// strong_candidates.png there. Checks against the real TabStripSprite
// (layout, hit test, pixels) are in TabShape.Strip.Tests.fsx.
#r "System.Drawing"
#load "../WtProgram/Shared/TabShape.fs"

open System
open System.Drawing
open System.Drawing.Drawing2D
open Bemo

let mutable failed = 0
let mutable passed = 0

let check name condition =
    if condition then passed <- passed + 1
    else
        failed <- failed + 1
        printfn "FAIL  %s" name

// ---------------------------------------------------------------------------
// The outline exactly as TabSprite built it before shapes existed (copied
// verbatim from TabStripSprite.fs: renderTabEdge + borderPath). This is the
// reference the default shape has to reproduce point for point.
let legacyRenderTabEdge (path: GraphicsPath, startPoint: PointF, endPoint: PointF) =
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
    do path.AddBezier(bezPoints.[0], bezPoints.[1], bezPoints.[2], bezPoints.[3])
    do path.AddBezier(bezPoints.[2], bezPoints.[3], bezPoints.[4], bezPoints.[5])

let bottomTop (height: int) (up: bool) =
    if up then float32(height), float32(0) else float32(-1), float32(height - 1)

let legacyPath (width: int) (height: int) (edgeWidth: int) (up: bool) =
    let path = new GraphicsPath()
    let bottom, top = bottomTop height up
    do legacyRenderTabEdge(path, PointF(float32(0), bottom), PointF(float32(edgeWidth), top))
    do path.AddLine(Point(edgeWidth, int(top)), Point(width - edgeWidth, int(top)))
    do legacyRenderTabEdge(path, PointF(float32(width) - float32(edgeWidth), top), PointF(float32(width), bottom))
    path

// The outline as TabSprite builds it now.
let shapePath (shape: string) (width: int) (height: int) (edgeWidth: int) (up: bool) =
    let path = new GraphicsPath()
    let bottom, top = bottomTop height up
    TabShape.outline shape (float32 width) bottom top (float32 edgeWidth)
    |> TabShape.addToPath path
    path

// Dpi.px, copied: 96-dpi design value -> device pixels.
let px (scale: float) (value: int) =
    if scale = 1.0 then value
    else int (Math.Round(float value * scale, MidpointRounding.AwayFromZero))

let scales = [ 1.0; 1.25; 1.5; 1.75; 2.0 ]
let directions = [ true; false ]
// Tab widths seen in practice: the 200 px default maximum, pinned tabs
// (icon only: 18 + 16 + 18 = 52 design px) and squeezed tabs.
let widthsAt scale = [ px scale 200; px scale 120; px scale 90; px scale 52; px scale 34; px scale 30; 12; 3 ]
// Strip height 25 design px minus the one-device-pixel hairline.
let heightAt scale = px scale 25 - 1

// ---------------------------------------------------------------------------
// 1. The default shape reproduces the old outline exactly: same points, same
//    point types, in the same order. Identical paths drawn by identical code
//    give identical pixels, which the ARGB comparison also confirms.
let mutable legacyCases = 0
for scale in scales do
    for up in directions do
        for width in widthsAt scale do
            let height = heightAt scale
            let edge = px scale 18
            use a = legacyPath width height edge up
            for shape in [ TabShape.scurve; ""; "unknown"; "unsupported"; null ] do
                use b = shapePath shape width height edge up
                let same =
                    a.PointCount = b.PointCount
                    && Array.forall2 (fun (p: PointF) (q: PointF) -> p.X = q.X && p.Y = q.Y) a.PathPoints b.PathPoints
                    && a.PathTypes = b.PathTypes
                legacyCases <- legacyCases + 1
                check (sprintf "scurve == legacy (shape=%A scale=%.2f up=%b w=%d)" shape scale up width) same
printfn "legacy equivalence: %d cases compared point for point" legacyCases

// ---------------------------------------------------------------------------
// 2. Geometry of every shape, in both directions and at every scale.
// Tolerance: the original formula divides by 3, so its points carry float
// rounding of about 1e-5 px (e.g. 49 - 3 * (49 / 3) is not exactly 0).
let eps = 0.01f
let near (a: float32) (b: float32) = abs (a - b) < eps
let samePoint (p: PointF) (q: PointF) = near p.X q.X && near p.Y q.Y
// GDI+ sometimes records a line's start point again even when it equals the
// previous point, so repeats are dropped before comparing outlines.
let dedup (ps: PointF[]) =
    ps |> Array.fold (fun acc p ->
        match acc with
        | last :: _ when samePoint last p -> acc
        | _ -> p :: acc) [] |> List.rev |> Array.ofList

// Where a box shape stands its sides, as the requirement reads (not taken
// from TabShape): the middle of the edge band, on a whole pixel, and the
// mirror column on the right. A tab too narrow for both keeps them apart by
// meeting in the middle.
let expectedBoxSides (width: int) (edge: int) =
    let inset = min (edge / 2) ((width - 1) / 2)
    float32 inset, float32 (width - 1 - inset)

for scale in scales do
    for up in directions do
        for width in widthsAt scale do
            let height = heightAt scale
            let edge = px scale 18
            let bottom, top = bottomTop height up
            let lo, hi = min bottom top, max bottom top
            for shape in TabShape.all do
                let label = sprintf "%s scale=%.2f up=%b w=%d" shape scale up width
                let segments = TabShape.outline shape (float32 width) bottom top (float32 edge)
                use path = shapePath shape width height edge up
                let points = path.PathPoints
                let cShape = List.contains shape [TabShape.rectangle; TabShape.roundedRectangle; TabShape.strongRoundedRectangle; TabShape.angularRectangle]
                // Band shapes run from 0 to one past the last column (outside
                // the bitmap), as the original always has; box shapes stand
                // on the two side columns.
                let left, right =
                    if cShape then expectedBoxSides width edge
                    elif TabShape.isBox shape then
                        let l, r = expectedBoxSides width edge
                        let radius = min (float32 edge / 4.0f) (min ((r - l) / 2.0f) (abs (top - bottom) / 2.0f))
                        l - radius, r + radius
                    elif shape = TabShape.angular then
                        let e = min (float32 edge) (float32 width / 2.0f)
                        let l = max 0.0f (min (float32(edge/2)) (floor (float32(width-1)/2.0f)))
                        let r = max l (float32(width-1)-l)
                        let c = min (float32 edge / 4.0f) (min ((r-l)/2.0f) (abs (top-bottom)/2.0f))
                        e / 2.0f - c, float32 width - e / 2.0f + c
                    elif shape = TabShape.slant then
                        let run = min (abs (top - bottom) / sqrt 3.0f) (float32 width / 2.0f)
                        let inset = max 0.0f ((min (float32 edge) (float32 width / 2.0f) - run) / 2.0f)
                        inset, float32 width - inset
                    elif shape = TabShape.cutLeft || shape = TabShape.cutRight then
                        let w = float32(width-1)
                        let run = min (abs(top-bottom)/sqrt 3.0f) (w/2.0f)
                        let inset = min (float32(edge/2)) (w/2.0f)
                        let baseInset = max 0.0f ((min (float32 edge) (w/2.0f)-run)/2.0f)
                        if shape = TabShape.cutLeft then baseInset,w-inset else inset,w-baseInset
                    else 0.0f, float32 width
                // Stays inside the tab's box. The original S-curve on a tab
                // narrower than its two edge bands has always overshot the
                // box (its top line runs backwards); that is kept as it was,
                // so only the new shapes are held to this below 2 * edge.
                if shape <> TabShape.scurve || width >= 2 * edge then
                    check ("inside box: " + label)
                        (points |> Array.forall (fun p -> p.X >= left - eps && p.X <= right + eps && p.Y >= lo - eps && p.Y <= hi + eps))
                // Starts and ends on the base row, so FillPath closes the
                // figure along the base (outside the bitmap) as before.
                let radius =
                    if not cShape || shape = TabShape.rectangle then 0.0f
                    else min (float32 edge * (if shape = TabShape.strongRoundedRectangle then 0.5f else 0.25f)) (min ((right-left)/2.0f) (abs(top-bottom)/2.0f))
                check ("starts on base: " + label) (near points.[0].Y bottom && near points.[0].X (left+radius))
                check ("ends on base: " + label) (near points.[points.Length - 1].Y bottom && near points.[points.Length - 1].X (right-radius))
                // Reaches the far edge (the tab has its full height).
                check ("reaches far edge: " + label) (points |> Array.exists (fun p -> near p.Y top))
                // Mirror symmetric: the right side is the left side reflected.
                let outlinePoints = dedup points
                let counterpart =
                    if shape = TabShape.cutLeft then TabShape.cutRight
                    elif shape = TabShape.cutRight then TabShape.cutLeft
                    else shape
                use mirrorPath = shapePath counterpart width height edge up
                let mirrorWidth = if counterpart <> shape then float32(width - 1) else left + right
                let mirrored =
                    dedup mirrorPath.PathPoints |> Array.rev |> Array.map (fun p -> PointF(mirrorWidth - p.X, p.Y))
                check ("mirror symmetric: " + label)
                    (outlinePoints.Length = mirrored.Length && Array.forall2 samePoint outlinePoints mirrored)
                // The new shapes are one continuous line: every segment
                // starts where the previous one ended (the original S keeps
                // its historical back-step, checked in section 1 instead).
                if shape <> TabShape.scurve then
                    let ends = segments |> List.map (function
                        | TabShape.Line(a, b) -> a, b
                        | TabShape.Bezier(a, _, _, d) -> a, d)
                    let continuous =
                        ends |> List.pairwise |> List.forall (fun ((_, e), (s, _)) -> samePoint e s)
                    check ("continuous: " + label) continuous
                    // Left half never moves back towards the outer edge, and
                    // never turns back towards the base (no self-crossing).
                    use flat = path.Clone() :?> GraphicsPath
                    flat.Flatten(null, 0.05f)
                    let fp = flat.PathPoints
                    let leftHalf = fp |> Array.takeWhile (fun p -> p.X <= (left + right) / 2.0f + eps)
                    let towardsTop = if up then -1.0f else 1.0f
                    let monotonic =
                        leftHalf |> Array.pairwise
                        |> Array.forall (fun (a, b) -> (cShape || b.X >= a.X - 0.001f) && (b.Y - a.Y) * towardsTop >= -0.001f)
                    // (A box squeezed to a single column has no left half:
                    // its two sides are the same line.)
                    if right - left >= 1.0f then
                        check ("left side moves inward and upward only: " + label) monotonic

// Where the left edge crosses a given fraction of the height.
let crossingX shape width height edge (fraction: float32) =
    let bottom, top = bottomTop height true
    use p = shapePath shape width height edge true
    p.Flatten(null, 0.01f)
    let y = bottom + (top - bottom) * fraction
    p.PathPoints
    |> Array.pairwise
    |> Array.pick (fun (a, b) ->
        if (a.Y - y) * (b.Y - y) <= 0.0f && a.Y <> b.Y then
            Some(a.X + (b.X - a.X) * (y - a.Y) / (b.Y - a.Y))
        else None)

for scale in scales do
    let edge = px scale 18
    let height = heightAt scale
    let width = px scale 200
    for up in directions do
        let bottom, top = bottomTop height up
        let label = sprintf "(scale %.2f up=%b)" scale up
        let l, r = expectedBoxSides width edge
        // slant: exactly a trapezoid with 4 corners.
        use slant = shapePath TabShape.slant width height edge up
        let slopeWidth = abs (top - bottom) / sqrt 3.0f
        let slopeInset = max 0.0f ((float32 edge - slopeWidth) / 2.0f)
        check ("slant is a trapezoid " + label)
            (dedup slant.PathPoints = [| PointF(slopeInset, bottom); PointF(slopeInset + slopeWidth, top); PointF(float32 width - slopeInset - slopeWidth, top); PointF(float32 width - slopeInset, bottom) |])
        check ("trapezoid sides are 60 degrees " + label)
            (abs (atan2 (abs (top - bottom)) slopeWidth * 180.0f / float32 Math.PI - 60.0f) < 0.001f)
        // strong: a quarter-round foot, an upright middle on the column
        // where square stands its side, a quarter-round shoulder - and the
        // right edge the mirror image. Expected points written out here, not
        // taken from TabShape: the foot is as wide as the middle's distance
        // from the end, the shoulder as wide as the rest of the band.
        let dir = if up then -1.0f else 1.0f
        let foot, shoulder = l, float32 edge - l
        match TabShape.outline TabShape.strong (float32 width) bottom top (float32 edge) with
        | [ TabShape.Bezier(a, b, c, d); TabShape.Line(e, f); TabShape.Bezier(g, h, i, j); TabShape.Line _
            TabShape.Bezier(k, _, _, _); TabShape.Line(m, n); TabShape.Bezier(_, _, _, o) ] ->
            check ("strong leaves the base flat " + label) (samePoint a (PointF(0.0f, bottom)) && near b.Y bottom && b.X > a.X)
            check ("strong foot is a quarter round up to the middle column " + label)
                (near c.X l && samePoint d (PointF(l, bottom + dir * foot)))
            check ("strong middle is upright on square's side column " + label)
                (samePoint e d && near f.X l && samePoint f (PointF(l, top - dir * shoulder)))
            check ("strong shoulder meets the top flat at the band's end " + label)
                (samePoint g f && near h.X l && near i.Y top && samePoint j (PointF(float32 edge, top)))
            check ("strong right edge mirrors the left " + label)
                (samePoint k (PointF(float32 (width - edge), top))
                 && near m.X (float32 width - l) && near n.X (float32 width - l)
                 && samePoint o (PointF(float32 width, bottom)))
        | other -> check (sprintf "strong is foot, upright, shoulder, top line and the mirror %s (got %d segments)" label other.Length) false
    // strong against the original S: the same band, but its middle stands
    // upright where the S leans, which is what makes it read as "more S" at
    // 100% (the S's middle third leans across several columns).
    let x shape f = crossingX shape width height edge f
    check (sprintf "strong middle is upright (scale %.2f)" scale)
        (abs (x TabShape.strong 0.6f - x TabShape.strong 0.4f) < 0.01f)
    // Across the middle half of the height the S leans over 4 px at 100%;
    // strong covers that half in about 1 px (the ends of its two turns).
    check (sprintf "scurve leans at least 3 px across the middle half (scale %.2f)" scale)
        (x TabShape.scurve 0.75f - x TabShape.scurve 0.25f >= 3.0f)
    check (sprintf "strong leans less than a third as much there (scale %.2f)" scale)
        (3.0f * (x TabShape.strong 0.75f - x TabShape.strong 0.25f) < x TabShape.scurve 0.75f - x TabShape.scurve 0.25f)
    check (sprintf "strong passes half height on square's side column (scale %.2f)" scale)
        (abs (x TabShape.strong 0.5f - fst (expectedBoxSides width edge)) < 0.01f)

// Box sides and the hit-test trim.
check "boxInset is half the band, whole pixels"
    ([ 18, 9; 23, 11; 27, 13; 32, 16; 36, 18 ] |> List.forall (fun (e, i) -> TabShape.boxInset e = i))
check "isBox" (TabShape.isBox TabShape.rounded && not (TabShape.isBox " Square ")
               && not (TabShape.isBox TabShape.scurve) && not (TabShape.isBox TabShape.strong)
               && not (TabShape.isBox TabShape.slant) && not (TabShape.isBox "hexagon") && not (TabShape.isBox null))

// Setting values.
check "normalize keeps every known shape" (TabShape.all |> List.forall (fun s -> TabShape.normalize s = s))
check "normalize maps unknown to scurve" (TabShape.normalize "hexagon" = TabShape.scurve)
check "old angular falls back" (TabShape.normalize "angular" = TabShape.scurve)
check "old rounded falls back" (TabShape.normalize "rounded" = TabShape.scurve)
check "normalize maps null to scurve" (TabShape.normalize null = TabShape.scurve)
check "normalize maps empty to scurve" (TabShape.normalize "" = TabShape.scurve)
check "normalize forgives case and blanks" (TabShape.normalize " trapezoid " = TabShape.slant && TabShape.normalize " ROUNDED VERTICAL LINE " = TabShape.rounded)
check "removed square safely falls back to the default" (TabShape.normalize "square" = TabShape.scurve)
check "default shape first in the list" (List.head TabShape.all = TabShape.scurve)
check "twelve choices in requested order"
    (TabShape.all = ["S-curve"; "Rounded vertical line"; "Strong rounded vertical line"; "Angular vertical line"; "Trapezoid"; "Right-cut trapezoid"; "Left-cut trapezoid"; "Rectangle"; "Rounded rectangle"; "Strong rounded rectangle"; "Angular rectangle"])
check "removed angular trapezoid falls back to S-curve" (TabShape.normalize "Angular trapezoid" = TabShape.scurve)
for scale in scales do
    let w, h, edge = px scale 200, heightAt scale, px scale 18
    for up in directions do
        let bottom, top = bottomTop h up
        use left = shapePath TabShape.cutLeft w h edge up
        use right = shapePath TabShape.cutRight w h edge up
        let run = abs(top-bottom)/sqrt 3.0f
        let baseInset = max 0.0f ((float32 edge-run)/2.0f)
        check "left cut has diagonal left and vertical right"
            (dedup left.PathPoints = [| PointF(baseInset, bottom); PointF(baseInset+run, top); PointF(float32(w-1-edge/2), top); PointF(float32(w-1-edge/2), bottom) |])
        check "right cut has vertical left and diagonal right"
            (dedup right.PathPoints = [| PointF(float32(edge/2), bottom); PointF(float32(edge/2), top); PointF(float32(w-1)-baseInset-run, top); PointF(float32(w-1)-baseInset, bottom) |])
        let angular = TabShape.outline TabShape.angular (float32 w) bottom top (float32 edge)
        check "angular S has seven straight segments"
            (angular.Length = 7 && angular |> List.forall (function TabShape.Line _ -> true | _ -> false))
        match angular with
        | TabShape.Line(a,b) :: TabShape.Line(c,d) :: TabShape.Line(e,f) :: _ ->
            check "short angular chamfers are 45 degrees"
                (near (abs (b.X-a.X)) (abs (b.Y-a.Y)) && near (abs (f.X-e.X)) (abs (f.Y-e.Y)) && near c.X d.X && near (abs(b.X-a.X)) (float32 edge / 4.0f))
        | _ -> check "angular segments exist" false
        let rounded = TabShape.outline TabShape.rounded (float32 w) bottom top (float32 edge)
        check "rounded rectangle has curved shoulders and outward feet"
            ((rounded |> List.filter (function TabShape.Bezier _ -> true | _ -> false)).Length = 4)
        match rounded with
        | TabShape.Bezier(a,b,c,d) :: _ ->
            check "rounded foot turns outward like strong S" (near a.Y bottom && near b.Y bottom && a.X < d.X && near c.X d.X)
        | _ -> check "rounded foot exists" false
        match angular, TabShape.outline TabShape.angularRectangle (float32 w) bottom top (float32 edge) with
        | TabShape.Line(a,b) :: _, TabShape.Line(c,d) :: _ ->
            check "angular styles have matching chamfer lengths"
                (near (abs(b.X-a.X)) (abs(d.X-c.X)) && near (abs(b.Y-a.Y)) (abs(d.Y-c.Y)))
        | _ -> check "angular corner segments exist" false
        for shape in [TabShape.roundedRectangle; TabShape.strongRoundedRectangle] do
            match TabShape.outline shape (float32 w) bottom top (float32 edge) with
            | TabShape.Bezier(a,b,c,d) :: _ as segments ->
                check "C corners curve inward rather than flaring out" (a.X > d.X && near a.Y bottom && near b.Y bottom && near c.X d.X)
                check "C rectangle has four rounded corners" ((segments |> List.filter(function TabShape.Bezier _ -> true | _ -> false)).Length = 4)
            | _ -> check "C corners exist" false
        let octagon = TabShape.outline TabShape.angularRectangle (float32 w) bottom top (float32 edge)
        check "angular rectangle is an octagon with implicit bottom edge"
            (octagon.Length = 7 && octagon |> List.forall(function TabShape.Line _ -> true | _ -> false))
        use smallC = shapePath TabShape.roundedRectangle w h edge up
        use largeC = shapePath TabShape.strongRoundedRectangle w h edge up
        check "strong C radius is larger" (largeC.PathPoints.[0].X > smallC.PathPoints.[0].X)

// ---------------------------------------------------------------------------
// Optional picture for choosing "a stronger S": given an output folder as the
// first argument, write strong_candidates.png. Each row is three neighbouring
// tabs with the default overlap, the middle one in front: first at 100% in
// real size (what the eye gets on a 100% monitor), then the same 3x, then
// 175% 3x. Besides the S-curve and this build's strong, the rows show the
// alternative Bezier handle lengths for comparison.
match fsi.CommandLineArgs |> Array.tryItem 1 with
| None -> ()
| Some(outDir) ->
    IO.Directory.CreateDirectory(outDir) |> ignore
    let bandTab (edgeFn: PointF -> PointF -> TabShape.Segment list) (edgeWidth: float32) (width: float32) (bottom: float32) (top: float32) =
        edgeFn (PointF(0.0f, bottom)) (PointF(edgeWidth, top))
        @ [ TabShape.Line(PointF(edgeWidth, top), PointF(width - edgeWidth, top)) ]
        @ edgeFn (PointF(width - edgeWidth, top)) (PointF(width, bottom))
    let oneBezier (handle: float32) (s: PointF) (e: PointF) =
        let reach = (e.X - s.X) * handle
        [ TabShape.Bezier(s, PointF(s.X + reach, s.Y), PointF(e.X - reach, e.Y), e) ]
    let ofShape shape = fun ew w b t -> TabShape.outline shape w b t ew
    let candidates =
        [ "scurve (the default, unchanged)", ofShape TabShape.scurve
          "strong, this build: round foot, upright middle on square's side column, round shoulder", ofShape TabShape.strong
          "one-Bezier family, handle 0.80", bandTab (oneBezier 0.8f)
          "one-Bezier family, handle 0.90", bandTab (oneBezier 0.9f)
          "one-Bezier family, handle 1.00", bandTab (oneBezier 1.0f)
          "rounded (for reference)", ofShape TabShape.rounded ]
    let zoom = 3
    let tabW scale = px scale 110
    let stripW scale = 3 * tabW scale - 2 * px scale 20
    let rowH = (heightAt 1.75 + 1) * zoom + 24
    let col1 = 10
    let col2 = col1 + stripW 1.0 + 30
    let col3 = col2 + stripW 1.0 * zoom + 30
    use sheet = new Bitmap(col3 + stripW 1.75 * zoom + 10, rowH * candidates.Length + 10)
    use g = Graphics.FromImage(sheet)
    g.Clear(Color.White)
    use captionFont = new Font("Segoe UI", 9.0f)
    use labelFont = new Font("Segoe UI", 9.0f)
    candidates |> List.iteri (fun i (caption, build) ->
        let y = 10 + i * rowH
        g.DrawString(caption, captionFont, Brushes.DimGray, 10.0f, float32 y)
        for scale, x0, z in [ 1.0, col1, 1; 1.0, col2, zoom; 1.75, col3, zoom ] do
            let w, h = tabW scale, heightAt scale
            use strip = new Bitmap(stripW scale, h + 1)
            use sg = Graphics.FromImage(strip)
            sg.Clear(Color.FromArgb(230, 238, 248))
            let segments = build (float32 (px scale 18)) (float32 w) (float32 h) 0.0f
            // Left, right, then the middle one on top (the active tab).
            for j, fill in [ 0, Color.FromArgb(0x9F, 0xC4, 0xF0); 2, Color.FromArgb(0x9F, 0xC4, 0xF0); 1, Color.FromArgb(0xFA, 0xFC, 0xFE) ] do
                use path = new GraphicsPath()
                TabShape.addToPath path segments
                use m = new Matrix()
                m.Translate(float32 (j * (w - px scale 20)), 0.0f)
                path.Transform(m)
                use brush = new SolidBrush(fill)
                sg.FillPath(brush, path)
                use pen = new Pen(Color.FromArgb(0x3A, 0x70, 0xB1), 1.0f)
                sg.DrawPath(pen, path)
                if z = 1 then
                    sg.DrawString(sprintf "Window %d" (j + 1), labelFont, Brushes.Black, float32 (j * (w - px scale 20) + px scale 22), 4.0f)
            g.InterpolationMode <- InterpolationMode.NearestNeighbor
            g.PixelOffsetMode <- PixelOffsetMode.Half
            g.DrawImage(strip, Rectangle(x0, y + 18, strip.Width * z, strip.Height * z)))
    let file = IO.Path.Combine(outDir, "strong_candidates.png")
    sheet.Save(file, Imaging.ImageFormat.Png)
    printfn "wrote %s" file

printfn ""
if failed <> 0 then failwithf "%d of %d checks failed" failed (failed + passed)
printfn "all %d checks passed" passed
