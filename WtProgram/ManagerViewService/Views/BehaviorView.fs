namespace Bemo
open System
open System.Drawing
open System.IO
open System.Windows.Forms
open Bemo.Win32
open Bemo.Win32.Forms


type InteractionView() =
    let mutable refreshing = false
    let refreshers = ResizeArray<unit -> unit>()
    let save key value =
        if not refreshing then Services.settings.setValue(key, value)

    let settingsProperty name =
        {
            new IProperty<'a> with
                member x.value
                    with get() = unbox<'a>(Services.settings.getValue(name))
                    and set(value) = save name (box(value))
        }
        

    // Next Tab / Previous Tab and the Ctrl+1 .. Ctrl+9 check box used to be
    // rows of this tab; they are on the Shortcut Keys tab now
    // (ShortcutKeysView), under the same settings keys.
    let table =
        
        let checkBox (prop:IProperty<bool>) = 
            let checkbox = BoolEditor() :> IPropEditor
            checkbox.value <- box(prop.value)
            checkbox.changed.Add <| fun() ->
                if not refreshing then prop.value <- unbox<bool>(checkbox.value)
            refreshers.Add(fun () -> checkbox.value <- box(prop.value))
            checkbox.control

        let settingsCheckbox key = checkBox(settingsProperty(key))
        
        let settingsRadios key normalize options =
            UIHelper.settingsRadios refreshers save key normalize options

        let buttonModeRadios key captions =
            List.zip [TabButtonPolicy.activeOnly; TabButtonPolicy.all; TabButtonPolicy.none] captions
            |> settingsRadios key TabButtonPolicy.normalize

        let snapChangeTabPositionRadios =
            settingsRadios "changeTabPositionOnSnap"
                (fun mode -> if mode = "nochange" then "nochange" else "change")
                ["change", "ChangeTabPositionOnSnapWhenUniform"; "nochange", "ChangeTabPositionOnSnapNever"]

        let lockPositionCheckbox = settingsCheckbox "lockWindowPosition"
        let gap = new Panel()
        let fields = List2([
            ("TabCloseButton", buttonModeRadios "tabCloseButton" ["TabCloseActiveOnly"; "TabCloseAll"; "TabCloseNone"])
            ("TabPinButton", buttonModeRadios "tabPinButton" ["TabPinActiveOnly"; "TabPinAll"; "TabPinNone"])
            ("EnableHoverActivate", settingsCheckbox "enableHoverActivate")
            ("LockWindowPosition", lockPositionCheckbox)
            ("", gap :> Control)
            ("ChangeTabPositionOnSnap", snapChangeTabPositionRadios)
            ("SnapTabHeightMargin", settingsCheckbox "snapTabHeightMargin")
            ("SnapOnDragDetach", settingsCheckbox "snapOnDragDetach")
        ])

        let formPanel = UIHelper.form fields
        // Radio groups already include their row spacing.
        for row in [0; 1; 5] do
            formPanel.GetControlFromPosition(1, row).Margin <- Padding(0)
        // An empty row avoids the caption's full-row minimum height.
        let gapRow = 4
        for column in 0 .. 1 do
            let control = formPanel.GetControlFromPosition(column, gapRow)
            formPanel.Controls.Remove(control)
            control.Dispose()
        formPanel.RowStyles.[gapRow].SizeType <- SizeType.Absolute
        formPanel.RowStyles.[gapRow].Height <- float32 (UIHelper.settingsRowHeightPx / 2)

        formPanel.Dock <- DockStyle.Fill
        formPanel.Padding <- UIHelper.settingsPagePadding
        formPanel

    member this.refresh() =
        let previous = refreshing
        refreshing <- true
        try
            for refresh in refreshers do refresh()
        finally
            refreshing <- previous

    interface ISettingsView with
        member x.key = SettingsViewType.InteractionSettings
        member x.title = Localization.getString("Interaction")
        member x.control = table :> Control


