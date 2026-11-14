// TestRuntime: Framework
// fsi tests for the pure parts of the host glass (TabGlassMath.glassRuns,
// intersectRuns, glassRects): which pixels get Windows' blur behind them and
// how they become rectangles; and TabGlassWay: which way a strip takes.
// Run: pwsh -File Tools/Run-Tests.ps1 -Filter TabGlassHost.Tests.fsx
// Covers energy saver, mask refusal, placement flags, per-run
// rectangles.
#r "WtProgram/bin/Debug/WindowTabs.exe"
open System.Drawing
open Bemo

let mutable failures = 0
let mutable passes = 0
let check name cond =
    if cond then passes <- passes + 1
    else
        failures <- failures + 1
        printfn "FAIL: %s" name

let rowOf (alphas: int list) = alphas |> List.collect (fun a -> [ 10uy; 20uy; 30uy; byte a ]) |> Array.ofList
let runs alphas dimmed s d =
    let row = rowOf alphas
    let n = List.length alphas
    let mask = match dimmed with Some m -> Array.ofList m | None -> Array.create n false
    TabGlassMath.glassRuns row mask n s d

// ----- glassRuns: an inactive group (whole window at 0x80) -----
check "inactive: 0 and 1 never" (runs [ 0; 1 ] None 128 128 = [])
check "inactive: opaque tab pixel" (runs [ 255 ] None 128 128 = [ (0, 1) ])
check "inactive: half covered edge" (runs [ 127; 128 ] None 128 128 = [ (1, 2) ])
check "inactive: runs split by a gap" (runs [ 255; 255; 0; 255 ] None 128 128 = [ (0, 2); (3, 4) ])
check "inactive: run reaching the end" (runs [ 0; 200; 200 ] None 128 128 = [ (1, 3) ])
check "window at opacity 1: only what it still catches" (runs [ 254; 255 ] None 1 128 = [ (1, 2) ])
check "window at opacity 0" (runs [ 255 ] None 0 128 = [])

// ----- glassRuns: an opaque strip with a faded tab (another desktop) -----
check "opaque, no faded tab: nothing" (runs [ 255; 128 ] None 255 128 = [])
check "faded tab body" (runs [ 128 ] (Some [ true ]) 255 128 = [ (0, 1) ])
check "faded tab edge below half" (runs [ 64 ] (Some [ true ]) 255 128 = [])
check "faded tab edge from half" (runs [ 65 ] (Some [ true ]) 255 128 = [ (0, 1) ])
check "opaque tab over a faded one" (runs [ 200 ] (Some [ true ]) 255 128 = [])
check "faded rect, background pixel" (runs [ 1 ] (Some [ true ]) 255 128 = [])
check "only marked pixels" (runs [ 128; 128 ] (Some [ true; false ]) 255 128 = [ (0, 1) ])

// Every pixel that gets the blur is one the strip catches clicks on (the
// backdrop never takes a click the strip let through).
let rnd = System.Random(11)
let mutable notHit = 0
let mutable missed = 0
for _ in 1 .. 20000 do
    let a = rnd.Next(256)
    let s = rnd.Next(256)
    let d = 1 + rnd.Next(255)
    let dimmed = rnd.Next(2) = 0
    match runs [ a ] (Some [ dimmed ]) s d with
    | [ _ ] -> if a * s / 255 = 0 then notHit <- notHit + 1
    | _ ->
        // A fully covered translucent tab pixel always gets it.
        let translucent = (dimmed && a <= d) || s < 255
        if a = 255 && s > 0 && s < 255 then missed <- missed + 1
        ignore translucent
check (sprintf "blurred pixels are hit pixels (%d not)" notHit) (notHit = 0)
check (sprintf "full tab pixels of a translucent window are blurred (%d missed)" missed) (missed = 0)

// ----- intersectRuns -----
check "intersect equal" (TabGlassMath.intersectRuns [ (0, 5) ] [ (0, 5) ] = [ (0, 5) ])
check "intersect overlap" (TabGlassMath.intersectRuns [ (0, 5) ] [ (3, 9) ] = [ (3, 5) ])
check "intersect disjoint" (TabGlassMath.intersectRuns [ (0, 3) ] [ (3, 9) ] = [])
check "intersect several" (TabGlassMath.intersectRuns [ (0, 4); (6, 10) ] [ (2, 8) ] = [ (2, 4); (6, 8) ])
check "intersect empty" (TabGlassMath.intersectRuns [] [ (0, 1) ] = [])

