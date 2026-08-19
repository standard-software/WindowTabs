namespace Bemo
open System
open System.Drawing
open System.IO
open System.Windows.Forms
open System.Threading
open Bemo.Win32.Forms

module DesktopManagerFormState =
    let mutable currentForm : Form option = None
    let mutable mutex : Mutex option = None

// The settings window is a Form subclass for one reason: WM_DPICHANGED.
//
// The process is Per-Monitor-V2, so when the user drags the dialog onto a
// monitor with a different scale Windows sends WM_DPICHANGED with the new DPI
// and a suggested rectangle, and expects the window to re-lay itself out and
// move there. Nothing does that by default here: .NET Framework 4.7's own
// WinForms high-DPI support is deliberately left switched off (App.config
// declares no System.Windows.Forms.ApplicationConfigurationSection, because
// enabling it would let WinForms scale this dialog a second time on top of
// SettingsDpi, off the system DPI rather than the monitor's). Without this
// handler the window would keep its old physical size - still sharp, but a
// third too small or too large for the monitor it moved to.
//
// The re-scale is applied as a RELATIVE factor (new/old) so a column the user
// has dragged wider keeps its proportion; the metrics SettingsDpi owns
// outright are recomputed absolutely from the new scale, so repeated crossings
// cannot accumulate rounding in them.
type SettingsDialogWindow(initialScale: float) =
    inherit Form()

    let WM_DPICHANGED = 0x02E0
    // Sent only while the user is dragging a border or corner (and by the
    // keyboard Size command), never for a move and never for a size this class
    // assigns itself - which is exactly the distinction the design size needs.
    let WM_SIZING = 0x0214
    let mutable scale = initialScale
    // WM_DPICHANGED arrives again while we move the window into the suggested
    // rectangle if that move crosses a monitor boundary. Re-entering here
    // would scale twice for one physical change.
    let mutable rescaling = false
    let mutable userSizing = false

    member private this.rescale(newScale: float, suggested: Rect) =
        if newScale > 0.0 && newScale <> scale && not rescaling then
            rescaling <- true
            this.SuspendLayout()
            try
                try
                    SettingsDpi.applyScale this scale newScale
                    scale <- newScale
                with _ -> ()
            finally
                this.ResumeLayout(true)
            // The SIZE is recomputed from the 96-dpi design at the new scale
            // rather than taken from the rectangle Windows suggests. The
            // suggested rectangle is this window's CURRENT size multiplied by
            // the DPI ratio, so it carries any working-area clamp with it:
            // a dialog opened at 175%, where 1400 x 1050 does not fit and is
            // shown as 1400 x 996, became 1000 x 711 at 125% instead of
            // 1000 x 750 and stayed that shape for the rest of its life. See
            // SettingsDpi.setWindowDesign.
            //
            // The suggested POSITION is kept - that is where Windows wants the
            // window, and it is what keeps it under the pointer during a drag -
            // and placeWindow clamps the result back into the monitor, so a
            // size that does not fit the new monitor is still shown whole.
            try SettingsDpi.placeWindow this (Rect(suggested.TL, SettingsDpi.windowSizeAt this newScale))
            with _ -> ()
            rescaling <- false

    // A size the user dragged the border to REPLACES the design size, so their
    // choice survives the move to another monitor exactly the way the original
    // 800 x 600 does. Gated on WM_SIZING so that neither a plain move nor the
    // re-size above can be mistaken for one, and skipped while maximised,
    // where the size is the monitor's rather than one anybody chose.
    override this.OnResizeEnd(e: EventArgs) =
        base.OnResizeEnd(e)
        if userSizing then
            userSizing <- false
            if not rescaling && this.WindowState = FormWindowState.Normal then
                SettingsDpi.adoptWindowSize this scale

    override this.WndProc(m: byref<Message>) =
        if m.Msg = WM_SIZING && not rescaling then userSizing <- true
        if m.Msg = WM_DPICHANGED then
            try
                // wParam packs the new DPI into both halves; lParam points at
                // the rectangle Windows wants the window moved to.
                let dpi = float (int (m.WParam.ToInt64() &&& 0xFFFFL))
                let suggested =
                    if m.LParam = IntPtr.Zero then this.Bounds.Rect
                    else (Runtime.InteropServices.Marshal.PtrToStructure(m.LParam, typeof<RECT>) :?> RECT).Rect
                this.rescale(dpi / Dpi.BaseDpi, suggested)
            with _ -> ()
        base.WndProc(&m)

