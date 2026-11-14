#load "SnapGeometry.fs"
#load "WindowFrameMargin.fs"
open Bemo
let mutable checks = 0
let equal label expected actual =
    checks <- checks + 1
    if expected <> actual then failwithf "%s: expected %A, got %A" label expected actual
let expected = [
    "640x400"; "VGA 640x480"; "SVGA 800x600"; "XGA 1024x768"
    "HD 1280x720"; "WXGA 1280x800"; "SXGA 1280x1024"; "1366x768"
    "WXGA+ 1440x900"; "HD+ 1600x900"; "WSXGA+ 1680x1050"
    "Full HD 1920x1080"; "WUXGA 1920x1200"; "WQHD 2560x1440"
    "WQXGA 2560x1600"; "4K UHD 3840x2160"
]
equal "labels and order" expected (SnapGeometry.sizePresets |> List.map (fun (s,_,_) -> s))
for label,w,h in SnapGeometry.sizePresets do
    equal "label dimensions" true (label.EndsWith(sprintf "%dx%d" w h))
let place = SnapGeometry.sizeBounds (0,0,1920,1080)
equal "keep top left" (Some(100,120,640,480)) (place (100,120) (640,480))
equal "right and bottom overflow" (Some(1280,600,640,480)) (place (1800,1000) (640,480))
equal "left and top overflow" (Some(0,0,640,480)) (place (-100,-100) (640,480))
equal "exact fit" (Some(0,0,1920,1080)) (place (100,100) (1920,1080))
equal "too wide" None (place (0,0) (1921,480))
equal "too tall" None (place (0,0) (640,1081))
equal "negative monitor" (Some(-640,-480,640,480)) (SnapGeometry.sizeBounds (-1920,-1080,1920,1080) (0,0) (640,480))
for dpi in [96;144;192] do
    let tabHeight = int (System.Math.Round(25.0 * float dpi / 96.0))
    let area = SnapGeometry.reserveTabHeight tabHeight (0,0,3840,2160)
    equal "physical pixels at every DPI" (Some(100,100,1920,1080)) (SnapGeometry.sizeBounds area (100,100) (1920,1080))
    equal "reserved tab margin excludes full height" None (SnapGeometry.sizeBounds area (0,0) (3840,2160))
    equal "top shifted below tab margin" (Some(0,tabHeight,640,480)) (SnapGeometry.sizeBounds area (0,0) (640,480))
    for m in [{WindowFrameMargin.zero with left=8;right=8;top=8;bottom=8};
              {WindowFrameMargin.zero with left= -8;right= -8;bottom= -8}] do
        let margin = WindowFrameMargin.scale 96 dpi m
        let visible : WindowFrameMargin.Bounds = {x=100;y=100;width=1920;height=1080}
        let native = WindowFrameMargin.inset margin visible
        equal "frame round trip at DPI" visible (WindowFrameMargin.outset margin native)
let source = (0,40,1920,1040)
let target = (1920,0,2560,1440)
let mapped = SnapGeometry.sizePosition false source target (480,300)
equal "relative position on larger right display" (2560,360) mapped
equal "relative position keeps pixel size" (Some(2560,360,640,480)) (SnapGeometry.sizeBounds target mapped (640,480))
equal "reverse mapping" (480,300) (SnapGeometry.sizePosition false target source mapped)
equal "same display preserves position" (480,300) (SnapGeometry.sizePosition true source source (480,300))
equal "negative display mapping" (-1440,-780) (SnapGeometry.sizePosition false source (-1920,-1040,1920,1040) (480,300))
let overflow = SnapGeometry.sizePosition false source target (1800,1000)
equal "clamp after proportional mapping" (Some(3840,960,640,480)) (SnapGeometry.sizeBounds target overflow (640,480))
let tabHeightAt scale = int (System.Math.Round(25.0 * scale, System.MidpointRounding.AwayFromZero))
let workArea = (1920,0,1920,1104)
let sizeArea same current target enabled = SnapGeometry.sizeWorkArea same current target tabHeightAt enabled workArea
let low = sizeArea false 2.0 1.0 true
let high = sizeArea false 1.0 2.0 true
equal "destination low DPI margin" (1920,24,1920,1080) low
equal "destination high DPI margin" (1920,49,1920,1055) high
equal "same display keeps committed strip scale" low (sizeArea true 1.0 2.0 true)
equal "fractional destination DPI margin" (1920,37,1920,1067) (sizeArea false 1.0 1.5 true)
equal "disabled margin" workArea (sizeArea false 1.0 2.0 false)
equal "low DPI boundary enabled" (Some(1920,24,1920,1080)) (SnapGeometry.sizeBounds low (1920,0) (1920,1080))
equal "high DPI boundary disabled" None (SnapGeometry.sizeBounds high (1920,0) (1920,1080))
equal "destination margin clamps top" (Some(1920,49,640,480)) (SnapGeometry.sizeBounds high (1920,0) (640,480))
printfn "%d checks, 0 failures" checks
