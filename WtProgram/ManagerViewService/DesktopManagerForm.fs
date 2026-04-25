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
            match Services.settings.root.getBool("EnableMenuDarkMode") with
            | Some(value) -> value
            | None -> false
        with _ -> false

    let form =
        let form = Form()
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
        // Branch 9 (dark-mode-9): branch 7 plus targeted source-level fixes
        // for TreeViewAdv (column headers + node text via a dark-mode flag in
        // Aga.Controls), a NativeWindow subclass that fills the TabControl
        // background dark on WM_ERASEBKGND so the gaps around the tab strip
        // and below selected tabs go dark, StatusBar coloring, and a ComboBox
        // drop-down theming hook (popup listbox is its own HWND so it needs
        // SetWindowTheme separately).
        form.Shown.Add(fun _ ->
            DarkMode.applyDarkThemeAggressivelyToForm form (isDarkModeEnabled()))
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

    member this.show() =
        // Try to create mutex for single instance
        let mutexCreated = ref false
        try
            DesktopManagerFormState.mutex <- Some(new Mutex(true, "WindowTabsSettingsDialog", mutexCreated))
            if not !mutexCreated then
                // Another instance exists, don't show
                ()
            else
                DesktopManagerFormState.currentForm <- Some(form)
                form.Show()
                form.Activate()
        with
        | _ -> 
            // If mutex creation fails, just show the form
            DesktopManagerFormState.currentForm <- Some(form)
            form.Show()
            form.Activate()

    member this.showView(view) =
        // Try to create mutex for single instance
        let mutexCreated = ref false
        try
            DesktopManagerFormState.mutex <- Some(new Mutex(true, "WindowTabsSettingsDialog", mutexCreated))
            if not !mutexCreated then
                // Another instance exists, don't show
                ()
            else
                let tabIndex = tabs.findIndex(fun tab -> tab.key = view)
                tabControl.SelectedIndex <- tabIndex
                DesktopManagerFormState.currentForm <- Some(form)
                form.Show()
                form.Activate()
        with
        | _ -> 
            // If mutex creation fails, just show the form
            let tabIndex = tabs.findIndex(fun tab -> tab.key = view)
            tabControl.SelectedIndex <- tabIndex
            DesktopManagerFormState.currentForm <- Some(form)
            form.Show()
            form.Activate()
        
    member this.close() =
        form.Close()
        DesktopManagerFormState.currentForm <- None
        // Release mutex
        match DesktopManagerFormState.mutex with
        | Some m -> 
            try
                m.ReleaseMutex()
                m.Dispose()
            with _ -> ()
            DesktopManagerFormState.mutex <- None
        | None -> ()
        
