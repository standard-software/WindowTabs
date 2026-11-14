#load "SnapGeometry.fs"
#load "WindowFrameMargin.fs"
open Bemo.WindowFrameMargin

let mutable checks = 0
let equal name expected actual =
    checks <- checks + 1
    if actual <> expected then failwithf "%s: expected %A, got %A" name expected actual
let rect x y width height : Bounds = { x = x; y = y; width = width; height = height }
let owner = rect 100 100 600 400
let frame = [rect 100 92 600 8; rect 93 100 7 400; rect 700 100 9 400; rect 100 500 600 10]
let expected : Margin = { top = 8; left = 7; right = 9; bottom = 10 }
equal "four external sides" expected (detect 96 owner frame)
// Discovery order must not affect the measured outer rectangle.
for shift in 0 .. 3 do
    let rotated = List.skip shift frame @ List.take shift frame
    equal "rotated order" expected (detect 96 owner rotated)
    equal "reversed order" expected (detect 96 owner (List.rev rotated))
equal "no frame" zero (detect 96 owner [])
equal "small popup and corner are not edges" zero
    (detect 96 owner [rect 100 90 60 10; rect 90 90 10 10])
equal "inside-only pieces" zero (detect 96 owner [rect 100 100 600 10])
equal "gap is not margin" zero (detect 96 owner [rect 100 88 600 8])
equal "overlapping frame measures only the outside" {zero with top = 8}
    (detect 96 owner [rect 100 92 600 10])
equal "large overlap is rejected" zero (detect 96 owner [rect 100 92 600 14])
equal "partial sample retains other sides" (8, 7, 9, 12)
    (emptySticky |> remember 96 expected |> remember 96 {zero with bottom = 12} |> readSticky 96)
equal "empty sample retains margins" (8, 7, 9, 10)
    (emptySticky |> remember 96 expected |> remember 144 zero |> readSticky 96)
equal "DPI scaling" (12, 11, 14, 15)
    (emptySticky |> remember 96 expected |> readSticky 144)
equal "zero-area owner" zero (detect 96 (rect 100 100 0 400) frame)
let accepted, pending = acceptSample zero None expected
equal "late frame accepted" expected accepted
equal "late frame does not wait" None pending
let held, missing = acceptSample expected None zero
equal "transient missing frame held" expected held
let gone, cleared = acceptSample held missing zero
equal "confirmed disappearance clears frame" zero gone
equal "confirmation consumed" None cleared
let partial = {expected with left=0}
let heldPartial, partialPending = acceptSample expected None partial
equal "partial disappearance waits" expected heldPartial
equal "partial disappearance confirmed" (partial, None) (acceptSample heldPartial partialPending partial)
equal "frame returning cancels disappearance" (expected, None) (acceptSample held missing expected)
let group = rect 20 30 800 600
let inside = inset (8, 7, 9, 10) group
equal "late frame stays inside original group" (rect 27 38 784 582) inside
equal "vanished frame fills original group" group (inset (0,0,0,0) group)
equal "first member has no existing base" false (needsReplacement false true false zero expected)
equal "late margin only replaces member" true (needsReplacement true true false zero expected)
equal "margin removal also replaces member" true (needsReplacement true true false expected zero)
equal "same margin permits ordinary move" false (needsReplacement true true false expected expected)
equal "minimized or maximized defers correction" false (needsReplacement true false false zero expected)
equal "native move defers correction" false (needsReplacement true true true zero expected)
equal "ordinary base is the native rectangle" group (outset (0,0,0,0) group)
for margin in [(0,0,0,0); (8,8,8,8); (3,5,7,9)] do
    equal "frame read reverses frame write" group (outset margin (inset margin group))
