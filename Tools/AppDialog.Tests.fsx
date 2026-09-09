// Run with Visual Studio's .NET Framework fsi.exe after building WtProgram
// with /p:OutputPath=bin\Debug\DialogValidation\. No windows are shown.
#r "System.Windows.Forms"
#r "../WtProgram/bin/Debug/DialogValidation/WindowTabs.exe"

open System
open System.Windows.Forms
open Bemo

let mutable count = 0
let check name condition =
    if not condition then failwith name
    count <- count + 1
    printfn "PASS %s" name

let gate = DialogGate()
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
second.Dispose()
check "new session closes normally" (not gate.IsBusy)

try
    use lifetime = gate.TryAcquire().Value
    failwith "Simulated construction failure"
with _ -> ()
check "construction failure releases session" (not gate.IsBusy)

let session = DialogState.tryAcquire().Value
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
session.Dispose()
check "shared gate becomes available again" (not (DialogState.isBusy()))
check "Disable becomes available after closing" (DialogState.canUseTrayCommand "Disable" false)
check "Enable becomes available after closing" (DialogState.canUseTrayCommand "Disable" true)
check "Settings is enabled after closing" (DialogState.canOpenSettings false)
check "Settings respects application disabled state" (not (DialogState.canOpenSettings true))

printfn "All %d dialog gate tests passed." count
