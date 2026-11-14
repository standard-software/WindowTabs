#r "../../Newtonsoft.Json.dll"
#load "../Localization/JsoncHelper.fs"
#load "../Localization/Localization_English.fs"
#load "PortableSettings.fs"
open System
open System.IO
open Bemo

let mutable checks = 0
let check label condition =
    checks <- checks + 1
    if not condition then failwith label
let fails label action =
    let mutable failed = false
    try action() with _ -> failed <- true
    check label failed

for a, e, expected in [false,false,"A"; true,false,"A"; false,true,"E"; true,true,"A"] do
    check "Existence priority" (PortableSettings.choosePath "A" "E" a e = expected)

let root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
Directory.CreateDirectory(root) |> ignore
try
    let a = Path.Combine(root, "A", "WindowTabsSettings.json")
    let e = Path.Combine(root, "E", "WindowTabsSettings.json")
    Directory.CreateDirectory(Path.GetDirectoryName(a)) |> ignore
    Directory.CreateDirectory(Path.GetDirectoryName(e)) |> ignore
    let transfer = PortableSettings.transferWithPaths a e
    let legacy = PortableSettings.legacyPath a
    let bytes = System.Text.Encoding.UTF8.GetBytes("{\"Version\":\"test\",\"Value\":42}\r\n")
    for ae, ee, le, expected in [false,false,true,true; false,false,false,false; true,false,true,false; false,true,true,false; true,true,true,false] do
        check "Migration decision" (PortableSettings.shouldMigrate ae ee le = expected)
    File.WriteAllBytes(legacy, bytes)
    check "Initial migration succeeds" (not (PortableSettings.migrate a e))
    check "Migration copies exact bytes" (File.ReadAllBytes(a) = bytes)
    check "Legacy source preserved" (File.ReadAllBytes(legacy) = bytes)
    File.Delete(a)
    check "Deletion before first save recopies TXT" (not (PortableSettings.migrate a e) && File.ReadAllBytes(a) = bytes)
    File.WriteAllBytes(a, bytes)
    let updated = System.Text.Encoding.UTF8.GetBytes("{\"Version\":\"test\",\"Value\":43}")
    File.WriteAllBytes(a, updated)
    do
        use locked = new FileStream(legacy, FileMode.Open, FileAccess.Read, FileShare.None)
        check "Second startup does not read legacy" (not (PortableSettings.migrate a e))
        check "Second startup keeps JSON" (File.ReadAllBytes(a) = updated)
        transfer a e true false
        check "Portable startup does not migrate legacy" (not (PortableSettings.migrate a e) && not (File.Exists(a)))
    let otherExe = Path.Combine(root, "E2", "WindowTabsSettings.json")
    check "Different executable recopies TXT after move" (not (PortableSettings.migrate a otherExe) && File.ReadAllBytes(a) = bytes)
    check "Portable JSON remains unchanged after recopy" (File.ReadAllBytes(e) = updated)
    File.Delete(a)
    check "Returned portable JSON still selected" (PortableSettings.choosePath a e (File.Exists(a)) (File.Exists(e)) = e && File.ReadAllBytes(e) = updated)
    check "JSON operations leave legacy unchanged" (File.ReadAllBytes(legacy) = bytes)
    let portableBackup = PortableSettings.backupPath a e e
    check "Portable backups use APPDATA directory" (Path.GetDirectoryName(portableBackup) = Path.GetDirectoryName(a))
    check "APPDATA backup name retained" (PortableSettings.backupPath a e a = a)
    check "Portable backups separated" (portableBackup <> a)
    check "Portable installations separated" (portableBackup <> PortableSettings.backupPath a (Path.Combine(root, "E2", "WindowTabsSettings.json")) (Path.Combine(root, "E2", "WindowTabsSettings.json")))
    File.Copy(e, portableBackup + ".bak.test")
    check "Portable backup discovery" (PortableSettings.backups portableBackup = [|portableBackup + ".bak.test"|])
    check "APPDATA discovery excludes portable backups" (PortableSettings.backups a |> Array.isEmpty)
    let corrupt = PortableSettings.retainedPath a e e "corrupt" "test"
    check "Corrupt path uses portable backup identity" (corrupt = portableBackup + ".corrupt.test")
    let staging = e + ".tmp"
    File.WriteAllBytes(staging, bytes)
    check "Portable recovery preserves corrupt file" (PortableSettings.replaceRecovered staging e corrupt)
    check "Corrupt bytes retained" (File.ReadAllBytes(corrupt) = updated)
    check "Recovery publishes replacement" (File.ReadAllBytes(e) = bytes)
    check "Only JSON remains beside executable" (Directory.GetFiles(Path.GetDirectoryName(e)) = [|e|])
    File.WriteAllBytes(staging, updated)
    let blocked = Path.Combine(root, "blocked")
    File.WriteAllText(blocked, "x")
    check "Preservation failure still recovers" (not (PortableSettings.replaceRecovered staging e (Path.Combine(blocked, "kept"))))
    check "Recovery survives preservation failure" (File.ReadAllBytes(e) = updated)
    fails "Portable archive failure" (fun () -> PortableSettings.transferWithPaths (Path.Combine(blocked, "WindowTabsSettings.json")) e e a true false)
    check "Archive failure preserves source and rolls back publication" (File.ReadAllBytes(e) = updated && not (File.Exists(a)))
    check "Archive failure leaves only JSON beside executable" (Directory.GetFiles(Path.GetDirectoryName(e)) = [|e|])
    check "Portable rollback copy belongs in APPDATA" (PortableSettings.retainedPath a e e "previous" "test" = portableBackup + ".previous.test")
    check "APPDATA moved naming unchanged" (PortableSettings.retainedPath a e a "moved" "test" = a + ".moved.test")
    transfer e a true false
    check "Return leaves executable directory empty" (Directory.GetFiles(Path.GetDirectoryName(e)).Length = 0)
    let moved = Directory.GetFiles(Path.GetDirectoryName(a), Path.GetFileName(portableBackup) + ".moved.*")
    check "Return retains verified bytes in APPDATA" (moved.Length = 1 && File.ReadAllBytes(moved.[0]) = updated)
    File.Delete(a)
    File.Delete(moved.[0])
    File.WriteAllBytes(e, updated)
    do
        use locked = new FileStream(e, FileMode.Open, FileAccess.Read, FileShare.Read)
        fails "Portable deletion failure" (fun () -> transfer e a true false)
        check "Deletion failure retains source and rolls back destination" (File.ReadAllBytes(e) = updated && not (File.Exists(a)))
        check "Failed return leaves only JSON beside executable" (Directory.GetFiles(Path.GetDirectoryName(e)) = [|e|])
    for kept in Directory.GetFiles(Path.GetDirectoryName(a), Path.GetFileName(portableBackup) + ".moved.*") do File.Delete(kept)
    File.Delete(e)
    let fresh = Path.Combine(root, "fresh", "WindowTabsSettings.json")
    Directory.CreateDirectory(Path.GetDirectoryName(fresh)) |> ignore
    File.WriteAllBytes(PortableSettings.legacyPath fresh, bytes)
    do
        use locked = new FileStream(PortableSettings.legacyPath fresh, FileMode.Open, FileAccess.Read, FileShare.None)
        check "Migration failure reported" (PortableSettings.migrate fresh e)
        check "Failed migration does not publish empty JSON" (not (File.Exists(fresh)))
    check "Migration retries after failure" (not (PortableSettings.migrate fresh e) && File.ReadAllBytes(fresh) = bytes)
    File.WriteAllBytes(fresh, updated)
    File.Copy(fresh, fresh + ".bak.test")
    File.Delete(fresh)
    check "Deleted JSON recopies TXT even after backup" (not (PortableSettings.migrate fresh e) && File.ReadAllBytes(fresh) = bytes)
    check "Recopy preserves existing backup" (File.ReadAllBytes(fresh + ".bak.test") = updated)
    let local = Path.Combine(root, "local", "WindowTabsSettings.json")
    File.WriteAllBytes(e, updated)
    check "Portable launch without APPDATA succeeds" (not (PortableSettings.migrate local e))
    check "Portable launch creates no APPDATA directory" (not (Directory.Exists(Path.GetDirectoryName(local))))
    Directory.CreateDirectory(Path.GetDirectoryName(local)) |> ignore
    File.WriteAllBytes(PortableSettings.legacyPath local, bytes)
    check "Foreign portable launch skips local TXT" (not (PortableSettings.migrate local e) && not (File.Exists(local)))
    File.WriteAllBytes(PortableSettings.backupPath local e e + ".bak.test", updated)
    check "Regular executable later migrates local TXT" (not (PortableSettings.migrate local otherExe) && File.ReadAllBytes(local) = bytes)
    check "Foreign portable JSON unchanged" (File.ReadAllBytes(e) = updated)
    File.Delete(e)

    check "Failure preserves legacy bytes" (File.ReadAllBytes(legacy) = bytes)
    File.WriteAllText(PortableSettings.legacyPath e, "unused")
    File.Delete(legacy)
    check "Legacy portable file ignored" (not (PortableSettings.migrate a e) && not (File.Exists(a)) && not (File.Exists(e)))
    let choose() = PortableSettings.choosePath a e (File.Exists(a)) (File.Exists(e))
    let bytes = System.Text.Encoding.UTF8.GetBytes("{\"Version\":\"test\",\"Unknown\":42}\r\n")
    File.WriteAllBytes(a, bytes)
    File.WriteAllText(e, "valid fallback")
    File.WriteAllText(a, "invalid JSON")
    check "Corrupt A still wins" (choose() = a)
    File.WriteAllBytes(a, bytes)
    transfer a e false true
    check "Copy preserves bytes and source" (File.ReadAllBytes(e) = bytes && File.ReadAllBytes(a) = bytes && choose() = a)
    transfer a e true true
    check "Move switches to E" (choose() = e && not (File.Exists(a)))
    check "Moved copy retained" (Directory.GetFiles(Path.GetDirectoryName(a), "*.moved.*").Length = 2)
    check "Moved copies excluded from backups" (PortableSettings.backups a |> Array.isEmpty)
    File.SetAttributes(e, FileAttributes.ReadOnly)
    check "Read-only E is unavailable" (not (PortableSettings.canWrite e))
    check "Read-only E never falls back" (choose() = e && not (File.Exists(a)))
    File.SetAttributes(e, FileAttributes.Normal)
    transfer e a true false
    check "Return switches to A" (choose() = a && not (File.Exists(e)))
    File.WriteAllText(e, "destination retained")
    do
        use locked = new FileStream(e, FileMode.Open, FileAccess.Read, FileShare.None)
        fails "Move destination failure" (fun () -> transfer a e true true)
        check "Failed move retains source" (File.ReadAllBytes(a) = bytes)
    do
        use locked = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.None)
        fails "Return destination failure" (fun () -> transfer e a true true)
        check "Failed return retains source" (File.ReadAllText(e) = "destination retained")
    do
        use locked = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.Read)
        fails "Source rename failure" (fun () -> transfer a e true true)
        check "Rename failure restores destination" (File.ReadAllText(e) = "destination retained")
        check "Rename failure retains source" (File.Exists(a) && File.ReadAllBytes(a) = bytes)
    do
        File.Delete(e)
        use locked = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.Read)
        fails "New destination rollback" (fun () -> transfer a e true false)
        check "New destination removed after rename failure" (not (File.Exists(e)))
    File.WriteAllText(e, "destination retained")
    File.SetAttributes(e, FileAttributes.ReadOnly)
    fails "Read-only destination refuses move" (fun () -> transfer a e true true)
    check "Read-only failure keeps A" (File.ReadAllBytes(a) = bytes)
    File.SetAttributes(e, FileAttributes.Normal)
    File.WriteAllText(e, "older portable settings")
    let originalA = File.ReadAllBytes(a)
    let originalE = File.ReadAllBytes(e)
    fails "Return must not replace live A" (fun () -> transfer e a true false)
    check "Return refusal retains both files" (File.ReadAllBytes(a) = originalA && File.ReadAllBytes(e) = originalE)
    fails "Identical destinations refused" (fun () -> transfer a a true true)
    check "Same path retains source" (File.Exists(a))
    let returnA, returnE = Path.Combine(root, "return-A.txt"), Path.Combine(root, "return-E.txt")
    File.WriteAllBytes(returnE, bytes)
    do
        use locked = new FileStream(returnE, FileMode.Open, FileAccess.Read, FileShare.Read)
        fails "Return source rename failure" (fun () -> transfer returnE returnA true false)
        check "Failed return keeps E active" (PortableSettings.choosePath returnA returnE (File.Exists(returnA)) (File.Exists(returnE)) = returnE)
        check "Failed return keeps original bytes" (File.ReadAllBytes(returnE) = bytes)
    File.WriteAllText(a + ".bak.test", "backup")
    check "Only backup pattern selected" ((PortableSettings.backups a).Length = 1)
    check "Rollback copies cleaned after handled failures" (Directory.GetFiles(root, "*.previous.*", SearchOption.AllDirectories).Length = 0)
    check "Staging files cleaned" (Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories).Length = 0)
    let mutable notifications = 0
    PortableSettings.notifyMigrationFailure()
    let mutable notificationKey = ""
    PortableSettings.writeFailed.Publish.Add(fun key -> notifications <- notifications + 1; notificationKey <- key)
    PortableSettings.notifyMigrationFailure()
    check "Warning waits for tray" (notifications = 0)
    PortableSettings.writeNotificationReady()
    PortableSettings.writeNotificationReady()
    PortableSettings.notifyWriteFailure()
    check "Migration warning uses dedicated message" (notificationKey = "SettingsMigrationFailed")
    check "Write warning once per process" (notifications = 1)
    printfn "PASS: portable selection, copy, move, return, failure preservation, backup filtering, and warning tests."
