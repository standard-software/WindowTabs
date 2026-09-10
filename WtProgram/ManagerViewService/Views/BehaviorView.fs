namespace Bemo
open System
open System.Drawing
open System.IO
open System.Windows.Forms
open Bemo.Win32
open Bemo.Win32.Forms


type HotKeyView() =
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
    let switchTabs =
        
        let checkBox (prop:IProperty<bool>) = 
            let checkbox = BoolEditor() :> IPropEditor
            checkbox.value <- box(prop.value)
            checkbox.changed.Add <| fun() ->
                if not refreshing then prop.value <- unbox<bool>(checkbox.value)
            refreshers.Add(fun () -> checkbox.value <- box(prop.value))
            checkbox.control

        let settingsCheckbox key = checkBox(settingsProperty(key))
        
        let defaultTabPositionCombo =
            let combo = new ComboBox()
            combo.DropDownStyle <- ComboBoxStyle.DropDownList
            combo.Width <- 100
            combo.Items.Add(Localization.getString("AlignTopLeft")) |> ignore
            combo.Items.Add(Localization.getString("AlignTopRight")) |> ignore

            let currentPosition = Services.settings.getValue("tabPositionByDefault") :?> string
            combo.SelectedIndex <-
                match currentPosition with
                | "TopLeft" -> 0
                | _ -> 1

            combo.SelectedIndexChanged.Add(fun _ ->
                let value = match combo.SelectedIndex with | 0 -> "TopLeft" | _ -> "TopRight"
                save "tabPositionByDefault" value
            )
            refreshers.Add(fun () ->
                combo.SelectedIndex <- if unbox<string>(Services.settings.getValue("tabPositionByDefault")) = "TopLeft" then 0 else 1)

            combo

        let hideTabsDelay =
            let textBox = new TextBox()
            let defaultValue =
                try
                    Services.settings.getValue("hideTabsDelayMilliseconds") :?> int
                with
                | _ -> 3000
            textBox.Text <- defaultValue.ToString()

            // Set initial enabled state based on current mode
            let currentMode = Services.settings.getValue("hideTabsWhenDownByDefault") :?> string
            textBox.Enabled <- (currentMode = "down")

            textBox.LostFocus.Add(fun _ ->
                match System.Int32.TryParse(textBox.Text) with
                | true, value when value >= 0 && value <= 10000 ->
                    save "hideTabsDelayMilliseconds" value
                | false, _ | _, _ ->
                    // Reset to previous value if invalid
                    textBox.Text <-
                        try
                            (Services.settings.getValue("hideTabsDelayMilliseconds") :?> int).ToString()
                        with
                        | _ -> "3000"
            )
            refreshers.Add(fun () ->
                textBox.Text <- (unbox<int>(Services.settings.getValue("hideTabsDelayMilliseconds"))).ToString())
            textBox

        let hideTabsRadio =
            // Use TableLayoutPanel to keep all RadioButtons in same container (for proper grouping)
            // while allowing delay label and textbox next to radioDown
            let table = new TableLayoutPanel()
            table.AutoSize <- true
            table.AutoSizeMode <- AutoSizeMode.GrowAndShrink
            table.Margin <- Padding(0)
            // Keep the complete group on the outer 3 x 35-px grid. Start the
            // radio controls slightly below the caption and keep their internal
            // spacing compact; the remaining space stays below the last row.
            table.Padding <- Padding(0, 6, 0, 15)
            table.ColumnCount <- 3
            table.RowCount <- 3
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)) |> ignore  // RadioButton column
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)) |> ignore  // Label column
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)) |> ignore  // TextBox column
            for _ in 1 .. 3 do
                table.RowStyles.Add(RowStyle(SizeType.Absolute, 28.0f)) |> ignore

            let currentMode =
                let mode = Services.settings.getValue("hideTabsWhenDownByDefault") :?> string
                // Use valid modes, default to "doubleclick" for invalid/unknown values
                match mode with
                | "never" | "down" | "doubleclick" -> mode
                | _ ->
                    Services.settings.setValue("hideTabsWhenDownByDefault", "doubleclick")
                    "doubleclick"

            let radioNever = new RadioButton()
            radioNever.Text <- Localization.getString("HideTabsNever")
            radioNever.AutoSize <- true
            radioNever.Margin <- Padding(0, 5, 0, 5)
            radioNever.Checked <- (currentMode = "never")
            radioNever.CheckedChanged.Add(fun _ ->
                if radioNever.Checked then
                    save "hideTabsWhenDownByDefault" "never"
                    hideTabsDelay.Enabled <- false
            )

            let radioDown = new RadioButton()
            radioDown.Text <- Localization.getString("HideTabsWhenDown")
            radioDown.AutoSize <- true
            radioDown.Margin <- Padding(0, 5, 0, 5)
            radioDown.Checked <- (currentMode = "down")
            radioDown.CheckedChanged.Add(fun _ ->
                if radioDown.Checked then
                    save "hideTabsWhenDownByDefault" "down"
                    hideTabsDelay.Enabled <- true
            )

            let radioDoubleClick = new RadioButton()
            radioDoubleClick.Text <- Localization.getString("HideTabsOnClick")
            radioDoubleClick.AutoSize <- true
            radioDoubleClick.Margin <- Padding(0, 5, 0, 5)
            radioDoubleClick.Checked <- (currentMode = "doubleclick")
            radioDoubleClick.CheckedChanged.Add(fun _ ->
                if radioDoubleClick.Checked then
                    save "hideTabsWhenDownByDefault" "doubleclick"
                    hideTabsDelay.Enabled <- false
            )
            refreshers.Add(fun () ->
                let mode = unbox<string>(Services.settings.getValue("hideTabsWhenDownByDefault"))
                radioNever.Checked <- (mode = "never")
                radioDown.Checked <- (mode = "down")
                radioDoubleClick.Checked <- (mode <> "never" && mode <> "down")
                hideTabsDelay.Enabled <- (mode = "down"))

            let delayLabel = new Label()
            delayLabel.Text <- Localization.getString("HideTabsDelayMilliseconds")
            delayLabel.AutoSize <- true
            delayLabel.Margin <- Padding(10, 5, 3, 0)  // Left margin to separate from radio, top margin to align with textbox

            hideTabsDelay.Width <- 60
            hideTabsDelay.Margin <- Padding(0, 2, 0, 0)

            // Row 0: radioNever (spans all 3 columns conceptually, but just in column 0)
            table.Controls.Add(radioNever, 0, 0)
            // Row 1: radioDown + delayLabel + hideTabsDelay
            table.Controls.Add(radioDown, 0, 1)
            table.Controls.Add(delayLabel, 1, 1)
            table.Controls.Add(hideTabsDelay, 2, 1)
            // Row 2: radioDoubleClick
            table.Controls.Add(radioDoubleClick, 0, 2)

            table

        let snapChangeTabPositionCombo =
            let combo = ComboBox()
            combo.DropDownStyle <- ComboBoxStyle.DropDownList
            combo.Width <- 300
            combo.Items.Add(Localization.getString("ChangeTabPositionOnSnapWhenUniform")) |> ignore
            combo.Items.Add(Localization.getString("ChangeTabPositionOnSnapNever")) |> ignore
            let currentMode =
                let mode = Services.settings.getValue("changeTabPositionOnSnap") :?> string
                match mode with
                | "change" | "nochange" -> mode
                | _ ->
                    Services.settings.setValue("changeTabPositionOnSnap", "change")
                    "change"
            combo.SelectedIndex <- if currentMode = "nochange" then 1 else 0
            combo.SelectedIndexChanged.Add(fun _ ->
                let value = if combo.SelectedIndex = 1 then "nochange" else "change"
                save "changeTabPositionOnSnap" value)
            refreshers.Add(fun () ->
                combo.SelectedIndex <- if unbox<string>(Services.settings.getValue("changeTabPositionOnSnap")) = "nochange" then 1 else 0)
            combo

        let tabVerticalDirectionCombo =
            let combo = ComboBox()
            combo.DropDownStyle <- ComboBoxStyle.DropDownList
            combo.Width <- 300
            combo.Items.Add(Localization.getString("TabVerticalAuto")) |> ignore
            combo.Items.Add(Localization.getString("TabVerticalAlwaysDown")) |> ignore
            let current =
                Services.settings.getValue("tabVerticalDirection") :?> string
                |> TabBehaviorPolicy.normalizeVerticalDirection
            combo.SelectedIndex <- if current = TabBehaviorPolicy.verticalAlwaysDown then 1 else 0
            combo.SelectedIndexChanged.Add(fun _ ->
                let value =
                    if combo.SelectedIndex = 1 then TabBehaviorPolicy.verticalAlwaysDown
                    else TabBehaviorPolicy.verticalAuto
                save "tabVerticalDirection" (box(value)))
            refreshers.Add(fun () ->
                let direction = unbox<string>(Services.settings.getValue("tabVerticalDirection")) |> TabBehaviorPolicy.normalizeVerticalDirection
                combo.SelectedIndex <- if direction = TabBehaviorPolicy.verticalAlwaysDown then 1 else 0)
            combo

        let fields = List2([
            ("RunAtStartup", settingsCheckbox "runAtStartup")
            ("HideInactiveTabs", settingsCheckbox "hideInactiveTabs")
            ("IsTabbingEnabledForAllProcessesByDefault", checkBox(prop<IFilterService, bool>(Services.filter, "isTabbingEnabledForAllProcessesByDefault")))
            ("EnableHoverActivate", settingsCheckbox "enableHoverActivate")
            ("TabPositionByDefault", defaultTabPositionCombo :> Control)
            ("ChangeTabPositionOnSnap", snapChangeTabPositionCombo :> Control)
            ("TabVerticalDirection", tabVerticalDirectionCombo :> Control)
            ("HideTabsWhenDownByDefault", hideTabsRadio :> Control)
            // hideTabsDelayMilliseconds is now integrated into hideTabsRadio panel
            ("HideTabsOnFullscreen", settingsCheckbox "hideTabsOnFullscreen")
            ("HideTabsWhileMoving", settingsCheckbox "hideTabsWhileMoving")
            ("SnapTabHeightMargin", settingsCheckbox "snapTabHeightMargin")
        ])

        let formPanel = UIHelper.form fields

        // This control already contains rows on the same 35-px grid as the
        // outer form. Do not add another outer row margin around the group.
        hideTabsRadio.Margin <- Padding(0)

        // Adjust the row height for the remaining radio button group.
        // Row index: 0=runAtStartup, 1=hideInactiveTabs, 2=isTabbingEnabled,
        //            3=enableHover, 4=tabPosition, 5=changeTabPositionOnSnap,
        //            6=tabVerticalDirection, 7=hideTabsWhenDown,
        //            8=hideTabsOnFullscreen, 9=hideTabsWhileMoving,
        //            10=snapTabHeightMargin
        let hideTabsRowIndex = 7

        // Let the radio-group row auto-size based on content.
        formPanel.RowStyles.[hideTabsRowIndex].SizeType <- SizeType.AutoSize

        "Switch Tabs", formPanel

    let sections = List2([
        switchTabs
        ])

    let table =
        // Remove GroupBox border for Switch Tabs section
        let (_,control) = sections.head
        control.Dock <- DockStyle.Fill
        // Add padding to match Appearance tab
        control.Padding <- Padding(10)
        control

    member this.refresh() =
        let previous = refreshing
        refreshing <- true
        try
            for refresh in refreshers do refresh()
        finally
            refreshing <- previous

    interface ISettingsView with
        member x.key = SettingsViewType.HotKeySettings
        member x.title = Localization.getString("Behavior")
        member x.control = table :> Control