// ----- glassRects -----
let rect x y w h = Rectangle(x, y, w, h)
check "no rows" (TabGlassMath.glassRects [||] 10 = [])
check "equal rows merge" (TabGlassMath.glassRects [| [ (2, 8) ]; [ (2, 8) ]; [ (2, 8) ] |] 10 = [ rect 2 0 6 3 ])
check "differing rows stay apart"
    (TabGlassMath.glassRects [| [ (3, 7) ]; [ (2, 8) ]; [ (2, 8) ] |] 10 = [ rect 3 0 4 1; rect 2 1 6 2 ])
check "empty rows leave holes" (TabGlassMath.glassRects [| [ (0, 4) ]; []; [ (0, 4) ] |] 10 = [ rect 0 0 4 1; rect 0 2 4 1 ])
check "two tabs side by side" (TabGlassMath.glassRects [| [ (0, 4); (6, 9) ]; [ (0, 4); (6, 9) ] |] 10 = [ rect 0 0 4 2; rect 6 0 3 2 ])

// A tab-like shape: sides that move every row (an S-curve), 40 rows, ten tabs.
let shapeRows =
    [| for y in 0 .. 39 ->
        let inset = (39 - y) / 2
        [ for t in 0 .. 9 -> (t * 100 + inset, t * 100 + 90 - inset) ] |]
let exact = TabGlassMath.glassRects shapeRows 100000
let fitted = TabGlassMath.glassRects shapeRows 96
let covered (rects: Rectangle list) =
    let pixels = System.Collections.Generic.HashSet<int * int>()
    for r in rects do
        for y in r.Top .. r.Bottom - 1 do
            for x in r.Left .. r.Right - 1 do pixels.Add((x, y)) |> ignore
    pixels
let inShape (x, y) = shapeRows.[y] |> List.exists (fun (a, b) -> x >= a && x < b)
let exactPixels = covered exact
let shapePixels = shapeRows |> Array.mapi (fun y r -> r |> List.sumBy (fun (a, b) -> b - a)) |> Array.sum
check (sprintf "exact rectangles cover the shape exactly (%d of %d)" exactPixels.Count shapePixels)
    (exactPixels.Count = shapePixels && exactPixels |> Seq.forall inShape)
check (sprintf "exact rectangles do not overlap (%d)" (exact |> List.sumBy (fun r -> r.Width * r.Height)))
    (exact |> List.sumBy (fun r -> r.Width * r.Height) = shapePixels)
check (sprintf "coarsened to the limit (%d > 96 before, %d after)" exact.Length fitted.Length)
    (exact.Length > 96 && fitted.Length <= 96)
let fittedPixels = covered fitted
check "coarsened rectangles stay inside the shape" (fittedPixels |> Seq.forall inShape)
check (sprintf "coarsened rectangles keep most of it (%d of %d)" fittedPixels.Count shapePixels)
    (float fittedPixels.Count >= 0.9 * float shapePixels)

// Random shapes: never outside the given pixels, at any limit.
let mutable outside = 0
let mutable overLimit = 0
for trial in 1 .. 300 do
    let height = 1 + rnd.Next(60)
    let rows =
        Array.init height (fun _ ->
            let mutable x = 0
            [ while x < 300 do
                let a = x + rnd.Next(20)
                let b = a + 1 + rnd.Next(40)
                if b <= 300 then yield (a, b)
                x <- b + 1 ])
    let limit = 1 + rnd.Next(120)
    let rects = TabGlassMath.glassRects rows limit
    for r in rects do
        for y in r.Top .. r.Bottom - 1 do
            if not (rows.[y] |> List.exists (fun (a, b) -> r.Left >= a && r.Right <= b)) then outside <- outside + 1
    // Coarsening stops at one band of all rows; only then may it stay over.
    if rects.Length > limit && height > 1 then
        let single = TabGlassMath.glassRects [| rows |> Array.reduce TabGlassMath.intersectRuns |] 100000
        if rects.Length > single.Length then overLimit <- overLimit + 1
