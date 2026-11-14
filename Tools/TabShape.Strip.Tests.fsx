// TestRuntime: Framework
// Checks the tab shapes on the real TabStripSprite of a built WindowTabs.exe:
// what the user sees at each pixel against what the hit test answers there,
// and how box-shaped tabs meet their neighbours. Opens no window, writes no
// file. Assembly references below use a matching Debug build.
// Run: pwsh -File Tools/Run-Tests.ps1 -Filter TabShape.Strip.Tests.fsx
#r "../WtProgram/bin/Debug/FSharp.PowerPack.dll"
#r "../WtProgram/bin/Debug/WindowTabs.exe"
#r "System.Drawing"
#r "System.Windows.Forms"

open System
open System.Drawing
open Bemo

// Optional shape names allow independent geometry cases to run in parallel.
let shapes =
    let requested = fsi.CommandLineArgs |> Array.skip 1 |> Array.toList
    if requested.IsEmpty then TabShape.all
    else
        for shape in requested do
            if not (List.contains shape TabShape.all) then failwithf "Unknown shape: %s" shape
        requested

let mutable failed = 0
let mutable passed = 0
let check name condition =
    if condition then passed <- passed + 1
    else
        failed <- failed + 1
        printfn "FAIL  %s" name

let px (scale: float) (v: int) = Dpi.px scale v

let borderColor = Color.FromArgb(0x3A, 0x70, 0xB1)

let lightAppearance shape overlap =
    let inactiveTab = Color.FromArgb(0x9F, 0xC4, 0xF0)
    {
        tabHeight = 25; tabMaxWidth = 200; tabPinnedTabWidth = 90; tabPinnedTabWidthIcon = true
        tabOverlap = overlap; tabHeightOffset = 1; tabIndentFlipped = 150; tabIndentFlippedRight = 150; tabIndentNormal = 10; tabIndentNormalRight = 10
        tabInactiveTextColor = Color.Black; tabSelectedTextColor = Color.Black; tabMouseOverTextColor = Color.Black
        tabActiveTextColor = Color.Black; tabFlashTextColor = Color.Black
        tabInactiveTabColor = inactiveTab; tabSelectedTabColor = Color.FromArgb(0xAE, 0xCC, 0xF2)
        tabMouseOverTabColor = Color.FromArgb(0xBD, 0xD5, 0xF4); tabActiveTabColor = Color.FromArgb(0xFA, 0xFC, 0xFE)
        tabFlashTabColor = Color.FromArgb(0xFF, 0xBB, 0xBB)
        tabInactiveBorderColor = borderColor; tabSelectedBorderColor = borderColor
        tabMouseOverBorderColor = borderColor; tabActiveBorderColor = borderColor
        tabFlashBorderColor = borderColor
        tabShape = shape
    }

// count tabs, the first one pinned, `right` of them right-aligned, the one
// in the middle on top.
let strip shape overlap direction scale count right width =
    let appearance = Dpi.scaleAppearance scale (lightAppearance shape overlap)
    let font = Dpi.scaledFont SystemFonts.MenuFont scale
    let ids = [ 1 .. count ]
    let info i =
        i, { TabDisplayInfo.bgColor = None; fillColor = None; underlineColor = None; borderColor = None
             text = sprintf "Window %d" i; textFont = font; textBrush = Brushes.Black; icon = SystemIcons.Application }
    let top = (count + 1) / 2
    {
        TabStripSprite.tabs = Map2<int, TabDisplayInfo>(List2(ids |> List.map info))
        appearance = appearance
        buttonModes = (TabButtonPolicy.activeOnly, TabButtonPolicy.activeOnly)
        activeTabOverride = None
        forcedSlideAlignment = None
        dimmedTabs = Set2<int>()
        hover = None
        captured = None
        visualOrder = List2(ids)
        zorder = List2(top :: (ids |> List.filter ((<>) top)))
        size = Sz(px scale width, appearance.tabHeight)
        slide = None
        tabAlignments = Map2<int, TabAlign>(List2(ids |> List.map (fun i -> i, if i > count - right then TopRight else TopLeft)))
        direction = direction
        transparent = false
        pinnedTabs = Set2<int>(List2([ 1 ]))
        selectedTabs = Set2<int>(List2([]))
        dragGroup = []
        scale = scale
    }

