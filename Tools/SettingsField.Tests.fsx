// TestRuntime: Framework
// Run with the .NET Framework fsi.exe. No windows are shown.
// The existing assembly supplies SettingsDpi; the field code is loaded from source.
#r "../WtProgram/bin/Debug/Win32.dll"
#r "System.Drawing"
#r "System.Windows.Forms"
#r "../WtProgram/bin/Debug/WindowTabs.exe"
#load "../WtProgram/Shared/SettingsField.fs"

open System
open System.Drawing
open System.Windows.Forms
open Bemo

let mutable checks = 0
let check name condition =
    checks <- checks + 1
    if not condition then failwith name

let baseline = defaultArg (SettingsDpi.font 1.0) Control.DefaultFont
let baselineHeight =
    use font = new Font(baseline.FontFamily,
                        baseline.Size * (baseline.SizeInPoints + 1.0f) / baseline.SizeInPoints,
                        baseline.Style, baseline.Unit, baseline.GdiCharSet, baseline.GdiVerticalFont)
    use probe = new NumericUpDown(Font = font)
    probe.PreferredHeight
let baselineRatio = float baselineHeight / float SettingsField.heightPx

// Start on a scaled monitor so the reference cannot depend on first-use DPI.
do
    use panel = new Panel()
    use numeric = new NumericUpDown()
    panel.Controls.Add(numeric)
    for scale in [1.75; 1.0; 1.25; 1.5; 1.75; 2.0; 1.0] do
        SettingsDpi.setCurrent scale
        panel.Font <- defaultArg (SettingsDpi.font scale) Control.DefaultFont
        SettingsField.apply numeric
        panel.PerformLayout()
        let extra = numeric.Font.SizeInPoints - panel.Font.SizeInPoints
        let commonHeight = SettingsDpi.px SettingsField.heightPx
        check "Height ratio stays within one pixel of the 100% reference"
            (abs (float numeric.Height - baselineRatio * float commonHeight) <= 1.0)
        if scale = 1.0 then
            check "100% keeps the exact one-point enlargement" (abs (extra - 1.0f) < 0.001f)
            check "100% keeps the measured reference height" (numeric.Height = baselineHeight)
        printfn "Scale %.0f%%: numeric %d px, common %d px, extra %.2f pt"
            (scale * 100.0) numeric.Height commonHeight extra

for unit in [GraphicsUnit.Point; GraphicsUnit.Pixel] do
    use form = new Form()
    use panel = new Panel()
    use other = new Panel()
    use numeric = new NumericUpDown()
    form.Controls.Add(panel)
    form.Controls.Add(other)
    panel.Controls.Add(numeric)
    SettingsField.apply numeric
    let fonts = ResizeArray<Font>()
    let verify () =
        let inherited = numeric.Parent.Font
        check "Font unit follows parent" (numeric.Font.Unit = inherited.Unit)
        check "Font charset follows parent" (numeric.Font.GdiCharSet = inherited.GdiCharSet)
        check "Font style follows parent" (numeric.Font.Style = inherited.Style)
        check "Vertical font follows parent" (numeric.Font.GdiVerticalFont = inherited.GdiVerticalFont)
        let extra = numeric.Font.SizeInPoints - inherited.SizeInPoints
        check "Enlargement is bounded by eight points" (extra >= -0.001f && extra <= 8.001f)
        check "Enlargement uses quarter-point steps" (abs (extra * 4.0f - float32 (Math.Round(float (extra * 4.0f)))) < 0.001f)
        use probe = new NumericUpDown()
        probe.BorderStyle <- numeric.BorderStyle
        let target = baselineRatio * float (SettingsDpi.px SettingsField.heightPx)
        let selectedDistance = abs (float numeric.PreferredHeight - target)
        for step in 0 .. 32 do
            let points = float32 step * 0.25f
            use candidate = new Font(inherited.FontFamily,
                                     inherited.Size * (inherited.SizeInPoints + points) / inherited.SizeInPoints,
                                     inherited.Style, inherited.Unit, inherited.GdiCharSet, inherited.GdiVerticalFont)
            probe.Font <- candidate
            check "Selected height is nearest within the eight-point limit"
                (selectedDistance <= abs (float probe.PreferredHeight - target))
    for scale in [1.0; 1.25; 1.5; 1.75; 2.0; 1.0; 1.75; 1.25] do
        SettingsDpi.setCurrent scale
        let font = new Font("Segoe UI", float32 (9.0 * scale), FontStyle.Regular, unit, 128uy)
        fonts.Add(font)
        form.Font <- font
        verify ()
        panel.Scale(SizeF(1.2f, 1.2f))
        panel.PerformLayout()
        verify ()
        // Simulate an explicit font overwritten by a later scaling pass.
        use overwritten = new Font(font.FontFamily, font.Size * 1.5f, font.Style, unit, font.GdiCharSet)
        numeric.Font <- overwritten
        panel.PerformLayout()
        check "An overwritten font does not refit unchanged inputs"
            (Object.ReferenceEquals(overwritten, numeric.Font))
        for border in [BorderStyle.FixedSingle; BorderStyle.Fixed3D] do
            numeric.BorderStyle <- border
            SettingsField.reassert form
            verify ()
        let before = numeric.Font
        for _ in 1 .. 5 do
            panel.PerformLayout()
            SettingsField.reassert form
        check "Unchanged layouts retain the fitted font" (Object.ReferenceEquals(before, numeric.Font))
    // A changed target must be noticed even if the inherited font is unchanged.
    SettingsDpi.setCurrent 1.0
    panel.PerformLayout()
    verify ()
    let mutable changes = 0
    numeric.FontChanged.Add(fun _ -> changes <- changes + 1)
    SettingsField.apply numeric
    let once = changes
    for _ in 1 .. 5 do SettingsField.apply numeric
    changes <- 0
    SettingsField.apply numeric
    check "Repeated apply does not multiply handlers" (changes = once)
    use otherFont = new Font("Segoe UI", 14.0f, FontStyle.Bold, unit, 1uy)
    other.Font <- otherFont
    other.Controls.Add(numeric)
    verify ()
    changes <- 0
    use oldParentFont = new Font("Segoe UI", 18.0f, FontStyle.Italic, unit, 128uy)
    panel.Font <- oldParentFont
    panel.PerformLayout()
    check "Old parent is detached" (changes = 0)
    numeric.Dispose()
    other.Font <- form.Font
    form.Dispose()
    for font in fonts do font.Dispose()

