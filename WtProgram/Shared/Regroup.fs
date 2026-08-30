namespace Bemo

open System

// Which windows have to change group when one application's grouping setting
// is switched on - and nothing else.
//
// Auto-grouping is decided once per window, at the moment the window first
// appears: tryAutoGroup is reached from addWindowToGroup, addWindowToGroup is
// reached only through ensureWindowIsGrouped, and that skips any window which
// is already in a group. A tab the user has dragged out is in a group - a
// group of one - so it is never asked again either. That is why ticking the
// box changed nothing for the windows already on screen: nothing ever asks
// them a second time.
//
// Asking them again on every pass is not the answer, and it is the one thing
// that must not happen: the tab the user pulled out by hand would be pulled
// straight back in at the next tick. So the question is asked exactly once, at
// the moment the setting goes from off to on, and this module is the whole of
// that one pass.
//
// It is arithmetic over plain values - which group each window is in, how far
// forward it is, what it would be grouped by - with no window handle touched
// and no group created or destroyed, so it can be run and checked without
// starting WindowTabs. Regroup.Tests.fsx next to this file loads this very
// file and prints the answers.
module Regroup =

    // A group, seen the way tryAutoGroup sees it when it looks for a home for
    // one window. The id is whatever the caller uses to name a group: the live
    // path passes the group object itself, the pass below passes its own key.
    type GroupView<'id> = {
        id: 'id
        // The applications the group's windows belong to, and the category of
        // each of those applications (0 = none). tryAutoGroup asks about
        // exactly these two things and nothing else.
        procPaths: string list
        categories: int list
        count: int
        // The z-order of the frontmost window in the group; smaller is nearer
        // the front. Int32.MaxValue stands for a window that is not in the
        // z-order list at all, which is what the live code substitutes.
        rank: int
    }

    // The rule that has always decided which group a newly opened window joins,
    // and now the only copy of it: Program.tryAutoGroup calls this, and so does
    // the pass below, so the two cannot drift apart.
    //
    // A category belongs to the application, not to the window, so a window
    // whose application has one looks for the category and a window whose
    // application has none looks for the application. Groups are tried from the
    // front of the screen backwards, and the first that matches wins.
    let chooseGroup (tabLimit: int option) (procPath: string) (category: int) (groups: GroupView<'id> list) =
        groups
        |> List.filter (fun g ->
            match tabLimit with
            | Some(limit) -> g.count < limit
            | None -> true)
        |> List.filter (fun g -> g.count > 0)
        // List.sortBy is stable, as List2.sortBy is, so groups that cannot be
        // told apart by z-order keep the order they were listed in.
        |> List.sortBy (fun g -> g.rank)
        |> List.tryFind (fun g ->
            if category > 0 then g.categories |> List.exists ((=) category)
            else g.procPaths |> List.exists ((=) procPath))
        |> Option.map (fun g -> g.id)

    // One window on screen, as the pass needs to see it.
    type WindowView = {
        hwnd: IntPtr
        procPath: string
        // The category of the window's application: the LOWEST number ticked,
        // 0 for none - what Program.getCategoryForProcess returns. The dialog
        // offers one at a time but the settings file can hold several, and the
        // live code takes the lowest, so this must too.
        category: int
        // Nearer the front is smaller.
        zorder: int
        // The group it is in now, if any. A window in no group is listed all
        // the same: it is part of the picture the pass reasons about.
        group: int option
        // True for the windows this pass may move: the application whose
        // setting has just changed, with nothing else standing in the way of
        // grouping them. Every other window is still listed, because a window
        // that may not move still tells us what its group holds.
        isTarget: bool
    }

    // Where a window has to go.
    type Destination =
        // A group that is already on screen.
        | Existing of int
        // A group that has to be made. Windows given the same number share one
        // group - that is how three windows the user had pulled apart end up in
        // one group rather than three.
        | Fresh of int

    type Move = {
        hwnd: IntPtr
        // The group it has to leave first, if it is in one.
        source: int option
        destination: Destination
    }

    // The pass itself.
    //
    // "As if it had just opened" is taken literally: every window of the
    // application lets go of its group at once, and then they are offered back
    // one at a time, frontmost first, to the same rule that greets a newly
    // opened window. Letting go FIRST and all together is what makes three
    // windows the user had pulled apart come back as one group - offered back
    // one at a time against the groups they are still sitting in, each would
    // simply find one of the others' groups and they would never converge.
    //
    // Two properties hold by construction, and they are the reason the pass is
    // safe to run against live windows:
    //
    //   * No window is ever left stranded. Every target is given a destination
    //     in the same step in which it is taken out of its group.
    //   * No destination can vanish. A group that is chosen by chooseGroup has
    //     at least one window that is NOT moving (the simulation starts each
    //     existing group with only the windows that stay), and a group that is
    //     reused for a fresh set keeps the window that nominated it, because
    //     that window's move to its own group is not a move at all. Neither
    //     kind of destination is ever emptied by this pass, so neither can be
    //     swept away by destroyEmptyGroups while the moves are being applied.
    let plan (tabLimit: int option) (windows: WindowView list) =
        let targets = windows |> List.filter (fun w -> w.isTarget) |> List.sortBy (fun w -> w.zorder)
        // The picture once every target has let go: only the windows that stay.
        let initial =
            windows
            |> List.filter (fun w -> not w.isTarget)
            |> List.choose (fun w -> w.group |> Option.map (fun g -> Existing(g), w))
            |> List.groupBy fst
            |> List.map (fun (key, pairs) -> key, pairs |> List.map snd)

        let mutable live = initial
        let mutable freshCount = 0
        let mutable placed : (WindowView * Destination) list = []

        for target in targets do
            let views =
                live |> List.map (fun (key, members) -> {
                    id = key
                    procPaths = members |> List.map (fun m -> m.procPath)
                    categories = members |> List.map (fun m -> m.category)
                    count = List.length members
                    rank =
                        members
                        |> List.fold (fun best m -> min best m.zorder) Int32.MaxValue })
            let destination =
                match chooseGroup tabLimit target.procPath target.category views with
                | Some(key) -> key
                | None ->
                    let key = Fresh(freshCount)
                    freshCount <- freshCount + 1
                    key
            // The window is in its new group from here on, so the window after
            // it sees what a second window opening a moment later would see.
            live <-
                if live |> List.exists (fun (key, _) -> key = destination) then
                    live |> List.map (fun (key, members) ->
                        if key = destination then key, members @ [target] else key, members)
                else
                    live @ [ (destination, [target]) ]
            placed <- placed @ [ (target, destination) ]

        // A group all of whose windows are targets is a group that ends the
        // pass empty. Destroying it and building a fresh one in its place would
        // throw away the group's own tab position and snap margin and make the
        // strip flicker - in the commonest case of all, an application whose
        // windows are already together, it would rebuild the group to arrive at
        // exactly what was there before. So a group that has to be created
        // moves into one of these instead.
        let emptied =
            windows
            |> List.choose (fun w -> w.group |> Option.map (fun g -> g, w))
            |> List.groupBy fst
            |> List.filter (fun (_, pairs) -> pairs |> List.forall (fun (_, w) -> w.isTarget))
            |> List.map (fun (g, pairs) ->
                g, pairs |> List.fold (fun best (_, w) -> min best w.zorder) Int32.MaxValue)
            |> Map.ofList

        // Which emptied group a fresh set moves into: the one nearest the front
        // among the groups its own windows came from. That is chooseGroup's own
        // tie-break - frontmost wins - applied to the one question chooseGroup
        // never has to answer, because a newly opened window is never one of a
        // set that is all moving at once.
        let mutable taken = Set.empty
        let mutable reuse = Map.empty
        for n in 0 .. freshCount - 1 do
            let candidate =
                placed
                |> List.filter (fun (_, d) -> d = Fresh(n))
                |> List.choose (fun (w, _) -> w.group)
                |> List.distinct
                |> List.filter (fun g -> emptied.ContainsKey(g) && not (taken.Contains(g)))
                |> List.sortBy (fun g -> (emptied.[g], g))
                |> List.tryHead
            match candidate with
            | Some(g) ->
                taken <- taken.Add(g)
                reuse <- reuse.Add(n, g)
            | None -> ()

        // Groups that hold something this pass is not moving. Only a group
        // like that can have been built by hand - the ordinary rule never puts
        // two applications together - and only there does leaving a lone
        // window where it is take nothing away.
        let mixedSources =
            windows
            |> List.choose (fun w -> if w.isTarget then None else w.group)
            |> Set.ofList

        // How many windows each fresh set holds, for the rule below.
        let freshSize =
            placed
            |> List.choose (fun (_, d) -> match d with Fresh(n) -> Some(n) | _ -> None)
            |> List.countBy id
            |> Map.ofList

        placed
        |> List.choose (fun (w, d) ->
            let destination =
                match d with
                | Fresh(n) ->
                    match reuse.TryFind(n) with
                    | Some(g) -> Existing(g)
                    | None -> Fresh(n)
                | existing -> existing
            match destination with
            // Already where it belongs: not a move, and saying so out loud is
            // what keeps the whole pass silent when nothing has to happen.
            | Existing(g) when w.group = Some(g) -> None
            // One window, in a group it shares with another application, and
            // nothing of its own to gather with. Making it a group of its own
            // would be this feature working backwards: the user asked for
            // windows to be brought together and a window would be pulled
            // apart, with no second window for it to be together WITH. The
            // group it is in is one the user built by hand - the ordinary rule
            // never puts two applications in one group - so leaving it there
            // takes nothing away that the pass could give back.
            //
            // A set of two or more is a different matter and does move: those
            // windows have each other, which is exactly what was asked for.
            | Fresh(n) when freshSize.TryFind(n) = Some(1) &&
                            (match w.group with
                             | Some(source) -> mixedSources.Contains(source)
                             | None -> false) -> None
            | _ -> Some { hwnd = w.hwnd; source = w.group; destination = destination })
