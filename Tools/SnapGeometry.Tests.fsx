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
#load "../WtProgram/Shared/WindowFrameMargin.fs"

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
// SnapGeometry, with the full-extent exception checked separately below.
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
                let area = referenceReserve tabHeight (Rectangle(ax, ay, aw, ah))
                let retainedIsFull =
                    (direction = snapLeft || direction = snapRight) && cw >= area.Width - 3 ||
                    (direction = snapTop || direction = snapBottom) && ch >= area.Height - 3
                let actual = snapBounds direction (reserveTabHeight tabHeight (ax, ay, aw, ah)) cw ch
                if not retainedIsFull && actual <> expected then
                    boundsAgree <- false
                    printfn "    mismatch: area=%A tab=%d %s size=%dx%d expected=%A actual=%A" (ax, ay, aw, ah) tabHeight direction cw ch expected actual
check "snapBounds + reserveTabHeight retain the former non-full geometry" boundsAgree

check "right snap keeps width, fills height, touches the right edge"
    (snapBounds snapRight hd 800 600 = (1120, 0, 800, 1040))
check "left snap keeps width, fills height, touches the left edge"
    (snapBounds snapLeft hd 800 600 = (0, 0, 800, 1040))
check "top snap keeps height, fills width, touches the top edge"
    (snapBounds snapTop hd 800 600 = (0, 0, 1920, 600))
check "bottom snap keeps height, fills width, touches the bottom edge"
    (snapBounds snapBottom hd 800 600 = (0, 440, 1920, 600))
check "a window wider than the area uses half width"
    (snapBounds snapRight hd 2500 600 = (960, 0, 960, 1040))
check "tab margin moves the top down and shortens the height"
    (reserveTabHeight 37 hd = (0, 37, 1920, 1003))
check "no tab margin leaves the area alone" (reserveTabHeight 0 hd = hd)
check "right snap with a 37 px tab margin"
    (snapBounds snapRight (reserveTabHeight 37 hd) 800 600 = (1120, 37, 800, 1003))
check "bottom snap with a margin still touches the bottom edge"
    (snapBounds snapBottom (reserveTabHeight 37 hd) 800 600 = (0, 440, 1920, 600))

// Reference menu percentage calculation from the previous implementation.
// Only the method header and caller-owned margin adjustment differ.
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


check "left to top uses half height" (snapBounds snapTop hd 960 1040 = (0, 0, 1920, 520))
check "top to right uses half width" (snapBounds snapRight hd 1920 520 = (960, 0, 960, 1040))
check "maximized to bottom uses half height" (snapBounds snapBottom hd 1936 1056 = (0, 520, 1920, 520))
check "DPI tolerance inclusive" (keepSnapLength 2.0 1000 994 = 500)
check "DPI tolerance outside" (keepSnapLength 2.0 1000 993 = 993)
check "standard tolerance inclusive" (keepSnapLength 1.0 1000 997 = 500)
check "standard tolerance outside" (keepSnapLength 1.0 1000 996 = 996)
check "odd height rounds down" (snapBounds snapBottom (10, 20, 1000, 801) 600 801 = (10, 421, 1000, 400))
check "reserved full height uses half" (snapBounds snapTop (10, 44, 1000, 776) 500 776 = (10, 44, 1000, 388))
check "full reserved extent survives screen scaling"
    (screenSnapSize 1.0 (1000, 800) (1000, 776) (500, 776) = (500, 800))
check "non-full extent keeps screen ratio"
    (screenSnapSize 1.5 (1000, 800) (970, 776) (500, 600) = (500, 600))
check "normal mapping scales position and size"
    (mapNormalBounds (-1000, 40, 1000, 800) (0, -900, 1500, 1200) (-900, 120, 400, 320) = (150, -780, 600, 480))
check "normal mapping clamps overflowing position"
    (mapNormalBounds (0, 0, 1000, 800) (1000, 20, 2000, 1600) (900, 700, 400, 300) = (2200, 1020, 800, 600))
check "normal mapping keeps oversized size"
    (mapNormalBounds (0, 0, 1000, 800) (-2000, 0, 2000, 1600) (-20, -30, 1200, 900) = (-2000, 0, 2400, 1800))
check "normal mapping clamps negative offsets"
    (mapNormalBounds (0, 0, 1000, 800) (1000, 20, 1000, 800) (-10, -10, 400, 300) = (1000, 20, 400, 300))

// Synthetic visible geometry leaves room below the frame after mapping.
let normalVisible : WindowFrameMargin.Bounds = { x = -1210; y = 32; width = 720; height = 640 }
let sourceInsets = (-9, -5, -5, -6)
let targetInsets = (-12, -8, -8, -10)
let sourceNative = WindowFrameMargin.inset sourceInsets normalVisible
let sourceFrame, mappedFrame, targetNative =
    WindowFrameMargin.mapNormalPlacement
        (-1200, 0, 1200, 900) (0, 0, 1800, 900) sourceInsets targetInsets sourceNative
