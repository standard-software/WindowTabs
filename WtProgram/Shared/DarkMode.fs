namespace Bemo
open System
open System.Drawing
open System.Runtime.InteropServices
open System.Windows.Forms
open Aga.Controls.Tree

module DarkMode =
    // Dark theme palette inspired by Win11 / VS Dark.
    //   darkSurface : form bg + most static surfaces (mid-dark)
    //   darkPanel   : page area, active tab, input controls (slightly lighter)
    //   darkTabStrip: tab strip background + inactive tabs (darker than
    //                 darkSurface so the strip is visually distinct from the
    //                 system title bar above it)
    //   darkBorder  : separator strokes
    //   darkText    : foreground
    //   darkAccent  : selection / pressed / focus ring
    let darkSurface = Color.FromArgb(32, 32, 32)
    let darkPanel = Color.FromArgb(45, 45, 45)
    let darkTabStrip = Color.FromArgb(20, 20, 20)
    let darkBorder = Color.FromArgb(64, 64, 64)
    let darkText = Color.FromArgb(240, 240, 240)
    let darkAccent = Color.FromArgb(0, 120, 212)

    // Module-wide flag set by DesktopManagerForm before view construction.
    // Used by code paths that need to know dark mode is on but live in
    // modules below DesktopManagerForm in compile order (e.g.
    // DarkNodeCheckBox factory, etc.).
    let mutable darkModeEnabled = false

    // The shipped Aga.Controls.dll is a prebuilt binary so we can't add a
    // dark-mode flag to its source. Instead we paint over the column headers
    // ourselves via the public Paint event and tint cell text via the public
    // BaseTextControl.DrawText event (see attachDarkTreeViewAdvOverlay).

    [<DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")>]
    extern int private DwmSetWindowAttributeNative(IntPtr hwnd, int attr, int& pvAttribute, int cbAttribute)

    [<DllImport("uxtheme.dll", CharSet = CharSet.Unicode, EntryPoint = "SetWindowTheme")>]
    extern int private SetWindowTheme(IntPtr hwnd, string pszSubAppName, string pszSubIdList)

    [<DllImport("user32.dll")>]
    extern IntPtr private GetWindowDC(IntPtr hWnd)

    [<DllImport("user32.dll")>]
    extern int private ReleaseDC(IntPtr hWnd, IntPtr hDC)

    [<DllImport("user32.dll")>]
    extern bool private ValidateRect(IntPtr hWnd, IntPtr lpRect)

    [<DllImport("kernel32.dll", CharSet = CharSet.Unicode)>]
    extern IntPtr private LoadLibraryW(string lpLibFileName)

    [<DllImport("kernel32.dll")>]
    extern IntPtr private GetProcAddress(IntPtr hModule, IntPtr lpProcName)

    [<UnmanagedFunctionPointer(CallingConvention.Winapi)>]
    type SetPreferredAppModeDelegate = delegate of int -> int

    [<UnmanagedFunctionPointer(CallingConvention.Winapi)>]
    type FlushMenuThemesDelegate = delegate of unit -> unit

    let DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19
    let DWMWA_USE_IMMERSIVE_DARK_MODE = 20

    let mutable private setPreferredAppMode : SetPreferredAppModeDelegate option = None
    let mutable private flushMenuThemes : FlushMenuThemesDelegate option = None

    // Initialize dark mode for menus
    let private initializeDarkModeForMenus() =
        try
            let hUxtheme = LoadLibraryW("uxtheme.dll")
            if hUxtheme <> IntPtr.Zero then
                // ordinal 135 = SetPreferredAppMode (Windows 10 1903+)
                let pSetPreferredAppMode = GetProcAddress(hUxtheme, IntPtr(135))
                if pSetPreferredAppMode <> IntPtr.Zero then
                    setPreferredAppMode <- Some(Marshal.GetDelegateForFunctionPointer<SetPreferredAppModeDelegate>(pSetPreferredAppMode))

                // ordinal 136 = FlushMenuThemes
                let pFlushMenuThemes = GetProcAddress(hUxtheme, IntPtr(136))
                if pFlushMenuThemes <> IntPtr.Zero then
                    flushMenuThemes <- Some(Marshal.GetDelegateForFunctionPointer<FlushMenuThemesDelegate>(pFlushMenuThemes))

        with
        | ex ->
            System.Diagnostics.Debug.WriteLine(sprintf "Failed to initialize dark mode menu APIs: %s" ex.Message)

    do initializeDarkModeForMenus()

    let setDarkModeForMenus(enabled: bool) =
        try
            match setPreferredAppMode with
            | Some(func) ->
                // 0 = Default (Light mode), 1 = AllowDark (Dark mode)
                let mode = if enabled then 1 else 0
                func.Invoke(mode) |> ignore
            | None -> ()

            match flushMenuThemes with
            | Some(func) ->
                func.Invoke()
            | None -> ()
        with
        | ex -> ()

    // Stronger hint than AllowDark: tells uxtheme to treat the entire process
    // as a dark-mode app. Combined with SetWindowTheme(DarkMode_*) per control
    // this lets dark-aware native pieces switch over even when WinForms colors
    // alone wouldn't reach them.
    let setPreferredAppModeForceDark() =
        try
            match setPreferredAppMode with
            | Some(func) -> func.Invoke(2) |> ignore
            | None -> ()
            match flushMenuThemes with
            | Some(func) -> func.Invoke()
            | None -> ()
        with _ -> ()

    let useImmersiveDarkMode (handle: IntPtr) (enabled: bool) =
        try
            // Always try to apply dark mode on Windows (don't check version)
            // First set the window theme
            if enabled then
                SetWindowTheme(handle, "DarkMode_Explorer", null) |> ignore

            // Try both attribute values to ensure compatibility
            let mutable value = if enabled then 1 else 0

            // Try the newer attribute first (Windows 11/10 20H1+)
            let result1 = DwmSetWindowAttributeNative(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, &value, sizeof<int>)

            // Also try the older attribute (Windows 10 older builds)
            let result2 = DwmSetWindowAttributeNative(handle, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, &value, sizeof<int>)

            result1 = 0 || result2 = 0
        with
        | _ -> false

    let setControlTheme (handle: IntPtr) (subAppName: string) =
        try SetWindowTheme(handle, subAppName, null) |> ignore
        with _ -> ()

    // ComboBox dropdown handle retrieval — used to theme the popup listbox
    // separately from the combo proper.
    [<StructLayout(LayoutKind.Sequential)>]
    type private COMBOBOXINFO =
        struct
            val mutable cbSize: int32
            val mutable rcItem: System.Drawing.Rectangle
            val mutable rcButton: System.Drawing.Rectangle
            val mutable buttonState: int32
            val mutable hwndCombo: IntPtr
            val mutable hwndItem: IntPtr
            val mutable hwndList: IntPtr
        end

    [<DllImport("user32.dll")>]
    extern bool private GetComboBoxInfo(IntPtr hwndCombo, COMBOBOXINFO& info)

    // Recursively apply a "best effort" dark color scheme to a WinForms control
    // tree. Type-aware so each control category gets sensible BackColor / ForeColor
    // / BorderStyle defaults.
    let rec applyDarkColorsToControl (control: Control) =
        try
            match control with
            | :? TextBox as tb ->
                tb.BackColor <- darkPanel
                tb.ForeColor <- darkText
                // NumericUpDown's inner UpDownEdit is also a TextBox, and
                // walking children of NumericUpDown brings us here. The outer
                // NumericUpDown already wears FixedSingle (recoloured in
                // NCPAINT) so a second FixedSingle on the inner edit produces
                // the visible double border the user reported. Inner edit
                // gets None.
                tb.BorderStyle <-
                    match tb.Parent with
                    | :? NumericUpDown -> BorderStyle.None
                    | _ -> BorderStyle.FixedSingle
            | :? NumericUpDown as nud ->
                nud.BackColor <- darkPanel
                nud.ForeColor <- darkText
                // FixedSingle keeps a 1-px border in the NON-CLIENT area —
                // OUTSIDE the inner TextBox's client rect — so focus state
                // changes on the inner edit can never erase the top edge.
                // The inner TextBox has its DarkMode_CFD theme stripped (in
                // applyDarkNativeThemeToControl) so we don't get a second
                // "double" border, and DarkNumericUpDownFrameSubclass
                // recolors this NC border in dark grey via WM_NCPAINT.
                nud.BorderStyle <- BorderStyle.FixedSingle
            | :? Button as btn ->
                // Skip buttons whose BackColor IS the content (e.g. the
                // ColorEditor's color-swatch button). They are tagged with
                // "DarkModePreserveColor" so we don't overwrite the chosen
                // color when applying the theme.
                let preserve =
                    match btn.Tag with
                    | :? string as s when s = "DarkModePreserveColor" -> true
                    | _ -> false
                if preserve then
                    // Preserve content color but switch to Flat with a 1-px
                    // dark border so the system "raised" chrome doesn't add
                    // a thick white frame around the swatch.
                    btn.FlatStyle <- FlatStyle.Flat
                    btn.FlatAppearance.BorderColor <- darkBorder
                    btn.FlatAppearance.BorderSize <- 1
                if not preserve then
                    btn.BackColor <- darkPanel
                    btn.ForeColor <- darkText
                    btn.FlatStyle <- FlatStyle.Flat
                    btn.FlatAppearance.BorderColor <- darkBorder
                    // FlatStyle still uses the system "disabled" foreground
                    // color when btn.Enabled = false, which renders as nearly
                    // black on our dark panel. For text-only buttons, re-draw
                    // the text on top in a readable tone (lighter when
                    // enabled, mid-grey when disabled). Skip image-bearing
                    // buttons so we don't overlap their glyph.
                    if isNull btn.Image then
                        btn.Paint.Add(fun e ->
                            if not (System.String.IsNullOrEmpty(btn.Text)) then
                                let g = e.Graphics
                                let textColor =
                                    if btn.Enabled then darkText
                                    else Color.FromArgb(160, 160, 160)
                                use bgBrush = new SolidBrush(darkPanel)
                                g.FillRectangle(bgBrush, btn.ClientRectangle)
                                use borderPen = new Pen(darkBorder)
                                let r = btn.ClientRectangle
                                g.DrawRectangle(borderPen, Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1))
                                let alignFlags =
                                    match btn.TextAlign with
                                    | ContentAlignment.MiddleLeft -> TextFormatFlags.Left ||| TextFormatFlags.VerticalCenter
                                    | ContentAlignment.MiddleRight -> TextFormatFlags.Right ||| TextFormatFlags.VerticalCenter
                                    | ContentAlignment.TopCenter -> TextFormatFlags.HorizontalCenter ||| TextFormatFlags.Top
                                    | ContentAlignment.BottomCenter -> TextFormatFlags.HorizontalCenter ||| TextFormatFlags.Bottom
                                    | _ -> TextFormatFlags.HorizontalCenter ||| TextFormatFlags.VerticalCenter
                                TextRenderer.DrawText(g, btn.Text, btn.Font, btn.ClientRectangle, textColor, alignFlags ||| TextFormatFlags.EndEllipsis))
            | :? CheckBox as cb ->
                cb.BackColor <- darkSurface
                cb.ForeColor <- darkText
                // FlatStyle.Flat lets FlatAppearance colors take effect, but
                // the underlying glyph rendering can still leak system grey
                // through. Attach a Paint handler that overpaints the 13x13
                // box area with pure black + grey border + light check glyph
                // AFTER the system paint, guaranteeing the desired look.
                cb.FlatStyle <- FlatStyle.Flat
                cb.FlatAppearance.CheckedBackColor <- darkSurface
                cb.FlatAppearance.BorderColor <- Color.FromArgb(120, 120, 120)
                cb.FlatAppearance.MouseOverBackColor <- Color.FromArgb(60, 60, 60)
                cb.FlatAppearance.MouseDownBackColor <- Color.FromArgb(60, 60, 60)
                cb.Paint.Add(fun e ->
                    let g = e.Graphics
                    let boxSize = 13
                    let boxY = (cb.Height - boxSize) / 2
                    let boxRect = Rectangle(0, boxY, boxSize, boxSize)
                    // Fill the box with the parent surface tone so the box
                    // visually melts into the background (only the grey
                    // border outlines it). Matches the "transparent / match
                    // bg" preference from branch-18 review.
                    use bg = new SolidBrush(cb.BackColor)
                    g.FillRectangle(bg, boxRect)
                    use border = new Pen(Color.FromArgb(120, 120, 120))
                    g.DrawRectangle(border, boxRect)
                    if cb.Checked then
                        use checkPen = new Pen(darkText, 2.0f)
                        checkPen.StartCap <- System.Drawing.Drawing2D.LineCap.Round
                        checkPen.EndCap <- System.Drawing.Drawing2D.LineCap.Round
                        g.DrawLines(checkPen, [|
                            Point(boxRect.X + 3, boxRect.Y + 6)
                            Point(boxRect.X + 5, boxRect.Y + 9)
                            Point(boxRect.X + 10, boxRect.Y + 3)
                        |])
                    elif cb.CheckState = CheckState.Indeterminate then
                        use fillBrush = new SolidBrush(darkText)
                        g.FillRectangle(fillBrush, Rectangle(boxRect.X + 3, boxRect.Y + 5, boxRect.Width - 6, 3)))
            | :? RadioButton as rb ->
                rb.BackColor <- darkSurface
                rb.ForeColor <- darkText
                rb.FlatStyle <- FlatStyle.Flat
                rb.FlatAppearance.CheckedBackColor <- darkSurface
                rb.FlatAppearance.BorderColor <- Color.FromArgb(120, 120, 120)
                rb.FlatAppearance.MouseOverBackColor <- Color.FromArgb(60, 60, 60)
                rb.FlatAppearance.MouseDownBackColor <- Color.FromArgb(60, 60, 60)
                rb.Paint.Add(fun e ->
                    let g = e.Graphics
                    let circleSize = 13
                    let circleY = (rb.Height - circleSize) / 2
                    let circleRect = Rectangle(0, circleY, circleSize, circleSize)
                    g.SmoothingMode <- System.Drawing.Drawing2D.SmoothingMode.AntiAlias
                    // Match the parent surface so the circle outline sits on
                    // an invisible disc rather than a stark black puck.
                    use bg = new SolidBrush(rb.BackColor)
                    g.FillEllipse(bg, circleRect)
                    use border = new Pen(Color.FromArgb(120, 120, 120))
                    g.DrawEllipse(border, circleRect)
                    if rb.Checked then
                        let dotInset = 4
                        let dotRect = Rectangle(circleRect.X + dotInset, circleRect.Y + dotInset, circleRect.Width - dotInset * 2, circleRect.Height - dotInset * 2)
                        use dot = new SolidBrush(darkText)
                        g.FillEllipse(dot, dotRect))
            | :? ComboBox as cmb ->
                cmb.BackColor <- darkPanel
                cmb.ForeColor <- darkText
                cmb.FlatStyle <- FlatStyle.Flat
                // OwnerDrawFixed lets us own the popup item highlight and
                // text colors, replacing the system blue with our accent.
                if cmb.DrawMode = DrawMode.Normal then
                    cmb.DrawMode <- DrawMode.OwnerDrawFixed
                cmb.DrawItem.Add(fun e ->
                    let g = e.Graphics
                    let isSelected = (e.State &&& DrawItemState.Selected) <> DrawItemState.None
                    let bgColor = if isSelected then darkAccent else darkPanel
                    use bg = new SolidBrush(bgColor)
                    g.FillRectangle(bg, e.Bounds)
                    if e.Index >= 0 && e.Index < cmb.Items.Count then
                        let txt =
                            match cmb.Items.[e.Index] with
                            | null -> ""
                            | o -> o.ToString()
                        TextRenderer.DrawText(
                            g, txt, cmb.Font, e.Bounds, darkText,
                            TextFormatFlags.Left ||| TextFormatFlags.VerticalCenter ||| TextFormatFlags.EndEllipsis))
                // Theme the dropdown LIST (popup) separately when it's about
                // to open. The popup is a different HWND from the combo proper
                // so SetWindowTheme on the combo doesn't reach it.
                cmb.DropDown.Add(fun _ ->
                    try
                        let mutable info = Unchecked.defaultof<COMBOBOXINFO>
                        info.cbSize <- Marshal.SizeOf(typeof<COMBOBOXINFO>)
                        if GetComboBoxInfo(cmb.Handle, &info) && info.hwndList <> IntPtr.Zero then
                            setControlTheme info.hwndList "DarkMode_Explorer"
                    with _ -> ())
            | :? StatusBar as sb ->
                sb.BackColor <- darkSurface
                sb.ForeColor <- darkText
                // StatusBar.Panels also need their colors; the BackColor of
                // the StatusBar covers the bar itself but each panel can draw
                // separately. We rely on the panels inheriting the parent.
                sb.SizingGrip <- false
            | :? TreeViewAdv as tva ->
                // Aga's TreeViewAdv: set the WinForms-level colors. Column
                // headers are overpainted in attachDarkTreeViewAdvOverlay
                // below since the prebuilt DLL still uses SystemBrushes for
                // them. Cell text color is hooked via BaseTextControl.DrawText
                // events on each NodeControl.
                tva.BackColor <- darkPanel
                tva.ForeColor <- darkText
                // The default LineColor (SystemColors.ControlDark, light
                // gray) is invisible against the dark background. Use a
                // mid-tone so the tree branch lines are at least faintly
                // visible.
                tva.LineColor <- Color.FromArgb(120, 120, 120)
                // HideSelection makes the inactive (focus-elsewhere)
                // selection invisible. Without this, the workspace tab's
                // initial appearance shows light selection background under
                // light text — a "row is selected but invisible" symptom.
                tva.HideSelection <- true
                // Default BorderStyle.Fixed3D draws a thick light sunken
                // edge around the tree which looks like a "white frame"
                // against the dark theme. Drop it so the tree blends with
                // the surrounding TabPage.
                tva.BorderStyle <- BorderStyle.None
            | :? Label as lbl ->
                lbl.BackColor <- darkSurface
                lbl.ForeColor <- darkText
            | :? GroupBox as gb ->
                gb.BackColor <- darkSurface
                gb.ForeColor <- darkText
            | :? TabControl as tc ->
                tc.BackColor <- darkTabStrip
                tc.ForeColor <- darkText
            | :? TabPage as tp ->
                // Page area uses darkPanel so it matches the active tab's fill
                tp.BackColor <- darkPanel
                tp.ForeColor <- darkText
            | :? ListView as lv ->
                lv.BackColor <- darkPanel
                lv.ForeColor <- darkText
            | :? ListBox as lb ->
                lb.BackColor <- darkPanel
                lb.ForeColor <- darkText
            | :? DataGridView as dgv ->
                dgv.BackgroundColor <- darkSurface
                dgv.GridColor <- darkBorder
                dgv.DefaultCellStyle.BackColor <- darkPanel
                dgv.DefaultCellStyle.ForeColor <- darkText
                dgv.ColumnHeadersDefaultCellStyle.BackColor <- darkSurface
                dgv.ColumnHeadersDefaultCellStyle.ForeColor <- darkText
                dgv.RowHeadersDefaultCellStyle.BackColor <- darkSurface
                dgv.RowHeadersDefaultCellStyle.ForeColor <- darkText
                dgv.EnableHeadersVisualStyles <- false
            | :? Panel ->
                control.BackColor <- darkSurface
                control.ForeColor <- darkText
            | :? TableLayoutPanel ->
                control.BackColor <- darkSurface
                control.ForeColor <- darkText
            | _ ->
                control.BackColor <- darkSurface
                control.ForeColor <- darkText
        with _ -> ()
        for child in control.Controls do
            applyDarkColorsToControl(child)

    // SetWindowTheme pass — see branch 3 commit message for rationale.
    let rec applyDarkNativeThemeToControl (control: Control) =
        try
            if control.IsHandleCreated then
                match control with
                | :? ComboBox ->
                    // Disable visual styles entirely on the combo proper so
                    // the system stops drawing hover/focus border flashes
                    // (white-blink on MouseEnter). Our FlatStyle.Flat +
                    // DarkComboBoxSubclass owns the appearance instead. The
                    // dropdown LIST popup is themed separately via
                    // DarkMode_Explorer in the cmb.DropDown handler.
                    SetWindowTheme(control.Handle, "", "") |> ignore
                | :? TextBox when (control.Parent :? NumericUpDown) ->
                    // Inner edit of NumericUpDown: kill its DarkMode_CFD
                    // border so the only visible frame is the outer
                    // FixedSingle (recoloured by DarkNumericUpDownFrameSubclass
                    // via WM_NCPAINT). Without this we'd see the inner CFD
                    // 1-px line plus the outer FixedSingle 1-px line as a
                    // visible double border.
                    SetWindowTheme(control.Handle, "", "") |> ignore
                | _ ->
                    let appName =
                        match control with
                        | :? TextBox -> "DarkMode_CFD"
                        | :? NumericUpDown -> "DarkMode_CFD"
                        | :? ListView -> "DarkMode_Explorer"
                        | :? ListBox -> "DarkMode_Explorer"
                        | :? DataGridView -> "DarkMode_Explorer"
                        | _ -> "DarkMode_Explorer"
                    setControlTheme control.Handle appName
        with _ -> ()
        for child in control.Controls do
            applyDarkNativeThemeToControl(child)

    // Owner-draw the tab headers of a TabControl. Active tab uses darkPanel
    // (matching the page area for visual unity) and inactive tabs use
    // darkTabStrip (noticeably darker so they're readily distinguishable
    // from the active tab). The fill extends 2 px BELOW the tab bounds to
    // hide the system divider line between the strip and the page; we do
    // NOT extend above any more — that caused inactive-tab text to overlap
    // the strip top edge.
    let attachDarkTabControlOwnerDraw (tabControl: TabControl) =
        // Tab geometry only — actual painting is done by
        // DarkTabControlFrameSubclass which fully owns WM_PAINT and paints
        // each tab + the page-frame area itself. DrawMode stays Normal
        // because no DrawItem dispatch is needed (base.WndProc never runs
        // for WM_PAINT once the subclass is attached).
        tabControl.ItemSize <- Size(0, 26)
        tabControl.SizeMode <- TabSizeMode.Normal

    // Owner-draw a GroupBox: the system always paints the title text + frame
    // in window colors, so we paint a dark frame + dark title text on top.
    let attachDarkGroupBoxOwnerDraw (gb: GroupBox) =
        gb.Paint.Add(fun e ->
            let g = e.Graphics
            use bgBrush = new SolidBrush(darkSurface)
            g.FillRectangle(bgBrush, gb.ClientRectangle)
            let textSize = TextRenderer.MeasureText(gb.Text, gb.Font)
            let halfText = textSize.Height / 2
            use borderPen = new Pen(darkBorder)
            // Outer frame with a gap above for the title text
            let titleStart = if String.IsNullOrEmpty(gb.Text) then 0 else 8
            let titleEnd = titleStart + textSize.Width
            // Top: left gap + skip text + right gap
            g.DrawLine(borderPen, 0, halfText, titleStart - 2, halfText)
            g.DrawLine(borderPen, titleEnd + 2, halfText, gb.Width - 1, halfText)
            // Sides + bottom
            g.DrawLine(borderPen, 0, halfText, 0, gb.Height - 1)
            g.DrawLine(borderPen, gb.Width - 1, halfText, gb.Width - 1, gb.Height - 1)
            g.DrawLine(borderPen, 0, gb.Height - 1, gb.Width - 1, gb.Height - 1)
            if not (String.IsNullOrEmpty(gb.Text)) then
                use textBrush = new SolidBrush(darkText)
                g.DrawString(gb.Text, gb.Font, textBrush, float32 titleStart, 0.0f))

    // Walk a control tree and attach owner-draw handlers to every TabControl
    // and GroupBox encountered.
    let rec attachDarkOwnerDrawHandlers (control: Control) =
        match control with
        | :? TabControl as tc -> attachDarkTabControlOwnerDraw tc
        | :? GroupBox as gb -> attachDarkGroupBoxOwnerDraw gb
        | _ -> ()
        for child in control.Controls do
            attachDarkOwnerDrawHandlers(child)

    // === Branch-7 specific helpers ====================================
    // Two long-standing problem cases:
    //  - TabControl background frame: even after owner-drawing the headers,
    //    the system still paints a 2-pixel light frame around the tab pages.
    //  - HotKeyControl (msctls_hotkey32): a Win32 common control whose
    //    painting ignores SetWindowTheme(DarkMode_*) hints AND doesn't fire
    //    WM_CTLCOLOREDIT to its parent, so neither the WinMerge approach nor
    //    the standard WinForms recolor reaches it.

    // Aggressively repaint the TabControl client area on every paint with a
    // dark fill before the system paints its frame. Combined with owner-drawn
    // headers in branch 4 this kills the lit frame between the strip and the
    // page area.
    let attachDarkTabControlBackgroundFill (tabControl: TabControl) =
        // Paint event runs after the system has drawn its frame, so we draw a
        // dark rectangle over the visible non-tab-page area. We compute the
        // strip area and the gap below it, leaving the tab page itself alone.
        tabControl.Paint.Add(fun e ->
            let g = e.Graphics
            use brush = new SolidBrush(darkSurface)
            // Determine the strip rectangle (top of control, height = first
            // tab's bounds height + a couple of pixels).
            let stripBottom =
                if tabControl.TabPages.Count > 0 && tabControl.TabPages.[0].IsHandleCreated then
                    let r = tabControl.GetTabRect(0)
                    r.Bottom
                else 24
            // Fill from the bottom of the strip to the top of the page area
            // (the thin frame the system would otherwise paint).
            let frameTop = stripBottom
            let frameBottom = tabControl.ClientSize.Height
            let pageRect =
                if tabControl.SelectedIndex >= 0 && tabControl.SelectedIndex < tabControl.TabPages.Count then
                    tabControl.TabPages.[tabControl.SelectedIndex].Bounds
                else Rectangle.Empty
            // Top frame strip
            if pageRect.Top > frameTop then
                g.FillRectangle(brush, Rectangle(0, frameTop, tabControl.ClientSize.Width, pageRect.Top - frameTop))
            // Bottom frame strip
            if pageRect.Bottom < frameBottom then
                g.FillRectangle(brush, Rectangle(0, pageRect.Bottom, tabControl.ClientSize.Width, frameBottom - pageRect.Bottom))
            // Left and right frame strips
            if pageRect.Left > 0 then
                g.FillRectangle(brush, Rectangle(0, frameTop, pageRect.Left, frameBottom - frameTop))
            if pageRect.Right < tabControl.ClientSize.Width then
                g.FillRectangle(brush, Rectangle(pageRect.Right, frameTop, tabControl.ClientSize.Width - pageRect.Right, frameBottom - frameTop)))

    // Throw multiple theme names at the HotKey common control to find one it
    // accepts, plus force its WinForms-side properties. If none stick, the
    // user at least sees the control clearly because its parent (set up by
    // applyDarkColorsToControl) is dark.
    let tryDarkenHotKeyLikeControl (control: Control) =
        try
            // The HotKey common control class name is "msctls_hotkey32".
            // We can't easily detect that from F# without P/Invoking GetClassName;
            // detect by the (already-public) BackColor/ForeColor write attempt
            // and the fact that this is called from a HotKey-aware caller.
            control.BackColor <- darkPanel
            control.ForeColor <- darkText
            if control.IsHandleCreated then
                // The HOTKEY common control occasionally responds to the modern
                // explorer dark-mode theme; try a few candidates.
                setControlTheme control.Handle "DarkMode_CFD"
                setControlTheme control.Handle "DarkMode_Explorer"
                setControlTheme control.Handle "DarkMode"
        with _ -> ()

    // Detect candidates by class name and apply the HotKey-specific treatment.
    [<DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")>]
    extern int private GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount)

    let private classNameOf (handle: IntPtr) =
        let buf = System.Text.StringBuilder(64)
        let len = GetClassName(handle, buf, buf.Capacity)
        if len > 0 then buf.ToString() else ""

    let rec applyDarkExtraTreatments (control: Control) =
        try
            // TabControl: extra background fill on top of owner-drawn headers
            match control with
            | :? TabControl as tc -> attachDarkTabControlBackgroundFill tc
            | _ -> ()
            // HotKey common control: identified by class name
            if control.IsHandleCreated then
                let cls = classNameOf control.Handle
                if cls = "msctls_hotkey32" || cls.StartsWith("msctls_hotkey") then
                    tryDarkenHotKeyLikeControl control
        with _ -> ()
        for child in control.Controls do
            applyDarkExtraTreatments(child)

    let applyDarkThemeToForm (form: Form) (enabled: bool) =
        if enabled then
            useImmersiveDarkMode form.Handle true |> ignore
            form.BackColor <- darkSurface
            form.ForeColor <- darkText
            for child in form.Controls do
                applyDarkColorsToControl(child)

    // Branch-4 entry point: title bar + recursive WinForms colors + native
    // SetWindowTheme pass + owner-draw handlers for TabControl headers and
    // GroupBox borders. Covers more of the surface than branch 3 at the cost
    // of more painting code.
    let applyDarkThemeFullToForm (form: Form) (enabled: bool) =
        if enabled then
            useImmersiveDarkMode form.Handle true |> ignore
            form.BackColor <- darkSurface
            form.ForeColor <- darkText
            for child in form.Controls do
                applyDarkColorsToControl(child)
                attachDarkOwnerDrawHandlers(child)
            for child in form.Controls do
                applyDarkNativeThemeToControl(child)
            form.Invalidate(true)

    // Branch-6 entry point: everything in applyDarkThemeFullToForm plus a
    // process-wide SetPreferredAppMode(ForceDark) call. The kitchen-sink
    // approach — combines WinForms-side recoloring (dark surface for static
    // areas), per-control SetWindowTheme (dark-aware native parts), owner
    // draw (TabControl headers, GroupBox frames) and OS-wide dark-mode hint
    // (covers any remaining dark-aware system controls). When this still
    // shows light remnants those controls are simply not dark-aware on this
    // OS build.
    let applyDarkThemeKitchenSinkToForm (form: Form) (enabled: bool) =
        if enabled then
            // Process-wide hint first so subsequent SetWindowTheme calls land
            // on a uxtheme that is already in dark-mode resolution mode.
            setPreferredAppModeForceDark()
            useImmersiveDarkMode form.Handle true |> ignore
            form.BackColor <- darkSurface
            form.ForeColor <- darkText
            for child in form.Controls do
                applyDarkColorsToControl(child)
                attachDarkOwnerDrawHandlers(child)
            for child in form.Controls do
                applyDarkNativeThemeToControl(child)
            form.Invalidate(true)

    // Branch-7 entry point: branch 6 plus extra treatments for the long-standing
    // problem cases (TabControl background frame and the HotKey common control).
    let applyDarkThemeWithExtrasToForm (form: Form) (enabled: bool) =
        if enabled then
            setPreferredAppModeForceDark()
            useImmersiveDarkMode form.Handle true |> ignore
            form.BackColor <- darkSurface
            form.ForeColor <- darkText
            for child in form.Controls do
                applyDarkColorsToControl(child)
                attachDarkOwnerDrawHandlers(child)
            for child in form.Controls do
                applyDarkNativeThemeToControl(child)
            for child in form.Controls do
                applyDarkExtraTreatments(child)
            form.Invalidate(true)

    // === Branch-9 specific helpers =====================================
    // Branch 9 attacks the residual problem cases reported in branch 7
    // testing: TreeViewAdv (column headers + node text), TabControl
    // background gaps, StatusBar visibility, and ComboBox drop-down popups.
    // It modifies treeviewadv source (TreeColumn / BaseTextControl /
    // TreeViewAdv.Draw) to honor a dark-mode flag and pairs that with
    // F#-side hooks for the WinForms layer.

    // NativeWindow subclass that overpaints WM_ERASEBKGND with the dark
    // surface so the entire TabControl client area starts off dark — the
    // owner-drawn tab headers and the (also-dark) tab pages sit on top of
    // this background, eliminating any "right of the last tab" or "between
    // strip and page" light gaps.
    type private DarkBackgroundSubclass(target: Control) as this =
        inherit NativeWindow()
        let WM_ERASEBKGND = 0x0014
        // TabControl uses the dedicated darkTabStrip color so the strip is
        // visibly distinct from the system title bar; everything else uses
        // darkSurface.
        let bgColor =
            match target with
            | :? TabControl -> darkTabStrip
            | _ -> darkSurface
        do
            let attach() = if target.IsHandleCreated then this.AssignHandle(target.Handle)
            attach()
            target.HandleCreated.Add(fun _ -> attach())
            target.HandleDestroyed.Add(fun _ -> this.ReleaseHandle())
        override this.WndProc(m: byref<Message>) =
            if m.Msg = WM_ERASEBKGND then
                try
                    use g = Graphics.FromHdc(m.WParam)
                    use brush = new SolidBrush(bgColor)
                    g.FillRectangle(brush, target.ClientRectangle)
                    m.Result <- IntPtr(1)
                with _ ->
                    base.WndProc(&m)
            else
                base.WndProc(&m)

    // Branch-13 TabControl frame killer. The WM_ERASEBKGND subclass above
    // covers the initial background, but the system still draws a 2-3 px
    // light frame around the tab page during WM_PAINT. This subclass
    // intercepts WM_PAINT, lets the system paint normally (so the
    // owner-drawn tabs appear), then immediately overpaints the four frame
    // strips around the selected tab page with darkSurface.
    // Full-owner-paint TabControl subclass. Earlier branches let the OS
    // paint the tab control via base.WndProc and then over-painted darker
    // colours on top, but the OS-drawn 1-px frame around the tab pages
    // kept showing through near the strip/page boundary (the "white line
    // under the tabs" the user reported through B17-B20). This subclass
    // takes over WM_PAINT entirely: skips base.WndProc, paints background
    // / each tab / frame area from scratch, and ValidateRect's the whole
    // window so the OS does not re-issue a paint. WM_ERASEBKGND is also
    // suppressed so the system never gets to flash a light background
    // first.
    type private DarkTabControlFrameSubclass(tc: TabControl) as this =
        inherit NativeWindow()
        let WM_PAINT = 0x000F
        let WM_ERASEBKGND = 0x0014
        let attach() =
            try if tc.IsHandleCreated && this.Handle = IntPtr.Zero then this.AssignHandle(tc.Handle)
            with _ -> ()
        let paintAll() =
            try
                use g = Graphics.FromHwnd(tc.Handle)
                let cr = tc.ClientRectangle
                // Background: darkTabStrip everywhere first; will be
                // overpainted with darkPanel for the page-frame area below.
                use stripBrush = new SolidBrush(darkTabStrip)
                g.FillRectangle(stripBrush, cr)
                // stripBottom = bottom edge of the tab strip
                let stripBottom =
                    if tc.TabPages.Count > 0 then
                        try (tc.GetTabRect(0)).Bottom
                        with _ -> 26
                    else 26
                // Page-frame area: from stripBottom down, paint with
                // darkPanel so the area around (and under) the active tab
                // matches the tab page surface. This intentionally extends
                // up to stripBottom (does NOT enter the tab strip), so
                // inactive tabs keep their darker fill all the way to
                // their bottom edge — no "light band under tabs".
                use pageBrush = new SolidBrush(darkPanel)
                if cr.Height > stripBottom then
                    g.FillRectangle(pageBrush, Rectangle(0, stripBottom, cr.Width, cr.Height - stripBottom))
                // Each tab: paint background + text. Replicates the per-
                // tab DrawItem dispatch so we don't depend on base.WndProc
                // running.
                use textBrush = new SolidBrush(darkText)
                use format = new StringFormat()
                format.Alignment <- StringAlignment.Center
                format.LineAlignment <- StringAlignment.Center
                let selectedIndex = tc.SelectedIndex
                for i in 0 .. tc.TabPages.Count - 1 do
                    try
                        let r = tc.GetTabRect(i)
                        let isSelected = i = selectedIndex
                        let bg = if isSelected then darkPanel else darkTabStrip
                        use bgBrush = new SolidBrush(bg)
                        // Slightly extend each tab fill so the system's old
                        // 1-px outline (now never drawn anyway) cannot
                        // reappear at the edges.
                        let extended = Rectangle(r.X - 1, r.Y - 2, r.Width + 2, r.Height + 4)
                        g.FillRectangle(bgBrush, extended)
                        let txt = tc.TabPages.[i].Text
                        let textRectF = RectangleF(float32 r.X, float32 r.Y, float32 r.Width, float32 r.Height)
                        g.DrawString(txt, tc.Font, textBrush, textRectF, format)
                    with _ -> ()
            with _ -> ()
        do
            attach()
            tc.HandleCreated.Add(fun _ -> attach())
            tc.HandleDestroyed.Add(fun _ -> try this.ReleaseHandle() with _ -> ())
            // Repaint on selection change so the active-tab fill follows
            // the user's click immediately.
            tc.SelectedIndexChanged.Add(fun _ -> try tc.Invalidate() with _ -> ())
        override this.WndProc(m: byref<Message>) =
            match m.Msg with
            | msg when msg = WM_ERASEBKGND ->
                m.Result <- IntPtr(1)
            | msg when msg = WM_PAINT ->
                paintAll()
                ValidateRect(tc.Handle, IntPtr.Zero) |> ignore
                m.Result <- IntPtr.Zero
            | _ ->
                base.WndProc(&m)

    // Walk the control tree and attach the dark-erase subclass to TabControls
    // and to any other control where the system paints a light background that
    // can't be covered via BackColor alone (notably TreeViewAdv's scrollbar
    // box corner). For TabControls also attach the frame-killer subclass so
    // the post-WM_PAINT system frame around the selected tab page is
    // overdrawn dark.
    let rec attachDarkBackgroundSubclassRecursive (control: Control) =
        match control with
        | :? TabControl as tc ->
            DarkBackgroundSubclass(control) |> ignore
            DarkTabControlFrameSubclass(tc) |> ignore
        | _ -> ()
        for child in control.Controls do
            attachDarkBackgroundSubclassRecursive(child)

    // Force every TreeViewAdv in the tree to invalidate so the new dark
    // overlays take effect on already-rendered cells / headers.
    let rec invalidateTreeViewAdvs (control: Control) =
        match control with
        | :? TreeViewAdv as tva -> tva.Invalidate()
        | _ -> ()
        for child in control.Controls do
            invalidateTreeViewAdvs(child)

    // TreeViewAdv.OnPaint doesn't call base.OnPaint(e), so the Paint event
    // never fires — Aga's prebuilt control owns the entire paint cycle. To
    // overlay the column header we have to subclass via NativeWindow and
    // overdraw after WM_PAINT lets the base do its system-themed render.
    type private DarkTreeViewAdvSubclass(tva: TreeViewAdv) as this =
        inherit NativeWindow()
        let WM_PAINT = 0x000F
        let attach() =
            try if tva.IsHandleCreated && this.Handle = IntPtr.Zero then this.AssignHandle(tva.Handle)
            with _ -> ()
        do
            attach()
            tva.HandleCreated.Add(fun _ -> attach())
            tva.HandleDestroyed.Add(fun _ -> try this.ReleaseHandle() with _ -> ())
        override this.WndProc(m: byref<Message>) =
            base.WndProc(&m)
            if m.Msg = WM_PAINT && tva.UseColumns then
                try
                    // Default _columnHeaderHeight in Aga is 20; widen to a
                    // safety value that comfortably covers any reasonable font
                    // so the entire system-drawn header is hidden.
                    let columnHeaderHeight = max 24 (tva.Font.Height + 8)
                    use g = Graphics.FromHwnd(tva.Handle)
                    use bg = new SolidBrush(darkPanel)
                    let headerRect = Rectangle(0, 0, tva.ClientRectangle.Width, columnHeaderHeight)
                    g.FillRectangle(bg, headerRect)
                    // Draw a subtle bottom rule for the header
                    use border = new Pen(Color.FromArgb(80, 80, 80))
                    g.DrawLine(border, 0, columnHeaderHeight - 1, tva.ClientRectangle.Width, columnHeaderHeight - 1)
                    // Column titles + per-column divider lines on the right
                    // edge of each column header.
                    let mutable x = -tva.OffsetX
                    use textBrush = new SolidBrush(darkText)
                    use dividerPen = new Pen(Color.FromArgb(80, 80, 80))
                    use format = new StringFormat()
                    format.LineAlignment <- StringAlignment.Center
                    format.Trimming <- StringTrimming.EllipsisCharacter
                    format.FormatFlags <- StringFormatFlags.NoWrap
                    for col in tva.Columns do
                        if col.IsVisible then
                            let r = RectangleF(float32 (x + 5), 0.0f, float32 (col.Width - 10), float32 (columnHeaderHeight - 1))
                            format.Alignment <-
                                match col.TextAlign with
                                | HorizontalAlignment.Right -> StringAlignment.Far
                                | HorizontalAlignment.Center -> StringAlignment.Center
                                | _ -> StringAlignment.Near
                            if not (String.IsNullOrEmpty(col.Header)) then
                                g.DrawString(col.Header, tva.Font, textBrush, r, format)
                            // Right divider — drawn in a slightly lighter
                            // tone so it's visible against darkPanel.
                            g.DrawLine(dividerPen, x + col.Width - 1, 2, x + col.Width - 1, columnHeaderHeight - 3)
                            x <- x + col.Width
                with _ -> ()

    // Long-lived brush for the inactive-selection background so we don't
    // allocate one per draw call.
    let private inactiveSelectionBrush = new SolidBrush(Color.FromArgb(60, 60, 60))

    let attachDarkTreeViewAdvOverlay (tva: TreeViewAdv) =
        // Header overpaint via NativeWindow subclass (Paint event doesn't
        // fire because TreeViewAdv.OnPaint omits the base call).
        DarkTreeViewAdvSubclass(tva) |> ignore
        // Hook DrawText on each NodeControl that's a BaseTextControl. Active
        // selection keeps system highlight colors so the focus row stays
        // distinguishable; everything else goes light. Inactive selection
        // (focus elsewhere) gets a slightly-lighter dark surface so the
        // selection state is still visible — fixes the workspace-tab "initial
        // selection looks white" issue where SystemBrushes.InactiveBorder
        // resolves to a light shade against our dark background.
        for nc in tva.NodeControls do
            match nc with
            | :? Aga.Controls.Tree.NodeControls.BaseTextControl as btc ->
                btc.DrawText.Add(fun args ->
                    match args.Context.DrawSelection with
                    | DrawSelectionMode.Active -> ()
                    | DrawSelectionMode.Inactive ->
                        args.TextColor <- darkText
                        args.BackgroundBrush <- inactiveSelectionBrush :> Brush
                    | _ -> args.TextColor <- darkText)
            | _ -> ()

    let rec attachDarkTreeViewAdvOverlayRecursive (control: Control) =
        match control with
        | :? TreeViewAdv as tva -> attachDarkTreeViewAdvOverlay tva
        | _ -> ()
        for child in control.Controls do
            attachDarkTreeViewAdvOverlayRecursive(child)

    // Helper to draw a dark-themed downward chevron (▼) glyph centered in a
    // given rectangle. Used by the ComboBox and NumericUpDown subclasses to
    // overpaint the system-themed dropdown / spinner glyphs.
    let drawDarkArrow (g: Graphics) (rect: Rectangle) (down: bool) =
        let cx = rect.X + rect.Width / 2
        let cy = rect.Y + rect.Height / 2
        let w = 4
        let pts =
            if down then
                [| Point(cx - w, cy - 2); Point(cx + w, cy - 2); Point(cx, cy + 3) |]
            else
                [| Point(cx - w, cy + 2); Point(cx + w, cy + 2); Point(cx, cy - 3) |]
        use brush = new SolidBrush(darkText)
        g.FillPolygon(brush, pts)

    // After a ComboBox paints itself, overdraw the right-edge dropdown arrow
    // area in dark and re-render the chevron in light text. The arrow rect
    // is the rightmost ~17 px of the client rectangle.
    // Full-owner-paint ComboBox subclass. Earlier versions painted darkBorder
    // AFTER base.WndProc on top of the system render, but the system's
    // hover/focus paint (a brief white frame) was visible for the frame
    // before our overpaint landed. This version skips base.WndProc for
    // WM_PAINT entirely, paints the background / border / arrow / selected
    // text from scratch, and ValidateRect's the window so the OS doesn't
    // re-issue WM_PAINT. WM_ERASEBKGND is also suppressed so the system
    // never has a chance to flash a light background.
    type private DarkComboBoxSubclass(cmb: ComboBox) as this =
        inherit NativeWindow()
        let WM_PAINT = 0x000F
        let WM_ERASEBKGND = 0x0014
        let attach() =
            try if cmb.IsHandleCreated && this.Handle = IntPtr.Zero then this.AssignHandle(cmb.Handle)
            with _ -> ()
        let paintAll() =
            try
                use g = Graphics.FromHwnd(cmb.Handle)
                let r = cmb.ClientRectangle
                // Background
                use bg = new SolidBrush(darkPanel)
                g.FillRectangle(bg, r)
                // Right-edge arrow area
                let arrowWidth = SystemInformation.HorizontalScrollBarArrowWidth
                let arrowRect = Rectangle(r.Right - arrowWidth, r.Top, arrowWidth, r.Height)
                use sep = new Pen(darkBorder)
                g.DrawLine(sep, arrowRect.Left, arrowRect.Top + 2, arrowRect.Left, arrowRect.Bottom - 2)
                drawDarkArrow g arrowRect true
                // Selected item text. For DropDownList style there is no
                // separate edit child, so we render the text ourselves to
                // ensure a fully-themed appearance with no system paint
                // leaking through.
                if cmb.SelectedIndex >= 0 then
                    let txt =
                        match cmb.Items.[cmb.SelectedIndex] with
                        | null -> ""
                        | o -> o.ToString()
                    let textRect = Rectangle(r.Left + 4, r.Top, r.Width - arrowWidth - 8, r.Height)
                    TextRenderer.DrawText(
                        g, txt, cmb.Font, textRect, darkText,
                        TextFormatFlags.Left ||| TextFormatFlags.VerticalCenter ||| TextFormatFlags.EndEllipsis)
                elif not (System.String.IsNullOrEmpty(cmb.Text)) then
                    let textRect = Rectangle(r.Left + 4, r.Top, r.Width - arrowWidth - 8, r.Height)
                    TextRenderer.DrawText(
                        g, cmb.Text, cmb.Font, textRect, darkText,
                        TextFormatFlags.Left ||| TextFormatFlags.VerticalCenter ||| TextFormatFlags.EndEllipsis)
                // Border last so it sits on top of any earlier fill.
                use border = new Pen(darkBorder)
                g.DrawRectangle(border, Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1))
            with _ -> ()
        do
            attach()
            cmb.HandleCreated.Add(fun _ -> attach())
            cmb.HandleDestroyed.Add(fun _ -> try this.ReleaseHandle() with _ -> ())
        override this.WndProc(m: byref<Message>) =
            match m.Msg with
            | msg when msg = WM_ERASEBKGND ->
                m.Result <- IntPtr(1)
            | msg when msg = WM_PAINT ->
                // Take over the paint cycle: render everything ourselves and
                // tell the OS the area is clean so no system paint runs.
                paintAll()
                ValidateRect(cmb.Handle, IntPtr.Zero) |> ignore
                m.Result <- IntPtr.Zero
            | _ ->
                base.WndProc(&m)

    // NumericUpDown outer-frame subclass: recolors the FixedSingle border
    // via WM_NCPAINT. The border lives in the NON-CLIENT area of the
    // NumericUpDown — outside the inner TextBox's client rectangle — so
    // even when the TextBox's WM_PAINT redraws on focus state change, it
    // can never erase any pixel of our border (the previous attempt
    // painted in the client area at Y=0 which the inner TextBox happily
    // overpainted on focus, leaving the user with a missing top edge).
    type private DarkNumericUpDownFrameSubclass(nud: NumericUpDown) as this =
        inherit NativeWindow()
        let WM_NCPAINT = 0x0085
        let WM_NCCALCSIZE = 0x0083
        let attach() =
            try if nud.IsHandleCreated && this.Handle = IntPtr.Zero then this.AssignHandle(nud.Handle)
            with _ -> ()
        let paintBorder() =
            try
                let hdc = GetWindowDC(nud.Handle)
                if hdc <> IntPtr.Zero then
                    try
                        use g = Graphics.FromHdc(hdc)
                        use pen = new Pen(darkBorder)
                        // Window rect, in window coordinates: (0,0) -
                        // (Width-1, Height-1). The 1-px FixedSingle border
                        // occupies the outermost ring; we draw exactly on
                        // top of it.
                        g.DrawRectangle(pen, Rectangle(0, 0, nud.Width - 1, nud.Height - 1))
                    finally
                        ReleaseDC(nud.Handle, hdc) |> ignore
            with _ -> ()
        do
            attach()
            nud.HandleCreated.Add(fun _ -> attach())
            nud.HandleDestroyed.Add(fun _ -> try this.ReleaseHandle() with _ -> ())
        override this.WndProc(m: byref<Message>) =
            base.WndProc(&m)
            if m.Msg = WM_NCPAINT then
                paintBorder()

    // NumericUpDown: the spin button child window paints its own up/down
    // arrows via the system theme. We subclass that child to overpaint in
    // dark colors. Find it via NumericUpDown.Controls (spin buttons are
    // exposed as a child Control of type System.Windows.Forms.UpDownBase+UpDownButtons).
    type private DarkUpDownButtonsSubclass(spinControl: Control) as this =
        inherit NativeWindow()
        let WM_PAINT = 0x000F
        let attach() =
            try if spinControl.IsHandleCreated && this.Handle = IntPtr.Zero then this.AssignHandle(spinControl.Handle)
            with _ -> ()
        do
            attach()
            spinControl.HandleCreated.Add(fun _ -> attach())
            spinControl.HandleDestroyed.Add(fun _ -> try this.ReleaseHandle() with _ -> ())
        override this.WndProc(m: byref<Message>) =
            base.WndProc(&m)
            if m.Msg = WM_PAINT then
                try
                    use g = Graphics.FromHwnd(spinControl.Handle)
                    let r = spinControl.ClientRectangle
                    use bg = new SolidBrush(darkPanel)
                    g.FillRectangle(bg, r)
                    let half = r.Height / 2
                    let upRect = Rectangle(r.X, r.Y, r.Width, half)
                    let downRect = Rectangle(r.X, r.Y + half, r.Width, r.Height - half)
                    use sep = new Pen(darkBorder)
                    g.DrawLine(sep, r.Left, r.Y + half, r.Right, r.Y + half)
                    g.DrawLine(sep, r.Left, r.Top, r.Left, r.Bottom)
                    drawDarkArrow g upRect false
                    drawDarkArrow g downRect true
                with _ -> ()

    // Walk a control tree and attach the ComboBox / NumericUpDown subclasses.
    let rec attachDarkSpinnerAndArrowSubclassesRecursive (control: Control) =
        try
            match control with
            | :? ComboBox as cmb -> DarkComboBoxSubclass(cmb) |> ignore
            | :? NumericUpDown as nud ->
                // Outer-frame subclass: stable 1-px grey border that doesn't
                // disappear on focus state transitions.
                DarkNumericUpDownFrameSubclass(nud) |> ignore
                // NumericUpDown's child controls include the editing TextBox
                // and a spin-buttons control. Subclass anything that isn't
                // the textbox.
                for child in nud.Controls do
                    if not (child :? TextBox) then
                        DarkUpDownButtonsSubclass(child) |> ignore
            | _ -> ()
        with _ -> ()
        for child in control.Controls do
            attachDarkSpinnerAndArrowSubclassesRecursive(child)

    // Custom ProfessionalColorTable that returns dark tones so any
    // ContextMenuStrip wearing ToolStripProfessionalRenderer renders dark.
    type private DarkColorTable() =
        inherit ProfessionalColorTable()
        override _.MenuItemSelected = darkAccent
        override _.MenuItemSelectedGradientBegin = darkAccent
        override _.MenuItemSelectedGradientEnd = darkAccent
        override _.MenuItemBorder = darkAccent
        override _.MenuBorder = darkBorder
        override _.MenuStripGradientBegin = darkPanel
        override _.MenuStripGradientEnd = darkPanel
        override _.ToolStripDropDownBackground = darkPanel
        override _.ImageMarginGradientBegin = darkPanel
        override _.ImageMarginGradientMiddle = darkPanel
        override _.ImageMarginGradientEnd = darkPanel
        override _.SeparatorDark = darkBorder
        override _.SeparatorLight = darkBorder
        override _.ToolStripBorder = darkBorder
        override _.MenuItemPressedGradientBegin = darkAccent
        override _.MenuItemPressedGradientEnd = darkAccent

    let private darkRenderer = new ToolStripProfessionalRenderer(DarkColorTable())

    let attachDarkContextMenuStripTheme (cms: ContextMenuStrip) =
        cms.Renderer <- darkRenderer
        cms.BackColor <- darkPanel
        cms.ForeColor <- darkText
        for item in cms.Items do
            try
                item.BackColor <- darkPanel
                item.ForeColor <- darkText
            with _ -> ()

    // Walk the entire form tree finding ContextMenuStrip instances. They live
    // off the Control.ContextMenuStrip property AND off DropdownButton's menu
    // (a custom class — not directly on the visual tree). For the latter we
    // catch any ToolStripDropDown known to the WinForms message loop.
    let rec attachDarkContextMenuStripsRecursive (control: Control) =
        try
            if not (isNull control.ContextMenuStrip) then
                attachDarkContextMenuStripTheme control.ContextMenuStrip
        with _ -> ()
        for child in control.Controls do
            attachDarkContextMenuStripsRecursive(child)

    // StatusBar owner-draw — the legacy StatusBar panels paint with system
    // raised borders and can leak system colors. Switching each panel to
    // OwnerDraw and rendering ourselves guarantees a dark surface with light
    // text regardless of system theme.
    let attachDarkStatusBarOwnerDraw (sb: StatusBar) =
        for panel in sb.Panels do
            try panel.Style <- StatusBarPanelStyle.OwnerDraw with _ -> ()
        sb.DrawItem.Add(fun e ->
            let g = e.Graphics
            use brush = new SolidBrush(darkSurface)
            g.FillRectangle(brush, e.Bounds)
            let panel = e.Panel
            if not (String.IsNullOrEmpty(panel.Text)) then
                use textBrush = new SolidBrush(darkText)
                use format = new StringFormat()
                format.LineAlignment <- StringAlignment.Center
                format.Trimming <- StringTrimming.EllipsisCharacter
                let r = RectangleF(float32 (e.Bounds.X + 2), float32 e.Bounds.Y, float32 (e.Bounds.Width - 4), float32 e.Bounds.Height)
                g.DrawString(panel.Text, sb.Font, textBrush, r, format))

    let rec attachDarkStatusBarOwnerDrawRecursive (control: Control) =
        match control with
        | :? StatusBar as sb -> attachDarkStatusBarOwnerDraw sb
        | _ -> ()
        for child in control.Controls do
            attachDarkStatusBarOwnerDrawRecursive(child)

    // Branch-12 entry point: branch-9 + the spinner / arrow subclasses + the
    // ContextMenuStrip dark renderer + the inactive-selection brush update
    // that ships in attachDarkTreeViewAdvOverlay above.
    let applyDarkThemeBranch12ToForm (form: Form) (enabled: bool) =
        if enabled then
            setPreferredAppModeForceDark()
            useImmersiveDarkMode form.Handle true |> ignore
            form.BackColor <- darkSurface
            form.ForeColor <- darkText
            for child in form.Controls do
                applyDarkColorsToControl(child)
                attachDarkOwnerDrawHandlers(child)
            for child in form.Controls do
                applyDarkNativeThemeToControl(child)
            for child in form.Controls do
                applyDarkExtraTreatments(child)
            for child in form.Controls do
                attachDarkBackgroundSubclassRecursive(child)
            for child in form.Controls do
                attachDarkTreeViewAdvOverlayRecursive(child)
            for child in form.Controls do
                attachDarkSpinnerAndArrowSubclassesRecursive(child)
            for child in form.Controls do
                attachDarkContextMenuStripsRecursive(child)
            for child in form.Controls do
                invalidateTreeViewAdvs(child)
            form.Invalidate(true)

    // Phase-1 entry point: only sets BackColor / ForeColor on the form and
    // recursively on every child control. No handle-dependent operations
    // (SetWindowTheme, NativeWindow subclass attachments, etc.) so this can
    // be safely invoked BEFORE form.Show(), eliminating the brief light->
    // dark flicker the user otherwise sees on dialog open.
    let applyDarkColorsBeforeShow (form: Form) =
        form.BackColor <- darkSurface
        form.ForeColor <- darkText
        for child in form.Controls do
            applyDarkColorsToControl(child)

    // Branch-15 entry point: identical pipeline to branch 13 but expects
    // applyDarkColorsBeforeShow to have already run during DesktopManagerForm
    // construction so the form is born dark and there's no light->dark
    // flicker visible to the user.
    let applyDarkThemeBranch15ToForm (form: Form) (enabled: bool) =
        if enabled then
            setPreferredAppModeForceDark()
            useImmersiveDarkMode form.Handle true |> ignore
            // Re-apply colors here (in addition to the pre-show pass) in case
            // any control was created lazily after the early call.
            form.BackColor <- darkSurface
            form.ForeColor <- darkText
            for child in form.Controls do
                applyDarkColorsToControl(child)
                attachDarkOwnerDrawHandlers(child)
            for child in form.Controls do
                applyDarkNativeThemeToControl(child)
            for child in form.Controls do
                applyDarkExtraTreatments(child)
            for child in form.Controls do
                attachDarkBackgroundSubclassRecursive(child)
            for child in form.Controls do
                attachDarkTreeViewAdvOverlayRecursive(child)
            for child in form.Controls do
                attachDarkSpinnerAndArrowSubclassesRecursive(child)
            for child in form.Controls do
                attachDarkContextMenuStripsRecursive(child)
            for child in form.Controls do
                attachDarkStatusBarOwnerDrawRecursive(child)
            for child in form.Controls do
                invalidateTreeViewAdvs(child)
            form.Invalidate(true)

    // Branch-13 entry point: branch 12 plus the TabControl frame killer
    // (DarkTabControlFrameSubclass attached in attachDarkBackgroundSubclassRecursive)
    // and the StatusBar owner-draw pass.
    let applyDarkThemeBranch13ToForm (form: Form) (enabled: bool) =
        if enabled then
            setPreferredAppModeForceDark()
            useImmersiveDarkMode form.Handle true |> ignore
            form.BackColor <- darkSurface
            form.ForeColor <- darkText
            for child in form.Controls do
                applyDarkColorsToControl(child)
                attachDarkOwnerDrawHandlers(child)
            for child in form.Controls do
                applyDarkNativeThemeToControl(child)
            for child in form.Controls do
                applyDarkExtraTreatments(child)
            for child in form.Controls do
                attachDarkBackgroundSubclassRecursive(child)
            for child in form.Controls do
                attachDarkTreeViewAdvOverlayRecursive(child)
            for child in form.Controls do
                attachDarkSpinnerAndArrowSubclassesRecursive(child)
            for child in form.Controls do
                attachDarkContextMenuStripsRecursive(child)
            for child in form.Controls do
                attachDarkStatusBarOwnerDrawRecursive(child)
            for child in form.Controls do
                invalidateTreeViewAdvs(child)
            form.Invalidate(true)

    // Branch-9 entry point: branch 7 plus the TreeViewAdv overlay (column
    // header overpaint + DrawText-event cell text tinting), the dark
    // WM_ERASEBKGND subclass for TabControls, StatusBar handling (added in
    // applyDarkColorsToControl), and ComboBox dropdown theming via the
    // DropDown event hook.
    let applyDarkThemeAggressivelyToForm (form: Form) (enabled: bool) =
        if enabled then
            setPreferredAppModeForceDark()
            useImmersiveDarkMode form.Handle true |> ignore
            form.BackColor <- darkSurface
            form.ForeColor <- darkText
            for child in form.Controls do
                applyDarkColorsToControl(child)
                attachDarkOwnerDrawHandlers(child)
            for child in form.Controls do
                applyDarkNativeThemeToControl(child)
            for child in form.Controls do
                applyDarkExtraTreatments(child)
            for child in form.Controls do
                attachDarkBackgroundSubclassRecursive(child)
            for child in form.Controls do
                attachDarkTreeViewAdvOverlayRecursive(child)
            for child in form.Controls do
                invalidateTreeViewAdvs(child)
            form.Invalidate(true)