// Count every fitFont event source after registration (no handle or parent
// changes in this loop). This also catches deferred parent layout feedback.
do
    use panel = new Panel()
    use numeric = new NumericUpDown()
    panel.Controls.Add(numeric)
    SettingsDpi.setCurrent 1.0
    SettingsField.apply numeric
    let mutable fitCalls = 0
    let mutable assignments = 0
    let countFit () =
        fitCalls <- fitCalls + 1
        if fitCalls > 2000 then failwith "Font fitting did not settle"
    panel.FontChanged.Add(fun _ -> countFit ())
    panel.Layout.Add(fun _ -> countFit ())
    numeric.FontChanged.Add(fun _ ->
        countFit ()
        assignments <- assignments + 1
        // Force synchronous layout while fitFont is assigning its result.
        panel.PerformLayout())
    let fonts = ResizeArray<Font>()
    for iteration in 0 .. 99 do
        let scale = [|1.0; 1.25; 1.5; 1.75|].[iteration % 4]
        SettingsDpi.setCurrent scale
        let font = new Font("Segoe UI", float32 (9.0 * scale))
        fonts.Add(font)
        let before = assignments
        panel.Font <- font
        panel.PerformLayout()
        check "Each changed input assigns at most one live font" (assignments - before <= 1)
        let settled = assignments
        for _ in 1 .. 3 do panel.PerformLayout()
        check "Repeated layouts do not assign any live font" (assignments = settled)
    check "Changed inputs exercised font assignment" (assignments > 50)
    check "Repeated font changes have bounded fitFont calls" (fitCalls <= 2000)
    printfn "100 parent font changes: %d fitFont event calls, %d live font assignments" fitCalls assignments
    numeric.Dispose()
    panel.Dispose()
    for font in fonts do font.Dispose()
    SettingsDpi.setCurrent 1.0

// A short native spinner stays vertically centred without a wrapper border.
use row = new TableLayoutPanel(ColumnCount = 2, RowCount = 1, Size = Size(400, 56))
row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 75.0f)) |> ignore
row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25.0f)) |> ignore
row.RowStyles.Add(new RowStyle(SizeType.Percent, 100.0f)) |> ignore
use field = new NumericUpDown(Anchor = (AnchorStyles.Left ||| AnchorStyles.Right), Margin = Padding(0, 7, 0, 7))
use reset = new Button(Dock = DockStyle.Fill, Margin = Padding(7))
SettingsField.apply field
row.Controls.Add(field, 0, 0)
row.Controls.Add(reset, 1, 0)
for height in [40; 48; 56; 40] do
    row.Height <- height
    row.PerformLayout()
    check "Native field and reset centres differ by at most half a pixel"
        (abs ((2 * field.Top + field.Height) - (2 * reset.Top + reset.Height)) <= 1)
