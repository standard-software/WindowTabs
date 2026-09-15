// Self-checks for the input hook's decisions (WtProgram/Shared/CaptionDragPolicy.fs)
// and the shared registry (CaptionDragTargets.fs). Pure: no hook, no window.
// Run from the repository root:
//   fsi.exe --exec .\Tools\CaptionDragPolicy.Tests.fsx
#load "../WtProgram/Shared/CaptionDragPolicy.fs"
#load "../WtProgram/Shared/CaptionDragFallback.fs"
#load "../WtProgram/Shared/CaptionDragTargets.fs"
open System
open Bemo
open Bemo.CaptionDragPolicy

let mutable checks = 0
let check name condition =
    if not condition then failwith name
    checks <- checks + 1
    printfn "PASS %s" name
let click target x y time = { target = target; receiver = target; x = x; y = y; hitX = x; hitY = y; time = time }
let step = CaptionDragPolicy.step 500u 2 2
let a = click 1L -100 200 1000u
let down = Down (Some a)

// ----- press / release pairing and double-click -----
let held, action = step empty down
check "Managed caption activates without delivering button-down" (action = Activate 1L && held.pendingUp)
let mutable during = held
for i in 1..3 do
    let next, action = step during Other
    check (sprintf "Motion %d passes without releasing the blocked gesture" i) (action = Pass && next.pendingUp)
    during <- next
let released, releaseAction = step during Up
check "Release pairs with intercepted down" (releaseAction = Block && not released.pendingUp)
check "Later release is untouched" (snd (step released Up) = Pass)
let secondClick = { a with time = 1200u }
let twice, doubleAction = step released (Down (Some secondClick))
check "Native double-click requested on same window, carrying the receiver and its point"
    (doubleAction = DoubleClick secondClick && twice.previous.IsNone)
let childBar = { a with receiver = 77L; hitX = -66; hitY = 133 }
let childHeld, _ = step empty (Down (Some childBar))
let childDouble = snd (step (fst (step childHeld Up)) (Down (Some { childBar with time = 1100u })))
check "Double-click on a child drag bar goes to the child, with the child's point"
    (match childDouble with DoubleClick c -> c.receiver = 77L && c.target = 1L && c.hitX = -66 | _ -> false)
let third, _ = step twice Up
check "Third click starts a new pair" (snd (step third (Down (Some { a with time = 1300u }))) = Activate 1L)
for name, value in ["Different target", { a with target = 2L; time = 1200u };
                    "Outside time threshold", { a with time = 1501u };
                    "Outside horizontal threshold", { a with x = -103; time = 1200u };
                    "Outside vertical threshold", { a with y = 203; time = 1200u }] do
    check name (snd (step released (Down (Some value))) = Activate value.target)
check "Duplicate down cannot maximize" (snd (step held down) = Activate 1L)
let disabled, _ = step held Disable
check "OFF mid-gesture clears double-click history" disabled.previous.IsNone
check "OFF mid-gesture still pairs swallowed release" (snd (step disabled Up) = Block)
check "OFF next click passes" (snd (step disabled (Down None)) = Pass)
let unrelated, _ = step released (Down None)
check "Intervening client/strip click clears history" (snd (step unrelated down) = Activate 1L)
let wrapped, _ = step empty (Down (Some { a with time = UInt32.MaxValue - 100u }))
let wrappedReleased, _ = step wrapped Up
check "Tick wrap preserves double-click interval" (snd (step wrappedReleased (Down (Some { a with time = 100u }))) = DoubleClick { a with time = 100u })

// ----- lParam packing (MAKELPARAM) -----
let unpack (l: int) = int (int16 (l &&& 0xffff)), int (int16 ((l >>> 16) &&& 0xffff))
check "Point packing keeps positive coordinates" (unpack (packPoint 1919 1079) = (1919, 1079))
check "Point packing keeps a monitor left of and above the primary (negative)" (unpack (packPoint -1500 -20) = (-1500, -20))
check "Point packing matches MAKELPARAM bit layout" (packPoint -1 2 = 0x0002ffff)

