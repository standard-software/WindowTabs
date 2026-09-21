// Runs the virtual-desktop straddle rule without starting WindowTabs.
//
//   fsi --exec VirtualDesktopIntegrity.Tests.fsx   (or: dotnet fsi ...)
//
// It loads Shared/VirtualDesktopIntegrity.fs itself - the same file the
// program compiles - so what is printed here is what the program would decide.
// No window is opened, read or moved; the desktop ids are made up.
//
// Three things matter more than the rest, and each cost a session:
//   * the window that MOVED is the one that left, even when it is the window
//     in front - sending a window to another desktop also brings it forward,
//     and an earlier rule read that as the other fifteen having wandered off;
//   * a window whose desktop Windows will not report is never taken for a
//     window that moved;
//   * a reading with nothing behind it that says most of the group has left
//     is not believed at all, while windows watched moving always are.

#load "VirtualDesktopIntegrity.fs"

open System
open Bemo.VirtualDesktopIntegrity

let mutable failures = 0
let mutable checks = 0

let check name condition =
    checks <- checks + 1
    if condition then printfn "  PASS  %s" name
    else
        failures <- failures + 1
        printfn "  FAIL  %s" name

let h (n: int) = nativeint n
let deskA = Guid("11111111-1111-1111-1111-111111111111")
let deskB = Guid("22222222-2222-2222-2222-222222222222")
let deskC = Guid("33333333-3333-3333-3333-333333333333")

let on desktop hwnd = { hwnd = h hwnd; desktop = Some desktop }
let unreadable hwnd = { hwnd = h hwnd; desktop = None }
let nothingBefore = Map.empty<IntPtr, Guid>
let before windows = reading windows

printfn ""
printfn "---- one desktop, nothing to do"

let together = [on deskA 1; on deskA 2; on deskA 3]

check "a group whose windows are all on one desktop is settled"
    (decide (before together) (h 1) together = Settled)

check "a single window cannot straddle"
    (decide nothingBefore (h 1) [on deskA 1] = Settled)

check "an empty group is settled"
    (decide nothingBefore (h 1) [] = Settled)

check "the whole group moving together is settled"
    // Switching desktops does not move a window, but "show on all desktops"
    // and a group moved window by window both end up here.
    (decide (before together) (h 1) [on deskB 1; on deskB 2; on deskB 3] = Settled)

printfn ""
printfn "---- a window was moved away"

let movedThird = [on deskA 1; on deskA 2; on deskB 3]

check "the window that moved is the stray"
    (decide (before together) (h 1) movedThird = Straddling(deskA, [h 3]))

check "the window that moved is the stray even when it is in front"
    // The case that took a session apart: window 3 moved and came to the
    // front, and the group must not conclude that 1 and 2 are the strays.
    (decide (before together) (h 3) movedThird = Straddling(deskA, [h 3]))

check "two windows moved in the same second are both strays"
    (decide (before together) (h 1) [on deskA 1; on deskB 2; on deskB 3]
        = Straddling(deskA, [h 2; h 3]))

check "a window moved out of a pair is the stray, front or not"
    (decide (before [on deskA 1; on deskA 2]) (h 2) [on deskA 1; on deskB 2]
        = Straddling(deskA, [h 2]))

printfn ""
printfn "---- nothing to compare against (the first look, or a restore)"

check "with no previous reading the group goes by weight of numbers"
    (decide nothingBefore (h 3) movedThird = Straddling(deskA, [h 3]))

check "an even split is decided by the front window"
    (decide nothingBefore (h 3) [on deskA 1; on deskA 2; on deskB 3; on deskB 4]
        = Straddling(deskB, [h 1; h 2]))

check "an even split with an unreadable front window still decides"
    (match decide nothingBefore (h 9) [on deskA 1; on deskB 2] with
     | Straddling(_, strays) -> strays.Length = 1
     | Settled -> false)

printfn ""
printfn "---- windows Windows will not talk about"

check "an unreadable window is not a stray"
    (decide (before together) (h 1) [on deskA 1; on deskA 2; unreadable 3] = Settled)

check "unreadable windows alone say nothing"
    (decide nothingBefore (h 1) [unreadable 1; unreadable 2] = Settled)

check "one readable window next to unreadable ones says nothing"
    (decide nothingBefore (h 1) [on deskA 1; unreadable 2; unreadable 3] = Settled)

check "a window that becomes unreadable is not taken for a window that moved"
    (decide (before movedThird) (h 1) [on deskA 1; on deskA 2; unreadable 3] = Settled)

check "a window that becomes readable again on its own desktop is a stray"
    (decide (before [on deskA 1; on deskA 2; unreadable 3]) (h 1) movedThird
        = Straddling(deskA, [h 3]))

printfn ""
printfn "---- what is worth acting on"

check "one window out of three having left is believed"
    (plausible (before together) movedThird (decide (before together) (h 1) movedThird))

check "one window of a pair having left is believed"
    // Half of a pair is one window, which is what a person moves.
    (plausible nothingBefore [on deskA 1; on deskB 2] (Straddling(deskA, [h 2])))

check "most of a group that was never seen otherwise is not believed"
    (plausible nothingBefore [on deskA 1; on deskA 2; on deskB 3; on deskB 4; on deskB 5]
        (Straddling(deskA, [h 3; h 4; h 5])) = false)

check "most of a group IS believed when they were watched moving"
    // Dragging a group to another desktop in task view is one window at a
    // time, and all but the last of them will have moved.
    (plausible (before [on deskA 1; on deskA 2; on deskA 3; on deskA 4; on deskA 5])
        [on deskA 1; on deskA 2; on deskB 3; on deskB 4; on deskB 5]
        (Straddling(deskA, [h 3; h 4; h 5])))

check "a settled group is never acted on"
    (plausible (before together) together Settled = false)

check "two out of five having left is believed"
    (plausible nothingBefore [on deskA 1; on deskA 2; on deskA 3; on deskB 4; on deskB 5]
        (Straddling(deskA, [h 4; h 5])))

printfn ""
printfn "---- the same answer, twice"

let straddle = decide (before together) (h 1) movedThird

check "a straddle seen twice is the same straddle"
    (confirmed straddle straddle = Some(deskA, [h 3]))

check "a straddle seen once is not"
    (confirmed Settled straddle = None)

check "a straddle that has gone is not"
    (confirmed straddle Settled = None)

check "a straddle whose base changed is not"
    (confirmed straddle (Straddling(deskB, [h 3])) = None)

check "a straddle whose strays changed is not"
    (confirmed straddle (Straddling(deskA, [h 2; h 3])) = None)

check "the order the windows are read in does not matter"
    (confirmed straddle (decide (before together) (h 1) [on deskB 3; on deskA 2; on deskA 1])
        = Some(deskA, [h 3]))

printfn ""
printfn "---- three desktops"

check "windows on two other desktops are all strays"
    (decide (before [on deskA 1; on deskA 2; on deskA 3; on deskA 4; on deskA 5])
        (h 1) [on deskA 1; on deskA 2; on deskA 3; on deskB 4; on deskC 5]
        = Straddling(deskA, [h 4; h 5]))

printfn ""
printfn "%d checks, %d failures" checks failures
if failures > 0 then exit 1
