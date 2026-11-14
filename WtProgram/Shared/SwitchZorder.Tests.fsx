// TestRuntime: Framework
// Load pure production decisions without starting WindowTabs or calling native APIs.
#r "System.Drawing"
#r "System.Windows.Forms"
#r "Newtonsoft.Json.dll"
#r "FSharp.PowerPack.dll"
#r "Aga.Controls.dll"
#r "Win32.dll"
#r "WindowTabs.exe"
open System
open Bemo.VirtualDesktopGroups
let mutable checks = 0
let equal label expected actual =
    checks <- checks + 1
    if actual <> expected then failwithf "%s: expected %A, got %A" label expected actual
let local : StripMember =
    { hwnd=1n; exists=true; visible=true; minimized=false; cloak=Some 0; presence=Here }
let destination = { local with hwnd=2n; cloak=Some 2; presence=Away }
let members = [local; destination]
equal "ordinary repair excludes shell-cloaked destination" (Some 1n) (stripRepairFront false None members)
equal "prediction anchors at shell-cloaked destination" (Some 2n) (stripRepairFront true (Some 2n) members)
equal "expired transition rejects cloaked preview" (Some 1n) (stripRepairFront false (Some 2n) members)
equal "unknown preferred handle cannot become anchor" (Some 1n) (stripRepairFront true (Some 9n) members)
for blocked in [ {destination with exists=false}; {destination with visible=false};
                 {destination with minimized=true}; {destination with cloak=Some 1};
                 {destination with cloak=Some 4}; {destination with cloak=Some 3};
                 {destination with cloak=Some 6} ] do
    equal "ineligible destination falls back without raising it" (Some 1n)
        (stripRepairFront true (Some 2n) [local;blocked])
equal "known destination is usable during external transition" (Some 2n)
    (stripRepairFront true None [{destination with presence=Here}])
equal "away member is not an external transition anchor" None
    (stripRepairFront true None [destination])
equal "native order wins without a preferred member" (Some 2n)
    (stripRepairFront true None [{destination with presence=Here; cloak=Some 0};local])
equal "empty group has no anchor" None (stripRepairFront true None [])
equal "unknown cloak retains existing eligibility" (Some 1n)
    (stripRepairFront false None [{local with cloak=None}])
equal "adjacent normal strip remains in place" KeepStacking (stripStacking true true false 3n false)
equal "normal target never requests topmost" NormalLayer (stripStacking false false false 3n true)
printfn "%d checks passed" checks
