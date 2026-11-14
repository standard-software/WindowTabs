#load "QueuedSubscription.fs"
open System
open System.Collections.Generic
open Bemo
let mutable checks = 0
let check name expected actual =
    checks <- checks + 1
    if expected <> actual then failwith name
let queue = Queue<unit -> unit>()
let post f = queue.Enqueue(f)
let drain() = while queue.Count > 0 do queue.Dequeue()()
let mutable registrations = 0
let mutable removals = 0
let mutable active = fun () -> false
let subscribe guard =
    registrations <- registrations + 1
    active <- guard
    { new IDisposable with member _.Dispose() = removals <- removals + 1 }
let early = new QueuedSubscription(post, subscribe) :> IDisposable
early.Dispose()
drain()
check "canceled registration is skipped" 0 registrations
check "unregistered subscription needs no native cleanup" 0 removals
let live = new QueuedSubscription(post, subscribe) :> IDisposable
check "registration is queued" 0 registrations
drain()
check "registered on owner queue" 1 registrations
check "callback enabled" true (active())
live.Dispose()
check "queued callback canceled immediately" false (active())
check "cleanup waits for owner queue" 0 removals
live.Dispose()
drain()
check "native cleanup runs once" 1 removals
printfn "%d checks, 0 failures" checks
