namespace Bemo
open System
open System.Drawing
open System.IO
open System.Text.RegularExpressions
open System.Windows.Forms
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
    textColor: int
    normalBgColor: int
    highlightBgColor: int
    activeBgColor: int
    flashBgColor: int
    borderColor: int
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

    let settingsCheckboxBool key defaultValue = checkBox(settingsPropertyBool key defaultValue)

    // Separate int and color properties for cleaner UI layout
    let intProperties = List2([
        intConfig "tabHeight" "Height"
        intConfig "tabMaxWidth" "Max Width"
        intConfig "tabOverlap" "Overlap"
        intConfig "tabIndentNormal" "Indent Normal"
        intConfig "tabIndentFlipped" "Indent Flipped"
        ])

    let colorProperties = List2([
        colorConfig "tabTextColor" "Text Color"
        colorConfig "tabNormalBgColor" "Background Normal"
        colorConfig "tabHighlightBgColor" "Background Highlight"
        colorConfig "tabActiveBgColor" "Background Active"
        colorConfig "tabFlashBgColor" "Background Flash"
        colorConfig "tabBorderColor" "Border"
        ])

    // Combined list for iteration (used by setEditorValues and applyAppearance)
    let allProperties = List2(intProperties.list @ colorProperties.list)

    // Layout: int properties + dark mode checkbox + color properties + copy/paste row + theme row
    let totalRows = intProperties.length + 1 + colorProperties.length + 2

    let panel =
        let panel = TableLayoutPanel()
        panel.AutoScroll <- true
        panel.Dock <- DockStyle.Fill
        panel.GrowStyle <- TableLayoutPanelGrowStyle.FixedSize
        panel.Padding <- Padding(10)
        panel.RowCount <- totalRows
        List2([0..totalRows - 1]).iter <| fun row ->
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
        | :? int as i -> sprintf "%d" i
        | :? Color as c -> sprintf "#%06X" (c.ToArgb() &&& 0xFFFFFF)
        | _ -> value.ToString()

    // Helper to create and place an editor at a specific row
    let createEditorAt (prop: AppearanceProperty) (row: int) =
        let label =
            let label = Label()
            label.AutoSize <- true
            label.Text <- Localization.getString(prop.displayText)
            label.TextAlign <- ContentAlignment.MiddleLeft
            label.Margin <- Padding(0,5,0,5)
            label
        let editor =
            match prop.propertyType with
            | ColorProperty -> ColorEditor() :> IPropEditor
            | IntProperty -> IntEditor() :> IPropEditor
            | HotKeyProperty -> HotKeyEditor() :> IPropEditor

        editor.control.Dock <- DockStyle.Fill
        editor.control.Margin <- Padding(0,5,0,5)
        panel.Controls.Add(label)
        panel.Controls.Add(editor.control)
        panel.SetRow(label, row)
        panel.SetColumn(label, 0)
        panel.SetRow(editor.control, row)
        panel.SetColumn(editor.control, 1)

        match prop.propertyType with
        | IntProperty ->
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
            panel.Controls.Add(resetBtn)
            panel.SetRow(resetBtn, row)
            panel.SetColumn(resetBtn, 2)
        | ColorProperty ->
            panel.SetColumnSpan(editor.control, 2)
        | _ -> ()

        (prop.key, editor)

    // Row indices for each section
    // Layout: int properties -> dark mode -> theme -> color properties -> copy/paste
    let darkModeRow = intProperties.length
    let themeRow = darkModeRow + 1
    let colorStartRow = themeRow + 1
    let copyPasteButtonRow = colorStartRow + colorProperties.length

    // Create editors in natural order: int properties, then color properties
    let editors : Map2<string, IPropEditor> =
        let intEditors = intProperties.enumerate.map (fun (i, prop) -> createEditorAt prop i)
        let colorEditors = colorProperties.enumerate.map (fun (i, prop) -> createEditorAt prop (colorStartRow + i))
        (intEditors.list @ colorEditors.list) |> List.fold (fun (acc: Map2<string, IPropEditor>) (key, editor) -> acc.add key editor) (Map2())

    // Create dark mode checkbox at its designated row
    let darkModeLabel =
        let label = Label()
        label.AutoSize <- true
        label.Text <- Localization.getString("MenuDarkMode")
        label.TextAlign <- ContentAlignment.MiddleLeft
        label.Margin <- Padding(0,5,0,5)
        panel.Controls.Add(label)
        panel.SetRow(label, darkModeRow)
        panel.SetColumn(label, 0)
        label

    let darkModeCheckbox =
        let checkbox = settingsCheckboxBool "enableMenuDarkMode" false
        checkbox.Margin <- Padding(0,5,0,5)
        panel.Controls.Add(checkbox)
        panel.SetRow(checkbox, darkModeRow)
        panel.SetColumn(checkbox, 1)
        checkbox

    let setEditorValues appearance =
        allProperties.iter <| fun prop ->
            let editor = editors.find prop.key
            try
                editor.value <- Serialize.readField appearance prop.key
            with | _ ->()

    let appearance = Services.program.tabAppearanceInfo


    // Color key mapping for clipboard operations
    let colorKeyMap = [
        ("tabTextColor", "TextColor")
        ("tabNormalBgColor", "NormalBgColor")
        ("tabHighlightBgColor", "HighlightBgColor")
        ("tabActiveBgColor", "ActiveBgColor")
        ("tabFlashBgColor", "FlashBgColor")
        ("tabBorderColor", "BorderColor")
    ]

    // Load custom themes from settings
    let loadCustomThemes() : ColorThemeData list =
        try
            let json = Services.settings.root
            match json.getObjectArray("customColorThemes") with
            | Some(arr) ->
                arr.list |> List.choose (fun item ->
                    try
                        Some {
                            name = item.getString("name") |> Option.defaultValue ""
                            textColor = item.getInt32("textColor") |> Option.defaultValue 0
                            normalBgColor = item.getInt32("normalBgColor") |> Option.defaultValue 0
                            highlightBgColor = item.getInt32("highlightBgColor") |> Option.defaultValue 0
                            activeBgColor = item.getInt32("activeBgColor") |> Option.defaultValue 0
                            flashBgColor = item.getInt32("flashBgColor") |> Option.defaultValue 0
                            borderColor = item.getInt32("borderColor") |> Option.defaultValue 0
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
            item.setInt32("textColor", t.textColor)
            item.setInt32("normalBgColor", t.normalBgColor)
            item.setInt32("highlightBgColor", t.highlightBgColor)
            item.setInt32("activeBgColor", t.activeBgColor)
            item.setInt32("flashBgColor", t.flashBgColor)
            item.setInt32("borderColor", t.borderColor)
            item
        )
        json.setObjectArray("customColorThemes", List2(arr))
        Services.settings.root <- json

    // Mutable list of custom themes
    let mutable customThemes = loadCustomThemes()

    // Preset themes (built-in)
    let presetThemes = ["Light"; "Dark"; "Dark Blue"]

    // Load saved Custom theme colors from settings
    let loadSavedCustomColors() : ColorThemeData option =
        try
            let json = Services.settings.root
            match json.getObject("savedCustomColors") with
            | Some(item) ->
                // Check if item has required fields
                match item.getInt32("textColor") with
                | Some _ ->
                    Some {
                        name = "Custom"
                        textColor = item.getInt32("textColor") |> Option.defaultValue 0
                        normalBgColor = item.getInt32("normalBgColor") |> Option.defaultValue 0
                        highlightBgColor = item.getInt32("highlightBgColor") |> Option.defaultValue 0
                        activeBgColor = item.getInt32("activeBgColor") |> Option.defaultValue 0
                        flashBgColor = item.getInt32("flashBgColor") |> Option.defaultValue 0
                        borderColor = item.getInt32("borderColor") |> Option.defaultValue 0
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
            item.setInt32("textColor", c.textColor)
            item.setInt32("normalBgColor", c.normalBgColor)
            item.setInt32("highlightBgColor", c.highlightBgColor)
            item.setInt32("activeBgColor", c.activeBgColor)
            item.setInt32("flashBgColor", c.flashBgColor)
            item.setInt32("borderColor", c.borderColor)
            json.setObject("savedCustomColors", item)
        | None ->
            // Remove the key if None
            json.Remove("savedCustomColors") |> ignore
        Services.settings.root <- json

    // Saved Custom theme colors (for restoring when Custom is selected)
    // Load from settings on initialization
    let mutable savedCustomColors : ColorThemeData option = loadSavedCustomColors()

    // Build ComboBox items list (no separator items - separators are drawn in DrawItem)
    // UnsavedCustom is only added when savedCustomColors has a value
    let buildThemeItems() =
        let items = ResizeArray<ThemeItem>()
        // Presets
        presetThemes |> List.iter (fun name -> items.Add(Preset name))
        // Custom themes
        customThemes |> List.iter (fun t -> items.Add(CustomTheme t.name))
        // Only add UnsavedCustom if there are saved custom colors
        if savedCustomColors.IsSome then
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

    // Save/Rename button and Up/Down buttons (created early so we can reference them)
    let saveRenameBtn = new Button()
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
            textColor = getColor "tabTextColor"
            normalBgColor = getColor "tabNormalBgColor"
            highlightBgColor = getColor "tabHighlightBgColor"
            activeBgColor = getColor "tabActiveBgColor"
            flashBgColor = getColor "tabFlashBgColor"
            borderColor = getColor "tabBorderColor"
        }

    // Check if colors match a theme
    let colorsMatch (theme: ColorThemeData) (current: ColorThemeData) =
        theme.textColor = current.textColor &&
        theme.normalBgColor = current.normalBgColor &&
        theme.highlightBgColor = current.highlightBgColor &&
        theme.activeBgColor = current.activeBgColor &&
        theme.flashBgColor = current.flashBgColor &&
        theme.borderColor = current.borderColor

    // Get preset theme colors
    let getPresetColors (name: string) =
        let appearance =
            match name with
            | "Light" -> Services.program.defaultTabAppearanceInfo
            | "Dark" -> Services.program.darkModeTabAppearanceInfo
            | "Dark Blue" -> Services.program.darkModeBlueTabAppearanceInfo
            | _ -> Services.program.defaultTabAppearanceInfo
        {
            name = name
            textColor = appearance.tabTextColor.ToArgb() &&& 0xFFFFFF
            normalBgColor = appearance.tabNormalBgColor.ToArgb() &&& 0xFFFFFF
            highlightBgColor = appearance.tabHighlightBgColor.ToArgb() &&& 0xFFFFFF
            activeBgColor = appearance.tabActiveBgColor.ToArgb() &&& 0xFFFFFF
            flashBgColor = appearance.tabFlashBgColor.ToArgb() &&& 0xFFFFFF
            borderColor = appearance.tabBorderColor.ToArgb() &&& 0xFFFFFF
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
                saveRenameBtn.Visible <- false
                upBtn.Visible <- false
                downBtn.Visible <- false
            | CustomTheme name ->
                saveRenameBtn.Text <- Localization.getString("Rename")
                saveRenameBtn.Enabled <- true
                saveRenameBtn.Visible <- true
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
                saveRenameBtn.Text <- Localization.getString("SaveAs")
                saveRenameBtn.Enabled <- true
                saveRenameBtn.Visible <- true
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
            tabTextColor = presetAppearance.tabTextColor
            tabNormalBgColor = presetAppearance.tabNormalBgColor
            tabHighlightBgColor = presetAppearance.tabHighlightBgColor
            tabActiveBgColor = presetAppearance.tabActiveBgColor
            tabFlashBgColor = presetAppearance.tabFlashBgColor
            tabBorderColor = presetAppearance.tabBorderColor
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
            tabTextColor = toColor theme.textColor
            tabNormalBgColor = toColor theme.normalBgColor
            tabHighlightBgColor = toColor theme.highlightBgColor
            tabActiveBgColor = toColor theme.activeBgColor
            tabFlashBgColor = toColor theme.flashBgColor
            tabBorderColor = toColor theme.borderColor
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
            | Preset "Dark Blue" -> true  // Always draw separator after Dark Blue
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
                match themeItems.[e.Index] with
                | Preset name | CustomTheme name ->
                    use brush = new SolidBrush(e.ForeColor)
                    e.Graphics.DrawString(name, e.Font, brush, float32 e.Bounds.Left, float32 e.Bounds.Top)
                | UnsavedCustom ->
                    use brush = new SolidBrush(e.ForeColor)
                    e.Graphics.DrawString("Custom", e.Font, brush, float32 e.Bounds.Left, float32 e.Bounds.Top)
                // Draw separator line below if needed
                if shouldDrawSeparatorBelow e.Index then
                    let y = e.Bounds.Bottom - 1
                    use pen = new Pen(Color.Gray, 1.0f)
                    e.Graphics.DrawLine(pen, e.Bounds.Left + 2, y, e.Bounds.Right - 2, y)
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
                    | Preset "Dark" ->
                        applyColorPreset(Services.program.darkModeTabAppearanceInfo)
                    | Preset "Dark Blue" ->
                        applyColorPreset(Services.program.darkModeBlueTabAppearanceInfo)
                    | Preset _ -> ()
                    | CustomTheme name ->
                        match customThemes |> List.tryFind (fun t -> t.name = name) with
                        | Some theme -> applyCustomTheme theme
                        | None -> ()
                    | UnsavedCustom ->
                        // Restore saved Custom colors if available
                        match savedCustomColors with
                        | Some colors -> applyCustomTheme colors
                        | None -> ()
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

    // Row 1: Copy/Paste buttons
    let copyPasteButtonPanel =
        let container = new FlowLayoutPanel()
        container.FlowDirection <- FlowDirection.LeftToRight
        container.AutoSize <- true
        container.WrapContents <- false
        container.Anchor <- AnchorStyles.Left ||| AnchorStyles.Top
        container.Dock <- DockStyle.Top
        container.Margin <- Padding(0, 0, 0, 0)

        let copyBtn = Button()
        copyBtn.Text <- Localization.getString("CopyColorSettings")
        copyBtn.Font <- font
        copyBtn.AutoSize <- true
        copyBtn.AutoSizeMode <- AutoSizeMode.GrowAndShrink
        copyBtn.Margin <- Padding(0, 0, 3, 0)  // Remove left margin
        copyBtn.Click.Add <| fun _ ->
            try
                // Get current theme name from ComboBox selection
                let themeName =
                    if colorThemeComboBox.SelectedIndex >= 0 && colorThemeComboBox.SelectedIndex < themeItems.Length then
                        match themeItems.[colorThemeComboBox.SelectedIndex] with
                        | Preset name -> name
                        | CustomTheme name -> name
                        | UnsavedCustom -> "Custom"
                    else
                        "Custom"
                // Build clipboard text with theme name at the beginning
                let colorLines = colorKeyMap |> List.map (fun (editorKey, clipboardKey) ->
                    let editor = editors.find editorKey
                    let color = unbox<Color>(editor.value)
                    sprintf "%s=#%06X" clipboardKey (color.ToArgb() &&& 0xFFFFFF)
                )
                let clipboardText = "ThemaName=" + themeName + "\n" + String.Join("\n", colorLines)
                if not (String.IsNullOrEmpty(clipboardText)) then
                    Clipboard.SetText(clipboardText)
            with | _ -> ()

        let pasteBtn = Button()
        pasteBtn.Text <- Localization.getString("PasteColorSettings")
        pasteBtn.Font <- font
        pasteBtn.AutoSize <- true
        pasteBtn.AutoSizeMode <- AutoSizeMode.GrowAndShrink
        pasteBtn.Margin <- Padding(3, 0, 0, 0)  // Align vertically with copyBtn
        pasteBtn.Click.Add <| fun _ ->
            try
                if Clipboard.ContainsText() then
                    let clipboardText = Clipboard.GetText()
                    suppressEvents <- true
                    // Parse clipboard text and apply colors
                    colorKeyMap |> List.iter (fun (editorKey, clipboardKey) ->
                        let pattern = sprintf @"%s=#([0-9A-Fa-f]{6})" clipboardKey
                        let m = Regex.Match(clipboardText, pattern)
                        if m.Success then
                            let hexValue = m.Groups.[1].Value
                            let colorValue = Int32.Parse(hexValue, System.Globalization.NumberStyles.HexNumber)
                            let color = Color.FromArgb(255, Color.FromArgb(colorValue))
                            let editor = editors.find editorKey
                            editor.value <- box(color)
                    )
                    this.applyAppearance()
                    switchToCustom()
                    suppressEvents <- false
            with | _ -> ()

        container.Controls.Add(copyBtn)
        container.Controls.Add(pasteBtn)
        container

    // Reserved theme names that cannot be used for custom themes
    // Note: Light, Dark, Dark Blue can be used as custom theme names (will create a custom theme with that name)
    let reservedThemeNames = ["Custom"]

    // Show input dialog for theme name (used for Rename)
    // Returns: Some(name) if OK pressed, None if cancelled
    // For rename mode: showDeleteHint=true shows hint about deleting by clearing name
    let showNameInputDialog (title: string) (defaultValue: string) (showDeleteHint: bool) =
        use form = new Form()
        form.Text <- title
        form.Size <- Size(350, if showDeleteHint then 170 else 150)
        form.StartPosition <- FormStartPosition.CenterParent
        form.FormBorderStyle <- FormBorderStyle.FixedDialog
        form.MaximizeBox <- false
        form.MinimizeBox <- false
        form.TopMost <- true  // Ensure dialog appears in front of settings dialog

        let label = new Label()
        label.Text <- Localization.getString("EnterThemeName")
        label.Location <- Point(10, 20)
        label.AutoSize <- true

        let hintLabel = new Label()
        if showDeleteHint then
            hintLabel.Text <- Localization.getString("DeleteThemeHint")
            hintLabel.Location <- Point(10, 40)
            hintLabel.AutoSize <- true
            hintLabel.ForeColor <- Color.Gray

        let textBox = new TextBox()
        textBox.Text <- defaultValue
        textBox.Location <- Point(10, if showDeleteHint then 65 else 45)
        textBox.Size <- Size(310, 25)

        let okBtn = new Button()
        okBtn.Text <- "OK"
        okBtn.DialogResult <- DialogResult.OK
        okBtn.Location <- Point(160, if showDeleteHint then 100 else 80)
        okBtn.Size <- Size(75, 25)

        let cancelBtn = new Button()
        cancelBtn.Text <- "Cancel"
        cancelBtn.DialogResult <- DialogResult.Cancel
        cancelBtn.Location <- Point(245, if showDeleteHint then 100 else 80)
        cancelBtn.Size <- Size(75, 25)

        form.Controls.Add(label)
        if showDeleteHint then
            form.Controls.Add(hintLabel)
        form.Controls.Add(textBox)
        form.Controls.Add(okBtn)
        form.Controls.Add(cancelBtn)
        form.AcceptButton <- okBtn
        form.CancelButton <- cancelBtn

        // Get parent form from panel to show dialog as modal child
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

    // Color Theme dropdown and Save/Rename button (placed in column 1)
    let themePanel =
        let container = new FlowLayoutPanel()
        container.FlowDirection <- FlowDirection.LeftToRight
        container.AutoSize <- true
        container.WrapContents <- false
        container.Dock <- DockStyle.Top
        container.Margin <- Padding(0, 8, 0, 0)  // Add top margin
        container.Padding <- Padding(0, 0, 0, 0)

        // Configure saveRenameBtn (already created earlier)
        saveRenameBtn.Text <- Localization.getString("Rename")
        saveRenameBtn.AutoSize <- true
        saveRenameBtn.AutoSizeMode <- AutoSizeMode.GrowAndShrink
        saveRenameBtn.Margin <- Padding(3, 0, 0, 0)  // Align vertically with ComboBox
        saveRenameBtn.Enabled <- false

        saveRenameBtn.Click.Add <| fun _ ->
            if colorThemeComboBox.SelectedIndex >= 0 && colorThemeComboBox.SelectedIndex < themeItems.Length then
                match themeItems.[colorThemeComboBox.SelectedIndex] with
                | UnsavedCustom ->
                    // Save As - use ComboBox dialog for theme name selection
                    match showSaveAsDialog (Localization.getString("ThemeNameTitle")) with
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
                | CustomTheme currentName ->
                    // Rename existing custom theme (show delete hint)
                    match showNameInputDialog (Localization.getString("ThemeNameTitle")) currentName true with
                    | Some newName when String.IsNullOrWhiteSpace(newName) ->
                        // Empty name means delete the theme
                        customThemes <- customThemes |> List.filter (fun t -> t.name <> currentName)
                        saveCustomThemes customThemes
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
        upBtn.Text <- "↑"
        upBtn.AutoSize <- true
        upBtn.AutoSizeMode <- AutoSizeMode.GrowAndShrink
        upBtn.MinimumSize <- Size(30, 0)
        upBtn.TextAlign <- ContentAlignment.MiddleCenter
        upBtn.Margin <- Padding(3, -1, 0, 0)  // Align with saveRenameBtn
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
        downBtn.Text <- "↓"
        downBtn.AutoSize <- true
        downBtn.AutoSizeMode <- AutoSizeMode.GrowAndShrink
        downBtn.MinimumSize <- Size(30, 0)
        downBtn.TextAlign <- ContentAlignment.MiddleCenter
        downBtn.Margin <- Padding(3, -1, 0, 0)  // Align with saveRenameBtn
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

        container.Controls.Add(colorThemeComboBox)
        container.Controls.Add(saveRenameBtn)
        container.Controls.Add(upBtn)
        container.Controls.Add(downBtn)
        container

    do
        // Add theme label and panel (after dark mode, before color properties)
        // Increase themeRow height to add bottom margin between theme and color properties
        panel.RowStyles.[themeRow].Height <- 45.0f
        panel.Controls.Add(themeLabel)
        panel.SetRow(themeLabel, themeRow)
        panel.SetColumn(themeLabel, 0)
        panel.Controls.Add(themePanel)
        panel.SetRow(themePanel, themeRow)
        panel.SetColumn(themePanel, 1)

        // Add copy/paste button panel (after color properties)
        panel.Controls.Add(copyPasteButtonPanel)
        panel.SetRow(copyPasteButtonPanel, copyPasteButtonRow)
        panel.SetColumn(copyPasteButtonPanel, 1)

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

