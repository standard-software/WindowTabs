// Exercise the shipping asynchronous continuation using a deterministic queue.
// No windows, message loop, settings files or wall-clock sleeps are needed.
#load "../WtProgram/Shared/SnapDrop.fs"
open Bemo
let mutable passed = 0
let check name condition =
    if not condition then failwith name
    passed <- passed + 1
    printfn "PASS %s" name

type Probe(initialDpi, targetDpi, perMonitor) =
    let queue = System.Collections.Generic.Queue<int * (unit -> unit)>()
    let mutable dpi = initialDpi
    let mutable now = 0L
    let mutable alive = true
    let mutable finishes = 0
    let mutable reads = 0
    do SnapDrop.finishAfterDpiChange initialDpi targetDpi perMonitor
           (fun() -> alive)
           (fun() -> reads <- reads + 1; dpi)
           (fun() -> now)
           (fun delay callback -> queue.Enqueue(delay, callback))
           (fun() -> finishes <- finishes + 1)
    member _.Dpi with set value = dpi <- value
    member _.Alive with set value = alive <- value
    member _.Finishes = finishes
    member _.Reads = reads
    member _.Pending = queue.Count
    member _.NextDelay = fst (queue.Peek())
    member _.Step() =
        let delay, callback = queue.Dequeue()
        now <- now + int64 delay
        callback()
    member _.Elapsed = now

for aware,current,target in [(false,96u,144u);(false,120u,96u);(true,144u,144u)] do
    let p = Probe(current,target,aware)
    check "same DPI or non-per-monitor window finishes synchronously" (p.Finishes = 1 && p.Pending = 0 && p.Reads = 0)
for source,target in [(96u,144u);(144u,96u);(192u,144u)] do
    let p = Probe(source,target,true)
    check "DPI transition returns with queued work" (p.Finishes = 0 && p.Pending = 1 && p.Reads = 0 && p.NextDelay = 10)
    p.Step()
    check "unchanged DPI queues another check" (p.Finishes = 0 && p.Pending = 1 && p.NextDelay = 10)
    p.Dpi <- target
    p.Step()
    check "target DPI queues settling continuation" (p.Finishes = 0 && p.Pending = 1 && p.NextDelay = 20)
    p.Step()
    check "settling finishes once with no remaining work" (p.Finishes = 1 && p.Pending = 0)
let timeout = Probe(96u,144u,true)
for _ in 1..19 do timeout.Step()
check "still pending before timeout" (timeout.Elapsed = 190L && timeout.Finishes = 0)
timeout.Step()
check "unresponsive DPI completes at the bounded deadline" (timeout.Elapsed = 200L && timeout.Finishes = 1 && timeout.Pending = 0)
let closed = Probe(96u,144u,true)
closed.Alive <- false
closed.Step()
check "closed window completes cleanup without a DPI read" (closed.Finishes = 1 && closed.Pending = 0 && closed.Reads = 0)
printfn "%d checks passed" passed
