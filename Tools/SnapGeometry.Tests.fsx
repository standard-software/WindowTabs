// Checks SnapGeometry - the snap rules shared by the tab menu's "Detach and
// snap" and the snap on dragging tabs out to a new window - without starting
// WindowTabs. The module depends on FSharp.Core only, so this script loads the
// very file that ships rather than a copy of the rules.
//
//   dotnet fsi Tools\SnapGeometry.Tests.fsx
//
// Exit code 0 when every check passes, 1 otherwise. Run it after touching
// SnapGeometry.fs, TabStripDecorator.calculateSnapBounds / applySnapRealign or
// Desktop.snapDroppedWindow.

#load "../WtProgram/Shared/SnapGeometry.fs"

open System.Drawing
open Bemo
open Bemo.SnapGeometry

let mutable failed = 0
let mutable passed = 0
let check name ok =
    if ok then
        passed <- passed + 1
        printfn "  PASS  %s" name
    else
        failed <- failed + 1
        printfn "  FAIL  %s" name

let dir area pt = directionForPoint area pt

// Fixed mouse-coordinate examples: expected values are literal, not another
// implementation of the diagonal inequalities. All corner points are inside.
printfn "direction: fixed coordinate table"
let hd = (0, 0, 1920, 1040)
let cases = [
    "right", hd, (1900, 520), "snapright"
    "left", hd, (20, 520), "snapleft"
    "top", hd, (960, 10), "snaptop"
    "bottom", hd, (960, 1030), "snapbottom"
    "wide TL corner", hd, (0, 0), "snapleft"
    "wide TR corner", hd, (1919, 0), "snaptop"
    "wide BL corner", hd, (0, 1039), "snapleft"
    "wide BR corner", hd, (1919, 1039), "snapright"
    "wide TL diagonal", hd, (240, 130), "snapleft"
    "wide TL above", hd, (240, 129), "snaptop"
    "wide TL below", hd, (240, 131), "snapleft"
    "wide TR diagonal", hd, (1680, 130), "snapright"
    "wide TR above", hd, (1680, 129), "snaptop"
    "wide TR below", hd, (1680, 131), "snapright"
    "wide BL diagonal", hd, (240, 910), "snapleft"
    "wide BL below", hd, (240, 911), "snapbottom"
    "wide BR diagonal", hd, (1680, 910), "snapright"
    "wide BR below", hd, (1680, 911), "snapbottom"
    "portrait TL diagonal", (0,0,1080,1920), (135,240), "snapleft"
    "portrait TL above", (0,0,1080,1920), (135,239), "snaptop"
    "portrait TL below", (0,0,1080,1920), (135,241), "snapleft"
    "portrait TR diagonal", (0,0,1080,1920), (945,240), "snapright"
    "portrait TR above", (0,0,1080,1920), (945,239), "snaptop"
    "portrait BL diagonal", (0,0,1080,1920), (135,1680), "snapleft"
    "portrait BL below", (0,0,1080,1920), (135,1681), "snapbottom"
    "portrait BR diagonal", (0,0,1080,1920), (945,1680), "snapright"
    "portrait BR below", (0,0,1080,1920), (945,1681), "snapbottom"
    "square TL diagonal", (0,0,100,100), (10,10), "snapleft"
    "square TR diagonal", (0,0,100,100), (90,10), "snapright"
    "square BL diagonal", (0,0,100,100), (10,90), "snapleft"
    "square BR diagonal", (0,0,100,100), (90,90), "snapright"
    "integer point regression", (0,0,100,100), (70,29), "snaptop"
    "centre", hd, (960,520), "snapright"
    "centre left", hd, (959,520), "snapleft"
    "centre above", hd, (960,519), "snaptop"
    "centre below", hd, (960,521), "snapbottom"
    "negative TL diagonal", (-1920,-200,1920,1040), (-1680,-70), "snapleft"
    "negative TL above", (-1920,-200,1920,1040), (-1680,-71), "snaptop"
    "negative TR diagonal", (-1920,-200,1920,1040), (-240,-70), "snapright"
    "negative BL diagonal", (-1920,-200,1920,1040), (-1680,710), "snapleft"
    "negative BL below", (-1920,-200,1920,1040), (-1680,711), "snapbottom"
    "negative BR diagonal", (-1920,-200,1920,1040), (-240,710), "snapright"
    "negative BR below", (-1920,-200,1920,1040), (-240,711), "snapbottom"
    "taskbar offset centre", (60,0,1860,1080), (990,540), "snapright"
    "outside below", hd, (960,1060), "snapbottom"
    "wide subtraction", (-2147483000,0,1920,1040), (2147483000,520), "snapright"
    "large products", (-2147483648,-2147483648,2147483647,2147483647), (2147483647,2147483647), "snapright"
]
for name, area, point, expected in cases do
    check name (dir area point = expected)

