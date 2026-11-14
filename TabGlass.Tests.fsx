// TestRuntime: Framework
// fsi tests for the pure parts of TabGlass (placement, blur, per-pixel mix).
// Run: pwsh -File Tools/Run-Tests.ps1 -Filter TabGlass.Tests.fsx
#r "WtProgram/bin/Debug/WindowTabs.exe"
open Bemo

let mutable failures = 0
let mutable passes = 0
let check name cond =
    if cond then passes <- passes + 1
    else
        failures <- failures + 1
        printfn "FAIL: %s" name

let near (a: float) (b: float) = abs (a - b) < 1e-6
let rectIs name (x, y, w, h) (ex, ey, ew, eh) =
    check (sprintf "%s: got %A expected %A" name (x, y, w, h) (ex, ey, ew, eh))
        (near x ex && near y ey && near w ew && near h eh)

// ----- placement -----
let monitor = 0.0, 0.0, 1920.0, 1080.0
rectIs "fill same aspect" (TabGlassMath.placement TabGlassMath.Fill (0.0, 0.0, 2560.0, 1440.0) (1920, 1080)) (0.0, 0.0, 2560.0, 1440.0)
rectIs "fill crops wide" (TabGlassMath.placement TabGlassMath.Fill monitor (1000, 1000)) (0.0, -420.0, 1920.0, 1920.0)
rectIs "fit letterboxes" (TabGlassMath.placement TabGlassMath.Fit monitor (1000, 1000)) (420.0, 0.0, 1080.0, 1080.0)
rectIs "center unscaled" (TabGlassMath.placement TabGlassMath.Center monitor (800, 600)) (560.0, 240.0, 800.0, 600.0)
rectIs "center larger" (TabGlassMath.placement TabGlassMath.Center monitor (3840, 2160)) (-960.0, -540.0, 3840.0, 2160.0)
rectIs "stretch" (TabGlassMath.placement TabGlassMath.Stretch monitor (10, 20)) monitor
rectIs "tile first tile" (TabGlassMath.placement TabGlassMath.Tile monitor (64, 32)) (0.0, 0.0, 64.0, 32.0)
// Span: two 1920x1080 side by side, viewed from the right monitor.
rectIs "span right monitor" (TabGlassMath.placement TabGlassMath.Span (-1920.0, 0.0, 3840.0, 1080.0) (3840, 1080)) (-1920.0, 0.0, 3840.0, 1080.0)
rectIs "unknown = fill" (TabGlassMath.placement 99 monitor (1000, 1000)) (0.0, -420.0, 1920.0, 1920.0)
rectIs "zero-size picture is safe" (TabGlassMath.placement TabGlassMath.Fit monitor (0, 0)) (420.0, 0.0, 1080.0, 1080.0)

// ----- reduced size -----
check "reduced 4K" (TabGlassMath.reducedSize (3840, 2160) = (480, 270))
check "reduced odd" (TabGlassMath.reducedSize (1366, 768) = (171, 96))
check "reduced tiny" (TabGlassMath.reducedSize (1, 1) = (1, 1))

// ----- blur -----
let image w h (f: int -> int -> byte * byte * byte * byte) =
    let p = Array.zeroCreate<byte> (w * h * 4)
    for y in 0 .. h - 1 do
        for x in 0 .. w - 1 do
            let b, g, r, a = f x y
            let i = (y * w + x) * 4
            p.[i] <- b; p.[i + 1] <- g; p.[i + 2] <- r; p.[i + 3] <- a
    p
let uniform = image 13 7 (fun _ _ -> 10uy, 120uy, 250uy, 255uy)
let blurred = Array.copy uniform
TabGlassMath.blur blurred 13 7 2
check "blur keeps a uniform picture (edges included)" (blurred = uniform)
let dot = image 21 21 (fun x y -> if x = 10 && y = 10 then 255uy, 255uy, 255uy, 255uy else 0uy, 0uy, 0uy, 255uy)
TabGlassMath.blur dot 21 21 2
let at (p: byte[]) w x y = int p.[(y * w + x) * 4]
check "blur spreads a dot" (at dot 21 10 10 < 255 && at dot 21 12 10 > 0 && at dot 21 10 13 > 0)
check "blur is symmetric" (at dot 21 8 10 = at dot 21 12 10 && at dot 21 10 8 = at dot 21 10 12)
check "blur reaches no further than 3 passes" (at dot 21 3 10 = 0 && at dot 21 17 10 = 0)
let total = [ for y in 0 .. 20 do for x in 0 .. 20 -> at dot 21 x y ] |> List.sum
check (sprintf "blur keeps the light roughly (sum %d)" total) (abs (total - 255) <= 40)
let bigRadius = image 3 2 (fun x _ -> byte (x * 100), 0uy, 0uy, 255uy)
TabGlassMath.blur bigRadius 3 2 5
check "blur radius larger than picture is safe" (bigRadius.Length = 24)

// ----- composeRow -----
let px (b, g, r, a) = [| b; g; r; a |]
let compose (pixel: byte * byte * byte * byte) (back: byte * byte * byte * byte) dimmed s =
    let row = px pixel
    TabGlassMath.composeRow row (px back) [| dimmed |] 1 s 128
    row.[0], row.[1], row.[2], row.[3]
let black = 0uy, 0uy, 0uy, 255uy
let noBackdrop = 0uy, 0uy, 0uy, 0uy

// Whole strip faded (inactive group).
check "faded opaque tab pixel: half tab, half backdrop, opaque"
    (compose (0uy, 100uy, 200uy, 255uy) black false 128 = (0uy, 50uy, 100uy, 255uy))