let groupOf count right t = if t > count - right then TopRight else TopLeft

// Compare the distinct icon pixels before and after neighbouring tabs compose.
// This catches layouts where geometry passes but an icon is mostly obscured.
let iconVisible (s: TabStripSprite<int>) t =
    use isolated = (s.renderTab t).bitmap
    use composed = s.render.bitmap
    let loc = s.tabLocation t
    let edge = px s.scale 18
    let iconHeight = min (max (px s.scale 16) (s.size.height - 1 - px s.scale 8)) (px s.scale 24)
    let pixels = [
        for y in 0 .. iconHeight - 1 do
            for x in edge .. edge + iconHeight - 1 do
                if x < isolated.Width && y < isolated.Height then
                    yield x, y, isolated.GetPixel(x, y) ]
    let background = pixels |> List.groupBy (fun (_, _, c) -> c.ToArgb()) |> List.maxBy (fun (_, group) -> group.Length) |> fst
    let distinctive = pixels |> List.filter (fun (_, _, c) -> c.ToArgb() <> background)
    let same = distinctive |> List.filter (fun (x, y, c) -> composed.GetPixel(loc.x + x, loc.y + y).ToArgb() = c.ToArgb()) |> List.length
    distinctive.Length > 0 && same * 4 >= distinctive.Length * 3

for shape in shapes do
    for scale in [1.0; 1.25; 1.5; 1.75] do
        for direction in [TabUp; TabDown] do
            let s = strip shape 20 direction scale 5 2 900
            for t in s.visualOrder.list do
                check (sprintf "composed icon remains visible: %s %.2f %A tab %d" shape scale direction t) (iconVisible s t)
                let loc = s.tabLocation t
                let tabHeight = s.size.height - 1
                let iconHeight = min (max (px scale 16) (tabHeight - px scale 8)) (px scale 24)
                let y0 = (tabHeight - iconHeight) / 2
                let hits = [
                    for y in y0 .. y0 + iconHeight - 1 do
                        for x in px scale 18 .. px scale 18 + iconHeight - 1 do
                            yield s.tryHit (Pt(loc.x + x, loc.y + y)) = Some(t, TabIcon) ]
                check (sprintf "entire icon rectangle is clickable, including transparent corners: %s %.2f %A tab %d" shape scale direction t)
                    (hits |> List.forall id)

type Analysis = {
    // pixels whose visible tab is not the tab the hit test returns, and how
    // far (px) the user would have to move from such a pixel, along its row,
    // to reach a pixel where the hit test returns the tab seen there
    mismatches: int
    maxDistance: int
    // pixels where no tab is drawn but the hit test returns one, and how far
    // such a pixel is, along its row, from a pixel of that tab
    phantoms: int
    maxPhantom: int
    // pixels between the first and last tab of a group that no tab paints
    holes: int
}

