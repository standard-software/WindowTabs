namespace Bemo
open System
open System.Drawing
open System.IO
open System.Windows.Forms
open Bemo.Win32
open Bemo.Win32.Forms


type HotKeyView() =
    let settingsProperty name =
        {
            new IProperty<'a> with
                member x.value
                    with get() = unbox<'a>(Services.settings.getValue(name))
                    and set(value) = Services.settings.setValue(name, box(value))
        }
        

    let switchTabs =
        let hotKeys = List2([
            ("nextTab", "nextTab")
            ("prevTab", "prevTab")
        ])

        let editors = hotKeys.enumerate.fold (Map2()) <| fun editors (i,(key, text)) ->
            let caption = Localization.getString text
            let label = UIHelper.label caption
            let editor = HotKeyEditor() :> IPropEditor
            editor.control.Margin <- Padding(0,5,0,5)
            label.Margin <- Padding(0,5,0,5)
            editors.add key editor

        hotKeys.iter <| fun (key,_) ->
            let editor = editors.find key
            editor.value <- Services.program.getHotKey(key)
            editor.changed.Add <| fun() ->
                Services.program.setHotKey key (unbox<int>(editor.value))
        
        let checkBox (prop:IProperty<bool>) = 
            let checkbox = BoolEditor() :> IPropEditor
            checkbox.value <- box(prop.value)
            checkbox.changed.Add <| fun() -> prop.value <- unbox<bool>(checkbox.value)
            checkbox.control

        let settingsCheckbox key = checkBox(settingsProperty(key))
        
        let defaultTabPositionRadio =
            let panel = new FlowLayoutPanel()
            panel.FlowDirection <- FlowDirection.TopDown
            panel.AutoSize <- true
            panel.AutoSizeMode <- AutoSizeMode.GrowAndShrink
            panel.Margin <- Padding(0)
            panel.Padding <- Padding(0, 0, 0, 10)  // Add bottom padding

            let currentPosition = Services.settings.getValue("tabPositionByDefault") :?> string

            let radioLeft = new RadioButton()
            radioLeft.Text <- Localization.getString("AlignLeft")
            radioLeft.AutoSize <- true
            radioLeft.Checked <- (currentPosition = "left")
            radioLeft.CheckedChanged.Add(fun _ ->
                if radioLeft.Checked then
                    Services.settings.setValue("tabPositionByDefault", "left")
            )

            let radioCenter = new RadioButton()
            radioCenter.Text <- Localization.getString("AlignCenter")
            radioCenter.AutoSize <- true
            radioCenter.Checked <- (currentPosition = "center")
            radioCenter.CheckedChanged.Add(fun _ ->
                if radioCenter.Checked then
                    Services.settings.setValue("tabPositionByDefault", "center")
            )

            let radioRight = new RadioButton()
            radioRight.Text <- Localization.getString("AlignRight")
            radioRight.AutoSize <- true
            radioRight.Checked <- (currentPosition = "right" || (currentPosition <> "left" && currentPosition <> "center"))
            radioRight.CheckedChanged.Add(fun _ ->
                if radioRight.Checked then
                    Services.settings.setValue("tabPositionByDefault", "right")
            )

            panel.Controls.Add(radioLeft)
            panel.Controls.Add(radioCenter)
            panel.Controls.Add(radioRight)
            panel

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
                    Services.settings.setValue("hideTabsDelayMilliseconds", value)
                | false, _ | _, _ ->
                    // Reset to previous value if invalid
                    textBox.Text <-
                        try
                            (Services.settings.getValue("hideTabsDelayMilliseconds") :?> int).ToString()
                        with
                        | _ -> "3000"
            )
            textBox

        let hideTabsRadio =
            let panel = new FlowLayoutPanel()
            panel.FlowDirection <- FlowDirection.TopDown
            panel.AutoSize <- true
            panel.AutoSizeMode <- AutoSizeMode.GrowAndShrink
            panel.Margin <- Padding(0)
            panel.Padding <- Padding(0, 0, 0, 10)  // Add bottom padding

            let currentMode =
                let mode = Services.settings.getValue("hideTabsWhenDownByDefault") :?> string
                // Use valid modes, default to "doubleclick" for invalid/unknown values
                match mode with
                | "never" | "down" | "doubleclick" -> mode
                | _ ->
                    Services.settings.setValue("hideTabsWhenDownByDefault", "doubleclick")
                    "doubleclick"

            let radioNever = new RadioButton()
            radioNever.Text <- Localization.getString("HideNever")
            radioNever.AutoSize <- true
            radioNever.Checked <- (currentMode = "never")
            radioNever.CheckedChanged.Add(fun _ ->
                if radioNever.Checked then
                    Services.settings.setValue("hideTabsWhenDownByDefault", "never")
                    hideTabsDelay.Enabled <- false
            )

            let radioDown = new RadioButton()
            radioDown.Text <- Localization.getString("HideWhenDown")
            radioDown.AutoSize <- true
            radioDown.Checked <- (currentMode = "down")
            radioDown.CheckedChanged.Add(fun _ ->
                if radioDown.Checked then
                    Services.settings.setValue("hideTabsWhenDownByDefault", "down")
                    hideTabsDelay.Enabled <- true
            )

            let radioDoubleClick = new RadioButton()
            radioDoubleClick.Text <- Localization.getString("HideOnDoubleClick")
            radioDoubleClick.AutoSize <- true
            radioDoubleClick.Checked <- (currentMode = "doubleclick")
            radioDoubleClick.CheckedChanged.Add(fun _ ->
                if radioDoubleClick.Checked then
                    Services.settings.setValue("hideTabsWhenDownByDefault", "doubleclick")
                    hideTabsDelay.Enabled <- false
            )

            panel.Controls.Add(radioNever)
            panel.Controls.Add(radioDown)
            panel.Controls.Add(radioDoubleClick)
            panel

        let fields = hotKeys.map <| fun(key,text) ->
            let editor = editors.find key
            text, editor.control

        let fields = fields.prependList(List2([
            ("runAtStartup", settingsCheckbox "runAtStartup")
            ("hideInactiveTabs", settingsCheckbox "hideInactiveTabs")
            ("isTabbingEnabledForAllProcessesByDefault", checkBox(prop<IFilterService, bool>(Services.filter, "isTabbingEnabledForAllProcessesByDefault")))
            ("enableCtrlNumberHotKey", settingsCheckbox "enableCtrlNumberHotKey")
            ("enableHoverActivate", settingsCheckbox "enableHoverActivate")
            ("makeTabsNarrowerByDefault", settingsCheckbox "makeTabsNarrowerByDefault")
            ("tabPositionByDefault", defaultTabPositionRadio :> Control)
            ("hideTabsWhenDownByDefault", hideTabsRadio :> Control)
            ("hideTabsDelayMilliseconds", hideTabsDelay :> Control)
        ]))

        let formPanel = UIHelper.form fields

        // Adjust row heights for radio button groups
        // Row index: 0=runAtStartup, 1=hideInactiveTabs, 2=isTabbingEnabled, 3=enableCtrlNumber,
        //            4=enableHover, 5=makeTabsNarrower, 6=tabPosition, 7=hideTabsWhenDown, 8=hideTabsDelay,
        //            9+=hotkeys
        let tabPositionRowIndex = 6
        let hideTabsRowIndex = 7

        if formPanel :? TableLayoutPanel then
            let table = formPanel :?> TableLayoutPanel
            // Let rows auto-size based on content
            table.RowStyles.[tabPositionRowIndex].SizeType <- SizeType.AutoSize
            table.RowStyles.[hideTabsRowIndex].SizeType <- SizeType.AutoSize

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

    interface ISettingsView with
        member x.key = SettingsViewType.HotKeySettings
        member x.title = Localization.getString("Behavior")
        member x.control = table :> Control

