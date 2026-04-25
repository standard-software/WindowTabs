namespace Bemo
open System
open System.Drawing
open System.Runtime.InteropServices
open System.Windows.Forms
open Aga.Controls.Tree

module DarkMode =
    // Dark theme palette inspired by Win11 / VS Dark.
    let darkSurface = Color.FromArgb(32, 32, 32)
    let darkPanel = Color.FromArgb(45, 45, 45)
    let darkBorder = Color.FromArgb(64, 64, 64)
    let darkText = Color.FromArgb(240, 240, 240)
    let darkAccent = Color.FromArgb(0, 120, 212)

    // The shipped Aga.Controls.dll is a prebuilt binary so we can't add a
    // dark-mode flag to its source. Instead we paint over the column headers
    // ourselves via the public Paint event and tint cell text via the public
    // BaseTextControl.DrawText event (see attachDarkTreeViewAdvOverlay).

    [<DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")>]
    extern int private DwmSetWindowAttributeNative(IntPtr hwnd, int attr, int& pvAttribute, int cbAttribute)

    [<DllImport("uxtheme.dll", CharSet = CharSet.Unicode, EntryPoint = "SetWindowTheme")>]
    extern int private SetWindowTheme(IntPtr hwnd, string pszSubAppName, string pszSubIdList)

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
                tb.BorderStyle <- BorderStyle.FixedSingle
            | :? NumericUpDown as nud ->
                nud.BackColor <- darkPanel
                nud.ForeColor <- darkText
                nud.BorderStyle <- BorderStyle.FixedSingle
            | :? Button as btn ->
                btn.BackColor <- darkPanel
                btn.ForeColor <- darkText
                btn.FlatStyle <- FlatStyle.Flat
                btn.FlatAppearance.BorderColor <- darkBorder
            | :? CheckBox as cb ->
                cb.BackColor <- darkSurface
                cb.ForeColor <- darkText
            | :? RadioButton as rb ->
                rb.BackColor <- darkSurface
                rb.ForeColor <- darkText
            | :? ComboBox as cmb ->
                cmb.BackColor <- darkPanel
                cmb.ForeColor <- darkText
                cmb.FlatStyle <- FlatStyle.Flat
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
                tva.LineColor <- darkBorder
            | :? Label as lbl ->
                lbl.BackColor <- darkSurface
                lbl.ForeColor <- darkText
            | :? GroupBox as gb ->
                gb.BackColor <- darkSurface
                gb.ForeColor <- darkText
            | :? TabControl as tc ->
                tc.BackColor <- darkSurface
                tc.ForeColor <- darkText
            | :? TabPage as tp ->
                tp.BackColor <- darkSurface
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
                let appName =
                    match control with
                    | :? ComboBox -> "DarkMode_CFD"
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

    // Owner-draw the tab headers of a TabControl so the strip behind the tab
    // pages and the tab labels themselves render in dark colors. Without this
    // the headers stay system-themed (light) even after BackColor is set.
    let attachDarkTabControlOwnerDraw (tabControl: TabControl) =
        tabControl.DrawMode <- TabDrawMode.OwnerDrawFixed
        tabControl.DrawItem.Add(fun e ->
            let g = e.Graphics
            let isSelected = (e.Index = tabControl.SelectedIndex)
            let bgColor = if isSelected then darkPanel else darkSurface
            use bgBrush = new SolidBrush(bgColor)
            g.FillRectangle(bgBrush, e.Bounds)
            if e.Index >= 0 && e.Index < tabControl.TabPages.Count then
                let txt = tabControl.TabPages.[e.Index].Text
                use textBrush = new SolidBrush(darkText)
                use format = new StringFormat()
                format.Alignment <- StringAlignment.Center
                format.LineAlignment <- StringAlignment.Center
                let rect = RectangleF(float32 e.Bounds.X, float32 e.Bounds.Y, float32 e.Bounds.Width, float32 e.Bounds.Height)
                g.DrawString(txt, tabControl.Font, textBrush, rect, format)
            // Subtle bottom rule for the selected tab so it stands out
            if isSelected then
                use accentPen = new Pen(darkAccent, 2.0f)
                g.DrawLine(accentPen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1))

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
        do
            let attach() = if target.IsHandleCreated then this.AssignHandle(target.Handle)
            attach()
            target.HandleCreated.Add(fun _ -> attach())
            target.HandleDestroyed.Add(fun _ -> this.ReleaseHandle())
        override this.WndProc(m: byref<Message>) =
            if m.Msg = WM_ERASEBKGND then
                try
                    use g = Graphics.FromHdc(m.WParam)
                    use brush = new SolidBrush(darkSurface)
                    g.FillRectangle(brush, target.ClientRectangle)
                    m.Result <- IntPtr(1)
                with _ ->
                    base.WndProc(&m)
            else
                base.WndProc(&m)

    // Walk the control tree and attach the dark-erase subclass to TabControls
    // and to any other control where the system paints a light background that
    // can't be covered via BackColor alone (notably TreeViewAdv's scrollbar
    // box corner).
    let rec attachDarkBackgroundSubclassRecursive (control: Control) =
        match control with
        | :? TabControl ->
            DarkBackgroundSubclass(control) |> ignore
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

    // Overpaint the column header band of a TreeViewAdv with dark, then
    // re-render each column title in light text. The Paint event runs after
    // the system already drew the light header, so we cover it. Also subscribe
    // to BaseTextControl.DrawText on each NodeControl so cell text uses light
    // colors when not selected.
    let attachDarkTreeViewAdvOverlay (tva: TreeViewAdv) =
        // Header overpaint
        tva.Paint.Add(fun e ->
            // ColumnHeaderHeight is internal in Aga.Controls so we approximate
            // it from the font (matches the default _columnHeaderHeight which
            // is set to Font.Height + ~4).
            let columnHeaderHeight = tva.Font.Height + 4
            if tva.UseColumns && columnHeaderHeight > 0 then
                let g = e.Graphics
                use bg = new SolidBrush(darkPanel)
                let headerRect = Rectangle(0, 0, tva.ClientRectangle.Width, columnHeaderHeight)
                g.FillRectangle(bg, headerRect)
                use border = new Pen(darkBorder)
                g.DrawLine(border, 0, columnHeaderHeight - 1, tva.ClientRectangle.Width, columnHeaderHeight - 1)
                // Re-draw column titles
                let mutable x = -tva.OffsetX
                use textBrush = new SolidBrush(darkText)
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
                        x <- x + col.Width)
        // Hook DrawText on each NodeControl that's a BaseTextControl
        for nc in tva.NodeControls do
            match nc with
            | :? Aga.Controls.Tree.NodeControls.BaseTextControl as btc ->
                btc.DrawText.Add(fun args ->
                    if args.Context.DrawSelection = DrawSelectionMode.None then
                        args.TextColor <- darkText
                    elif args.Context.DrawSelection = DrawSelectionMode.Inactive then
                        args.TextColor <- darkText)
            | _ -> ()

    let rec attachDarkTreeViewAdvOverlayRecursive (control: Control) =
        match control with
        | :? TreeViewAdv as tva -> attachDarkTreeViewAdvOverlay tva
        | _ -> ()
        for child in control.Controls do
            attachDarkTreeViewAdvOverlayRecursive(child)

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
