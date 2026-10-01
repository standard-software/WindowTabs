namespace Bemo

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading

// Group threads publish membership; the input thread never calls a service
// proxy or waits for a group. Owner identity prevents a late removal from an
// old group from erasing a window already transferred to a new group.
//
// The input thread publishes, in return, the last primary-button press it let
// through on a managed window, for the MOVESIZESTART fallback in WindowGroup
// (see CaptionDragFallback). Both sides only take a short lock.
module CaptionDragTargets =
    [<ReferenceEquality>]
    type private Registration = { owner: obj }
    let private targets = ConcurrentDictionary<IntPtr, Registration>()

    /// Set by the hook's own thread: asks it to look at the state again.
    /// A group registers its windows only while it is locked, so the hook is
    /// wanted exactly while something is registered here - the first window
    /// to arrive has to bring it up, and the last to leave has to take it
    /// down.
    let mutable wake : unit -> unit = id
    let private notifyHook () = try wake() with _ -> ()

    let add owner hwnd =
        let wasEmpty = targets.IsEmpty
        targets.AddOrUpdate(hwnd, (fun _ -> { owner = owner }),
            (fun _ current -> if obj.ReferenceEquals(current.owner, owner) then current else { owner = owner })) |> ignore
        if wasEmpty then notifyHook ()
    let remove owner hwnd =
        match targets.TryGetValue(hwnd) with
        | true, current when obj.ReferenceEquals(current.owner, owner) ->
            (targets :> ICollection<KeyValuePair<IntPtr, Registration>>).Remove(KeyValuePair(hwnd, current)) |> ignore
        | _ -> ()
        if targets.IsEmpty then notifyHook ()
    let contains hwnd = targets.ContainsKey(hwnd)
    // Read-only snapshots for the asynchronous caption-button discovery worker.
    // The token changes on remove/re-add even if the group owner is the same.
    let snapshot () =
        targets.ToArray() |> Array.map (fun entry -> KeyValuePair(entry.Key, box entry.Value))
    let ownedBy hwnd owner =
        match targets.TryGetValue(hwnd) with
        | true, current -> obj.ReferenceEquals(current, owner)
        | _ -> false
    /// Whether any window is locked at all, which is what decides whether the
    /// hook is installed.
    let anyTarget () = not targets.IsEmpty

    type Press =
        {
            hwnd: IntPtr
            // The top-level window's hit-test answer at the press point.
            rootHit: int option
            x: int
            y: int
            // Window rectangle at the press, before the target received it.
            bounds: CaptionDragFallback.Box
            // Environment.TickCount at the press.
            tick: int
            // Value of pressSequence right after this press was counted.
            sequence: int64
        }

    let private gate = obj()
    let mutable private lastPress : Press option = None
    // [0] presses, [1] fallback engagements (array slots can be addressed).
    let private counters = [| 0L; 0L |]

    // Every primary-button press the hook sees, managed window or not.
    let notePress () = Interlocked.Increment(&counters.[0])
    let pressSequence () = Interlocked.Read(&counters.[0])

    let setPress (press: Press option) = lock gate (fun () -> lastPress <- press)
    let currentPress () = lock gate (fun () -> lastPress)

    // Diagnostics: how many drags the fallback had to undo.
    let noteFallback () = Interlocked.Increment(&counters.[1])
