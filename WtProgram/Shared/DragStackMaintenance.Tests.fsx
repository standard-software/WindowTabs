// TestRuntime: Framework
// Load production decisions without invoking the entry point or native APIs.
#r "System.Drawing"
#r "System.Windows.Forms"
#r "Newtonsoft.Json.dll"
#r "FSharp.PowerPack.dll"
#r "Aga.Controls.dll"
#r "Win32.dll"
#r "WindowTabs.exe"
open System
open Bemo
let mutable checks = 0
let equal label expected actual =
    checks <- checks + 1
    if actual <> expected then failwithf "%s: expected %A, got %A" label expected actual
let top, guard, left, right, bottom, foreign, frame, tail = 1n,2n,3n,4n,5n,6n,7n,8n
let strips = [top;left;right;bottom]
let signature order = VirtualDesktopGroups.stripOrderSignature strips [frame] (Array.ofList order)
let legacy order =
    let a = Array.ofList order
    [for i in 0..a.Length-1 do
        if a.[i] = top then yield top, a.[i+1]
        elif a.[i] = frame then yield frame, a.[i-1]]
let before = [top;guard;left;right;bottom;foreign;frame;tail]
let behind = [top;guard;foreign;frame;tail;left;right;bottom]
equal "old cache misses all side strips falling behind" (legacy before) (legacy behind)
equal "production cache sees side strips falling behind" false (signature before = signature behind)
equal "unchanged stack stays quiet" (signature before) (signature before)
for side in [left;right;bottom] do
    let changed = (before |> List.filter ((<>) side)) @ [side]
    equal "each side participates" false (signature before = signature changed)
    let removed = before |> List.filter ((<>) side)
    equal "missing side participates" false (signature before = signature removed)
let topOnlySignature order = VirtualDesktopGroups.stripOrderSignature [top] [frame] (Array.ofList order)
equal "top-only keeps the old observations" (topOnlySignature before) (topOnlySignature behind)
let raised = frame :: (before |> List.filter ((<>) frame))
equal "async content raise invalidates cache" false (signature before = signature raised)
equal "repair restores original signature" (signature before) (signature (List.ofSeq before))
// Four-edge top placement uses the same selected monitor as side placement.
let rect (x,y,w,h) = Rect(Pt(x,y),Sz(w,h))
let home, neighbor = rect (0,0,200,200), rect (0,-200,200,200)
for topOnly in [false;true] do
    let decorator : WindowDecorator = {
        windowBounds = rect (20,0,100,100)
        monitorBounds = List2(TabEdgeLayout.placementMonitors topOnly (Some home) [home;neighbor])
        decoratorHeight = 20; decoratorHeightOffset = 0
        decoratorIndentFlipped = 0; decoratorIndentFlippedRight = 0
        decoratorIndentNormal = 0; decoratorIndentNormalRight = 0 }
    equal "production top seam follows selected policy" (not topOnly)
        (decorator.placementFor(TabBehaviorPolicy.verticalAuto, not topOnly) |> fst)
    let straddled = { decorator with windowBounds = rect (20,-40,100,140) }
    equal "straddling top uses home boundary" (not topOnly)
        (straddled.placementFor(TabBehaviorPolicy.verticalAuto, not topOnly) |> fst)
printfn "DragStackMaintenance: %d checks passed" checks