type DesktopManagerForm() =
    // Flip code-path flags BEFORE the views are constructed so child
    // controls are born in their dark-aware variants:
    //  - HotKeyControl: managed (TextBox-based) path instead of comctl32
    //    "msctls_hotkey32" common control.
    //  - DropdownButton: ContextMenuStrip auto-themed with the dark renderer.
    //  - DarkMode.darkModeEnabled: read by DarkModeFactory.makeNodeCheckBox so
    //    NodeCheckBox columns in the Programs tab use the dark variant.
    do
        try
            let darkOn =
                match Services.settings.root.getBool("EnableDarkMode") with
                | Some(value) -> value
                | None -> false
            Bemo.Win32.HotKeyControl.UseManaged <- darkOn
            Bemo.DropdownButton.UseDarkMode <- darkOn
            Bemo.DarkMode.darkModeEnabled <- darkOn
            // Localized "no hotkey set" label. Read here so newly-constructed
            // HotKeyControls use the current language. Localization.getString
            // falls back to the English default "None" when the key is missing.
            Bemo.Win32.HotKeyControl.NoneLabel <- Localization.getString("HotKeyNone")
        with _ -> ()

    // Probe for the monitor the dialog will open on, taken BEFORE the views
    // are built so everything constructed below already sees the right scale.
    //
    // FormStartPosition.CenterScreen used to choose that monitor: for a form
    // with no owner, Form.CenterToScreen centres on the screen under the mouse
    // pointer, which is why the dialog already appears where the tray icon or
    // the tab context menu was clicked. The same probe is used here, and the
    // window is then positioned explicitly - which is also what makes the
    // working-area clamp further down possible.
    let openProbe = Control.MousePosition.Pt
    let openScale = SettingsDpi.forCursor()
    do SettingsDpi.setCurrent(openScale)

    let title = sprintf "WindowTabs Settings (version %s)"  (Services.program.version)
    let tabs = List2([
        ProgramView() :> ISettingsView
        AppearanceView() :> ISettingsView
        HotKeyView() :> ISettingsView
        WorkspaceView() :> ISettingsView
        // DiagnosticsView() :> ISettingsView  // Scan tab hidden
        ])
    let tabControl : TabControl = {
        new TabControl() with
            override this.OnKeyDown(e:KeyEventArgs) =
                if (e.KeyData = (Keys.Control ||| Keys.PageDown) ||
                    e.KeyData = (Keys.Control  ||| Keys.PageUp)) then
                    ()
                else
                    base.OnKeyDown(e)
        }

    let isDarkModeEnabled() =
        try
            match Services.settings.root.getBool("EnableDarkMode") with
            | Some(value) -> value
            | None -> false
        with _ -> false

    let form =
        // Held as a plain Form from here on: the subclass exists only to catch
        // WM_DPICHANGED, which is virtual dispatch and needs no cooperation
        // from this side.
        let form = (new SettingsDialogWindow(openScale)) :> Form
        // The window's size in 96-dpi design units. Used twice below - to build
        // the window in design units, and to register what every later scale is
        // computed from - so the two cannot drift apart.
        let designSize = Sz(800, 600)
        tabs.iter <| fun view ->
            let page = TabPage(view.title)
            let control = view.control
            control.Dock <- DockStyle.Fill
            page.Controls.Add(control)
            page.Dock <- DockStyle.Fill
            tabControl.TabPages.Add(page)
        tabControl.Dock <- DockStyle.Fill
        form.Controls.Add(tabControl)
        form.FormBorderStyle <- FormBorderStyle.SizableToolWindow
        form.Size <- designSize.Size
        form.Text <- title
        form.Icon <- Services.openIcon("Bemo.ico")
        form.TopMost <- true
        // Turn the 96-dpi design into device pixels for the monitor the dialog
        // is opening on. Everything the window contains exists by now.
        //
        // It has NOT been laid out, though - the size assignment above only
        // marks the tree dirty, and a TabControl lays out one page anyway - so
        // applyScale starts by laying the window out itself. The second
        // generation left that to WinForms on the grounds that this is the
        // point at which a form auto-scales; it is not, PerformAutoScale runs
        // from OnLayout and handle creation, and the missing layout pass is
        // what turned the dark-mode check box into a 1-px line at 125% and
        // above. See SettingsDpi.settleLayout for the measurements.
        SettingsDpi.applyScale form 1.0 openScale
        // Place the window by hand instead of leaving it to CenterScreen.
        //
        // Two reasons. The size has to be CLAMPED to the monitor first -
        // against the FULL monitor rectangle, not the working area: the user
        // accepts the bottom edge lying over the taskbar, and clamping to the
        // ~996-px working area is what cut the 175% dialog to 1400 x 996 and
        // put a scrollbar on every tab. 800 x 600 at 175% is 1400 x 1050,
        // which fits the 1920 x 1080 monitor whole. And Control.Scale sized
        // the frame through AdjustWindowRectEx, which answers for the system
        // DPI rather than the target monitor's, so the exact design size is
        // set here instead. Content that no longer fits a smaller monitor
        // still scrolls: every tab is either an AutoScroll TableLayoutPanel
        // or a TreeViewAdv.
        //
        // The 800 x 600 is registered as the window's design size BEFORE the
        // clamp, and it is the design size - not the clamped result - that
        // every later monitor change is computed from. Without that, opening
        // on a monitor too short for the dialog left it permanently short on
        // the roomier ones as well.
        form.StartPosition <- FormStartPosition.Manual
        SettingsDpi.setWindowDesign form designSize
        SettingsDpi.placeWindow form
            (SettingsDpi.centeredAt openProbe (SettingsDpi.windowSizeAt form openScale))
        // Branch 16 (dark-mode-16): apply colors during construction so the
        // form is born dark. The full theme (handle-dependent subclasses
        // etc.) is applied just before form.Show() in member this.show()
        // — see below — so the dialog never paints in system colors.
        if isDarkModeEnabled() then
            DarkMode.applyDarkColorsBeforeShow form
        form.FormClosed.Add(fun _ ->
            DesktopManagerFormState.currentForm <- None
            // Release mutex when form is closed
            match DesktopManagerFormState.mutex with
            | Some m -> 
                try
                    m.ReleaseMutex()
                    m.Dispose()
                with _ -> ()
                DesktopManagerFormState.mutex <- None
            | None -> ()
        )
        form

    // Acquire the single-instance mutex. If the named mutex already exists
    // (mutexCreated=false), DISPOSE the just-created non-owning handle
    // immediately and leave State.mutex untouched — overwriting it would
    // orphan any existing M1 ownership and re-introduce the already-fixed
    // "dialog won't reopen" class of bugs. Returns true when ownership is
    // successfully acquired and stored.
    let tryAcquireSingleInstanceMutex () =
        let mutexCreated = ref false
        try
            let m = new Mutex(true, "WindowTabsSettingsDialog", mutexCreated)
            if !mutexCreated then
                DesktopManagerFormState.mutex <- Some(m)
                true
            else
                // Another holder exists: don't leak the handle, don't clobber
                // State.mutex.
                try m.Dispose() with _ -> ()
                false
        with _ ->
            // Mutex construction itself failed — fall through to "show
            // anyway" rather than leaving the user with no dialog.
            true

    let showFormCommon () =
        // Anti-flicker: hide via Opacity=0 while we Show + apply the dark
        // theme, then bump Opacity back to 1. This lets the form paint its
        // initial system frame off-screen (invisible) so the user only ever
        // sees the fully-themed dark dialog.
        if isDarkModeEnabled() then
            form.Opacity <- 0.0
            form.Show()
            form.CreateControl()
            DarkMode.applyDarkThemeBranch15ToForm form true
            form.Refresh()
            form.Opacity <- 1.0
        else
            form.Show()
        form.Activate()

    member this.show() =
        if tryAcquireSingleInstanceMutex() then
            DesktopManagerFormState.currentForm <- Some(form)
            showFormCommon()

    member this.showView(view) =
        if tryAcquireSingleInstanceMutex() then
            let tabIndex = tabs.findIndex(fun tab -> tab.key = view)
            tabControl.SelectedIndex <- tabIndex
            DesktopManagerFormState.currentForm <- Some(form)
            showFormCommon()

