// TestRuntime: Framework
// Run with Framework FSI and --lib pointing to an isolated Debug build.
#r "System.Windows.Forms"
#r "Win32.dll"
open System
open System.Reflection
open Bemo.Win32

let equal name expected actual =
    if expected <> actual then failwithf "%s: expected %A, got %A" name expected actual

let format = HotKeyControl.FormatMenuShortcut
let packed flags key = (flags <<< 8) ||| key
for managed in [false; true] do
    for flags in [0 .. 15] do
        equal "unassigned shortcut" "" (format(packed flags 0, managed))
    let methodName = if managed then "FormatHotKey" else "FormatNativeHotKey"
    let fieldFormatter = typeof<HotKeyControl>.GetMethod(methodName, BindingFlags.Static ||| BindingFlags.NonPublic)
    for flags in [0 .. 15] do
        for key in [0x4E; 0x31; 0x70; 0x25; 0x2E; 0xBA] do
            let code = packed flags key
            let expected = fieldFormatter.Invoke(null, [| box code |]) :?> string
            equal "settings field format" expected (format(code, managed))

equal "control shift letter" "Ctrl + Shift + N" (format(packed 3 0x4E, true))
equal "all modifiers" "Ctrl + Alt + Shift + N" (format(packed 7 0x4E, true))
equal "digit" "Alt + 1" (format(packed 4 0x31, true))
equal "function key" "F1" (format(0x70, true))
printfn "New tab menu shortcut tests passed."
