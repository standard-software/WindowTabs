namespace Bemo
open System
open System.Drawing
open System.Runtime.InteropServices
open System.Windows.Forms

module DarkMode =
    // Dark theme palette inspired by Win11 / VS Dark.
    let darkSurface = Color.FromArgb(32, 32, 32)
    let darkPanel = Color.FromArgb(45, 45, 45)
    let darkBorder = Color.FromArgb(64, 64, 64)
    let darkText = Color.FromArgb(240, 240, 240)
    let darkAccent = Color.FromArgb(0, 120, 212)

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