finally
    let resolved = Path.GetFullPath(root)
    check "Cleanup stays in temporary directory" (resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
    Directory.Delete(resolved, true)

let keys = Localization_English.strings.Keys |> Seq.filter (fun key -> key = "System" || key = "SettingsMigrationFailed" || key.StartsWith("Portable")) |> Seq.toArray
let languageDir = Path.Combine(__SOURCE_DIRECTORY__, "..", "Settings", "Language")
let mutable languages = 0
for file in Directory.GetFiles(languageDir, "*.json") do
    let parsed = Newtonsoft.Json.Linq.JToken.Parse(removeJsoncComments(File.ReadAllText(file)))
    if Path.GetFileName(file) <> "FileList.json" then
        languages <- languages + 1
        for key in keys do
            check "Translated key exists" (not (isNull parsed.[key]) && parsed.[key].ToString().Length > 0)
check "All shipped languages checked" (languages = 16 && keys.Length = 16)
for json, expected in ["{\"value\":\"https://example.invalid/a\"}", "https://example.invalid/a"; "{ /* comment */ \"value\":\"/* literal */\"}", "/* literal */"] do
    let parsed = Newtonsoft.Json.Linq.JObject.Parse(removeJsoncComments(json))
    check "JSONC preserves string contents" (parsed.["value"].ToString() = expected)
fails "Invalid JSON rejected" (fun () -> Newtonsoft.Json.Linq.JObject.Parse(removeJsoncComments("{invalid")) |> ignore)
printfn "PASS: all 16 language files, 16 keys, English fallback, and JSONC regression cases."

printfn "PASS: %d assertions." checks
