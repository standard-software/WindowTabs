namespace Bemo

open System

// The order arithmetic of the session restore, and nothing else.
//
// Restoring a group is spread over minutes or hours: WindowTabs starts, some
// of the group's windows are already open, and the rest appear one at a time
// as their applications start. Every arrival recomputes the whole group's
// order from the order that was saved, so the answer must not depend on the
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
