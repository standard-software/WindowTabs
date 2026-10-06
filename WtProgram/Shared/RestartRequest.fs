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
