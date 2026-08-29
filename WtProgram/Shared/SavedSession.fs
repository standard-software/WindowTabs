namespace Bemo

open System
open Newtonsoft.Json.Linq

// Everything the session restore DECIDES, with no window handling in it.
//
// SavedTabState next to this file is one tab's fields and their JSON; this is
// the layer above: which groups go into the settings file and in what order,
// which saved tab comes back as which live window, and what may be put on it.
// Program.fs keeps only the parts that touch Windows - reading a window's
// rectangle, creating a group, writing a global map - and hands the values
// here.
//
// It is split this way because of what the second generation could not show.
// A codec check proves that a record survives being written and read; it
// cannot prove that the program writes the tabs in the order they are on
// screen, that a window which reopens where it was gets its own state back
// rather than its twin's, or that loading a file and saving it again leaves
// it alone. Those are decisions, and they were made in the middle of a method
// that needs a desktop full of windows to run. Here they are ordinary
// functions over ordinary values, so SavedSession.Tests.fsx can drive a whole
// screen's worth of groups through the real save, the real JSON text and the
// real load without WindowTabs running.
//
// The only dependencies are System and Newtonsoft, which is what lets a script
// load this very file rather than a copy of it.
module SavedSession =
    open SavedTabState

    // ---------------------------------------------------------------- file --

    // One saved group as it stands in the settings file.
    type SavedGroup = {
        // In saved tab order. The file has no index field: the sequence IS
        // the order.
        windows: SavedTab list
        tabPosition: string option
        snapMargin: bool option
    }

    // ---------------------------------------------------------------- save --

    // A saved window that is not on screen: either its application has not
    // started yet (a restore seed) or the user closed the tab. Both are held
    // in the closed-tab cache and both are written back to the file, so that a
    // group whose windows are all still to start is not replaced by the
    // nothing that is running ten seconds after boot.
    type PendingTab = {
        tab: SavedTab
        // The index it held in the saved order, which is where it goes back.
        rank: int
        // A window still waiting to start, as against a tab the user closed.
        // The two differ in how long they are kept and in what may claim them.
        isRestoreSeed: bool
    }

    // One live group on its way into the file.
    type GroupToSave = {
        // The strip's real on-screen order, and the group's own mirror of it.
        // Both, because neither is complete on its own: the mirror goes stale
        // after a pin/unpin normalization, and the strip snapshot does not yet
        // hold a window that was added moments ago. The merge below is what
        // the save actually writes, so it is here where it can be checked
        // rather than inline where it cannot.
        stripOrder: IntPtr list
        mirrorOrder: IntPtr list
        seeds: PendingTab list
        tabPosition: string option
        snapMargin: bool option
    }

    let mergedOrder (stripOrder: IntPtr list) (mirrorOrder: IntPtr list) =
        let inStrip = Set.ofList stripOrder
        stripOrder @ (mirrorOrder |> List.filter (fun h -> not (inStrip.Contains h)))

    // How long an entry may sit in the file waiting for its window. A tab the
    // user closed is kept longer than a window that has not started, because
    // reopening a closed tab is a deliberate act and starting an application
    // is not.
    let isCurrent (now: DateTime) (seedMaxAgeDays: float) (closedTabMaxAgeDays: float) (e: PendingTab) =
        let since = e.tab.seedSince |> Option.defaultValue now
        let limit = if e.isRestoreSeed then seedMaxAgeDays else closedTabMaxAgeDays
        (now - since).TotalDays <= limit

    // The entries that go back into the file at this save. Seeds are all kept;
    // closed tabs are capped, by date and not by position - entries read back
    // from the file are pushed onto the cache in the order the groups are
    // walked, so after a restart the head of the list has nothing to do with
    // which of them are the newest.
    let seedsToSave
            (now: DateTime) (seedMaxAgeDays: float) (closedTabMaxAgeDays: float)
            (closedTabSaveLimit: int) (entries: PendingTab list) =
        let current = entries |> List.filter (isCurrent now seedMaxAgeDays closedTabMaxAgeDays)
        (current |> List.filter (fun e -> e.isRestoreSeed))
        @ (current
           |> List.filter (fun e -> not e.isRestoreSeed)
           |> List.sortByDescending (fun e -> e.tab.seedSince |> Option.defaultValue DateTime.MinValue)
           |> List.truncate closedTabSaveLimit)
        |> List.sortBy (fun e -> e.rank)

    // One group's windows, with the ones that have not opened yet spliced back
    // in at the index they held. A group with nothing in it is not written at
    // all: emptying a group is the plainest way of saying it is finished with.
    let groupToSavedJson (windows: SavedTab list) (seeds: PendingTab list)
                         (tabPosition: string option) (snapMargin: bool option) =
        let arr = JArray()
        windows |> List.iter (fun t -> arr.Add(toJson t))
        seeds
        |> List.sortBy (fun e -> e.rank)
        |> List.iter (fun e ->
            let at = if e.rank >= 0 && e.rank < arr.Count then e.rank else arr.Count
            arr.Insert(at, toJson e.tab))
        if arr.Count = 0 then None else Some(groupToJson arr tabPosition snapMargin)

    // The whole of SavedTabGroupsForRestart.
    //
    // `stateOf` answers what the program knows about one live window - its
    // identity, its rectangle and its entries in the global per-window maps -
    // and None for a handle that is no longer a window. It is a function
    // because that half is the only part of the save that has to touch
    // Windows; everything else about the file is decided here.
    let write (stateOf: IntPtr -> SavedTab option) (groups: GroupToSave list) : JArray =
        let arr = JArray()
        groups |> List.iter (fun g ->
            let windows = mergedOrder g.stripOrder g.mirrorOrder |> List.choose stateOf
            groupToSavedJson windows g.seeds g.tabPosition g.snapMargin
            |> Option.iter (fun o -> arr.Add(o)))
        arr

    // ---------------------------------------------------------------- load --

    let read (groupsArray: JArray) : SavedGroup list =
        groupsArray
        |> Seq.map (fun t ->
            { windows = groupWindows t
              tabPosition = groupTabPosition t
              snapMargin = groupSnapMargin t })
        |> List.ofSeq

    // ------------------------------------------------------------- restore --

    // A window that is on screen when the restore runs.
    type LiveWindow = {
        handle: IntPtr
        exePath: string
        // Normalized the same way the saved title was.
        title: string
        // The centre of its rectangle, which is how twins are told apart.
        center: (float * float) option
    }

    let centerOfRect (rect: (int * int * int * int) option) =
        rect |> Option.map (fun (x, y, w, h) -> (float x + float w / 2.0, float y + float h / 2.0))

    // What became of one saved tab.
    type Outcome =
        // It is on screen: put it back in its group.
        | Matched
        // Its window is not there yet. The entry waits in the closed-tab cache
        // and is written back to the file at every save until it is claimed.
        | Waiting
        // It waited longer than it is kept for. Not seeded and so not written
        // back either: it leaves the file at this save.
        | Expired
        // Nothing to wait for: no identity was saved with it, or its identity
        // appears in another saved group as well and picking one of them would
        // not be an uncertain restore but a certain change to the grouping.
        | Unusable

    type PlannedTab = {
        // The entry exactly as the file holds it. Never edited: it is what
        // goes back into the file, and an entry that is trimmed here is
        // trimmed in the file at the next save even if its window never opens.
        saved: SavedTab
        rank: int
        live: IntPtr option
        // The same entry cut down to what may be put on the window it matched.
        // Alignment and pin are always in it (see below); the name and the
        // colours only when they are known to belong to this window.
        applied: SavedTab
        // Whether a claim arriving later may apply the name and the colours.
        // Recorded rather than acted on, because the entry may be claimed
        // hours after this runs.
        stateIsCertain: bool
        waitingSince: DateTime
        outcome: Outcome
    }

    type PlannedGroup = {
        tabs: PlannedTab list
        tabPosition: string option
        snapMargin: bool option
        // The saved order in the handles the file holds, which is what an
        // arrival's order arithmetic is done against.
        savedOrder: IntPtr list
        // The first saved handle, used as the group's name for as long as no
        // live group exists to point at. Never a live strip handle: the strip
        // of a group created moments ago does not reliably have one yet.
        token: IntPtr
    }

    // An entry that is still waiting, as it goes back into the settings file.
    //
    // It keeps everything the file held, and takes the moment its wait started
    // so that the next restart can tell how long it has been waiting even
    // though the entry was written by a window that was running at the time.
    // Program.fs holds these in its closed-tab cache as a ClosedTabInfo, which
    // carries drawing types this file cannot see; it builds that from this
    // record, and builds this record back from it at the next save, so what
    // survives a save-load-save cycle is what survives here.
    let pendingOfPlanned (t: PlannedTab) : PendingTab =
        { tab = { t.saved with seedSince = Some(t.waitingSince) }
          rank = t.rank
          isRestoreSeed = not t.saved.closedByUser }

    // The application path is compared without regard to case and the title
    // exactly, the two joined by a character neither can hold.
    let identityKey (exePath: string) (title: string) =
        exePath.ToLowerInvariant() + "\u001f" + title

    // A saved tab has an identity only if both halves are there. Without one
    // there is nothing to find the window by once its handle has died.
    let identityOf (t: SavedTab) =
        match t.exePath, t.windowTitle with
        | Some(exe), Some(title) when exe <> "" && title <> "" -> Some(identityKey exe title)
        | _ -> None

    // Distance between a saved rectangle and a live one, for choosing between
    // windows that are otherwise identical. A pair with no rectangle to compare
    // costs a large fixed amount rather than infinity, so that the pairs which
    // CAN be compared still decide the assignment.
    let private unknownCost = 1.0e12

    let private cost (a: (float * float) option) (b: (float * float) option) =
        match a, b with
        | Some(ax, ay), Some(bx, by) -> (ax - bx) * (ax - bx) + (ay - by) * (ay - by)
        | _ -> unknownCost

    // Every way of giving each of `n` saved tabs a different one of `candidates`
    // (as indices into it), in ascending order, so that the first assignment
    // found at a given cost is the one that keeps saved order.
    let rec private injections (n: int) (candidates: int list) : int list list =
        if n = 0 then [[]]
        else
            candidates
            |> List.collect (fun c ->
                injections (n - 1) (candidates |> List.filter ((<>) c))
                |> List.map (fun rest -> c :: rest))

    // How many indistinguishable windows may be assigned by trying every
    // arrangement. 6 saved against 6 live is 720 arrangements; beyond that the
    // count grows too fast and the greedy answer is used instead. A group with
    // seven windows of the same application AND the same title is not a case
    // worth spending seconds on.
    let private exhaustiveLimit = 6

    // Which live window each of a set of saved tabs that cannot be told apart
    // comes back as.
    //
    // They cannot be told apart by identity - same application, same title -
    // so the only evidence left is where they were: windows generally reopen
    // where they closed. Taking the nearest window for each saved tab in turn
    // is not enough, because the first tab can take the window that was the
    // only good answer for the second; the arrangement that is nearest OVERALL
    // is the one that puts every window back where it was. Ties - which is
    // what a group of windows with no saved rectangles is - fall back to saved
    // order against the order the windows were enumerated in, so the answer is
    // the same every time even when there is nothing to choose between them.
    //
    // When there are fewer live windows than saved tabs the later tabs in
    // saved order go without and wait as seeds instead.
    let assignTwins (saved: (float * float) option list)
                    (candidates: (float * float) option list) : int option list =
        let s = List.length saved
        let c = List.length candidates
        let m = min s c
        if m = 0 then saved |> List.map (fun _ -> None)
        else
            let savedArr = List.toArray saved
            let candArr = List.toArray candidates
            let chosen =
                if m <= exhaustiveLimit && c <= exhaustiveLimit then
                    injections m [0 .. c - 1]
                    |> List.map (fun pick ->
                        let total =
                            pick |> List.mapi (fun i j -> cost savedArr.[i] candArr.[j]) |> List.sum
                        (total, pick))
                    // The pick is part of the key, so equal costs are settled
                    // by the earliest candidate indices - saved order against
                    // enumeration order.
                    |> List.minBy id
                    |> snd
                else
                    // Too many to arrange: each in saved order takes the
                    // nearest window still free.
                    let free = System.Collections.Generic.HashSet<int>([0 .. c - 1])
                    [ for i in 0 .. m - 1 ->
                        let j =
                            free
                            |> Seq.sortBy (fun j -> (cost savedArr.[i] candArr.[j], j))
                            |> Seq.head
                        free.Remove(j) |> ignore
                        j ]
            let chosenArr = List.toArray chosen
            [ for i in 0 .. s - 1 -> if i < m then Some(chosenArr.[i]) else None ]

    // The value all of a set of entries agree on, or None if they do not.
    let private consensus (values: 'a option list) =
        match values |> List.distinct with
        | [ single ] -> single
        | _ -> None

    // What the restore does with every saved tab in the file.
    //
    // Called once at startup with the file as it was read and the windows that
    // are on screen at that moment. Nothing here changes anything: the caller
    // creates the groups, fills the global maps and seeds the closed-tab cache
    // from the answer.
    let plan (now: DateTime) (seedMaxAgeDays: float) (closedTabMaxAgeDays: float)
             (groups: SavedGroup list) (live: LiveWindow list) : PlannedGroup list =

        let liveByHandle = System.Collections.Generic.Dictionary<IntPtr, LiveWindow>()
        live |> List.iter (fun w -> liveByHandle.[w.handle] <- w)

        // A handle match is trusted only if the process still agrees. A handle
        // is unique only within one Windows session, and an entry now lives in
        // the file for days, so the number can belong to something else
        // entirely by the time it is read. Entries saved before exePath
        // existed are trusted as before.
        let savedHwndTrusted (t: SavedTab) =
            match liveByHandle.TryGetValue(t.hwnd) with
            | true, w ->
                (match t.exePath with
                 | Some(exe) when exe <> "" -> String.Equals(exe, w.exePath, StringComparison.OrdinalIgnoreCase)
                 | _ -> true)
            | _ -> false

        // Handles claimed by the same-session path, reserved up front so that
        // the identity fallback can never steal a window a later group still
        // matches by handle. A tab the user closed is matched on its title and
        // nothing else - its saved handle belongs to a window that no longer
        // exists, and reserving it would let an unrelated window of the same
        // application take the closed tab's name, colours and pin without its
        // title ever being looked at.
        let reserved = System.Collections.Generic.HashSet<IntPtr>()
        groups |> List.iter (fun g ->
            g.windows |> List.iter (fun t ->
                if savedHwndTrusted t && not t.closedByUser then reserved.Add(t.hwnd) |> ignore))

        // Identity matching after a Windows restart. Once every handle is new,
        // application path + title is all that is left, and it does not always
        // name one window: two terminals can both be titled "Claude1". The two
        // ambiguous cases are not equally bad.
        //   - The same identity in two DIFFERENT saved groups is not restored
        //     at all (Unusable below).
        //   - The same identity more than once inside ONE saved group is
        //     restored. Whichever live window each entry ends up matching, the
        //     resulting group membership is identical, so it is right either
        //     way.
        let savedCounts = System.Collections.Generic.Dictionary<string, int>()
        let savedGroupOf = System.Collections.Generic.Dictionary<string, int>()
        let crossGroup = System.Collections.Generic.HashSet<string>()
        groups |> List.iteri (fun gi g ->
            g.windows |> List.iter (fun t ->
                if not (reserved.Contains(t.hwnd)) then
                    identityOf t |> Option.iter (fun key ->
                        savedCounts.[key] <-
                            (match savedCounts.TryGetValue(key) with
                             | true, n -> n + 1
                             | _ -> 1)
                        match savedGroupOf.TryGetValue(key) with
                        | true, g0 when g0 <> gi -> crossGroup.Add(key) |> ignore
                        | true, _ -> ()
                        | _ -> savedGroupOf.[key] <- gi)))

        let liveCounts = System.Collections.Generic.Dictionary<string, int>()
        live |> List.iter (fun w ->
            if w.exePath <> "" && w.title <> "" then
                let key = identityKey w.exePath w.title
                liveCounts.[key] <- (match liveCounts.TryGetValue(key) with
                                     | true, n -> n + 1
                                     | _ -> 1))

        let countIn (d: System.Collections.Generic.Dictionary<string, int>) key =
            match d.TryGetValue(key) with
            | true, n -> n
            | _ -> 0

        // The identity names exactly one saved and one live window, so a name
        // or a colour put on it cannot land on the wrong window.
        let isCertainIdentity key = countIn savedCounts key = 1 && countIn liveCounts key = 1
        // Uniqueness on the saved side alone. An entry that is still waiting
        // has no live window to count - that is the whole reason it exists -
        // and asking for the live half made every window come back after a
        // reboot stripped of everything it was saved with. The live half is
        // answered later, when the entry is claimed.
        let isUniqueSaved key = countIn savedCounts key = 1

        // ---- who comes back as what ----

        let taken = System.Collections.Generic.HashSet<IntPtr>()
        // (group index, index in group) -> live handle
        let resolved = System.Collections.Generic.Dictionary<int * int, IntPtr>()

        // Same session first: a window that still carries the handle it was
        // saved with IS the window that was saved.
        groups |> List.iteri (fun gi g ->
            g.windows |> List.iteri (fun ti t ->
                if reserved.Contains(t.hwnd) && taken.Add(t.hwnd) then
                    resolved.[(gi, ti)] <- t.hwnd))

        // Then by identity, all the tabs of one identity together, because
        // which of them gets which window is one decision and not several.
        let byIdentity =
            [ for gi in 0 .. List.length groups - 1 do
                let g = groups.[gi]
                for ti in 0 .. List.length g.windows - 1 do
                    let t = g.windows.[ti]
                    match identityOf t with
                    | Some(key) when not (resolved.ContainsKey((gi, ti))) && not (crossGroup.Contains(key)) ->
                        yield (key, (gi, ti, t))
                    | _ -> () ]
            |> List.groupBy fst
            |> List.map (fun (key, xs) -> (key, xs |> List.map snd))

        byIdentity |> List.iter (fun (key, entries) ->
            let candidates =
                live
                |> List.filter (fun w ->
                    w.exePath <> "" && w.title <> "" &&
                    identityKey w.exePath w.title = key &&
                    not (reserved.Contains(w.handle)) &&
                    not (taken.Contains(w.handle)))
            let picks =
                assignTwins
                    (entries |> List.map (fun (_, _, t) -> centerOfRect t.rect))
                    (candidates |> List.map (fun w -> w.center))
            let candArr = List.toArray candidates
            List.zip entries picks
            |> List.iter (fun ((gi, ti, _), pick) ->
                pick |> Option.iter (fun j ->
                    let w = candArr.[j]
                    if taken.Add(w.handle) then resolved.[(gi, ti)] <- w.handle)))

        // ---- what may be put on them ----

        groups
        |> List.mapi (fun gi g ->
            // The name and the colours of the entries that share an identity
            // inside this group. When they all say the same thing it does not
            // matter which twin is which, and the state can be applied after
            // all - which is the common case of two terminals renamed alike.
            let sameIdentity key =
                g.windows |> List.filter (fun t -> identityOf t = Some(key))
            let tabs =
                g.windows
                |> List.mapi (fun ti t ->
                    let liveHandle =
                        match resolved.TryGetValue((gi, ti)) with
                        | true, h -> Some(h)
                        | _ -> None
                    let key = identityOf t
                    let certain =
                        match liveHandle, key with
                        // Matched by its own handle: the same window, so
                        // everything on the entry is its own.
                        | Some(h), _ when h = t.hwnd -> true
                        | Some(_), Some(k) -> isCertainIdentity k
                        | Some(_), None -> false
                        | None, Some(k) -> isUniqueSaved k
                        | None, None -> false
                    let applied =
                        if certain then t
                        else
                            // Alignment and pin are NOT held back, although
                            // they were, and holding them back is what made a
                            // jumbled group get worse at every restart instead
                            // of better. They are not decoration, they are the
                            // position: the pair decides which of the strip's
                            // four bands the tab is drawn in, and a saved order
                            // can only be honoured inside a band. They came
                            // from an entry of THIS group, so whichever way
                            // round the twins end up the group gets back the
                            // same set of (place, alignment, pin) - only
                            // possibly with two windows nobody can tell apart
                            // exchanged, which is the trade already accepted
                            // for the group membership itself. Letting them go,
                            // by contrast, loses them for good: nothing else
                            // ever restores an alignment, the tab keeps
                            // whatever default it picks up, and the next save
                            // records that instead.
                            //
                            // The name and the colours are different. A rename
                            // landing on the wrong twin is a visible mistake
                            // with no such argument behind it, so it is applied
                            // only when the twins agree about it - and it is
                            // still carried in `saved`, so an entry that is
                            // never claimed keeps it in the file.
                            let twins = match key with
                                        | Some(k) -> sameIdentity k
                                        | None -> [t]
                            { t with
                                renamedTabName = consensus (twins |> List.map (fun x -> x.renamedTabName))
                                fillColor = consensus (twins |> List.map (fun x -> x.fillColor))
                                underlineColor = consensus (twins |> List.map (fun x -> x.underlineColor))
                                borderColor = consensus (twins |> List.map (fun x -> x.borderColor)) }
                    let waitingSince = t.seedSince |> Option.defaultValue now
                    let outcome =
                        match liveHandle with
                        | Some(_) -> Matched
                        | None ->
                            match key with
                            | None -> Unusable
                            | Some(k) when crossGroup.Contains(k) -> Unusable
                            | Some(_) ->
                                let limit = if t.closedByUser then closedTabMaxAgeDays else seedMaxAgeDays
                                if (now - waitingSince).TotalDays > limit then Expired else Waiting
                    // Whether a claim arriving later may put the name and the
                    // colours on the window that makes it: either nothing
                    // could be confused with this entry, or the entries it
                    // could be confused with all say the same thing, so it
                    // does not matter which of them the window turns out to
                    // be. Decided here, once, and carried on the entry -
                    // deciding it at the claim instead would look at whichever
                    // twins had not been claimed yet, and two entries that
                    // disagree would end with the second window wearing the
                    // name of the first.
                    let stateMayBeApplied =
                        certain ||
                        (applied.renamedTabName = t.renamedTabName &&
                         applied.fillColor = t.fillColor &&
                         applied.underlineColor = t.underlineColor &&
                         applied.borderColor = t.borderColor)
                    { saved = t
                      rank = ti
                      live = liveHandle
                      applied = applied
                      stateIsCertain = stateMayBeApplied
                      waitingSince = waitingSince
                      outcome = outcome })
            let savedOrder = g.windows |> List.map (fun t -> t.hwnd)
            { tabs = tabs
              tabPosition = g.tabPosition
              snapMargin = g.snapMargin
              savedOrder = savedOrder
              token = (match savedOrder with h :: _ -> h | [] -> IntPtr.Zero) })
