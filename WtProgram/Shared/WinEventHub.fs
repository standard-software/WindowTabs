namespace Bemo

open System
open System.Collections.Generic
open System.Threading

[<Struct>]
type HubEvent =
    { Hook: nativeint
      Number: int
      Hwnd: nativeint
      ObjectId: nativeint
      ChildId: nativeint
      ThreadId: int
      Time: int }

// The generation also cancels deliveries queued before a disable/enable cycle.
type WinEventDeliveryGate() =
    let mutable generation = 0
    member _.Stamp = Volatile.Read(&generation)
    member this.IsCurrent stamp = stamp % 2 = 0 && this.Stamp = stamp
    member _.SetEnabled enabled =
        let old = Volatile.Read(&generation)
        if (old % 2 = 0) <> enabled then Interlocked.Increment(&generation) |> ignore

type HubSubscription =
    { Min: int
      Max: int
      Pid: int
      Tid: int
      Hwnd: nativeint option
      Active: unit -> bool
      Deliver: HubEvent -> unit }

// Tables are replaced only on the pump. Dispatch reads arrays/dictionaries;
// it never enumerates subscriptions, takes a lock or builds a candidate list.
type private HubBucket(entries: HubSubscription array) =
    let byWindow = Dictionary<nativeint, HubSubscription array>()
    let byThread = Dictionary<int, HubSubscription array>()
    let byProcess = Dictionary<int, HubSubscription array>()
    let globalEntries = ResizeArray<HubSubscription>()
    let add (table: Dictionary<'k, HubSubscription array>) key entry =
        let mutable old = Unchecked.defaultof<_>
        table.[key] <- if table.TryGetValue(key, &old) then Array.append old [| entry |] else [| entry |]
    do
        for entry in entries do
            match entry.Hwnd with
            | Some hwnd -> add byWindow hwnd entry
            | None when entry.Tid <> 0 -> add byThread entry.Tid entry
            | None when entry.Pid <> 0 -> add byProcess entry.Pid entry
            | None -> globalEntries.Add entry
    let globals = globalEntries.ToArray()
    let find (table: Dictionary<'k, HubSubscription array>) key =
        let mutable found = Unchecked.defaultof<_>
        if table.TryGetValue(key, &found) then found else Array.empty
    let needsPid (entries: HubSubscription array) tid =
        let mutable needed = false
        let mutable i = 0
        while i < entries.Length && not needed do
            let entry = entries.[i]
            needed <- entry.Pid <> 0 && (entry.Tid = 0 || entry.Tid = tid) && entry.Active()
            i <- i + 1
        needed
    let deliver (entries: HubSubscription array) (evt: HubEvent) pid =
        for entry in entries do
            if (entry.Tid = 0 || entry.Tid = evt.ThreadId) &&
               (entry.Pid = 0 || entry.Pid = pid) && entry.Active() then
                entry.Deliver evt
    member _.Dispatch(evt: HubEvent, resolvePid: HubEvent -> int) =
        let windows = find byWindow evt.Hwnd
        let threads = find byThread evt.ThreadId
        let pid =
            if byProcess.Count <> 0 || needsPid windows evt.ThreadId || needsPid threads evt.ThreadId then
                resolvePid evt
            else 0
        deliver globals evt pid
        deliver windows evt pid
        deliver threads evt pid
        if pid <> 0 then deliver (find byProcess pid) evt pid

// Native registration is injected so lifetime and routing tests need no OS hook.
// All members run on the registration pump, including native cleanup. A failed
// unhook remains owned and can be reused or retried; never install a duplicate.
type WinEventHub(register: int -> (HubEvent -> unit) -> (unit -> bool), resolvePid: HubEvent -> int) as this =
    let subscriptions = Dictionary<int, HubSubscription>()
    let hooks = Dictionary<int, unit -> bool>()
    let mutable table = Dictionary<int, HubBucket>()
    let mutable enabled = true
    let mutable nextId = 0
    let rebuild() =
        let next = Dictionary<int, HubBucket>()
        if enabled then
            let events = Dictionary<int, ResizeArray<HubSubscription>>()
            for entry in subscriptions.Values do
                for number in entry.Min .. entry.Max do
                    let mutable entries = Unchecked.defaultof<_>
                    if not (events.TryGetValue(number, &entries)) then
                        entries <- ResizeArray()
                        events.Add(number, entries)
                    entries.Add entry
            for pair in events do next.Add(pair.Key, HubBucket(pair.Value.ToArray()))
        table <- next
        for number in hooks.Keys |> Seq.toArray do
            if not (next.ContainsKey number) && hooks.[number]() then hooks.Remove number |> ignore
        for number in next.Keys do
            if not (hooks.ContainsKey number) then
                hooks.Add(number, register number this.Dispatch)
    member _.Dispatch(evt: HubEvent) =
        // Zero HWND is meaningful for global system events. Window-specific
        // subscribers naturally miss it; do not change the global contract.
        if evt.ObjectId = nativeint 0 then
            let mutable bucket = Unchecked.defaultof<_>
            if table.TryGetValue(evt.Number, &bucket) then bucket.Dispatch(evt, resolvePid)
    member _.Subscribe entry =
        nextId <- nextId + 1
        let id = nextId
        subscriptions.Add(id, entry)
        rebuild()
        { new IDisposable with
            member _.Dispose() =
                if subscriptions.Remove id then rebuild() }
    member _.SetEnabled value =
        enabled <- value
        rebuild()
    member _.SubscriptionCount = subscriptions.Count
    member _.HookCount = hooks.Count
