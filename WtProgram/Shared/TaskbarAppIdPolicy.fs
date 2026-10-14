namespace Bemo

open System

/// Pure decisions for grouping taskbar buttons; native access lives in TaskbarAppId.
///
/// What the taskbar does (Windows 11):
/// - a window joins the button of the AppUserModelID set on its property store,
///   usually without any rebuild of its button;
/// - the name and icon of a button come from the first window that received its
///   ID, and stay when that window leaves again; only a new ID picks again;
/// - writing back the value the window had before (or VT_EMPTY when it had
///   none) gives it back the button it had before;
/// - every change of the ID sends the window TaskbarButtonCreated, rebuilt or
///   not: an application may answer by setting its own ID again;
/// - the button of each ID is listed by UI Automation with the AutomationId
///   "Appid: <ID>" and a name ending in "- <n> ..." (n windows) while buttons
///   are combined.
module TaskbarAppIdPolicy =
    let prefix = "WindowTabs.Group."

    // Fixed sentinel GUIDs returned by Windows virtual desktops for pinned windows/apps.
    let windowPinnedDesktop = Guid("c2ddea68-66f2-4cf9-8264-1bfd00fbbbac")
    let appPinnedDesktop = Guid("bb64d5b7-4de3-4ab2-a87c-db7601aea7dc")

    /// App pinning belongs to the original AppUserModelID; replacing it breaks pinning.
    let isAppPinned desktop = desktop = Some appPinnedDesktop

    /// An ID this feature gives out. Nothing else uses the prefix.
    let isOurs (value: string option) =
        value |> Option.exists (fun id -> id.StartsWith(prefix, StringComparison.Ordinal))

    /// The value to give back later. An ID of ours found on a window is a
    /// leftover with an unknown original; it must not be treated as VT_EMPTY.
    let originalOf (value: string option) = if isOurs value then None else Some value

    /// Off and untouched, nothing is computed and nothing is posted. Nothing is
    /// decided while tab monitoring is suspended (startup restore, desktop
    /// transfers): the next pass after it sees the settled groups at once.
    let shouldPass enabled holding suspended = (enabled || holding) && not suspended

    /// The desktop a window counts for. Guid.Empty stands for "no virtual desktops".
    type Place =
        | OnDesktop of Guid
        | Unknown

    /// `here`: Some true on the desktop being looked at, Some false away, None
    /// when the two public signals disagree. `desktop`: GetWindowDesktopId.
    /// For about 15 ms after a button rebuild a window reads as not here with
    /// no desktop (GUID_NULL); that reading, like any without a desktop, is
    /// Unknown and decides nothing.
    let placeOf (current: Guid option) (read: (bool option * Guid option) option) =
        let known = function Some d when d <> Guid.Empty -> Some d | _ -> None
        match read with
        | Some(Some true, desktop) ->
            match current, known desktop with
            | Some c, _ -> OnDesktop c
            | None, Some d -> OnDesktop d
            | None, None -> Unknown
        | Some(_, desktop) ->
            match known desktop with
            | Some d -> OnDesktop d
            | None -> Unknown
        | None -> Unknown

    type SlotKey = { group: IntPtr; desktop: Guid }

    /// GUID_NULL is not a virtual desktop ID. Shared-only slots use it on
    /// every desktop; without virtual desktops the same key is already used.
    let sharedDesktop = Guid.Empty

    /// Every nonempty slot gets a button. Shared windows join one shown group
    /// (lowest strip handle breaks ties), on the current desktop only.
    /// Bound tabs lead so switching shared members does not rename the button.
    let slots (groups: (IntPtr * IntPtr list) list) (excluded: Set<IntPtr>)
              (placeOf: IntPtr -> Place) (previous: Map<IntPtr, SlotKey>) (refused: Set<Set<IntPtr>>)
              (shared: Set<IntPtr>) (shown: Set<IntPtr>) (current: Guid option) =
        let memberships =
            groups |> List.collect (fun (g, windows) -> List.distinct windows |> List.map (fun h -> h, g))
                   |> List.groupBy fst |> Map.ofList
        let owner h =
            memberships.[h] |> List.map snd
            |> List.sortBy (fun g -> (if shown.Contains g then 0 else 1), g.ToInt64()) |> List.head
        let boundPlace group h =
            match placeOf h with
            | OnDesktop d -> Some { group = group; desktop = d }
            | Unknown -> previous.TryFind h |> Option.filter (fun key -> key.group = group)
        groups
        |> List.collect (fun (group, windows) ->
            let eligible = windows |> List.distinct |> List.filter (fun h -> not (excluded.Contains h))
            let bound =
                eligible |> List.filter (fun h -> not (shared.Contains h) && memberships.[h].Length = 1)
                         |> List.choose (fun h -> boundPlace group h |> Option.map (fun key -> key, h))
            let sharedKey =
                match current with
                | Some d when bound |> List.exists (fun (key, _) -> key.desktop = d) ->
                    { group = group; desktop = d }
                | None ->
                    eligible |> List.filter shared.Contains
                             |> List.tryPick (fun h -> previous.TryFind h |> Option.filter (fun key -> key.group = group))
                             |> Option.defaultValue { group = group; desktop = sharedDesktop }
                | _ -> { group = group; desktop = sharedDesktop }
            let common =
                eligible |> List.filter (fun h -> shared.Contains h && owner h = group)
                         |> List.map (fun h -> sharedKey, h)
            bound @ common)
        |> List.groupBy fst
        |> List.map (fun (key, pairs) -> key, pairs |> List.map snd)
        |> List.filter (fun (_, members) -> not (refused.Contains(Set.ofList members)))
        |> Map.ofList

    /// The ID of a button and the window that chose its name and icon.
    type Slot = { id: string; leader: IntPtr }

    type Step =
        | Apply of IntPtr * string
        | Restore of IntPtr

    /// Restores first, then each slot in tab order. A slot whose first tab
    /// changed gets a new ID, given to the new first tab before the others, so
    /// that the taskbar names the button after it. The other windows move to
    /// the new ID directly; none passes through its own button on the way.
    /// Otherwise only windows that do not carry the slot's ID yet are written.
    let plan (newId: unit -> string) (previous: Map<SlotKey, Slot>)
             (carried: Map<IntPtr, string>) (desired: Map<SlotKey, IntPtr list>) =
        let next =
            desired |> Map.map (fun key members ->
                let leader = List.head members
                match previous.TryFind key with
                | Some slot when slot.leader = leader -> slot
                | _ -> { id = newId(); leader = leader })
        let wanted = desired |> Map.toList |> List.collect snd |> Set.ofList
        let restores =
            carried |> Map.toList |> List.choose (fun (h, _) -> if wanted.Contains h then None else Some(Restore h))
        let applies =
            desired |> Map.toList |> List.collect (fun (key, members) ->
                let id = next.[key].id
                members |> List.choose (fun h -> if carried.TryFind h = Some id then None else Some(Apply(h, id))))
        next, restores @ applies

    /// Writes wait until a slot reads the same on two passes in a row. While
    /// groups are still being put together (the startup restore, windows that
    /// open late and are claimed one by one, a tab being dragged along the
    /// strip) every change of a first tab would be a new ID, and every new ID
    /// a TaskbarButtonCreated to each of its windows and a button moved to
    /// the end of the taskbar. Meanwhile a slot that is written already keeps
    /// its ID and its name: the windows that still carry it stay, its first
    /// tab first; windows that left are given back at once.
    /// Returns the next slots, the steps, and whether a slot is still waiting.
    let settledPlan (newId: unit -> string) (lastDesired: Map<SlotKey, IntPtr list>) (previous: Map<SlotKey, Slot>)
                    (carried: Map<IntPtr, string>) (desired: Map<SlotKey, IntPtr list>) =
        let kept =
            desired |> Map.toList |> List.choose (fun (key, members) ->
                if lastDesired.TryFind key = Some members then None
                else
                    match previous.TryFind key with
                    | None -> None
                    | Some slot ->
                        match members |> List.filter (fun h -> carried.TryFind h = Some slot.id) with
                        | [] -> None
                        | carrying ->
                            let ordered =
                                if List.contains slot.leader carrying then slot.leader :: List.filter ((<>) slot.leader) carrying
                                else carrying
                            Some(key, (slot, ordered)))
            |> Map.ofList
        let effective =
            desired |> Map.filter (fun key members -> lastDesired.TryFind key = Some members || kept.ContainsKey key)
                    |> Map.map (fun key members -> match kept.TryFind key with Some(_, ordered) -> ordered | None -> members)
        // A waiting slot keeps its ID even when the window that named it left.
        let previousForPlan =
            kept |> Map.fold (fun (m: Map<SlotKey, Slot>) key (slot, ordered) -> m.Add(key, { slot with leader = List.head ordered })) previous
        let next, steps = plan newId previousForPlan carried effective
        // ... and remembers that window, so a new first tab still renames it once settled.
        let next = next |> Map.map (fun key slot -> match kept.TryFind key with Some(original, _) -> original | None -> slot)
        let waiting = desired |> Map.exists (fun key members -> lastDesired.TryFind key <> Some members)
        next, steps, waiting

    /// Shared membership follows an accepted desktop hand-over immediately
    /// when the bound tab order is unchanged. Do not return it between slots.
    let settledSharedPlan newId (lastDesired: Map<SlotKey, IntPtr list>) previous carried desired (shared: Set<IntPtr>) =
        let bound members = members |> List.filter (fun h -> not (shared.Contains h))
        let accepted =
            desired |> Map.fold (fun (last: Map<SlotKey, IntPtr list>) key members ->
                if bound members = bound (defaultArg (lastDesired.TryFind key) []) then last.Add(key, members)
                else last) lastDesired
        settledPlan newId accepted previous carried desired

    /// A returning bound tab must not create a temporary shared-only button.
    let deferReturningGroups groups (returning: Set<IntPtr>) (shared: Set<IntPtr>) desired =
        let blocked = groups |> List.choose (fun (g, members) ->
            if members |> List.exists (fun h -> returning.Contains h && not (shared.Contains h)) then Some g else None) |> Set.ofList
        desired |> Map.filter (fun key members ->
            not (blocked.Contains key.group && List.forall shared.Contains members))

    /// A shared window still eligible in its group keeps its current ID while
    /// a returning bound member or a new destination is settling.
    let retainWaitingShared (shared: Set<IntPtr>) (filed: Map<IntPtr, SlotKey>)
                            (desired: Map<SlotKey, IntPtr list>) eligible steps =
        let retained =
            eligible |> Map.toList |> List.collect (fun (key, members) ->
                members |> List.filter (fun h ->
                    shared.Contains h &&
                    (desired.ContainsKey key || (filed.TryFind h |> Option.exists (fun old -> old.group = key.group)))))
            |> Set.ofList
        steps |> List.filter (function Restore h -> not (retained.Contains h) | _ -> true)

    /// Remember dormant shared-only buttons while their group and leader survive.
    let retainSharedSlots groups (shared: Set<IntPtr>) previous next =
        previous |> Map.fold (fun (state: Map<SlotKey, Slot>) key slot ->
            if key.desktop = sharedDesktop && not (state.ContainsKey key) && shared.Contains slot.leader &&
               (groups |> List.exists (fun (g, members) -> g = key.group && List.contains slot.leader members))
            then state.Add(key, slot) else state) next

    type Rewrite =
        | Rewrite
        | ApplicationChanged
        | Unreadable

    // ------------------------------------------------------- verification --

    /// Button AutomationIds look like "Appid: <AppUserModelID>".
    let buttonAppId (automationId: string) =
        if not (isNull automationId) && automationId.StartsWith("Appid: ", StringComparison.Ordinal) then
            Some(automationId.Substring(7))
        else None

    /// The window count at the end of a button's name ("App - 3 running windows",
    /// also followed by a pinned mark). Only the digits are looked at; the words
    /// are localized. A name without one is a button of one window.
    let windowCount (name: string) =
        if isNull name then None else
        let i = name.LastIndexOf(" - ", StringComparison.Ordinal)
        if i < 0 then None else
        let rest = name.Substring(i + 3)
        let digits = rest |> Seq.takeWhile Char.IsDigit |> Seq.length
        if digits = 0 || digits > 4 || (digits < rest.Length && rest.[digits] <> ' ') then None
        else Some(int (rest.Substring(0, digits)))

    /// Stands for an ID with two or more buttons on one taskbar: whatever the
    /// counts, its windows are not under one button.
    let splitButton = -1

    /// Windows per WindowTabs ID, from the buttons of each taskbar (one list per
    /// monitor); None when nothing can be counted: no button carried an
    /// AutomationId of that form, or buttons are not combined (their names
    /// then hold window titles, whose digits are no count). With `split`, each
    /// window shows on one taskbar only and the taskbars add up; otherwise
    /// every taskbar (or the main one) lists all windows.
    let countsById combined split (trays: (string * string) list list) =
        let perTray =
            trays |> List.map (fun buttons ->
                buttons |> List.choose (fun (automationId, name) ->
                    buttonAppId automationId |> Option.map (fun id -> id, name)))
        if not combined || perTray |> List.forall List.isEmpty then None
        else
            perTray
            |> List.map (fun ours ->
                ours
                |> List.filter (fun (id, _) -> id.StartsWith(prefix, StringComparison.Ordinal))
                |> List.groupBy fst
                |> List.map (fun (id, named) ->
                    id, (match named with
                         | [ (_, n) ] -> defaultArg (windowCount n) 1
                         | _ -> splitButton)))
            |> List.concat
            |> List.groupBy fst
            |> List.map (fun (id, counts) ->
                let counts = counts |> List.map snd
                id, (if List.contains splitButton counts then splitButton
                     elif split then List.sum counts
                     else List.max counts))
            |> Map.ofList
            |> Some

    /// Explorer's MMTaskbarMode: 2 shows a window's button only on the taskbar
    /// of its monitor. 0 (all taskbars) and 1 (main and own) list it on one
    /// taskbar in full.
    let splitAcrossTaskbars (mode: int option) = mode = Some 2

    /// Explorer's TaskbarGlomLevel: 0 (or absent) always combines buttons.
    let alwaysCombined (glom: int option) = glom.IsNone || glom = Some 0

    type Verdict =
        /// At least as many windows as expected are under one button.
        | Merged
        /// Windows whose value is no longer ours: the application set its own
        /// again. They leave the button for good, and the slot is planned anew
        /// without them (its first tab may change, and with it the ID).
        | Declined of IntPtr list
        /// Fewer: rebuild the buttons once.
        | RebuildOnce
        /// Still fewer after the rebuild, and no window can be told apart:
        /// the slot is given back, and this set of windows is not tried again.
        | Short
        /// The taskbar cannot be counted (not combined, or not readable):
        /// nothing is rebuilt on a guess; the slot stays as written.
        | Unverified

    /// `values`: each member's current value, None where it could not be read.
    /// Members are only read when the count falls short.
    let verdict rebuilt expected (counts: Map<string, int> option) id
                (values: unit -> (IntPtr * string option option) list) =
        let count = counts |> Option.map (fun m -> defaultArg (m.TryFind id) 0)
        match count with
        | Some n when n >= expected && n <> splitButton -> Merged
        | _ ->
            let off = values () |> List.choose (fun (h, v) -> match v with Some v when v <> Some id -> Some h | _ -> None)
            if not off.IsEmpty then Declined off
            elif count.IsNone then Unverified
            elif not rebuilt then RebuildOnce
            else Short

    /// A window given its own value back, checked on the button of the ID it
    /// carried: that button must not list more windows than still carry the ID.
    type GivenVerdict =
        | Refiled
        | RebuildGiven
        /// Still listed after one rebuild: nothing more is tried.
        | GivenStuck
        /// Nothing to compare: preserve the record without guessing completion.
        | GivenUnverified

    let givenVerdict rebuilt (counts: Map<string, int> option) (id: string option) (holders: int) =
        match counts, id with
        | Some counts, Some id ->
            let n = defaultArg (counts.TryFind id) 0
            if n <> splitButton && n <= holders then Refiled
            elif not rebuilt then RebuildGiven
            else GivenStuck
        | _ -> GivenUnverified

    /// Explorer takes about 150 ms to refile a window; a check needs a later
    /// desktop pass and at least this much time since the last write.
    let settleMs = 500

    let settled (now: int) (writtenAt: int) = now - writtenAt >= settleMs

    // ------------------------------------------------------- recovery file --

    /// A window that carries (Applied) or carried (Returned, its own value
    /// written back but its button not yet seen refiled) an ID of ours: the
    /// window, its process and the process creation time, which together
    /// identify it across a crash; its own value, encrypted (see TaskbarAppId)
    /// or None when it had none; exact current/previous assigned IDs and an
    /// optional window-generation cookie. Legacy rows have no cookie.
    type Phase =
        | Applied
        | Returned

    /// Pin records promotion intent before any mutation, including a crash before PinView.
    type Written = { hwnd: int64; pid: uint32; started: int64; phase: Phase; secret: string option; id: string option; previous: string option; cookie: int option; pin: bool }

    let private phaseText = function Applied -> "A" | Returned -> "R"

    let private idText (id: string option) =
        match id with
        | Some id when isOurs (Some id) -> id.Substring(prefix.Length)
        | _ -> "-"

    let format (entries: Written list) =
        entries
        |> List.map (fun e ->
            sprintf "%X %X %X %s %s %s %s %s %s" e.hwnd e.pid e.started (phaseText e.phase) (defaultArg e.secret "-") (idText e.id) (idText e.previous) (e.cookie |> Option.map string |> Option.defaultValue "-") (if e.pin then "P" else "-"))
        |> String.concat "\r\n"

    /// The entries, and whether any line could not be read. Lines of the two
    /// earlier forms are read too (five fields without the ID; three fields
    /// without the own value, preserved as unknown rather than empty).
    let parse (text: string) =
        if isNull text then [], false else
        let lines = text.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries) |> Array.toList
        let entry (h: string) (p: string) (s: string) phase secret id =
            try
                Some { hwnd = Convert.ToInt64(h, 16); pid = Convert.ToUInt32(p, 16); started = Convert.ToInt64(s, 16)
                       phase = phase; secret = secret; id = id; previous = None; cookie = None; pin = false }
            with _ -> None
        let phaseOf = function "A" -> Some Applied | "R" -> Some Returned | _ -> None
        let secretOf (text: string) = if text = "-" then None else Some text
        let entries =
            lines |> List.choose (fun line ->
                let fields = line.Trim().Split(' ')
                let pin, fields =
                    if fields.Length = 9 then
                        (match fields.[8] with "P" -> Some true | "-" -> Some false | _ -> None), fields.[0..7]
                    else Some false, fields
                let parsed =
                    match fields with
                    | [| h; p; s; phase; secret; id; previous; cookie |] ->
                        try
                            phaseOf phase |> Option.bind (fun phase ->
                                entry h p s phase (secretOf secret) (if id = "-" then None else Some(prefix + id)))
                            |> Option.map (fun e -> { e with previous = (if previous = "-" then None else Some(prefix + previous)); cookie = (if cookie = "-" then None else Some(Int32.Parse cookie)) })
                        with _ -> None
                    | [| h; p; s; phase; secret; id |] ->
                        phaseOf phase |> Option.bind (fun phase ->
                            entry h p s phase (secretOf secret) (if id = "-" then None else Some(prefix + id)))
                    | [| h; p; s; phase; secret |] ->
                        phaseOf phase |> Option.bind (fun phase -> entry h p s phase (secretOf secret) None)
                    | [| h; p; s |] -> entry h p s Applied (Some "unknown-original") None
                    | _ -> None
                match pin, parsed with
                | Some pin, Some entry -> Some { entry with pin = pin }
                | _ -> None)
        entries, entries.Length <> lines.Length

    /// Whether a listed window is still the window that was listed.
    type Identity =
        | Same
        | Gone
        /// Alive in the same process, whose creation time cannot be read now:
        /// neither touched nor forgotten.
        | Undecided

    let identity (entry: Written) alive pid (started: int64 option) =
        if not alive || pid <> entry.pid then Gone
        else
            match started with
            | Some s when s = entry.started -> Same
            | Some _ -> Gone
            | None -> Undecided

    /// Recover an identifiable handle even when the remaining fields are damaged.
    let damagedHandles (text: string) =
        if isNull text then Set.empty else
        text.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.choose (fun line ->
            let entries, damaged = parse line
            if not damaged then None else
            try Some(nativeint (Convert.ToInt64(line.Trim().Split(' ').[0], 16))) with _ -> None)
        |> Set.ofArray

    let ownsGeneration (entry: Written) current =
        match current with
        | Some id -> entry.id = Some id || entry.previous = Some id
        | None -> false

    /// A promotion intent is durable before the ID changes. It never grants
    /// permission to unpin a restored app ID or an unrelated generation.
    let unpinBeforeReturn (entry: Written) current desktop =
        entry.pin && ownsGeneration entry current && desktop = Some windowPinnedDesktop

    let pinReturnVerified (entry: Written) desktop =
        not entry.pin || isAppPinned desktop

    /// The live check also covers an app pin enabled after preparation.
    let pinWriteAllowed pin rotating desktop =
        if pin then
            if rotating then desktop = Some windowPinnedDesktop else isAppPinned desktop
        else desktop.IsSome && not (isAppPinned desktop)

    type CountReading = Counted of Map<string, int> | Configuration | TemporaryFailure
    let countMap = function Counted m -> Some m | _ -> None
    let retryCount retried = function TemporaryFailure -> not retried | _ -> false
    let includeSecondary enabled glom = enabled = Some 1 && alwaysCombined glom

    /// Rechecked after durable preparation, immediately before the native write.
    let canApply same original current = same && current = Some original

    let rewriteGeneration expected current =
        match current with
        | None -> Unreadable
        | Some value when value = Some expected -> Rewrite
        | Some _ -> ApplicationChanged

    /// Releasing a return gate is a one-shot reason to plan the same snapshot again.
    let resumeAfterReturn enabled completed = enabled && completed

    /// Disable continues normal observations; only process exit skips settling.
    let givenCheckDue final now writtenAt = final || settled now writtenAt
    let retryGivenCount final retried reading = not final && retryCount retried reading

    /// Called only after window identity and the original value were verified.
    let returnWithoutShell retried = function
        | Configuration -> true
        | TemporaryFailure -> retried
        | Counted _ -> false

    /// A missing registry desktop is not proof that any particular window is here.
    let verificationHere current desktop allMembersHere =
        match current with
        | Some d -> desktop = sharedDesktop || d = desktop
        | None -> allMembersHere ()

    /// Native desktop membership can wobble immediately after a property write
    /// or a rebuild. Keep the existing settling observation pending meanwhile.
    let needsSettledObservation now writtenAt isHere = not (settled now writtenAt) || isHere ()

    type PinObservation = PinReady | PinIssue | PinWait | PinExpired

    /// Observe on existing hand-overs; each command gets one settling interval.
    let pinObservation now at issued desktop =
        if issued && desktop = Some windowPinnedDesktop then PinReady
        elif not issued && (desktop |> Option.exists (fun d -> d <> Guid.Empty && d <> appPinnedDesktop && d <> windowPinnedDesktop)) then PinIssue
        elif settled now at then PinExpired
        else PinWait

    let returnDesktop desktop =
        if desktop = Some windowPinnedDesktop || isAppPinned desktop then Some sharedDesktop else desktop
