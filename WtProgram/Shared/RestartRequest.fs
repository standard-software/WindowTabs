namespace Bemo

open System

// A start-time identity prevents a recycled process ID from being terminated.
module RestartRequest =
    let parse (args: string[]) =
        match args with
        | [| "--watchdog-restart"; pid; started |] ->
            match Int32.TryParse(pid), Int64.TryParse(started) with
            | (true, p), (true, s) when p > 0 && s > 0L -> Some(p, s)
            | _ -> None
        | _ -> None

    let matches expected actual = expected = actual

    // Only confirmed foreign hangs defer the original one-timeout restart.
    // A failed probe must not disable recovery from an in-process deadlock.
    let restartDecision probeKnown foreignHung (deferredElapsedMs: int64) =
        if probeKnown && foreignHung then
            if deferredElapsedMs >= 120000L then "restart-after-wait"
            else "wait-desktop-hung"
        else "restart-local"

    // Dispatch itself can block in the invoker. The caller owns only the deadline.
    let waitForSave timeoutMs (dispatch: (unit -> unit) -> unit) =
        let completion = System.Threading.Tasks.TaskCompletionSource<bool>()
        let worker = System.Threading.Thread(System.Threading.ThreadStart(fun () ->
            try dispatch (fun () -> completion.TrySetResult(true) |> ignore)
            with _ -> completion.TrySetResult(false) |> ignore))
        worker.IsBackground <- true
        worker.Start()
        completion.Task.Wait(timeoutMs: int) && completion.Task.Result