// `inset`: how far inside its tab a box shape draws its sides (0 for the
// band shapes), so group extents start and end at what is drawn.
let analyse (s: TabStripSprite<int>) (inset: int) =
    let ids = s.visualOrder.list
    let top = s.zorder.list
    let images = ids |> List.map (fun t -> t, (s.renderTab t).bitmap) |> Map.ofList
    let locs = ids |> List.map (fun t -> t, s.tabLocation t) |> Map.ofList
    let visibleAt x y =
        top |> List.tryFind (fun t ->
            let loc : Pt = locs.[t]
            let img : Bitmap = images.[t]
            let lx, ly = x - loc.x, y - loc.y
            lx >= 0 && ly >= 0 && lx < img.Width && ly < img.Height && img.GetPixel(lx, ly).A >= 128uy)
    let w = s.size.width
    let rows = [ for t in ids -> locs.[t].y ] |> List.head
    let h = images.[List.head ids].Height
    let mutable mismatches = 0
    let mutable maxDistance = 0
    let mutable phantoms = 0
    let mutable maxPhantom = 0
    let mutable holes = 0
    let extent group =
        match group with
        | [] -> None
        | _ ->
            let l = (group |> List.map (fun t -> locs.[t].x) |> List.min) + inset
            let r = (group |> List.map (fun t -> locs.[t].x + images.[t].Width - 1) |> List.max) - inset
            Some(l, r)
    let groups =
        let left = ids |> List.filter (fun t -> s.tabAlignments.tryFind(t) <> Some(TopRight))
        let right = ids |> List.filter (fun t -> s.tabAlignments.tryFind(t) = Some(TopRight))
        [ extent left; extent right ] |> List.choose id
    let nearest (row: 'a[]) x (wanted: 'a) =
        let mutable distance = Int32.MaxValue
        for x2 = 0 to w - 1 do
            if row.[x2] = wanted then distance <- min distance (abs (x2 - x))
        distance
    for y in rows .. rows + h - 1 do
        let visible = Array.init w (fun x -> visibleAt x y)
        let hit = Array.init w (fun x -> s.tryHitForTooltip(Pt(x, y)))
        for x in 0 .. w - 1 do
            match visible.[x] with
            | Some(t) when hit.[x] <> Some(t) ->
                mismatches <- mismatches + 1
                maxDistance <- max maxDistance (nearest hit x (Some t))
            | _ -> ()
        // Only the rows of the tab body: towards the far edge the curved
        // shoulders leave notches by design, so rows within a third of the
        // band of it are skipped, and so is the base row. (Empty pixels are
        // only held to a bound for the box shape, which has no shoulder.)
        let corner = px s.scale 18 / 3 + 1
        let fromFarEdge = match s.direction with TabUp -> y - rows | TabDown -> rows + h - 1 - y
        let fromBase = h - 1 - fromFarEdge
        if fromFarEdge > corner && fromBase > 1 then
            for x in 0 .. w - 1 do
                match visible.[x], hit.[x] with
                | None, Some(t) ->
                    phantoms <- phantoms + 1
                    maxPhantom <- max maxPhantom (nearest visible x (Some t))
                | _ -> ()
            for (l, r) in groups do
                for x in l .. r do
                    if visible.[x].IsNone then holes <- holes + 1
    { mismatches = mismatches; maxDistance = maxDistance; phantoms = phantoms; maxPhantom = maxPhantom; holes = holes }

let scales = [ 1.0; 1.25; 1.5; 1.75 ]
let directions = [ TabUp, "up"; TabDown, "down" ]
// Two layouts: a roomy strip (whole-pixel tab widths) and a crowded one whose
// tab widths are fractional, each with a left and a right group.
let layouts = [ "roomy", 5, 2, 900; "crowded", 11, 3, 380 ]
// The Tab Overlap of the Light preset (the default) and of the other six
// presets' records. (Applying a preset in the dialog keeps the user's own
// overlap, but -1 is a value the settings code itself carries, so it is
// checked as a real configuration.)
let overlaps = [ 20; -1 ]

