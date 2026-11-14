// Run with dotnet fsi WtProgram/Shared/StartupRegistration.Tests.fsx
#load "StartupRegistration.fs"
#r "../../Newtonsoft.Json.dll"
#load "../Localization/JsoncHelper.fs"

open System
open System.IO
open System.Diagnostics
open Bemo

let mutable passed = 0
let check name condition =
    if not condition then failwith ("FAIL: " + name)
    passed <- passed + 1

let exe = @"C:\a\app.exe"
check "case insensitive" (StartupRegistration.samePath exe @"c:\A\APP.EXE")
check "full path normalization" (StartupRegistration.samePath exe @"C:\a\b\..\app.exe")
check "other installation" (not (StartupRegistration.samePath exe @"C:\b\app.exe"))
check "relative path rejected" (not (StartupRegistration.samePath exe "app.exe"))
check "empty target" (not (StartupRegistration.samePath exe ""))
check "quoted legacy" (StartupRegistration.registryMatches exe (box ("\"" + exe + "\"")))
check "unquoted legacy" (StartupRegistration.registryMatches exe (box exe))
check "quoted arguments" (StartupRegistration.registryMatches exe (box ("\"" + exe + "\" --flag")))
check "unquoted arguments" (StartupRegistration.registryMatches exe (box (exe + " --flag")))
check "spaces in path" (StartupRegistration.registryMatches @"C:\a b\app.exe" (box @"C:\a b\app.exe"))
check "different legacy" (not (StartupRegistration.registryMatches exe (box @"C:\b\app.exe")))
check "prefix rejected" (not (StartupRegistration.registryMatches exe (box (exe + "x"))))
check "bad quoting" (not (StartupRegistration.registryMatches exe (box ("\"" + exe))))
check "null legacy" (not (StartupRegistration.registryMatches exe null))
check "nonstring legacy" (not (StartupRegistration.registryMatches exe (box 1)))
let enable = StartupRegistration.plan true [] true
check "enable creates only a shortcut" (enable.Create && enable.Delete.IsEmpty && not enable.DeleteLegacy)
check "enable preserves matching shortcut" (not (StartupRegistration.plan true ["link-1"] false).Create)
let disable = StartupRegistration.plan false ["link-1"; "link-2"] true
check "disable removes all matching registrations" (not disable.Create && disable.Delete.Length = 2 && disable.DeleteLegacy)
check "disable preserves unrelated legacy" (not (StartupRegistration.plan false [] false).DeleteLegacy)

check "drive-relative rejected" (not (StartupRegistration.samePath exe @"C:a\app.exe"))
check "root-relative rejected" (not (StartupRegistration.samePath exe @"\a\app.exe"))
let variable = "WT_STARTUP_TEST_ROOT"
let previous = Environment.GetEnvironmentVariable(variable)
try
    Environment.SetEnvironmentVariable(variable, @"C:\a")
    check "environment target expanded" (StartupRegistration.samePath exe @"%WT_STARTUP_TEST_ROOT%\app.exe")
    check "environment legacy expanded" (StartupRegistration.registryMatches exe (box @"%WT_STARTUP_TEST_ROOT%\app.exe"))
finally Environment.SetEnvironmentVariable(variable, previous)

let mutable readErrors = 0
let readTarget path =
    match path with
    | "denied.lnk" -> raise (UnauthorizedAccessException())
    | "com-denied.lnk" -> raise (System.Runtime.InteropServices.COMException("denied", -2147024891))
    | _ -> exe
let scan onError =
    StartupRegistration.matchingTargets exe readTarget onError ["denied.lnk"; "com-denied.lnk"; "match.lnk"] |> Seq.toList
check "unreadable links do not hide match" (scan (fun _ -> readErrors <- readErrors + 1) = ["match.lnk"])
check "all read failures retained" (readErrors = 2)
let removed = ResizeArray<string>()
let mutable legacyRemoved = false
let mutable operationFailed = false
try
    StartupRegistration.applyChange false scan (fun () -> failwith "Unexpected creation") removed.Add
        (fun () -> legacyRemoved <- true)
with _ -> operationFailed <- true
check "off ignores unrelated read failures" (removed.Count = 1 && legacyRemoved && not operationFailed)
let mutable laterRemoved = false
legacyRemoved <- false
operationFailed <- false
try
    StartupRegistration.applyChange false (fun _ -> ["link-1"; "link-2"]) ignore
        (fun path -> if path = "link-1" then raise (IOException()) else laterRemoved <- true)
        (fun () -> legacyRemoved <- true)
with _ -> operationFailed <- true
check "off continues after delete failures" (laterRemoved && legacyRemoved && operationFailed)
legacyRemoved <- false
try
    StartupRegistration.applyChange false (fun _ -> raise (IOException())) ignore ignore
        (fun () -> legacyRemoved <- true)
