namespace Bemo

open System

module ClosedTabClaim =
    let sameIdentity (path, title) (otherPath, otherTitle) =
        AppPath.sameApp path otherPath && title = otherTitle

    // Re-evaluate only quarantined entries. Ordinary same-group twins and
    // shared-window claims retain their existing policy. Callers remove expired
    // entries first, so an expired competitor cannot hold a survivor forever.
    let heldIndices (entries: ((string * string) * 'group * bool) list) =
        entries |> List.indexed |> List.choose (fun (i, (identity, group, held)) ->
            if held && entries |> List.exists (fun (other, otherGroup, _) ->
                otherGroup <> group && sameIdentity identity other) then Some i else None)
        |> Set.ofList

    let matchingIndices identity (entries: ((string * string) * bool) list) =
        entries |> List.indexed |> List.choose (fun (i, (other, held)) ->
            if not held && sameIdentity identity other then Some i else None)

    // The caller supplies normalized titles and only live group members.
    let canRecord closing identity liveTabs =
        liveTabs |> List.exists (fun (hwnd, other) -> hwnd <> closing && sameIdentity identity other) |> not

    type Decision = AlreadyClaimed | NoMatch | Claim of int

    // Cache order is authoritative for both closed records and restore seeds.
    // The first offered unclaimed window takes the first matching entry.
    let decide hwnd claimed (matches: int list) =
        if Set.contains hwnd claimed then AlreadyClaimed
        else
            match matches with
            | index :: _ -> Claim index
            | [] -> NoMatch

    // Recheck the exact entry and the window immediately before committing
    // the claim. A stale index must never consume the next available record.
    // The main thread consumes both before posting any placement or detachment;
    // that work continues this claim rather than making a second claim.
    let take hwnd isEntry claimed entries =
        if Set.contains hwnd claimed then None
        else
            entries |> List.tryFindIndex isEntry |> Option.map (fun index ->
                let entry = entries.[index]
                let remaining = entries |> List.indexed |> List.choose (fun (i, e) -> if i = index then None else Some e)
                entry, Set.add hwnd claimed, remaining)

    let forget hwnd claimed = Set.remove hwnd claimed

    let mayPlace isRestoreSeed inFormerGroup canBindSavedGroup =
        inFormerGroup || (isRestoreSeed && canBindSavedGroup)