printfn "visible tab vs hit test (real TabStripSprite):"
printfn "%-8s %4s %-8s %5s %5s %9s %8s %9s %8s %6s" "shape" "ovl" "layout" "scale" "dir" "mismatch" "max dist" "phantoms" "max dist" "holes"
for shape in shapes do
    for overlap in overlaps do
        for layoutName, count, right, width in layouts do
            for scale in scales do
                for direction, dirName in directions do
                    let s = strip shape overlap direction scale count right width
                    let edge = px scale 18
                    let inset = if TabShape.isBox shape then TabShape.boxInset edge else 0
                    let a = analyse s inset
                    printfn "%-8s %4d %-8s %5.2f %5s %9d %8d %9d %8d %6d" shape overlap layoutName scale dirName a.mismatches a.maxDistance a.phantoms a.maxPhantom a.holes
                    let label = sprintf "%s overlap %d %s %.2f %s" shape overlap layoutName scale dirName
                    let ov = px scale overlap
                    if TabShape.isBox shape then
                        // What is drawn is what is hit, up to the few columns
                        // two touching boxes overlap (the Tab Overlap less
                        // the two insets: 2 at 100%, 4 at 150%), which the
                        // hit test splits in the middle whichever box is in
                        // front, plus one for a fractional tab width.
                        let boxOverlap = max 0 (ov - 2 * inset)
                        // Outward rounded feet extend beyond the upright side.
                        let footRadius = int (ceil (float edge / 4.0))
                        check ("rounded: ownership stays within the overlap and foot radius: " + label)
                            (a.maxDistance <= (boxOverlap + 1) / 2 + 1 + footRadius)
                        // Where nothing is drawn, the hit test only answers
                        // within the gap between two boxes (split in the
                        // middle) - never beyond the ends of a group. Plus
                        // two for a fractional tab width: the tab is drawn
                        // int(length) wide but hit over its exact length,
                        // as it always has been.
                        let gap = max 0 (edge - ov)
                        // C-shaped corners intentionally cut away the box's
                        // corner pixels; this is not a missing edge segment.
                        let cornerRadius =
                            if shape = TabShape.strongRoundedRectangle then int (ceil (float edge / 2.0))
                            elif shape = TabShape.roundedRectangle || shape = TabShape.angularRectangle then int (ceil (float edge / 4.0))
                            else 0
                        check ("box: an empty pixel is hit only within half a gap of its tab: " + label)
                            (a.maxPhantom <= (gap + 1) / 2 + 2 + cornerRadius)
                        if ov > edge && cornerRadius = 0 then
                            check ("box: no gap between neighbouring tabs: " + label) (a.holes = 0)
                    else
                        // Curved and slanted tabs interleave, and the hit
                        // test splits the overlap in the middle regardless
                        // of which tab is on top (by design, see
                        // tryHitForTooltip): a pixel is never further than
                        // half the overlap from its own tab's hit area.
                        check ("band: hit test within half the overlap of the drawn tab: " + label)
                            (float a.maxDistance <= ceil (float (max 0 ov) / 2.0))

// Click targets follow the visible frontmost content: wherever
// the hit test answers with a tab's icon, close button or pin button, that
// tab is the one drawn in front there. (A distance bound cannot say this: a
// neighbour's box can cover a close button that is still hit a few pixels
// away.) Check the frontmost hit target on
// every row of the parts, for every shape, both overlaps, both directions,
// every scale, with the neighbours on either side in front: first the
// middle tab on top, then the order reversed. Crowded strips check destructive
// Close/Pin actions too. Their icons can extend past their own outline in
// the original S-curve, so only that pre-existing icon case is excluded.
//
// Slant clips interactive parts to the frontmost outline. Its formerly
// allowed close-button corners must now pass the same zero-covered rule.
let partOf (s: TabStripSprite<int>) pt =
    match s.tryHit pt with
    | Some(t, part) when part = TabClose || part = TabIcon || part = TabPin -> Some(t, part)
    | _ -> None
