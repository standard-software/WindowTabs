#r "../Newtonsoft.Json.dll"
#load "../WtProgram/Shared/TabOrder.fs"
#load "../WtProgram/Shared/Regroup.fs"
#load "../WtProgram/Shared/SavedTabState.fs"
#load "../WtProgram/Shared/AppPath.fs"
#load "../WtProgram/Shared/RestorePosition.fs"
#load "../WtProgram/Shared/SavedSession.fs"
#load "../WtProgram/Shared/VirtualDesktopIntegrity.fs"
#load "../WtProgram/Shared/VirtualDesktopGroups.fs"
#load "../WtProgram/Shared/VirtualDesktopReader.fs"
open System
open System.Threading
open Bemo
let a = Some(Guid "00000000-0000-0000-0000-000000000001")
let b = Some(Guid "00000000-0000-0000-0000-000000000002")
let unlisted = Some(Guid "00000000-0000-0000-0000-000000000003")
let check name expected actual = if expected <> actual then failwithf "%s: %A <> %A" name expected actual
let cases = [
    "known-here", a, a, Some true, Some 0, true, 0L, Some true
    "no-current", None, a, Some true, Some 0, true, 0L, Some true
    "com-failure", a, a, None, Some 0, true, 0L, Some true
    "cloak-failure", a, a, Some true, None, true, 0L, Some true
    "both-fail", None, None, None, None, true, 0L, Some true
    "pending", None, None, None, None, false, 999L, None
    "hung", None, None, None, None, false, 1000L, Some true
    "com-away", None, None, Some false, None, true, 1000L, Some false
    "shell-cloak", None, None, None, Some 2, true, 1000L, Some false
    "conflicting-cloak", a, a, Some true, Some 2, true, 0L, Some false
    "shared-origin", a, b, Some true, Some 0, true, 0L, Some true
    "shared-unlisted", a, unlisted, Some true, Some 0, true, 0L, Some true
    "shared-cloak-unknown", a, b, Some true, None, true, 0L, Some true
    "unknown-mismatched-id", a, b, None, Some 0, true, 0L, Some false
    "unknown-unlisted-id", a, unlisted, None, None, true, 0L, Some false
    "mismatched-com-false", a, b, Some false, Some 0, true, 0L, Some false
    "shared-shell-cloak", a, b, Some true, Some 2, true, 0L, Some false
    "app-cloak", a, a, Some true, Some 1, true, 0L, Some true
]
for name, current, desktop, onCurrent, cloak, hasRead, elapsed, expected in cases do
    let actual = VirtualDesktopReader.firstTabDecision elapsed true current desktop onCurrent cloak hasRead
    check name expected actual
    let old = (defaultArg onCurrent true) && not (cloak |> Option.exists (fun c -> c <> 0))
    let strict = hasRead && current.IsSome && VirtualDesktopGroups.presence onCurrent cloak = VirtualDesktopGroups.Here
    printfn "%s: legacy=%b strict=%b fixed=%A" name old strict actual
check "unsupported" (Some true) (VirtualDesktopReader.firstTabDecision 0L false a b (Some false) (Some 2) true)
let h = IntPtr 1
let waiters = VirtualDesktopReader.FirstTabWaiters()
check "start" 0L (waiters.Elapsed(h, 10L))
check "no-expiry" [] (waiters.Expired 1009L)
check "expiry" [h] (waiters.Expired 1010L)
check "deadline-preserved" 1000L (waiters.Elapsed(h, 1010L))
waiters.Remove h
check "removed" Set.empty waiters.Windows
// Completion wakes only candidates included in the accepted publication.
let sample: VirtualDesktopReader.Reading = {
    request = { grouped = Set.empty; windows = [h] }
    readAt = DateTime.MinValue; supported = true; listed = Some [a.Value; b.Value]
    registryBefore = a; registryAfter = a
    reads = Map.ofList [h, { hwnd = h; presence = VirtualDesktopGroups.Here; desktop = b }]
    desktopResults = Map.empty; cloak = Map.ofList [h, Some 0]
    onCurrent = Map.ofList [h, Some true] }
check "no-waiter-no-rescan" false (waiters.HasReading sample)
waiters.Elapsed(h, 0L) |> ignore
check "completion-before-deadline" true (waiters.HasReading sample)
check "no-current-completion" true (waiters.HasReading { sample with registryBefore = None; registryAfter = None })
check "unknown-answer-completion" true (waiters.HasReading { sample with onCurrent = Map.ofList [h, None] })
check "not-in-request" false (waiters.HasReading { sample with onCurrent = Map.empty })
check "unsupported-completion" true (waiters.HasReading { sample with supported = false; onCurrent = Map.empty })
check "completion-keeps-deadline" 50L (waiters.Elapsed(h, 50L))
check "completion-admitted" (Some true) (VirtualDesktopReader.firstTabDecision 50L true a b (Some true) (Some 0) true)
waiters.Remove h
check "resolved-no-repeat-rescan" false (waiters.HasReading sample)
// A real blocked reader cannot publish or accept an overlapping request.
let entered = new ManualResetEventSlim(false)
let release = new ManualResetEventSlim(false)
let finished = new ManualResetEventSlim(false)
let reader = new VirtualDesktopReader.Reader<int,int>((fun n -> entered.Set(); release.Wait(); n), (fun () -> finished.Set()), ignore)
check "request" true (reader.Request 1)
check "entered" true (entered.Wait 5000)
check "busy" false (reader.Request 2)
check "unpublished" None reader.Latest
check "hung-admitted" (Some true) (VirtualDesktopReader.firstTabDecision 1000L true None None None None false)
release.Set()
check "completed" true (finished.Wait 5000)
// Existing membership gates still reject unknown evidence and ordinary duplicates.
let state = VirtualDesktopGroups.Live.snapshot()
check "unknown-not-shared" false (VirtualDesktopGroups.Live.mayDuplicate state h)
check "unknown-not-moved" false (VirtualDesktopGroups.Live.mayMove state a h)
check "grouped-not-new" false (VirtualDesktopGroups.wantsGroup false false true)
printfn "All first-tab checks passed."

reader.Stop()
