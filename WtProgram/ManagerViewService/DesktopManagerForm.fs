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
        let form = Form()
        // Restore the 96-dpi UI font: the DPI manifest makes .NET report a
        // 25%-larger default, which overflows this dialog's fixed 250-px label
        // column. See Dpi.applyLegacyDialogFont.
        Dpi.applyLegacyDialogFont(form)
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
        form.StartPosition <- FormStartPosition.CenterScreen
        form.Size <- Size(800, 600)
        form.Text <- title
        form.Icon <- Services.openIcon("Bemo.ico")
        form.TopMost <- true
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

