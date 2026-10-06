namespace Bemo

open System
open System.Threading

// Registration and cleanup share one native thread. Cancellation also covers
// callbacks already queued to a UI thread when the subscription is disposed.
type QueuedSubscription(post: (unit -> unit) -> unit, subscribe: (unit -> bool) -> IDisposable) =
    let mutable disposed = 0
    let mutable subscription: IDisposable option = None
    let active() = Volatile.Read(&disposed) = 0
    do post (fun () -> if active() then subscription <- Some(subscribe active))
    interface IDisposable with
        member this.Dispose() =
            if Interlocked.Exchange(&disposed, 1) = 0 then
                post (fun () ->
                    subscription |> Option.iter (fun value -> value.Dispose())
                    subscription <- None)