printfn ""
printfn "clickable parts (icon / close / pin) under another tab:"
printfn "%-8s %4s %5s %5s %7s %8s %12s %8s" "shape" "ovl" "scale" "dir" "zorder" "covered" "slant corner" "outside"
for shape in shapes do
    for overlap in overlaps do
        for layoutName, count, right, width in layouts do
            for scale in scales do
                for direction, dirName in directions do
                    for orderName, reorder in [ "middle", id; "reverse", List.rev ] do
                        let s0 = strip shape overlap direction scale count right width
                        let s = { s0 with zorder = List2(reorder s0.zorder.list) }
                        let ids = s.visualOrder.list
                        let images = ids |> List.map (fun t -> t, (s.renderTab t).bitmap) |> Map.ofList
                        let visibleAt x y =
                            s.zorder.list |> List.tryFind (fun t ->
                                let loc = s.tabLocation t
                                let img : Bitmap = images.[t]
                                let lx, ly = x - loc.x, y - loc.y
                                lx >= 0 && ly >= 0 && lx < img.Width && ly < img.Height && img.GetPixel(lx, ly).A >= 128uy)
                        let top = s.tabLocation (List.head ids)
                        let h = images.[List.head ids].Height
                        let mutable covered = 0
                        let mutable outside = 0
                        let mutable example = ""
                        for y in top.y .. top.y + h - 1 do
                            for x in 0 .. s.size.width - 1 do
                                match partOf s (Pt(x, y)) with
                                | Some(_, TabIcon) when layoutName = "crowded" -> ()
                                | Some(t, _) when visibleAt x y = None -> outside <- outside + 1
                                | Some(t, part) when visibleAt x y <> Some t ->
                                    covered <- covered + 1
                                    if example = "" then example <- sprintf "tab %d %A at (%d, %d) shows %A" t part x y (visibleAt x y)
                                | _ -> ()
                        printfn "%-8s %4d %5.2f %5s %7s %8d %12d %8d %s" shape overlap scale dirName orderName covered 0 outside example
                        check (sprintf "no clickable part under another tab: %s %s overlap %d %.2f %s %s" shape layoutName overlap scale dirName orderName)
                            (covered = 0)
                        if TabShape.needsOutlineHit shape then
                            check (sprintf "%s has no clickable part outside its visible outline: %d %.2f %s %s" shape overlap scale dirName orderName)
                                (outside = 0)

// Every shape keeps the original layout exactly: the same tab positions and
// sizes as the default shape, at both overlaps, so switching the shape moves
// no tab, icon, text or button, and dragging and dropping are untouched.
for shape in shapes do
    for overlap in overlaps do
        for layoutName, count, right, width in layouts do
            for scale in scales do
                let a = strip TabShape.scurve overlap TabUp scale count right width
                let b = strip shape overlap TabUp scale count right width
                let same =
                    a.visualOrder.list |> List.forall (fun t ->
                        a.tabLocation t = b.tabLocation t && (a.renderTab t).bitmap.Size = (b.renderTab t).bitmap.Size)
                check (sprintf "%s lays tabs out like scurve (overlap %d %s %.2f)" shape overlap layoutName scale) same

// Every tab's middle answers to that tab, for every shape.
for shape in shapes do
    for overlap in overlaps do
        for layoutName, count, right, width in layouts do
            for scale in scales do
                for direction, dirName in directions do
                    let s = strip shape overlap direction scale count right width
                    for t in s.visualOrder.list do
                        let l = s.tabLocation t
                        let mid = Pt(l.x + (s.renderTab t).bitmap.Width / 2, l.y + (s.size.height - 1) / 2)
                        check (sprintf "middle of tab %d hits it: %s overlap %d %s %.2f %s" t shape overlap layoutName scale dirName)
                            (s.tryHitForTooltip mid = Some(t))

