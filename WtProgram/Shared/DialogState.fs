namespace Bemo

open System

// A settings window (including its owned editors), a message, or an update
// check owns one session. New top-level operations never dismiss its owner.
type DialogGate() =
    let sync = obj()
    let mutable occupied = false
    let available = Event<unit>()

    member this.IsBusy = lock sync (fun () -> occupied)
    member this.Available = available.Publish

    member this.TryAcquire() =
        lock sync (fun () ->
            if occupied then None
            else
                occupied <- true
                let mutable released = false
                Some { new IDisposable with
                    member this.Dispose() =
                        let notify =
                            lock sync (fun () ->
                                if released then false
                                else
                                    released <- true
                                    occupied <- false
                                    true)
                        if notify then available.Trigger() })

module DialogState =
    let private gate = DialogGate()
    let isBusy() = gate.IsBusy
    let tryAcquire() = gate.TryAcquire()
    let available = gate.Available

    let canOpenSettings disabled = not disabled && not (isBusy())

    let canUseTrayCommand tag disabled =
        match tag with
        | "Settings" -> canOpenSettings disabled
        | _ -> not (isBusy())
