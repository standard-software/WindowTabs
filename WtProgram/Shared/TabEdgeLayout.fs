namespace Bemo

// Where the strips of the left, right and bottom edges go, how a left or
// right strip written across lays out its rows, and the room a snap leaves
// for the strips: rectangles and lengths in device pixels as plain integers,
// so that the rules can be checked without a window
// (TabEdgeLayout.Tests.fsx). The top strip keeps its own placement
// (WindowDecorator), sharing the visibility decision here.
//
// Rectangles are (x, y, width, height). `frame` is the window's visible
// frame (its rectangle less the invisible resize borders), `monitors` the
// work area of the window's display for four-edge placement.
module TabEdgeLayout =
    open TabEdges

    type Box = int * int * int * int

    /// Four-edge placement belongs to the window's monitor, even at a seam.
    /// The caller resolves that monitor with MonitorFromWindow. Keep the
    /// top-only path's existing all-monitor policy; no monitor means no evidence.
    let placementMonitors topOnly home monitors =
        if topOnly then monitors else home |> Option.toList

    // ------------------------------------------------ outside or inside --

    // How much of `strip` one display shows across the strip: its height for
    // a top or bottom strip, its width for a left or right one - counted only
    // where the display and the strip also meet along the strip.
    let private acrossOn (edge: Edge) ((mx, my, mw, mh): Box) ((sx, sy, sw, sh): Box) =
        let ox = min (mx + mw) (sx + sw) - max mx sx
        let oy = min (my + mh) (sy + sh) - max my sy
        if runsHorizontally edge then (if ox > 0 then max 0 oy else 0)
        else (if oy > 0 then max 0 ox else 0)

    // One work area must show the full thickness and at least 90% of the
    // length. Do not combine coverage from different displays.
    let private fitsOutside (edge: Edge) (monitors: Box list) ((sx, sy, sw, sh): Box) =
        sw > 0 && sh > 0 &&
        (monitors |> List.exists (fun (mx, my, mw, mh) ->
            let ox = max 0 (min (mx + mw) (sx + sw) - max mx sx)
            let oy = max 0 (min (my + mh) (sy + sh) - max my sy)
            if runsHorizontally edge then
                oy = sh && int64 ox * 10L >= int64 sw * 9L
            else
                ox = sw && int64 oy * 10L >= int64 sh * 9L))

    let private showsInsideForStrip edge monitors inside outside visibleOutside =
        not (fitsOutside edge monitors visibleOutside) &&
        (monitors |> List.exists (fun m -> acrossOn edge m inside > acrossOn edge m outside))

    /// Prefer a sufficiently visible outside strip on every edge.
    /// Otherwise retain the existing per-display comparison.
    let showsInside (edge: Edge) (monitors: Box list) (inside: Box) (outside: Box) =
        showsInsideForStrip edge monitors inside outside outside

    /// With the window's monitor resolved, lack of room there is enough.
    /// A straddling frame may put both candidate strips beyond its boundary.
    let showsInsideHome edge monitors outside =
        not (List.isEmpty monitors) && not (fitsOutside edge monitors outside)

    /// The direction a strip takes (5-1): "always inside", or inside only
    /// when outside would leave the screen.
    let goesInside (direction: Direction) (edge: Edge) (monitors: Box list) (inside: Box) (outside: Box) =
        match direction with
        | Direction.AlwaysInside -> true
        | Direction.Outside -> showsInside edge monitors inside outside

    // ------------------------------------------------------- the lengths --

    /// A strip shortened at its two ends by (first, last) - its distances
    /// from the frame's ends, along x for a top or bottom strip and along y
    /// for a left or right one - and never below one pixel.
    let inset (edge: Edge) (first: int, last: int) ((x, y, w, h): Box) : Box =
        let cut length =
            let first = min (max 0 (length - 1)) first
            let last = min (max 0 (length - first - 1)) last
            first, max 1 (length - first - last)
        if runsHorizontally edge then
            let first, length = cut w
            (x + first, y, length, h)
        else
            let first, length = cut h
            (x, y + first, w, length)

    /// A left or right strip where a top or bottom strip (`corners`) covers
    /// one of its ends: the top and bottom strips win the corner, and the
    /// side strip starts below one over its upper half and ends above one
    /// over its lower half (7-2) - TabEdges.sideStripOutside's rule, judged
    /// on the strip itself, so that a corner strip clear of it changes
    /// nothing.
    let belowCorners ((x, y, w, h): Box) (corners: Box list) : Box =
        let middle = y + h / 2
        let mutable first = y
        let mutable last = y + h
        for (ox, oy, ow, oh) in corners do
            let across = ow > 0 && oh > 0 && ox < x + w && x < ox + ow
            let along = oy < y + h && y < oy + oh
            if across && along then
                if oy + oh / 2 < middle then first <- max first (oy + oh)
                else last <- min last oy
        (x, first, w, max 0 (last - first))

    // -------------------------------------------- a strip written across --

    /// The columns between the frame's side and the edge of the display
    /// there (6-1): measured on the display the column just outside the
    /// frame is on - of those, the one that shares most of the frame's
    /// height. 0 when that column is on no display: the frame stands at, or
    /// past, the edge of the screen.
    let sideGap (edge: Edge) (monitors: Box list) ((fx, fy, fw, fh): Box) =
        let isLeft = edge = Edge.Left
        let column = if isLeft then fx - 1 else fx + fw
        let shared (_, my, _, mh) = min (my + mh) (fy + fh) - max my fy
        monitors
        |> List.filter (fun (mx, _, mw, _ as m) -> mx <= column && column < mx + mw && shared m > 0)
        |> function
            | [] -> 0
            | found ->
                let (mx, _, mw, _) = found |> List.maxBy shared
                if isLeft then fx - mx else mx + mw - (fx + fw)

    /// How wide a strip written across stands outside (6-1): as wide as the
    /// tab width setting, narrower when the gap is, and down to the icon.
    /// None when not even the icon fits: then the icon-wide strip goes
    /// inside the window (and the window does not move).
    let acrossWidth (maxWidth: int) (iconWidth: int) (gap: int) =
        let width = min maxWidth gap
        if width >= min maxWidth iconWidth then Some(width) else None

    /// A row written across, shown by its icon alone: the padding at both
    /// ends, the icon, and the one-pixel hairline on the window's side.
    let iconOnlyWidth (padding: int) (iconSize: int) = padding * 2 + iconSize + 1

    /// The icon of a row `length` long: the icon of a top tab of that
    /// height (16 to 24 design pixels, 8 less than the tab, which is the
    /// strip less its hairline), never taller than the row. `px` turns
    /// design pixels into the display's.
    let rowIconSize (px: int -> int) (length: int) =
        max 1 (min (min (max (px 16) (length - 1 - px 8)) (px 24)) (length - 2))

    // --------------------------------------------------------- placing --

    /// One edge's strip as the placement needs it, every length already in
    /// the device pixels of the edge's display.
    type EdgeSpec = {
        edge: Edge
        direction: Direction
        /// A left or right strip written across (rows of text).
        across: bool
        /// Across the strip: the tab height of a bottom strip or a strip
        /// written upwards, the tab width setting of a strip written across.
        thickness: int
        /// A strip written across, shown by the icons alone
        /// (iconOnlyWidth); the thickness elsewhere.
        minThickness: int
        /// From the frame's ends: [left][right] on the bottom edge,
        /// [top][bottom] on a left or right one.
        outsideDistance: int * int
        insideDistance: int * int
        /// Appearance offset retained for callers; bottom placement and snap
        /// reserve ignore it so the outside strip starts just below the frame.
        heightOffset: int
    }

    type Placement = {
        rect: Box
        inside: bool
    }

    /// Where a bottom, left or right strip goes. The bottom strip stands
    /// below the frame, or inside its bottom; a left or right strip beside
    /// the frame or inside its side, cut short where a top or bottom strip
    /// (`corners`) takes the corner. Written across, a strip beside the
    /// frame narrows to the gap to the display's edge, and when not even the
    /// icons fit it goes inside, as wide as the icons (6-1); "always inside"
    /// puts the whole width inside (5-3). The distances are the outside ones
    /// for a strip outside and the inside ones for a strip inside.
    let place (spec: EdgeSpec) ((fx, fy, fw, fh) as frame: Box) (monitors: Box list) (corners: Box list) : Placement =
        let t = max 1 spec.thickness
        match spec.edge with
        | Edge.Top -> invalidArg "spec" "the top strip is WindowDecorator's"
        | Edge.Bottom ->
            let t = min t (max 1 fh)
            let outside = inset Edge.Bottom spec.outsideDistance (fx, fy + fh, fw, t)
            let inside = inset Edge.Bottom spec.insideDistance (fx, fy + fh - t, fw, t)
            let isInside = spec.direction = Direction.AlwaysInside || showsInsideHome Edge.Bottom monitors outside
            { rect = (if isInside then inside else outside); inside = isInside }
        | Edge.Left | Edge.Right ->
            let isLeft = spec.edge = Edge.Left
            let column x w : Box = (x, fy, w, fh)
            let outsideAt w = column (if isLeft then fx - w else fx + fw) w
            let insideAt w =
                let w = min w (max 1 fw)
                column (if isLeft then fx else fx + fw - w) w
            let finish isInside (strip: Box) =
                let distance = if isInside then spec.insideDistance else spec.outsideDistance
                { rect = belowCorners (inset spec.edge distance strip) corners; inside = isInside }
            let sideShowsInside outside =
                showsInsideHome spec.edge monitors (belowCorners outside corners)
            if spec.across then
                match spec.direction with
                | Direction.AlwaysInside -> finish true (insideAt t)
                | Direction.Outside ->
                    // End distances and corners affect the usable width on
                    // the window's display, supplied by the placement caller.
                    let preferredWidth =
                        monitors
                        |> List.choose (fun m -> acrossWidth t spec.minThickness (sideGap spec.edge [m] frame))
                        |> List.sortDescending
                        |> List.tryFind (fun w -> fitsOutside spec.edge monitors (finish false (outsideAt w)).rect)
                    let width =
                        match preferredWidth with
                        | Some _ -> preferredWidth
                        | None -> acrossWidth t spec.minThickness (sideGap spec.edge monitors frame)
                    match width with
                    | Some(w) ->
                        let outside = inset spec.edge spec.outsideDistance (outsideAt w)
                        if sideShowsInside outside then
                            finish true (insideAt (min t spec.minThickness))
                        else finish false (outsideAt w)
                    | None -> finish true (insideAt (min t spec.minThickness))
            else
                let outside = inset spec.edge spec.outsideDistance (outsideAt t)
                let isInside = spec.direction = Direction.AlwaysInside || sideShowsInside outside
                finish isInside (if isInside then insideAt t else outsideAt t)

    // ------------------------------------------ the rows of a strip written across --

    type Rows = {
        length: int
        overlap: int
        band: int
        icon: int
    } with
        /// From one row's start to the next: height less effective overlap.
        member this.pitch = max 1 (this.length - this.overlap)

    // The design pixels a row keeps clear around its icon and text, above
    // and below together: the outline never comes nearer them than that.
    let private rowClearance = 2

    // The top strip's edge band, in design pixels: the band a row's outline
    // turns its corners in whenever the row has room for it.
    let private rowEdgeBand = 18

    /// The rows of a strip written across, from the tab height and the tab
    /// overlap; `px` turns design pixels into the display's. This is the one
    /// place their geometry is decided:
    /// - the length is the tab height, and overlap reduces the pitch;
    /// - the overlap is the tab overlap in pixels, kept to what leaves every
    ///   row's icon and text clear of its neighbours' outlines and inside its
    ///   own, whatever the setting (0, negative, or larger than a row);
    /// - the band is the top strip's (18 at 100%), so that the seven shapes
    ///   keep their corners, narrowed only where the outline would otherwise
    ///   come nearer the icon than `rowClearance`.
    let rowsOf (px: int -> int) (tabHeight: int) (tabOverlap: int) (textHeight: int) =
        let length = max 1 tabHeight
        let icon = max 1 (min (rowIconSize px length) (length - 1 - px rowClearance))
        let content = max textHeight icon + px rowClearance
        let edgeBand = max 0 (px rowEdgeBand)
        // Protect centred content even from a neighbour's full rectangular
        // bitmap, on either side and regardless of which tab is active.
        let most = max 0 ((length - content) / 2)
        // Negative overlap opens a gap, limited to one row's height.
        // Buttons do not determine density; icons and text stay clear even
        // when the active row covers a neighbour from the opposite side.
        let overlap = max (-length) (min most tabOverlap)
        let band = max 0 (min edgeBand (length - 1 - content))
        { length = length; overlap = overlap; band = band; icon = icon }

    /// Front to back: active first, then the strip's visual order.
    let rowOrder active tabs =
        (tabs |> List.filter (fun t -> Some t = active)) @
        (tabs |> List.filter (fun t -> Some t <> active))

    /// Along the strip, from the first row's start to the last row's end.
    let contentLength (rows: Rows) (count: int) =
        if count <= 0 then 0 else (count - 1) * rows.pitch + rows.length

    /// A row's padding at each end across the strip (6 at 100%).
    let rowPadding (px: int -> int) = px 6

    /// The narrowest a row is: its icon alone (iconOnlyWidth) - the width of
    /// a pinned row "icon only", and the least a strip narrows to (6-1).
    let iconOnlyRow (px: int -> int) (rows: Rows) = iconOnlyWidth (rowPadding px) rows.icon

    /// The part of the strip the rows are shown in, as (start, length). All
    /// of it while they fit. When they do not, a scroll button `button` long
    /// takes each end and the rows scroll between them (6-1); a strip too
    /// short for two buttons and a row keeps its whole length and scrolls by
    /// the wheel alone.
    let viewport (rows: Rows) (count: int) (stripLength: int) (button: int) =
        let button = max 0 button
        if contentLength rows count > stripLength && button > 0 && stripLength - 2 * button >= rows.length then
            (button, stripLength - 2 * button)
        else (0, max 0 stripLength)

    /// Whether the strip shows its scroll buttons.
    let scrolls (rows: Rows) (count: int) (stripLength: int) (button: int) =
        fst (viewport rows count stripLength button) > 0

    /// The length the rows are laid out along: the view while they fit, so
    /// that the rows aligned to the end stand at the bottom; all the rows
    /// when they do not, one after the other.
    let layoutLength (rows: Rows) (count: int) (view: int) = max view (contentLength rows count)

    let maxScroll (rows: Rows) (count: int) (view: int) = max 0 (contentLength rows count - max 0 view)

    let clampScroll (rows: Rows) (count: int) (view: int) (scroll: int) =
        max 0 (min (maxScroll rows count view) scroll)

    let pitch (rows: Rows) = rows.pitch

    // The scroll stops a whole row at a time: at the start of a row, and at
    // the very end (maxScroll), where the last row shows whole. `n` rows on
    // from `scroll`, or back from it.
    let private rowsOn (rows: Rows) (count: int) (view: int) (scroll: int) (n: int) =
        let p = pitch rows
        let scroll = clampScroll rows count view scroll
        let target =
            if n = 0 then scroll
            elif n > 0 then (scroll / p + n) * p
            else ((scroll + p - 1) / p + n) * p
        clampScroll rows count view target

    /// After a turn of the wheel (WM_MOUSEWHEEL's delta: 120 a notch,
    /// positive away from the user, towards the first row): a row a notch,
    /// from row start to row start.
    let wheelScroll (rows: Rows) (count: int) (view: int) (scroll: int) (delta: int) =
        let notches = delta / 120
        rowsOn rows count view scroll (-notches)

    /// How many rows a page is (a click on a scroll button): the rows the
    /// view shows whole, less one, so that the row at the edge stays in
    /// sight; never less than one.
    let pageRows (rows: Rows) (view: int) =
        max 1 ((max 0 (view - rows.overlap)) / pitch rows - 1)

    /// A page on, from row start to row start.
    let pageScroll (rows: Rows) (count: int) (view: int) (scroll: int) (down: bool) =
        let page = pageRows rows view
        rowsOn rows count view scroll (if down then page else -page)

    /// A row on (the pointer over a scroll button while a tab is dragged).
    let rowScroll (rows: Rows) (count: int) (view: int) (scroll: int) (down: bool) =
        rowsOn rows count view scroll (if down then 1 else -1)

    /// Keep sub-notch input per edge; positive deltas move towards the first row.
    let accumulateWheel (rows: Rows) count view scroll remainder delta =
        let total = remainder + delta
        wheelScroll rows count view scroll total, total % 120

    /// How many rows are out of sight, wholly or in part, before and after
    /// the view: (before, after).
    let hiddenRows (rows: Rows) (count: int) (view: int) (scroll: int) =
        if count <= 0 then (0, 0)
        else
            let scroll = clampScroll rows count view scroll
            let starts = [ for i in 0 .. count - 1 -> i * pitch rows ]
            let before = starts |> List.filter (fun s -> s < scroll) |> List.length
            let after = starts |> List.filter (fun s -> s + rows.length > scroll + view) |> List.length
            (before, after)

    /// The scroll that shows the row starting at `start` (content
    /// coordinates) whole, moving as little as it can.
    let scrollToShow (rows: Rows) (count: int) (view: int) (scroll: int) (start: int) =
        let scroll = clampScroll rows count view scroll
        let s =
            if start < scroll then start
            elif start + rows.length > scroll + view then ((start + rows.length - view + pitch rows - 1) / pitch rows) * pitch rows
            else scroll
        clampScroll rows count view s

    /// The row a point `at` along the strip (content coordinates) is on: of
    /// the rows that cover it, the one whose middle is nearest - the border
    /// between two rows is the middle of their overlap, as between two tabs
    /// of the top strip. The first of equals.
    let rowAt (rows: Rows) (starts: ('id * int) list) (at: int) : 'id option =
        starts
        |> List.filter (fun (_, s) -> at >= s && at < s + rows.length)
        |> function
            | [] -> None
            | covering ->
                let distance (_, s) = abs (2 * at - (2 * s + rows.length))
                covering |> List.minBy distance |> fst |> Some

    // ----------------------------------------------------------- snapping --

    /// Room left by a snap (7-1), on each side of the work area.
    type Reserve = {
        top: int
        left: int
        right: int
        bottom: int
    }

    let noReserve = { top = 0; left = 0; right = 0; bottom = 0 }

    /// "Leave room for tabs when snapping", decided with the direction of
    /// each strip, for the destination and from nothing but it: an edge with
    /// tabs that stand outside gets room as thick as its strip - the tab
    /// height, or the tab width setting for a strip written across (Q-J) -
    /// and in that room its strip then stands outside; an edge "always
    /// inside" gets none. Nothing of where the window was, or whether it was
    /// maximised, goes in, so the same snap gives the same rectangle however
    /// often it is made (7-1). The top edge reserves tabHeight less one
    /// when supplied by the caller.
    /// `specs` are the edges other than the top that have tabs, `topHeight`
    /// the top strip's height when it has tabs and is not always inside.
    let snapReserve (enabled: bool) (topHeight: int option) (specs: EdgeSpec list) =
        if not enabled then noReserve
        else
            let room (edge: Edge) =
                match specs |> List.tryFind (fun s -> s.edge = edge) with
                | Some(s) when s.direction = Direction.Outside ->
                    max 0 s.thickness
                | _ -> 0
            { top = (match topHeight with Some(h) -> max 0 (h - 1) | None -> 0)
              left = room Edge.Left
              right = room Edge.Right
              bottom = room Edge.Bottom }

    /// The work area less the room, never less than a pixel either way
    /// (with tabs on edges other than the top; the top edge alone keeps
    /// SnapGeometry.reserveTabHeight's arithmetic as it is): the left and
    /// right rooms are taken together or not at all; the top's is taken
    /// first, as SnapGeometry.reserveTabHeight takes it, then the bottom's -
    /// each only where it leaves some work area. A strip whose room is not
    /// taken then goes inside.
    let reserveArea (r: Reserve) ((ax, ay, aw, ah): Box) : Box =
        let (ax, aw) = if r.left + r.right < aw then (ax + r.left, aw - r.left - r.right) else (ax, aw)
        let (ax, ay, aw, ah) = if r.top < ah then SnapGeometry.reserveTabHeight r.top (ax, ay, aw, ah) else (ax, ay, aw, ah)
        let ah = if r.bottom < ah then ah - r.bottom else ah
        (ax, ay, aw, ah)

    /// Clamp a popup on all four sides; only new edge popups use this policy.
    let fitPopup ((wx, wy, ww, wh): Box) ((x, y, w, h): Box) : Box =
        let w, h = min w (max 1 ww), min h (max 1 wh)
        max wx (min x (wx + ww - w)), max wy (min y (wy + wh - h)), w, h