check (sprintf "random shapes: no rectangle outside its rows (%d)" outside) (outside = 0)
check (sprintf "random shapes: over the limit only when nothing coarser exists (%d)" overLimit) (overLimit = 0)

// ----- TabGlassWay: WINDOWTABS_DEBUG_GLASS -----
check "forced: null" (TabGlassWay.parseForced null = TabGlassWay.Auto)
check "forced: empty" (TabGlassWay.parseForced "" = TabGlassWay.Auto)
check "forced: host, any case and spaces" (TabGlassWay.parseForced " HoSt " = TabGlassWay.Host)
check "forced: host-probe" (TabGlassWay.parseForced "host-probe" = TabGlassWay.Probe)
check "forced: wallpaper" (TabGlassWay.parseForced "wallpaper" = TabGlassWay.Wallpaper)
check "forced: plain" (TabGlassWay.parseForced "plain" = TabGlassWay.Plain)
check "forced: unknown word" (TabGlassWay.parseForced "hosts" = TabGlassWay.Auto)
for f in [ TabGlassWay.Auto; TabGlassWay.Host; TabGlassWay.Probe; TabGlassWay.Wallpaper; TabGlassWay.Plain ] do
    let name = TabGlassWay.forcedName f
    check (sprintf "forced name round trip %s" name) (f = TabGlassWay.Auto || TabGlassWay.parseForced name = f)

// ----- TabGlassWay: what blocks Windows' blur -----
let good : TabGlassWay.Conditions =
    { transparency = true; highContrast = false; powerSaver = false; energySaver = None; remote = false
      effectsSupported = Some true; effectsFast = Some true }
check "blocked: nothing" (TabGlassWay.blockedBy good = None)
check "blocked: transparency off" (TabGlassWay.blockedBy { good with transparency = false } = Some "transparency-effects-off")
check "blocked: high contrast" (TabGlassWay.blockedBy { good with highContrast = true } = Some "high-contrast")
check "blocked: energy saver" (TabGlassWay.blockedBy { good with powerSaver = true } = Some "energy-saver")
check "blocked: effects unsupported" (TabGlassWay.blockedBy { good with effectsSupported = Some false } = Some "effects-unsupported")
check "not blocked: effects unknown" (TabGlassWay.blockedBy { good with effectsSupported = None; effectsFast = None } = None)
check "not blocked: remote session (recorded only)" (TabGlassWay.blockedBy { good with remote = true } = None)
check "not blocked: slow effects (recorded only)" (TabGlassWay.blockedBy { good with effectsFast = Some false } = None)
check "blocked: transparency first" (TabGlassWay.blockedBy { good with transparency = false; highContrast = true; powerSaver = true } = Some "transparency-effects-off")
check "unread conditions do not block" (TabGlassWay.blockedBy TabGlassWay.unread = None)
let described = TabGlassWay.describe { good with effectsFast = None }
check "describe: every value" (described = "transparency=true highContrast=false powerSaver=false energySaver=unknown remote=false effectsSupported=yes effectsFast=unknown")

// ----- TabGlassWay: the choice per strip window -----
let off = { good with transparency = false; effectsSupported = Some false }
let refused forced start conditions failed = TabGlassWay.hostRefused forced start conditions failed
check "auto ready good: host" (refused TabGlassWay.Auto TabGlassWay.Ready good false = None)
check "auto ready blocked: reason" (refused TabGlassWay.Auto TabGlassWay.Ready off false = Some "transparency-effects-off")
check "auto not started" (refused TabGlassWay.Auto TabGlassWay.NotStarted good false = Some "host-starting")
check "auto starting" (refused TabGlassWay.Auto TabGlassWay.Starting good false = Some "host-starting")
check "auto start failed" (refused TabGlassWay.Auto (TabGlassWay.Failed "compositor hr=0x80004005") good false = Some "host-start-failed compositor hr=0x80004005")
check "auto window failed" (refused TabGlassWay.Auto TabGlassWay.Ready good true = Some "host-window-failed")
check "forced host skips the conditions" (refused TabGlassWay.Host TabGlassWay.Ready off false = None)
check "forced probe skips the conditions" (refused TabGlassWay.Probe TabGlassWay.Ready off false = None)
check "forced host still waits for the start" (refused TabGlassWay.Host TabGlassWay.Starting off false = Some "host-starting")
check "forced host still falls back on a failed start" (refused TabGlassWay.Host (TabGlassWay.Failed "x") off false = Some "host-start-failed x")
check "forced host still falls back on a failed window" (refused TabGlassWay.Host TabGlassWay.Ready good true = Some "host-window-failed")
for start in [ TabGlassWay.NotStarted; TabGlassWay.Starting; TabGlassWay.Ready; TabGlassWay.Failed "x" ] do
    for failed in [ false; true ] do
        check (sprintf "forced wallpaper %A %b" start failed) (refused TabGlassWay.Wallpaper start good failed = Some "forced-wallpaper")
        check (sprintf "forced plain %A %b" start failed) (refused TabGlassWay.Plain start good failed = Some "forced-plain")
