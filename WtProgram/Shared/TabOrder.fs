namespace Bemo

open System

// Pure order arithmetic for session restore and new-tab placement.
//
// Restoring a group is spread over minutes or hours: WindowTabs starts, some
// of the group's windows are already open, and the rest appear one at a time
// as their applications start. The saved order supplies insertion anchors;
// an arrival must not reorder the tabs already on screen. When those tabs
// still agree with the save, the result must not depend on the
// sequence the windows happened to arrive in - which makes it a calculation
// over three plain values (the saved order, the tabs as they stand now, and
// the old-handle-to-new-handle correspondence) with no window handling in it
// at all. It lives here so it can be run and checked without starting
// WindowTabs; TabOrder.Tests.fsx next to this file loads this very file.
module TabOrder =

    // The strip draws its tabs in four bands, and the stored list is kept
    // sorted by band (TabStrip.normalizeVisualOrder) because the band IS the
    // position on screen: left-aligned tabs sit against the left edge, right-
    // aligned ones against the right, and pinned tabs lead within each. This
    // is the one definition of the four; TabStrip.visualZoneOf calls it, and
    // so do the tests, so a check can never be run against zones the strip
    // would not agree with.
    let zoneOf (isLeftAligned: bool) (isPinned: bool) =
        match isLeftAligned, isPinned with
        | true, true -> 0
        | true, false -> 1
        | false, true -> 2
        | false, false -> 3

    // Known tab state wins over the group's default, before any ordering.
    let initialState defaultAlignment alignment pinned =
        Option.defaultValue defaultAlignment alignment, pinned

    // Insert once into the final band. Existing peers keep their order;
    // an explicit predecessor is used only inside the same band.
    let addTab tab zone after zoneOfTab tabs =
        if List.contains tab tabs then tabs else
        let target =
            after |> Option.bind (fun anchor ->
                tabs |> List.tryFindIndex (fun t -> t = anchor && zoneOfTab t = zone))
        match target with
        | Some index ->
            let before, rest = List.splitAt (index + 1) tabs
            before @ [tab] @ rest
        | None ->
            let before, rest = List.splitAt (tabs |> List.takeWhile (fun t -> zoneOfTab t <= zone) |> List.length) tabs
            before @ [tab] @ rest

    // A tab as the ordering sees it: the handle it has now, and the band it is
    // drawn in.
    type Placed = {
        handle: IntPtr
        zone: int
    }

    let placed (handle: IntPtr) (zone: int) = { handle = handle; zone = zone }

    // Where a tab that is on screen now stood in the saved order. Two ways in,
    // because a restore covers two different situations: WindowTabs restarting
    // inside one Windows session, where a window still carries the very handle
    // that was saved, and a Windows restart, where every handle is new and the
    // only link back is the old handle the window was matched to.
    let rankIn (savedOrder: IntPtr list) (oldHandleOf: IntPtr -> IntPtr option) (handle: IntPtr) =
        let indexOf h = savedOrder |> List.tryFindIndex ((=) h)
        match indexOf handle with
        | Some(i) -> Some(i)
        | None -> oldHandleOf handle |> Option.bind indexOf

    // The order the group should be shown in.
    //
    // Band by band, never across bands. An order that ignored the bands would
    // not survive the next normalize - which is why an earlier attempt to sort
    // a group as one flat list appeared to do nothing at all whenever the
    // group's tabs were not all aligned the same way.
    //
    // So when a group's alignment is mixed, "restored in the saved order"
    // means: within each band the tabs stand in the order they were saved in.
    // The alternative - shifting tabs between bands so that the saved sequence
    // reads left to right - would restore the order by throwing the left/right
    // alignment away, and of the two the alignment is the one the user set on
    // purpose. Note that this is a definition of what to do when the bands
    // disagree with the saved order, NOT a way of living with a lost
    // alignment: an alignment that has gone missing puts the tab in the wrong
    // band and no amount of arithmetic here can tell. Keeping the alignment is
    // the restore's job (see withoutIdentityState in Program.fs); this only
    // has to be right once it has been done.
    //
    // A tab the saved order knows nothing about - opened by the user after the
    // save, or dragged in by hand - is given no place of its own: it keeps
    // following the tab it currently follows within its band, so ordering the
    // restored tabs around it leaves it where the user last saw it. One that
    // currently precedes every known tab of its band stays at the front of it.
    let restoreOrder
            (savedOrder: IntPtr list)
            (oldHandleOf: IntPtr -> IntPtr option)
            (current: Placed list) : IntPtr list =
        if List.isEmpty savedOrder then current |> List.map (fun p -> p.handle) else
        let rank = rankIn savedOrder oldHandleOf
        current
        |> List.mapi (fun i p -> (i, p))
        |> List.groupBy (fun (_, p) -> p.zone)
        |> List.sortBy fst
        |> List.collect (fun (_, inZone) ->
            // The key is (rank, isUnknown, position now). The position now is
            // carried so that the comparison is a total order: two tabs can
            // never be left to be separated by something the caller cannot
            // see, which is what makes the result the same for every arrival
            // sequence and unchanged when it is applied twice. -1 is "ahead of
            // every saved tab", the rank an unknown tab inherits while no
            // known tab has been passed yet in this band.
            inZone
            |> List.mapFold (fun previousRank (i, p) ->
                match rank p.handle with
                | Some(r) -> ((r, 0, i), p.handle), r
                | None -> ((previousRank, 1, i), p.handle), previousRank) (-1)
            |> fst
            |> List.sortBy fst
            |> List.map snd)

    // Restore one arrival without replaying a stale snapshot over its peers.
    // Use the saved order to choose its predecessor in its own visual band;
    // splice only this handle into the current order. Conflicting peer order
    // is a user choice we preserve, even if the snapshot cannot be satisfied.
    let placeRestoredTab (savedOrder: IntPtr list) (oldHandleOf: IntPtr -> IntPtr option)
                         (handle: IntPtr) (savedHandle: IntPtr) (fallbackIndex: int)
                         (current: Placed list) : IntPtr list =
        let unchanged = current |> List.map (fun p -> p.handle)
        match current |> List.tryFind (fun p -> p.handle = handle) with
        | None -> unchanged
        | Some(self) ->
            let peers = current |> List.filter (fun p -> p.handle <> handle)
            let inZone = current |> List.filter (fun p -> p.zone = self.zone)
            let zoneStart = peers |> List.takeWhile (fun p -> p.zone < self.zone) |> List.length
            let zoneCount = peers |> List.filter (fun p -> p.zone = self.zone) |> List.length
            let index =
                if savedOrder |> List.contains savedHandle then
                    // Resolve this arrival through the claimed entry even if
                    // its current handle also occurs elsewhere in the snapshot.
                    let order =
                        savedOrder
                        |> List.filter (fun h -> h <> handle || h = savedHandle)
                        |> List.map (fun h -> if h = savedHandle then handle else h)
                    let desired = restoreOrder order oldHandleOf inZone
                    let before = desired |> List.takeWhile ((<>) handle)
                    match List.tryLast before with
                    | Some(previous) ->
                        (peers |> List.findIndex (fun p -> p.handle = previous)) + 1
                    | None -> zoneStart
                else
                    // The recorded index is absolute, but cannot cross bands.
                    max zoneStart (min (zoneStart + zoneCount) fallbackIndex)
            let before, after = List.splitAt index peers
            (before @ [self] @ after) |> List.map (fun p -> p.handle)