// ----- DPI: which point is hit-tested (P0: never hit-test a virtualized window with physical pixels) -----
check "Per-monitor aware window is asked with the physical point" (hitTestPoint awarenessPerMonitor (-1500, 20) None = Some(-1500, 20))
check "Per-monitor aware window ignores any conversion" (hitTestPoint awarenessPerMonitor (300, 20) (Some(200, 13)) = Some(300, 20))
check "System-aware window is asked with the converted logical point" (hitTestPoint awarenessSystem (300, 20) (Some(200, 13)) = Some(200, 13))
check "DPI-unaware window is asked with the converted logical point" (hitTestPoint awarenessUnaware (300, 20) (Some(450, 30)) = Some(450, 30))
check "Virtualized window without a conversion is not asked (click passes)" (hitTestPoint awarenessSystem (300, 20) None = None)
check "Unknown awareness is not asked (click passes)" (hitTestPoint -1 (300, 20) (Some(300, 20)) = None)
check "Virtualized caption answer above the client area is trusted" (trustCaption awarenessSystem 110 (Some 130))
check "Virtualized caption answer inside the client area is not trusted" (not (trustCaption awarenessSystem 400 (Some 130)))
check "Virtualized caption answer without a client origin is not trusted" (not (trustCaption awarenessUnaware 110 None))
check "Per-monitor caption answer inside a custom frame's client area is trusted" (trustCaption awarenessPerMonitor 400 None)

// ----- hit walk from the window under the cursor -----
check "Top-level caption is swallowed" (hitStep true false false false hitCaption = CaptionHit)
check "Border, corner, client, buttons and system menu of the top-level window pass"
    ([-2; 0; 1; 3; 4; 8; 9; 10; 11; 12; 13; 14; 15; 16; 17; 18; 20; 21]
     |> List.forall (fun hit -> hitStep true false false false hit = OtherHit hit))
check "Captionless child answering HTCAPTION (drag bar) is swallowed" (hitStep false false false false hitCaption = CaptionHit)
check "MDI-style child with its own caption passes" (hitStep false true false false hitCaption = OtherHit hitCaption)
// Office draws the search box, the file-name menu and the account button in
// one child that answers HTCAPTION for the whole band: those clicks must
// arrive, and a drag started there is undone by the fallback instead.
check "A child whose caption band holds controls passes" (hitStep false false true false hitCaption = OtherHit hitCaption)
check "Office's caption host is known" (captionHostsControls "NetUIHWND")
check "Other caption children are not" (not (captionHostsControls "Chrome_RenderWidgetHostHWND"))
check "Transparent child climbs to a same-thread parent" (hitStep false false false true hitTransparent = Climb)
check "Transparent child of another thread stops the walk" (hitStep false false false false hitTransparent = OtherHit hitTransparent)
check "Transparent top-level window does not climb" (hitStep true false false true hitTransparent = OtherHit hitTransparent)
check "Child client area passes" (hitStep false false false true 1 = OtherHit 1)

// ----- hook lifecycle -----
check "Stopping wins over everything" (plan true true true true true = Exit)
check "ON without a hook installs one" (plan false true false false false = Install)
check "ON with a hook keeps it (and stops a leftover release poll)" (plan false true true true true = Keep)
check "OFF with a swallowed press still held waits for its release" (plan false false true true true = WaitForRelease)
check "OFF with the swallowed press released removes the hook" (plan false false true true false = Remove)
check "OFF with no swallowed press removes the hook even if a button is held" (plan false false true false true = Remove)
check "Primary button is VK_LBUTTON normally" (primaryButtonKey false = 1)
check "Primary button is VK_RBUTTON with swapped buttons" (primaryButtonKey true = 2)
check "Silent hook loss: cursor moved and no callback for 1 s" (hookLooksLost 1500 true)
check "No loss while callbacks are recent" (not (hookLooksLost 200 true))
check "No loss while the cursor stands still" (not (hookLooksLost 60000 false))

// ----- double-click alternative -----
check "System-command double-click restores a maximized window" (doubleClickCommand true true = Some scRestore)
check "System-command double-click maximizes a normal window with a maximize box" (doubleClickCommand false true = Some scMaximize)
check "System-command double-click does nothing without a maximize box" (doubleClickCommand false false = None)

// ----- registry shared with the group threads -----
let firstOwner, secondOwner = obj(), obj()
let hwnd = IntPtr(42)
check "Unregistered target absent" (not (CaptionDragTargets.contains hwnd))
CaptionDragTargets.add firstOwner hwnd
check "New managed target present" (CaptionDragTargets.contains hwnd)
CaptionDragTargets.add secondOwner hwnd
CaptionDragTargets.remove firstOwner hwnd
check "Old-group removal cannot erase transferred target" (CaptionDragTargets.contains hwnd)
CaptionDragTargets.remove secondOwner hwnd
check "Removal/destruction unregisters target" (not (CaptionDragTargets.contains hwnd))
check "Removal after down still consumes matching up" (snd (step held Up) = Block)
let before = CaptionDragTargets.pressSequence()
let sequence = CaptionDragTargets.notePress()
check "Every press advances the sequence the fallback's settle time watches" (sequence = before + 1L && CaptionDragTargets.pressSequence() = sequence)
let press : CaptionDragTargets.Press =
    { hwnd = hwnd; rootHit = Some 1; x = 5; y = 6; bounds = CaptionDragFallback.box 0 0 10 10; tick = 0; sequence = sequence }
