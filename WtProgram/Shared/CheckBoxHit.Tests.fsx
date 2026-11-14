// TestRuntime: Framework
// Run with .NET Framework FSI --lib:WtProgram/bin/Debug.
// Load the executable as a library. Never show a form or start the application.
#r "System.Drawing"
#r "System.Windows.Forms"
#r "Aga.Controls.dll"
#r "Win32.dll"
#r "WindowTabs.exe"
open System
open System.Drawing
open System.Reflection
open System.Windows.Forms
open Bemo

let mutable checks = 0
let check name condition =
    if not condition then failwith name
    checks <- checks + 1

type Probe() =
    inherit SettingsCheckBox()
    let mutable downs = 0
    member this.Move(x, y) = base.OnMouseMove(MouseEventArgs(MouseButtons.None, 0, x, y, 0))
    member _.Downs = downs
    override this.OnMouseDown(e) =
        downs <- downs + 1
        base.OnMouseDown(e)
    member this.Mouse(message, x, y) =
        let mutable m = Message.Create(this.Handle, message, IntPtr(1), IntPtr((y <<< 16) ||| (x &&& 0xffff)))
        base.WndProc(&m)
    member this.Space() =
        base.OnKeyDown(KeyEventArgs(Keys.Space))
        base.OnKeyUp(KeyEventArgs(Keys.Space))

let contains = typeof<SettingsCheckBox>.GetMethod("ContainsContent", BindingFlags.NonPublic ||| BindingFlags.Instance)
let hit (c: SettingsCheckBox) x y = unbox<bool>(contains.Invoke(c, [|box (Point(x, y))|]))
// Compare against the actual .NET Framework Flat adapter used for painting.
// Reflection belongs only in this local regression test, never in the application.
let nativeCaption (c: CheckBox) (g: Graphics) =
    let flags = BindingFlags.NonPublic ||| BindingFlags.Public ||| BindingFlags.Instance
    let adapter = typeof<ButtonBase>.GetProperty("Adapter", flags).GetValue(c, null)
    use args = new PaintEventArgs(g, c.ClientRectangle)
    let options = adapter.GetType().GetMethod("Layout", flags).Invoke(adapter, [|box args|])
    let layout = options.GetType().GetMethod("Layout", flags).Invoke(options, [||])
    let bounds = unbox<Rectangle>(layout.GetType().GetField("textBounds", flags).GetValue(layout))
    if c.UseCompatibleTextRendering then Rectangle(bounds.X - 1, bounds.Y, bounds.Width + 1, bounds.Height)
    else bounds