// Exhaustive: the host is used only when ready, its window did not fail,
// and either forced or nothing blocks.
let mutable wrong = 0
for forced in [ TabGlassWay.Auto; TabGlassWay.Host; TabGlassWay.Probe; TabGlassWay.Wallpaper; TabGlassWay.Plain ] do
    for start in [ TabGlassWay.NotStarted; TabGlassWay.Starting; TabGlassWay.Ready; TabGlassWay.Failed "x" ] do
        for failed in [ false; true ] do
            for t in [ false; true ] do
                for hc in [ false; true ] do
                    for ps in [ false; true ] do
                        for rm in [ false; true ] do
                            for es in [ None; Some false; Some true ] do
                                let c = { good with transparency = t; highContrast = hc; powerSaver = ps; remote = rm; effectsSupported = es }
                                let used = (refused forced start c failed).IsNone
                                let expected =
                                    start = TabGlassWay.Ready && not failed &&
                                    (match forced with
                                     | TabGlassWay.Host | TabGlassWay.Probe -> true
                                     | TabGlassWay.Auto -> t && not hc && not ps && es <> Some false
                                     | _ -> false)
                                if used <> expected then wrong <- wrong + 1
check (sprintf "exhaustive choice (%d wrong)" wrong) (wrong = 0)

// ----- host policy -----

// Energy saver: PowerManager decides where it was read; Disabled is not On.
check "energy saver on" (TabGlassWay.blockedBy { good with energySaver = Some 2 } = Some "energy-saver")
check "energy saver off" (TabGlassWay.blockedBy { good with energySaver = Some 1 } = None)
check "energy saver disabled (plugged in)" (TabGlassWay.blockedBy { good with energySaver = Some 0 } = None)
check "energy saver off overrides the battery flag" (TabGlassWay.blockedBy { good with energySaver = Some 1; powerSaver = true } = None)
check "battery flag when energy saver unread" (TabGlassWay.blockedBy { good with energySaver = None; powerSaver = true } = Some "energy-saver")
check "describe: energy saver on" ((TabGlassWay.describe { good with energySaver = Some 2 }).Contains "energySaver=on")
check "describe: energy saver disabled" ((TabGlassWay.describe { good with energySaver = Some 0 }).Contains "energySaver=disabled")
for es in [ None; Some 0; Some 1; Some 2 ] do
    for ps in [ false; true ] do
        let expected = (match es with Some s -> s = 2 | None -> ps)
        check (sprintf "savingEnergy %A %b" es ps) (TabGlassWay.savingEnergy { good with energySaver = es; powerSaver = ps } = expected)

// Mask: empty or too complex is refused for that drawing; the limit itself fits.
check "mask empty" (TabGlassWay.maskRefused 0 96 = Some "mask-empty")
check "mask one" (TabGlassWay.maskRefused 1 96 = None)
check "mask at limit" (TabGlassWay.maskRefused 96 96 = None)
check "mask over limit" (TabGlassWay.maskRefused 97 96 = Some "mask-too-complex rects=97")

