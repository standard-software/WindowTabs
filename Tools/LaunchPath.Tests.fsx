// Checks LaunchPath - what "open another window of this program" starts -
// without starting WindowTabs. Loads the file that ships.
//
//   dotnet fsi Tools\LaunchPath.Tests.fsx
//
// Exit code 0 when every check passes, 1 otherwise.

#load "../WtProgram/Shared/LaunchPath.fs"

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

let chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe"
let terminal = @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.24.11911.0_x64__8wekyb3d8bbwe\WindowsTerminal.exe"
let store = @"C:\Program Files\WindowsApps\Claude_1.0.0.0_x64__pzs8sxrjxfjjc\app\claude.exe"

printfn "store detection"
check "an ordinary program is not a Store app" (not (LaunchPath.isStoreApp chrome))
check "a package folder path is a Store app" (LaunchPath.isStoreApp store)
check "Windows Terminal is a Store app" (LaunchPath.isStoreApp terminal)

printfn "resolve"
check "an ordinary program starts from its own path" (LaunchPath.resolve chrome = Some chrome)
check "Windows Terminal starts through wt.exe" (LaunchPath.resolve terminal = Some "wt.exe")
check "a Store app with no alias cannot be started (None -> the caller reports it)" (LaunchPath.resolve store = None)
check "the terminal alias is matched on the file name, case-insensitively"
    (LaunchPath.resolve @"C:\Program Files\WindowsApps\x\WINDOWSTERMINAL.EXE" = Some "wt.exe")
check "a terminal-like name outside WindowsApps is left alone"
    (LaunchPath.resolve @"C:\tools\WindowsTerminal.exe" = Some @"C:\tools\WindowsTerminal.exe")
check "an empty path is an ordinary (if useless) path, not a Store app" (LaunchPath.resolve "" = Some "")

printfn ""
if failed = 0 then
    printfn "all %d checks passed" passed
    exit 0
else
    printfn "%d of %d check(s) FAILED" failed (passed + failed)
    exit 1
