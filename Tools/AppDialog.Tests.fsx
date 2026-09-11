// Run with Visual Studio's .NET Framework fsi.exe after a normal Debug build.
// See SettingsTests.md. No windows are shown.
#r "System.Windows.Forms"
#r "../WtProgram/bin/Debug/WindowTabs.exe"

open System
open System.Windows.Forms
open Bemo

let mutable count = 0
let check name condition =
    if not condition then failwith name
    count <- count + 1
    printfn "PASS %s" name

let gate = DialogGate()
let mutable releases = 0
gate.Available.Add(fun () -> releases <- releases + 1)
check "initially available" (not gate.IsBusy)
let first = gate.TryAcquire().Value
check "first dialog blocks entry points" gate.IsBusy
check "second request is rejected" (gate.TryAcquire().IsNone)
check "rejected request preserves the existing session" gate.IsBusy
first.Dispose()
check "closing the dialog restores entry points" (not gate.IsBusy)
let second = gate.TryAcquire().Value
first.Dispose()
check "late cleanup cannot release a newer session" gate.IsBusy
check "late cleanup does not trigger notification draining" (releases = 1)
second.Dispose()
check "new session closes normally" (not gate.IsBusy)

try
    use lifetime = gate.TryAcquire().Value
    failwith "Simulated construction failure"
with _ -> ()
check "construction failure releases session" (not gate.IsBusy)

let session = DialogState.tryAcquire().Value
let pendingField =
    typeof<AppDialog.Buttons>.Assembly.GetTypes()
    |> Array.collect (fun t -> t.GetFields(System.Reflection.BindingFlags.Static ||| System.Reflection.BindingFlags.NonPublic ||| System.Reflection.BindingFlags.Public))
    |> Array.find (fun f -> f.Name.StartsWith("pending") && f.FieldType = typeof<System.Collections.Generic.Queue<string * string>>)
let pending = pendingField.GetValue(null) :?> System.Collections.Generic.Queue<string * string>
for tag in ["Settings"; "Language"; "CheckForUpdates"; "RestartWindowTabs"; "CloseWindowTabs"] do
    check (tag + " is disabled during a dialog") (not (DialogState.canUseTrayCommand tag false))
check "Disable is disabled during a dialog" (not (DialogState.canUseTrayCommand "Disable" false))
check "Enable is disabled during a dialog" (not (DialogState.canUseTrayCommand "Disable" true))
check "tab menu Settings is disabled during a dialog" (not (DialogState.canOpenSettings false))
let blocked = AppDialog.confirm "Test" "Must not open or dismiss any window"
check "blocked confirmation never returns OK" (not blocked)
check "blocked confirmation preserves current session" (DialogState.isBusy())
AppDialog.info "Test" "Must not close the settings dialog"
check "blocked notification preserves current session" (DialogState.isBusy())
check "blocked notification is retained" (pending.Count = 1)
session.Dispose()
check "notification survives until UI dispatcher is available" (pending.Count = 1)
check "notification retains its message" (pending.Peek() = ("Test", "Must not close the settings dialog"))
pending.Clear()
check "shared gate becomes available again" (not (DialogState.isBusy()))
check "Disable becomes available after closing" (DialogState.canUseTrayCommand "Disable" false)
check "Enable becomes available after closing" (DialogState.canUseTrayCommand "Disable" true)
check "Settings is enabled after closing" (DialogState.canOpenSettings false)
check "Settings respects application disabled state" (not (DialogState.canOpenSettings true))

let completed = System.Threading.Tasks.Task.FromResult("release data")
check "completed update response is returned" (UpdateChecker.waitForResponse 20 completed = "release data")
let stalled = System.Threading.Tasks.TaskCompletionSource<string>()
let timedOut =
    try
        UpdateChecker.waitForResponse 20 stalled.Task |> ignore
        false
    with :? TimeoutException -> true
check "stalled update response times out" timedOut

printfn "All %d dialog gate tests passed." count