// Placement flags: never activate, never reorder owned windows; change only what differs.
let has (flags: uint32) (f: uint32) = flags &&& f = f
for below in [ false; true ] do
    for moved in [ false; true ] do
        for shown in [ false; true ] do
            let f = TabGlassWay.placeFlags below moved shown
            let name = sprintf "flags below=%b moved=%b shown=%b" below moved shown
            check (name + ": no activate") (has f TabGlassWay.SWP_NOACTIVATE)
            check (name + ": no owner order") (has f TabGlassWay.SWP_NOOWNERZORDER)
            check (name + ": z only when not below") (has f TabGlassWay.SWP_NOZORDER = below)
            check (name + ": move only when moved") (has f TabGlassWay.SWP_NOMOVE = not moved && has f TabGlassWay.SWP_NOSIZE = not moved)
            check (name + ": show only when hidden") (has f TabGlassWay.SWP_SHOWWINDOW = not shown)
            check (name + ": never hides") (not (has f TabGlassWay.SWP_HIDEWINDOW))
            check (name + ": needed") (TabGlassWay.placeNeeded below moved shown = (not below || moved || not shown))
check "hide flags hide and change nothing else"
    (TabGlassWay.hideFlags = (TabGlassWay.SWP_HIDEWINDOW ||| TabGlassWay.SWP_NOMOVE ||| TabGlassWay.SWP_NOSIZE ||| TabGlassWay.SWP_NOZORDER ||| TabGlassWay.SWP_NOACTIVATE ||| TabGlassWay.SWP_NOOWNERZORDER))

// Per-run merge: a run grows down while it goes on, whatever its neighbours do.
check "per-run merge"
    (TabGlassMath.glassRects [| [ (0, 4); (6, 9) ]; [ (0, 4); (6, 8) ] |] 10 = [ rect 0 0 4 2; rect 6 0 3 1; rect 6 1 2 1 ])
check "per-run merge, neighbour appears"
    (TabGlassMath.glassRects [| [ (0, 4) ]; [ (0, 4); (6, 9) ]; [ (0, 4); (6, 9) ] |] 10 = [ rect 0 0 4 3; rect 6 1 3 2 ])
check "a run that stops and comes back is two rectangles"
    (TabGlassMath.glassRects [| [ (0, 4) ]; [ (5, 6) ]; [ (0, 4) ] |] 10 = [ rect 0 0 4 1; rect 5 1 1 1; rect 0 2 4 1 ])
// Tabs alike side by side: the per-run count is the tabs times the distinct
// rows of one tab, the same each time the same shape is given.
let tabRows tabs =
    [| for y in 0 .. 29 ->
        let inset = max 0 (4 - y)
        [ for t in 0 .. tabs - 1 -> (t * 120 + inset, t * 120 + 110 - inset) ] |]
let once = TabGlassMath.glassRects (tabRows 6) 96
let again = TabGlassMath.glassRects (tabRows 6) 96
check (sprintf "same shape, same rectangles (%d)" once.Length) (once = again)
check (sprintf "rounded tabs: tabs x distinct rows (%d)" once.Length) (once.Length = 6 * 5)
check (sprintf "one more tab, one tab's worth more (%d)" (TabGlassMath.glassRects (tabRows 7) 96).Length)
    ((TabGlassMath.glassRects (tabRows 7) 96).Length = 7 * 5)

// Random shapes, exact: the per-run rectangles cover the rows exactly, once.
let mutable inexact = 0
for trial in 1 .. 300 do
    let height = 1 + rnd.Next(40)
    let pick () =
        let mutable x = 0
        [ while x < 200 do
            let a = x + rnd.Next(20)
            let b = a + 1 + rnd.Next(30)
            if b <= 200 then yield (a, b)
            x <- b + 1 ]
    // Rows repeat often, as tab rows do.
    let rows = Array.zeroCreate height
    for y in 0 .. height - 1 do
        rows.[y] <- if y > 0 && rnd.Next(3) > 0 then rows.[y - 1] else pick ()
    let rects = TabGlassMath.glassRects rows 100000
    let seen = System.Collections.Generic.HashSet<int * int>()
    let mutable twice = false
    for r in rects do
        for y in r.Top .. r.Bottom - 1 do
            for x in r.Left .. r.Right - 1 do
                if not (seen.Add((x, y))) then twice <- true
    let expected = rows |> Array.mapi (fun y runs -> [ for a, b in runs do for x in a .. b - 1 -> (x, y) ]) |> Seq.concat |> Set.ofSeq
    if twice || Set.ofSeq seen <> expected then inexact <- inexact + 1
