namespace Bemo

// Tabs on the four edges of a window (top, left, right, bottom), as decisions
// over plain values and nothing else.
//
// A tab's edge belongs to the TAB, and the tab to its group: the owner of an
// edge is (group, window), never the window alone, because a window shown on
// all virtual desktops is a tab of one group per desktop and may sit on the
// left in one of them and on the top in another. A group keeps its tabs in
// ONE list, sorted into sixteen zones (edge x alignment x pin); each edge's
// strip is a view of that list.
//
// The rule every function here is held to: with the top edge as the only
// edge (the default), the answer is exactly what WindowTabs gives today.
// Where today's rule is a function it is called, not repeated
// (TabOrder.zoneOf, SnapGeometry.realignment, and the two index functions
// below, which WindowGroup itself calls). Where it is a member of a type that
// needs a window (TabStripSprite.movedTab, TabStrip.moveTabs), the function
// here names the lines it follows, and the tests hold it to them: the drop
// position against the real TabStripSprite of a build
// (Tools\TabEdges.Drop.Tests.fsx), the rest against line-by-line models
// (TabEdges.Tests.fsx).
module TabEdges =

    // ------------------------------------------------------------- edges --

    [<RequireQualifiedAccess>]
    type Edge =
        | Top
        | Left
        | Right
        | Bottom

    /// The one order every rule walks the edges in: top, left, right, bottom.
    let priority = [ Edge.Top; Edge.Left; Edge.Right; Edge.Bottom ]

    let rank (edge: Edge) =
        match edge with
        | Edge.Top -> 0
        | Edge.Left -> 1
        | Edge.Right -> 2
        | Edge.Bottom -> 3

    /// Top and bottom strips run left to right; left and right strips run
    /// top to bottom.
    let runsHorizontally (edge: Edge) =
        match edge with
        | Edge.Top | Edge.Bottom -> true
        | Edge.Left | Edge.Right -> false

    /// Menu directions follow the axis of the target strip.
    let menuKey edge horizontal vertical =
        if runsHorizontally edge then horizontal else vertical

    /// The top strip keeps its group default; other strips use their panel.
    let defaultAlignment edge topDefault (defaults: Map<Edge, 'a>) =
        if edge = Edge.Top then topDefault
        else defaults |> Map.tryFind edge |> Option.defaultValue topDefault

    /// Start maps to start and end to end on every edge, including restored tabs.
    let alignmentOnArrival (_sourceEdge: Edge option) (_targetEdge: Edge) alignment =
        alignment

    /// Mirror the rotation across side edges while retaining the text baseline.
    let sideTextClockwise textBottomInside edge =
        match edge with
        | Edge.Right -> textBottomInside
        | Edge.Left -> not textBottomInside
        | _ -> false

    /// Convert physical strip coordinates to the unrotated layout for hit testing.
    /// Tab order still runs top to bottom in either text orientation.
    let sideLayoutPoint clockwise thickness (x, y) =
        y, (if clockwise then thickness - 1 - x else x)

    /// Physical column of a rotated tab, including its one-pixel baseline offset.
    let sideTabColumn clockwise thickness offset height =
        if clockwise then thickness - offset - height else offset

    /// Horizontal side pins occupy start-aligned rows; other strips keep their alignment.
    let pinnedRowAlignment across pinned start alignment =
        if across && pinned then start else alignment

    // ------------------------------------------------------------ aligns --

    /// Which end of its strip a tab is drawn against: the left end of a top or
    /// bottom strip and the top end of a left or right strip (Start), or the
    /// other end (End). Named by position, so that moving a tab between a
    /// horizontal and a vertical strip keeps it - left <-> top, right <->
    /// bottom, the correspondence of 1-5. Saved as today's "TopLeft" /
    /// "TopRight".
    type Align =
        | AlignStart
        | AlignEnd

    // ---------------------------------------------------- allowed edges --

    /// The edges checked under "Tab position". One setting for the whole
    /// program, never empty.
    type Allowed = private { edges: Edge list }

    /// None for an empty list: there is always at least one edge.
    let allowedOf (edges: Edge list) =
        match priority |> List.filter (fun e -> List.contains e edges) with
        | [] -> None
        | sorted -> Some { edges = sorted }

    /// The default, and the setting under which nothing may change.
    let topOnly = { edges = [ Edge.Top ] }

    /// A single enabled edge has no own-window edge switching.
    let isSingleEdge (allowed: Allowed) = List.length allowed.edges = 1

    /// Edge-switch tolerance in logical pixels; geometry uses screen pixels.
    let ownWindowEdgeHysteresis = 16

    /// Every body point chooses the nearest allowed edge, with half-open bounds.
    let ownWindowEdgeAt scale allowed (x, y, width, height) (px, py) (current: Edge option) =
        if width <= 0 || height <= 0 ||
           px < x || py < y || px >= x + width || py >= y + height then None
        else
            let distance = function
                | Edge.Top -> py - y
                | Edge.Left -> px - x
                | Edge.Right -> x + width - 1 - px
                | Edge.Bottom -> y + height - 1 - py
            let stick = int (System.Math.Round(float ownWindowEdgeHysteresis * scale, System.MidpointRounding.AwayFromZero))
            let best = allowed.edges |> List.minBy distance
            match current with
            | Some edge when List.contains edge allowed.edges && distance edge <= distance best + stick -> Some edge
            | _ -> Some best

    let ownWindowEdge allowed rect pt current =
        if isSingleEdge allowed then None else ownWindowEdgeAt 1.0 allowed rect pt current

    /// Legacy strip tolerance is admitted only after visible-window checks.
    /// Receiver hints keep their exact rectangle.
    let ownWindowStripContains receiver horizontal gap (x, y, w, h) (px, py) =
        let dx, dy = if receiver then 0, 0 elif horizontal then 0, gap else gap, 0
        px >= x - dx && px < x + w + dx && py >= y - dy && py < y + h + dy

    /// Query only on a changed hit/order. Hidden siblings have no arbitrary
    /// count limit; a repeated handle terminates a corrupt native chain.
    let ownWindowHomeUncovered above anchor zero visible =
        let seen = System.Collections.Generic.HashSet<_>()
        let rec walk hwnd =
            if hwnd = anchor then true
            elif hwnd = zero || not (seen.Add hwnd) then false
            else walk (above hwnd)
        anchor <> zero && visible <> zero && walk visible

    /// The source block is immutable for the drag; rebuild only after arrivals.
    let ownWindowBlock original present = original |> List.distinct |> List.filter present

    // The controller uses these decisions for both native moves and its timer.
    type OwnWindowDragRoute<'target> =
        | MoveOwn of 'target
        | EnterOwn of 'target
        | LeaveOwn
        | ContinueDrag
        | EnterForeign of 'target

    let ownWindowDragRoute current currentIsOwn destination =
        match destination with
        | Some target when current = Some target -> MoveOwn target
        | Some target -> EnterOwn target
        | None when currentIsOwn -> LeaveOwn
        | None -> ContinueDrag

    /// Visible registered foreign strips win even over an empty home's band.
    /// The lazy own hit avoids walking the home's Z order in that case.
    let ownWindowPointerRoute current currentIsOwn foreign ownHit =
        match foreign with
        | Some target when current <> Some target -> EnterForeign target
        | Some _ -> ContinueDrag
        | None -> ownWindowDragRoute current currentIsOwn (ownHit())

    /// Shared with the controller: changing strips in any one group never parks.
    let exitBeforeStripChange sameGroup current destination =
        match current with
        | Some previous -> previous <> destination && not (sameGroup previous destination)
        | None -> false

    /// Reinsert only restored tabs; keep the relative order of all others.
    let ownWindowRestoreOrder original restore present =
        let moving = original |> List.filter (fun t -> restore t && List.contains t present)
        let mutable order = present |> List.filter (fun t -> not (List.contains t moving))
        for tab in moving do
            let before = original |> List.takeWhile ((<>) tab) |> List.rev |> List.tryFind (fun t -> List.contains t order)
            let index = before |> Option.map (fun t -> 1 + List.findIndex ((=) t) order) |> Option.defaultValue 0
            order <- (order |> List.take index) @ [tab] @ (order |> List.skip index)
        order

    /// Commit callbacks run on the destination thread after dragEnd and capture
    /// release. Activation must precede selection (the active tab is implicit).
    let completeDragDrop activate select pivot selected =
        activate pivot
        selected |> List.iter select

    let dragHoverRequestValid ended epoch requested capture button escape slop (x,y) (px,py) sameHit =
        not ended && epoch = requested && capture && button && not escape &&
        abs (x-px) <= slop && abs (y-py) <= slop && sameHit

    /// Strip geometry and its tolerance have different visibility rules.
    let dragStripKeeps inStrip inMargin foreignStrip visibleBelow ownBody desktop outsideWork =
        not foreignStrip && (if inStrip then visibleBelow
                             else inMargin && (ownBody || desktop || outsideWork))

    /// One native batch: bands precede every moved body, without duplicates
    /// or an attempt to move the insertion anchor behind itself.
    let dragStackOrder anchor strips bodies =
        anchor :: ((strips @ bodies) |> List.distinct |> List.filter ((<>) anchor))

    let dragHoverWindows pivot selected contains =
        (pivot :: selected) |> List.distinct |> List.filter contains

    /// The arriving window previews the destination; its previous front stays
    /// immediately behind it. No activation or source-group mutation is needed.
    let dragGroupFrontOrder pivot previous = [pivot; previous] |> List.distinct

    let dragGroupRestoreAnchor valid predecessors fallback =
        predecessors |> List.tryFind valid |> Option.defaultValue fallback

    let dragRememberFront saved front = saved |> Option.orElse front

    // Preserve every visit, including revisits; undo newest native raise first.
    let dragRecordRestore restore history = restore :: history

    let dragHoverFrontMs = 300L
    let dragHoverSlop = 4
    type DragHoverState<'id> = { target: 'id; point: int * int; since: int64; fired: bool }
    let dragHoverStep now slop target (x, y) state =
        match target with
        | None -> None, false
        | Some target ->
            match state with
            | Some old when old.target = target && abs (x - fst old.point) <= slop && abs (y - snd old.point) <= slop ->
                let fire = not old.fired && now - old.since >= dragHoverFrontMs
                Some { old with fired = old.fired || fire }, fire
            | _ -> Some { target = target; point = x,y; since = now; fired = false }, false

    /// Every setting there can be: the fifteen non-empty sets of edges.
    let everyAllowed =
        [ for mask in 1 .. 15 ->
            { edges = priority |> List.filter (fun e -> mask &&& (1 <<< rank e) <> 0) } ]

    /// In priority order, without repeats.
    let edgesOf (allowed: Allowed) = allowed.edges

    let isAllowed (allowed: Allowed) (edge: Edge) = List.contains edge allowed.edges

    let isTopOnly (allowed: Allowed) = allowed.edges = [ Edge.Top ]

    /// Group snaps keep the legacy path when only the top strip is occupied.
    /// Detached tabs retain the path selected by the allowed-edge setting.
    let useTopOnlySnap allowed edgesInUse ofGroup =
        isTopOnly allowed || (ofGroup && edgesInUse = [ Edge.Top ])

    /// The first checked edge in priority order: where a new group's first
    /// tab goes, and where the tabs of an unchecked edge go.
    let firstAllowed (allowed: Allowed) = List.head allowed.edges

    /// The last checked edge cannot be unchecked.
    let canUncheck (allowed: Allowed) (edge: Edge) =
        isAllowed allowed edge && allowed.edges.Length > 1

    /// Check or uncheck one edge. Unchecking the last one is refused and
    /// leaves the set as it was.
    let setChecked (allowed: Allowed) (edge: Edge) (on: bool) =
        if on then { edges = priority |> List.filter (fun e -> e = edge || isAllowed allowed e) }
        elif canUncheck allowed edge then { edges = allowed.edges |> List.filter ((<>) edge) }
        else allowed

    // ------------------------------------------------ which edge a tab gets --

    /// Why a tab is being given an edge (1-4). Only Restored carries a saved
    /// edge: a saved edge is used by the restore and nothing else, so an edge
    /// the user chose by a drag or a menu is never overwritten by an old
    /// record.
    type Arrival =
        /// A restart restore, or a closed tab coming back: its saved edge.
        | Restored of savedEdge: Edge
        /// A window joining a group that exists (a new window grouped
        /// automatically, a tab moved from another group by a menu): the edge
        /// of the group's active tab. None when the group has no active tab.
        | Joins of activeEdge: Edge option
        /// Tab menu "New tab", opened beside the tab it was called on: that
        /// tab's edge, even when the active tab is on another one.
        | NewTabBeside of invokerEdge: Edge
        /// Dropped on the strip of an edge.
        | DroppedOnStrip of stripEdge: Edge
        /// The first tab of a group being created.
        | StartsGroup

    /// An edge that is not checked gives way to the first checked edge in
    /// priority order (1-5).
    let fallback (allowed: Allowed) (edge: Edge) =
        if isAllowed allowed edge then edge else firstAllowed allowed

    /// The edge the tab goes to. None only for a drop on the strip of an
    /// unchecked edge, which cannot happen (an unchecked edge has no strip)
    /// and is refused rather than quietly sent to another edge, so that a
    /// caller's mistake shows.
    let edgeFor (allowed: Allowed) (arrival: Arrival) : Edge option =
        match arrival with
        | DroppedOnStrip(edge) -> if isAllowed allowed edge then Some(edge) else None
        | Restored(edge)
        | NewTabBeside(edge)
        | Joins(Some(edge)) -> Some(fallback allowed edge)
        | Joins(None)
        | StartsGroup -> Some(firstAllowed allowed)

    // ---------------------------------------------------------- the order --

    /// A tab as the ordering sees it. A group's tabs are one list of these.
    type Placed<'id> = {
        id: 'id
        edge: Edge
        align: Align
        pinned: bool
    }

    let placed (id: 'id) (edge: Edge) (align: Align) (pinned: bool) =
        { id = id; edge = edge; align = align; pinned = pinned }

    /// Sixteen zones: the edge (top, left, right, bottom), then the four
    /// zones each strip has today. Built ON TabOrder.zoneOf, so that on the
    /// top edge the zone IS today's zone and not a copy meant to agree with
    /// it. A pinned tab leads the tabs of its own alignment on every edge.
    let zoneOf (edge: Edge) (align: Align) (pinned: bool) =
        rank edge * 4 + TabOrder.zoneOf (align = AlignStart) pinned

    let zoneOfPlaced (p: Placed<'id>) = zoneOf p.edge p.align p.pinned

    /// TabStrip.normalizeVisualOrder (TabStrip.fs 758-762) over all four
    /// edges: a stable sort by zone.
    let normalize (tabs: Placed<'id> list) = tabs |> List.sortBy zoneOfPlaced

    /// Menu moves preserve alignment and pin state, appending within destination zones.
    /// Tabs already on the destination stay in place, including mixed selections.
    let moveFromMenu allowed edge moving (tabs: Placed<'id> list) =
        if isTopOnly allowed || not (isAllowed allowed edge) then tabs
        else
            let targets = Set.ofList moving
            let moved, staying =
                normalize tabs |> List.partition (fun p -> targets.Contains p.id && p.edge <> edge)
            normalize (staying @ (moved |> List.map (fun p -> { p with edge = edge })))

    /// One edge's strip in its drawing order.
    let band (edge: Edge) (tabs: Placed<'id> list) =
        normalize tabs |> List.filter (fun p -> p.edge = edge)

    /// The edges that have a tab: the strips the group shows.
    let edgesInUse (tabs: Placed<'id> list) =
        priority |> List.filter (fun e -> tabs |> List.exists (fun p -> p.edge = e))

    // --------------------------------------------------------- navigation --

    /// "Next" / "previous" from `index` among `count` tabs, wrapping at both
    /// ends. WindowGroup.switchWindow calls this.
    let stepIndex (count: int) (index: int) (forward: bool) =
        let max = count - 1
        let target = if forward then index + 1 else index - 1
        if target > max then 0
        elif target < 0 then max
        else target

    /// The tab chosen when the tab at `index` among `count` closes: the next
    /// one, or the previous one when it was the last. Never wraps.
    /// WindowGroup.removeWindow calls this.
    let indexAfterClose (count: int) (index: int) =
        if index < count - 1 then Some(index + 1)
        elif index > 0 then Some(index - 1)
        else None

    /// What a Shift+click does to the selection.
    type RangeSelection<'id> =
        | SelectTabs of 'id list
        | ClearSelection
        | KeepSelection

    /// Shift+click on `target` with `active` the active tab
    /// (WindowGroup.selectRange, 796-815): the tabs from the active one to the
    /// target, the active one itself left out. A range works inside one edge
    /// only (3-3); a Shift+click on a tab of another edge leaves the
    /// selection as it is, as today's does when a tab cannot be found.
    let selectRange (tabs: Placed<'id> list) (active: 'id option) (target: 'id) =
        match active with
        | None -> SelectTabs [ target ]
        | Some(a) when a = target -> ClearSelection
        | Some(a) ->
            let order = normalize tabs
            let find id = order |> List.tryFindIndex (fun p -> p.id = id)
            match find a, find target with
            | Some(ia), Some(it) when order.[ia].edge = order.[it].edge ->
                let lo = min ia it
                let hi = max ia it
                order.[lo .. hi] |> List.map (fun p -> p.id) |> List.filter ((<>) a) |> SelectTabs
            | _ -> KeepSelection

    // ----------------------------------------------- a drop on a strip --

    /// A tab of one strip as the strip's layout sees it.
    type StripTab<'id> = {
        tab: 'id
        tabAlign: Align
        tabPinned: bool
    }

    let stripTabsOf (edge: Edge) (tabs: Placed<'id> list) =
        band edge tabs |> List.map (fun p -> { tab = p.id; tabAlign = p.align; tabPinned = p.pinned })

    /// The lengths a strip has actually laid its tabs out with: its own
    /// length, the overlap, and the length it gives a pinned and an unpinned
    /// tab (TabStripSprite.tabLengths). Handed over by the strip that draws
    /// them, so that a drop is judged by the tabs as they are on screen - at
    /// any scale, with icon-only pinned tabs - and not by a second
    /// calculation of them.
    type StripLengths = {
        stripLength: float
        tabOverlap: float
        pinnedLength: float
        unpinnedLength: float
    }

    let private lengthIn (strip: StripTab<'id> list) (pinnedLength: float) (unpinnedLength: float) (id: 'id) =
        if strip |> List.exists (fun t -> t.tab = id && t.tabPinned) then pinnedLength else unpinnedLength

    let private alignIn (strip: StripTab<'id> list) (id: 'id) =
        match strip |> List.tryFind (fun t -> t.tab = id) with
        | Some(t) -> t.tabAlign
        | None -> AlignStart

    /// Where each tab of a strip starts along it while nothing is being
    /// dragged: TabStripSprite.tabLocation (770-781), with calcGroupWidth
    /// (633-639) and tabOffsetInGroup (641-648).
    let tabStartsAt (lengths: StripLengths) (strip: StripTab<'id> list) : ('id * int) list =
        let lengthOf = lengthIn strip lengths.pinnedLength lengths.unpinnedLength
        let overlap = lengths.tabOverlap
        strip
        |> List.map (fun t ->
            let groupTabs = strip |> List.filter (fun u -> u.tabAlign = t.tabAlign) |> List.map (fun u -> u.tab)
            let groupStart =
                match t.tabAlign with
                | AlignStart -> 0.0
                | AlignEnd ->
                    let groupWidth =
                        if groupTabs.IsEmpty then 0.0
                        else
                            let totalLen = groupTabs |> List.sumBy lengthOf
                            totalLen - float(groupTabs.Length - 1) * overlap
                    max 0.0 (lengths.stripLength - groupWidth)
            let idx = groupTabs |> List.findIndex ((=) t.tab)
            let offset =
                groupTabs
                |> Seq.take idx
                |> Seq.fold (fun acc u -> acc + lengthOf u - overlap) 0.0
            t.tab, int(groupStart + offset))

    /// Where a drag being slid along a strip lands (2-4: near the point it is
    /// dropped at; which side of the free space decides the alignment).
    /// TabStripSprite.movedTab (785-923), with dragGroupOffsetOf (609-631),
    /// taken along the strip, so that a left or right strip does with its
    /// y what a top strip does with its x.
    ///
    /// `strip` is the strip's tabs in its order, the dragged ones included
    /// (the sprite holds them while they slide); `dragGroup` the tabs dragged
    /// together, empty for one tab; `pivot` the tab under the mouse; `at` the
    /// pivot's position along the strip (the slide's x); `lengths` the
    /// lengths the strip draws them with. Returns the index in the strip
    /// without the dragged tabs, and the alignment.
    let dropPositionWithAlignment forcedAlignment (lengths: StripLengths) (strip: StripTab<'id> list) (dragGroup: 'id list) (pivot: 'id) (at: int) : int * Align =
        let count = strip.Length
        if count = 0 then (0, defaultArg forcedAlignment AlignStart)
        else
            let lengthOf = lengthIn strip lengths.pinnedLength lengths.unpinnedLength
            let alignOf = alignIn strip
            let overlap = lengths.tabOverlap
            let stripLength = lengths.stripLength
            let offsetFromPivot (tab: 'id) =
                let pIdx = dragGroup |> List.tryFindIndex ((=) pivot) |> Option.defaultValue 0
                let tIdx = dragGroup |> List.tryFindIndex ((=) tab) |> Option.defaultValue 0
                if tIdx = pIdx then 0.0
                elif tIdx > pIdx then
                    let mutable offset = 0.0
                    for i in pIdx .. tIdx - 1 do
                        let tLen = lengthOf dragGroup.[i]
                        offset <- offset + tLen - overlap
                    offset
                else
                    let mutable offset = 0.0
                    for i in tIdx .. pIdx - 1 do
                        let tLen = lengthOf dragGroup.[i]
                        offset <- offset - (tLen - overlap)
                    offset
            let groupTabs = if List.isEmpty dragGroup then [ pivot ] else dragGroup
            let inGroup t = List.contains t groupTabs
            let groupHead = if List.isEmpty dragGroup then pivot else List.head dragGroup
            let dragTabLen = lengthOf groupHead
            let groupHeadX = float(at) + offsetFromPivot groupHead
            let centerX = groupHeadX + dragTabLen / 2.0
            let order = strip |> List.map (fun t -> t.tab)
            let startWithout = order |> List.filter (fun t -> not (inGroup t) && alignOf t = AlignStart)
            let endWithout = order |> List.filter (fun t -> not (inGroup t) && alignOf t = AlignEnd)
            let calcWidth (tabs: 'id list) =
                if tabs.IsEmpty then 0.0
                else
                    let total = tabs |> List.sumBy lengthOf
                    total - float(tabs.Length - 1) * overlap
            let startWidthWithout = calcWidth startWithout
            let endWidthWithout = calcWidth endWithout
            let endStartWithout = stripLength - endWidthWithout
            let emptyCenterWithout = (startWidthWithout + endStartWithout) / 2.0
            let target = defaultArg forcedAlignment (if centerX >= emptyCenterWithout then AlignEnd else AlignStart)
            let endWidthFull =
                if target = AlignEnd then
                    let overlap = if endWithout.IsEmpty then 0.0 else overlap
                    endWidthWithout + dragTabLen - overlap
                else endWidthWithout
            let groupList, groupStartX =
                match target with
                | AlignStart -> startWithout, 0.0
                | AlignEnd -> endWithout, stripLength - endWidthFull
            // A multi-tab drag towards the end is judged by the dragged
            // block's centre, everything else by its head's leading edge.
            let isEndMulti = target = AlignEnd && List.length groupTabs > 1
            let groupTotalWidth =
                let n = List.length groupTabs
                let total = groupTabs |> List.sumBy lengthOf
                total - float(max 0 (n - 1)) * overlap
            let judgeX = if isEndMulti then groupHeadX + groupTotalWidth / 2.0 else groupHeadX
            let groupIndex =
                if groupList.IsEmpty then 0
                else
                    let mutable idx = groupList.Length
                    let mutable offset = 0.0
                    let mutable found = false
                    for i in 0 .. groupList.Length - 1 do
                        if not found then
                            let t = groupList.[i]
                            let tLen = lengthOf t
                            let tCenter = groupStartX + offset + tLen / 2.0
                            if judgeX < tCenter then
                                idx <- i
                                found <- true
                            offset <- offset + (tLen - overlap)
                    max 0 (min idx groupList.Length)
            let without = order |> List.filter (fun t -> not (inGroup t))
            let mutable groupCount = 0
            let mutable index = without.Length
            let mutable found = false
            for i in 0 .. without.Length - 1 do
                if not found then
                    let t = without.[i]
                    if alignOf t = target then
                        if groupCount = groupIndex then
                            index <- i
                            found <- true
                        else
                            groupCount <- groupCount + 1
                            index <- i + 1
            (max 0 (min without.Length index), target)

    let dropPositionAt lengths strip dragGroup pivot at =
        dropPositionWithAlignment None lengths strip dragGroup pivot at

    /// Put `moving` (in that order) into the strip of `edge` at `index` of
    /// that strip without them, with `align` if given: TabStrip.moveTabs
    /// (622-702), one strip of the group instead of the whole of it. The
    /// moved tabs take the pin of the tab after them with the same alignment
    /// when they are unpinned and it is pinned, and lose theirs when they are
    /// pinned and the tab before them is not ("smart pin"). Tabs of the other
    /// edges do not move. `moving` must already be tabs of the group (the
    /// strip holds a dragged tab from the moment it enters, TabStripDecorator
    /// dragEnter); ids that are not are ignored.
    let moveIntoStrip (edge: Edge) (moving: 'id list) (index: int) (align: Align option) (tabs: Placed<'id> list) =
        let tabs = normalize tabs
        let movingTabs =
            moving
            |> List.choose (fun id -> tabs |> List.tryFind (fun p -> p.id = id))
            |> List.map (fun p -> { p with edge = edge; align = defaultArg align p.align })
        if List.isEmpty movingTabs then tabs
        else
            let isMoving (p: Placed<'id>) = movingTabs |> List.exists (fun m -> m.id = p.id)
            let without = tabs |> List.filter (fun p -> p.edge = edge && not (isMoving p))
            let safeIndex = max 0 (min without.Length index)
            let spliced = List.truncate safeIndex without @ movingTabs @ List.skip safeIndex without
            let pivot = List.head movingTabs
            let firstIdx = spliced |> List.findIndex isMoving
            let lastIdx = spliced |> List.findIndexBack isMoving
            let rightNeighbor =
                spliced |> List.skip (lastIdx + 1) |> List.tryFind (fun p -> not (isMoving p) && p.align = pivot.align)
            let leftNeighbor =
                if firstIdx > 0 then
                    spliced |> List.truncate firstIdx |> List.filter (fun p -> not (isMoving p) && p.align = pivot.align) |> List.tryLast
                else None
            let shouldBePinned =
                if pivot.pinned then
                    match leftNeighbor with
                    | Some(l) when not l.pinned -> Some(false)
                    | _ -> None
                else
                    match rightNeighbor with
                    | Some(r) when r.pinned -> Some(true)
                    | _ -> None
            let spliced =
                match shouldBePinned with
                | Some(pin) -> spliced |> List.map (fun p -> if isMoving p then { p with pinned = pin } else p)
                | None -> spliced
            let others = tabs |> List.filter (fun p -> p.edge <> edge && not (isMoving p))
            normalize (others @ spliced)

    /// A drop on the strip of `edge` (2-3, 2-4): dropPosition, then
    /// moveIntoStrip - TabStripDecorator's dragEnd (3653-3667), which moves
    /// the tab or the dragged block to the position the sprite reports with
    /// the alignment it reports. `tabs` already holds the dragged tabs, on
    /// the strip they are being slid along.
    let dropOnStripAt (lengths: StripLengths) (edge: Edge) (dragGroup: 'id list) (pivot: 'id) (at: int) (tabs: Placed<'id> list) =
        let index, align = dropPositionAt lengths (stripTabsOf edge tabs) dragGroup pivot at
        let moving = if List.isEmpty dragGroup then [ pivot ] else dragGroup
        moveIntoStrip edge moving index (Some align) tabs

    // ------------------------------------------------ joining a group --

    // ------------------------------------------ the strips while dragging --

    /// The edges that show a thin strip to drop on while a tab is dragged
    /// (2-5): every allowed edge the group has no tab on - for every group,
    /// not only the dragged tab's. None at all while the top edge is the only
    /// one: nothing new appears in that setting.
    let receiverEdges (allowed: Allowed) (inUse: Edge list) =
        if isTopOnly allowed then []
        else edgesOf allowed |> List.filter (fun e -> not (List.contains e inUse))

    /// Minimum pixel opacity whose product with window opacity survives truncation.
    /// A fully transparent window remains transparent and does not receive hits.
    let receiverPixelAlpha (windowAlpha: byte) =
        let alpha = int windowAlpha
        if alpha = 0 then 0 else (255 + alpha - 1) / alpha

    /// Expand the thin receiver outward, preserving its window-facing boundary.
    /// Inputs and output are device-pixel rectangles.
    let receiverHitRect edge tabHeight (x, y, w, h) =
        let thickness = max (if runsHorizontally edge then h else w) (max 1 tabHeight)
        match edge with
        | Edge.Top -> x, y + h - thickness, w, thickness
        | Edge.Left -> x + w - thickness, y, thickness, h
        | Edge.Right -> x, y, thickness, h
        | Edge.Bottom -> x, y, w, thickness

    /// The end of a drag in a group (TabStripDecorator.dragEnd): `body`, and
    /// should it throw, `cleanup` - the slide, the dragged block and the thin
    /// strips cleared - before the exception goes on. While the top edge is
    /// the only one the body runs bare, exactly as the end always ran.
    let endDrag (allowed: Allowed) (body: unit -> unit) (cleanup: unit -> unit) =
        if isTopOnly allowed then body()
        else
            try body()
            with _ ->
                (try cleanup() with _ -> ())
                reraise()

    /// An enter may be delivered before the destination's queued begin.
    let clearBlockOnBegin allowed hasIncoming = isTopOnly allowed || not hasIncoming

    /// A failure in one reset must not prevent the remaining resets.
    let cleanupDrag (steps: (unit -> unit) list) =
        for step in steps do
            try step() with _ -> ()

    // ------------------------------------------------ edges switched off --

    /// Where the tabs go when edges are unchecked (1-5). Every tab of an edge
    /// no longer allowed moves to the first allowed edge. Nothing already
    /// there moves; the arrivals are added at the end of their zone
    /// (alignment x pin) in their relative order, with their pin and their
    /// alignment (left <-> top, right <-> bottom, which Align already is).
    /// Several edges unchecked at once are emptied in priority order, so the
    /// answer does not depend on the order they were unchecked or listed in.
    /// No tab is added or removed, so the active tab stays the active tab.
    let moveOffUnchecked (allowed: Allowed) (tabs: Placed<'id> list) =
        let target = firstAllowed allowed
        let tabs = normalize tabs
        let staying = tabs |> List.filter (fun p -> isAllowed allowed p.edge)
        let moving =
            priority
            |> List.filter (isAllowed allowed >> not)
            |> List.collect (fun e -> tabs |> List.filter (fun p -> p.edge = e))
            |> List.map (fun p -> { p with edge = target })
        normalize (staying @ moving)

    /// A saved order (`saved`, each item's saved edge by `edgeOf`) as a
    /// restore has to put it back while some of those edges are no longer
    /// allowed (1-5): the items that stay on their edge first, then those
    /// that move, edge by edge in priority order, each in the saved order.
    /// Tabs added to a group in this order, or placed one by one against it
    /// in any order of arrival (TabOrder.placeRestoredTab), end up as
    /// moveOffUnchecked has them: the edge's residents first, then the
    /// arrivals in their relative order. Nothing moves while every saved edge
    /// is allowed, and always nothing while the top edge is the only one and
    /// every tab was saved on it.
    let residentsFirst (allowed: Allowed) (edgeOf: 'a -> Edge) (saved: 'a list) : 'a list =
        let staying = saved |> List.filter (edgeOf >> isAllowed allowed)
        let moving =
            priority
            |> List.filter (isAllowed allowed >> not)
            |> List.collect (fun e -> saved |> List.filter (fun x -> edgeOf x = e))
        staying @ moving

    // -------------------------------------------------------------- joins --

    /// What a window joining a group is (1-4): the group's active tab is the
    /// head of its z-order as it was BEFORE the window was counted in - the
    /// window itself comes to the head the moment it is, and read then the
    /// group would look new. `edgeOfMember` is the edge of a tab of the
    /// group, None for a handle that is not one.
    let arrivalOf (zorderBefore: 'id list) (joining: 'id) (edgeOfMember: 'id -> Edge option) =
        match zorderBefore with
        | active :: _ when active <> joining ->
            match edgeOfMember active with
            | Some(edge) -> Joins(Some(edge))
            | None -> StartsGroup
        | _ -> StartsGroup

    // ----------------------------------------------------------- snapping --

    /// "Tab alignment on snap and move" (4-2), edge by edge: the tabs of a top or
    /// bottom strip answer a left or right snap, the tabs of a left or right
    /// strip a top or bottom snap, and only when all of that edge's tabs -
    /// pinned ones included - are aligned alike. The decision IS
    /// SnapGeometry.realignment: horizontal strips follow the horizontal
    /// component, vertical strips the vertical component, including corners.
    /// Centre, maximise and unknown directions change nothing.
    /// Returns each edge to realign and its new alignment.
    let snapRealign (enabled: bool) (snapDirection: string) (tabs: (Edge * Align) list) : (Edge * Align) list =
        priority
        |> List.choose (fun edge ->
            let aligns = tabs |> List.filter (fun (e, _) -> e = edge) |> List.map snd
            let direction =
                if runsHorizontally edge then snapDirection
                else
                    match snapDirection with
                    | SnapGeometry.snapTop | "snaptopleft" | "snaptopright"
                    | "top" | "topleft" | "topright" -> SnapGeometry.snapLeft
                    | SnapGeometry.snapBottom | "snapbottomleft" | "snapbottomright"
                    | "bottom" | "bottomleft" | "bottomright" -> SnapGeometry.snapRight
                    | _ -> ""
            SnapGeometry.realignment enabled direction aligns AlignStart AlignEnd
            |> Option.map (fun a -> edge, a))

    /// snapRealign for a group's tabs: which tabs take which alignment, one
    /// entry per edge that realigns, its tabs in the order given. The tabs of
    /// an edge the snap does not concern are in no entry and keep theirs.
    let snapRealignTabs (enabled: bool) (snapDirection: string) (tabs: ('id * Edge * Align) list) : ('id list * Align) list =
        snapRealign enabled snapDirection (tabs |> List.map (fun (_, e, a) -> e, a))
        |> List.map (fun (edge, align) ->
            (tabs |> List.filter (fun (_, e, _) -> e = edge) |> List.map (fun (id, _, _) -> id)), align)

    // ------------------------------------------------------ the settings --

    /// "Outside, inside when it would leave the screen" / "always inside".
    [<RequireQualifiedAccess>]
    type Direction =
        | Outside
        | AlwaysInside

    /// How a left or right strip writes the names.
    [<RequireQualifiedAccess>]
    type SideText =
        | Horizontal
        | Vertical

    /// Enable horizontal side text in the picker, saved settings and defaults.
    [<Literal>]
    let offerSideHorizontalText = false

    /// An edge's panel, less the tab appearance: the alignment a tab newly on
    /// the edge takes, the direction, the distances as (start, end) -
    /// [left][right] on a top or bottom edge, [top][bottom] on a left or right
    /// one - and, on a left or right edge, the text.
    type EdgeLayout = {
        defaultAlign: Align
        direction: Direction
        outsideDistance: int * int
        insideDistance: int * int
        text: SideText option
    }

    /// Today's values that become the top edge's (9), as Settings.fs has
    /// loaded them - tabPositionByDefault after TabBehaviorPolicy.
    /// loadTabPosition, tabVerticalDirection after normalizeVerticalDirection
    /// - and the two distances of the tab appearance.
    type LoadedTop = {
        tabPositionByDefault: string
        tabVerticalDirection: string
        tabIndentNormal: int
        tabIndentFlipped: int
    }

    /// The reset values of the distances, the same on every edge (8).
    let defaultOutsideDistance = 10
    let defaultInsideDistance = 150

    /// The top edge's layout from today's values: "TopLeft" is the start and
    /// every other value the end (WindowGroup.fs 209-212 and 264-267);
    /// "down" is always inside and every other value outside; the one outside
    /// distance goes to both [left] and [right], the inside one likewise.
    let topLayoutOf (loaded: LoadedTop) =
        { defaultAlign = if loaded.tabPositionByDefault = "TopLeft" then AlignStart else AlignEnd
          direction =
            if TabBehaviorPolicy.normalizeVerticalDirection loaded.tabVerticalDirection = TabBehaviorPolicy.verticalAlwaysDown
            then Direction.AlwaysInside
            else Direction.Outside
          outsideDistance = (loaded.tabIndentNormal, loaded.tabIndentNormal)
          insideDistance = (loaded.tabIndentFlipped, loaded.tabIndentFlipped)
          text = None }

    /// The layout the bottom, left and right edges start from (9, and the
    /// parent's decision 12): bottom left-aligned and outside; left and right
    /// top-aligned, the offered default text and outside; the reset distances.
    let newEdgeLayout (edge: Edge) =
        { defaultAlign = AlignStart
          direction = Direction.Outside
          outsideDistance = (defaultOutsideDistance, defaultOutsideDistance)
          insideDistance = (defaultInsideDistance, defaultInsideDistance)
          text = if runsHorizontally edge then None
                 else Some SideText.Vertical }

    // ---------------------------------------------------- the saved form --

    let savedOfEdge (edge: Edge) =
        match edge with
        | Edge.Top -> SavedTabState.EdgeTop
        | Edge.Left -> SavedTabState.EdgeLeft
        | Edge.Right -> SavedTabState.EdgeRight
        | Edge.Bottom -> SavedTabState.EdgeBottom

    let edgeOfSaved (saved: SavedTabState.SavedEdge) =
        match saved with
        | SavedTabState.EdgeTop -> Edge.Top
        | SavedTabState.EdgeLeft -> Edge.Left
        | SavedTabState.EdgeRight -> Edge.Right
        | SavedTabState.EdgeBottom -> Edge.Bottom

    /// The edge a saved tab comes back on (1-4-1, 1-5).
    let restoredEdge (allowed: Allowed) (t: SavedTabState.SavedTab) =
        fallback allowed (edgeOfSaved t.edge)

    // Keep floating-image rendering and non-top detached placement at one scale.
    let dragPreviewScale = 0.5

    let dragPreviewDropPoint (px, py) (ix, iy) windowOffset legacyOffset =
        match windowOffset with
        | Some(wx, wy) ->
            let scaled n = int (float n * dragPreviewScale)
            px - scaled ix + scaled wx, py - scaled iy + scaled wy
        | None ->
            let wx, wy = legacyOffset
            px - ix + wx, py - iy + wy

    // Physical preview coordinates. Keep the source tab at its actual size,
    // extending the canvas if it is longer than the window thumbnail.
    let dragPreviewPlacement edge align (windowWidth, windowHeight) (tabWidth, tabHeight) =
        let width = max windowWidth tabWidth
        let height = max windowHeight tabHeight
        let along length tabLength = if align = AlignStart then 0 else length - tabLength
        match edge with
        | Edge.Left -> (windowWidth + tabWidth, height), (0, along height tabHeight), (tabWidth, 0)
        | Edge.Right -> (windowWidth + tabWidth, height), (windowWidth, along height tabHeight), (0, 0)
        | Edge.Bottom -> (width, windowHeight + tabHeight), (along width tabWidth, windowHeight), (0, 0)
        | Edge.Top -> (width, windowHeight + tabHeight), (along width tabWidth, 0), (0, tabHeight)
