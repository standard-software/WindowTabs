namespace Bemo

/// Timing and duplicate suppression do not read windows or the shell.
module TaskbarOrderPolicy =
    [<Literal>]
    let settleMs = 3000

    [<Literal>]
    let paceMs = 300

    [<Literal>]
    let addAttempts = 3

    /// TickCount can wrap while a continuation is waiting.
    let paceRemaining now previous =
        match previous with
        | None -> 0
        | Some at -> max 0 (paceMs - int (min (uint32 paceMs) (uint32 (now - at))))

    /// Once deletion starts, restoring the button is independent of cancellation.
    /// Retry only addition, and stop if the original window is no longer safe.
    let reRegister attempts canRetry delete add =
        let mutable added = false
        let deleted =
            try
                try delete () with _ -> false
            finally
                let mutable attempt = 0
                while not added && attempt < attempts && (attempt = 0 || canRetry ()) do
                    attempt <- attempt + 1
                    added <- try add () with _ -> false
        deleted && added

    type Decision = Wait of int | Execute | Ignore

    let decide changedAt now front members previous =
        if Set.count members < 2 || not (Set.contains front members) then Ignore
        else
            let remaining = int64 settleMs - (now - changedAt)
            if remaining > 0L then Wait (int remaining)
            elif previous = Some(front, members) then Ignore
            else Execute
