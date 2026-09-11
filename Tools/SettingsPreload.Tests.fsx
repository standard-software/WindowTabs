// Run with .NET Framework fsi.exe. This probe never calls Show or Activate.
#r "System.Drawing"
#r "System.Windows.Forms"
#r "../WtProgram/bin/Debug/WindowTabs.exe"
#r "../WtProgram/bin/Debug/Win32.dll"
open System
open System.Drawing
open System.Windows.Forms
open System.Runtime.InteropServices
open Bemo

module Native =
    [<DllImport("user32.dll")>]
    extern IntPtr GetForegroundWindow()

let check message condition =
    if not condition then failwith message
    printfn "PASS %s" message

let foreground = Native.GetForegroundWindow()
let gateBefore = DialogState.isBusy()
let run() =
    use window = new SettingsDialogWindow(1.0)
    window.Size <- Size(800,600)
    let panel = new TableLayoutPanel(Dock=DockStyle.Fill, RowCount=1, ColumnCount=1)
    panel.Controls.Add(new Label(Text="Settings preload probe", AutoSize=true))
    window.Controls.Add(panel)
    SettingsDpi.captureLayoutDesigns window
    SettingsDpi.applyScale window 1.0 1.0
    let rec createHandles (control: Control) =
        control.Handle |> ignore
        for child in control.Controls do createHandles child
    createHandles window
    check "native handles exist without showing the window" (window.IsHandleCreated && not window.Visible)
    window.prepareAtCursor()
    check "cursor-monitor preparation does not show the window" (not window.Visible)
    check "hidden preparation leaves the dialog gate unchanged" (DialogState.isBusy() = gateBefore)
    check "hidden preparation does not take foreground focus" (Native.GetForegroundWindow() = foreground)
run()
check "disposing hidden window leaves gate unchanged" (DialogState.isBusy() = gateBefore)
printfn "These checks do not replace full cached-dialog lifecycle or physical mixed-DPI testing."
