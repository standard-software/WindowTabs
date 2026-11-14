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
let terminal = @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.0.0.0_x64__8wekyb3d8bbwe\WindowsTerminal.exe"
let store = @"C:\Program Files\WindowsApps\Example.App_1.0.0.0_x64__examplepub\app\app.exe"

printfn "store detection"
check "an ordinary program is not a Store app" (not (LaunchPath.isStoreApp chrome))
check "a package folder path is a Store app" (LaunchPath.isStoreApp store)
check "Windows Terminal is a Store app" (LaunchPath.isStoreApp terminal)

printfn "resolve"
check "an ordinary program starts from its own path" (LaunchPath.resolve chrome = Some chrome)
check "Windows Terminal starts through wt.exe" (LaunchPath.resolve terminal = Some "wt.exe")
check "a Store app without an alias requires AUMID activation" (LaunchPath.resolve store = None)
check "the terminal alias is matched on the file name, case-insensitively"
    (LaunchPath.resolve @"C:\Program Files\WindowsApps\x\WINDOWSTERMINAL.EXE" = Some "wt.exe")
check "a terminal-like name outside WindowsApps is left alone"
    (LaunchPath.resolve @"C:\tools\WindowsTerminal.exe" = Some @"C:\tools\WindowsTerminal.exe")
check "an empty path is an ordinary (if useless) path, not a Store app" (LaunchPath.resolve "" = Some "")

printfn "activation policy (injected effects only)"
let notepad = @"C:\Program Files\WindowsApps\Microsoft.WindowsNotepad_x64__8wekyb3d8bbwe\Notepad.exe"
let aumid = "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App"
let exercise path id directFailure activationResult =
    let calls = ResizeArray<string>()
    let result = LaunchPath.startWith path
                    (fun target ->
                        calls.Add("direct:" + target)
                        if directFailure then raise (System.ComponentModel.Win32Exception(5)))
                    (fun () -> calls.Add("aumid"); id)
                    (fun value -> calls.Add("activate:" + value); activationResult)
    result, List.ofSeq calls
let result1, calls1 = exercise notepad (Some aumid) false true
check "Notepad activates by AUMID before an error is requested"
    (result1 && calls1 = ["aumid"; "activate:" + aumid])
for id in [None; Some ""; Some "  "] do
    let result1, calls1 = exercise notepad id false true
    check "missing AUMID requests the UWP error without attempting COM" (not result1 && calls1 = ["aumid"])
let result2, calls2 = exercise notepad (Some aumid) false false
check "failed activation requests the UWP error after the attempt"
    (not result2 && calls2 = ["aumid"; "activate:" + aumid])
let result3, calls3 = exercise terminal (Some aumid) false true
check "successful Terminal alias launch does not query or activate AUMID"
    (result3 && calls3 = ["direct:wt.exe"])
let result4, calls4 = exercise terminal (Some aumid) true true
check "failed Terminal alias launch tries AUMID next"
    (result4 && calls4 = ["direct:wt.exe"; "aumid"; "activate:" + aumid])
let result5, calls5 = exercise terminal (Some aumid) true false
check "both Terminal launch methods failing requests the UWP error"
    (not result5 && calls5 = ["direct:wt.exe"; "aumid"; "activate:" + aumid])
let result6, calls6 = exercise chrome (Some aumid) false true
check "ordinary launch keeps the existing callback"
    (result6 && calls6 = ["direct:" + chrome])
let mutable queried = false
let mutable ordinaryFailed = false
try
    LaunchPath.startWith chrome (fun _ -> raise (System.ComponentModel.Win32Exception(5)))
        (fun () -> queried <- true; Some aumid) (fun _ -> true) |> ignore
with :? System.ComponentModel.Win32Exception -> ordinaryFailed <- true
check "ordinary process failure preserves its existing error handling" (ordinaryFailed && not queried)
let mutable unexpectedFailed = false
try
    LaunchPath.startWith terminal (fun _ -> failwith "unexpected")
        (fun () -> failwith "must not query") (fun _ -> failwith "must not activate") |> ignore
with ex -> unexpectedFailed <- ex.Message = "unexpected"
check "unexpected launch errors are not replaced with UWP errors" unexpectedFailed

printfn ""
if failed = 0 then
    printfn "all %d checks passed" passed
    exit 0
else
    printfn "%d of %d check(s) FAILED" failed (passed + failed)
    exit 1