// A NodeCheckBox subclass that paints itself in our dark palette so the
// Programs-tab Tabs / AutoGrouping / Category columns match the standard
// CheckBox dark style instead of the system-themed white square.
type DarkNodeCheckBox() =
    inherit Aga.Controls.Tree.NodeControls.NodeCheckBox()
    override this.Draw(node: Aga.Controls.Tree.TreeNodeAdv, context: Aga.Controls.Tree.DrawContext) =
        let bounds = this.GetBounds(node, context)
        let state = this.GetCheckState(node)
        let g = context.Graphics
        let imgSize = Aga.Controls.Tree.NodeControls.NodeCheckBox.ImageSize
        let rect = System.Drawing.Rectangle(bounds.X, bounds.Y, imgSize, imgSize)
        // Box background — slightly lighter grey when checked, near-black
        // otherwise. Matches the user's preferred "grey box with a check"
        // look for NodeCheckBox in the Programs tab columns rather than the
        // accent-blue checked state used by colored selection highlights.
        let bgColor =
            if state = System.Windows.Forms.CheckState.Checked then DarkMode.darkPanel
            else System.Drawing.Color.FromArgb(50, 50, 50)
        use bg = new System.Drawing.SolidBrush(bgColor)
        g.FillRectangle(bg, rect)
        // Grey border — same tone as the standard CheckBox chrome.
        use border = new System.Drawing.Pen(System.Drawing.Color.FromArgb(120, 120, 120))
        g.DrawRectangle(border, rect)
        // Check / indeterminate glyph in light text color.
        match state with
        | System.Windows.Forms.CheckState.Checked ->
            use checkPen = new System.Drawing.Pen(DarkMode.darkText, 2.0f)
            checkPen.StartCap <- System.Drawing.Drawing2D.LineCap.Round
            checkPen.EndCap <- System.Drawing.Drawing2D.LineCap.Round
            g.DrawLines(checkPen, [|
                System.Drawing.Point(rect.X + 3, rect.Y + 6)
                System.Drawing.Point(rect.X + 5, rect.Y + 9)
                System.Drawing.Point(rect.X + 10, rect.Y + 3)
            |])
        | System.Windows.Forms.CheckState.Indeterminate ->
            use fillBrush = new System.Drawing.SolidBrush(DarkMode.darkText)
            g.FillRectangle(fillBrush, System.Drawing.Rectangle(rect.X + 3, rect.Y + 5, rect.Width - 6, 3))
        | _ -> ()

module DarkModeFactory =
    /// Create a NodeCheckBox — DarkNodeCheckBox if dark mode is on,
    /// the stock NodeCheckBox otherwise.
    let makeNodeCheckBox () : Aga.Controls.Tree.NodeControls.NodeCheckBox =
        if DarkMode.darkModeEnabled then
            DarkNodeCheckBox() :> Aga.Controls.Tree.NodeControls.NodeCheckBox
        else
            Aga.Controls.Tree.NodeControls.NodeCheckBox()