// Reflection about the geometric centre uses width-x, not width-1-x.
// Restrict samples to interior points so both reflected points stay inside.
for w,h in [(7,5);(40,40);(41,41);(64,36);(36,64);(97,53)] do
    let mutable mirrored = true
    for x in 1..w-1 do
        for y in 1..h-1 do
            let d = dir (0,0,w,h) (x,y)
            let vertical = match d with "snaptop" -> snapBottom | "snapbottom" -> snapTop | d -> d
            let horizontal = match d with "snapleft" -> snapRight | "snapright" -> snapLeft | d -> d
            if dir (0,0,w,h) (x,h-y) <> vertical then mirrored <- false
            if (2*x <> w || 2*y <> h) && dir (0,0,w,h) (w-x,y) <> horizontal then mirrored <- false
    check (sprintf "integer-coordinate symmetry %dx%d" w h) mirrored

printfn "B: fixed 50-percent rectangles"
let halfCases = [
    "snapright", hd, (960,0,960,1040)
    "snapleft", hd, (0,0,960,1040)
    "snaptop", hd, (0,0,1920,520)
    "snapbottom", hd, (0,520,1920,520)
    "snapright", (-1920,-200,1920,1040), (-960,-200,960,1040)
    "snapbottom", (0,37,1920,1003), (0,539,1920,501)
    "snaptop", (0,37,1920,1003), (0,37,1920,501)
    "snapright", (0,37,1920,1003), (960,37,960,1003)
    "snapleft", (0,37,1920,1003), (0,37,960,1003)
    "snapright", (0,0,1919,1039), (960,0,959,1039)
    "snapbottom", (0,0,1080,1920), (0,960,1080,960)
]
for direction, area, expected in halfCases do
    check (sprintf "50 percent %s %A" direction area)
        (calculateSnapBoundsWithPercent direction 50 area = expected)

// ---------------------------------------------------------- snap bounds --

printfn "snap bounds"
// The body of TabStripDecorator.calculateSnapBounds before it moved to
// SnapGeometry, verbatim apart from taking the already-adjusted work area.
let referenceSnapBounds (snapDirection: string) (workArea: Rectangle) (currentWidth: int) (currentHeight: int) =
    let clampedWidth = min currentWidth workArea.Width
    let clampedHeight = min currentHeight workArea.Height
    match snapDirection with
    | "snapright" ->
        let newWidth = clampedWidth
        let newHeight = workArea.Height
        let x = workArea.Right - newWidth
        let y = workArea.Top
        (x, y, newWidth, newHeight)
    | "snapleft" ->
        let newWidth = clampedWidth
        let newHeight = workArea.Height
        let x = workArea.Left
        let y = workArea.Top
        (x, y, newWidth, newHeight)
    | "snaptop" ->
        let newWidth = workArea.Width
        let newHeight = clampedHeight
        let x = workArea.Left
        let y = workArea.Top
        (x, y, newWidth, newHeight)
    | "snapbottom" ->
        let newWidth = workArea.Width
        let newHeight = clampedHeight
        let x = workArea.Left
        let y = workArea.Bottom - newHeight
        (x, y, newWidth, newHeight)
    | _ ->
        (workArea.Left, workArea.Top, clampedWidth, clampedHeight)
