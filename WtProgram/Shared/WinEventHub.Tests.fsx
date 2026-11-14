#load "WinEventHub.fs"
// Run with fsi, referencing FSharp.PowerPack.dll, Win32.dll and
// WindowTabs.exe from the temporary build output.
open System
open System.Collections.Generic
open Bemo

let mutable checks = 0
let check name expected actual =
    checks <- checks + 1
    if expected <> actual then failwithf "%s: expected %A, got %A" name expected actual
let callbacks = Dictionary<int * int, HubEvent -> unit>()
let installed = ResizeArray<int * int>()
let blocked = HashSet<int * int>()
let mutable attempts = 0
let failures = HashSet<int * int>()
let registrationAttempts = ResizeArray<int * int>()
// BeginInvoke-style work drains in one batch. Timers have a separate clock.
let work = Queue<unit -> unit>()
let post action = work.Enqueue action
let drainWork() = while work.Count > 0 do work.Dequeue()()
let pending = ResizeArray<int64 * (unit -> unit)>()
let mutable timerClock = 0L
let mutable acceptDeferred = true
let defer milliseconds action =
    if not acceptDeferred then false
    else pending.Add((timerClock + int64 milliseconds, action)); true
let advance milliseconds =
    timerClock <- timerClock + int64 milliseconds
    let due = pending |> Seq.filter (fun (at, _) -> at <= timerClock) |> Seq.toArray
    pending.RemoveAll(fun (at, _) -> at <= timerClock) |> ignore
    for _, action in due do action()
let drain() = advance 500
let mutable tick = 100
let register number pid callback =
    let key = number, pid
    registrationAttempts.Add key
    if failures.Contains key then None
    else
        if callbacks.ContainsKey key then failwith "Duplicate native hook"
        callbacks.Add(key, callback)
        installed.Add(key)
        Some (fun () ->
            attempts <- attempts + 1
            if blocked.Contains key then false
            else callbacks.Remove key |> ignore; true)
let mutable resolutions = 0
let hub = WinEventHub(register, (fun evt -> resolutions <- resolutions + 1; evt.ThreadId / 10), defer, fun () -> tick)
let delivered = ResizeArray<int>()
let entry number pid tid hwnd tag =
    { Min = number; Max = number; Pid = pid; Tid = tid; Hwnd = hwnd
      Active = (fun () -> true); Deliver = (fun _ -> delivered.Add(tag)) }
let scopes() = callbacks.Keys |> Set.ofSeq
let emit number pid tid hwnd objectId =
    callbacks.[(number, pid)]
        { Hook = 0n; Number = number; Hwnd = hwnd; ObjectId = objectId
          ChildId = 0n; ThreadId = tid; Time = tick + 1 }
let p1 = hub.Subscribe(entry 1 1 0 None 1)
drain()
let p1b = hub.Subscribe(entry 1 1 10 (Some 1n) 2)
drain()
let p2 = hub.Subscribe(entry 1 2 0 None 3)
drain()
check "one hook per distinct process" (set [(1,1); (1,2)]) (scopes())
check "same process reuses hook" 2 installed.Count
emit 1 1 10 1n 0n
check "process and window routing" [1;2] (List.sort (List.ofSeq delivered))
delivered.Clear()
emit 1 1 11 1n 0n
check "thread restriction survives" [1] (List.ofSeq delivered)
let g = hub.Subscribe(entry 1 0 0 None 4)
drain()
check "global replaces process hooks" (set [(1,0)]) (scopes())
delivered.Clear()
emit 1 0 20 0n 0n
check "global and matching process receive zero HWND" [3;4] (List.sort (List.ofSeq delivered))
g.Dispose()
drain()
check "global removal restores process hooks" (set [(1,1); (1,2)]) (scopes())
p1.Dispose()
drain()
check "remaining process subscriber retains hook" 2 hub.HookCount
p1b.Dispose()
drain()
check "last process subscriber removes hook" (set [(1,2)]) (scopes())
let w = hub.Subscribe(entry 1 0 0 (Some 1n) 5)
drain()
check "PID-free window subscription requires global" (set [(1,0)]) (scopes())
w.Dispose()
drain()
let t = hub.Subscribe(entry 1 0 20 None 6)
drain()
check "thread-only subscription retains coverage" (set [(1,0)]) (scopes())
t.Dispose()
drain()
let range = hub.Subscribe { entry 2 3 0 None 7 with Max = 3 }
drain()
check "event ranges remain independent" (set [(1,2); (2,3); (3,3)]) (scopes())
range.Dispose()
drain()