check (sprintf "random shapes: exact rectangles cover exactly once (%d)" inexact) (inexact = 0)

// An unknown or failed queue status must never permit apartment teardown.
for status in [None; Some 0; Some 2; Some 3; Some -1; Some 4] do
    check "queue remains owned" (not (TabGlassWay.queueCompleted status))
check "completed queue can be released" (TabGlassWay.queueCompleted (Some 1))
let failedOnce = TabGlassWay.nextFailures 0 false
let failedTwice = TabGlassWay.nextFailures failedOnce false
let failedThird = TabGlassWay.nextFailures failedTwice false
check "one failure retries" (not (TabGlassWay.failuresExhausted failedOnce))
check "two failures retry" (not (TabGlassWay.failuresExhausted failedTwice))
check "third failure stops" (TabGlassWay.failuresExhausted failedThird)
check "failure count saturates" (TabGlassWay.nextFailures failedThird false = failedThird)
check "success resets consecutive failures" (TabGlassWay.nextFailures failedTwice true = 0)

let before = Rectangle(1, 2, 3, 4)
let after = Rectangle(5, 6, 7, 8)
check "identical geometry remains visible" (not (TabGlassWay.geometryChanging before before 0u))
check "move hides old backdrop" (TabGlassWay.geometryChanging before (Rectangle(5, 6, 3, 4)) TabGlassWay.SWP_NOSIZE)
check "resize hides old backdrop" (TabGlassWay.geometryChanging before (Rectangle(1, 2, 7, 8)) TabGlassWay.SWP_NOMOVE)
check "ignored WINDOWPOS values do not hide" (not (TabGlassWay.geometryChanging before after (TabGlassWay.SWP_NOMOVE ||| TabGlassWay.SWP_NOSIZE)))
check "hide always hides backdrop" (TabGlassWay.geometryChanging before before TabGlassWay.SWP_HIDEWINDOW)

// Synthetic elapsed counters exercise the retry boundary, not wall-clock times.
check "no failure has no retry" (not (TabGlassWay.retryDue 6000L 0L))
check "retry waits for cooldown" (not (TabGlassWay.retryDue 4999L 5000L))
check "retry at deadline" (TabGlassWay.retryDue 5000L 5000L)
check "retry after deadline" (TabGlassWay.retryDue 5001L 5000L)
for reason in ["backdrop-cloaked"; "not-below-strip"; "bounds-changed"; "strip-hidden";
               "backdrop-hidden"; "layer-mismatch"; "place-again"; "place-again-exhausted"] do
    check "transient placement failure gets a short first wait" (TabGlassWay.transientFailure reason)
for reason in ["hide-failed"; "place-failed"; "region-host-call-failed"] do
    check "native failure gets the normal first wait" (not (TabGlassWay.transientFailure reason))

// A finite retry budget applies even if every placement failure is transient.
check "transient first wait" (TabGlassWay.retryDelay 0 true = 500L)
check "native first wait" (TabGlassWay.retryDelay 0 false = 5000L)
for transient in [false; true] do
    check "second wait backs off" (TabGlassWay.retryDelay 1 transient = 10000L)
    check "third wait backs off" (TabGlassWay.retryDelay 2 transient = 20000L)
    check "budget stops retries" (TabGlassWay.retryDelay 3 transient = -1L)
    check "exhausted deadline never becomes due" (not (TabGlassWay.retryDue System.Int64.MaxValue -1L))
    let mutable count = 0
    let mutable waits = []
    while not (TabGlassWay.failuresExhausted count) do
        waits <- TabGlassWay.retryDelay count transient :: waits
        count <- TabGlassWay.nextFailures count false
    check "only three retries without success or invalidation" (waits.Length = 3 && TabGlassWay.retryDelay count transient < 0L)
    check "reset allows a fresh attempt" (TabGlassWay.retryDelay (TabGlassWay.nextFailures count true) transient > 0L)

printfn "passed %d, failed %d" passes failures
if failures > 0 then exit 1
