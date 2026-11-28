namespace Bemo
open System
open System.Drawing
open System.IO
open System.Windows.Forms
open Bemo.Win32
open Bemo.Win32.Forms


module private BehaviorControls =
    let row (shares: float32 list) (controls: Control list) =
        let panel = new SettingsTableLayoutPanel(ColumnCount = shares.Length, RowCount = 1, Dock = DockStyle.Fill,
                                         Margin = Padding(0), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink)
        for share in shares do
            panel.ColumnStyles.Add(ColumnStyle(SizeType.Percent, share * 100.0f / List.sum shares)) |> ignore
        panel.RowStyles.Add(RowStyle(SizeType.AutoSize)) |> ignore
        controls |> List.iteri (fun column control ->
            // No top or bottom anchor: a cell shorter than the row (a number
            // beside a taller choice) stands at the row's vertical centre.
            control.Anchor <- AnchorStyles.Left ||| AnchorStyles.Right
            control.Margin <- Padding(0)
            // Keep a width a control asked for (a choice sized to its longest item).
            control.MinimumSize <- Size(control.MinimumSize.Width, UIHelper.settingsRowHeightPx - 2 * UIHelper.settingsRowMarginPx)
            panel.Controls.Add(control, column, 0))
        panel :> Control

    let labelled key leftMargin (number: Control) =
        let cell = new SettingsTableLayoutPanel(ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill,
                                        AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                                        Margin = Padding(leftMargin, 0, 0, 0))
        cell.ColumnStyles.Add(ColumnStyle(SizeType.AutoSize)) |> ignore
        cell.ColumnStyles.Add(ColumnStyle(SizeType.Percent, 100.0f)) |> ignore
        cell.RowStyles.Add(RowStyle(SizeType.AutoSize)) |> ignore
        let label = new Label(AutoSize = true, Text = Localization.getString(key),
                              TextAlign = ContentAlignment.MiddleLeft,
                              MinimumSize = Size(0, UIHelper.settingsRowHeightPx - 2 * UIHelper.settingsRowMarginPx),
                              Anchor = (AnchorStyles.Top ||| AnchorStyles.Left), Margin = Padding(0, 0, 5, 0))
        number.Anchor <- AnchorStyles.Top ||| AnchorStyles.Left ||| AnchorStyles.Right
        cell.Controls.Add(label, 0, 0)
        cell.Controls.Add(number, 1, 0)
        cell :> Control

    let number key minimum maximum defaultValue save (refreshers: ResizeArray<unit -> unit>) =
        let number = SettingsNumericUpDown.Create()
        number.Minimum <- decimal minimum
        number.Maximum <- decimal maximum
        let refresh() = number.Value <- decimal (max minimum (min maximum (unbox<int>(Services.settings.getValue key))))
        refresh()
        refreshers.Add(refresh)
        number.ValueChanged.Add(fun _ -> save key (box (int number.Value)))
        number.Margin <- Padding(0)
        number.SetNumericMenu(Some(string defaultValue, fun () -> number.Value <- decimal defaultValue))
        number :> Control

