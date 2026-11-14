// Check that all detach entry points use the tested frame geometry.
open System
open System.IO
let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
let source = File.ReadAllText(Path.Combine(root,"TabStripDecorator.fs"))
let mutable checks = 0
let check name condition =
    checks <- checks + 1
    if not condition then failwith name
let methodBody name =
    let start = source.IndexOf("member private this." + name + "(", StringComparison.Ordinal)
    let finish = source.IndexOf("\n    member ",start+1,StringComparison.Ordinal)
    source.Substring(start,finish-start)
for suffix in ["Position"; "Screen"; "Snap"; "ScreenSnap"; "SnapWithPercent"; "ScreenSnapWithPercent"] do
    let one = methodBody ("detachTabTo"+suffix)
    check (suffix+" captures before removal")
        (one.IndexOf("capturePlacementMargin",StringComparison.Ordinal) >= 0 &&
         one.IndexOf("capturePlacementMargin",StringComparison.Ordinal) < one.IndexOf("group.removeWindow",StringComparison.Ordinal))
    check (suffix+" writes a base through the detached placement helper")
        (one.Contains("FramePlacement.moveDetached") && not (one.Contains("window.move")))
    let many = methodBody ("detachTabsTo"+suffix)
    check (suffix+" selection uses common margin-aware placement")
        (many.Contains("detachTabsCommon") && many.Contains("place (") && not (many.Contains("window.move")))
let core = methodBody "detachTabsCommon"
check "selection snapshots every member" (core.Contains("tabsInOrder |> List.map (fun h -> h, group.capturePlacementMargin(h))"))
check "selection seeds each follower's own sample" (core.Contains("FramePlacement.prepare tabHwnd margins.[tabHwnd]"))
let desktop = File.ReadAllText(Path.Combine(root,"Desktop.fs"))
check "drag snap uses detached placement" (desktop.Contains("FramePlacement.moveDetachedSnap window margin baseBounds"))
check "drag selection passes each snapshot" (desktop.Contains("FramePlacement.prepareSnap selected dragInfo.sourceFrameMargins.[selected] baseBounds"))
let group = File.ReadAllText(Path.Combine(root,"WindowGroup.fs"))
check "new member consumes the transferred sample" (group.Contains("let prepared = FramePlacement.take hwnd"))
check "new group keeps the requested base" (group.Contains("placement.set(Some(value.bounds, window.placement))"))
printfn "%d detach route checks passed" checks
