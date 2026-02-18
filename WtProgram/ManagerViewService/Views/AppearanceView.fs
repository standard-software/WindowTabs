namespace Bemo
open System
open System.Drawing
open System.IO
open System.Text.RegularExpressions
open System.Windows.Forms
open System.Windows.Forms.VisualStyles
open Bemo.Win32
open Bemo.Win32.Forms
open Microsoft.FSharp.Reflection
open Newtonsoft.Json.Linq

type AppearanceProperty = {
    displayText : string
    key: string
    propertyType : AppearancePropertyType
    }

and AppearancePropertyType =
    | HotKeyProperty
    | IntProperty
    | ColorProperty

// Custom theme type for storage
type ColorThemeData = {
    name: string
    inactiveTextColor: int
    mouseOverTextColor: int
    activeTextColor: int
    flashTextColor: int
    inactiveTabColor: int
    mouseOverTabColor: int
    activeTabColor: int
    flashTabColor: int
    inactiveBorderColor: int
    mouseOverBorderColor: int
    activeBorderColor: int
    flashBorderColor: int
}

// ComboBox item types
type ThemeItem =
    | Preset of string
    | CustomTheme of string
    | UnsavedCustom

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

    let settingsCheckboxBool settingsKey defaultValue = checkBox(settingsPropertyBool settingsKey defaultValue)

    // Separate int and color properties for cleaner UI layout
    let intProperties = List2([
        intConfig "tabHeight" "Height"
        intConfig "tabMaxWidth" "Max Width"
        intConfig "tabOverlap" "Overlap"
        intConfig "tabIndentNormal" "Indent Normal"
        intConfig "tabIndentFlipped" "Indent Flipped"
        ])

    // Color properties organized by state (for new grid layout)
    // Each state has: TabColor, TextColor, BorderColor (in that order for columns)
    let colorStateRows = [
        ("Inactive", [("tabInactiveTabColor", "TabColor"); ("tabInactiveTextColor", "TextColor"); ("tabInactiveBorderColor", "BorderColor")])
        ("MouseOver", [("tabMouseOverTabColor", "TabColor"); ("tabMouseOverTextColor", "TextColor"); ("tabMouseOverBorderColor", "BorderColor")])
        ("Active", [("tabActiveTabColor", "TabColor"); ("tabActiveTextColor", "TextColor"); ("tabActiveBorderColor", "BorderColor")])
        ("Flash", [("tabFlashTabColor", "TabColor"); ("tabFlashTextColor", "TextColor"); ("tabFlashBorderColor", "BorderColor")])
    ]

    // All color property keys for iteration
    let colorPropertyKeys =
        colorStateRows
        |> List.collect (fun (_, cols) -> cols |> List.map fst)

    // Combined list of all properties (int + color) for setEditorValues
    let allPropertyKeys =
        (intProperties.list |> List.map (fun p -> p.key)) @ colorPropertyKeys

    // Layout structure:
    // - Main panel (2 rows): upper section + color grid section
    // - Upper panel: int properties + dark mode (3 columns: label, input, reset)
    // - Color panel: theme row + header row + 4 state rows (4 columns: state label, tab color, text color, border color)
    let upperRowCount = intProperties.length + 2  // int props + dark mode + split menu (theme moved to colorPanel)
    let colorGridRowCount = 6  // theme row + header + 4 state rows

    // Main container panel (vertical stack)
    let panel =
        let panel = TableLayoutPanel()
        panel.AutoScroll <- true
        panel.Dock <- DockStyle.Fill
        panel.GrowStyle <- TableLayoutPanelGrowStyle.FixedSize
        panel.Padding <- Padding(10)
        panel.RowCount <- 2
        panel.ColumnCount <- 1
        panel.RowStyles.Add(RowStyle(SizeType.AutoSize)).ignore  // Upper section
        panel.RowStyles.Add(RowStyle(SizeType.AutoSize)).ignore  // Color grid section
        panel.ColumnStyles.Add(ColumnStyle(SizeType.Percent, 100.0f)).ignore
        panel

    // Upper panel for int properties, dark mode, and theme
    let upperPanel =
        let p = TableLayoutPanel()
        p.Dock <- DockStyle.Top
        p.AutoSize <- true
        p.GrowStyle <- TableLayoutPanelGrowStyle.FixedSize
        p.RowCount <- upperRowCount
        p.ColumnCount <- 3
        List2([0..upperRowCount - 1]).iter <| fun _ ->
            p.RowStyles.Add(RowStyle(SizeType.Absolute, 35.0f)).ignore
        p.ColumnStyles.Add(ColumnStyle(SizeType.Absolute, 150.0f)).ignore  // Label
        p.ColumnStyles.Add(ColumnStyle(SizeType.Percent, 100.0f)).ignore   // Input
        p.ColumnStyles.Add(ColumnStyle(SizeType.AutoSize)).ignore          // Reset button
        p

    // Color grid panel for color settings (separate from upper panel)
    let colorPanel =
        let p = TableLayoutPanel()
        p.Dock <- DockStyle.Top
        p.AutoSize <- true
        p.GrowStyle <- TableLayoutPanelGrowStyle.FixedSize
        p.RowCount <- colorGridRowCount
        p.ColumnCount <- 4
        List2([0..colorGridRowCount - 1]).iter <| fun _ ->
            p.RowStyles.Add(RowStyle(SizeType.Absolute, 35.0f)).ignore
        // Match upperPanel's first column width (150px)
        p.ColumnStyles.Add(ColumnStyle(SizeType.Absolute, 150.0f)).ignore  // State label
        p.ColumnStyles.Add(ColumnStyle(SizeType.Percent, 33.33f)).ignore   // Tab color
        p.ColumnStyles.Add(ColumnStyle(SizeType.Percent, 33.33f)).ignore   // Text color
        p.ColumnStyles.Add(ColumnStyle(SizeType.Percent, 33.34f)).ignore   // Border color
        p

    // Add sub-panels to main panel
    do
        panel.Controls.Add(upperPanel)
        panel.SetRow(upperPanel, 0)
        panel.SetColumn(upperPanel, 0)
        panel.Controls.Add(colorPanel)
        panel.SetRow(colorPanel, 1)
        panel.SetColumn(colorPanel, 0)

   
    let getDefaultValue key =
        let defaultAppearance = Services.program.defaultTabAppearanceInfo
        Serialize.readField defaultAppearance key

    let formatDefaultValue key =
        let value = getDefaultValue key
        match value with
        | :? int as i -> sprintf "%d" i
        | :? Color as c -> sprintf "#%06X" (c.ToArgb() &&& 0xFFFFFF)
        | _ -> value.ToString()

    // Helper to create and place an int property editor at a specific row in upperPanel
    let createIntEditorAt (prop: AppearanceProperty) (row: int) =
        let label =
            let label = Label()
            label.AutoSize <- true
            label.Text <- Localization.getString(prop.displayText)
            label.TextAlign <- ContentAlignment.MiddleLeft
            label.Anchor <- AnchorStyles.Left
            label.Margin <- Padding(0,5,0,5)
            label
        let editor = IntEditor() :> IPropEditor

        editor.control.Anchor <- AnchorStyles.Left ||| AnchorStyles.Right
        editor.control.Margin <- Padding(0,5,0,5)
        upperPanel.Controls.Add(label)
        upperPanel.Controls.Add(editor.control)
        upperPanel.SetRow(label, row)
        upperPanel.SetColumn(label, 0)
        upperPanel.SetRow(editor.control, row)
        upperPanel.SetColumn(editor.control, 1)

        let resetBtn =
            let btn = Button()
            btn.Text <- sprintf "%s:%s" (Localization.getString("Reset")) (formatDefaultValue prop.key)
            btn.Dock <- DockStyle.Fill
            btn.TextAlign <- ContentAlignment.MiddleLeft
            btn.Margin <- Padding(5,5,0,5)
            btn.Click.Add <| fun _ ->
                suppressEvents <- true
                let defaultValue = getDefaultValue prop.key
                editor.value <- defaultValue
                this.applyAppearance()
                suppressEvents <- false
            btn
        upperPanel.Controls.Add(resetBtn)
        upperPanel.SetRow(resetBtn, row)
        upperPanel.SetColumn(resetBtn, 2)

        (prop.key, editor)

    // Row indices for each section
    // Upper panel: int properties -> dark mode
    let darkModeRow = intProperties.length
    let splitMenuRow = darkModeRow + 1
    // Color panel: theme row (0) -> header row (1) -> state rows (2-5)
    let themeRow = 0  // Now in colorPanel
    let colorHeaderRow = 1
    let colorStateStartRow = 2

    // Create int property editors
    let intEditors =
        intProperties.enumerate.map (fun (i, prop) -> createIntEditorAt prop i)

    // Create color editors storage
    let mutable colorEditorsList : (string * IPropEditor) list = []

    // Create color grid header row in colorPanel
    do
        let headerLabels = [
            (Localization.getString("TabColorHeader"), 0)   // State column label
            (Localization.getString("TabColor"), 1)         // Tab color column header
            (Localization.getString("TextColor"), 2)        // Text color column header
            (Localization.getString("BorderColor"), 3)      // Border color column header
        ]
        headerLabels |> List.iter (fun (text, col) ->
            let label = Label()
            label.AutoSize <- true
            label.Text <- text
            label.TextAlign <- ContentAlignment.MiddleLeft
            label.Anchor <- AnchorStyles.Left
            label.Margin <- Padding(0, 8, 0, 5)
            colorPanel.Controls.Add(label)
            colorPanel.SetRow(label, colorHeaderRow)
            colorPanel.SetColumn(label, col)
        )

    // Create color state rows with 3 color editors each in colorPanel
    do
        colorStateRows |> List.iteri (fun rowIndex (stateName, colorCols) ->
            let row = colorStateStartRow + rowIndex

            // State label (column 0)
            let stateLabel = Label()
            stateLabel.AutoSize <- true
            stateLabel.Text <- Localization.getString(stateName)
            stateLabel.TextAlign <- ContentAlignment.MiddleLeft
            stateLabel.Anchor <- AnchorStyles.Left ||| AnchorStyles.Top
            stateLabel.Margin <- Padding(0, 7, 0, 5)
            colorPanel.Controls.Add(stateLabel)
            colorPanel.SetRow(stateLabel, row)
            colorPanel.SetColumn(stateLabel, 0)

            // Color editors (columns 1, 2, 3)
            colorCols |> List.iteri (fun colIndex (key, _) ->
                let editor = ColorEditor() :> IPropEditor
                editor.control.Dock <- DockStyle.None
                editor.control.Anchor <- AnchorStyles.Left ||| AnchorStyles.Top
                editor.control.Margin <- Padding(0, 5, 20, 5)  // right=20 for column spacing
                colorPanel.Controls.Add(editor.control)
                colorPanel.SetRow(editor.control, row)
                colorPanel.SetColumn(editor.control, colIndex + 1)  // columns 1, 2, 3
                colorEditorsList <- colorEditorsList @ [(key, editor)]
            )
        )

    // Combine all editors into a map
    let editors : Map2<string, IPropEditor> =
        (intEditors.list @ colorEditorsList)
        |> List.fold (fun (acc: Map2<string, IPropEditor>) (key, editor) -> acc.add key editor) (Map2())

    // Create dark mode checkbox at its designated row in upperPanel
    let darkModeLabel =
        let label = Label()
        label.AutoSize <- true
        label.Text <- Localization.getString("MenuDarkMode")
        label.TextAlign <- ContentAlignment.MiddleLeft
        label.Margin <- Padding(0,8,0,5)
        upperPanel.Controls.Add(label)
        upperPanel.SetRow(label, darkModeRow)
        upperPanel.SetColumn(label, 0)
        label

    let darkModeCheckbox =
        let checkbox = settingsCheckboxBool "EnableMenuDarkMode" false
        checkbox.Margin <- Padding(0,5,0,5)
        upperPanel.Controls.Add(checkbox)
        upperPanel.SetRow(checkbox, darkModeRow)
        upperPanel.SetColumn(checkbox, 1)
        checkbox

    // Create split menu checkbox at the row below dark mode
    let splitMenuLabel =
        let label = Label()
        label.AutoSize <- true
        label.Text <- Localization.getString("EnableSplitMoveSnapMenu")
        label.TextAlign <- ContentAlignment.MiddleLeft
        label.Margin <- Padding(0,8,0,5)
        upperPanel.Controls.Add(label)
        upperPanel.SetRow(label, splitMenuRow)
        upperPanel.SetColumn(label, 0)
        label

    let splitMenuCheckbox =
        let checkbox = settingsCheckboxBool "EnableSplitMoveSnapMenu" true
        checkbox.Margin <- Padding(0,5,0,5)
        upperPanel.Controls.Add(checkbox)
        upperPanel.SetRow(checkbox, splitMenuRow)
        upperPanel.SetColumn(checkbox, 1)
        checkbox

    let setEditorValues appearance =
        allPropertyKeys |> List.iter (fun key ->
            let editor = editors.find key
            try
                editor.value <- Serialize.readField appearance key
            with | _ -> ()
        )

    let appearance = Services.program.tabAppearanceInfo


    // Color key mapping for clipboard operations
    let colorKeyMap = [
        ("tabInactiveTextColor", "InactiveTextColor")
        ("tabMouseOverTextColor", "MouseOverTextColor")
        ("tabActiveTextColor", "ActiveTextColor")
        ("tabFlashTextColor", "FlashTextColor")
        ("tabInactiveTabColor", "InactiveTabColor")
        ("tabMouseOverTabColor", "MouseOverTabColor")
        ("tabActiveTabColor", "ActiveTabColor")
        ("tabFlashTabColor", "FlashTabColor")
        ("tabInactiveBorderColor", "InactiveBorderColor")
        ("tabMouseOverBorderColor", "MouseOverBorderColor")
        ("tabActiveBorderColor", "ActiveBorderColor")
        ("tabFlashBorderColor", "FlashBorderColor")
    ]

    // Load custom themes from settings
    let loadCustomThemes() : ColorThemeData list =
        try
            let json = Services.settings.root
            match json.getObjectArray("CustomColorThemes") with
            | Some(arr) ->
                arr.list |> List.choose (fun item ->
                    try
                        Some {
                            name = item.getString("name") |> Option.defaultValue ""
                            inactiveTextColor = item.getInt32("inactiveTextColor") |> Option.defaultValue 0
                            mouseOverTextColor = item.getInt32("mouseOverTextColor") |> Option.defaultValue 0
                            activeTextColor = item.getInt32("activeTextColor") |> Option.defaultValue 0
                            flashTextColor = item.getInt32("flashTextColor") |> Option.defaultValue 0
                            inactiveTabColor = item.getInt32("inactiveTabColor") |> Option.defaultValue 0
                            mouseOverTabColor = item.getInt32("mouseOverTabColor") |> Option.defaultValue 0
                            activeTabColor = item.getInt32("activeTabColor") |> Option.defaultValue 0
                            flashTabColor = item.getInt32("flashTabColor") |> Option.defaultValue 0
                            inactiveBorderColor = item.getInt32("inactiveBorderColor") |> Option.defaultValue 0
                            mouseOverBorderColor = item.getInt32("mouseOverBorderColor") |> Option.defaultValue 0
                            activeBorderColor = item.getInt32("activeBorderColor") |> Option.defaultValue 0
                            flashBorderColor = item.getInt32("flashBorderColor") |> Option.defaultValue 0
                        }
                    with | _ -> None
                )
            | None -> []
        with | _ -> []

    // Save custom themes to settings
    let saveCustomThemes (themes: ColorThemeData list) =
        let json = Services.settings.root
        let arr = themes |> List.map (fun t ->
            let item = JObject()
            item.setString("name", t.name)
            item.setInt32("inactiveTextColor", t.inactiveTextColor)
            item.setInt32("mouseOverTextColor", t.mouseOverTextColor)
            item.setInt32("activeTextColor", t.activeTextColor)
            item.setInt32("flashTextColor", t.flashTextColor)
            item.setInt32("inactiveTabColor", t.inactiveTabColor)
            item.setInt32("mouseOverTabColor", t.mouseOverTabColor)
            item.setInt32("activeTabColor", t.activeTabColor)
            item.setInt32("flashTabColor", t.flashTabColor)
            item.setInt32("inactiveBorderColor", t.inactiveBorderColor)
            item.setInt32("mouseOverBorderColor", t.mouseOverBorderColor)
            item.setInt32("activeBorderColor", t.activeBorderColor)
            item.setInt32("flashBorderColor", t.flashBorderColor)
            item
        )
        json.setObjectArray("CustomColorThemes", List2(arr))
        Services.settings.root <- json

    // Mutable list of custom themes
    let mutable customThemes = loadCustomThemes()

    // Preset themes (built-in)
    let presetThemes = ["Light"; "Light Mono"; "Dark"; "Dark Blue"; "Dark Mono"; "Dark Red Frame"]

    // Load saved Custom theme colors from settings
    let loadSavedCustomColors() : ColorThemeData option =
        try
            let json = Services.settings.root
            match json.getObject("SavedCustomColors") with
            | Some(item) ->
                // Check if item has required fields
                match item.getInt32("inactiveTextColor") with
                | Some _ ->
                    Some {
                        name = "Custom"
                        inactiveTextColor = item.getInt32("inactiveTextColor") |> Option.defaultValue 0
                        mouseOverTextColor = item.getInt32("mouseOverTextColor") |> Option.defaultValue 0
                        activeTextColor = item.getInt32("activeTextColor") |> Option.defaultValue 0
                        flashTextColor = item.getInt32("flashTextColor") |> Option.defaultValue 0
                        inactiveTabColor = item.getInt32("inactiveTabColor") |> Option.defaultValue 0
                        mouseOverTabColor = item.getInt32("mouseOverTabColor") |> Option.defaultValue 0
                        activeTabColor = item.getInt32("activeTabColor") |> Option.defaultValue 0
                        flashTabColor = item.getInt32("flashTabColor") |> Option.defaultValue 0
                        inactiveBorderColor = item.getInt32("inactiveBorderColor") |> Option.defaultValue 0
                        mouseOverBorderColor = item.getInt32("mouseOverBorderColor") |> Option.defaultValue 0
                        activeBorderColor = item.getInt32("activeBorderColor") |> Option.defaultValue 0
                        flashBorderColor = item.getInt32("flashBorderColor") |> Option.defaultValue 0
                    }
                | None -> None
            | None -> None
        with | _ -> None

    // Save saved Custom theme colors to settings
    let saveSavedCustomColors (colors: ColorThemeData option) =
        let json = Services.settings.root
        match colors with
        | Some c ->
            let item = JObject()
            item.setInt32("inactiveTextColor", c.inactiveTextColor)
            item.setInt32("mouseOverTextColor", c.mouseOverTextColor)
            item.setInt32("activeTextColor", c.activeTextColor)
            item.setInt32("flashTextColor", c.flashTextColor)
            item.setInt32("inactiveTabColor", c.inactiveTabColor)
            item.setInt32("mouseOverTabColor", c.mouseOverTabColor)
            item.setInt32("activeTabColor", c.activeTabColor)
            item.setInt32("flashTabColor", c.flashTabColor)
            item.setInt32("inactiveBorderColor", c.inactiveBorderColor)
            item.setInt32("mouseOverBorderColor", c.mouseOverBorderColor)
            item.setInt32("activeBorderColor", c.activeBorderColor)
            item.setInt32("flashBorderColor", c.flashBorderColor)
            json.setObject("SavedCustomColors", item)
        | None ->
            // Remove the key if None
            json.update("SavedCustomColors", None)
        Services.settings.root <- json

    // Saved Custom theme colors (for restoring when Custom is selected)
    // Load from settings on initialization
    let mutable savedCustomColors : ColorThemeData option = loadSavedCustomColors()

    // Build ComboBox items list (no separator items - separators are drawn in DrawItem)
    // UnsavedCustom (Custom) is always shown - defaults to Light theme colors if not saved
    let buildThemeItems() =
        let items = ResizeArray<ThemeItem>()
        // Presets
        presetThemes |> List.iter (fun name -> items.Add(Preset name))
        // Custom themes
        customThemes |> List.iter (fun t -> items.Add(CustomTheme t.name))
        // Always add UnsavedCustom (Custom) - will use Light theme colors as default if not saved
        items.Add(UnsavedCustom)
        items.ToArray() |> Array.toList

    let mutable themeItems = buildThemeItems()

    // Color theme ComboBox with owner-draw for separator lines
    let colorThemeComboBox =
        let combo = new ComboBox()
        combo.DropDownStyle <- ComboBoxStyle.DropDownList
        combo.DrawMode <- DrawMode.OwnerDrawVariable
        combo.ItemHeight <- 20
        combo.Margin <- Padding(0, 0, 3, 0)  // Remove left margin, small right margin
        combo

    // Save button, Edit button, and Up/Down buttons (created early so we can reference them)
    let saveBtn = new Button()
    let editBtn = new Button()
    let upBtn = new Button()
    let downBtn = new Button()

    // Refresh ComboBox items
    let refreshComboBoxItems() =
        themeItems <- buildThemeItems()
        suppressEvents <- true
        colorThemeComboBox.Items.Clear()
        themeItems |> List.iter (fun item ->
            match item with
            | Preset name -> colorThemeComboBox.Items.Add(name) |> ignore
            | CustomTheme name -> colorThemeComboBox.Items.Add(name) |> ignore
            | UnsavedCustom -> colorThemeComboBox.Items.Add("Custom") |> ignore
        )
        suppressEvents <- false

    // Initialize ComboBox items
    do refreshComboBoxItems()

    // Get current colors as ColorThemeData
    let getCurrentColors() =
        let getColor key =
            let editor = editors.find key
            let c = unbox<Color>(editor.value)
            c.ToArgb() &&& 0xFFFFFF
        {
            name = ""
            inactiveTextColor = getColor "tabInactiveTextColor"
            mouseOverTextColor = getColor "tabMouseOverTextColor"
            activeTextColor = getColor "tabActiveTextColor"
            flashTextColor = getColor "tabFlashTextColor"
            inactiveTabColor = getColor "tabInactiveTabColor"
            mouseOverTabColor = getColor "tabMouseOverTabColor"
            activeTabColor = getColor "tabActiveTabColor"
            flashTabColor = getColor "tabFlashTabColor"
            inactiveBorderColor = getColor "tabInactiveBorderColor"
            mouseOverBorderColor = getColor "tabMouseOverBorderColor"
            activeBorderColor = getColor "tabActiveBorderColor"
            flashBorderColor = getColor "tabFlashBorderColor"
        }

    // Check if colors match a theme
    let colorsMatch (theme: ColorThemeData) (current: ColorThemeData) =
        theme.inactiveTextColor = current.inactiveTextColor &&
        theme.mouseOverTextColor = current.mouseOverTextColor &&
        theme.activeTextColor = current.activeTextColor &&
        theme.flashTextColor = current.flashTextColor &&
        theme.inactiveTabColor = current.inactiveTabColor &&
        theme.mouseOverTabColor = current.mouseOverTabColor &&
        theme.activeTabColor = current.activeTabColor &&
        theme.flashTabColor = current.flashTabColor &&
        theme.inactiveBorderColor = current.inactiveBorderColor &&
        theme.mouseOverBorderColor = current.mouseOverBorderColor &&
        theme.activeBorderColor = current.activeBorderColor &&
        theme.flashBorderColor = current.flashBorderColor

    // Get preset theme colors
    let getPresetColors (name: string) =
        let appearance =
            match name with
            | "Light" -> Services.program.defaultTabAppearanceInfo
            | "Light Mono" -> Services.program.lightMonoTabAppearanceInfo
            | "Dark" -> Services.program.darkModeTabAppearanceInfo
            | "Dark Blue" -> Services.program.darkModeBlueTabAppearanceInfo
            | "Dark Mono" -> Services.program.darkMonoTabAppearanceInfo
            | "Dark Red Frame" -> Services.program.darkRedFrameTabAppearanceInfo
            | _ -> Services.program.defaultTabAppearanceInfo
        {
            name = name
            inactiveTextColor = appearance.tabInactiveTextColor.ToArgb() &&& 0xFFFFFF
            mouseOverTextColor = appearance.tabMouseOverTextColor.ToArgb() &&& 0xFFFFFF
            activeTextColor = appearance.tabActiveTextColor.ToArgb() &&& 0xFFFFFF
            flashTextColor = appearance.tabFlashTextColor.ToArgb() &&& 0xFFFFFF
            inactiveTabColor = appearance.tabInactiveTabColor.ToArgb() &&& 0xFFFFFF
            mouseOverTabColor = appearance.tabMouseOverTabColor.ToArgb() &&& 0xFFFFFF
            activeTabColor = appearance.tabActiveTabColor.ToArgb() &&& 0xFFFFFF
            flashTabColor = appearance.tabFlashTabColor.ToArgb() &&& 0xFFFFFF
            inactiveBorderColor = appearance.tabInactiveBorderColor.ToArgb() &&& 0xFFFFFF
            mouseOverBorderColor = appearance.tabMouseOverBorderColor.ToArgb() &&& 0xFFFFFF
            activeBorderColor = appearance.tabActiveBorderColor.ToArgb() &&& 0xFFFFFF
            flashBorderColor = appearance.tabFlashBorderColor.ToArgb() &&& 0xFFFFFF
        }

    // Find matching theme index
    let findMatchingTheme() =
        let current = getCurrentColors()
        let mutable found = -1
        themeItems |> List.iteri (fun i item ->
            if found < 0 then
                match item with
                | Preset name ->
                    if colorsMatch (getPresetColors name) current then found <- i
                | CustomTheme name ->
                    match customThemes |> List.tryFind (fun t -> t.name = name) with
                    | Some theme -> if colorsMatch theme current then found <- i
                    | None -> ()
                | _ -> ()
        )
        found

    // Get custom theme index in customThemes list by name
    let getCustomThemeIndex (name: string) =
        customThemes |> List.tryFindIndex (fun t -> t.name = name)

    // Update button state based on selection
    let updateButtonState() =
        if colorThemeComboBox.SelectedIndex >= 0 && colorThemeComboBox.SelectedIndex < themeItems.Length then
            match themeItems.[colorThemeComboBox.SelectedIndex] with
            | Preset _ ->
                // Hide all buttons for preset themes
                saveBtn.Visible <- false
                editBtn.Visible <- false
                upBtn.Visible <- false
                downBtn.Visible <- false
            | CustomTheme name ->
                // Show edit button for custom themes
                saveBtn.Visible <- false
                editBtn.Visible <- true
                editBtn.Enabled <- true
                upBtn.Visible <- true
                downBtn.Visible <- true
                // Enable/disable up/down buttons based on position
                match getCustomThemeIndex name with
                | Some idx ->
                    upBtn.Enabled <- idx > 0
                    downBtn.Enabled <- idx < customThemes.Length - 1
                | None ->
                    upBtn.Enabled <- false
                    downBtn.Enabled <- false
            | UnsavedCustom ->
                // Show save button for unsaved custom
                saveBtn.Visible <- true
                saveBtn.Enabled <- true
                editBtn.Visible <- false
                upBtn.Visible <- false
                downBtn.Visible <- false

    // Helper function to apply color preset
    let applyColorPreset (presetAppearance: TabAppearanceInfo) =
        suppressEvents <- true
        let currentAppearance = Services.program.tabAppearanceInfo
        let mergedAppearance = {
            tabHeight = currentAppearance.tabHeight
            tabMaxWidth = currentAppearance.tabMaxWidth
            tabOverlap = currentAppearance.tabOverlap
            tabHeightOffset = currentAppearance.tabHeightOffset
            tabIndentFlipped = currentAppearance.tabIndentFlipped
            tabIndentNormal = currentAppearance.tabIndentNormal
            tabInactiveTextColor = presetAppearance.tabInactiveTextColor
            tabMouseOverTextColor = presetAppearance.tabMouseOverTextColor
            tabActiveTextColor = presetAppearance.tabActiveTextColor
            tabFlashTextColor = presetAppearance.tabFlashTextColor
            tabInactiveTabColor = presetAppearance.tabInactiveTabColor
            tabMouseOverTabColor = presetAppearance.tabMouseOverTabColor
            tabActiveTabColor = presetAppearance.tabActiveTabColor
            tabFlashTabColor = presetAppearance.tabFlashTabColor
            tabInactiveBorderColor = presetAppearance.tabInactiveBorderColor
            tabMouseOverBorderColor = presetAppearance.tabMouseOverBorderColor
            tabActiveBorderColor = presetAppearance.tabActiveBorderColor
            tabFlashBorderColor = presetAppearance.tabFlashBorderColor
        }
        Services.settings.setValue("tabAppearance", box(mergedAppearance))
        setEditorValues mergedAppearance
        suppressEvents <- false

    // Apply custom theme colors
    let applyCustomTheme (theme: ColorThemeData) =
        suppressEvents <- true
        let currentAppearance = Services.program.tabAppearanceInfo
        let toColor value = Color.FromArgb(255, Color.FromArgb(value))
        let mergedAppearance = {
            tabHeight = currentAppearance.tabHeight
            tabMaxWidth = currentAppearance.tabMaxWidth
            tabOverlap = currentAppearance.tabOverlap
            tabHeightOffset = currentAppearance.tabHeightOffset
            tabIndentFlipped = currentAppearance.tabIndentFlipped
            tabIndentNormal = currentAppearance.tabIndentNormal
            tabInactiveTextColor = toColor theme.inactiveTextColor
            tabMouseOverTextColor = toColor theme.mouseOverTextColor
            tabActiveTextColor = toColor theme.activeTextColor
            tabFlashTextColor = toColor theme.flashTextColor
            tabInactiveTabColor = toColor theme.inactiveTabColor
            tabMouseOverTabColor = toColor theme.mouseOverTabColor
            tabActiveTabColor = toColor theme.activeTabColor
            tabFlashTabColor = toColor theme.flashTabColor
            tabInactiveBorderColor = toColor theme.inactiveBorderColor
            tabMouseOverBorderColor = toColor theme.mouseOverBorderColor
            tabActiveBorderColor = toColor theme.activeBorderColor
            tabFlashBorderColor = toColor theme.flashBorderColor
        }
        Services.settings.setValue("tabAppearance", box(mergedAppearance))
        setEditorValues mergedAppearance
        suppressEvents <- false

    // Set up ComboBox measure event - all items have same height
    do
        colorThemeComboBox.MeasureItem.Add <| fun e ->
            e.ItemHeight <- 20

    // Helper to check if separator should be drawn below an item
    let shouldDrawSeparatorBelow (index: int) =
        if index < 0 || index >= themeItems.Length then false
        else
            match themeItems.[index] with
            | Preset "Dark Red Frame" -> true  // Always draw separator after Dark Red Frame
            | CustomTheme _ ->
                // Draw separator after last custom theme (before UnsavedCustom)
                if index + 1 < themeItems.Length then
                    match themeItems.[index + 1] with
                    | UnsavedCustom -> true
                    | _ -> false
                else false
            | _ -> false

    // Set up ComboBox draw event for separator lines
    do
        colorThemeComboBox.DrawItem.Add <| fun e ->
            e.DrawBackground()
            if e.Index >= 0 && e.Index < themeItems.Length then
                let text =
                    match themeItems.[e.Index] with
                    | Preset name | CustomTheme name -> name
                    | UnsavedCustom -> "Custom"
                use brush = new SolidBrush(e.ForeColor)
                // Calculate vertical center position for text
                let textSize = e.Graphics.MeasureString(text, e.Font)
                let y = float32 e.Bounds.Top + (float32 e.Bounds.Height - textSize.Height) / 2.0f
                e.Graphics.DrawString(text, e.Font, brush, float32 e.Bounds.Left + 2.0f, y)
                // Draw separator line below if needed
                if shouldDrawSeparatorBelow e.Index then
                    let lineY = e.Bounds.Bottom - 1
                    use pen = new Pen(Color.Gray, 1.0f)
                    e.Graphics.DrawLine(pen, e.Bounds.Left + 2, lineY, e.Bounds.Right - 2, lineY)
            e.DrawFocusRectangle()

    // Set up ComboBox event handler
    do
        colorThemeComboBox.SelectedIndexChanged.Add <| fun _ ->
            if not suppressEvents then
                let currentIndex = colorThemeComboBox.SelectedIndex
                if currentIndex >= 0 && currentIndex < themeItems.Length then
                    match themeItems.[currentIndex] with
                    | Preset "Light" ->
                        applyColorPreset(Services.program.defaultTabAppearanceInfo)
                    | Preset "Light Mono" ->
                        applyColorPreset(Services.program.lightMonoTabAppearanceInfo)
                    | Preset "Dark" ->
                        applyColorPreset(Services.program.darkModeTabAppearanceInfo)
                    | Preset "Dark Blue" ->
                        applyColorPreset(Services.program.darkModeBlueTabAppearanceInfo)
                    | Preset "Dark Mono" ->
                        applyColorPreset(Services.program.darkMonoTabAppearanceInfo)
                    | Preset "Dark Red Frame" ->
                        applyColorPreset(Services.program.darkRedFrameTabAppearanceInfo)
                    | Preset _ -> ()
                    | CustomTheme name ->
                        match customThemes |> List.tryFind (fun t -> t.name = name) with
                        | Some theme -> applyCustomTheme theme
                        | None -> ()
                    | UnsavedCustom ->
                        // Restore saved Custom colors if available, else use Light theme colors as default
                        match savedCustomColors with
                        | Some colors -> applyCustomTheme colors
                        | None -> applyCustomTheme (getPresetColors "Light")
                updateButtonState()

    // Helper to switch to Custom when colors are manually changed
    let switchToCustom() =
        if not suppressEvents then
            // First check if current colors match any theme
            let matchIndex = findMatchingTheme()
            if matchIndex >= 0 then
                suppressEvents <- true
                colorThemeComboBox.SelectedIndex <- matchIndex
                suppressEvents <- false
            else
                // Save current colors as Custom theme
                savedCustomColors <- Some(getCurrentColors())
                // Persist to settings file
                saveSavedCustomColors savedCustomColors
                // Refresh ComboBox items to include UnsavedCustom
                refreshComboBoxItems()
                // Switch to Custom (last item)
                let customIndex = themeItems.Length - 1
                if colorThemeComboBox.SelectedIndex <> customIndex then
                    suppressEvents <- true
                    colorThemeComboBox.SelectedIndex <- customIndex
                    suppressEvents <- false
            updateButtonState()

    // Helper function to convert theme to JSON object string
    let themeToJsonObject (theme: ColorThemeData) =
        sprintf "  {\n    \"ThemeName\": \"%s\",\n    \"InactiveTextColor\": \"#%06X\",\n    \"MouseOverTextColor\": \"#%06X\",\n    \"ActiveTextColor\": \"#%06X\",\n    \"FlashTextColor\": \"#%06X\",\n    \"InactiveTabColor\": \"#%06X\",\n    \"MouseOverTabColor\": \"#%06X\",\n    \"ActiveTabColor\": \"#%06X\",\n    \"FlashTabColor\": \"#%06X\",\n    \"InactiveBorderColor\": \"#%06X\",\n    \"MouseOverBorderColor\": \"#%06X\",\n    \"ActiveBorderColor\": \"#%06X\",\n    \"FlashBorderColor\": \"#%06X\"\n  }" theme.name theme.inactiveTextColor theme.mouseOverTextColor theme.activeTextColor theme.flashTextColor theme.inactiveTabColor theme.mouseOverTabColor theme.activeTabColor theme.flashTabColor theme.inactiveBorderColor theme.mouseOverBorderColor theme.activeBorderColor theme.flashBorderColor

    // Helper function to convert themes list to JSON array string
    let themesToJson (themes: ColorThemeData list) =
        let jsonObjects = themes |> List.map themeToJsonObject
        "[\n" + String.Join(",\n", jsonObjects) + "\n]"

    // Mutable reference to "Copy All Saved Themes" menu item for dynamic enable/disable
    let mutable copyAllSavedMenuItem : System.Windows.Forms.ToolStripMenuItem option = None

    // Update the enabled state of "Copy All Saved Themes" menu item
    let updateCopyAllSavedMenuState() =
        match copyAllSavedMenuItem with
        | Some item -> item.Enabled <- customThemes.Length > 0
        | None -> ()

    // Clipboard operations dropdown using DropdownButton component
    let clipboardDropdownBtn =
        let dropdown = DropdownButton(Localization.getString("ClipboardOperations"), DropdownButtonColor)

        // Menu item 1: Copy Selected Theme
        dropdown.AddItem(Localization.getString("CopySelectedTheme"), fun () ->
            try
                let currentColors = getCurrentColors()
                let themeName =
                    if colorThemeComboBox.SelectedIndex >= 0 && colorThemeComboBox.SelectedIndex < themeItems.Length then
                        match themeItems.[colorThemeComboBox.SelectedIndex] with
                        | Preset name -> name
                        | CustomTheme name -> name
                        | UnsavedCustom -> "Custom"
                    else
                        "Custom"
                let theme = { currentColors with name = themeName }
                let jsonText = themesToJson [theme]
                if not (String.IsNullOrEmpty(jsonText)) then
                    Clipboard.SetText(jsonText)
            with | _ -> ()
        ) |> ignore

        // Separator 1
        dropdown.AddSeparator()

        // Menu item 2: Copy All Preset Themes
        dropdown.AddItem(Localization.getString("CopyAllPresetThemes"), fun () ->
            try
                let presetThemesList = presetThemes |> List.map getPresetColors
                let jsonText = themesToJson presetThemesList
                if not (String.IsNullOrEmpty(jsonText)) then
                    Clipboard.SetText(jsonText)
            with | _ -> ()
        ) |> ignore

        // Menu item 3: Copy All Saved Themes
        let copyAllSavedItem = dropdown.AddItem(Localization.getString("CopyAllSavedThemes"), fun () ->
            try
                if customThemes.Length > 0 then
                    let jsonText = themesToJson customThemes
                    if not (String.IsNullOrEmpty(jsonText)) then
                        Clipboard.SetText(jsonText)
            with | _ -> ()
        )
        copyAllSavedMenuItem <- Some copyAllSavedItem
        copyAllSavedItem.Enabled <- customThemes.Length > 0

        // Separator 2
        dropdown.AddSeparator()

        // Menu item 4: Paste Themes
        dropdown.AddItem(Localization.getString("PasteThemes"), fun () ->
            try
                if Clipboard.ContainsText() then
                    let clipboardText = Clipboard.GetText()
                    // Try to parse as JSON array
                    let jsonArray = JArray.Parse(clipboardText)
                    suppressEvents <- true
                    let mutable lastAppliedTheme : ColorThemeData option = None
                    for item in jsonArray do
                        let jObj = item :?> JObject
                        // If ThemeName is missing or empty, treat as "Custom"
                        let themeName =
                            match jObj.getString("ThemeName") with
                            | Some name when not (String.IsNullOrWhiteSpace(name)) -> name
                            | _ -> "Custom"
                        // Parse colors (optional - only apply if present)
                        let parseColor (key: string) (existing: int) =
                            match jObj.getString(key) with
                            | Some hex when hex.StartsWith("#") && hex.Length = 7 ->
                                try
                                    Int32.Parse(hex.Substring(1), System.Globalization.NumberStyles.HexNumber)
                                with | _ -> existing
                            | _ -> existing

                        // Get base colors (current Custom colors or zeros)
                        let baseColors = savedCustomColors |> Option.defaultValue {
                            name = ""
                            inactiveTextColor = 0
                            mouseOverTextColor = 0
                            activeTextColor = 0
                            flashTextColor = 0
                            inactiveTabColor = 0
                            mouseOverTabColor = 0
                            activeTabColor = 0
                            flashTabColor = 0
                            inactiveBorderColor = 0
                            mouseOverBorderColor = 0
                            activeBorderColor = 0
                            flashBorderColor = 0
                        }

                        let newTheme = {
                            name = themeName
                            inactiveTextColor = parseColor "InactiveTextColor" baseColors.inactiveTextColor
                            mouseOverTextColor = parseColor "MouseOverTextColor" baseColors.mouseOverTextColor
                            activeTextColor = parseColor "ActiveTextColor" baseColors.activeTextColor
                            flashTextColor = parseColor "FlashTextColor" baseColors.flashTextColor
                            inactiveTabColor = parseColor "InactiveTabColor" baseColors.inactiveTabColor
                            mouseOverTabColor = parseColor "MouseOverTabColor" baseColors.mouseOverTabColor
                            activeTabColor = parseColor "ActiveTabColor" baseColors.activeTabColor
                            flashTabColor = parseColor "FlashTabColor" baseColors.flashTabColor
                            inactiveBorderColor = parseColor "InactiveBorderColor" baseColors.inactiveBorderColor
                            mouseOverBorderColor = parseColor "MouseOverBorderColor" baseColors.mouseOverBorderColor
                            activeBorderColor = parseColor "ActiveBorderColor" baseColors.activeBorderColor
                            flashBorderColor = parseColor "FlashBorderColor" baseColors.flashBorderColor
                        }

                        if themeName = "Custom" then
                            // Apply to Custom (unsaved)
                            savedCustomColors <- Some newTheme
                            saveSavedCustomColors savedCustomColors
                            lastAppliedTheme <- Some newTheme
                        else
                            // Check if theme with this name exists
                            let existingIndex = customThemes |> List.tryFindIndex (fun t -> t.name = themeName)
                            match existingIndex with
                            | Some idx ->
                                // Overwrite existing theme
                                customThemes <- customThemes |> List.mapi (fun i t ->
                                    if i = idx then newTheme else t
                                )
                            | None ->
                                // Add new theme
                                customThemes <- customThemes @ [newTheme]
                            saveCustomThemes customThemes
                            updateCopyAllSavedMenuState()
                            lastAppliedTheme <- Some newTheme

                    // Refresh UI and apply last theme
                    refreshComboBoxItems()
                    match lastAppliedTheme with
                    | Some theme ->
                        applyCustomTheme theme
                        // Select the theme in ComboBox
                        let themeIndex = themeItems |> List.tryFindIndex (fun item ->
                            match item with
                            | CustomTheme n -> n = theme.name
                            | UnsavedCustom -> theme.name = "Custom"
                            | _ -> false
                        )
                        match themeIndex with
                        | Some idx -> colorThemeComboBox.SelectedIndex <- idx
                        | None -> ()
                    | None -> ()
                    suppressEvents <- false
                    updateButtonState()
            with | _ -> ()
        ) |> ignore

        dropdown.Control

    // Reserved theme names that cannot be used for custom themes
    // Note: Light, Dark, Dark Blue can be used as custom theme names (will create a custom theme with that name)
    let reservedThemeNames = ["Custom"]

    // Show Edit Theme dialog for renaming or deleting custom themes
    // Returns: Some(name) if OK pressed, None if cancelled
    // Empty name means delete the theme
    // OK button is disabled until text is changed from current value
    let showEditThemeDialog (currentName: string) =
        use form = new Form()
        form.Text <- Localization.getString("EditThemeTitle")
        form.Size <- Size(350, 170)
        form.StartPosition <- FormStartPosition.CenterParent
        form.FormBorderStyle <- FormBorderStyle.FixedDialog
        form.MaximizeBox <- false
        form.MinimizeBox <- false
        form.TopMost <- true

        let label = new Label()
        label.Text <- Localization.getString("EditThemePrompt")
        label.Location <- Point(10, 20)
        label.AutoSize <- true

        let hintLabel = new Label()
        hintLabel.Text <- Localization.getString("DeleteThemeHint")
        hintLabel.Location <- Point(10, 40)
        hintLabel.AutoSize <- true
        hintLabel.ForeColor <- Color.Gray

        let textBox = new TextBox()
        textBox.Text <- currentName
        textBox.Location <- Point(10, 65)
        textBox.Size <- Size(310, 25)

        let okBtn = new Button()
        okBtn.Text <- "OK"
        okBtn.DialogResult <- DialogResult.OK
        okBtn.Location <- Point(160, 100)
        okBtn.Size <- Size(75, 25)
        okBtn.Enabled <- false  // Disabled until text changes

        let cancelBtn = new Button()
        cancelBtn.Text <- "Cancel"
        cancelBtn.DialogResult <- DialogResult.Cancel
        cancelBtn.Location <- Point(245, 100)
        cancelBtn.Size <- Size(75, 25)

        // Enable OK button when text changes from current value
        textBox.TextChanged.Add <| fun _ ->
            okBtn.Enabled <- textBox.Text <> currentName

        form.Controls.Add(label)
        form.Controls.Add(hintLabel)
        form.Controls.Add(textBox)
        form.Controls.Add(okBtn)
        form.Controls.Add(cancelBtn)
        form.AcceptButton <- okBtn
        form.CancelButton <- cancelBtn

        let parentForm = panel.FindForm()
        let result =
            if parentForm <> null then
                form.ShowDialog(parentForm)
            else
                form.ShowDialog()
        if result = DialogResult.OK then
            Some(textBox.Text.Trim())
        else
            None

    // Show Save As dialog with ComboBox for theme name selection (used for Save As)
    // Returns: Some(name, isOverwrite) if OK pressed, None if cancelled
    let showSaveAsDialog (title: string) =
        use form = new Form()
        form.Text <- title
        form.Size <- Size(350, 150)
        form.StartPosition <- FormStartPosition.CenterParent
        form.FormBorderStyle <- FormBorderStyle.FixedDialog
        form.MaximizeBox <- false
        form.MinimizeBox <- false
        form.TopMost <- true

        let label = new Label()
        label.Text <- Localization.getString("EnterThemeName")
        label.Location <- Point(10, 20)
        label.AutoSize <- true

        let comboBox = new ComboBox()
        comboBox.Location <- Point(10, 45)
        comboBox.Size <- Size(310, 25)
        comboBox.DropDownStyle <- ComboBoxStyle.DropDown  // Allow text input
        // Add existing custom theme names to ComboBox
        customThemes |> List.iter (fun t -> comboBox.Items.Add(t.name) |> ignore)

        let okBtn = new Button()
        okBtn.Text <- "OK"
        okBtn.DialogResult <- DialogResult.OK
        okBtn.Location <- Point(160, 80)
        okBtn.Size <- Size(75, 25)
        okBtn.Enabled <- false  // Initially disabled

        let cancelBtn = new Button()
        cancelBtn.Text <- "Cancel"
        cancelBtn.DialogResult <- DialogResult.Cancel
        cancelBtn.Location <- Point(245, 80)
        cancelBtn.Size <- Size(75, 25)

        // Validate input and update OK button state
        let validateInput() =
            let text = comboBox.Text.Trim()
            let isReserved = reservedThemeNames |> List.exists (fun n ->
                String.Equals(n, text, StringComparison.OrdinalIgnoreCase))
            okBtn.Enabled <- not (String.IsNullOrWhiteSpace(text)) && not isReserved

        comboBox.TextChanged.Add(fun _ -> validateInput())
        comboBox.SelectedIndexChanged.Add(fun _ -> validateInput())

        form.Controls.Add(label)
        form.Controls.Add(comboBox)
        form.Controls.Add(okBtn)
        form.Controls.Add(cancelBtn)
        form.AcceptButton <- okBtn
        form.CancelButton <- cancelBtn

        let parentForm = panel.FindForm()
        let result =
            if parentForm <> null then
                form.ShowDialog(parentForm)
            else
                form.ShowDialog()
        if result = DialogResult.OK then
            let name = comboBox.Text.Trim()
            let isOverwrite = customThemes |> List.exists (fun t -> t.name = name)
            Some(name, isOverwrite)
        else
            None

    // Color Theme label (placed in column 0)
    let themeLabel =
        let label = Label()
        label.AutoSize <- true
        label.Text <- Localization.getString("ColorTheme")
        label.TextAlign <- ContentAlignment.MiddleLeft
        label.Margin <- Padding(0, 10, 0, 5)  // Add top margin
        label

    // Color Theme dropdown and Save/Rename button (placed in colorPanel column 1)
    // Simple FlowLayoutPanel for ComboBox and buttons
    let themePanel =
        let container = new FlowLayoutPanel()
        container.FlowDirection <- FlowDirection.LeftToRight
        container.AutoSize <- true
        container.WrapContents <- false
        container.Dock <- DockStyle.Left
        container.Margin <- Padding(0, 5, 0, 5)
        container.Padding <- Padding(0, 0, 0, 0)

        // Configure saveBtn (for UnsavedCustom - Save As)
        saveBtn.Text <- Localization.getString("SaveAs")
        saveBtn.AutoSize <- true
        saveBtn.AutoSizeMode <- AutoSizeMode.GrowAndShrink
        saveBtn.Margin <- Padding(3, -1, 0, 0)  // Align vertically with ComboBox, 1px up
        saveBtn.Enabled <- false
        saveBtn.Visible <- false

        saveBtn.Click.Add <| fun _ ->
            if colorThemeComboBox.SelectedIndex >= 0 && colorThemeComboBox.SelectedIndex < themeItems.Length then
                match themeItems.[colorThemeComboBox.SelectedIndex] with
                | UnsavedCustom ->
                    // Save As - use ComboBox dialog for theme name selection
                    match showSaveAsDialog (Localization.getString("SaveThemeTitle")) with
                    | Some (name, isOverwrite) ->
                        let newTheme = { getCurrentColors() with name = name }
                        if isOverwrite then
                            // Overwrite existing theme with same name
                            customThemes <- customThemes |> List.map (fun t ->
                                if t.name = name then newTheme else t
                            )
                        else
                            // Add new theme
                            customThemes <- customThemes @ [newTheme]
                        saveCustomThemes customThemes
                        updateCopyAllSavedMenuState()
                        refreshComboBoxItems()
                        // Select the saved theme
                        let newIndex = themeItems |> List.findIndex (fun item ->
                            match item with
                            | CustomTheme n -> n = name
                            | _ -> false
                        )
                        colorThemeComboBox.SelectedIndex <- newIndex
                        updateButtonState()
                    | None -> ()
                | _ -> ()

        // Configure editBtn (for CustomTheme - Edit/Rename/Delete)
        editBtn.Text <- Localization.getString("Edit")
        editBtn.AutoSize <- true
        editBtn.AutoSizeMode <- AutoSizeMode.GrowAndShrink
        editBtn.Margin <- Padding(3, -1, 0, 0)  // Align vertically with ComboBox, 1px up
        editBtn.Enabled <- false
        editBtn.Visible <- false

        editBtn.Click.Add <| fun _ ->
            if colorThemeComboBox.SelectedIndex >= 0 && colorThemeComboBox.SelectedIndex < themeItems.Length then
                match themeItems.[colorThemeComboBox.SelectedIndex] with
                | CustomTheme currentName ->
                    // Edit existing custom theme
                    match showEditThemeDialog currentName with
                    | Some newName when String.IsNullOrWhiteSpace(newName) ->
                        // Empty name means delete the theme
                        customThemes <- customThemes |> List.filter (fun t -> t.name <> currentName)
                        saveCustomThemes customThemes
                        updateCopyAllSavedMenuState()
                        // Ensure Custom item exists (save current colors if needed)
                        if savedCustomColors.IsNone then
                            savedCustomColors <- Some(getCurrentColors())
                            saveSavedCustomColors savedCustomColors
                        refreshComboBoxItems()
                        // Switch to Custom (last item) - colors remain unchanged from current
                        let customIndex = themeItems.Length - 1
                        colorThemeComboBox.SelectedIndex <- customIndex
                        updateButtonState()
                    | Some newName ->
                        // Rename the theme
                        customThemes <- customThemes |> List.map (fun t ->
                            if t.name = currentName then { t with name = newName }
                            else t
                        )
                        saveCustomThemes customThemes
                        refreshComboBoxItems()
                        // Select the renamed theme
                        let newIndex = themeItems |> List.findIndex (fun item ->
                            match item with
                            | CustomTheme n -> n = newName
                            | _ -> false
                        )
                        colorThemeComboBox.SelectedIndex <- newIndex
                        updateButtonState()
                    | None -> ()
                | _ -> ()

        // Configure upBtn
        upBtn.Text <- Localization.getString("Up")
        upBtn.AutoSize <- true
        upBtn.AutoSizeMode <- AutoSizeMode.GrowAndShrink
        upBtn.MinimumSize <- Size(30, 0)
        upBtn.TextAlign <- ContentAlignment.MiddleCenter
        upBtn.Margin <- Padding(3, -1, 0, 0)  // Align with saveEditBtn
        upBtn.Enabled <- false
        upBtn.Visible <- false

        upBtn.Click.Add <| fun _ ->
            if colorThemeComboBox.SelectedIndex >= 0 && colorThemeComboBox.SelectedIndex < themeItems.Length then
                match themeItems.[colorThemeComboBox.SelectedIndex] with
                | CustomTheme name ->
                    match getCustomThemeIndex name with
                    | Some idx when idx > 0 ->
                        // Swap with previous theme
                        let prev = customThemes.[idx - 1]
                        let curr = customThemes.[idx]
                        customThemes <- customThemes |> List.mapi (fun i t ->
                            if i = idx - 1 then curr
                            elif i = idx then prev
                            else t
                        )
                        saveCustomThemes customThemes
                        refreshComboBoxItems()
                        // Select the moved theme
                        let newIndex = themeItems |> List.findIndex (fun item ->
                            match item with
                            | CustomTheme n -> n = name
                            | _ -> false
                        )
                        colorThemeComboBox.SelectedIndex <- newIndex
                        updateButtonState()
                    | _ -> ()
                | _ -> ()

        // Configure downBtn
        downBtn.Text <- Localization.getString("Down")
        downBtn.AutoSize <- true
        downBtn.AutoSizeMode <- AutoSizeMode.GrowAndShrink
        downBtn.MinimumSize <- Size(30, 0)
        downBtn.TextAlign <- ContentAlignment.MiddleCenter
        downBtn.Margin <- Padding(3, -1, 0, 0)  // Align with saveEditBtn
        downBtn.Enabled <- false
        downBtn.Visible <- false

        downBtn.Click.Add <| fun _ ->
            if colorThemeComboBox.SelectedIndex >= 0 && colorThemeComboBox.SelectedIndex < themeItems.Length then
                match themeItems.[colorThemeComboBox.SelectedIndex] with
                | CustomTheme name ->
                    match getCustomThemeIndex name with
                    | Some idx when idx < customThemes.Length - 1 ->
                        // Swap with next theme
                        let curr = customThemes.[idx]
                        let next = customThemes.[idx + 1]
                        customThemes <- customThemes |> List.mapi (fun i t ->
                            if i = idx then next
                            elif i = idx + 1 then curr
                            else t
                        )
                        saveCustomThemes customThemes
                        refreshComboBoxItems()
                        // Select the moved theme
                        let newIndex = themeItems |> List.findIndex (fun item ->
                            match item with
                            | CustomTheme n -> n = name
                            | _ -> false
                        )
                        colorThemeComboBox.SelectedIndex <- newIndex
                        updateButtonState()
                    | _ -> ()
                | _ -> ()

        // Add controls to themePanel (FlowLayoutPanel)
        container.Controls.Add(colorThemeComboBox)
        container.Controls.Add(saveBtn)
        container.Controls.Add(editBtn)
        container.Controls.Add(upBtn)
        container.Controls.Add(downBtn)

        container

    do
        // Increase theme row height to prevent ComboBox from being cut off
        colorPanel.RowStyles.[themeRow].Height <- 38.0f

        // Add theme controls to colorPanel (row 0)
        // themeLabel in column 0
        colorPanel.Controls.Add(themeLabel)
        colorPanel.SetRow(themeLabel, themeRow)
        colorPanel.SetColumn(themeLabel, 0)
        // themePanel (ComboBox + buttons) in columns 1-2 (Tab Color + Text Color columns)
        colorPanel.Controls.Add(themePanel)
        colorPanel.SetRow(themePanel, themeRow)
        colorPanel.SetColumn(themePanel, 1)
        colorPanel.SetColumnSpan(themePanel, 2)  // Span Tab Color and Text Color columns
        // clipboardDropdownBtn in column 3 (Border Color column)
        clipboardDropdownBtn.Anchor <- AnchorStyles.Left ||| AnchorStyles.Top
        clipboardDropdownBtn.Margin <- Padding(0, 5, 0, 5)
        colorPanel.Controls.Add(clipboardDropdownBtn)
        colorPanel.SetRow(clipboardDropdownBtn, themeRow)
        colorPanel.SetColumn(clipboardDropdownBtn, 3)

        // Initialize editor values and set up change handlers
        setEditorValues appearance
        editors.items.map(snd).iter <| fun editor ->
            editor.changed.Add <| fun() ->
                if not suppressEvents then
                    this.applyAppearance()
                    switchToCustom()

        // Set initial ComboBox selection based on current colors
        let matchIndex = findMatchingTheme()
        if matchIndex >= 0 then
            colorThemeComboBox.SelectedIndex <- matchIndex
        else
            // Current colors don't match any preset or custom theme
            // Save them as Custom colors and add UnsavedCustom to the list
            savedCustomColors <- Some(getCurrentColors())
            // Persist to settings file so Custom option remains after dialog close/reopen
            saveSavedCustomColors savedCustomColors
            refreshComboBoxItems()
            colorThemeComboBox.SelectedIndex <- themeItems.Length - 1  // Custom
        updateButtonState()
        
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
            tabInactiveTextColor = unbox(getValue "tabInactiveTextColor")
            tabMouseOverTextColor = unbox(getValue "tabMouseOverTextColor")
            tabActiveTextColor = unbox(getValue "tabActiveTextColor")
            tabFlashTextColor = unbox(getValue "tabFlashTextColor")
            tabInactiveTabColor = unbox(getValue "tabInactiveTabColor")
            tabMouseOverTabColor = unbox(getValue "tabMouseOverTabColor")
            tabActiveTabColor = unbox(getValue "tabActiveTabColor")
            tabFlashTabColor = unbox(getValue "tabFlashTabColor")
            tabInactiveBorderColor = unbox(getValue "tabInactiveBorderColor")
            tabMouseOverBorderColor = unbox(getValue "tabMouseOverBorderColor")
            tabActiveBorderColor = unbox(getValue "tabActiveBorderColor")
            tabFlashBorderColor = unbox(getValue "tabFlashBorderColor")
        }

        Services.settings.setValue("tabAppearance", box(newAppearance))
        
    interface ISettingsView with
        member x.key = SettingsViewType.AppearanceSettings
        member x.title = Localization.getString("Appearance")
        member x.control = panel :> Control

