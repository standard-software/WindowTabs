namespace Bemo
open System
open System.Drawing
open System.Windows.Forms

/// One height for every field of the settings dialogs - drop-downs, number
/// fields, colour fields - so that a field lines up with the button beside it
/// and with the fields of the other tabs. A 96-dpi design number, like the
/// rest of the layout: Control.Scale multiplies it on a scaled monitor.
module SettingsField =
    open System.Runtime.InteropServices

    [<StructLayout(LayoutKind.Sequential)>]
    type private RECT =
        struct
            val mutable left: int
            val mutable top: int
            val mutable right: int
            val mutable bottom: int
        end

    [<DllImport("user32.dll", CharSet = CharSet.Unicode)>]
    extern nativeint private SendMessageW(nativeint hwnd, uint32 message, nativeint wparam, nativeint lparam)

    /// Controls that carry this as their Tag draw no border of their own: they
    /// sit in a box that has one, and two would show as a double line. Dark
    /// mode, which puts a border on every field, reads it too.
    let noBorderTag = "SettingsFieldNoBorder"

    /// The boxes whose text has been centred, and how to centre it again.
    /// A theme or a change of scale puts the drawing rectangle back, so it
    /// is asked for once more when the dialog has finished building itself.
    let private centred = Runtime.CompilerServices.ConditionalWeakTable<Control, (unit -> unit)>()

    /// Settings rows: every tab lays its rows out on this vertical grid
    /// (design pixels at 96 DPI). A row is its content height plus the same
    /// top and bottom margin on every caption and input.
    let rowHeightPx = 32
    let rowMarginPx = 4

    /// One item plus the border of a drop-down list: what the colour theme
    /// drop-down has always had, and the height every field is given.
    let comboItemHeightPx = 20
    let heightPx = comboItemHeightPx + 6

    // Measure the actual 100% settings font once, rather than assuming font
    // metrics for a particular Windows language. The probe is never shown;
    // its +1pt PreferredHeight defines a constant ratio at every scale.
    let private numericHeightRatio = lazy (
        let baseline = defaultArg (SettingsDpi.font 1.0) Control.DefaultFont
        use font = new Font(baseline.FontFamily,
                            baseline.Size * (baseline.SizeInPoints + 1.0f) / baseline.SizeInPoints,
                            baseline.Style, baseline.Unit, baseline.GdiCharSet, baseline.GdiVerticalFont)
        use probe = new NumericUpDown(Font = font)
        float probe.PreferredHeight / float heightPx)

    /// A text box draws its text against the top of the box, so a box made
    /// taller than its font shows the text high up. EM_SETRECT moves the
    /// drawing rectangle down by half of what is left over - and a single-line
    /// edit control ignores that message, so the box is made multiline first.
    /// It still holds one line: it accepts no return and does not wrap.
    let private centreText (box: TextBoxBase) =
        box.Multiline <- true
        box.WordWrap <- false
        match box with
        | :? TextBox as tb -> tb.AcceptsReturn <- false
        | _ -> ()
        let mutable pending = false
        let mutable generation = 0
        let mutable applied = None
        let coordinates (rect: RECT) = rect.left, rect.top, rect.right, rect.bottom
        let centre () =
            if not box.IsDisposed && box.IsHandleCreated then
                let line = TextRenderer.MeasureText("Wg", box.Font).Height
                let inset = max 0 ((box.ClientSize.Height - line) / 2 - 1)
                let mutable rect = RECT()
                // Clear of the border: drawing from the very edge left no room
                // for the left line of the box.
                // WS_BORDER edits erase the font-dependent left margin next
                // to the formatting rectangle. Keep that erase inside the frame.
                let leftMargin = int (SendMessageW(box.Handle, 0xD4u (* EM_GETMARGINS *), 0n, 0n)) &&& 0xFFFF
                rect.left <- if box.BorderStyle = BorderStyle.FixedSingle then max 3 (leftMargin + 1) else 3
                rect.top <- inset
                rect.right <- box.ClientSize.Width - 1
                rect.bottom <- box.ClientSize.Height
                let size = Marshal.SizeOf(typeof<RECT>)
                let memory = Marshal.AllocHGlobal(size)
                try
                    let read () =
                        SendMessageW(box.Handle, 0xB2u (* EM_GETRECT *), 0n, memory) |> ignore
                        Marshal.PtrToStructure(memory, typeof<RECT>) :?> RECT |> coordinates
                    let desired = coordinates rect
                    let actual = read ()
                    // Remember the native result too: EDIT may round the rectangle.
                    // A reset by Windows is detected even when our inputs are unchanged.
                    if applied <> Some(desired, actual) then
                        Marshal.StructureToPtr(rect, memory, false)
                        SendMessageW(box.Handle, 0xB3u (* EM_SETRECT *), 0n, memory) |> ignore
                        let measured = read ()
                        let _, actualTop, _, _ = measured
                        // Native EDIT adds its border to EM_SETRECT coordinates.
                        // Compensate from the readback rather than assuming a
                        // fixed border width at every DPI and theme.
                        if actualTop <> inset then
                            rect.top <- rect.top + inset - actualTop
                            Marshal.StructureToPtr(rect, memory, false)
                            SendMessageW(box.Handle, 0xB3u (* EM_SETRECT *), 0n, memory) |> ignore
                        applied <- Some(desired, read ())
                finally Marshal.FreeHGlobal(memory)
        let queueCentre () =
            if not pending && not box.IsDisposed && box.IsHandleCreated then
                pending <- true
                let queuedGeneration = generation
                try
                    box.BeginInvoke(MethodInvoker(fun () ->
                        if generation = queuedGeneration then
                            pending <- false
                            centre ())) |> ignore
                with :? InvalidOperationException -> pending <- false
        let refresh () =
            centre ()
            // FontChanged/SizeChanged can precede the native EDIT reset.
            // Coalesce a second pass after font assignment and layout complete.
            queueCentre ()
        centred.Remove(box) |> ignore
        centred.Add(box, refresh)
        box.HandleDestroyed.Add(fun _ ->
            generation <- generation + 1
            pending <- false
            applied <- None)
        box.HandleCreated.Add(fun _ -> refresh ())
        box.SizeChanged.Add(fun _ -> refresh ())
        box.FontChanged.Add(fun _ -> refresh ())
        box.Layout.Add(fun _ -> queueCentre ())
        refresh ()

    /// A drop-down list takes its closed height from the item height, and it
    /// listens to that only when it draws its own items: left to the system,
    /// the box is exactly as tall as the font makes it. What is drawn here is
    /// what a plain list draws anyway - the colours come from the event, so
    /// selection and dark mode are unchanged.
    let private ownerDraw (combo: ComboBox) =
        combo.DrawMode <- DrawMode.OwnerDrawFixed
        combo.DrawItem.Add <| fun e ->
            e.DrawBackground()
            if e.Index >= 0 && e.Index < combo.Items.Count then
                let text = combo.GetItemText(combo.Items.[e.Index])
                let bounds = Rectangle(e.Bounds.X + 1, e.Bounds.Y, e.Bounds.Width - 2, e.Bounds.Height)
                TextRenderer.DrawText(e.Graphics, text, e.Font, bounds, e.ForeColor,
                                      TextFormatFlags.Left ||| TextFormatFlags.VerticalCenter |||
                                      TextFormatFlags.NoPrefix ||| TextFormatFlags.EndEllipsis)
            e.DrawFocusRectangle()

    // Match the dark spinner geometry without changing its paint path.
    let private drawNumericArrow (graphics: Graphics) (rect: Rectangle) down color =
        let height = max 2 (SettingsDpi.px 5)
        let width = min (SettingsDpi.px 4) ((rect.Width - 3) / 2)
        let top = rect.Top + (rect.Height - height) / 2
        let center = rect.Left + (rect.Width - 1) / 2
        use brush = new SolidBrush(color)
        for row in 0 .. height - 1 do
            let step = if down then height - 1 - row else row
            let halfWidth = width * step / (height - 1)
            graphics.FillRectangle(brush, center - halfWidth, top + row, 2 * halfWidth + 1, 1)

    // UpDownButtons forwards mouse events to its parent without raising its
    // own events. Track native messages before its normal input handling.
    type private NumericButtons(numeric: NumericUpDown, spin: Control) as this =
        inherit NativeWindow()
        let mutable hot = -1
        let mutable pressed = -1
        let halfAt y = if y < spin.Height / 2 then 0 else 1
        let update nextHot nextPressed =
            if hot <> nextHot || pressed <> nextPressed then
                hot <- nextHot
                pressed <- nextPressed
                spin.Invalidate()
        let attach () =
            try
                if spin.IsHandleCreated && this.Handle = IntPtr.Zero then this.AssignHandle(spin.Handle)
            with _ -> ()
        do
            attach ()
            spin.HandleCreated.Add(fun _ -> attach ())
            spin.HandleDestroyed.Add(fun _ ->
                hot <- -1
                pressed <- -1
                try this.ReleaseHandle() with _ -> ())
        override this.WndProc(message: byref<Message>) =
            if numeric.BorderStyle = BorderStyle.Fixed3D then
                let x = int (int16 (int64 message.LParam &&& 0xFFFFL))
                let y = int (int16 ((int64 message.LParam >>> 16) &&& 0xFFFFL))
                let part = if spin.ClientRectangle.Contains(x, y) then halfAt y else -1
                match message.Msg with
                | 0x0200 -> update part pressed // WM_MOUSEMOVE
                | 0x02A3 -> update -1 pressed // WM_MOUSELEAVE
                | 0x0201 | 0x0203 -> update part part // WM_LBUTTONDOWN / WM_LBUTTONDBLCLK
                | 0x0202 -> update part -1 // WM_LBUTTONUP
                | 0x0215 -> update hot -1 // WM_CAPTURECHANGED
                | _ -> ()
            base.WndProc(&message)
            if numeric.BorderStyle = BorderStyle.Fixed3D &&
               (message.Msg = 0x000F || message.Msg = 0x0318) then
                try
                    use graphics =
                        if message.Msg = 0x000F then Graphics.FromHwnd(spin.Handle)
                        else Graphics.FromHdc(message.WParam)
                    // Themed corners are transparent. Remove the native
                    // button state beneath them before painting our state.
                    use background = new SolidBrush(if numeric.Enabled then numeric.BackColor else SystemColors.Control)
                    graphics.FillRectangle(background, spin.ClientRectangle)
                    let height = spin.ClientSize.Height / 2
                    for half in 0 .. 1 do
                        let bounds = Rectangle(0, half * height, spin.ClientSize.Width, height)
                        let state =
                            if not numeric.Enabled then 4
                            elif pressed = half && hot = half then 3
                            elif hot = half then 2
                            else 1
                        let buttonState = enum<VisualStyles.PushButtonState>(state)
                        ButtonRenderer.DrawButton(graphics, bounds, buttonState)
                        let color = if numeric.Enabled then SystemColors.ControlText else SystemColors.GrayText
                        drawNumericArrow graphics bounds (half = 1) color
                with _ -> ()

    let private numericInk = Runtime.CompilerServices.ConditionalWeakTable<Font, Tuple<int, int>>()

    let private digitInk (font: Font) =
        numericInk.GetValue(font, fun font ->
            let size = TextRenderer.MeasureText("0123456789", font, Size.Empty,
                                               TextFormatFlags.NoPadding ||| TextFormatFlags.SingleLine)
            use bitmap = new Bitmap(size.Width, size.Height)
            use graphics = Graphics.FromImage(bitmap)
            graphics.Clear(Color.White)
            TextRenderer.DrawText(graphics, "0123456789", font, Point.Empty, Color.Black,
                                  TextFormatFlags.NoPadding ||| TextFormatFlags.SingleLine)
            let mutable top = size.Height
            let mutable bottom = -1
            for y in 0 .. size.Height - 1 do
                for x in 0 .. size.Width - 1 do
                    let pixel = bitmap.GetPixel(x, y)
                    if int pixel.R + int pixel.G + int pixel.B < 384 then
                        top <- min top y
                        bottom <- max bottom y
            Tuple.Create(top, bottom))

    // Register once per numeric field, even when apply is called again.
    let private numericFonts = Runtime.CompilerServices.ConditionalWeakTable<NumericUpDown, (unit -> unit)>()

    /// Give `control` the shared height. Anything else is left alone, so a
    /// caller may hand it a whole row of controls.
    let rec apply (control: Control) =
        match control with
        | :? ComboBox as combo ->
            if combo.DrawMode = DrawMode.Normal then ownerDraw combo
            combo.ItemHeight <- comboItemHeightPx
        | :? NumericUpDown as numeric ->
            numeric.AutoSize <- false
            match numericFonts.TryGetValue(numeric) with
            | true, refresh -> refresh ()
            | _ ->
                let fallback = numeric.Font
                let mutable parent: Control = null
                let mutable owned: Font option = None
                let mutable fitting = false
                let mutable fitted: (Font * int * BorderStyle) option = None
                let fitFont () =
                    if not fitting && not numeric.IsDisposed then
                        let family = if isNull parent then fallback else parent.Font
                        let target = SettingsDpi.px heightPx
                        let unchanged =
                            match fitted with
                            | Some(source, height, border) ->
                                source.Equals(family) && height = target && border = numeric.BorderStyle
                            | None -> false
                        if not unchanged then
                            fitting <- true
                            try
                                // Explicit fonts stop WinForms inheritance. Always start
                                // from the parent, never from our previous enlargement.
                                let assign (font: Font) =
                                    if numeric.Font.Equals(font) then font.Dispose()
                                    else
                                        let previous = owned
                                        numeric.Font <- font
                                        owned <- Some font
                                        previous |> Option.iter (fun old -> old.Dispose())
                                let fontAt points =
                                    new Font(family.FontFamily,
                                             family.Size * (family.SizeInPoints + points) / family.SizeInPoints,
                                             family.Style, family.Unit, family.GdiCharSet, family.GdiVerticalFont)
                                let targetHeight = numericHeightRatio.Value * float target
                                use probe = new NumericUpDown(BorderStyle = numeric.BorderStyle)
                                // Count every candidate, including the final live font.
                                // The probe has no parent and never creates a window.
                                let maxFonts = 40
                                let mutable created = 0
                                let measure extra =
                                    use candidate = fontAt extra
                                    created <- created + 1
                                    probe.Font <- candidate
                                    abs (float probe.PreferredHeight - targetHeight)
                                let mutable bestExtra = 1.0f
                                let mutable bestDistance = measure bestExtra
                                // Compare unrounded distances: rounding the target first
                                // can select the wrong native height. On ties prefer the
                                // extra nearest +1pt, preserving the exact 100% font.
                                // The +8pt ceiling bounds growth for unusual fonts/scales.
                                let mutable step = 0
                                while step <= 32 && created < maxFonts - 1 do
                                    let extra = float32 step * 0.25f
                                    let distance = measure extra
                                    if distance < bestDistance ||
                                       (distance = bestDistance && abs (extra - 1.0f) < abs (bestExtra - 1.0f)) then
                                        bestExtra <- extra
                                        bestDistance <- distance
                                    step <- step + 1
                                // Publish the input before assignment: synchronous and
                                // deferred layouts must both observe a completed fit.
                                fitted <- Some(family, target, numeric.BorderStyle)
                                created <- created + 1
                                assign (fontAt bestExtra)
                            finally fitting <- false
                let parentFontChanged = EventHandler(fun _ _ -> fitFont ())
                // Layout notices changes to the destination scale. A font
                // overwritten by Control.Scale is not a new fitting input;
                // unchanged inputs must never restart the layout cycle.
                let parentLayout = LayoutEventHandler(fun _ _ -> fitFont ())
                let followParent () =
                    if not (isNull parent) then
                        parent.FontChanged.RemoveHandler(parentFontChanged)
                        parent.Layout.RemoveHandler(parentLayout)
                    parent <- numeric.Parent
                    if not (isNull parent) then
                        parent.FontChanged.AddHandler(parentFontChanged)
                        parent.Layout.AddHandler(parentLayout)
                    fitFont ()
                numericFonts.Add(numeric, fitFont)
                numeric.ParentChanged.Add(fun _ -> followParent ())
                numeric.FontChanged.Add(fun _ -> fitFont ())
                numeric.HandleCreated.Add(fun _ -> fitFont ())
                numeric.Disposed.Add(fun _ ->
                    if not (isNull parent) then
                        parent.FontChanged.RemoveHandler(parentFontChanged)
                        parent.Layout.RemoveHandler(parentLayout)
                    owned |> Option.iter (fun font -> font.Dispose())
                    owned <- None)
                followParent ()
                // The number lives in an edit box the control lays out for itself,
                // against the top of the field. It is put back in the middle after
                // every layout, which is when the control has just moved it.
                let centreEdit () =
                    numeric.SuspendLayout()
                    try
                        for child in numeric.Controls do
                            match child with
                            | :? TextBoxBase as box ->
                                // EDIT's native baseline is one pixel above the GDI
                                // text probe; compensate without changing the font.
                                let ink = digitInk box.Font
                                let top = max 0 (1 + (numeric.ClientSize.Height - 1 - ink.Item1 - ink.Item2) / 2)
                                box.AutoSize <- false
                                let height = max 1 (numeric.ClientSize.Height - top - 1)
                                if box.Top <> top || box.Height <> height then
                                    box.SetBounds(box.Left, top, box.Width, height)
                            | spin ->
                                // Native layout gives an odd-height spinner to two
                                // buttons. Leave the spare pixel below both halves.
                                let available = max 0 (numeric.ClientSize.Height - 2)
                                let height = available - available % 2
                                if spin.Height <> height then spin.Height <- height
                    finally numeric.ResumeLayout(false)
                numeric.Layout.Add(fun _ -> centreEdit ())
                numeric.SizeChanged.Add(fun _ -> centreEdit ())
                numeric.FontChanged.Add(fun _ -> centreEdit ())
                for child in numeric.Controls do
                    if not (child :? TextBoxBase) then NumericButtons(numeric, child) |> ignore
                centred.Add(numeric, centreEdit)
                centreEdit ()
        | :? TextBoxBase as box ->
            if not box.Multiline then
                box.AutoSize <- false
                box.Height <- heightPx
                centreText box
        | _ -> ()

    /// Centre the text of every field under `root` again. The settings dialog
    /// calls this once it has been themed and scaled: both put the drawing
    /// rectangle of an edit control back where it was.
    let rec reassert (root: Control) =
        match root with
        | :? NumericUpDown as numeric ->
            match numericFonts.TryGetValue(numeric) with
            | true, refresh -> refresh ()
            | _ -> ()
        | _ -> ()
        match centred.TryGetValue(root) with
        | true, centre -> centre ()
        | _ -> ()
        for child in root.Controls do reassert child

    let private numericLayouts = Runtime.CompilerServices.ConditionalWeakTable<NumericUpDown, obj>()

    let arrangingNumeric (numeric: NumericUpDown) =
        match numericLayouts.TryGetValue(numeric) with
        | true, _ -> true
        | _ -> false

    /// Finish child geometry before painting, after UpDownBase has performed
    /// its native layout. Never let child resizing restart the parent layout.
    let reassertNumericLayout (numeric: NumericUpDown) =
        match numericFonts.TryGetValue(numeric), numericLayouts.TryGetValue(numeric) with
        | (true, _), (false, _) ->
            numericLayouts.Add(numeric, obj())
            try reassert numeric
            finally numericLayouts.Remove(numeric) |> ignore
        | _ -> ()