for tabHeight in [0; 20; 30] do
    let reserved = Bemo.SnapGeometry.reserveTabHeight tabHeight (0,0,1000,800)
    let x,y,w,h = Bemo.SnapGeometry.snapBounds "snapright" reserved 400 300
    let baseBounds = rect x y w h
    equal "snap preserves work area bottom" 800 (y+h)
    equal "snap reserves tab height once" tabHeight y
    let fromOrdinaryMember = outset (0,0,0,0) baseBounds
    let linked = inset (8,8,8,8) fromOrdinaryMember
    equal "first frame member preserves snapped base" baseBounds (outset (8,8,8,8) linked)
equal "prepared base initializes a normal new group" true (usePreparedBase false true)
equal "prepared base never replaces an existing group" false (usePreparedBase true true)
equal "prepared base does not override a minimized or maximized state" false (usePreparedBase false false)
let sample = remember 96 {zero with top=6;left=8;right=10;bottom=12} emptySticky
let sourceMargin = readSticky 96 sample
let destinationMargin = readSticky 144 sample
let restored = rect 100 80 320 240
let outer = outset sourceMargin restored
equal "detached input includes frame width" 338 outer.width
equal "detached input includes frame height" 258 outer.height
let x,y,w,h = Bemo.SnapGeometry.snapBounds "snapleft" (1000,20,1000,780) outer.width outer.height
let detachedBase = rect x y w h
let detachedNative = inset destinationMargin detachedBase
equal "cross-DPI detached frame stays inside base" detachedBase (outset destinationMargin detachedNative)
equal "detached snap reserves top and external frame" 29 detachedNative.y
for memberMargin in [sourceMargin; (0,0,0,0); (4,2,6,8)] do
    equal "each selected member shares the same base" detachedBase (outset memberMargin (inset memberMargin detachedBase))
let px,py,pw,ph = Bemo.SnapGeometry.calculateSnapBoundsWithPercent "snapright" 50 (1000,20,1000,780)
let percentBase = rect px py pw ph
equal "percentage controls outer size" 500 percentBase.width
equal "percentage snap includes each member frame" percentBase (outset destinationMargin (inset destinationMargin percentBase))
// Synthetic geometry only; no native windows or captured values.
let nativeSample = rect -200 40 700 500
for amount in [(0,0,0,0); (0,6,8,10); (3,12,11,9); (32,32,32,32)] do
    let visible = inset amount nativeSample
    equal "valid DWM sample" amount (frameInsets nativeSample visible)
    equal "visible size survives snap round trip" visible (inset amount (outset amount visible))
for invalid in [rect -201 40 700 500; rect -160 40 660 500; rect -200 40 0 500;
                rect -200 40 701 500; rect -200 40 700 501] do
    equal "invalid DWM sample is ignored" (0,0,0,0) (frameInsets nativeSample invalid)
equal "overflow-safe DWM sample" (0,0,0,0)
    (frameInsets (rect System.Int32.MinValue 0 100 100) (rect System.Int32.MaxValue 0 100 100))
let invisibleMargin = (0,6,8,10)
let visible = inset invisibleMargin nativeSample
for direction in ["snapleft";"snapright";"snaptop";"snapbottom"] do
    let x,y,w,h = Bemo.SnapGeometry.snapBounds direction (-1000,0,1000,800) visible.width visible.height
    let wanted = rect x y w h
    equal "keep-size preserves the visible dimension" wanted (inset invisibleMargin (outset invisibleMargin wanted))
    if direction = "snapleft" || direction = "snapright" then equal "visible width" visible.width w
    else equal "visible height" visible.height h
// Reserve remains unchanged: expand the native rectangle, then draw the strip
// at the visible edge. The outside edge is exactly the unreserved boundary.
let reserved = rect 24 0 952 776
let native = outset invisibleMargin reserved
let aligned = inset invisibleMargin native
equal "left strip reaches boundary" 0 (aligned.x - 24)
equal "right strip reaches boundary" 1000 (aligned.x + aligned.width + 24)
equal "bottom strip reaches boundary" 800 (aligned.y + aligned.height + 24)
let destinationFrame = (2,10,12,16)
equal "destination remeasurement preserves target" reserved (inset destinationFrame (outset destinationFrame reserved))
printfn "%d checks passed" checks