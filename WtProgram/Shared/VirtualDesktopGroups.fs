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
/// The groups are real groups. A window shown on all desktops is a member of
/// the group of every desktop it has been seen on, and nothing is taken apart
/// or put back together when the desktop changes: switching only changes which
/// strips are drawn. That is what lets the strip of the desktop being left go
/// away in the same frame as its windows do - the shell cloaks a tool window
/// together with the window that owns it (unite/vd-pin/owner-probe-CC3.log),
/// so a strip owned by a window that lives on one desktop only is hidden and
/// shown by the shell itself.
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
            if leftHome then { keep with display = Hidden }
            else
            match d.current, g.home with
            | None, _ -> keep
            | Some c, None ->
                if not here.IsEmpty && away.IsEmpty then { keep with display = Shown; home = Some c }
                else keep
            | Some c, Some h when c = h -> { keep with display = Shown }
            | Some c, Some _ ->
                let heldHereByAnother hwnd =
                    groups |> List.exists (fun o -> o.key <> g.key && homedHere o && List.contains hwnd o.members)
                if not away.IsEmpty || here.IsEmpty then { keep with display = Hidden }
                elif here |> List.exists shared.Contains then { keep with display = Hidden }
                elif here |> List.exists heldHereByAnother then { keep with display = Hidden }
                elif g.members |> List.exists shared.Contains then { keep with display = Hidden }
                else { keep with display = Shown; home = Some c })

    // ------------------------------------------------------------- visits --

    /// A group to make on the desktop being looked at: windows that are here,
    /// belong to a group, and have no group drawn here. `source` is the group
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
    let visits (current: Guid) (reads: Map<IntPtr, WindowRead>) (groups: (GroupState * Display) list)
               (eligible: IntPtr -> bool) : Visit list =
        let isHere h = match reads.TryFind h with Some r -> r.presence = Here | None -> false
        let shownHolds h = groups |> List.exists (fun (g, disp) -> disp = Shown && List.contains h g.members)
        let homeHolds h = groups |> List.exists (fun (g, _) -> g.home = Some current && List.contains h g.members)
        let wanted =
            groups
            |> List.collect (fun (g, _) -> g.members)
            |> List.distinct
            |> List.filter (fun h -> isHere h && not (shownHolds h) && not (homeHolds h) && eligible h)
            |> Set.ofList
        let taken = Collections.Generic.HashSet<IntPtr>()
        groups
        |> List.choose (fun (g, disp) ->
            if disp <> Hidden then None
            else
                let ms = g.members |> List.filter (fun h -> wanted.Contains h && taken.Add h)
                if ms.IsEmpty then None else Some { source = g.key; members = ms })

    /// A visit is carried out only when the same plan comes out of two passes
    /// in a row. Making a group is not undone by the next reading.
    let sameVisits (a: Visit list) (b: Visit list) =
        let norm (vs: Visit list) = vs |> List.map (fun v -> v.source, v.members) |> List.sort
        not a.IsEmpty && norm a = norm b

    // ----------------------------------------------------- the strip itself --

    /// The window that owns a group's strip. The shell shows and hides an owned
    /// tool window together with its owner, so a strip owned by a window that
    /// lives on one desktop only disappears in the same frame as that desktop
    /// does. The front window owns it as always, unless it is shown on every
    /// desktop and the group has a window that is not: then that one does.
    let stripOwner (zorder: IntPtr list) (shared: Set<IntPtr>) =
        match zorder with
        | [] -> None
        | top :: _ when not (shared.Contains top) -> Some top
        | top :: _ ->
            Some(zorder |> List.tryFind (shared.Contains >> not) |> Option.defaultValue top)

    /// Whether WindowTabs has to hide a group's strip itself. Only when the
    /// group is not drawn here AND its owner is here - otherwise the shell has
    /// already hidden it with its owner, and leaving it alone is what lets it
    /// come back in the same frame as its desktop.
    let hideStrip (display: Display) (owner: Presence) =
        display = Hidden && owner = Here

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
    /// is here wherever one looks, and a window that was moved to the desktop
    /// being looked at is given a group here by a visit and leaves its old
    /// group only when that group's desktop is looked at and it is not there.
    let mayLeave (isShared: bool) (presence: Presence) =
        not isShared && presence <> Here

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
    /// (Program.setDisabled). As before, the ones that exist and are on
    /// screen; a group that held a window shown on all desktops keeps its
    /// windows on another desktop too (visible, only cloaked there).
    let keepOnReenable (heldSeveral: bool) (isWindow: bool) (visibleOnScreen: bool) (visible: bool) =
        isWindow && (visibleOnScreen || (heldSeveral && visible))

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
            | Some r when hint d r = SaysShared -> homes |> List.map fst
            | Some({ desktop = Some wd }) ->
                match homes |> List.tryFind (fun (_, h) -> h = Some wd) with
                | Some(i, _) -> [ i ]
                | None -> [ first ]
            | _ -> [ first ]

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

    /// A closed tab's former group, as the restore may use it: only while it is
    /// drawn here. One drawn elsewhere is treated like one that is gone - the
    /// tab's name, colours and pin still come back, and grouping falls through
    /// to the ordinary rules - so a record is never refused, only its group.
    let restoreInto (found: 'g option) (isShown: 'g -> bool) : 'g option =
        found |> Option.filter isShown

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

    /// What the main thread's pass last found, for the group threads to read:
    /// the windows taken to be shown on all desktops, the windows that are
    /// members of more than one group, and the desktop being looked at (when
    /// it could be trusted). Written by that pass only.
    module Live =
        let private gate = obj()
        let mutable private sharedSet : Set<IntPtr> = Set.empty
        let mutable private severalSet : Set<IntPtr> = Set.empty
        let mutable private currentDesktop : Guid option = None
        let shared () = lock gate (fun () -> sharedSet)
        let inSeveral () = lock gate (fun () -> severalSet)
        let current () = lock gate (fun () -> currentDesktop)
        let publish (s: Set<IntPtr>) (several: Set<IntPtr>) (current: Guid option) =
            lock gate (fun () ->
                sharedSet <- s
                severalSet <- several
                currentDesktop <- current)
