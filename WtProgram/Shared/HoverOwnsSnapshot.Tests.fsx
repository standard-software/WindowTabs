// TestRuntime: Framework
// Load types only. Do not run the application entry point or create windows.
#r "System.Drawing"
#r "System.Windows.Forms"
#r "Newtonsoft.Json.dll"
#r "FSharp.PowerPack.dll"
#r "Aga.Controls.dll"
#r "Win32.dll"
#r "WindowTabs.exe"
open System
open System.Threading
open Bemo
let mutable checks = 0
let equal name expected actual =
    checks <- checks + 1
    if expected <> actual then failwithf "%s: expected %A, got %A" name expected actual
let scope = CellScope(checkTid=true)
let members = scope.create(Set2<nativeint>())
let published = members.createSnapshot()
let readOnWorker (f: unit -> 'a) =
    let result = ref None
    let error = ref None
    let worker = new Thread(ThreadStart(fun () ->
        try result.Value <- Some(f()) with ex -> error.Value <- Some ex))
    worker.IsBackground <- true
    worker.Start()
    if not (worker.Join(5000)) then failwith "Snapshot reader waited for the owner"
    match error.Value with Some ex -> raise ex | None -> result.Value.Value
equal "initial membership is empty" true (readOnWorker (fun () -> published.value.isEmpty))
equal "direct Cell access is forbidden on reader thread" true
    (readOnWorker (fun () ->
        try members.value |> ignore; false
        with ex -> ex.Message = "accessing cell on wrong thread"))
let mutable notifications = 0
scope.listen (fun () ->
    let ownerValue = members.value
    // The owner is inside a dependency computation and waits for the reader.
    // Reading must neither invoke the owner nor enumerate its execution stack.
    let actual = readOnWorker (fun () -> published.value.items.list)
    equal "publication precedes listeners" ownerValue.items.list actual
    notifications <- notifications + 1)
let first = Set2<nativeint>().add(1n).add(2n)
let second = Set2<nativeint>().add(3n).add(4n)
members.set(first)
let retained = published.value
members.set(second)
equal "old snapshot remains immutable" [1n;2n] retained.items.list
equal "removed members disappear immediately" false (published.value.contains 1n)
equal "new members appear immediately" true (published.value.contains 4n)
equal "initial and changed listener calls" 3 notifications
// Separate scope: sustained publication overlaps four readers without any UI.
let stressScope = CellScope(checkTid=true)
let stressCell = stressScope.create(first)
let stressSnapshot = stressCell.createSnapshot()
let start = new ManualResetEventSlim(false)
let failures = System.Collections.Concurrent.ConcurrentQueue<exn>()
let counts = Array.zeroCreate<int> 4
let readers =
    [|for reader in 0..3 ->
        let worker = new Thread(ThreadStart(fun () ->
            try
                start.Wait()
                for iteration in 1..100000 do
                    let snapshot = stressSnapshot.value
                    let a,b,c,d = snapshot.contains 1n, snapshot.contains 2n, snapshot.contains 3n, snapshot.contains 4n
                    if not ((a && b && not c && not d) || (not a && not b && c && d)) then
                        failwith "Reader observed mixed membership"
                    counts.[reader] <- counts.[reader] + 1
            with ex -> failures.Enqueue(ex)))
        worker.IsBackground <- true
        worker.Start()
        worker|]
start.Set()
for iteration in 1..20000 do
    stressCell.set(if iteration % 2 = 0 then first else second)
for reader in readers do
    if not (reader.Join(10000)) then failwith "Concurrent snapshot reader did not finish"
equal "no reader failures" true failures.IsEmpty
equal "concurrent reads" 400000 (Array.sum counts)
start.Dispose()
equal "last publication is visible" [1n;2n] (readOnWorker (fun () -> stressSnapshot.value.items.list))
let edgeHandles = PublishedSnapshot<nativeint list>([])
edgeHandles.publish([5n;6n])
let oldEdges = edgeHandles.value
edgeHandles.publish([])
equal "removed edge handles clear" [] (readOnWorker (fun () -> edgeHandles.value))
equal "old edge list stays immutable" [5n;6n] oldEdges
printfn "HoverOwnsSnapshot: %d checks passed; 400000 concurrent reads, 20000 publications" checks