// A retained hook must not deliver alongside its replacement.
blocked.Add((1,2)) |> ignore
let g2 = hub.Subscribe(entry 1 0 0 None 8)
drain()
check "failed unhook stays owned" (set [(1,0); (1,2)]) (scopes())
delivered.Clear()
let beforeResolve = resolutions
emit 1 2 20 0n 0n
check "obsolete scope does no routing work" beforeResolve resolutions
emit 1 0 20 0n 0n
check "overlapping native scopes deliver once" [3;8] (List.sort (List.ofSeq delivered))
let beforeReuse = installed.Count
g2.Dispose()
drain()
check "retained process hook reused" beforeReuse installed.Count
check "global removed after reuse" (set [(1,2)]) (scopes())
blocked.Clear()
blocked.Add((1,0)) |> ignore
let g3 = hub.Subscribe(entry 1 0 0 None 9)
drain()
g3.Dispose()
drain()
check "failed global unhook stays owned" (set [(1,0); (1,2)]) (scopes())
delivered.Clear()
emit 1 0 20 0n 0n
emit 1 2 20 0n 0n
check "obsolete global cannot duplicate delivery" [3] (List.ofSeq delivered)
blocked.Clear()
let retryBefore = attempts
hub.SetEnabled false
drain()
check "cleanup retried" true (attempts > retryBefore)
check "disabled hub removes hooks" 0 hub.HookCount
let p3 = hub.Subscribe(entry 1 3 0 None 10)
drain()
check "disabled subscriptions install nothing" 0 hub.HookCount
hub.SetEnabled true
drain()
check "enable rebuilds process hooks" (set [(1,2); (1,3)]) (scopes())
blocked.Add((1,2)) |> ignore
hub.SetEnabled false
drain()
delivered.Clear()
emit 1 2 20 0n 0n
check "disabled retained hook cannot deliver" 0 delivered.Count
let beforeEnable = installed.Count
hub.SetEnabled true
drain()
check "enable reuses retained hook" (beforeEnable + 1) installed.Count
blocked.Clear()
p2.Dispose()
drain()
p3.Dispose()
drain()
check "all subscriptions removed" 0 hub.SubscriptionCount
check "all hooks removed" 0 hub.HookCount

let inactive = hub.Subscribe { entry 4 1 0 None 11 with Active = (fun () -> false) }
drain()
delivered.Clear()
emit 4 1 10 0n 0n
check "inactive subscription cannot deliver" 0 delivered.Count
inactive.Dispose()
drain()
let globalOnly = hub.Subscribe(entry 5 0 0 None 12)
drain()
let beforeGlobal = resolutions
emit 5 0 10 0n 1n
check "non-window object filtered" 0 delivered.Count
emit 5 0 10 0n 0n
check "global-only routing needs no PID query" beforeGlobal resolutions
globalOnly.Dispose()
drain()
let gate = WinEventDeliveryGate()
let stamp = gate.Stamp
gate.SetEnabled false
check "disable rejects queued delivery" false (gate.IsCurrent stamp)
gate.SetEnabled true
check "enable still rejects old generation" false (gate.IsCurrent stamp)
check "current generation delivers" true (gate.IsCurrent gate.Stamp)
// Registration failures never become owned hooks, and retry on rebuild.
failures.Add((6,1)) |> ignore
let failedProcess = hub.Subscribe(entry 6 1 0 None 13)
check "process failure falls back to global" (set [(6,0)]) (scopes())
delivered.Clear()
emit 6 0 10 0n 0n
check "fallback delivers process subscription" [13] (List.ofSeq delivered)
let attemptsBeforeRetry = registrationAttempts.Count
hub.SetEnabled true
check "failed process retried" true (registrationAttempts.Count > attemptsBeforeRetry)
failures.Clear()
hub.SetEnabled true
check "fallback retained until queued cleanup" (set [(6,0);(6,1)]) (scopes())
drain()
check "retry restores process scope" (set [(6,1)]) (scopes())
failures.Add((6,0)) |> ignore
let failedGlobal = hub.Subscribe(entry 6 0 0 None 14)
drain()
check "global failure retains existing process" (set [(6,1)]) (scopes())
delivered.Clear()
emit 6 1 10 0n 0n
check "existing coverage still delivers" [13;14] (List.sort (List.ofSeq delivered))
failures.Clear()
hub.SetEnabled true
drain()
check "global retry succeeds" (set [(6,0)]) (scopes())
// Both a process registration and its fallback fail during a reverse switch.
failures.Add((6,2)) |> ignore
let partial = hub.Subscribe(entry 6 2 0 None 15)
failedGlobal.Dispose()
drain()
check "failed process preserves existing global fallback" (set [(6,0)]) (scopes())
failures.Clear()
hub.SetEnabled true
drain()
check "partial retry installs every process" (set [(6,1);(6,2)]) (scopes())
failedProcess.Dispose()
partial.Dispose()
drain()
failures.Add((7,2)) |> ignore
failures.Add((7,0)) |> ignore
let existing = hub.Subscribe(entry 7 1 0 None 16)
let missing = hub.Subscribe(entry 7 2 0 None 17)
drain()
check "double failure preserves old process coverage" (set [(7,1)]) (scopes())
failures.Clear()
hub.SetEnabled true
drain()
check "double failure retries missing process" (set [(7,1);(7,2)]) (scopes())
existing.Dispose()
missing.Dispose()
drain()

