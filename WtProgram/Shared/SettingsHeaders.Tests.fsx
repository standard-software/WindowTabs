// TestRuntime: Framework
// Construct only tab headers off screen; do not enumerate windows or show forms.
#r "System.Drawing"
#r "System.Windows.Forms"
#r "Newtonsoft.Json.dll"
#r "FSharp.PowerPack.dll"
#r "Aga.Controls.dll"
#r "Win32.dll"
#r "WindowTabs.exe"
open System
open System.IO
open System.Drawing
open System.Windows.Forms
open Bemo
let check name condition = if not condition then failwith name
let source = File.ReadAllText(Path.Combine(__SOURCE_DIRECTORY__, "../ManagerViewService/DesktopManagerForm.fs"))
let pageTail = source.Substring(source.IndexOf("let tabs = List2(["))
let pageSource = pageTail.Substring(0,pageTail.IndexOf("])"))
let entries = pageSource.Split([|'\n'|]) |> Array.filter (fun l -> l.Contains(":> ISettingsView") && not(l.TrimStart().StartsWith("//")) || l.Trim() = "appearanceView.colorView")
check "eight production pages" (entries.Length = 8)
check "diagnostics removed" (not (pageSource.Contains("DiagnosticsView")))
let keys = ["Programs";"Appearance";"ColorPage";"Interaction";"General";"ShortcutKeys";"Workspace";"System"]
let output = Path.Combine(Path.GetTempPath(), "WindowTabs-headers-" + Guid.NewGuid().ToString("N"))
Directory.CreateDirectory(output) |> ignore
for language in Directory.GetFiles(Path.Combine(__SOURCE_DIRECTORY__, "../Settings/Language"), "*.json") do
 if Path.GetFileNameWithoutExtension(language) <> "FileList" then
  let json = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText language)
  for scale in [1.0;1.25;1.5;1.75] do
    SettingsDpi.setCurrent 1.0
    use form = new Form(AutoScaleMode = AutoScaleMode.None, Size = Size(800,600))
    let tabs = new System.Windows.Forms.TabControl(Dock = DockStyle.Fill)
    for key in keys do tabs.TabPages.Add(new TabPage(string json.[key]))
    form.Controls.Add tabs
    SettingsDpi.captureLayoutDesigns form
    SettingsDpi.applyScale form 1.0 scale
    form.Handle |> ignore
    tabs.Handle |> ignore
    SettingsDpi.reassertAfterShow form
    for index in 0..keys.Length - 1 do
        let rect = tabs.GetTabRect(index)
        check (sprintf "%s %g header %d fits: %A within %A" (Path.GetFileNameWithoutExtension language) scale index rect tabs.ClientSize) (rect.Left >= 0 && rect.Right <= tabs.ClientSize.Width && rect.Height > 0)
        if index > 0 then check "headers do not overlap" (tabs.GetTabRect(index-1).Right <= rect.Left)
    if Path.GetFileNameWithoutExtension(language).StartsWith("Japanese") || Path.GetFileNameWithoutExtension(language) = "English" then
        use bitmap = new Bitmap(tabs.Width, tabs.GetTabRect(0).Bottom + 2)
        tabs.DrawToBitmap(bitmap,Rectangle(0,0,bitmap.Width,bitmap.Height))
        bitmap.Save(Path.Combine(output,sprintf "%s-%d.png" (Path.GetFileNameWithoutExtension language) (int(scale*100.0))))
    printfn "PASS eight headers %s %g" (Path.GetFileNameWithoutExtension language) scale