let run () =
    Application.EnableVisualStyles()
    use form = new Form()
    use c = new Probe()
    form.Controls.Add(c)
    c.Size <- Size(320, 25)
    let original = c.Bounds
    for scale in [1.0; 1.25; 1.5; 1.75; 2.0; 2.5] do
        for dark in [false; true] do
            c.Text <- ""
            c.FlatStyle <- FlatStyle.Flat
            c.BackColor <- if dark then DarkMode.darkSurface else SystemColors.Control
            c.Padding <- Padding(int (Math.Round(13.0 * scale, MidpointRounding.AwayFromZero)) - 13, 0, 0, 0)
            c.Height <- int (25.0 * scale)
            let y = c.Height / 2
            use graphics = c.CreateGraphics()
            let width = 13 + c.Padding.Left + (if dark then 1 else 0)
            check "Glyph left" (hit c 0 y)
            check "Glyph right" (hit c (width - 1) y)
            check "Empty area beside glyph" (not (hit c width y))
            check "Empty cell" (not (hit c 200 y))
            check "Above glyph" (not (hit c 2 0))
            let before = c.Downs
            c.Mouse(0x0201, width + 10, y)
            c.Mouse(0x0202, width + 10, y)
            c.Mouse(0x0203, width + 10, y)
            c.Mouse(0x0202, width + 10, y)
            check "Empty click and double click suppressed" (c.Downs = before)
            c.Mouse(0x0201, 5, y)
            check "Glyph mouse down delivered" (c.Downs = before + 1)
            let previous = c.Checked
            c.Mouse(0x0202, width + 10, y)
            check "Drag release in empty area cancelled" (c.Checked = previous && not c.Capture)
            c.Space()
            check "Space toggles" (c.Checked <> previous)
            check "Tab stop retained" c.TabStop
            c.Text <- "value-1"
            let font = new Font(SystemFonts.DefaultFont.FontFamily, float32 (9.0 * scale))
            c.Font <- font
            c.Height <- int (60.0 * scale)
            let bounds = SettingsCheckBoxLayout.caption c graphics
            let x = bounds.Left + bounds.Width / 2
            let cy = bounds.Top + bounds.Height / 2
            check "Caption accepted" (hit c x cy)
            check "Above caption rejected" (not (hit c x (bounds.Top - 1)))
            check "Below caption rejected" (not (hit c x bounds.Bottom))
            check "Beyond caption rejected" (not (hit c bounds.Right cy))
            let beforeCaption = c.Downs
            c.Mouse(0x0201, x, cy)
            check "Caption mouse down delivered" (c.Downs = beforeCaption + 1)
            let previous = c.Checked
            c.Mouse(0x0202, x, bounds.Bottom)
            check "Release below caption cancelled" (c.Checked = previous && not c.Capture)
            let beforeBlank = c.Downs
            for bx, by in [x, bounds.Top - 1; x, bounds.Bottom; bounds.Right, cy] do
                c.Mouse(0x0201, bx, by)
                c.Mouse(0x0202, bx, by)
            check "Blank around caption suppressed" (c.Downs = beforeBlank)
            check "Negative coordinate rejected" (not (hit c -1 cy))
            check "Layout width retained" (c.Width = original.Width)
            c.Font <- SystemFonts.DefaultFont
            font.Dispose()
    let flags = BindingFlags.NonPublic ||| BindingFlags.Instance
    let nativeHot = typeof<ButtonBase>.GetProperty("MouseIsOver", flags)
    let hot () = unbox<bool>(nativeHot.GetValue(c, null))
    let mutable entered = 0
    let mutable left = 0
    c.MouseEnter.Add(fun _ -> entered <- entered + 1)
    c.MouseLeave.Add(fun _ -> left <- left + 1)
    for scale in [1.0; 1.5; 2.0] do
        for dark in [false; true] do
            c.BackColor <- if dark then DarkMode.darkSurface else SystemColors.Control
            c.Padding <- Padding(int (Math.Round(13.0 * scale, MidpointRounding.AwayFromZero)) - 13, 0, 0, 0)
            c.Size <- Size(320, int (80.0 * scale))
            use font = new Font(SystemFonts.DefaultFont.FontFamily, float32 (9.0 * scale))
            c.Font <- font
            for compatible in [true; false] do
                c.UseCompatibleTextRendering <- compatible
                for caption in ["value-1"; "value-1 value-2 value-3 value-4 value-5 value-6 value-7"; "&value-1"; ""] do
                    c.Text <- caption
                    c.Width <- if caption.Length > 30 then 110 else 320
                    use bitmap = new Bitmap(c.Width, c.Height)
                    bitmap.SetResolution(float32 (96.0 * scale), float32 (96.0 * scale))
                    use g = Graphics.FromImage(bitmap)
                    let actual = SettingsCheckBoxLayout.caption c g
                    if caption <> "" then
                        let expected =
                            if dark then
                                let x = 13 + c.Padding.Left + SettingsDpi.px 4
                                let area = Rectangle(x, 0, max 0 (c.Width - x), c.Height)
                                let flags = TextFormatFlags.Left ||| TextFormatFlags.VerticalCenter ||| TextFormatFlags.NoPadding
                                let size = TextRenderer.MeasureText(g, c.Text, c.Font, area.Size, flags)
                                Rectangle.Intersect(area, Rectangle(x, (c.Height - size.Height) / 2, size.Width, size.Height))
                            else nativeCaption c g
                        check (sprintf "Native caption at scale %g compatible %b: %A <> %A" scale compatible actual expected) (actual = expected)
                    else check "Empty caption rectangle" actual.IsEmpty
            c.UseCompatibleTextRendering <- true
            c.Text <- "value-1"
            c.Width <- 320
            use g = c.CreateGraphics()
            let bounds = SettingsCheckBoxLayout.caption c g
            let x = bounds.Left + bounds.Width / 2
            let y = bounds.Top + bounds.Height / 2
            c.Move(300, 0)
            check "Blank is not hot" (not (hot ()))
            c.Move(x, y)
            check "Caption is hot" (hot ())
            for bx, by in [x, bounds.Top - 1; x, bounds.Bottom; bounds.Right, y] do
                c.Move(bx, by)
                check "Blank around caption is not hot" (not (hot ()))
                c.Move(x, y)
            c.Move(2, c.Height / 2)
            check "Glyph is hot" (hot ())
            c.Move(300, 0)
            check "Leaving content clears hot" (not (hot ()))
            check "Light painter receives balanced hover events" (entered = left)
            c.Font <- SystemFonts.DefaultFont
    check "Hidden form" (not form.Visible)
    printfn "PASS: %d checks" checks
let mutable failure = false
let rec report (ex: exn) =
    eprintfn "%s: %s" (ex.GetType().FullName) ex.Message
    if not (isNull ex.InnerException) then report ex.InnerException
let thread = new Threading.Thread(Threading.ThreadStart(fun () -> try run() with ex -> failure <- true; report ex))
thread.SetApartmentState(Threading.ApartmentState.STA)
thread.Start()
thread.Join()

if failure then exit 1







