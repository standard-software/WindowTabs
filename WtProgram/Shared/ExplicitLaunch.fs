namespace Bemo

open System

// Protection expires; launch provenance permits full state replacement.
module ExplicitLaunch =
    [<Literal>]
    let explicitProtectionSeconds = 15

    type Request<'destination> = {
        id: Guid
        exePath: string
        startedAt: DateTime
        existing: Set<IntPtr>
        destination: 'destination
    }

    // Measure from the request, not from the first observed window/title.
    let active now (request: Request<'a> option) =
        request |> Option.filter (fun r ->
            now < r.startedAt.AddSeconds(float explicitProtectionSeconds))

    // Called after suppression: explicit arrivals overwrite even user changes.
    let canClaimState request pristine =
        Option.isSome request || pristine

    let take now exePath hwnd (pending: Request<'a> list) =
        let fresh = pending |> List.filter (fun r -> (now - r.startedAt).TotalSeconds < 30.0)
        let matched = fresh |> List.tryFind (fun r ->
            not (r.existing.Contains hwnd) &&
            String.Equals(r.exePath, exePath, StringComparison.OrdinalIgnoreCase))
        matched, (fresh |> List.filter (fun r -> matched |> Option.forall (fun m -> m.id <> r.id)))

    // Use exactly the same normalized title as ordinary restore. Never delete
    // entries created after this operation, even if the title changes later.
    let suppress now request title identity entries =
        match active now request with
        | None -> false, entries
        | Some(r) ->
            true, (entries |> List.filter (fun entry ->
                let path, savedTitle, closedAt = identity entry
                not (title <> "" && closedAt <= r.startedAt &&
                     String.Equals(path, r.exePath, StringComparison.OrdinalIgnoreCase) &&
                     savedTitle = title)))