SettingsDpi.setCurrent 1.0

// Hidden native EDIT controls exercise late formatting resets without desktop input.
[<System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)>]
type EditRect =
    struct
        val mutable left: int
        val mutable top: int
        val mutable right: int
        val mutable bottom: int
    end

[<System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)>]
extern nativeint SendMessageW(nativeint hwnd, uint32 message, nativeint wparam, nativeint lparam)

type CentredTextBox() =
    inherit TextBox()
    let mutable rectangleWrites = 0
    member this.RectangleWrites = rectangleWrites
    member this.RecreateForTest() = this.RecreateHandle()
    override this.WndProc(message: byref<Message>) =
        if message.Msg = 0xB3 then rectangleWrites <- rectangleWrites + 1
        base.WndProc(&message)

let editRect (box: TextBoxBase) =
    let memory = System.Runtime.InteropServices.Marshal.AllocHGlobal(16)
    try
        SendMessageW(box.Handle, 0xB2u, 0n, memory) |> ignore
        System.Runtime.InteropServices.Marshal.PtrToStructure(memory, typeof<EditRect>) :?> EditRect
    finally System.Runtime.InteropServices.Marshal.FreeHGlobal(memory)

for border in [BorderStyle.None; BorderStyle.FixedSingle; BorderStyle.Fixed3D] do
    use form = new Form(AutoScaleMode = AutoScaleMode.None)
    use field = new CentredTextBox(BorderStyle = border, Text = "D0D0D0")
    form.Controls.Add(field)
    SettingsField.apply field
    form.Handle |> ignore
    field.Handle |> ignore
    let fonts = ResizeArray<Font>()
    let mutable changeEvents = 0
    field.FontChanged.Add(fun _ -> changeEvents <- changeEvents + 1)
    field.SizeChanged.Add(fun _ -> changeEvents <- changeEvents + 1)
    let mutable scenario = "handle recreation"
    let verify () =
        Application.DoEvents()
        let expected = max 0 ((field.ClientSize.Height - TextRenderer.MeasureText("Wg", field.Font).Height) / 2)
        let actual = (editRect field).top
        check (sprintf "Native formatting rectangle: border=%A %s client=%A expected=%d actual=%d" border scenario field.ClientSize expected actual) (actual = expected)
        // Verify the actual first character position, not just rectangle readback.
        let position = SendMessageW(field.Handle, 0xD6u (* EM_POSFROMCHAR *), 0n, 0n).ToInt64()
        let characterTop = int (int16 ((position >>> 16) &&& 0xFFFFL))
        check (sprintf "Text origin: border=%A %s expected=%d actual=%d" border scenario expected characterTop)
            (characterTop = expected)
        check "The complete text line fits inside the client area"
            (characterTop >= 0 && characterTop + TextRenderer.MeasureText("Wg", field.Font).Height <= field.ClientSize.Height)
        let writes = field.RectangleWrites
        for _ in 1 .. 5 do
            field.PerformLayout()
            SettingsField.reassert form
            Application.DoEvents()
        check "Unchanged layout does not rewrite the native rectangle" (field.RectangleWrites = writes)
    for fontFirst in [true; false] do
        for scale in [1.0; 1.25; 1.5; 1.75; 1.5; 1.25; 1.0; 1.75; 1.0] do
            let eventsBefore = changeEvents
            scenario <- sprintf "scale=%.0f%% fontFirst=%b" (scale * 100.0) fontFirst
            let font = new Font("Segoe UI", float32 (9.0 * scale))
            fonts.Add(font)
            let resize () = field.Size <- Size(int (160.0 * scale), int (float SettingsField.heightPx * scale))
            if fontFirst then
                field.Font <- font
                resize ()
            else
                resize ()
                field.Font <- font
            // A repeated scale/font can produce no change events. Complete a
            // layout explicitly before simulating its late native reset.
            if changeEvents = eventsBefore then field.PerformLayout()
            // Simulate EDIT resetting formatting after managed events have run.
            SendMessageW(field.Handle, 0xB3u, 0n, 0n) |> ignore
            verify ()
            // DPI completion must also repair a reset with identical font/size.
            SendMessageW(field.Handle, 0xB3u, 0n, 0n) |> ignore
            SettingsField.reassert form
            verify ()
    // A pending callback from an old handle must not block the new handle.
    field.PerformLayout()
    scenario <- "handle recreation"
    field.RecreateForTest()
    verify ()
    field.Dispose()
    form.Dispose()
    for font in fonts do font.Dispose()

printfn "%d checks, 0 failures" checks
