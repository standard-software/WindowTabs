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
    let private targets = ConcurrentDictionary<IntPtr, obj>()
    let add owner hwnd = targets.[hwnd] <- owner
    let remove owner hwnd =
        (targets :> ICollection<KeyValuePair<IntPtr, obj>>).Remove(KeyValuePair(hwnd, owner)) |> ignore
    let contains hwnd = targets.ContainsKey(hwnd)

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
