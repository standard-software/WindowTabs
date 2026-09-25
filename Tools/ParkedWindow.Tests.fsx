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

// Two 2560x1600 monitors stacked, the second above the primary, with the
// taskbar taking 48 px off the bottom of each and 40 px off the side of the
// upper one. The layout the bug was found on.
let stackedDisplays = [ box 0 0 2560 1600; box 0 -1600 2560 1600 ]
let stackedWork = [ box 0 0 2560 1552; box 0 -1600 2520 1552 ]

// Side by side: a 1920x1080 primary and a 2560x1440 to its right, taller.
let sideDisplays = [ box 0 0 1920 1080; box 1920 -200 2560 1440 ]
let sideWork = [ box 0 0 1920 1040; box 1920 -200 2560 1400 ]

let parkedAt displays work (w, h) =
    let (x, y) = ParkedWindow.parkPoint work
    ParkedWindow.isParked displays work (box x y w h)

printfn "parkPoint"
check "stacked monitors: 100 px past the widest right and the lowest bottom work edge"
    (ParkedWindow.parkPoint stackedWork = (2660, 1652))
check "side by side: the right and bottom maxima come from different monitors"
    (ParkedWindow.parkPoint sideWork = (4580, 1300))

printfn "isParked - windows hideOffScreen put there"
check "a window at the park point is parked (stacked)" (parkedAt stackedDisplays stackedWork (2386, 1207))
check "a window at the park point is parked (side by side)" (parkedAt sideDisplays sideWork (800, 600))
check "a tiny window at the park point is parked" (parkedAt stackedDisplays stackedWork (1, 1))
check "further out along the same diagonal is still the parking region"
    (ParkedWindow.isParked stackedDisplays stackedWork (box 5000 4000 640 400))

printfn "isParked - windows that are not ours"
check "a window on screen is not parked"
    (not (ParkedWindow.isParked stackedDisplays stackedWork (box 80 60 2386 1207)))
check "the minimized position (-32000,-32000) is not parked"
    (not (ParkedWindow.isParked stackedDisplays stackedWork (box -32000 -32000 160 28)))
check "an application's own hiding place at negative coordinates is not parked"
    (not (ParkedWindow.isParked stackedDisplays stackedWork (box -6000 -6000 640 400)))
check "off screen to the right but level with the monitors is not parked"
    (not (ParkedWindow.isParked stackedDisplays stackedWork (box 2700 100 640 400)))
check "off screen below but not past the right edge is not parked"
    (not (ParkedWindow.isParked stackedDisplays stackedWork (box 100 1700 640 400)))
check "past both edges but still overlapping a display (the taskbar strip) is not parked"
    (not (ParkedWindow.isParked [ box 0 0 2560 1600 ] [ box 0 0 2500 1552 ] (box 2520 1560 640 400)))
check "an empty rectangle is not parked"
    (not (ParkedWindow.isParked stackedDisplays stackedWork (box 2660 1652 0 0)))
check "with no monitors known, nothing is parked"
    (not (ParkedWindow.isParked [] [] (box 2660 1652 640 400)))

printfn "homeFor"
check "a window that fits is centred and keeps its size"
    (ParkedWindow.homeFor (box 0 0 2560 1552) 2386 1207 = (87, 172, 2386, 1207))
check "a window larger than the work area is shrunk to it"
    (ParkedWindow.homeFor (box 0 0 1920 1040) 2580 1880 = (0, 0, 1920, 1040))
check "the work area's own offset is kept"
    (ParkedWindow.homeFor (box 1920 -200 2560 1400) 640 400 = (2880, 300, 640, 400))

printfn ""
if failed = 0 then
    printfn "all %d checks passed" passed
    exit 0
else
    printfn "%d of %d checks FAILED" failed (passed + failed)
    exit 1
