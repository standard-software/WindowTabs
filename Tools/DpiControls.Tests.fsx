// Non-displaying regression checks; no real settings or desktop input.
#r "System.Drawing"
#r "System.Windows.Forms"
#r "../Newtonsoft.Json.dll"
#r "../WtProgram/bin/Debug/DpiChoiceValidation/WindowTabs.exe"
#r "../WtProgram/bin/Debug/DpiChoiceValidation/Win32.dll"
#r "../WtProgram/bin/Debug/DpiChoiceValidation/Aga.Controls.dll"
#r "../WtProgram/bin/Debug/DpiChoiceValidation/FSharp.PowerPack.dll"
open System
open System.Drawing
open System.Windows.Forms
open System.Reflection
open Bemo
open Newtonsoft.Json.Linq

let mutable count = 0
let check name condition =
    if not condition then failwith name
    count <- count + 1
    printfn "PASS %s" name
Application.EnableVisualStyles()
let root = JObject()
Services.register({ new ISettings with
    member _.getValue _ = null
    member _.setValue _ = failwith "Unexpected setting write"
    member _.notifyValue _ _ = ()
    member _.root with get() = root and set _ = failwith "Unexpected root write" }, false)
for dark in [false; true] do
    SettingsDpi.setCurrent 1.0
    use form = new DpiAwareDialog()
    form.ClientSize <- Size(500,300)
    let cb = new CheckBox(Text="Checkbox", AutoSize=true, Location=Point(20,20))
    let rb = new RadioButton(Text="Radio", AutoSize=true, Location=Point(20,70))
    form.Controls.Add cb
    form.Controls.Add rb
    form.InitializeDpi 1.75
    if dark then DarkMode.applyDarkColorsBeforeShow form else ScaledChoiceGlyph.applyLight form
    for scale in [1.75; 1.0; 1.5; 1.75; 1.0; 1.75] do
        form.ApplyDpi(scale, Rectangle(0,0,int(500.0*scale),int(300.0*scale)))
        SettingsDpi.reassertAfterShow form
        let expected = Dpi.px scale 13 - 13
        check (sprintf "glyph reservation survives restoration dark=%b scale=%.2f" dark scale)
            (cb.Padding.Left = expected && rb.Padding.Left = expected)
        check "glyph fits the checkbox and radio control heights"
            (cb.Height >= 13+expected && rb.Height >= 13+expected)
    check "offscreen control test never shows its form" (not form.Visible)
    use snapshot = new Bitmap(320, 130)
    use output = Graphics.FromImage snapshot
    output.Clear(form.BackColor)
    for index, control in [cb :> Control; rb :> Control] |> List.indexed do
        use part = new Bitmap(control.Width, control.Height)
        control.DrawToBitmap(part, Rectangle(Point.Empty, part.Size))
        output.DrawImageUnscaled(part, 10, 10 + index * 55)
    snapshot.Save(IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "unite", (if dark then "dpi-choice-dark.png" else "dpi-choice-light.png")))

SettingsDpi.setCurrent 1.0
let treeForm = new DpiAwareDialog()
let tree = new Aga.Controls.Tree.TreeViewAdv(RowHeight=24)
treeForm.Controls.Add tree
treeForm.InitializeDpi 1.75
SettingsDpi.setCurrent 1.0
check "tree glyph uses its own DPI, not another dialog's DPI" (SettingsDpi.checkBoxSizeFor tree = 23)

let headerField = typeof<Aga.Controls.Tree.TreeViewAdv>.GetField("_columnHeaderHeight", BindingFlags.Instance ||| BindingFlags.NonPublic)
for scale in [1.0; 1.5; 1.0; 1.75; 1.0] do
    treeForm.ApplyDpi(scale, Rectangle(0,0,500,300))
    SettingsDpi.reassertAfterShow treeForm
    let actual = headerField.GetValue(tree) :?> int
    check (sprintf "native header matches dark overlay at %.2f DPI scale" scale)
        (actual = SettingsDpi.treeHeaderHeight tree)
    use freshForm = new DpiAwareDialog()
    let freshTree = new Aga.Controls.Tree.TreeViewAdv(RowHeight=24)
    freshForm.Controls.Add freshTree
    freshForm.InitializeDpi scale
    check (sprintf "moved header matches fresh header at %.2f DPI scale" scale)
        (actual = (headerField.GetValue(freshTree) :?> int))