// Box shapes on the composed strip: with the default overlap two neighbours
// meet on exactly ONE border line (never a double line, never a gap), at any
// scale; with -1 they stand apart by the band's width plus one, like the band
// shapes at half height. Nothing is drawn or hit outside the ends of a
// group. On the roomy strip only: in the crowded one the icon of a squeezed
// tab overflows onto its side (in every shape, as it always has) and the two
// groups run into each other; the pixel analysis above covers that strip.
for shape in shapes |> List.filter ((=) TabShape.rounded) do
    for layoutName, count, right, width in layouts |> List.filter (fun (name, _, _, _) -> name = "roomy") do
        for scale in scales do
            for direction, dirName in directions do
                let label = sprintf "%s %s %.2f %s" shape layoutName scale dirName
                let edge = px scale 18
                let inset = TabShape.boxInset edge
                for overlap in overlaps do
                    let s = strip shape overlap direction scale count right width
                    use composed = s.render.bitmap
                    let midY = (s.size.height - 1) / 2 + (match direction with TabUp -> 0 | TabDown -> 1)
                    let ids = s.visualOrder.list
                    let loc t = s.tabLocation t
                    let tabWidth t = (s.renderTab t).bitmap.Width
                    let pairs = ids |> List.pairwise |> List.filter (fun (a, b) -> groupOf count right a = groupOf count right b)
                    for a, b in pairs do
                        let aSide = (loc a).x + tabWidth a - 1 - inset
                        let bSide = (loc b).x + inset
                        let lo, hi = min aSide bSide - 2, max aSide bSide + 2
                        let border =
                            [ lo .. hi ] |> List.filter (fun x -> composed.GetPixel(x, midY).ToArgb() = borderColor.ToArgb())
                        if overlap = 20 then
                            check (sprintf "one line between tabs %d and %d: %s (columns %A)" a b label border) (border.Length = 1)
                        else
                            check (sprintf "tabs %d and %d stand apart by the band + 1: %s" a b label)
                                (abs (bSide - aSide - 1 - (edge - px scale overlap)) <= 1 && border.Length = 2)
                    // Group ends: nothing is drawn or hit outside the first
                    // and the last box.
                    let groupsOf = [ ids |> List.filter (fun t -> groupOf count right t = TopLeft)
                                     ids |> List.filter (fun t -> groupOf count right t = TopRight) ]
                    for group in groupsOf |> List.filter (not << List.isEmpty) do
                        let first, last = List.head group, List.last group
                        let firstSide = (loc first).x + inset
                        let lastSide = (loc last).x + tabWidth last - 1 - inset
                        // A tab squeezed narrower than its content shows its
                        // icon past the side (every shape does); that part
                        // is drawn, so it is hit too.
                        let iconHeight = min (max (px scale 16) (s.size.height - 1 - px scale 8)) (px scale 24)
                        let lastDrawn = max lastSide (min ((loc last).x + edge + iconHeight - 1) ((loc last).x + tabWidth last - 1))
                        let y = (loc first).y + (s.size.height - 1) / 2
                        check (sprintf "first box side is hit, the column before is not: %s overlap %d" label overlap)
                            (s.tryHitForTooltip(Pt(firstSide, y)) = Some first
                             && (firstSide = 0 || s.tryHitForTooltip(Pt(firstSide - 2, y)) <> Some first))
                        check (sprintf "last drawn column is hit, the column after is not: %s overlap %d" label overlap)
                            (s.tryHitForTooltip(Pt(lastDrawn - 1, y)) = Some last
                             && s.tryHitForTooltip(Pt(lastDrawn + 2, y)) <> Some last)
                        // The outward rounded feet extend past those sides
                        // near the base. Every painted outer pixel must also
                        // respond to both hover and clicks.
                        let mutable footPixels = 0
                        for tab, leading in [first, true; last, false] do
                            use bitmap = (s.renderTab tab).bitmap
                            for localY in 0 .. min (bitmap.Height - 1) (s.size.height - 2) do
                                for localX in 0 .. bitmap.Width - 1 do
                                    let outsideSide =
                                        if leading then localX < inset
                                        else localX > bitmap.Width - 1 - inset
                                    if outsideSide && bitmap.GetPixel(localX, localY).A >= 128uy then
                                        footPixels <- footPixels + 1
                                        let point = Pt((loc tab).x + localX, (loc tab).y + localY)
                                        check (sprintf "rounded foot hover: %s %d (%d,%d)" label tab localX localY)
                                            (s.tryHitForTooltip point = Some tab)
                                        check (sprintf "rounded foot click: %s %d (%d,%d)" label tab localX localY)
                                            (s.tryHit point |> Option.map fst = Some tab)
                        check ("rounded foot regression covers painted pixels: " + label) (footPixels > 0)
                let s = strip shape 20 direction scale count right width
                let iconHeight = min (max (px scale 16) (s.size.height - 1 - px scale 8)) (px scale 24)
                // The icon stays where the band shapes put it (a band in
                // from the tab's end), so it sits (band - inset) inside the
                // box on both sides.
                check ("pinned icon-only tab shows the icon centred in its box: " + label)
                    ((s.renderTab 1).bitmap.Width - 2 * inset = 2 * (edge - inset) + iconHeight)

printfn ""
if failed <> 0 then failwithf "%d of %d checks failed" failed (failed + passed)
printfn "all %d checks passed" passed
