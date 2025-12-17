namespace Bemo
open System
open System.Drawing
open System.IO
open System.Windows.Forms
open Bemo.Win32
open Bemo.Win32.Forms
open Microsoft.FSharp.Reflection

type AppearanceProperty = {
    displayText : string
    key: string
    propertyType : AppearancePropertyType
    }

and AppearancePropertyType =
    | HotKeyProperty
    | IntProperty
    | ColorProperty

type AppearanceView() as this =
    let settingsPropertyBool name (defaultValue:bool) =
        {
            new IProperty<bool> with
                member x.value
                    with get() =
                        try
                            let json = Services.settings.root
                            match json.getBool(name) with
                            | Some(value) -> value
                            | None -> defaultValue
                        with | _ -> defaultValue
                    and set(value) =
                        let json = Services.settings.root
                        json.setBool(name, value)
                        Services.settings.root <- json
        }

    let colorConfig key displayText =
        { displayText=displayText; key=key; propertyType=ColorProperty }

    let intConfig key displayText =
        { displayText=displayText; key=key; propertyType=IntProperty }

    let hkConfig key displayText =
        { displayText=displayText; key=key; propertyType=HotKeyProperty }
        
    let mutable suppressEvents = false

    let checkBox (prop:IProperty<bool>) =
        let checkbox = BoolEditor() :> IPropEditor
        checkbox.value <- box(prop.value)
        checkbox.changed.Add <| fun() -> prop.value <- unbox<bool>(checkbox.value)
        checkbox.control

    let settingsCheckboxBool key defaultValue = checkBox(settingsPropertyBool key defaultValue)

    let properties = List2([
        intConfig "tabHeight" "Height"
        intConfig "tabMaxWidth" "Max Width"
        intConfig "tabOverlap" "Overlap"
        intConfig "tabIndentNormal" "Indent Normal"
        intConfig "tabIndentFlipped" "Indent Flipped"
        colorConfig "tabTextColor" "Text Color"
        colorConfig "tabNormalBgColor" "Background Normal"
        colorConfig "tabHighlightBgColor" "Background Highlight"
        colorConfig "tabActiveBgColor" "Background Active"
        colorConfig "tabFlashBgColor" "Background Flash"
        colorConfig "tabBorderColor" "Border"
        ])

    let panel =
        let panel = TableLayoutPanel()
        panel.AutoScroll <- true
        panel.Dock <- DockStyle.Fill
        panel.GrowStyle <- TableLayoutPanelGrowStyle.FixedSize
        panel.Padding <- Padding(10)
        panel.RowCount <- properties.length + 2  // +1 for checkbox, +1 for buttons
        List2([0..properties.length + 1]).iter <| fun row ->
            panel.RowStyles.Add(RowStyle(SizeType.Absolute, 35.0f)).ignore
        panel.ColumnCount <- 3
        panel.ColumnStyles.Add(ColumnStyle(SizeType.Absolute, 200.0f)).ignore
        panel.ColumnStyles.Add(ColumnStyle(SizeType.Percent, 100.0f)).ignore
        panel.ColumnStyles.Add(ColumnStyle(SizeType.Absolute, 100.0f)).ignore
        panel

   
    let getDefaultValue key =
        let defaultAppearance = Services.program.defaultTabAppearanceInfo
        Serialize.readField defaultAppearance key

    let formatDefaultValue key =
        let value = getDefaultValue key
        match value with
        | :? int as i -> sprintf "%3d" i  // 3-digit right-aligned
        | :? Color as c -> sprintf "#%06X" (c.ToArgb() &&& 0xFFFFFF)
        | _ -> value.ToString()

    let editors = properties.enumerate.fold (Map2()) <| fun editors (i,prop) ->
        let label =
            let label = Label()
            label.AutoSize <- true
            label.Text <- Localization.getString(prop.displayText)
            label.TextAlign <- ContentAlignment.MiddleLeft
            label
        let editor =
            match prop.propertyType with
            | ColorProperty -> ColorEditor() :> IPropEditor
            | IntProperty -> IntEditor() :> IPropEditor
            | HotKeyProperty -> HotKeyEditor() :> IPropEditor

        editor.control.Dock <- DockStyle.Fill
        editor.control.Margin <- Padding(0,5,0,5)
        label.Margin <- Padding(0,5,0,5)
        panel.Controls.Add(label)
        panel.Controls.Add(editor.control)
        panel.SetRow(label, i)
        panel.SetColumn(label, 0)
        panel.SetRow(editor.control, i)
        panel.SetColumn(editor.control, 1)

        // Add reset button only for IntProperty, expand ColorProperty to span 2 columns
        match prop.propertyType with
        | IntProperty ->
            let resetBtn =
                let btn = Button()
                btn.Text <- sprintf "%s:%s" (Localization.getString("Reset")) (formatDefaultValue prop.key)
                btn.Font <- Font("Consolas", 9f)  // Monospace font for aligned numbers
                btn.Dock <- DockStyle.Fill
                btn.Margin <- Padding(5,5,0,5)
                btn.Click.Add <| fun _ ->
                    suppressEvents <- true
                    let defaultValue = getDefaultValue prop.key
                    editor.value <- defaultValue
                    this.applyAppearance()
                    suppressEvents <- false
                btn
            panel.Controls.Add(resetBtn)
            panel.SetRow(resetBtn, i)
            panel.SetColumn(resetBtn, 2)
        | ColorProperty ->
            // Expand color editor to span columns 1 and 2
            panel.SetColumnSpan(editor.control, 2)
        | _ -> ()

        editors.add prop.key editor

    let setEditorValues appearance =
        properties.iter <| fun prop ->
            let editor = editors.find prop.key
            try
                editor.value <- Serialize.readField appearance prop.key
            with | _ ->()

    let appearance = Services.program.tabAppearanceInfo

    let font = Font(Localization.getString("Font"), 9f)

    let buttonPanel =
        let container = new FlowLayoutPanel()
        container.FlowDirection <- FlowDirection.LeftToRight
        container.AutoSize <- true
        container.WrapContents <- false
        container.Anchor <- AnchorStyles.Right

        let lightBtn = Button()
        lightBtn.Text <- Localization.getString("LightColor")
        lightBtn.Font <- font
        lightBtn.Click.Add <| fun _ ->
            suppressEvents <- true
            let currentAppearance = Services.program.tabAppearanceInfo
            let defaultAppearance = Services.program.defaultTabAppearanceInfo
            // Apply only color settings from default, keep size settings
            let mergedAppearance = {
                tabHeight = currentAppearance.tabHeight
                tabMaxWidth = currentAppearance.tabMaxWidth
                tabOverlap = currentAppearance.tabOverlap
                tabHeightOffset = currentAppearance.tabHeightOffset
                tabIndentFlipped = currentAppearance.tabIndentFlipped
                tabIndentNormal = currentAppearance.tabIndentNormal
                tabTextColor = defaultAppearance.tabTextColor
                tabNormalBgColor = defaultAppearance.tabNormalBgColor
                tabHighlightBgColor = defaultAppearance.tabHighlightBgColor
                tabActiveBgColor = defaultAppearance.tabActiveBgColor
                tabFlashBgColor = defaultAppearance.tabFlashBgColor
                tabBorderColor = defaultAppearance.tabBorderColor
            }
            Services.settings.setValue("tabAppearance", box(mergedAppearance))
            setEditorValues mergedAppearance
            suppressEvents <- false
        
        let darkBtn = Button()
        darkBtn.Text <- Localization.getString("DarkColor")
        darkBtn.Font <- font
        darkBtn.Click.Add <| fun _ ->
            suppressEvents <- true
            let currentAppearance = Services.program.tabAppearanceInfo
            let darkAppearance = Services.program.darkModeTabAppearanceInfo
            // Apply only color settings from dark theme, keep size settings
            let mergedAppearance = {
                tabHeight = currentAppearance.tabHeight
                tabMaxWidth = currentAppearance.tabMaxWidth
                tabOverlap = currentAppearance.tabOverlap
                tabHeightOffset = currentAppearance.tabHeightOffset
                tabIndentFlipped = currentAppearance.tabIndentFlipped
                tabIndentNormal = currentAppearance.tabIndentNormal
                tabTextColor = darkAppearance.tabTextColor
                tabNormalBgColor = darkAppearance.tabNormalBgColor
                tabHighlightBgColor = darkAppearance.tabHighlightBgColor
                tabActiveBgColor = darkAppearance.tabActiveBgColor
                tabFlashBgColor = darkAppearance.tabFlashBgColor
                tabBorderColor = darkAppearance.tabBorderColor
            }
            Services.settings.setValue("tabAppearance", box(mergedAppearance))
            setEditorValues mergedAppearance
            suppressEvents <- false

        let darkBlueBtn = Button()
        darkBlueBtn.Text <- Localization.getString("DarkBlueColor")
        darkBlueBtn.Font <- font
        darkBlueBtn.Click.Add <| fun _ ->
            suppressEvents <- true
            let currentAppearance = Services.program.tabAppearanceInfo
            let blueAppearance = Services.program.darkModeBlueTabAppearanceInfo
            // Apply only color settings from dark blue theme, keep size settings
            let mergedAppearance = {
                tabHeight = currentAppearance.tabHeight
                tabMaxWidth = currentAppearance.tabMaxWidth
                tabOverlap = currentAppearance.tabOverlap
                tabHeightOffset = currentAppearance.tabHeightOffset
                tabIndentFlipped = currentAppearance.tabIndentFlipped
                tabIndentNormal = currentAppearance.tabIndentNormal
                tabTextColor = blueAppearance.tabTextColor
                tabNormalBgColor = blueAppearance.tabNormalBgColor
                tabHighlightBgColor = blueAppearance.tabHighlightBgColor
                tabActiveBgColor = blueAppearance.tabActiveBgColor
                tabFlashBgColor = blueAppearance.tabFlashBgColor
                tabBorderColor = blueAppearance.tabBorderColor
            }
            Services.settings.setValue("tabAppearance", box(mergedAppearance))
            setEditorValues mergedAppearance
            suppressEvents <- false
        
        container.Controls.Add(darkBtn)
        container.Controls.Add(darkBlueBtn)
        container.Controls.Add(lightBtn)
        container

    do
        // Add dark mode checkbox after border color (last property)
        let darkModeLabel = Label()
        darkModeLabel.AutoSize <- true
        darkModeLabel.Text <- Localization.getString("MenuDarkMode")
        darkModeLabel.TextAlign <- ContentAlignment.MiddleLeft
        darkModeLabel.Margin <- Padding(0,5,0,5)

        let darkModeCheckbox = settingsCheckboxBool "enableMenuDarkMode" false
        darkModeCheckbox.Margin <- Padding(0,5,0,5)

        let checkboxRow = properties.length
        panel.Controls.Add(darkModeLabel)
        panel.Controls.Add(darkModeCheckbox)
        panel.SetRow(darkModeLabel, checkboxRow)
        panel.SetColumn(darkModeLabel, 0)
        panel.SetRow(darkModeCheckbox, checkboxRow)
        panel.SetColumn(darkModeCheckbox, 1)

        // Add button panel
        panel.Controls.Add(buttonPanel)
        let btnRow = properties.length + 1
        panel.SetRow(buttonPanel, btnRow)
        panel.SetColumn(buttonPanel, 1)
        setEditorValues appearance
        editors.items.map(snd).iter <| fun editor ->
            editor.changed.Add <| fun() ->
                if not suppressEvents then
                    this.applyAppearance()
        
    member this.applyAppearance() =
        // Get all current values from UI editors
        let getValue key = (editors.find key).value
        
        // Get the current appearance for fields not in UI
        let currentAppearance = Services.program.tabAppearanceInfo
        
        // Create new appearance with correct field order
        let newAppearance = {
            tabHeight = unbox(getValue "tabHeight")
            tabMaxWidth = unbox(getValue "tabMaxWidth")
            tabOverlap = unbox(getValue "tabOverlap")
            tabHeightOffset = currentAppearance.tabHeightOffset  // Keep internal value
            tabIndentFlipped = unbox(getValue "tabIndentFlipped")
            tabIndentNormal = unbox(getValue "tabIndentNormal")
            tabTextColor = unbox(getValue "tabTextColor")
            tabNormalBgColor = unbox(getValue "tabNormalBgColor")
            tabHighlightBgColor = unbox(getValue "tabHighlightBgColor")
            tabActiveBgColor = unbox(getValue "tabActiveBgColor")
            tabFlashBgColor = unbox(getValue "tabFlashBgColor")
            tabBorderColor = unbox(getValue "tabBorderColor")
        }
        
        Services.settings.setValue("tabAppearance", box(newAppearance))
        
    interface ISettingsView with
        member x.key = SettingsViewType.AppearanceSettings
        member x.title = Localization.getString("Appearance")
        member x.control = panel :> Control