type MessageProbe() =
    inherit DpiAwareDialog()
    member this.DeliverDpi(dpi: int, bounds: Rectangle) =
        let pointer = Runtime.InteropServices.Marshal.AllocHGlobal(16)
        try
            Runtime.InteropServices.Marshal.Copy([|bounds.Left; bounds.Top; bounds.Right; bounds.Bottom|],0,pointer,4)
            let mutable message = Message.Create(this.Handle,0x02E0,IntPtr(dpi ||| (dpi <<< 16)),pointer)
            base.WndProc(&message)
        finally Runtime.InteropServices.Marshal.FreeHGlobal(pointer)
let moving = new MessageProbe()
let button = new Button(Size=Size(80,30))
moving.Controls.Add button
moving.InitializeDpi 1.0
let baseFontHeight = button.Font.Height
for dpi, width in [168,140; 96,80; 168,140; 96,80] do
    moving.DeliverDpi(dpi,Rectangle(0,0,600,400))
    check (sprintf "WM_DPICHANGED resizes button at %d DPI" dpi) (button.Width = width)
    check (sprintf "WM_DPICHANGED rescales font at %d DPI" dpi)
        (if dpi = 96 then button.Font.Height = baseFontHeight else button.Font.Height > baseFontHeight)
check "DPI message probe remains hidden" (not moving.Visible)

let table = UIHelper.formCompact(List2(["Name", new TextBox() :> Control]))
let workspace = UIHelper.okCancelForm table
workspace.Size <- Size(380,240)
workspace.InitializeDpi 1.75
let rec descendants (control: Control) = seq {
    yield control
    for child in control.Controls do yield! descendants child }
check "workspace wrappers do not enable scrollbars at 175 percent"
    (descendants workspace |> Seq.forall(function :? ScrollableControl as c -> not c.AutoScroll | _ -> true))

// Replace only this test process's localization dictionary to make the
// distinction observable without loading or writing a user's language files.
let dictionary = System.Collections.Generic.Dictionary<string,string>()
dictionary.Add("OK","Translated OK")
dictionary.Add("Cancel","Translated Cancel")
let loaded = typeof<AppDialog.Buttons>.Assembly.GetTypes()
             |> Array.collect(fun t -> t.GetFields(BindingFlags.Static ||| BindingFlags.NonPublic ||| BindingFlags.Public))
             |> Array.find(fun f -> f.Name.StartsWith("loadedStrings"))
loaded.SetValue(null, Some(dictionary :> System.Collections.Generic.IDictionary<string,string>))
let build = typeof<AppDialog.Buttons>.Assembly.GetType("Bemo.AppDialog").GetMethod("build",BindingFlags.Static ||| BindingFlags.NonPublic ||| BindingFlags.Public)
for english in [false; true] do
    use dialog = build.Invoke(null,[|box(None:IWin32Window option);box "Language Change";box "Message";box AppDialog.OkOnly;box AppDialog.DefaultOk;box english|]) :?> Form
    let ok = dialog.Controls |> Seq.cast<Control> |> Seq.pick(function :? Button as b -> Some b | _ -> None)
    check (sprintf "OK caption policy english=%b" english) (ok.Text = (if english then "OK" else "Translated OK"))
    check "confirmation is DPI-aware" (dialog :? DpiAwareDialog)
for radio in [false;true] do
    use bitmap = new Bitmap(40,40)
    use graphics = Graphics.FromImage bitmap
    ScaledChoiceGlyph.draw graphics (Rectangle(0,0,23,23)) radio 5
    let columns = [for x in 0..39 do if [0..39] |> List.exists(fun y -> bitmap.GetPixel(x,y).A > 0uy) then yield x]
    check (sprintf "themed glyph pixels scale to 175 percent radio=%b" radio) (List.max columns - List.min columns + 1 >= 21)
workspace.Dispose()
moving.Dispose()
treeForm.Dispose()
printfn "%d checks passed" count
