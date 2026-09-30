namespace Bemo

open System
open System.Threading

/// A single background apartment. Requests never wait for the shell, and a
/// busy reader drops overlapping requests instead of building a queue.
module VirtualDesktopReader =
    type Publication<'a> = { generation: int64; value: 'a }

    type Reader<'request, 'reading>(read: 'request -> 'reading, completed: unit -> unit, cleanup: unit -> unit) =
        let gate = obj()
        let mutable stopped = false
        let mutable busy = false
        let mutable pending: 'request option = None
        let mutable latest: Publication<'reading> option = None
        let run () =
            try
                let mutable running = true
                while running do
                    let request = lock gate (fun () ->
                        while not stopped && pending.IsNone do Monitor.Wait(gate) |> ignore
                        if stopped then None
                        else
                            let result = pending
                            pending <- None
                            result)
                    match request with
                    | None -> running <- false
                    | Some request ->
                        let result = try Some(read request) with _ -> None
                        let notify = lock gate (fun () ->
                            busy <- false
                            if stopped then false
                            else
                                match result with
                                | Some value ->
                                    let generation = latest |> Option.map (fun p -> p.generation) |> Option.defaultValue 0L
                                    latest <- Some { generation = generation + 1L; value = value }
                                    true
                                | None -> false)
                        if notify then
                            try completed() with _ -> ()
            finally
                try cleanup() with _ -> ()
        let thread = Thread(ThreadStart(run), IsBackground = true, Name = "Virtual desktop reader")
        do
            thread.SetApartmentState(ApartmentState.MTA)
            thread.Start()
        member _.Request(request) = lock gate (fun () ->
            if stopped || busy then false
            else
                busy <- true
                pending <- Some request
                Monitor.Pulse(gate)
                true)
        member _.Latest = lock gate (fun () -> latest)
        member _.IsStopped = lock gate (fun () -> stopped)
        member _.IsAlive = thread.IsAlive
        member _.Stop() = lock gate (fun () ->
            stopped <- true
            pending <- None
            Monitor.Pulse(gate))
        interface IDisposable with
            member this.Dispose() = this.Stop()

    type Request = { grouped: Set<IntPtr>; windows: IntPtr list }
    type Reading = {
        request: Request
        readAt: DateTime
        supported: bool
        listed: Guid list option
        registryBefore: Guid option
        registryAfter: Guid option
        reads: Map<IntPtr, VirtualDesktopGroups.WindowRead>
        desktopResults: Map<IntPtr, int * Guid>
        onCurrent: Map<IntPtr, bool option>
    }

    /// Main-thread waiters are separate from periodic refresh requests. Even
    /// a failed COM answer (Some None in onCurrent) completes a wait once.
    type WaitingWindows() =
        let mutable waiting: Set<IntPtr> = Set.empty
        member _.Add(hwnd) = waiting <- waiting.Add hwnd
        member _.Windows = waiting
        member _.TakeReady(reading: Reading, isGrouped: IntPtr -> bool) =
            if reading.supported && reading.registryBefore <> reading.registryAfter then false
            else
                let ready = waiting |> Set.filter (fun hwnd ->
                    not reading.supported || reading.onCurrent.ContainsKey hwnd)
                // Remove before the caller scans again, including windows
                // grouped by another event while the read was in flight.
                waiting <- Set.difference waiting ready
                ready |> Set.exists (fun hwnd -> not (isGrouped hwnd))
