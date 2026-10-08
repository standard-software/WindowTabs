namespace Bemo

open System

/// Tab groups per virtual desktop.
///
/// A window set to "show on all desktops" (task view: "Show this window on all
/// desktops" or "Show windows from this app on all desktops") is really there
/// on every desktop, so it takes part in a tab group on each of them, and those
/// groups are separate groups: separate members, separate order, separate
/// settings. What a desktop's tab strip shows is its own group, and only
/// windows that are on that desktop.
///
/// Membership survives desktop switches. Strips are always ownerless tool
/// windows: WindowTabs explicitly presents the groups of the current desktop
/// and updates retained groups in place, without a shell-owned hide/show cycle.
/// Native visibility supplies the early presentation pass; the confirmed
/// desktop reader continues to govern membership and shared-window evidence.
///
/// Nothing here calls Windows. The caller reads, this decides, the caller
/// carries it out, and VirtualDesktopGroups.Tests.fsx drives the same
/// functions over made-up desktops.
///
/// What is read, and what each reading is trusted for:
///   - IsWindowOnCurrentVirtualDesktop and the shell bit of DWMWA_CLOAKED say
///     whether a window is on the desktop being looked at (Presence). They are
///     public, and they are the only thing any display or membership decision
///     rests on: a window shown on all desktops answers "here" on every one of
///     them, whatever else it answers.
///   - GetWindowDesktopId says where a window lives. It is used only as
///     EVIDENCE that a window is on more than one desktop (it is here, yet names
///     some other desktop or none at all), and as the ordinary "which desktop
///     did this window go to" of a window that left. No particular value is
///     looked for: an application-wide pin has never been observed, and every
///     answer it could give - a listed desktop, an unlisted one, the desktop
///     being looked at, an error - is run through the tests.
///   - Explorer's list of desktops, and the desktop it is showing, from the
///     registry. The current desktop decides only things that are harmless to
///     get wrong for a moment: which of several groups made only of
///     all-desktop windows is drawn (all of their windows are on every
///     desktop, so none of them can show a window that is not there), and
///     which desktop a new group is filed under. A reading that the windows
///     themselves contradict is not used at all (trustCurrent).
module VirtualDesktopGroups =

    /// A window sent to another desktop stays in its tab group. The group is
    /// drawn on every desktop that has one of its own windows, and the tabs of
    /// the windows that are elsewhere are drawn dimmed. Off, such a window
    /// leaves the group (the straddle rule) or is handed to a group of the
    /// desktop it arrived on.
    let mutable keepAway = true

    /// How much of a dimmed tab is left: half. The strip of an inactive group
    /// is drawn at the same ratio, so a dimmed tab in one is left a quarter.
    let dimmedAlpha = 0x80

    // ------------------------------------------------------------ reading --

    type Presence =
        /// On the desktop being looked at.
        | Here
        /// On another desktop.
        | Away
        /// The two public signals disagree, or a call failed. Nothing is
        /// decided on it.
        | Unsure

    let private cloakedByShell = 2

    /// `onCurrent` is IsWindowOnCurrentVirtualDesktop and `cloak` DWMWA_CLOAKED,
    /// each None when the call failed. Both have to agree: a window being
    /// cloaked by its own application (a suspended UWP frame) is not away, and
    /// a window the API calls away that the shell has not cloaked is not
    /// believed either.
    let presence (onCurrent: bool option) (cloak: int option) =
        match onCurrent, cloak with
        | Some true, Some c when c &&& cloakedByShell = 0 -> Here
        | Some false, Some c when c &&& cloakedByShell <> 0 -> Away
        | _ -> Unsure

    type WindowRead = {
        hwnd: IntPtr
        presence: Presence
        /// GetWindowDesktopId, None when the call failed or said nothing.
        desktop: Guid option
    }

    /// Capture members independently of where their group is filed. Unreadable
    /// members stay included so a partial reading cannot silently lose them.
    let captureWindows supported (opened: Guid option) (current: Guid option)
                       (shared: Set<IntPtr>) (reads: Map<IntPtr, WindowRead>) members =
        match supported, opened, current with
        | true, Some target, Some now ->
            members |> List.filter (fun hwnd ->
                shared.Contains hwnd ||
                match reads.TryFind hwnd with
                | None -> true
                | Some r ->
                    r.presence = Unsure || r.desktop.IsNone ||
                    (now = target && r.presence <> Away) || r.desktop = Some target)
        | _ -> members

    // Exit and completion may race. Once work starts, only its finally may
    // release the step; a queued action abandoned on exit must never run late.
    let runWorkspaceGroupStep subscribe isExited tryPost postMain gone action complete =
        let gate = obj()
        let mutable phase = 0 // Waiting, running, finished.
        let mutable exited = false
        let mutable subscription : IDisposable option = None
        let deliver error =
            let cleanup = lock gate (fun () -> subscription)
            cleanup |> Option.iter(fun token -> token.Dispose())
            postMain (fun () -> complete error)
        let finishWaiting error =
            let won = lock gate (fun () ->
                if phase = 0 then phase <- 2; true else false)
            if won then deliver error
        let onExit () =
            let waiting = lock gate (fun () ->
                exited <- true
                if phase = 0 then phase <- 2; true else false)
            if waiting then deliver gone
        try
            let token = subscribe onExit
            let alreadyFinished = lock gate (fun () ->
                subscription <- Some token
                phase = 2)
            if alreadyFinished then token.Dispose()
            elif isExited() then finishWaiting gone
            else
                let work () =
                    let run = lock gate (fun () ->
                        if phase = 0 then phase <- 1; true else false)
                    if run then
                        let mutable error = None
                        try
                            try action()
                            with e -> error <- Some e
                        finally
                            let result = lock gate (fun () ->
                                phase <- 2
                                if exited then error |> Option.orElse gone else error)
                            deliver result
                if not (tryPost work) then finishWaiting gone
        with error -> finishWaiting (Some error)

    // Continuations serialize restore steps without blocking their owner threads.
    // Dispatch is supplied by the caller; tests can drain a queue without Windows.
    let runWorkspaceSteps post completed (steps: ((exn option -> unit) -> unit) list) =
        let mutable finished = false
        let finish error =
            if not finished then
                finished <- true
                completed error
        let rec next remaining =
            if not finished then
                match remaining with
                | [] -> finish None
                | step :: rest ->
                    let mutable reported = false
                    let report error =
                        if not reported then
                            reported <- true
                            match error with
                            | Some _ -> finish error
                            | None ->
                                try post (fun () -> next rest)
                                with error -> finish (Some error)
                    try step report with error -> report (Some error)
        next steps

    // A group's recovery completes before its callback. Its failure must not
    // discard the independent groups that remain in the workspace.
    let runWorkspaceGroups post report completed groups =
        let tolerant step complete =
            let doneGroup error =
                error |> Option.iter report
                complete None
            try step doneGroup with error -> doneGroup (Some error)
        runWorkspaceSteps post completed (groups |> List.map tolerant)

    // Only windows positively here may be anchors in a workspace z-order batch.
    // Unlike capture, uncertainty must not let an off-desktop window be raised.
    let workspaceZorder presenceOf members =
        members |> List.filter (fun hwnd -> presenceOf hwnd = Here)

    // Unreadable windows stay at the top level so a partial reading cannot
    // hide a destination. Here also includes windows shown on all desktops.
    // So does a window that names a desktop Explorer does not list: it has no
    // parent item to go under, and leaving it out would drop its group from
    // the menu altogether.
    let menuTabs current (listed: Guid list option) (reads: Map<IntPtr, WindowRead>) tabs =
        let isListed desktop = listed |> Option.exists (List.contains desktop)
        let elsewhere (hwnd, _) =
            match Map.tryFind hwnd reads with
            | Some({ presence = Away; desktop = Some desktop }) when Some desktop <> current && isListed desktop ->
                Some desktop
            | _ -> None
        let here = tabs |> List.filter (elsewhere >> Option.isNone)
        let others =
            listed |> Option.defaultValue [] |> List.mapi (fun i desktop ->
                i + 1, desktop, tabs |> List.filter (fun tab -> elsewhere tab = Some desktop))
            |> List.filter (fun (_, desktop, members) -> Some desktop <> current && not members.IsEmpty)
        here, others

    type Desktops = {
        /// The desktop being looked at, when it can be trusted (trustCurrent).
        current: Guid option
        /// Explorer's list of desktops; None when it could not be read.
        listed: Guid list option
    }

    /// Whether the registry's idea of the desktop being looked at agrees with
    /// the windows. A window that is here and not shown everywhere names the
    /// desktop being looked at; if most of those name something else, the
    /// registry has not caught up with a switch yet (or has stopped being
    /// written), and the reading is not used for anything this pass.
    ///
    /// A window that names the desktop that was being looked at before is no
    /// witness: that is what a window pinned on that desktop may answer, and
    /// it is the one answer a late registry cannot produce (a late registry
    /// still NAMES the previous desktop; the windows of the new one name the
    /// new one).
    let trustCurrent (registryCurrent: Guid option) (previousCurrent: Guid option)
                     (listed: Guid list option) (shared: Set<IntPtr>) (reads: WindowRead list) =
        match registryCurrent with
        | None -> None
        | Some current ->
            let listedHas id = match listed with Some l -> List.contains id l | None -> true
            let witnesses =
                reads
                |> List.filter (fun r -> r.presence = Here && not (shared.Contains r.hwnd))
                |> List.choose (fun r -> r.desktop)
                |> List.filter (fun id -> listedHas id && Some id <> previousCurrent)
            let agree = witnesses |> List.filter ((=) current) |> List.length
            let disagree = witnesses.Length - agree
            if listedHas current && agree >= disagree then Some current else None

    /// The windows' own word against the registry's: a window that lives only
    /// on a group's desktop, is away, and still names that desktop, says the
    /// desktop being looked at is not that one. When the registry says it is,
    /// the registry is behind.
    let contradicted (current: Guid) (reads: Map<IntPtr, WindowRead>) (shared: Set<IntPtr>)
                     (groups: (Guid option * IntPtr list) list) =
        groups |> List.exists (fun (home, members) ->
            home = Some current &&
            members |> List.exists (fun h ->
                not (shared.Contains h) &&
                (match reads.TryFind h with
                 | Some r -> r.presence = Away && r.desktop = Some current
                 | None -> false)))

    // ----------------------------------------------------------- evidence --

    /// What one reading says about whether a window is on more than one
    /// desktop.
    type Hint =
        | SaysShared
        | SaysBound
        | SaysNothing

    let hint (d: Desktops) (r: WindowRead) =
        match r.presence, r.desktop with
        // A window shown on every desktop is never away from the one being
        // looked at.
        | Away, _ -> SaysBound
        | Here, Some id ->
            match d.current, d.listed with
            | Some current, _ when id = current -> SaysBound
            // Here, and naming no desktop Explorer knows of (what a window
            // pinned on its own was observed to answer).
            | _, Some listed when not (List.contains id listed) -> SaysShared
            // Here, and naming a different desktop.
            | Some _, Some _ -> SaysShared
            | _ -> SaysNothing
        | _ -> SaysNothing

    type Evidence = {
        sharedRun: int
        boundRun: int
        /// Taken to be shown on all desktops.
        shared: bool
        /// What the window named while it was taken to be shared. A window
        /// that keeps naming the same desktop has not changed; one that names
        /// another has (an unpinned window starts naming the desktop it is
        /// left on).
        sharedId: Guid option
        /// The desktop it was last seen here on, and what it named then.
        lastHereOn: Guid option
        lastId: Guid option
        /// Every desktop it has been seen here on since it was last away.
        hereOn: Set<Guid>
        /// Taken to be shared on the strength of where it was seen, while
        /// naming whichever desktop was being looked at each time. Naming the
        /// desktop being looked at is then all it ever does, and says nothing.
        answersCurrent: bool
    }

    let noEvidence =
        { sharedRun = 0; boundRun = 0; shared = false; sharedId = None
          lastHereOn = None; lastId = None; hereOn = Set.empty; answersCurrent = false }

    /// Readings in a row that it takes to change the verdict either way. One
    /// reading taken in the middle of a switch can say anything.
    let evidenceNeeded = 2

    /// Each member's evidence after one more pass. Windows not read this pass
    /// are dropped: the caller passes every window of every group.
    ///
    /// Four things make a window count as shown on all desktops:
    ///   - it is here and names no desktop Explorer lists (hint);
    ///   - it is here and names another desktop (hint);
    ///   - it was here on one desktop, is here on another, never away in
    ///     between, and names the same thing it named before. A window that
    ///     is moved to the other desktop comes to name that desktop;
    ///   - it is here again on a desktop it was here on before, having been
    ///     here on another in between and never away. A window that was moved
    ///     and followed is away when its old desktop is looked at again.
    /// The one pin none of these catches at once is one whose answer is always
    /// the desktop being looked at: it is known from the first return.
    /// Two things undo it: being away (a pinned window never is), or naming
    /// the desktop being looked at when it used to name something else.
    let updateEvidence (d: Desktops) (previous: Map<IntPtr, Evidence>) (reads: WindowRead list) =
        let sharedStep e (id: Guid option) immediate answersCurrent =
            let run = e.sharedRun + 1
            { e with sharedRun = run; boundRun = 0
                     shared = e.shared || immediate || run >= evidenceNeeded
                     sharedId = id
                     answersCurrent = answersCurrent }
        let boundStep e =
            let run = e.boundRun + 1
            let shared = e.shared && run < evidenceNeeded
            { e with sharedRun = 0; boundRun = run; shared = shared
                     sharedId = if shared then e.sharedId else None
                     answersCurrent = shared && e.answersCurrent }
        reads
        |> List.map (fun r ->
            let e = defaultArg (previous.TryFind r.hwnd) noEvidence
            let next =
                match r.presence with
                | Unsure -> e
                | Away -> { boundStep e with lastHereOn = None; lastId = None; hereOn = Set.empty }
                | Here ->
                    let followedUs =
                        match d.current, e.lastHereOn with
                        | Some c, Some p -> c <> p && r.desktop.IsSome && r.desktop = e.lastId
                        | _ -> false
                    let returned =
                        match d.current, e.lastHereOn with
                        | Some c, Some p -> c <> p && e.hereOn.Contains c
                        | _ -> false
                    let stepped =
                        match hint d r with
                        | _ when followedUs -> sharedStep e r.desktop true false
                        | _ when returned -> sharedStep e r.desktop true (r.desktop = d.current)
                        | SaysShared -> sharedStep e r.desktop false false
                        | SaysBound when e.shared && (e.answersCurrent || r.desktop = e.sharedId) -> e
                        | SaysBound -> boundStep e
                        | SaysNothing -> e
                    match d.current with
                    | Some c -> { stepped with lastHereOn = Some c; lastId = r.desktop; hereOn = e.hereOn.Add c }
                    | None -> stepped
            r.hwnd, next)
        |> Map.ofList

    let sharedOf (evidence: Map<IntPtr, Evidence>) =
        evidence |> Seq.filter (fun kv -> kv.Value.shared) |> Seq.map (fun kv -> kv.Key) |> Set.ofSeq

    /// Historical evidence is not permission to copy when this reading is
    /// missing, unsure, or contradicts the identity observed while shared.
    let positiveShared (d: Desktops) (evidence: Map<IntPtr, Evidence>) (reads: Map<IntPtr, WindowRead>) =
        sharedOf evidence |> Set.filter (fun h ->
            match reads.TryFind h, evidence.TryFind h with
            | Some r, Some e when d.current.IsSome && r.presence = Here ->
                hint d r = SaysShared || e.answersCurrent || r.desktop = e.sharedId
            | _ -> false)

    // ------------------------------------------------------------ display --

    type Display =
        | Shown
        | Hidden

    type GroupState = {
        /// Anything that names the group to the caller.
        key: int
        /// In the order of the strip.
        members: IntPtr list
        /// The desktop the group belongs to. None until it has been seen.
        home: Guid option
        /// What was decided last time; a new group starts Shown.
        display: Display
    }

    type GroupDecision = {
        key: int
        display: Display
        home: Guid option
    }

    /// With windows kept in their group across desktops (keepAway), a window
    /// shown on all desktops needs one group only, and the copies made for it
    /// per desktop are surplus. Of two groups holding the same window, the one
    /// with more windows is the group; then the one filed under the desktop
    /// being looked at; then the earlier one.
    let betterHolder (current: Guid option) (g: GroupState) (o: GroupState) =
        let homedHere (x: GroupState) = current.IsSome && x.home = current
        o.key <> g.key &&
        (o.members.Length > g.members.Length ||
         (o.members.Length = g.members.Length &&
          ((homedHere o && not (homedHere g)) || (homedHere o = homedHere g && o.key < g.key))))

    /// The (group key, window) pairs to take out so that each window is left
    /// in its one group. Only windows read as here: nothing is decided on a
    /// window that could not be read.
    let surplusCopies (d: Desktops) (reads: Map<IntPtr, WindowRead>) (groups: GroupState list) =
        if not keepAway || d.current.IsNone then []
        else
            groups |> List.collect (fun g ->
                g.members
                |> List.filter (fun h ->
                    (match reads.TryFind h with Some r -> r.presence = Here | None -> false) &&
                    groups |> List.exists (fun o -> betterHolder d.current g o && List.contains h o.members))
                |> List.map (fun h -> g.key, h))

    /// Which groups are drawn on the desktop being looked at, and which desktop
    /// each belongs to.
    ///
    /// The rules, in the order they are tried:
    ///  1. A window that lives only on the group's desktop, away from here and
    ///     still naming that desktop, says the group's desktop is not the one
    ///     being looked at - whatever the registry says. Hidden.
    ///  2. Nowhere to stand (the current desktop is not trusted): as it was.
    ///  3. A group not filed under any desktop yet is filed here once its
    ///     windows are all here; it is drawn meanwhile, as groups always were.
    ///  4. The group of this desktop: drawn.
    ///  5. The group of another desktop, seen from here:
    ///     - a window of it is away: hidden (the ordinary case of a group on
    ///       another desktop);
    ///     - its windows here are windows shown everywhere, or windows another
    ///       group of this desktop already holds: hidden - it is the other
    ///       desktop's view of them, and this desktop has its own;
    ///     - otherwise every window of it is here and none of them is shown
    ///       everywhere: the whole group was moved here, and it is filed here
    ///       and drawn (as a group moved window by window always arrived).
    let decide (d: Desktops) (reads: Map<IntPtr, WindowRead>) (shared: Set<IntPtr>)
               (groups: GroupState list) : GroupDecision list =
        let presenceOf h = match reads.TryFind h with Some r -> r.presence | None -> Unsure
        let desktopOf h = reads.TryFind h |> Option.bind (fun r -> r.desktop)
        let homedHere (g: GroupState) =
            match d.current, g.home with
            | Some c, Some h -> c = h
            | _ -> false
        groups
        |> List.map (fun g ->
            let keep = { key = g.key; display = g.display; home = g.home }
            let here = g.members |> List.filter (fun h -> presenceOf h = Here)
            let away = g.members |> List.filter (fun h -> presenceOf h = Away)
            let leftHome =
                match g.home with
                | Some home ->
                    g.members |> List.exists (fun h ->
                        not (shared.Contains h) && presenceOf h = Away && desktopOf h = Some home)
                | None -> false
            let heldByBetter h =
                groups |> List.exists (fun o -> betterHolder d.current g o && List.contains h o.members)
            // Only a surplus copy: the group that keeps these windows draws them.
            if keepAway && not here.IsEmpty && here |> List.forall heldByBetter then
                { keep with display = Hidden }
            elif keepAway && not here.IsEmpty then
                // A window of it is here: drawn here, whichever desktop the
                // group is filed under, and whether or not that window is
                // shown on all desktops. It is filed here only once all of it
                // is here.
                let allHere =
                    away.IsEmpty && here.Length = g.members.Length &&
                    not (g.members |> List.exists shared.Contains)
                let home =
                    match d.current with
                    | Some c when g.home.IsNone || allHere -> Some c
                    | _ -> g.home
                { keep with display = Shown; home = home }
            // Every tab of it would be dimmed.
            elif keepAway && here.IsEmpty && not away.IsEmpty then { keep with display = Hidden }
            elif leftHome then { keep with display = Hidden }
            else
            match d.current, g.home with
            | None, _ -> keep
            | Some c, None ->
                if not here.IsEmpty && here.Length = g.members.Length then { keep with display = Shown; home = Some c }
                else keep
            | Some c, Some h when c = h -> { keep with display = Shown }
            | Some c, Some _ ->
                let heldHereByAnother hwnd =
                    groups |> List.exists (fun o -> o.key <> g.key && homedHere o && List.contains hwnd o.members)
                if not away.IsEmpty || here.IsEmpty then { keep with display = Hidden }
                elif here |> List.exists shared.Contains then { keep with display = Hidden }
                elif here |> List.exists heldHereByAnother then { keep with display = Hidden }
                elif g.members |> List.exists shared.Contains then { keep with display = Hidden }
                elif here.Length <> g.members.Length then keep
                else { keep with display = Shown; home = Some c })

    // ------------------------------------------------------------- visits --

    /// A group to make on the desktop being looked at: confirmed shared
    /// windows that are here, belong to a group, and have no group drawn here. `source` is the group
    /// they are taken from - its settings and per-tab state are copied, and its
    /// order kept - and it keeps them: the source is another desktop's group.
    type Visit = {
        source: int
        members: IntPtr list
    }

    /// `groups` in priority order: a window in several hidden groups is taken
    /// from the first. `eligible` is the caller's say on whether a window may be
    /// grouped at all (tabbable, not in the middle of a drop). A window that a
    /// group filed under this desktop already holds is never given another
    /// one here, whatever that group's display says: that is what keeps a
    /// registry that is behind from making a second group on the desktop it
    /// wrongly names.
    let visits (current: Guid) (reads: Map<IntPtr, WindowRead>) (shared: Set<IntPtr>) (groups: (GroupState * Display) list)
               (eligible: IntPtr -> bool) : Visit list =
        let isHere h = match reads.TryFind h with Some r -> r.presence = Here | None -> false
        let shownHolds h = groups |> List.exists (fun (g, disp) -> disp = Shown && List.contains h g.members)
        let homeHolds h = groups |> List.exists (fun (g, _) -> g.home = Some current && List.contains h g.members)
        let wanted =
            groups
            |> List.collect (fun (g, _) -> g.members)
            |> List.distinct
            |> List.filter (fun h -> shared.Contains h && isHere h && not (shownHolds h) && not (homeHolds h) && eligible h)
            |> Set.ofList
        let taken = Collections.Generic.HashSet<IntPtr>()
        groups
        |> List.choose (fun (g, disp) ->
            if disp <> Hidden then None
            else
                let ms = g.members |> List.filter (fun h -> wanted.Contains h && taken.Add h)
                if ms.IsEmpty then None else Some { source = g.key; members = ms })

    /// Bound arrivals are transferred, not copied. Reuse visit ordering and
    /// destination exclusion, but require a known, different source home.
    let moves current reads shared groups eligible =
        let d = { current = Some current; listed = None }
        let bound =
            reads |> Map.toList |> List.choose (fun (h, r) ->
                if not (Set.contains h shared) && r.presence = Here &&
                   r.desktop = Some current && hint d r = SaysBound then Some h else None)
            |> Set.ofList
        visits current reads bound groups eligible
        |> List.filter (fun v -> groups |> List.exists (fun (g, _) ->
            g.key = v.source && g.home.IsSome && g.home <> Some current))

    /// A visit is carried out only when the same plan comes out of two passes
    /// in a row. Making a group is not undone by the next reading.
    let sameVisits (a: Visit list) (b: Visit list) =
        let norm (vs: Visit list) = vs |> List.map (fun v -> v.source, v.members) |> List.sort
        not a.IsEmpty && norm a = norm b

    // ----------------------------------------------------- the strip itself --

    // Strips are permanently ownerless. Desktop visibility is our decision,
    // including groups containing a window shown on every desktop.
    let stripOwnerRequest (_requested: IntPtr) = IntPtr.Zero
    let hideStrip display = display = Hidden
    let excludeSharedDimmed (shared: Set<IntPtr>) members =
        members |> List.filter (fun h -> not (shared.Contains h))

    let dimmed shared shown presenceOf members =
        if keepAway && shown then
            members |> List.filter (fun h -> presenceOf h = Away) |> excludeSharedDimmed shared
        else []

    // Presentation retains known all-desktop windows across an incomplete read.
    // This does not grant membership or duplication permission. A definite
    // non-shared reading still removes the presentation exemption.
    let stripSharedEvidence reliable previous current (reads: Map<IntPtr, WindowRead>) =
        if not reliable then Set.union previous current else
        Set.union current (previous |> Set.filter (fun h ->
            match reads.TryFind h with
            | None -> true
            | Some r -> r.presence = Unsure))

    type StripView = {
        current: Guid option
        presence: Map<IntPtr, Presence>
        noticeTick: uint32 option
    }
    type StripMember = {
        hwnd: IntPtr
        exists: bool
        visible: bool
        minimized: bool
        cloak: int option
        presence: Presence
    }
    // The shell may cloak both desktops during its animation. A known member
    // of the selected desktop still needs its strip; application/inherited
    // cloaking and explicit hiding/minimizing must not be bypassed.
    let stripPresence (current: Guid option) shared (reading: WindowRead) cloak =
        if shared then Here else
        match cloak with
        | Some 0 -> Here
        | Some value when value &&& 2 <> 0 ->
            if not shared && current.IsSome && reading.desktop = current then Here else Away
        | Some _ -> Here
        | None -> reading.presence

    let stripMemberVisible memberState =
        memberState.exists && memberState.visible && not memberState.minimized &&
        memberState.presence = Here &&
        (match memberState.cloak with Some c -> c &&& 5 = 0 | None -> true)

    let stripVisible shown minimized members = shown && not minimized && List.exists stripMemberVisible members
    let stripFront members = members |> List.tryFind stripMemberVisible |> Option.map (fun w -> w.hwnd)

    // Keep the remaining order intact; only an eligible, opaque member can
    // lead the strip. A prepared click supplies destination eligibility.
    let stripFrontOrder (eligible: Set<IntPtr>) (dimmed: Set<IntPtr>) preferred order =
        let candidates = (preferred |> Option.toList) @ order
        let front = candidates |> List.tryFind (fun h ->
            List.contains h order && eligible.Contains h && not (dimmed.Contains h))
        let ordered =
            match front with
            | Some h -> h :: (order |> List.filter ((<>) h))
            | None -> order
        front, ordered

    type StripPrediction = {
        shown: bool
        presence: Map<IntPtr, Presence>
        dimmed: IntPtr list
        front: IntPtr option
        order: IntPtr list
    }
    let predictStrip destination (reads: Map<IntPtr, WindowRead>) (shared: Set<IntPtr>)
                     (previous: Map<IntPtr, Presence>) available shown order =
        let presence =
            order |> List.map (fun h ->
                let p =
                    if shared.Contains h then Here else
                    match reads.TryFind h |> Option.bind (fun r -> r.desktop) with
                    | Some desktop -> if desktop = destination then Here else Away
                    | None -> previous.TryFind h |> Option.defaultValue Unsure
                h, p) |> Map.ofList
        let eligible = available |> Set.filter (fun h -> presence.TryFind h = Some Here)
        let away = order |> List.filter (fun h -> presence.[h] = Away) |> excludeSharedDimmed shared
        let front, ordered = stripFrontOrder eligible (Set.ofList away) None order
        let visible =
            if not eligible.IsEmpty then true
            elif presence |> Map.exists (fun _ p -> p = Unsure) then shown
            else false
        { shown=visible; presence=presence; dimmed=away; front=front; order=ordered }

    let predictStripContent shown prediction = shown && prediction.shown && prediction.front.IsSome

    let stripForegroundInvolves (members: Set<IntPtr>) previous next =
        members.Contains previous || members.Contains next

    // The guard may sit between the strip and the frame. Its position is
    // excluded from the predecessor supplied by the caller.
    type StripStacking = KeepStacking | AboveWindow of IntPtr | TopmostLayer | NormalLayer
    let stripStacking adjacent sameLayer frontTopmost previous previousTopmost =
        if not sameLayer then
            if frontTopmost then TopmostLayer else NormalLayer
        elif adjacent then KeepStacking
        elif frontTopmost then
            if previous = IntPtr.Zero then TopmostLayer else AboveWindow previous
        elif previous = IntPtr.Zero || previousTopmost then NormalLayer
        else AboveWindow previous

    type StripPreview = { target: IntPtr; destination: Guid; expires: int64; dimmed: IntPtr list }
    let previewStrip now shown shared target (reads: WindowRead list) =
        match reads |> List.tryFind (fun r -> r.hwnd = target) with
        | Some r when keepAway && shown && r.presence = Away && r.desktop.IsSome ->
            Some { target=target; destination=r.desktop.Value; expires=now + 5000L
                   dimmed=reads |> List.choose (fun w ->
                       if w.desktop.IsSome && w.desktop <> r.desktop then Some w.hwnd else None)
                       |> excludeSharedDimmed shared }
        | _ -> None
    let keepStripPreview now valid confirmed preview =
        valid && now < preview.expires && not confirmed

    let stripPreviewConfirmed clickedAt readAt fresh current targetHere uncloaked preview =
        fresh && readAt >= clickedAt && current = Some preview.destination && targetHere && uncloaked

    let sameStripView shown previousShown (view: StripView) (previous: StripView) =
        shown = previousShown && view.current = previous.current && view.presence = previous.presence

    // -------------------------------------------------- shared rectangles --

    /// Whether a group adopts the rectangle a member window has just been
    /// given. A window shown on all desktops has one rectangle, and every
    /// group it is in shares it: moved anywhere, it is moved in all of them,
    /// the same as if it had been dragged there.
    ///   - a group drawn here follows its front window, as always, and in
    ///     addition a shared member that something else moved;
    ///   - a group drawn elsewhere follows its shared members only - a
    ///     window that lives on the other desktop is not being handled by
    ///     anyone right now, and what it does must not reach this desktop.
    let adoptsMoveOf (groupShown: bool) (isShared: bool) (isTop: bool) =
        if groupShown then isTop || isShared else isShared

    // ------------------------------------------------------------ leaving --

    /// The group's windows as the 09.21 straddle rule is to see them
    /// (VirtualDesktopIntegrity.decide). A window shown on all desktops is
    /// counted as being where the group is: it has not left, and its own
    /// answer (another desktop, or none that exists) would make it look as
    /// though it had. Without a desktop to put it on it is left out. So is,
    /// the other way round, a window that is here but will not say where it
    /// lives while the group's own desktop is the one being looked at: it is
    /// plainly where the group is, and counting it lets a neighbour that did
    /// leave be seen to.
    let straddleView (home: Guid option) (current: Guid option) (shared: Set<IntPtr>)
                     (reads: (IntPtr * Presence * Guid option) list) =
        let at hwnd desktop = { VirtualDesktopIntegrity.hwnd = hwnd; VirtualDesktopIntegrity.desktop = desktop }
        reads
        |> List.choose (fun (hwnd, presence, desktop) ->
            if shared.Contains hwnd then home |> Option.map (fun h -> at hwnd (Some h))
            else
                match desktop, presence, home with
                | None, Here, Some h when current = Some h -> Some(at hwnd (Some h))
                | _ -> Some(at hwnd desktop))

    /// The straddle rule takes the desktop most of a group is on, or failing
    /// that the front window's, as where the group is. A group that knows its
    /// own desktop is where it is: seen from another desktop, a window here in
    /// front of it (one that answers like an ordinary window although it is
    /// shown everywhere, or one moved and followed) must not make the windows
    /// that stayed home look like the ones that left.
    let rebase (home: Guid option) (view: VirtualDesktopIntegrity.WindowDesktop list)
               (decision: VirtualDesktopIntegrity.Decision) =
        match home, decision with
        | Some h, VirtualDesktopIntegrity.Straddling(b, _)
                when b <> h && view |> List.exists (fun w -> w.desktop = Some h) ->
            let strays =
                view |> List.choose (fun w ->
                    match w.desktop with
                    | Some d when d <> h -> Some w.hwnd
                    | _ -> None)
            if strays.IsEmpty then VirtualDesktopIntegrity.Settled
            else VirtualDesktopIntegrity.Straddling(h, strays)
        | _ -> decision

    /// A window no longer shown on all desktops stays in the group of the
    /// desktop it was left on and goes from every other desktop's group, as
    /// if its tab had been closed there. Known where it happens: it is here,
    /// it names the desktop being looked at, and it used to be taken for
    /// shared. Returns (group key, window) pairs to take out.
    let leftBehind (current: Guid option) (wasShared: Set<IntPtr>) (shared: Set<IntPtr>)
                   (reads: Map<IntPtr, WindowRead>) (groups: GroupState list) =
        match current with
        | None -> []
        | Some c ->
            let unpinned =
                wasShared
                |> Set.filter (fun h ->
                    not (shared.Contains h) &&
                    (match reads.TryFind h with
                     | Some r -> r.presence = Here && r.desktop = Some c
                     | None -> false))
            groups
            |> List.collect (fun g ->
                match g.home with
                | Some home when home <> c ->
                    g.members |> List.filter unpinned.Contains |> List.map (fun h -> g.key, h)
                | _ -> [])

    /// Whether a window that the straddle rule found elsewhere may be taken out
    /// of its group. Never a window that is here: a window shown on all desktops
    /// is here wherever one looks. A moved window that has not been positively
    /// distinguished from a shared one waits until its old desktop is viewed
    /// and it is read as away; then the straddle rule transfers it.
    let mayLeave (isShared: bool) (presence: Presence) =
        not isShared && presence = Away

    // ------------------------------------------- the ordinary program paths --

    /// Whether the ordinary window pass (Program.ensureWindowIsGrouped) finds a
    /// group for a window. As before, a window in a group anywhere is left
    /// alone - a window shown on all desktops gets its group on each desktop
    /// from a visit, not from auto-grouping - except a tab the user has just
    /// dropped outside every strip: it wants a group of its own here even if
    /// another desktop's group still holds it.
    let wantsGroup (dropped: bool) (inShownGroup: bool) (inAnyGroup: bool) =
        if dropped then not inShownGroup else not inAnyGroup

    /// Which windows a group rebuilt when WindowTabs is switched back on keeps
    /// (Program.setDisabled). Visibility is presentation, not identity: an
    /// ordinary cloaked or temporarily hidden member still owns its state.
    /// Only a destroyed window has left this in-memory snapshot.
    let keepOnReenable (heldSeveral: bool) (isWindow: bool) (visibleOnScreen: bool) (visible: bool) =
        isWindow

    // ---------------------------------------------------------- the file --

    let formatDesktop (g: Guid) = g.ToString("D")

    let parseDesktop (s: string) =
        match Guid.TryParse(if isNull s then "" else s) with
        | true, g when g <> Guid.Empty -> Some g
        | _ -> None

    /// A saved desktop that Explorer no longer lists belongs to nothing.
    let savedHome (listed: Guid list option) (saved: string option) =
        match saved |> Option.bind parseDesktop, listed with
        | Some g, Some l when not (List.contains g l) -> None
        | g, _ -> g

    /// Which of the restored groups a window saved in several of them goes
    /// back into. Saved in several because it was shown on all desktops; after
    /// a restart that is often no longer true (a single window's setting does
    /// not outlive the window), and a window that lives on one desktop now goes
    /// only into that desktop's group - or the first, if none is that
    /// desktop's. `homes` are the groups' indices and desktops.
    let restoreTargets (d: Desktops) (read: WindowRead option) (homes: (int * Guid option) list) =
        match homes with
        | [] | [_] -> homes |> List.map fst
        | (first, _) :: _ ->
            match read with
            | Some r when d.current.IsSome && hint d r = SaysShared -> homes |> List.map fst
            | Some({ desktop = Some wd }) ->
                match homes |> List.tryFind (fun (_, h) -> h = Some wd) with
                | Some(i, _) -> [ i ]
                | None -> [ first ]
            | _ -> [ first ]

    /// Repair any non-shared duplicate, including duplicates loaded from disk.
    /// Uncertain reads never remove membership. Prefer the window's own desktop,
    /// then stable group order when its home group no longer exists.
    let duplicateRemovals (d: Desktops) (reads: Map<IntPtr, WindowRead>) (shared: Set<IntPtr>)
                          (groups: GroupState list) =
        groups |> List.collect (fun g -> g.members) |> List.distinct
        |> List.collect (fun h ->
            match reads.TryFind h with
            | Some r when d.current.IsSome && r.desktop.IsSome && not (shared.Contains h) && hint d r = SaysBound ->
                let holders = groups |> List.filter (fun g -> List.contains h g.members)
                let keep =
                    holders |> List.tryFind (fun g -> r.desktop.IsSome && g.home = r.desktop)
                    |> Option.orElseWith (fun () -> List.tryHead holders)
                holders |> List.choose (fun g ->
                    if keep |> Option.exists (fun k -> k.key <> g.key) then Some(g.key, h) else None)
            | _ -> [])

    // ----------------------------------------------------------- live state --

    /// The group a user's action on a window is about: the one drawn here,
    /// and only failing that the first that holds it. A window shown on all
    /// desktops is in several groups, and the one the person is looking at is
    /// the one they mean.
    let groupFor (hwnd: IntPtr) (groups: (GroupState * Display) list) =
        match groups |> List.tryFind (fun (g, disp) -> disp = Shown && List.contains hwnd g.members) with
        | Some(g, _) -> Some g.key
        | None ->
            groups |> List.tryFind (fun (g, _) -> List.contains hwnd g.members) |> Option.map (fun (g, _) -> g.key)

    /// The groups a window can be put into by WindowTabs itself - auto-grouping
    /// a new window, a "new window" launch, a setting-change regroup, a closed
    /// tab coming back, a window returning to the group it left: the groups
    /// drawn here. A window being grouped is on the desktop being looked at
    /// (the ordinary pass takes no other), and a group of another desktop is
    /// one it would be an invisible tab in. Two groups holding the same window
    /// shown on all desktops rank alike to auto-grouping, and the one it
    /// happened to list first - often the other desktop's - used to win.
    let joinable (groups: ('g * Display) list) : 'g list =
        groups |> List.filter (fun (_, disp) -> disp = Shown) |> List.map fst

    /// A claimed tab may rejoin its existing group across desktops. Shared
    /// windows still belong to the group drawn here; ordinary auto-grouping
    /// uses joinable instead and never gains this exception.
    let restoreInto isShared (found: 'g option) (isShown: 'g -> bool) : 'g option =
        found |> Option.filter (fun g -> isShown g || (keepAway && not isShared))

    /// Capture a hidden destination before reserving membership, since a
    /// desktop pass can mark the group shown before its queued insertion runs.
    /// Only a claimed member of its former group takes the safe link path.
    let restoreLinked isShared inFormerGroup destinationHidden joinerAway =
        keepAway && not isShared && inFormerGroup && (destinationHidden || joinerAway)

    /// Which of the groups holding a window it is written into when the groups
    /// are saved (or set aside while WindowTabs is switched off), and whether
    /// it is marked as being on all desktops. `holders` are the groups' indices
    /// and desktops, in the order the groups are walked.
    ///   - one group: that one, unmarked, as always;
    ///   - taken to be shown on all desktops: every one of them, marked;
    ///   - otherwise it is a window that was moved to another desktop and
    ///     followed, still waiting to leave the group it left: only the group
    ///     of the desktop it names, failing that the first. It is not one
    ///     window in several groups, and must not come back as one.
    let savedMembership (isShared: bool) (windowDesktop: Guid option) (holders: (int * Guid option) list) =
        match holders with
        | [] -> [], false
        | [ (only, _) ] -> [ only ], false
        | (first, _) :: _ when not isShared ->
            match windowDesktop |> Option.bind (fun wd -> holders |> List.tryFind (fun (_, h) -> h = Some wd)) with
            | Some(i, _) -> [ i ], false
            | None -> [ first ], false
        | _ -> holders |> List.map fst, true

    /// What identifies a window beyond its handle, which Windows hands out
    /// again once the window is gone: its process, its thread and the
    /// executable. Taken when the window joins a group.
    type Identity = {
        pid: int
        tid: int
        exe: string
    }

    /// Whether a member may be moved because a window shown on all desktops
    /// was moved elsewhere: it is still the window that joined. A member whose
    /// identity could not be read when it joined is taken as it is (nothing
    /// to compare, as before); one that cannot be read now is gone.
    let sameWindow (joined: Identity option) (now: Identity option) =
        match joined, now with
        | None, _ -> true
        | Some _, None -> false
        | Some j, Some n -> j = n

    /// One immutable publication, so evidence and readings cannot come from
    /// different passes. Desktop results retain HRESULTs for diagnostics only.
    module Live =
        type Snapshot = {
            generation: int64
            readAt: DateTime
            shared: Set<IntPtr>
            inSeveral: Set<IntPtr>
            stripShared: Set<IntPtr>
            current: Guid option
            listed: Guid list option
            reads: Map<IntPtr, WindowRead>
            desktopResults: Map<IntPtr, int * Guid>
        }
        let private gate = obj()
        let mutable private latest = {
            generation = 0L; readAt = DateTime.MinValue
            shared = Set.empty; inSeveral = Set.empty; stripShared = Set.empty; current = None; listed = None
            reads = Map.empty; desktopResults = Map.empty }
        let mutable private invalidatedAt = DateTime.MinValue
        // Cloaking/uncloaking invalidates in-flight as well as published reads.
        let invalidate () = lock gate (fun () -> invalidatedAt <- DateTime.UtcNow)
        let freshAfter changedAt (state: Snapshot) = state.readAt >= changedAt && state.current.IsSome
        let accepts readAt = lock gate (fun () -> readAt >= invalidatedAt)
        let isFresh state = lock gate (fun () -> freshAfter invalidatedAt state)
        // Cloak notifications can invalidate readings without a desktop switch.
        // Explorer confirming the same desktop keeps that publication usable.
        let usable fresh (explorerDesktop: Guid option) (state: Snapshot) =
            state.current.IsSome && (fresh || explorerDesktop = state.current)
        let snapshot () = lock gate (fun () -> latest)
        let shared () =
            let state = snapshot()
            if isFresh state then state.shared else Set.empty
        let inSeveral () = (snapshot()).inSeveral
        let current () =
            let state = snapshot()
            if isFresh state then state.current else None
        let read (state: Snapshot) hwnd =
            state.reads |> Map.tryFind hwnd
            |> Option.defaultValue { hwnd = hwnd; presence = Unsure; desktop = None }
        let isHere state hwnd = isFresh state && (read state hwnd).presence = Here
        let canDecide state members =
            isFresh state && members |> List.forall (fun h -> (read state h).presence <> Unsure)
        let mayDuplicate state hwnd = isHere state hwnd && state.shared.Contains hwnd
        let isNewer generation (state: Snapshot) = state.generation > generation
        let confirmationReady pending state =
            pending |> Option.exists (fun generation -> isNewer generation state)
        let view state home members =
            if not (canDecide state members) then [] else
            members |> List.map (fun hwnd ->
                let r = read state hwnd
                hwnd, r.presence, r.desktop)
            |> straddleView home state.current state.shared
        let visitsAfter changedAt state groups eligible =
            match state.current with
            | Some c when freshAfter changedAt state -> visits c state.reads state.shared groups eligible
            | _ -> []
        let visitsNow state groups eligible =
            let changedAt = lock gate (fun () -> invalidatedAt)
            visitsAfter changedAt state groups eligible
        let movesAfter changedAt state groups eligible =
            match state.current with
            | Some c when freshAfter changedAt state -> moves c state.reads state.shared groups eligible
            | _ -> []
        let movesNow state groups eligible =
            let changedAt = lock gate (fun () -> invalidatedAt)
            movesAfter changedAt state groups eligible
        let mayMove state (home: Guid option) hwnd =
            isHere state hwnd && not (state.shared.Contains hwnd) &&
            home.IsSome && home <> state.current && (read state hwnd).desktop = state.current
        let publish s several current listed readAt reads desktopResults =
            lock gate (fun () ->
                latest <- {
                    generation = latest.generation + 1L; readAt = readAt
                    shared = s; inSeveral = several; current = current; listed = listed
                    stripShared = stripSharedEvidence (current.IsSome && readAt >= invalidatedAt) latest.stripShared s reads
                    reads = reads; desktopResults = desktopResults })

        // Subscribers only post immutable requests to their own group queues.
        // No publication invokes a group's cells or waits for its painting.
        type PredictionRequest = {
            id: int64; destination: Guid; state: Snapshot; requestedAt: DateTime
            tick: int; source: string; origin: IntPtr; issued: int64
        }
        let private predictionClock = Diagnostics.Stopwatch.StartNew()
        let private predictionEvents = Event<PredictionRequest>()
        let mutable private predictionId = 0L
        let mutable private lastPrediction : PredictionRequest option = None
        let predictions = predictionEvents.Publish
        let predictionAge request = predictionClock.ElapsedMilliseconds - request.issued
        let requestPrediction destination source origin =
            let request = lock gate (fun () ->
                match lastPrediction with
                | Some p when source = "registry" && p.destination = destination && predictionAge p < 5000L -> None
                | _ ->
                    predictionId <- predictionId + 1L
                    let p = { id=predictionId; destination=destination; state=latest
                              requestedAt=DateTime.UtcNow
#if DEBUG
                              tick=Environment.TickCount
#else
                              tick=0
#endif
                              source=source
                              origin=origin; issued=predictionClock.ElapsedMilliseconds }
                    lastPrediction <- Some p
                    Some p)
            request |> Option.iter (fun p ->
                VirtualDesktopTrace.noticeSwitch()
                VirtualDesktopTrace.handover (fun () ->
                    sprintf "prediction-known id=%d destination=%A source=%s knownTick=%d" p.id destination source p.tick)
                Threading.ThreadPool.QueueUserWorkItem(fun _ -> predictionEvents.Trigger(p)) |> ignore)
            request