let referenceReserve tabHeight (workArea: Rectangle) =
    if tabHeight > 0 then Rectangle(workArea.X, workArea.Y + tabHeight, workArea.Width, workArea.Height - tabHeight)
    else workArea

let mutable boundsAgree = true
for (ax, ay, aw, ah) in [ (0, 0, 1920, 1040); (-1920, -200, 1920, 1080); (60, 0, 1860, 1080); (2560, 0, 1440, 2520) ] do
    for tabHeight in [ 0; -3; 24; 37 ] do
        for direction in [ snapLeft; snapRight; snapTop; snapBottom; "snapunknown" ] do
            for (cw, ch) in [ (800, 600); (1920, 1040); (3000, 2000); (1, 1); (1500, 90) ] do
                let expected = referenceSnapBounds direction (referenceReserve tabHeight (Rectangle(ax, ay, aw, ah))) cw ch
                let actual = snapBounds direction (reserveTabHeight tabHeight (ax, ay, aw, ah)) cw ch
                if actual <> expected then
                    boundsAgree <- false
                    printfn "    mismatch: area=%A tab=%d %s size=%dx%d expected=%A actual=%A" (ax, ay, aw, ah) tabHeight direction cw ch expected actual
check "snapBounds + reserveTabHeight equal the former calculateSnapBounds (400 cases)" boundsAgree

check "right snap keeps width, fills height, touches the right edge"
    (snapBounds snapRight hd 800 600 = (1120, 0, 800, 1040))
check "left snap keeps width, fills height, touches the left edge"
    (snapBounds snapLeft hd 800 600 = (0, 0, 800, 1040))
check "top snap keeps height, fills width, touches the top edge"
    (snapBounds snapTop hd 800 600 = (0, 0, 1920, 600))
check "bottom snap keeps height, fills width, touches the bottom edge"
    (snapBounds snapBottom hd 800 600 = (0, 440, 1920, 600))
check "a window wider than the area is clamped to it"
    (snapBounds snapRight hd 2500 600 = (0, 0, 1920, 1040))
check "tab margin moves the top down and shortens the height"
    (reserveTabHeight 37 hd = (0, 37, 1920, 1003))
check "no tab margin leaves the area alone" (reserveTabHeight 0 hd = hd)
check "right snap with a 37 px tab margin"
    (snapBounds snapRight (reserveTabHeight 37 hd) 800 600 = (1120, 37, 800, 1003))
check "bottom snap with a margin still touches the bottom edge"
    (snapBounds snapBottom (reserveTabHeight 37 hd) 800 600 = (0, 440, 1920, 600))

