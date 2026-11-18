namespace Bemo

module HiddenReplacement =
    [<Literal>]
    let lifetimeMs = 1500L

    type Pending<'tab> = {
        processId: int
        removedAt: int64
        neighbor: 'tab
    }

    let remember selected exists visible skipActivation processId now neighbor =
        if selected && exists && not visible && not skipActivation && processId <> 0 then
            neighbor |> Option.map (fun tab -> { processId=processId; removedAt=now; neighbor=tab })
        else None

    // The front is still held when the foreground window is the neighbor this
    // group brought forward or the arriving window itself, and the neighbor is
    // not minimized. Anything else means the user moved on.
    let frontHeld foreground neighbor arriving neighborMinimized =
        (foreground = neighbor || foreground = arriving) && not neighborMinimized

    // The caller consumes the record on every eligible automatic arrival,
    // even when the process, selection, or current window state does not match.
    let shouldActivate now selected processId exists visible skipActivation frontHeld pending =
        match pending with
        | Some entry ->
            let age = now - entry.removedAt
            age >= 0L && age <= lifetimeMs &&
            processId <> 0 && entry.processId = processId && entry.neighbor = selected &&
            exists && visible && not skipActivation && frontHeld
        | None -> false
