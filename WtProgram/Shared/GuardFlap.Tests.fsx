#load "SnapGeometry.fs"
#load "WindowFrameMargin.fs"
#load "TopEdgeGuardPolicy.fs"
open Bemo.TopEdgeGuardPolicy
let mutable passed = 0
let check name expected actual =
    if expected <> actual then failwithf "%s: expected %A, got %A" name expected actual
    passed <- passed + 1
let previous = function | 1n -> 2n | 2n -> 3n | 3n -> 4n | _ -> 0n
check "hidden predecessors skipped" (Some 4n) (visiblePredecessor previous ((=) 4n) 1n)
check "visible foreign barrier retained" (Some 2n) (visiblePredecessor previous (fun _ -> true) 1n)
check "empty chain" (Some 0n) (visiblePredecessor previous (fun _ -> false) 1n)
check "cycle fails closed" None (visiblePredecessor (fun _ -> 2n) (fun _ -> false) 1n)
for height in [6;8;9;16;28] do
    let owner = {x=0;y=0;width=1000;height=800}
    let band = rectangle 0 0 900 0 height 25
    check "height retained" height band.height
    check "corner gap retained" 25 (clipHorizontal owner 25 band).x
    check "visible move has no show/hide" 0x0214u (positionFlags true true)
    check "visible reorder has no show/hide" 0x0210u (positionFlags true false)
    check "hidden initial placement shows" 0x0250u (positionFlags false false)
let body = {x=0;y=10;width=1000;height=800}
let band = rectangle 0 0 1000 10 8 25
let frame = {x=0;y=0;width=1000;height=10}
check "owned top frame remains covered" 18 (coverTopFrames 96 body band [frame]).height
// Visible zero-area shell surfaces must not choose the insertion anchor.
let rect width height = Some {x=0;y=0;width=width;height=height}
check "zero width cannot intercept" false (blocksInput true (rect 0 1080))
check "zero height cannot intercept" false (blocksInput true (rect 1920 0))
check "hidden nonempty cannot intercept" false (blocksInput false (rect 1920 1080))
check "unknown geometry remains barrier" true (blocksInput true None)
let input hwnd = blocksInput true (if hwnd = 2n then rect 0 0 else rect 1920 1080)
check "empty visible predecessor skipped" (Some 3n) (visiblePredecessor previous input 1n)
let topmost hwnd = hwnd = 2n || hwnd = 3n
check "interleaved topmost rejected" None (ownerInsertion 9n (Some 3n) previous input topmost)
check "empty then interleaved rejected" None
    (ownerInsertion 9n (visiblePredecessor previous input 1n) previous input topmost)
check "normal topmost boundary allowed" (Some(0n,false))
    (ownerInsertion 9n (Some 3n) previous input (fun _ -> true))
check "ordinary predecessor used directly" (Some(4n,false))
    (ownerInsertion 9n (Some 4n) previous input topmost)
check "already adjacent retains zorder" (Some(0n,true))
    (ownerInsertion 9n (Some 9n) previous input topmost)
check "unproven predecessor rejected" None (ownerInsertion 9n None previous input topmost)
check "cyclic layer scan rejected" None
    (ownerInsertion 9n (Some 3n) (fun _ -> 3n) input topmost)
let mutable shows = 0
let show () = shows <- shows+1; true
check "failed native placement stays hidden" false (placeVerified false (fun () -> false) (fun () -> true) show)
check "bad native order stays hidden" false (placeVerified false (fun () -> true) (fun () -> false) show)
check "failed validation never shows" 0 shows
check "valid hidden placement shows" true (placeVerified false (fun () -> true) (fun () -> true) show)
check "one show after validation" 1 shows
check "visible reposition does not show again" true (placeVerified true (fun () -> true) (fun () -> true) show)
check "no redundant show" 1 shows
let retry = HiddenRetry<int>()
check "initial attempt" true (retry.Allow(0L,1))
retry.Fail(0L,1)
for now in [50L;100L;1000L;9999L] do
    check "same failure backs off" false (retry.Allow(now,1))
    check "repair timer backs off" false (retry.Ready now)
check "ten second retry" true (retry.Allow(10000L,1))
check "changed owner geometry/order retries immediately" true (retry.Allow(1L,2))
retry.Reset()
check "foreground invalidation retries" true (retry.Allow(1L,1))
printfn "GuardFlap: %d passed" passed

// Exercise the compiled Debug counters without writing a log or using windows.
if fsi.CommandLineArgs.Length > 1 then
    let assembly = System.Reflection.Assembly.LoadFrom(fsi.CommandLineArgs.[1])
    let t = assembly.GetType("Bemo.Win32.ShellCallTrace", true)
    let field name = t.GetField(name, System.Reflection.BindingFlags.NonPublic ||| System.Reflection.BindingFlags.Static).GetValue(null)
    (field "Timer" :?> System.IDisposable).Dispose()
    let call name args = t.GetMethod(name).Invoke(null, args) |> ignore
    let a, b = 1001n, 1002n
    call "GuardRegister" [|box a|]
    call "GuardRegister" [|box b|]
    call "GuardVisibility" [|box a; box false; box true|]
    call "GuardVisibility" [|box a; box true; box true|]
    [|1..1000|] |> Array.Parallel.iter (fun _ -> call "GuardVisibility" [|box b; box true; box false|])
    let guards = field "Guards" :?> System.Collections.IDictionary
    let value hwnd name = let counts = guards.[hwnd] in counts.GetType().GetField(name).GetValue(counts)
    check "actual show only" 1L (unbox<int64> (value a "Show"))
    check "no false hide" 0L (unbox<int64> (value a "Hide"))
    check "concurrent counts remain separate" 1000L (unbox<int64> (value b "Hide"))
    call "GuardUnregister" [|box a|]
    check "retired counter survives for history" true (unbox<bool> (value a "Retired"))
    printfn "GuardFlap including counters: %d passed" passed