// Historical menu percentage body, copied from the read-only generation-1
// source. Only the method header and caller-owned margin adjustment differ.
let referencePercent (snapDirection: string) (percent: int) (workArea: Rectangle) =
    let percentFloat = float(percent) / 100.0
    match snapDirection with
    | "snapright" ->
        let newWidth = int(float(workArea.Width) * percentFloat)
        let newHeight = workArea.Height
        let x = workArea.Right - newWidth
        let y = workArea.Top
        (x, y, newWidth, newHeight)
    | "snapleft" ->
        let newWidth = int(float(workArea.Width) * percentFloat)
        let newHeight = workArea.Height
        let x = workArea.Left
        let y = workArea.Top
        (x, y, newWidth, newHeight)
    | "snaptop" ->
        let newWidth = workArea.Width
        let newHeight = int(float(workArea.Height) * percentFloat)
        let x = workArea.Left
        let y = workArea.Top
        (x, y, newWidth, newHeight)
    | "snapbottom" ->
        let newWidth = workArea.Width
        let newHeight = int(float(workArea.Height) * percentFloat)
        let x = workArea.Left
        let y = workArea.Bottom - newHeight
        (x, y, newWidth, newHeight)
    | "snaptopleft" ->
        let newWidth = int(float(workArea.Width) * percentFloat)
        let newHeight = int(float(workArea.Height) * percentFloat)
        let x = workArea.Left
        let y = workArea.Top
        (x, y, newWidth, newHeight)
    | "snaptopright" ->
        let newWidth = int(float(workArea.Width) * percentFloat)
        let newHeight = int(float(workArea.Height) * percentFloat)
        let x = workArea.Right - newWidth
        let y = workArea.Top
        (x, y, newWidth, newHeight)
    | "snapbottomleft" ->
        let newWidth = int(float(workArea.Width) * percentFloat)
        let newHeight = int(float(workArea.Height) * percentFloat)
        let x = workArea.Left
        let y = workArea.Bottom - newHeight
        (x, y, newWidth, newHeight)
    | "snapbottomright" ->
        let newWidth = int(float(workArea.Width) * percentFloat)
        let newHeight = int(float(workArea.Height) * percentFloat)
        let x = workArea.Right - newWidth
        let y = workArea.Bottom - newHeight
        (x, y, newWidth, newHeight)
    | "snapcenter" ->
        let newWidth = int(float(workArea.Width) * percentFloat)
        let newHeight = int(float(workArea.Height) * percentFloat)
        let x = workArea.Left + (workArea.Width - newWidth) / 2
        let y = workArea.Top + (workArea.Height - newHeight) / 2
        (x, y, newWidth, newHeight)
    | "snapcenterhorizontal" ->
        let newWidth = int(float(workArea.Width) * percentFloat)
        let newHeight = workArea.Height
        let x = workArea.Left + (workArea.Width - newWidth) / 2
        let y = workArea.Top
        (x, y, newWidth, newHeight)
    | "snapcentervertical" ->
        let newWidth = workArea.Width
        let newHeight = int(float(workArea.Height) * percentFloat)
        let x = workArea.Left
        let y = workArea.Top + (workArea.Height - newHeight) / 2
        (x, y, newWidth, newHeight)
    | "snapmaximizedisplay" | "snapmaximizedesktop" ->
        (workArea.Left, workArea.Top, workArea.Width, workArea.Height)
    | _ ->
        (workArea.Left, workArea.Top, workArea.Width, workArea.Height)

let mutable percentCases = 0
let mutable percentAgree = true
for ax,ay,aw,ah in [(0,0,1920,1040);(-1920,-200,1919,1039);(0,0,1080,1920)] do
    for margin in [0;24;37] do
        for percent in [25;33;50;67;75;100] do
            for direction in ["snapleft";"snapright";"snaptop";"snapbottom";
                              "snaptopleft";"snaptopright";"snapbottomleft";"snapbottomright";
                              "snapcenter";"snapcenterhorizontal";"snapcentervertical";
                              "snapmaximizedisplay";"snapmaximizedesktop";"unknown"] do
                let expected = referencePercent direction percent (referenceReserve margin (Rectangle(ax,ay,aw,ah)))
                let actual = calculateSnapBoundsWithPercent direction percent (reserveTabHeight margin (ax,ay,aw,ah))
                if actual <> expected then percentAgree <- false
                percentCases <- percentCases + 1
check (sprintf "percentage geometry preserves the original menu (%d cases)" percentCases) percentAgree

// ---------------------------------------------------------- realignment --

printfn "realignment"
// Stand-ins for TabAlign.TopLeft / TopRight.
let L, R = "TopLeft", "TopRight"
check "setting off: never realigns" (realignment false snapRight [L; L] L R = None)
check "all left, right snap: all right" (realignment true snapRight [L; L] L R = Some R)
check "all right, left snap: all left" (realignment true snapLeft [R; R; R] L R = Some L)
check "all left, left snap: left (a no-op, as the menu does)" (realignment true snapLeft [L] L R = Some L)
check "mixed alignment: left alone" (realignment true snapRight [L; R] L R = None)
check "top snap: left alone" (realignment true snapTop [L; L] L R = None)
check "bottom snap: left alone" (realignment true snapBottom [R] L R = None)
check "no tabs: nothing to realign" (realignment true snapRight [] L R = None)

printfn ""
printfn "%d passed, %d failed" passed failed
exit (if failed = 0 then 0 else 1)