check "normal visible frame recovered before mapping" (sourceFrame = normalVisible)
check "normal hidden border does not shift the top" (mappedFrame.y = 32)
check "normal left overhang is clamped" (mappedFrame.x = 0)
check "normal width follows work area ratio" (mappedFrame.width = 1080 && mappedFrame.height = 640)
check "target hidden border does not shift the visible frame"
    (WindowFrameMargin.outset targetInsets targetNative = mappedFrame)
check "normal native top includes destination border" (targetNative.y = 20)
let checkMappedClamp name visible expected =
    let _, mapped, native =
        WindowFrameMargin.mapNormalPlacement (0,0,1200,900) (-1800,40,1800,1350)
            sourceInsets targetInsets (WindowFrameMargin.inset sourceInsets visible)
    check name (mapped = expected && WindowFrameMargin.outset targetInsets native = expected)
checkMappedClamp "visible right and bottom overhang is clamped"
    { x=1000; y=800; width=400; height=300 } { x= -600; y=940; width=600; height=450 }
checkMappedClamp "oversized visible frame keeps size at target origin"
    { x=100; y=80; width=1400; height=1000 } { x= -1800; y=40; width=2100; height=1500 }
check "full width staging stays inside" (snapStagingPosition snapRight (10,20,1200,900) 1200 600 = (10,20))
check "full height staging stays inside" (snapStagingPosition snapBottom (10,20,1200,900) 800 900 = (10,20))
check "partial width staging keeps right alignment" (snapStagingPosition snapRight (10,20,1200,900) 800 600 = (410,20))
check "partial height staging keeps bottom alignment" (snapStagingPosition snapBottom (10,20,1200,900) 800 600 = (10,320))
let syntheticNative : WindowFrameMargin.Bounds = { x = 115; y = 64; width = 590; height = 430 }
let visible1, visible2, native2 =
    WindowFrameMargin.mapNormalPlacement
        (0, 0, 1200, 900) (-1800, 40, 1800, 1350) (6, 5, 5, 4) (9, 8, 8, 6) syntheticNative
check "external frames map before applying target margins"
    (visible1 = { x=110; y=58; width=600; height=440 } &&
     visible2 = { x = -1635; y=127; width=900; height=660 })
check "external target frame round trip" (WindowFrameMargin.outset (9,8,8,6) native2 = visible2)
let _, sameVisible, sameNative =
    WindowFrameMargin.mapNormalPlacement
        (0,0,1200,900) (0,0,1200,900) (6,5,5,4) (6,5,5,4) syntheticNative
check "same screen normal native round trip" (sameNative = syntheticNative)
let _, otherVisible, otherNative =
    WindowFrameMargin.mapNormalPlacement
        (0,0,1200,900) (-1800,40,1800,1350) (6,5,5,4) (-4,-3,-3,-4) syntheticNative
check "members with different frames share visible placement"
    (otherVisible = visible2 && WindowFrameMargin.outset (-4,-3,-3,-4) otherNative = visible2)

printfn ""
// Synthetic placement reserves the top gap; one extra pixel checks fractional width truncation.
let samePositionInsets = (0, -10, -10, -10)
let samePositionSource : WindowFrameMargin.Bounds = { x= -10; y=20; width=1021; height=990 }
let samePositionFrame, samePositionMapped, samePositionNative =
    WindowFrameMargin.mapNormalPlacement
        (0,0,2000,1000) (-1500,0,1500,1000) samePositionInsets samePositionInsets samePositionSource
check "same-position source excludes hidden borders"
    (samePositionFrame = { x=0; y=20; width=1001; height=980 })
check "same-position visible top gap survives clamping"
    (samePositionMapped = { x= -1500; y=20; width=750; height=980 })
check "same-position native bounds restore hidden borders"
    (samePositionNative = { x= -1510; y=20; width=770; height=990 })
let scaledNormalInsets = WindowFrameMargin.scale 96 144 { top=0; left=10; right=10; bottom=10 }
let st, sl, sr, sb = scaledNormalInsets
let _, scaledSamePosition, scaledSamePositionNative =
    WindowFrameMargin.mapNormalPlacement
        (0,0,2000,1000) (-1500,0,1500,1000) samePositionInsets (-st,-sl,-sr,-sb) samePositionSource
check "same-position destination DPI scales only native borders"
    (scaledSamePosition = samePositionMapped && scaledSamePositionNative.x = -1515 &&
     WindowFrameMargin.outset (-st,-sl,-sr,-sb) scaledSamePositionNative = samePositionMapped)

printfn "%d passed, %d failed" passed failed
exit (if failed = 0 then 0 else 1)
