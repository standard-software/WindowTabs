// Checks ParkedWindow - where hideOffScreen parks a window, and which windows
// count as left there - without starting WindowTabs. Loads the file that ships.
//
//   dotnet fsi Tools\ParkedWindow.Tests.fsx
//
// Exit code 0 when every check passes, 1 otherwise.

#load "../WtProgram/Shared/ParkedWindow.fs"

open Bemo

let mutable failed = 0
let mutable passed = 0
let check name ok =
    if ok then
        passed <- passed + 1
        printfn "  PASS  %s" name
    else
        failed <- failed + 1
        printfn "  FAIL  %s" name

let box = ParkedWindow.ofXYWH

// Synthetic stacked monitors with bottom work-area insets and a side inset
// on the upper monitor.
let stackedDisplays = [ box 0 0 2400 1600; box 0 -1600 2400 1600 ]
let stackedWork = [ box 0 0 2400 1550; box 0 -1600 2360 1550 ]

// Synthetic side-by-side monitors with a taller display on the right.
let sideDisplays = [ box 0 0 1920 1080; box 1920 -200 2400 1440 ]
let sideWork = [ box 0 0 1920 1040; box 1920 -200 2400 1400 ]

let parkedAt displays work (w, h) =
    let (x, y) = ParkedWindow.parkPoint work
    ParkedWindow.isParked displays work (box x y w h)

printfn "parkPoint"
check "stacked monitors: 100 px past the widest right and the lowest bottom work edge"
    (ParkedWindow.parkPoint stackedWork = (2500, 1650))
check "side by side: the right and bottom work edges determine the park point"
    (ParkedWindow.parkPoint sideWork = (4420, 1300))

printfn "isParked - windows hideOffScreen put there"
check "a window at the park point is parked (stacked)" (parkedAt stackedDisplays stackedWork (2000, 1000))
check "a window at the park point is parked (side by side)" (parkedAt sideDisplays sideWork (800, 600))
check "a tiny window at the park point is parked" (parkedAt stackedDisplays stackedWork (1, 1))
check "further out along the same diagonal is still the parking region"
    (ParkedWindow.isParked stackedDisplays stackedWork (box 5000 4000 640 400))

printfn "isParked - windows that are not ours"
check "a window on screen is not parked"
    (not (ParkedWindow.isParked stackedDisplays stackedWork (box 80 60 2000 1000)))
check "the minimized position (-32000,-32000) is not parked"
    (not (ParkedWindow.isParked stackedDisplays stackedWork (box -32000 -32000 160 28)))
check "an application's own hiding place at negative coordinates is not parked"
    (not (ParkedWindow.isParked stackedDisplays stackedWork (box -6000 -6000 640 400)))
// Past the corner of the upper monitor, so it counts: the test asks for the
// corner of any one work area, not of the union (see the cases below).
check "off screen to the right of a stacked pair, below the upper one's corner, is parked"
    (ParkedWindow.isParked stackedDisplays stackedWork (box 2600 100 640 400))
check "off screen below but not past the right edge is not parked"
    (not (ParkedWindow.isParked stackedDisplays stackedWork (box 100 1700 640 400)))
check "past both edges but still overlapping a display (the taskbar strip) is not parked"
    (not (ParkedWindow.isParked [ box 0 0 2400 1600 ] [ box 0 0 2350 1550 ] (box 2360 1560 640 400)))
check "an empty rectangle is not parked"
    (not (ParkedWindow.isParked stackedDisplays stackedWork (box 2500 1650 0 0)))
check "with no monitors known, nothing is parked"
    (not (ParkedWindow.isParked [] [] (box 2500 1650 640 400)))

// The corner is that of any one work area. A monitor added to the right or
// below after a window was parked moves the corner of the union past the
// window, and a test against the union then never finds it.
printfn "isParked - one work area's corner, not the union's"
let oneDisplay = [ box 0 0 2400 1080 ]
let oneWork = [ box 0 0 2400 1030 ]
// Added afterwards: a shorter one to the right, a narrower one below. Neither
// covers the old park point, but the union's corner is now past it.
let grownDisplays = oneDisplay @ [ box 2400 -1000 2400 2000; box 0 1080 2000 1440 ]
let grownWork = oneWork @ [ box 2400 -1000 2400 1950; box 0 1080 2000 1390 ]
let threeDisplays = [ box 0 0 1920 1080; box 1920 0 1920 1080; box 0 1080 1920 1080 ]
let threeWork = [ box 0 0 1920 1030; box 1920 0 1920 1030; box 0 1080 1920 1030 ]
check "one monitor: a window at its park point is parked"
    (ParkedWindow.parkPoint oneWork = (2500, 1130) &&
     ParkedWindow.isParked oneDisplay oneWork (box 2500 1130 640 400))
check "the same window after monitors were added to the right and below is still parked"
    (ParkedWindow.isParked grownDisplays grownWork (box 2500 1130 640 400))
check "three monitors: a window at their park point is parked"
    (ParkedWindow.parkPoint threeWork = (3940, 2210) &&
     ParkedWindow.isParked threeDisplays threeWork (box 3940 2210 640 400))
check "the minimized position is not parked"
    (not (ParkedWindow.isParked oneDisplay oneWork (box -32000 -32000 160 28)))
check "an application's hiding place at (-10000, 500) is not parked"
    (not (ParkedWindow.isParked oneDisplay oneWork (box -10000 500 640 400)))
check "right of every monitor but level with one (y = 300) is not parked"
    (not (ParkedWindow.isParked oneDisplay oneWork (box 2600 300 640 400)))
check "below every monitor but not past its right edge (x = 100) is not parked"
    (not (ParkedWindow.isParked oneDisplay oneWork (box 100 1200 640 400)))
check "visible on the monitor to the right is not parked"
    (not (ParkedWindow.isParked grownDisplays grownWork (box 2450 200 640 400)))

printfn "homeFor"
check "a window that fits is centred and keeps its size"
    (ParkedWindow.homeFor (box 0 0 2400 1550) 2000 1000 = (200, 275, 2000, 1000))
check "a window larger than the work area is shrunk to it"
    (ParkedWindow.homeFor (box 0 0 1920 1040) 3000 2000 = (0, 0, 1920, 1040))
check "the work area's own offset is kept"
    (ParkedWindow.homeFor (box 1920 -200 2400 1400) 640 400 = (2800, 300, 640, 400))

printfn ""
if failed = 0 then
    printfn "all %d checks passed" passed
    exit 0
else
    printfn "%d of %d checks FAILED" failed (passed + failed)
    exit 1