check "faded strip background (alpha 1) stays click-through like before"
    (compose (1uy, 1uy, 1uy, 1uy) black false 128 = (1uy, 1uy, 1uy, 0uy))
check "fully transparent pixel stays transparent"
    (compose (0uy, 0uy, 0uy, 0uy) black false 128 = (0uy, 0uy, 0uy, 0uy))
check "antialiased edge keeps its coverage"
    (let _, _, _, a = compose (0uy, 100uy, 200uy, 100uy) black false 128 in a = 100uy)
check "no backdrop: the old opacity, colour untouched"
    (compose (0uy, 100uy, 200uy, 255uy) noBackdrop false 128 = (0uy, 100uy, 200uy, 128uy))

// Opaque strip with a tab from another virtual desktop.
check "dimmed tab body: full coverage, half mix"
    (compose (0uy, 100uy, 200uy, 128uy) black true 255 = (0uy, 50uy, 100uy, 255uy))
check "dimmed tab edge: coverage restored from the faded alpha"
    (let _, _, _, a = compose (0uy, 100uy, 200uy, 64uy) black true 255 in a = 127uy)
check "dimmed tab gap pixel (alpha 1) unchanged"
    (compose (1uy, 1uy, 1uy, 1uy) black true 255 = (1uy, 1uy, 1uy, 1uy))
check "opaque tab drawn over a dimmed one is not glassed"
    (compose (0uy, 100uy, 200uy, 255uy) black true 255 = (0uy, 100uy, 200uy, 255uy))
check "antialiased edge of an opaque tab outside dimmed tabs is unchanged"
    (compose (0uy, 100uy, 200uy, 128uy) black false 255 = (0uy, 100uy, 200uy, 128uy))
check "dimmed tab in a faded strip: a quarter tab"
    (compose (0uy, 200uy, 255uy, 128uy) black true 128 = (0uy, 50uy, 64uy, 255uy))
check "window alpha 0: nothing visible"
    (let _, _, _, a = compose (0uy, 100uy, 200uy, 255uy) black false 0 in a = 0uy)

// Over a backdrop equal to what was on screen, an opaque pixel looks as before.
let rnd = System.Random(7)
let mutable worst = 0
for _ in 1 .. 20000 do
    let c = byte (rnd.Next 256)
    let bg = byte (rnd.Next 256)
    let s = 128
    let b, _, _, a = compose (c, c, c, 255uy) (bg, bg, bg, 255uy) false s
    let before = (int c * s + int bg * (255 - s)) / 255
    worst <- max worst (abs (int b - before))
    if a <> 255uy then worst <- 999
check (sprintf "glass over the same colour matches the old blend (worst %d)" worst) (worst <= 1)

// Pixels other than tab pixels: same effective alpha as the old window opacity.
let mutable alphaMismatch = 0
for a in 0 .. 255 do
    for s in [ 0; 1; 64; 128; 200; 255 ] do
        let row = [| 9uy; 9uy; 9uy; byte a |]
        TabGlassMath.composeRow row [| 0uy; 0uy; 0uy; 0uy |] [| false |] 1 s 128
        if int row.[3] <> a * s / 255 then alphaMismatch <- alphaMismatch + 1
check (sprintf "without backdrop the effective alpha is unchanged (%d mismatches)" alphaMismatch) (alphaMismatch = 0)

// A whole row: only marked pixels count as dimmed.
let row = Array.concat [ px (0uy, 100uy, 200uy, 128uy); px (0uy, 100uy, 200uy, 128uy) ]
TabGlassMath.composeRow row (Array.concat [ px black; px black ]) [| true; false |] 2 255 128
check "row: first pixel glassed, second untouched" (row = [| 0uy; 50uy; 100uy; 255uy; 0uy; 100uy; 200uy; 128uy |])

// Safety limits and independent reduction scales.
check "zero monitors rejected" (not (TabGlassMath.validMonitorCount 0u))
check "one monitor accepted" (TabGlassMath.validMonitorCount 1u)
check "monitor upper bound accepted" (TabGlassMath.validMonitorCount 64u)
check "excess monitors rejected" (not (TabGlassMath.validMonitorCount 65u))
check "unsigned maximum rejected" (not (TabGlassMath.validMonitorCount System.UInt32.MaxValue))
check "empty file rejected" (not (TabGlassMath.validFileSize 0L))
check "file upper bound accepted" (TabGlassMath.validFileSize (64L * 1024L * 1024L))
check "oversized file rejected" (not (TabGlassMath.validFileSize (64L * 1024L * 1024L + 1L)))
check "zero image rejected" (not (TabGlassMath.validImageSize 0 1))
check "pixel upper bound accepted" (TabGlassMath.validImageSize 8000 4000)
check "oversized image rejected" (not (TabGlassMath.validImageSize 8001 4000))
check "pixel multiplication cannot overflow" (not (TabGlassMath.validImageSize System.Int32.MaxValue System.Int32.MaxValue))
rectIs "odd monitor covers reduced height exactly"
    (TabGlassMath.reducedPlacement (1366, 769) (0.0, 0.0, 1366.0, 769.0))
    (0.0, 0.0, 171.0, 97.0)
rectIs "negative span offsets use independent axes"
    (TabGlassMath.reducedPlacement (1366, 769) (-1366.0, -769.0, 2732.0, 1538.0))
    (-171.0, -97.0, 342.0, 194.0)
rectIs "even monitor preserves old placement"
    (TabGlassMath.reducedPlacement (1920, 1080) (80.0, 40.0, 960.0, 540.0))
    (10.0, 5.0, 120.0, 67.5)

printfn "passed %d, failed %d" passes failures
if failures > 0 then exit 1
