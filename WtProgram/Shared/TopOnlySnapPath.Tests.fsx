// TestRuntime: Framework
#r "../../Aga.Controls.dll"
#r "FSharp.PowerPack.dll"
#r "../../Newtonsoft.Json.dll"
#r "Win32.dll"
// Pure snap calculations against the built production assembly; no windows are created.
#r "WindowTabs.exe"
open Bemo
open Bemo.TabEdges
let mutable checks = 0
let equal label expected actual =
    checks <- checks + 1
    if actual <> expected then failwithf "%s: expected %A, got %A" label expected actual
for direction, desired in
    ["snapleft", Some AlignStart; "snapright", Some AlignEnd
     "snaptopleft", Some AlignStart; "snaptopright", Some AlignEnd
     "snapbottomleft", Some AlignStart; "snapbottomright", Some AlignEnd
     "snaptop", None; "snapbottom", None; "snapcenter", None
     "snapcenterhorizontal", None; "snapcentervertical", None
     "snapmaximizedisplay", None; "snapmaximizedesktop", None; "unknown", None] do
    for enabled in [false; true] do
        for aligns in [ []; [AlignStart]; [AlignEnd]; [AlignStart; AlignStart]
                        [AlignEnd; AlignEnd]; [AlignStart; AlignEnd]; [AlignEnd; AlignStart] ] do
            let uniform = not aligns.IsEmpty && (aligns |> List.forall ((=) aligns.Head))
            let expected = if enabled && uniform then desired else None
            equal "top-only alignment rule" expected
                (SnapGeometry.realignment enabled direction aligns AlignStart AlignEnd)
            let expectedEdges = expected |> Option.toList |> List.map (fun a -> Edge.Top, a)
            equal "top-only group and detach alignment" expectedEdges
                (snapRealign enabled direction (aligns |> List.map (fun a -> Edge.Top, a)))
let dimensions (r: Rect) = r.width, r.height
let beforeNormal = Rect(Pt(100, 100), Sz(800, 600))
let restored = Rect(Pt(100, 100), Sz(800, 600))
let beforeMaximized = Rect(Pt(0, 0), Sz(1920, 1080))
let beforeMinimized = Rect(Pt(-32000, -32000), Sz(160, 30))
for allowed in everyAllowed do
    for occupied in [] :: (everyAllowed |> List.map edgesOf) do
        equal "group path" (isTopOnly allowed || occupied = [Edge.Top]) (useTopOnlySnap allowed occupied true)
        equal "detach path unchanged" (isTopOnly allowed) (useTopOnlySnap allowed occupied false)
    if isAllowed allowed Edge.Top then
        for before in [beforeNormal; beforeMaximized; beforeMinimized] do
            for sourceHeight in [25; 38; 50] do
                for targetHeight in [25; 38; 50] do
                    for margin in [false; true] do
                        for target in [(0,0,1920,1080); (1920,0,2560,1440); (-1280,0,1280,720)] do
                            for direction in ["snapleft";"snapright";"snaptop";"snapbottom"] do
                                // Model the orchestration, using production bounds and geometry functions.
                                let calculate setting topDirection =
                                    let legacy = useTopOnlySnap setting [Edge.Top] true
                                    let bounds = TabStripDecorator.snapSourceBounds(legacy, before, restored)
                                    let height = if legacy then sourceHeight else targetHeight
                                    let area = SnapGeometry.reserveTabHeight (if TabBehaviorPolicy.reserveTopMargin margin topDirection then height - 1 else 0) target
                                    let _,_,tw,th = target
                                    let w,h = dimensions bounds
                                    let crossW = int (float tw * float w / 1920.0)
                                    let crossH = int (float th * float h / 1080.0)
                                    SnapGeometry.snapBounds direction area w h,
                                    SnapGeometry.snapBounds direction area crossW crossH,
                                    [for percent in [25;50;75;100] -> SnapGeometry.calculateSnapBoundsWithPercent direction percent area]
                                for topDirection in ["auto";"down"] do
                                    equal "same input and operation" (calculate topOnly topDirection) (calculate allowed topDirection)
                                equal "legacy pre-restore dimensions" (dimensions before)
                                    (TabStripDecorator.snapSourceBounds(useTopOnlySnap allowed [Edge.Top] true, before, restored) |> dimensions)
for occupied in [[Edge.Left]; [Edge.Right]; [Edge.Bottom]; [Edge.Top;Edge.Left]] do
    let allowed = allowedOf priority |> Option.get
    equal "other edges retain restored dimensions" (dimensions restored)
        (TabStripDecorator.snapSourceBounds(useTopOnlySnap allowed occupied true, beforeMaximized, restored) |> dimensions)
printfn "TopOnlySnapPath: %d checks passed" checks