type GeneralView() =
    let mutable refreshing = false
    let refreshers = ResizeArray<unit -> unit>()
    let checkBox (prop: IProperty<bool>) =
        let editor = BoolEditor() :> IPropEditor
        editor.value <- box prop.value
        editor.changed.Add(fun () -> if not refreshing then prop.value <- unbox<bool> editor.value)
        refreshers.Add(fun () -> editor.value <- box prop.value)
        editor.control
    let settingsCheckbox key =
        checkBox {
            new IProperty<bool> with
                member _.value
                    with get() = unbox<bool>(Services.settings.getValue key)
                    and set value = Services.settings.setValue(key, box value) }
    let startupCheckbox() =
        let editor = BoolEditor() :> IPropEditor
        let read() = editor.value <- Services.settings.getValue "runAtStartup"
        read()
        refreshers.Add(read)
        editor.changed.Add(fun () ->
            if not refreshing then
                let mutable failed = false
                refreshing <- true
                try
                    try Services.settings.setValue("runAtStartup", editor.value)
                    with StartupRegistration.ChangeFailed -> failed <- true
                finally
                    try read()
                    finally refreshing <- false
                if failed then
                    AppDialog.showOwned (editor.control.FindForm()) "WindowTabs"
                        (Localization.getString("StartupRegistrationFailed")))
        editor.control
    let table =
        let fields = List2([
            ("RunAtStartup", startupCheckbox())
            ("IsTabbingEnabledForAllProcessesByDefault", checkBox(prop<IFilterService, bool>(Services.filter, "isTabbingEnabledForAllProcessesByDefault")))
            ("GroupTaskbarButtons", settingsCheckbox "groupTaskbarButtons")
            ("HideInactiveTabs", settingsCheckbox "hideInactiveTabs")
            ("HideTabsOnFullscreen", settingsCheckbox "hideTabsOnFullscreen")
            ("HideTabsWhileMoving", settingsCheckbox "hideTabsWhileMoving")
        ])
        let panel = UIHelper.form fields
        panel.Padding <- UIHelper.settingsPagePadding
        panel
    member _.refresh() =
        let previous = refreshing
        refreshing <- true
        try
            for refresh in refreshers do refresh()
        finally
            refreshing <- previous
    interface ISettingsView with
        member _.key = SettingsViewType.GeneralSettings
        member _.title = Localization.getString("General")
        member _.control = table :> Control


