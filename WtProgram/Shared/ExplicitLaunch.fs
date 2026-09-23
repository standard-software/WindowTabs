namespace Bemo

open System

// Launch intent survives startup title changes, but never survives the window.
module ExplicitLaunch =
    type Request<'destination> = {
        id: Guid
        exePath: string
        startedAt: DateTime
        existing: Set<IntPtr>
        destination: 'destination
    }

    let take now exePath hwnd (pending: Request<'a> list) =
        let fresh = pending |> List.filter (fun r -> (now - r.startedAt).TotalSeconds < 30.0)
        let matched = fresh |> List.tryFind (fun r ->
            not (r.existing.Contains hwnd) &&
            String.Equals(r.exePath, exePath, StringComparison.OrdinalIgnoreCase))
        matched, (fresh |> List.filter (fun r -> matched |> Option.forall (fun m -> m.id <> r.id)))

    // Use exactly the same normalized title as ordinary restore. Never delete
    // entries created after this operation, even if the title changes later.
    let suppress (request: Request<'a> option) title identity entries =
        match request with
        | None -> false, entries
        | Some(r) ->
            true, (entries |> List.filter (fun entry ->
                let path, savedTitle, closedAt = identity entry
                not (title <> "" && closedAt <= r.startedAt &&
                     String.Equals(path, r.exePath, StringComparison.OrdinalIgnoreCase) &&
                     savedTitle = title)))