with _ -> ()
check "legacy attempted after enumeration failure" legacyRemoved
for requested in [false; true] do
    for remaining in [false; true] do
        for fails in [false; true] do
            let mutable attempted = false
            let actual, failed = StartupRegistration.changeAndRead requested
                                    (fun () -> attempted <- true; if fails then raise (IOException()))
                                    (fun () -> check "read follows attempt" attempted; remaining)
            check "actual state wins over requested state" (actual = remaining)
            check "operation failure or state mismatch reported" (failed = (fails || remaining <> requested))

let mutable created = false
StartupRegistration.applyChange true scan (fun () -> created <- true) ignore ignore
check "on with unreadable links preserves existing match" (not created)
let unreadableOnly onError =
    StartupRegistration.matchingTargets exe readTarget onError ["denied.lnk"; "com-denied.lnk"] |> Seq.toList
StartupRegistration.applyChange true unreadableOnly (fun () -> created <- true) ignore ignore
check "on with only unrelated unreadable links succeeds" created
for enabled in [false; true] do
    for error in [UnauthorizedAccessException() :> exn; IOException() :> exn] do
        let mutable legacyAttempted = false
        let mutable failed = false
        try
            StartupRegistration.applyChange enabled (fun _ -> raise error) ignore ignore
                (fun () -> legacyAttempted <- true)
        with _ -> failed <- true
        check "folder read failure reported" failed
        check "folder failure does not skip legacy removal" (legacyAttempted = not enabled)

let original = Newtonsoft.Json.Linq.JObject.Parse("{\"RunAtStartup\":true,\"nested\":{\"RunAtStartup\":true}}")
let cleaned = RemovedSettings.remove original
check "obsolete root removed" (isNull cleaned.["RunAtStartup"])
check "cleanup preserves caller" (not (isNull original.["RunAtStartup"]))
check "cleanup preserves nested schema" (not (isNull cleaned.["nested"].["RunAtStartup"]))
check "obsolete key is case insensitive" (RemovedSettings.isRemovedKey "runatstartup")

let testShortcuts() =
    let folder = Path.Combine(Path.GetTempPath(), "startup-test-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(folder) |> ignore
    try
        check "missing folder is empty"
            (StartupRegistration.matchingShortcuts (Path.Combine(folder, "missing")) exe).IsEmpty
        let notFolder = Path.Combine(folder, "file-1")
        File.WriteAllText(notFolder, "value-1")
        let mutable folderFailed = false
        try StartupRegistration.matchingShortcuts notFolder exe |> ignore
        with :? IOException -> folderFailed <- true
        check "folder IO failure is not hidden as empty" folderFailed
        let target = Path.Combine(folder, "app.exe")
        File.WriteAllBytes(target, [||])
        let other = Path.Combine(folder, "other", "app.exe")
        let occupied = Path.Combine(folder, "WindowTabs.lnk")
        StartupRegistration.createShortcut occupied other
        StartupRegistration.addShortcut folder target
        let renamed = Path.Combine(folder, "link-1.lnk")
        StartupRegistration.createShortcut renamed target
        File.WriteAllText(Path.Combine(folder, "broken.lnk"), "invalid")
        let watch = Stopwatch.StartNew()
        let found = StartupRegistration.hasMatchingShortcut folder target
        watch.Stop()
        check "startup detects matching shortcut" found
        printfn "Synthetic startup scan: %d ms" watch.ElapsedMilliseconds
        watch.Restart()
        let matching = StartupRegistration.matchingShortcuts folder target
        watch.Stop()
        check "all names matched" (matching.Length = 2 && List.contains renamed matching)
        check "other link preserved" (StartupRegistration.samePath other (StartupRegistration.readShortcut occupied))
        check "no duplicate requested" (not (StartupRegistration.plan true matching false).Create)
        for path in (StartupRegistration.plan false matching false).Delete do File.Delete(path)
        check "matching links removed" (StartupRegistration.matchingShortcuts folder target).IsEmpty
        check "unrelated link retained" (File.Exists(occupied))
        let mutable denied = false
        try StartupRegistration.createShortcut (Path.Combine(folder, "missing", "link.lnk")) target
        with _ -> denied <- true
        check "creation failure surfaced" denied
        printfn "Synthetic folder scan: %d ms" watch.ElapsedMilliseconds
    finally
        Directory.Delete(folder, true)
let mutable error: exn option = None
let thread = System.Threading.Thread(System.Threading.ThreadStart(fun () ->
    try testShortcuts() with ex -> error <- Some ex))
thread.SetApartmentState(System.Threading.ApartmentState.STA)
thread.Start()
thread.Join()
match error with Some ex -> raise ex | None -> ()
printfn "PASS: %d checks" passed