type InteractionView() =
    let mutable refreshing = false
    let refreshers = ResizeArray<unit -> unit>()
    let save key value =
        if not refreshing then Services.settings.setValue(key, value)

    let writeEdge f =
        if not refreshing then
            let root = Services.settings.root
            f root
            Services.settings.root <- root
            TabEdgeSettings.changed.Trigger()

    let refreshCloseAll, closeAllControls =
        let modes = TabEdgeSettings.closeAllModeChoices
        let combo = EdgeRows.choice (["CloseAllButtonConfirm"; "CloseAllButtonImmediate"] |> List.map Localization.getString)
        // Disabled controls do not receive mouse messages. Their enabled parent
        // owns the tooltip over the same area instead.
        let host = BehaviorControls.row [1.0f] [combo]
        let hint = new ToolTip(ShowAlways = true)
        let refreshHint () =
            hint.Active <- not combo.Enabled
            hint.SetToolTip(host, if combo.Enabled then "" else Localization.getString "CloseAllButtonDisabledHint")
        combo.EnabledChanged.Add(fun _ -> refreshHint())
        host.Disposed.Add(fun _ -> hint.Dispose())
        let mutable refreshingMode = false
        let refresh () =
            refreshingMode <- true
            try
                let mode = TabEdgeSettings.closeAllConfirmation Services.settings.root
                combo.SelectedIndex <- modes |> List.findIndex (fun (_, m) -> m = mode)
                combo.Enabled <- not (List.isEmpty (TabEdgeSettings.closeAllPositions Services.settings.root))
            finally refreshingMode <- false
        refresh()
        refreshHint()
        combo.SelectedIndexChanged.Add(fun _ ->
            if not refreshingMode && combo.SelectedIndex >= 0 then
                writeEdge (fun root -> TabEdgeSettings.writeCloseAllMode root (snd modes.[combo.SelectedIndex])))
        let subscription = TabEdgeSettings.changed.Publish.Subscribe(fun () -> refresh())
        combo.Disposed.Add(fun _ -> subscription.Dispose())
        refresh, host
    do refreshers.Add refreshCloseAll

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
        
        let settingsChoice key normalize options =
            let combo = EdgeRows.choice (options |> List.map (snd >> Localization.getString))
            let fit() = EdgeRows.fitChoiceWidth combo
            fit()
            combo.FontChanged.Add(fun _ -> fit())
            combo.DropDown.Add(fun _ -> fit())
            let refresh() =
                let mode = Services.settings.getValue key |> unbox<string> |> normalize
                combo.SelectedIndex <- options |> List.findIndex (fun (value,_) -> value = mode)
            refresh()
            refreshers.Add(refresh)
            combo.SelectedIndexChanged.Add(fun _ ->
                if combo.SelectedIndex >= 0 then save key (box (fst options.[combo.SelectedIndex])))
            combo :> Control

        let buttonModeChoice key captions =
            List.zip [TabButtonPolicy.activeOnly; TabButtonPolicy.all; TabButtonPolicy.none] captions
            |> settingsChoice key TabButtonPolicy.normalize

        let snapChangeTabPositionChoice =
            settingsChoice "changeTabPositionOnSnap"
                (fun mode -> if mode = "nochange" || mode = "always" then mode else "change")
                ["nochange", "ChangeTabPositionOnSnapNever"; "always", "ChangeTabPositionOnSnapAlways"; "change", "ChangeTabPositionOnSnapWhenUniform"]

        let previewNumber key minimum maximum defaultValue =
            BehaviorControls.number key minimum maximum defaultValue save refreshers

        let previewControls =
            let delay = BehaviorControls.labelled "HideTabsDelayMilliseconds" 0 (previewNumber "tabPreviewDelay" 0 5000 500)
            let size = BehaviorControls.labelled "TabPreviewSize" 0 (previewNumber "tabPreviewSize" 80 1000 320)
            let panel = BehaviorControls.row [1.0f; 2.0f; 2.0f] [settingsCheckbox "enableTabPreview"; size; delay]
            delay.Margin <- Padding(10, 0, 0, 0)
            panel

        let snapMarginEnabled =
            checkBox {
                new IProperty<bool> with
                    member _.value
                        with get() = Services.settings.getValue "snapTabMarginMode" |> unbox<string> |> SnapTabMarginMode.enabled
                        and set on =
                            let preferred = Services.settings.getValue "snapDetectionMode" |> unbox<string>
                            save "snapTabMarginMode" (box (SnapTabMarginMode.marginForDetection on preferred)) }

        let snapMarginChoice = EdgeRows.choice (SnapTabMarginMode.choices |> List.map (snd >> Localization.getString))
        let fitSnapMarginChoice() =
            EdgeRows.fitChoiceWidth snapMarginChoice
            // Keep the closed field within the shared editor column.
            snapMarginChoice.MinimumSize <- Size.Empty
        fitSnapMarginChoice()
        snapMarginChoice.FontChanged.Add(fun _ -> fitSnapMarginChoice())
        snapMarginChoice.DropDown.Add(fun _ -> fitSnapMarginChoice())
        let buttonOnlyMarginChoice = EdgeRows.choice (["SnapButtonOnlyMarginNone"; "SnapButtonOnlyMarginAll"] |> List.map Localization.getString)
        let snapMarginDelay = BehaviorControls.labelled "HideTabsDelayMilliseconds" 0 (previewNumber "snapTabMarginDelayMilliseconds" 100 10000 3000)
        let mutable refreshingMargin = false
        let refreshMargin() =
            refreshingMargin <- true
            try
                let mode = Services.settings.getValue "snapTabMarginMode" |> unbox<string>
                let preferred = Services.settings.getValue "snapDetectionMode" |> unbox<string>
                let selected = preferred
                snapMarginChoice.SelectedIndex <- SnapTabMarginMode.choices |> List.findIndex (fun (value, _) -> value = selected)
                buttonOnlyMarginChoice.SelectedIndex <- if Services.settings.getValue "snapButtonOnlyMargin" |> unbox<bool> then 1 else 0
                let enabled = SnapTabMarginMode.enabled mode
                snapMarginChoice.Enabled <- true
                buttonOnlyMarginChoice.Enabled <- enabled
                snapMarginDelay.Enabled <- SnapTabMarginMode.externalEnabled selected
            finally refreshingMargin <- false
        refreshMargin()
        refreshers.Add(refreshMargin)
        (snapMarginEnabled :?> CheckBox).CheckedChanged.Add(fun _ -> refreshMargin())
        snapMarginChoice.SelectedIndexChanged.Add(fun _ ->
            if not refreshingMargin && not refreshing && snapMarginChoice.SelectedIndex >= 0 then
                let selected = fst SnapTabMarginMode.choices.[snapMarginChoice.SelectedIndex]
                save "snapDetectionMode" (box selected)
                refreshMargin())
        buttonOnlyMarginChoice.SelectedIndexChanged.Add(fun _ ->
            if not refreshingMargin && not refreshing && buttonOnlyMarginChoice.SelectedIndex >= 0 then
                save "snapButtonOnlyMargin" (box (buttonOnlyMarginChoice.SelectedIndex = 1)))
        let snapMarginControls = BehaviorControls.row [3.0f; 2.0f] [snapMarginChoice; snapMarginDelay]
        do
            // Reserve the complete wait caption and a five-digit numeric field.
            let delay = snapMarginDelay :?> TableLayoutPanel
            delay.ColumnStyles.[1].SizeType <- SizeType.Absolute
            delay.ColumnStyles.[1].Width <- 70.0f
            let row = snapMarginControls :?> TableLayoutPanel
            row.ColumnStyles.[0].Width <- 100.0f
            row.ColumnStyles.[1].SizeType <- SizeType.AutoSize
            snapMarginDelay.Margin <- Padding(10, 0, 0, 0)

        let lockPositionCheckbox = settingsCheckbox "lockWindowPosition"
        let fields = List2([
            ("TabCloseButton", buttonModeChoice "tabCloseButton" ["TabCloseActiveOnly"; "TabCloseAll"; "TabCloseNone"])
            ("TabPinButton", buttonModeChoice "tabPinButton" ["TabPinActiveOnly"; "TabPinAll"; "TabPinNone"])
            ("CloseAllTabsButtonBehavior", closeAllControls :> Control)
            ("", new Panel() :> Control)
            ("SnapDetectionMode", snapMarginControls)
            ("ChangeTabPositionOnSnap", snapChangeTabPositionChoice)
            ("SnapTabMarginMode", BehaviorControls.row [1.0f; 1.0f] [snapMarginEnabled; buttonOnlyMarginChoice])
            ("SnapOnDragDetach", settingsCheckbox "snapOnDragDetach")
            ("", new Panel() :> Control)
            ("EnableHoverActivate", settingsCheckbox "enableHoverActivate")
            ("EnableTabPreview", previewControls)
            ("", new Panel() :> Control)
            ("LockWindowPosition", lockPositionCheckbox)
        ])

        let formPanel = UIHelper.form fields
        // Absorb unused page height without stretching the final editor row.
        formPanel.RowCount <- fields.length + 1
        formPanel.RowStyles.Add(RowStyle(SizeType.Percent, 100.0f)) |> ignore
        // Captions occupy the complete row, including wrapped two-line text.
        // Editors are centred independently of their native preferred heights.
        for row in 0 .. fields.length - 1 do
            let label = formPanel.GetControlFromPosition(0, row)
            label.MinimumSize <- Size(0, UIHelper.settingsRowHeightPx)
            label.Margin <- Padding(0)
            let control = formPanel.GetControlFromPosition(1, row)
            control.Anchor <- AnchorStyles.Left ||| AnchorStyles.Right
            control.Dock <- DockStyle.None
            control.Margin <- Padding(0)
            formPanel.RowStyles.[row].SizeType <- SizeType.Absolute
            formPanel.RowStyles.[row].Height <- float32 UIHelper.settingsRowHeightPx

        // Empty half-row gaps use the same layout as the System page.
        for gapRow in [3; 8; 11] do
            for column in 0 .. 1 do
                let control = formPanel.GetControlFromPosition(column, gapRow)
                formPanel.Controls.Remove(control)
                control.Dispose()
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
        let taskbarButtons = new SettingsTableLayoutPanel(AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 2)
        taskbarButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.0f)) |> ignore
        for _ in 1 .. 2 do taskbarButtons.RowStyles.Add(new RowStyle(SizeType.AutoSize)) |> ignore
        let checkbox = settingsCheckbox "groupTaskbarButtons"
        checkbox.Margin <- Padding(0)
        taskbarButtons.Controls.Add(checkbox)
        let note = new Label(AutoSize = true, Anchor = (AnchorStyles.Left ||| AnchorStyles.Right),
                             MinimumSize = Size(0, UIHelper.settingsRowHeightPx - 2 * UIHelper.settingsRowMarginPx),
                             Margin = Padding(3, 0, 3, 0), TextAlign = ContentAlignment.MiddleLeft,
                             Text = Localization.getString("GroupTaskbarButtonsNote"))
        taskbarButtons.Controls.Add(note)
        let activeFirst = settingsCheckbox "taskbarActiveTabFirst"
        let save key value = if not refreshing then Services.settings.setValue(key, value)
        let delayNumber = BehaviorControls.number "taskbarOrderDelayMilliseconds" 100 10000 2000 save refreshers
        let delay = BehaviorControls.labelled "HideTabsDelayMilliseconds" 0 delayNumber
        let refreshEnabled() = delay.Enabled <- Services.settings.getValue "taskbarActiveTabFirst" |> unbox<bool>
        refreshEnabled()
        refreshers.Add(refreshEnabled)
        (activeFirst :?> CheckBox).CheckedChanged.Add(fun _ -> refreshEnabled())
        let activeFirstControls = BehaviorControls.row [3.0f; 2.0f] [activeFirst; delay]
        delay.Margin <- Padding(10, 0, 0, 0)
        let fields = List2([
            ("RunAtStartup", startupCheckbox())
            ("IsTabbingEnabledForAllProcessesByDefault", checkBox(prop<IFilterService, bool>(Services.filter, "isTabbingEnabledForAllProcessesByDefault")))
            ("GroupTaskbarButtons", taskbarButtons :> Control)
            ("TaskbarActiveTabFirst", activeFirstControls)
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
    let operations = new SettingsTableLayoutPanel(AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 5)
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
        let row = new SettingsTableLayoutPanel(AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2)
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
        // A half-row section gap, set before DPI scaling.
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
