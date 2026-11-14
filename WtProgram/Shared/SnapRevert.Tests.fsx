// Run with dotnet fsi; no native windows are created.
#r "../../Newtonsoft.Json.dll"
#load "TabOrder.fs"
#load "Regroup.fs"
#load "SavedTabState.fs"
#load "AppPath.fs"
#load "RestorePosition.fs"
#load "SavedSession.fs"
#load "VirtualDesktopIntegrity.fs"
#load "VirtualDesktopGroups.fs"

open System
open System.Collections.Generic
open Newtonsoft.Json.Linq
open Bemo
open Bemo.VirtualDesktopGroups

#load "SnapGeometry.fs"
#load "WindowFrameMargin.fs"
open Bemo.WindowFrameMargin

let mutable checks = 0
let mutable failures = 0
let equal name expected actual =
    checks <- checks + 1
    if actual <> expected then
        failures <- failures + 1
        printfn "FAIL %s: expected %A, got %A" name expected actual
let rect x y w h : Bounds = { x=x; y=y; width=w; height=h }
let p = rect 10 20 80 60
let q = rect 100 200 160 120
let atOldPosition = rect 10 20 160 120
let corrected, write = secondPass true p q
equal "snap uses the front position and size" (q, true) (corrected, write)
equal "position-only mismatch still writes" (q, true) (secondPass true atOldPosition q)
equal "off-screen front preserves follower position" (atOldPosition, true) (secondPass false p q)
equal "identical rectangle does not write" (q, false) (secondPass true q q)
equal "off-screen position ignored when size matches" (atOldPosition, false) (secondPass false atOldPosition q)
let parked = rect -32000 -32000 80 60
equal "parked follower returns to on-screen target" (q, true) (secondPass true parked q)
equal "existing iconic write exception retained" (parked, true) (secondPass false parked parked)
equal "different DPI and position use DPI protocol" true (secondPassUsesDpi p q 1.0 1.5)
equal "same DPI uses direct protocol" false (secondPassUsesDpi p q 1.0 1.0)
equal "size-only correction does not change DPI protocol" false (secondPassUsesDpi p atOldPosition 1.0 1.5)

// Compose the same pure margin operations used by the native read/write helpers.
let frontMargin = (2, 3, 4, 5)
let followerMargin = (7, 8, 9, 10)
let frontNative = rect 103 202 153 113
let groupBounds = outset frontMargin frontNative
equal "front margin recovers visible group rectangle" q groupBounds
equal "front-only margin follows expanded front" (q, true) (secondPass true p groupBounds)
let followerTarget = inset followerMargin q
let expectedFollower = rect 108 207 143 103
equal "follower-only margin retains corrected position" (expectedFollower, true) (secondPass true p followerTarget)
equal "both margins use one group rectangle" (expectedFollower, true)
    (secondPass true p (inset followerMargin groupBounds))
equal "off-screen front with margins keeps current position" (rect 10 20 143 103, true)
    (secondPass false p (inset followerMargin groupBounds))
// Maximized placement supplies raw top bounds; no normal-frame margin is applied.
let maximized = rect -1 -1 400 300
equal "maximized raw target remains exact" (maximized, true) (secondPass true p maximized)
equal "matching maximized placement is skipped" (maximized, false) (secondPass true maximized maximized)

for shared in [false; true] do
    for changed in [false; true] do
        for normal in [false; true] do
            let expected =
                match shared, changed, normal with
                | false, _, _ -> ForgetWrite
                | true, false, _ -> KeepWrite
                | true, true, false -> ForgetWrite
                | true, true, true -> RememberWrite
            equal (sprintf "write decision %b %b %b" shared changed normal) expected
                (placementWrite shared changed normal)

// A snap is in progress while the first pass still has the previous group base.
let mutable echo = None
let mutable follower = p
let mutable groupBase = p
let record changed target =
    match placementWrite true changed true with
    | RememberWrite -> echo <- Some (target, 1)
    | ForgetWrite -> echo <- None
    | KeepWrite -> ()
record true p
// The front's native snap finishes before the second pass.
groupBase <- q
let finalTarget, needsWrite = secondPass true follower q
record needsWrite finalTarget
if needsWrite then follower <- finalTarget
equal "scenario follower receives the completed snap" q follower
// LOCATIONCHANGE reads the current rectangle, not a captured old event rectangle.
echo <- placementEcho echo (follower, 1)
if echo.IsNone then groupBase <- follower
equal "scenario own echo is recognized" (Some (q, 1)) echo
equal "scenario final group base stays at completed snap" q groupBase
// A late first-pass echo must not replace a newer front base either.
let oldEcho = placementEcho (Some (p, 1)) (p, 1)
let afterLateEcho = if oldEcho.IsSome then q else p
equal "late first-pass echo preserves completed front base" q afterLateEcho
// A non-shared observation/write clears the record before shared following resumes.
match placementWrite false false false with
| ForgetWrite -> echo <- None
| _ -> ()
equal "shared return cannot reuse cleared write" None (placementEcho echo (q, 1))

printfn "%d checks, %d failures" checks failures
if failures > 0 then exit 1