CaptionDragTargets.setPress (Some press)
check "Recorded press is readable by the group" (CaptionDragTargets.currentPress() = Some press)
CaptionDragTargets.setPress None
check "Cleared press is gone" (CaptionDragTargets.currentPress().IsNone)
// Terminal's standard caption boundary: preserve genuine resize surfaces.
let frame : CaptionRect = { left = -1700; top = 180; right = -600; bottom = 830 }
let title : CaptionRect = { left = -1670; top = 188; right = -608; bottom = 211 }
let buttons : CaptionRect = { left = -753; top = 180; right = -607; bottom = 210 }
let boundary hit supported visible t client b x y =
    nativeCaptionBoundary hit supported visible frame t client b x y
check "Terminal first native caption row is blocked" (boundary 12 true true title 211 buttons -1200 188)
check "Terminal last actual resize row remains available" (not (boundary 12 true true title 211 buttons -1200 187))
check "Unsupported frame information keeps its HTTOP answer" (not (boundary 12 false true title 211 buttons -1200 188))
check "Custom title bar overlapping the client is excluded" (not (boundary 12 true true title 180 buttons -1200 188))
check "Caption buttons retain their input" (not (boundary 12 true true title 211 buttons -700 188))
check "System menu retains its input" (not (boundary 12 true true title 211 buttons -1690 188))
check "Left corner resize is untouched" (not (boundary 13 true true title 211 buttons -1200 188))
check "Right corner resize is untouched" (not (boundary 14 true true title 211 buttons -1200 188))
check "Client clicks are untouched" (not (boundary 1 true true title 211 buttons -1200 188))
check "Invisible captions are excluded" (not (boundary 12 true false title 211 buttons -1200 188))
check "Client top is excluded" (not (boundary 12 true true title 211 buttons -1200 211))
check "Invalid button geometry fails open" (not (boundary 12 true true title 211 { buttons with right = buttons.left } -1200 188))
check "Out of window button geometry fails open" (not (boundary 12 true true title 211 { buttons with right = 0 } -1200 188))
for scale in [1.0; 1.25; 1.5; 1.75; 2.0] do
    let scaled (r: CaptionRect) =
        let n v = int (float v * scale)
        { left = n r.left; top = n r.top; right = n r.right; bottom = n r.bottom }
    let f,t,b = scaled frame, scaled title, scaled buttons
    let x = int (-1200.0 * scale)
    check (sprintf "Native caption boundary at scale %g" scale)
        (nativeCaptionBoundary 12 true true f t t.bottom b x t.top)
    check (sprintf "Native resize boundary at scale %g" scale)
        (not (nativeCaptionBoundary 12 true true f t t.bottom b x (t.top - 1)))
// Actual 100% custom-frame geometry: do not reinterpret an app's resize hit.
let customFrame : CaptionRect = { left = 0; top = 24; right = 2238; bottom = 1032 }
let customTitle : CaptionRect = { left = 30; top = 32; right = 2230; bottom = 55 }
let customButtons : CaptionRect = { left = 2090; top = 24; right = 2230; bottom = 54 }
check "VSCode custom title bar is not corrected" (not (nativeCaptionBoundary 12 true true customFrame customTitle 24 customButtons 500 32))
check "Excel custom title bar is not corrected" (not (nativeCaptionBoundary 12 true true customFrame customTitle 25 customButtons 500 32))
check "Chrome missing native title rectangle is not corrected" (not (nativeCaptionBoundary 12 true true customFrame { customTitle with bottom = 0; top = 0 } 24 customButtons 500 32))
check "Native frame without an app name is corrected" (nativeCaptionBoundary 12 true true frame title 211 buttons -1200 188)

// The top border resizes the window from the edge the tab strip sits on, so a
// locked window blocks it; the other borders keep resizing.
check "HTTOP is a top border" (isTopBorder 12)
check "HTTOPLEFT is a top border" (isTopBorder 13)
check "HTTOPRIGHT is a top border" (isTopBorder 14)
for hit, name in [1, "HTCLIENT"; 10, "HTLEFT"; 11, "HTRIGHT"; 15, "HTBOTTOM"; 16, "HTBOTTOMLEFT"; 17, "HTBOTTOMRIGHT"] do
    check (sprintf "%s is not a top border" name) (not (isTopBorder hit))
printfn "All %d checks passed" checks