// Simulate native notifications already waiting when a scope is replaced.
let waiting = hub.Subscribe(entry 8 1 0 (Some 1n) 18)
let oldProcess = callbacks.[(8,1)]
let notify callback time =
    callback { Hook = 0n; Number = 8; Hwnd = 1n; ObjectId = 0n
               ChildId = 0n; ThreadId = 10; Time = time }
let mutable toGlobal = Unchecked.defaultof<IDisposable>
post (fun () ->
    toGlobal <- hub.Subscribe(entry 8 0 0 None 19)
    post (fun () -> ()))
drainWork()
check "managed callback batch cannot run deferred cleanup" true (callbacks.ContainsKey((8,1)))
advance 499
check "old hook remains during grace period" true (callbacks.ContainsKey((8,1)))
check "old process survives until grace period" true (callbacks.ContainsKey((8,1)))
delivered.Clear()
notify oldProcess (tick - 1)
check "queued pre-switch process notification delivered" [18;19] (List.sort (List.ofSeq delivered))
delivered.Clear()
notify oldProcess (tick + 1)
notify callbacks.[(8,0)] (tick + 1)
check "post-switch notification delivered only by replacement" [18;19] (List.sort (List.ofSeq delivered))
delivered.Clear()
notify oldProcess tick
check "equal tick preserved conservatively" 2 delivered.Count
advance 1
check "expired grace period removes old process" false (callbacks.ContainsKey((8,1)))
delivered.Clear()
// Model the OS dropping a notification after its hook has been removed.
if callbacks.ContainsKey((8,1)) then notify callbacks.[(8,1)] (tick - 1)
check "OS cannot deliver removed hook notification" 0 delivered.Count
notify oldProcess (tick - 1)
check "late old callback is also rejected by hub" 0 delivered.Count
let oldGlobal = callbacks.[(8,0)]
tick <- 200
toGlobal.Dispose()
check "old global survives until grace period" true (callbacks.ContainsKey((8,0)))
delivered.Clear()
notify oldGlobal (tick - 1)
check "queued pre-switch global notification delivered" [18] (List.ofSeq delivered)
delivered.Clear()
notify oldGlobal (tick + 1)
notify callbacks.[(8,1)] (tick + 1)
check "reverse switch avoids later duplicates" [18] (List.ofSeq delivered)
// Reuse a retiring hook before its queued cleanup runs.
let reusedGlobal = hub.Subscribe(entry 8 0 0 None 20)
drain()
check "stale cleanup cannot remove reused global" (set [(8,0)]) (scopes())
reusedGlobal.Dispose()
// Disable before deferred cleanup: retained callbacks must stop immediately.
blocked.Add((8,0)) |> ignore
hub.SetEnabled false
delivered.Clear()
notify oldGlobal (tick - 1)
check "disable cancels retirement delivery" 0 delivered.Count
drain()
blocked.Clear()
hub.SetEnabled true
drain()
check "enable retries failed cleanup" (set [(8,1)]) (scopes())
// Exercise tick rollover using artificial values only.
tick <- System.Int32.MinValue
let rollover = hub.Subscribe(entry 8 0 0 None 21)
delivered.Clear()
notify callbacks.[(8,1)] System.Int32.MaxValue
check "pre-switch notification survives tick rollover" 2 delivered.Count
delivered.Clear()
notify callbacks.[(8,1)] (System.Int32.MinValue + 1)
check "post-switch notification rejected across rollover" 0 delivered.Count
drain()
rollover.Dispose()
waiting.Dispose()
drain()
check "final hooks removed" 0 hub.HookCount
check "final subscriptions removed" 0 hub.SubscriptionCount
// A stopping pump rejects new timers; cleanup must run immediately.
let stoppedProcess = hub.Subscribe(entry 9 1 0 None 22)
acceptDeferred <- false
let stoppedGlobal = hub.Subscribe(entry 9 0 0 None 23)
check "rejected defer cleans up immediately" (set [(9,0)]) (scopes())
check "rejected defer creates no pending action" 0 pending.Count
stoppedGlobal.Dispose()
check "reverse switch handles rejected defer" (set [(9,1)]) (scopes())
stoppedProcess.Dispose()
acceptDeferred <- true
// Old timers must not remove a reused hook that retires again later.
let repeatedProcess = hub.Subscribe(entry 10 1 0 None 24)
let firstGlobal = hub.Subscribe(entry 10 0 0 None 25)
advance 100
firstGlobal.Dispose()
let secondGlobal = hub.Subscribe(entry 10 0 0 None 26)
advance 400
check "stale timer cannot end newer retirement" true (callbacks.ContainsKey((10,1)))
advance 100
check "new retirement expires on its own timer" false (callbacks.ContainsKey((10,1)))
secondGlobal.Dispose()
hub.SetEnabled false
drain()
check "disable removes hooks despite outstanding timers" 0 hub.HookCount
repeatedProcess.Dispose()
printfn "%d checks, 0 failures" checks