type SystemView() =
    let text key = Localization.getString(key)
    let pathField path =
        let field = new TextBox(ReadOnly = true, Text = path,
                                Anchor = (AnchorStyles.Top ||| AnchorStyles.Left ||| AnchorStyles.Right))
        SettingsField.apply field
        SettingsField.alignHeight field
        field
    let active = pathField ""
    let explanation = new Label(AutoSize = true, Anchor = (AnchorStyles.Left ||| AnchorStyles.Right),
                                MinimumSize = Size(0, UIHelper.settingsRowHeightPx - 2 * UIHelper.settingsRowMarginPx),
                                Margin = Padding(3, 0, 3, 0), TextAlign = ContentAlignment.MiddleLeft,
                                Text = text "PortableDescription")
    let reason = new Label(AutoSize = true, Anchor = (AnchorStyles.Left ||| AnchorStyles.Right))
    let operations = new TableLayoutPanel(AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 5)
    let buttons = ResizeArray<Button * bool>()
    let showFailure() =
        active.Text <- Services.settings.activePath
        AppDialog.showReserved "WindowTabs" (text "PortableOperationFailed" + Environment.NewLine + text "PortableActive" + Environment.NewLine + active.Text) AppDialog.OkOnly AppDialog.DefaultOk |> ignore
    let refreshState() =
        active.Text <- Services.settings.activePath
        let appData, exe = PortableSettings.appDataPath(), PortableSettings.exePath()
        let exeWritable = PortableSettings.canWrite(exe)
        for button, toExe in buttons do
            let source, destination = if toExe then appData, exe else exe, appData
            let applicable = active.Text = source && File.Exists(source) && (toExe || not (File.Exists(appData)))
            button.Enabled <-
                not (String.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) && applicable &&
                (if toExe then exeWritable else PortableSettings.canWrite(appData))
        reason.Text <- if exeWritable then "" else text "PortableExeNotWritable"
        reason.Visible <- not exeWritable
    let folder path =
        let row = new TableLayoutPanel(AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2)
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0f)) |> ignore
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)) |> ignore
        let value = pathField path
        row.Margin <- Padding(0)
        // The outer form owns the vertical margins; both children share one cell height.
        value.Margin <- Padding(0)
        let button = new Button(AutoSize = false, Anchor = (AnchorStyles.Top ||| AnchorStyles.Left),
                                Height = value.Height, Margin = Padding(3, 0, 3, 0),
                                Text = text "PortableOpen")
        // Follow alignHeight after font and DPI changes without changing row margins.
        let alignButton () =
            if button.Height <> value.Height then button.Height <- value.Height
        value.SizeChanged.Add(fun _ -> alignButton ())
        row.Layout.Add(fun _ -> alignButton ())
        button.Click.Add(fun _ ->
            try
                Directory.CreateDirectory(path) |> ignore
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path, UseShellExecute = true)) |> ignore
            with _ -> showFailure())
        row.Controls.Add(value, 0, 0)
        row.Controls.Add(button, 1, 0)
        row :> Control
    do
        // Percent-width cells wrap labels to the available content width, as on the adjacent pages.
        operations.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0f)) |> ignore
        for _ in 1 .. 5 do operations.RowStyles.Add(new RowStyle(SizeType.AutoSize)) |> ignore
        operations.Controls.Add(explanation)
        for key, toExe, move in ["PortableCopy", true, false; "PortableMove", true, true; "PortableReturn", false, true] do
            let button = new Button(AutoSize = true, Text = text key)
            buttons.Add(button, toExe)
            operations.Controls.Add(button)
            button.Click.Add(fun _ ->
                refreshState()
                if button.Enabled then
                    let destination = if toExe then PortableSettings.exePath() else PortableSettings.appDataPath()
                    let confirmed =
                        not toExe || not (File.Exists(destination)) ||
                        AppDialog.showReserved "WindowTabs" (text (if move then "PortableMoveOverwrite" else "PortableOverwrite")) AppDialog.OkCancel AppDialog.DefaultCancel = DialogResult.OK
                    if confirmed then
                        try
                            Services.program.saveTabGroupsBeforeExit()
                            Services.settings.transferSettings(toExe, move)
                        with _ -> showFailure()
                refreshState())
        operations.Controls.Add(reason)
    let table =
        let panel = UIHelper.form (List2([
            "PortableAppData", folder (Path.GetDirectoryName(PortableSettings.appDataPath()))
            "PortableExe", folder (Path.GetDirectoryName(PortableSettings.exePath()))
            "PortableActive", active :> Control
            "", new Panel() :> Control
            "PortableOperations", operations :> Control
        ]))
        // Match the Interaction page's half-row section gap before DPI scaling.
        let gapRow = 3
        for column in 0 .. 1 do
            let control = panel.GetControlFromPosition(column, gapRow)
            panel.Controls.Remove(control)
            control.Dispose()
        panel.RowStyles.[gapRow].SizeType <- SizeType.Absolute
        panel.RowStyles.[gapRow].Height <- float32 (UIHelper.settingsRowHeightPx / 2)
        panel.Padding <- UIHelper.settingsPagePadding
        panel
    member _.refresh() = refreshState()
    interface ISettingsView with
        member _.key = SettingsViewType.SystemSettings
        member _.title = text "System"
        member _.control = table :> Control
